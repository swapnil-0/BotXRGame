// cupnpu - SNPE runner for the cup detector, called from Unity (C#) via
// DllImport("cupnpu").
//
// Stage 1 (this file): a PROBE. Load a DLC from memory, build it for a chosen
// runtime (CPU / GPU / DSP=Hexagon NPU), run it N times, and report what
// happened and how long it took - in the app's own process, which is the only
// place the NPU path can be judged (adb shell and run-as are refused by
// SELinux on this headset; see tools/npu/).
//
// Linked against libSNPE.so by soname, so the same .so works with the SDK
// runtime bundled in the APK or, in a build that bundles none, with the
// headset's own public /vendor/lib64/libSNPE.so.
//
// Build: tools/npu/build_plugin.sh (Unity's NDK, arm64-v8a).

#include <android/log.h>
#include <stdarg.h>
#include <stdbool.h>   // the SNPE C headers use bool without including it
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "DlContainer/DlContainer.h"
#include "DlSystem/DlEnums.h"
#include "DlSystem/DlError.h"
#include "DlSystem/DlVersion.h"
#include "DlSystem/ITensor.h"
#include "DlSystem/PlatformConfig.h"
#include "DlSystem/RuntimeList.h"
#include "DlSystem/StringList.h"
#include "DlSystem/TensorMap.h"
#include "DlSystem/TensorShape.h"
#include "SNPE/SNPE.h"
#include "SNPE/SNPEBuilder.h"
#include "SNPE/SNPEUtil.h"

#define TAG "CupNpu"
#define EXPORT __attribute__((visibility("default")))

// ------------------------------------------------------------- report text

typedef struct { char* buf; int cap; int len; } Report;

static void say(Report* r, const char* fmt, ...)
{
    char line[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(line, sizeof line, fmt, ap);
    va_end(ap);

    __android_log_print(ANDROID_LOG_INFO, TAG, "%s", line);
    if (r->buf == NULL || r->len >= r->cap - 1) return;
    int n = snprintf(r->buf + r->len, (size_t)(r->cap - r->len), "%s\n", line);
    if (n > 0) r->len += n;
    if (r->len >= r->cap) r->len = r->cap - 1;
}

static double now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1.0e6;
}

static const char* last_error(void)
{
    const char* s = Snpe_ErrorCode_GetLastErrorString();
    return s ? s : "(no error string)";
}

static const char* runtime_name(int rt)
{
    switch (rt)
    {
        case 0: return "CPU";
        case 1: return "GPU";
        case 2: return "DSP (Hexagon NPU)";
        default: return "?";
    }
}

// ------------------------------------------------------------------ probe

