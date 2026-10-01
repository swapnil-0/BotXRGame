using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

/// <summary>
/// The headset's world-facing camera, as pixels the detector can read.
///
/// Android XR exposes the passthrough camera as an ordinary Camera2 device
/// (camera_id 0, the "rear" camera in phone terms) behind the normal CAMERA
/// permission. AR Foundation's CPU-image path is NOT available on Android XR -
/// the installed provider reports supportsCameraImage = false - so frames come
/// through WebCamTexture instead, which sits on top of the same Camera2 device.
///
/// Also reads the lens calibration straight from Camera2 over JNI, because a
/// detection is only a pixel until it is turned into a ray, and that needs the
/// focal length and principal point. Everything it learns goes into
/// <see cref="Status"/> so the first build reports what the hardware actually
/// provides instead of us assuming it.
///
/// Runs in the Unity editor too, on the PC's webcam - so the detector can be
/// tried against a real cup without building to the headset.
/// </summary>
public class PassthroughCamera : MonoBehaviour
{
    [Header("Stream")]
    [Tooltip("Requested size. The device picks the nearest it supports - the " +
             "actual size is reported. 640x480 is plenty for a 320-px model " +
             "and keeps the per-frame copy cheap.")]
    public int requestedWidth = 640;
    public int requestedHeight = 480;
    public int requestedFps = 30;

    [Tooltip("Prefer a device that is NOT front-facing. On Android XR the " +
             "world-facing camera maps to the 'rear' camera; the 'front' one " +
             "returns the user's avatar, which is useless here.")]
    public bool preferWorldFacing = true;

    [Header("Fallback lens model")]
    [Tooltip("Horizontal field of view used when Camera2 does not report " +
             "intrinsics. Tunable from the headset - a wrong value shows up as " +
             "markers drifting outward or inward as the cup moves off-centre.")]
    public float fallbackHorizontalFov = 90f;

    [Header("Pose history")]
    [Tooltip("Seconds of head pose to keep, for matching a frame to where the " +
             "head was when it was captured rather than where it is now.")]
    public float poseHistorySeconds = 0.5f;

    // ------------------------------------------------------------------ state

    public WebCamTexture Texture { get; private set; }
    public bool IsRunning => Texture != null && Texture.isPlaying && Texture.width > 16;
    public int Width => Texture != null ? Texture.width : 0;
    public int Height => Texture != null ? Texture.height : 0;

    /// <summary>Frames actually delivered, and the measured rate.</summary>
    public int FrameCount { get; private set; }
    public float MeasuredFps { get; private set; }

    /// <summary>Time.realtimeSinceStartup when the latest frame arrived.</summary>
    public float LastFrameTime { get; private set; } = -1f;

    /// <summary>True only on frames where the texture received a new image.</summary>
    public bool NewFrameThisUpdate { get; private set; }

    public string DeviceName { get; private set; } = "";
    public string Status { get; private set; } = "camera: not started";

    // ------------------------------------------------------------ intrinsics

    public bool HasCamera2Intrinsics { get; private set; }

    /// <summary>Raw Camera2 values, in active-array pixels. For the report.</summary>
    public float[] RawIntrinsics { get; private set; }
    public Vector2Int ActiveArraySize { get; private set; }
    public float[] LensPoseTranslation { get; private set; }
    public float[] LensPoseRotation { get; private set; }
    public int LensPoseReference { get; private set; } = -1;
    public int TimestampSource { get; private set; } = -1;
    public string Camera2Report { get; private set; } = "camera2: not queried";

    private bool permissionRequested;
    private bool started;
    private float fpsWindowStart;
    private int fpsWindowFrames;

    private struct PoseSample { public float t; public Vector3 p; public Quaternion r; }
    private readonly List<PoseSample> poses = new List<PoseSample>(128);

    // ------------------------------------------------------------- lifecycle

