using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// NPU Test experience: runs the quantized cup model through Qualcomm SNPE in
/// this app's own process on the CPU, the Hexagon NPU (DSP) and the GPU, and
/// shows what worked and how fast.
///
/// Why in-app: on this headset the NPU cannot be reached from adb shell or
/// run-as (SELinux denies the FastRPC device and the DSP HAL to those
/// domains). An ordinary app process is the domain the DSP HAL serves - so
/// this is the only honest test of "can the app use the NPU".
///
/// Pieces (tools/npu/build_plugin.sh stages them):
///   libcupnpu.so + SNPE runtime   Plugins/Android/libs/arm64-v8a (CPU side)
///   libSnpeHtpV69Skel.so + .dlc   StreamingAssets/CupNpu, copied to a plain
///                                 folder here; ADSP_LIBRARY_PATH points at it
///   libcdsprpc.so                 declared in CupNpu.androidlib's manifest
///
/// Self-installing: a bootstrap adds this to the scene at load, so the only
/// scene change is the menu row (ExperienceCatalog). Like the other
/// experiences it polls GameMode.Chosen rather than checking in Start().
/// The report also goes to logcat (tag CupNpu) and to
/// /sdcard/Android/data/com.swapnil.BotXRGame/files/CupNpu/report.txt.
/// </summary>
public class NpuTestMode : MonoBehaviour
{
    private const string Folder = "CupNpu";
    private const string Skel = "libSnpeHtpV69Skel.so";
    private const string Model = "yolo11n_320_q.dlc";
    private const string VendorDsp = "/vendor/lib/rfsa/adsp;/vendor/dsp/cdsp;/system/lib/rfsa/adsp;/dsp";

    [DllImport("cupnpu")]
    private static extern int cupnpu_probe(string adspLibraryPath, byte[] dlc, int dlcLen,
                                           int runtime, int unsignedPd, int runs,
                                           StringBuilder report, int reportCap);

    private bool active;
    private TMPro.TextMeshPro text;
    private readonly StringBuilder log = new StringBuilder();
    private volatile string threadReport;
    private volatile bool threadDone;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindAnyObjectByType<NpuTestMode>(FindObjectsInactive.Include) != null) return;
        new GameObject("NpuTest").AddComponent<NpuTestMode>();
    }

    void Update()
    {
        if (!active)
        {
            if (GameMode.Chosen && GameMode.IsNpuTest) Activate();
            return;
        }
        if (threadReport != null) { Append(threadReport); threadReport = null; }
    }

    void LateUpdate()
    {
        if (!active || text == null || Camera.main == null) return;

        // Follows the gaze loosely, 1.4 m out, so the report stays readable.
        var head = Camera.main.transform;
        Vector3 target = head.position + head.forward * 1.4f;
        text.transform.position = Vector3.Lerp(text.transform.position, target, 0.08f);
        text.transform.rotation = Quaternion.LookRotation(text.transform.position - head.position, Vector3.up);
    }

    private void Activate()
    {
        active = true;
        Debug.Log("[NpuTest] experience active");
        ExperienceStandDown.Gameplay(FindAnyObjectByType<ArenaPlacer>(FindObjectsInactive.Include));
        ExperienceStandDown.HideHud();
        BuildText();
        StartCoroutine(Run());
    }

    private void BuildText()
    {
        var go = new GameObject("NpuTestText");
        go.transform.SetParent(transform, false);
        text = go.AddComponent<TMPro.TextMeshPro>();
        text.fontSize = 0.32f;
        text.alignment = TMPro.TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        text.color = new Color(0.85f, 0.95f, 1f);
        var rt = text.GetComponent<RectTransform>();
        if (rt != null) rt.sizeDelta = new Vector2(1.6f, 1.0f);
        if (Camera.main != null)
            go.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 1.4f;
    }

    private void Append(string s)
    {
        log.AppendLine(s);
        if (text != null) text.text = "NPU TEST - SNPE in-app probe\n\n" + log;
        Debug.Log("[NpuTest] " + s.Replace("\n", " | "));
    }

    private IEnumerator Run()
    {
        string dir = Path.Combine(Application.persistentDataPath, Folder);
        Directory.CreateDirectory(dir);

        // The skel must be a plain file FastRPC can read; files inside the APK
        // are not. The model is passed to SNPE as bytes.
        byte[] skel = null, dlc = null;
        yield return Load(Skel, b => skel = b);
        yield return Load(Model, b => dlc = b);
        if (skel == null || dlc == null)
        {
            Append("FAIL: StreamingAssets/CupNpu is missing the skel or the model - run tools/npu/build_plugin.sh");
            yield break;
        }
        File.WriteAllBytes(Path.Combine(dir, Skel), skel);
        Append(string.Format("model {0} KB, skel copied to {1}", dlc.Length / 1024, dir));

        string ours = dir + ";" + VendorDsp;

        // CPU first: proves the plugin, SNPE and the model load at all.
        // Then the NPU as an unsigned PD with our skel (the APK-shipped
        // route), then signed against the headset's own skels (expected to
        // fail on a 2.32/2.25 version mismatch - but it says how), then GPU.
        var runs = new (string label, string adsp, int runtime, int unsignedPd, int n)[]
        {
            ("CPU",                    ours,      0, 0, 20),
            ("NPU unsigned (our skel)", ours,      2, 1, 50),
            ("NPU signed (vendor skel)", VendorDsp, 2, 0, 50),
            ("GPU",                    ours,      1, 0, 20),
        };

        foreach (var r in runs)
        {
            Append("\n== " + r.label + " ...");
            threadDone = false;
            var t = new System.Threading.Thread(() =>
            {
                // Off the main thread: building a graph for the NPU can take
                // seconds, and a stalled frame loop in a headset is miserable.
                var sb = new StringBuilder(8192);
                int rc;
                try { rc = cupnpu_probe(r.adsp, dlc, dlc.Length, r.runtime, r.unsignedPd, r.n, sb, sb.Capacity); }
                catch (System.Exception e) { rc = -1; sb.Append("EXCEPTION ").Append(e.Message); }
                threadReport = sb.ToString().TrimEnd() + (rc == 0 ? "\n-> OK" : "\n-> FAILED");
                threadDone = true;
            });
            t.Start();
            while (!threadDone || threadReport != null) yield return null;
        }

        Append("\nDone. Report also in logcat (tag CupNpu) and files/CupNpu/report.txt");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
    }

    private static IEnumerator Load(string name, System.Action<byte[]> done)
    {
        string path = Path.Combine(Application.streamingAssetsPath, Folder, name);
        if (!path.Contains("://")) path = "file://" + path;   // editor / desktop
        using (var req = UnityWebRequest.Get(path))
        {
            yield return req.SendWebRequest();
            done(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.data : null);
        }
    }
}