// runtime: 0 = CPU, 1 = GPU, 2 = DSP. unsignedPd: 1 = request an unsigned
// protection domain (needed for a skel shipped in the APK; the headset's own
// signed skels need 0). adspLibraryPath: where FastRPC looks for the skel -
// set before SNPE touches the DSP. Returns 0 if every run succeeded.
EXPORT int cupnpu_probe(const char* adspLibraryPath,
                        const unsigned char* dlc, int dlcLen,
                        int runtime, int unsignedPd, int runs,
                        char* reportBuf, int reportCap)
{
    Report rep = { reportBuf, reportCap, 0 };
    Report* r = &rep;
    if (reportBuf && reportCap > 0) reportBuf[0] = 0;

    int rc = 1;
    Snpe_DlContainer_Handle_t container = NULL;
    Snpe_SNPEBuilder_Handle_t builder = NULL;
    Snpe_RuntimeList_Handle_t rtList = NULL;
    Snpe_PlatformConfig_Handle_t platform = NULL;
    Snpe_SNPE_Handle_t snpe = NULL;
    Snpe_StringList_Handle_t inNames = NULL, outNames = NULL;
    Snpe_TensorShape_Handle_t inShape = NULL;
    Snpe_ITensor_Handle_t inTensor = NULL;
    Snpe_TensorMap_Handle_t inMap = NULL, outMap = NULL;

    if (adspLibraryPath && adspLibraryPath[0])
    {
        setenv("ADSP_LIBRARY_PATH", adspLibraryPath, 1);
        say(r, "ADSP_LIBRARY_PATH=%s", adspLibraryPath);
    }

    Snpe_DlVersion_Handle_t ver = Snpe_Util_GetLibraryVersion();
    say(r, "SNPE library %s", ver ? Snpe_DlVersion_ToString(ver) : "?");
    if (ver) Snpe_DlVersion_Delete(ver);

    Snpe_Runtime_t rt = runtime == 2 ? SNPE_RUNTIME_DSP
                      : runtime == 1 ? SNPE_RUNTIME_GPU : SNPE_RUNTIME_CPU;
    int avail = Snpe_Util_IsRuntimeAvailableCheckOption(
        rt, unsignedPd ? SNPE_RUNTIME_CHECK_OPTION_UNSIGNEDPD_CHECK
                       : SNPE_RUNTIME_CHECK_OPTION_NORMAL_CHECK);
    say(r, "runtime %s, unsignedPD %s: available=%d", runtime_name(runtime),
        unsignedPd ? "ON" : "off", avail);

    container = Snpe_DlContainer_OpenBuffer(dlc, (size_t)dlcLen);
    if (!container) { say(r, "FAIL DlContainer_OpenBuffer: %s", last_error()); goto done; }

    builder = Snpe_SNPEBuilder_Create(container);
    rtList = Snpe_RuntimeList_Create();
    Snpe_RuntimeList_Add(rtList, rt);
    Snpe_SNPEBuilder_SetRuntimeProcessorOrder(builder, rtList);
    Snpe_SNPEBuilder_SetPerformanceProfile(builder, SNPE_PERFORMANCE_PROFILE_BURST);

    if (unsignedPd)
    {
        platform = Snpe_PlatformConfig_Create();
        Snpe_PlatformConfig_SetPlatformOptions(platform, "unsignedPD:ON");
        if (!Snpe_PlatformConfig_IsOptionsValid(platform))
            say(r, "warning: platform options not valid: %s", last_error());
        Snpe_SNPEBuilder_SetPlatformConfig(builder, platform);
    }

    double t0 = now_ms();
    snpe = Snpe_SNPEBuilder_Build(builder);
    double buildMs = now_ms() - t0;
    if (!snpe) { say(r, "FAIL build (%.0f ms): %s", buildMs, last_error()); goto done; }
    say(r, "built in %.0f ms (includes graph prepare on first load)", buildMs);

    inNames = Snpe_SNPE_GetInputTensorNames(snpe);
    outNames = Snpe_SNPE_GetOutputTensorNames(snpe);
    if (!inNames || Snpe_StringList_Size(inNames) == 0) { say(r, "FAIL no input tensors"); goto done; }
    const char* inName = Snpe_StringList_At(inNames, 0);

    inShape = Snpe_SNPE_GetInputDimensions(snpe, inName);
    char dims[96] = "";
    for (size_t i = 0; inShape && i < Snpe_TensorShape_Rank(inShape); i++)
    {
        char d[16];
        snprintf(d, sizeof d, i ? "x%zu" : "%zu", Snpe_TensorShape_At(inShape, i));
        strncat(dims, d, sizeof dims - strlen(dims) - 1);
    }
    say(r, "input '%s' %s", inName, dims);

    inTensor = Snpe_Util_CreateITensor(inShape);
    if (!inTensor) { say(r, "FAIL CreateITensor: %s", last_error()); goto done; }
    float* in = (float*)Snpe_ITensor_GetData(inTensor);
    size_t inCount = Snpe_ITensor_GetSize(inTensor);
    for (size_t i = 0; i < inCount; i++) in[i] = 0.5f;   // mid grey - timing only

    inMap = Snpe_TensorMap_Create();
    Snpe_TensorMap_Add(inMap, inName, inTensor);
    outMap = Snpe_TensorMap_Create();

    double total = 0, best = 1e9, worst = 0;
    int ok = 0;
    for (int i = 0; i < runs; i++)
    {
        double s = now_ms();
        Snpe_ErrorCode_t e = Snpe_SNPE_ExecuteITensors(snpe, inMap, outMap);
        double ms = now_ms() - s;
        if (e != SNPE_SUCCESS) { say(r, "FAIL execute run %d: %s", i, last_error()); goto done; }
        if (i == 0) say(r, "first run %.2f ms", ms);
        else { total += ms; if (ms < best) best = ms; if (ms > worst) worst = ms; ok++; }
    }
    if (ok > 0)
        say(r, "RESULT %s: %d runs, avg %.2f ms, min %.2f, max %.2f",
            runtime_name(runtime), ok, total / ok, best, worst);

    for (size_t i = 0; outNames && i < Snpe_StringList_Size(outNames); i++)
    {
        const char* n = Snpe_StringList_At(outNames, i);
        Snpe_ITensor_Handle_t t = Snpe_TensorMap_GetTensor_Ref(outMap, n);
        say(r, "output '%s' %zu values", n, t ? Snpe_ITensor_GetSize(t) : (size_t)0);
    }
    rc = 0;

done:
    if (outMap) Snpe_TensorMap_Delete(outMap);
    if (inMap) Snpe_TensorMap_Delete(inMap);
    if (inTensor) Snpe_ITensor_Delete(inTensor);
    if (inShape) Snpe_TensorShape_Delete(inShape);
    if (outNames) Snpe_StringList_Delete(outNames);
    if (inNames) Snpe_StringList_Delete(inNames);
    if (snpe) Snpe_SNPE_Delete(snpe);
    if (platform) Snpe_PlatformConfig_Delete(platform);
    if (rtList) Snpe_RuntimeList_Delete(rtList);
    if (builder) Snpe_SNPEBuilder_Delete(builder);
    if (container) Snpe_DlContainer_Delete(container);
    return rc;
}