    /// <summary>Ask for permission (Android) and open the camera.</summary>
    public void Begin()
    {
        if (started) return;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            if (!permissionRequested)
            {
                permissionRequested = true;
                Status = "camera: asking permission - accept the prompt in the headset";

                var cb = new PermissionCallbacks();
                cb.PermissionGranted += _ => { Status = "camera: permission granted"; Begin(); };
                // PermissionDeniedAndDontAskAgain deliberately not used: it is
                // deprecated in recent Unity, and an obsolete-as-error member
                // would stop the whole project compiling. Denied covers it.
                cb.PermissionDenied += _ => Status =
                    "camera: PERMISSION DENIED - enable Camera for this app in Settings";
                Permission.RequestUserPermission(Permission.Camera, cb);
            }
            return;
        }
#endif
        OpenDevice();
    }

    private void OpenDevice()
    {
        var devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            Status = "camera: NO DEVICES - permission ok but nothing enumerated";
            return;
        }

        int pick = 0;
        if (preferWorldFacing)
            for (int i = 0; i < devices.Length; i++)
                if (!devices[i].isFrontFacing) { pick = i; break; }

        DeviceName = devices[pick].name;

        var list = new System.Text.StringBuilder();
        for (int i = 0; i < devices.Length; i++)
            list.AppendFormat("{0}{1}{2}", i > 0 ? ", " : "", devices[i].name,
                              devices[i].isFrontFacing ? "(front)" : "");

        Texture = new WebCamTexture(DeviceName, requestedWidth, requestedHeight, requestedFps);
        Texture.Play();
        started = true;
        fpsWindowStart = Time.realtimeSinceStartup;

        Debug.LogFormat("[Cam] devices: {0}  -> opened '{1}'", list, DeviceName);

        QueryCamera2(CameraIdFromName(DeviceName));
    }

    void OnDisable()
    {
        if (Texture != null && Texture.isPlaying) Texture.Stop();
    }

    void OnDestroy()
    {
        if (Texture != null)
        {
            if (Texture.isPlaying) Texture.Stop();
            Destroy(Texture);
            Texture = null;
        }
    }

    void Update()
    {
        RecordHeadPose();

        NewFrameThisUpdate = false;
        if (Texture == null) return;

        if (Texture.didUpdateThisFrame && Texture.width > 16)
        {
            NewFrameThisUpdate = true;
            FrameCount++;
            fpsWindowFrames++;
            LastFrameTime = Time.realtimeSinceStartup;
        }

        float elapsed = Time.realtimeSinceStartup - fpsWindowStart;
        if (elapsed >= 1f)
        {
            MeasuredFps = fpsWindowFrames / elapsed;
            fpsWindowFrames = 0;
            fpsWindowStart = Time.realtimeSinceStartup;
        }

        if (!IsRunning)
            Status = string.Format("camera: '{0}' opening...", DeviceName);
        else
            Status = string.Format("camera: '{0}' {1}x{2} @ {3:F0} fps  rot {4}{5}  frames {6}",
                DeviceName, Width, Height, MeasuredFps,
                Texture.videoRotationAngle,
                Texture.videoVerticallyMirrored ? " vflip" : "",
                FrameCount);
    }

    // --------------------------------------------------------- pose history

    private void RecordHeadPose()
    {
        var cam = Camera.main;
        if (cam == null) return;

        float now = Time.realtimeSinceStartup;
        poses.Add(new PoseSample { t = now, p = cam.transform.position, r = cam.transform.rotation });

        // Drop from the front once older than the window.
        int drop = 0;
        while (drop < poses.Count && now - poses[drop].t > poseHistorySeconds) drop++;
        if (drop > 0) poses.RemoveRange(0, drop);
    }

    /// <summary>
    /// Head pose at an earlier moment, interpolated from history.
    ///
    /// A camera frame describes the world as it was when the shutter fired,
    /// which is tens of milliseconds before Unity sees it. Casting its ray from
    /// the CURRENT head pose throws the cup sideways whenever the head is
    /// turning - by the turn rate times the latency, which at a casual 60 deg/s
    /// and 50 ms is 3 degrees, or 10 cm at 2 m.
    /// </summary>
    public bool TryGetHeadPose(float time, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        if (poses.Count == 0) return false;

        if (time <= poses[0].t) { pos = poses[0].p; rot = poses[0].r; return true; }

        var last = poses[poses.Count - 1];
        if (time >= last.t) { pos = last.p; rot = last.r; return true; }

        for (int i = poses.Count - 1; i > 0; i--)
        {
            var a = poses[i - 1];
            var b = poses[i];
            if (time >= a.t && time <= b.t)
            {
                float k = (b.t - a.t) > 1e-6f ? (time - a.t) / (b.t - a.t) : 0f;
                pos = Vector3.Lerp(a.p, b.p, k);
                rot = Quaternion.Slerp(a.r, b.r, k);
                return true;
            }
        }
        pos = last.p; rot = last.r;
        return true;
    }

    // ------------------------------------------------------------ intrinsics

    /// <summary>
    /// Focal lengths and principal point for the STREAM being delivered, in
    /// stream pixels, with y measured downward from the top of the image.
    ///
    /// Camera2 reports intrinsics against the sensor's active array. Output
    /// streams are a centred crop of that array to the stream's aspect ratio,
    /// then scaled - so the calibration is cropped and scaled the same way.
    /// Without Camera2 data, a pinhole with the fallback FOV is used.
    /// </summary>
    public void GetStreamIntrinsics(out float fx, out float fy, out float cx, out float cy)
    {
        float w = Mathf.Max(1, Width);
        float h = Mathf.Max(1, Height);

        if (HasCamera2Intrinsics && ActiveArraySize.x > 0 && ActiveArraySize.y > 0)
        {
            float aw = ActiveArraySize.x, ah = ActiveArraySize.y;

            // Largest centred region of the active array with the stream's aspect.
            float cropW = aw, cropH = aw * h / w;
            if (cropH > ah) { cropH = ah; cropW = ah * w / h; }

            float k = w / cropW;
            float offX = (aw - cropW) * 0.5f;
            float offY = (ah - cropH) * 0.5f;

            fx = RawIntrinsics[0] * k;
            fy = RawIntrinsics[1] * k;
            cx = (RawIntrinsics[2] - offX) * k;
            cy = (RawIntrinsics[3] - offY) * k;
            return;
        }

        fx = (w * 0.5f) / Mathf.Tan(fallbackHorizontalFov * 0.5f * Mathf.Deg2Rad);
        fy = fx;
        cx = w * 0.5f;
        cy = h * 0.5f;
    }

    private static string CameraIdFromName(string name)
    {
        // Android device names are typically "Camera 0" / "Camera 1".
        if (string.IsNullOrEmpty(name)) return "0";
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i])) i--;
        string digits = name.Substring(i + 1);
        return digits.Length > 0 ? digits : "0";
    }

    /// <summary>
    /// Read the lens calibration from Camera2 over JNI.
    ///
    /// No native plugin: CameraManager and CameraCharacteristics are plain Java
    /// objects reachable through AndroidJavaObject. Each key is read on its own
    /// and a missing one is reported, not fatal - which keys a headset fills in
    /// is exactly what this is here to find out.
    /// </summary>
    private void QueryCamera2(string cameraId)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        var report = new System.Text.StringBuilder();
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var manager = activity.Call<AndroidJavaObject>("getSystemService", "camera"))
            using (var keys = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics"))
            {
                string[] ids = manager.Call<string[]>("getCameraIdList");
                report.AppendFormat("camera2 ids [{0}] using {1}\n", string.Join(",", ids), cameraId);

                using (var ch = manager.Call<AndroidJavaObject>("getCameraCharacteristics", cameraId))
                {
                    RawIntrinsics = ReadFloats(ch, keys, "LENS_INTRINSIC_CALIBRATION");
                    LensPoseTranslation = ReadFloats(ch, keys, "LENS_POSE_TRANSLATION");
                    LensPoseRotation = ReadFloats(ch, keys, "LENS_POSE_ROTATION");
                    LensPoseReference = ReadInt(ch, keys, "LENS_POSE_REFERENCE");
                    TimestampSource = ReadInt(ch, keys, "SENSOR_INFO_TIMESTAMP_SOURCE");

                    // Intrinsics are defined against the PRE-correction array.
                    ActiveArraySize = ReadRectSize(ch, keys, "SENSOR_INFO_PRE_CORRECTION_ACTIVE_ARRAY_SIZE");
                    if (ActiveArraySize.x <= 0)
                        ActiveArraySize = ReadRectSize(ch, keys, "SENSOR_INFO_ACTIVE_ARRAY_SIZE");
                }
            }

            HasCamera2Intrinsics = RawIntrinsics != null && RawIntrinsics.Length >= 4
                                   && RawIntrinsics[0] > 1f && ActiveArraySize.x > 0;

            report.AppendFormat("intrinsics {0}  array {1}x{2}\n",
                RawIntrinsics == null ? "MISSING" : Join(RawIntrinsics),
                ActiveArraySize.x, ActiveArraySize.y);
            report.AppendFormat("lens pose t {0}  r {1}  ref {2}  ts-src {3}",
                LensPoseTranslation == null ? "-" : Join(LensPoseTranslation),
                LensPoseRotation == null ? "-" : Join(LensPoseRotation),
                PoseReferenceName(LensPoseReference),
                TimestampSource == 1 ? "REALTIME" : TimestampSource == 0 ? "UNKNOWN" : "-");
        }
        catch (System.Exception e)
        {
            report.Append("camera2 query failed: " + e.Message);
        }

        Camera2Report = report.ToString();
        Debug.Log("[Cam] " + Camera2Report.Replace("\n", " | "));
#else
        Camera2Report = "camera2: editor - using fallback FOV";
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static float[] ReadFloats(AndroidJavaObject ch, AndroidJavaClass keys, string keyName)
    {
        try
        {
            using (var key = keys.GetStatic<AndroidJavaObject>(keyName))
            using (var val = ch.Call<AndroidJavaObject>("get", key))
            {
                if (val == null) return null;
                return AndroidJNIHelper.ConvertFromJNIArray<float[]>(val.GetRawObject());
            }
        }
        catch { return null; }
    }

    private static int ReadInt(AndroidJavaObject ch, AndroidJavaClass keys, string keyName)
    {
        try
        {
            using (var key = keys.GetStatic<AndroidJavaObject>(keyName))
            using (var val = ch.Call<AndroidJavaObject>("get", key))
                return val == null ? -1 : val.Call<int>("intValue");
        }
        catch { return -1; }
    }

    private static Vector2Int ReadRectSize(AndroidJavaObject ch, AndroidJavaClass keys, string keyName)
    {
        try
        {
            using (var key = keys.GetStatic<AndroidJavaObject>(keyName))
            using (var val = ch.Call<AndroidJavaObject>("get", key))
                return val == null ? Vector2Int.zero
                                   : new Vector2Int(val.Call<int>("width"), val.Call<int>("height"));
        }
        catch { return Vector2Int.zero; }
    }
#endif

    private static string Join(float[] v)
    {
        var parts = new string[v.Length];
        for (int i = 0; i < v.Length; i++) parts[i] = v[i].ToString("F2");
        return "[" + string.Join(" ", parts) + "]";
    }

    private static string PoseReferenceName(int r)
    {
        switch (r)
        {
            case 0: return "PRIMARY_CAMERA";
            case 1: return "GYROSCOPE";
            case 2: return "UNDEFINED";
            case 3: return "AUTOMOTIVE";
            default: return "-";
        }
    }
}
