using System.Collections;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

/// <summary>
/// Finds cups in passthrough camera frames with a YOLO model on Unity's
/// Inference Engine (shown as "Sentis" in the package manager, namespace
/// Unity.InferenceEngine).
///
/// Pipeline per frame: camera texture -> tensor on the GPU -> model run a few
/// layers per rendered frame (so a 30 ms model never costs a 30 ms frame) ->
/// non-blocking readback -> decode boxes -> keep the cup class -> optional red
/// check -> non-max suppression.
///
/// The output decoder detects the layout from the tensor shape, so the common
/// YOLO exports all work without code changes:
///   [1, 4+C, N]  YOLOv8 / YOLO11 (anchors last)
///   [1, N, 4+C]  the same, transposed
///   [1, N, 6]    NMS-free end-to-end exports (x1 y1 x2 y2 score class)
///
/// Uses the GPU, not the NPU. The Galaxy XR's chip (XR2+ Gen 2) is not on
/// LiteRT's Qualcomm NPU list, and Inference Engine has no NPU backend at all.
/// </summary>
public class CupDetector : MonoBehaviour
{
    [Header("Model")]
    [Tooltip("The YOLO model, imported from an .onnx (or .tflite) file. " +
             "See tools/export_cup_model.py.")]
    public ModelAsset modelAsset;

    [Tooltip("GPUCompute keeps the frame on the GPU from camera to model. " +
             "Switch to CPU only to compare - it frees the GPU for rendering " +
             "but is much slower on this chip.")]
    public BackendType backend = BackendType.GPUCompute;

    [Tooltip("Model input size in pixels. Must match the size the model was " +
             "exported at (the export script uses 320).")]
    public int inputWidth = 320;
    public int inputHeight = 320;

    [Tooltip("Layers scheduled per rendered frame. Higher finishes each " +
             "inference sooner but costs more per frame. Inference Engine runs " +
             "on the main thread, so this is the frame-time dial.")]
    [Range(1, 200)] public int layersPerFrame = 25;

    [Header("Filtering")]
    [Tooltip("COCO class ids counted as cups. 41 = cup. Add 39 (bottle), " +
             "40 (wine glass) or 45 (bowl) if solo cups get labelled as those.")]
    public int[] cupClassIds = { 41 };

    [Range(0.05f, 0.95f)] public float scoreThreshold = 0.30f;
    [Range(0.1f, 0.9f)] public float iouThreshold = 0.45f;

    [Tooltip("Reject boxes that are not mostly red.\n\nThe COCO 'cup' class " +
             "fires on mugs, glasses and paper cups too. Red solo cups are a " +
             "near-ideal colour target, so a cheap hue check removes most " +
             "false positives the model can't.")]
    public bool requireRed = true;

    [Range(0.05f, 0.9f)] public float minRedFraction = 0.20f;

    [Tooltip("Flip the image vertically before the model sees it. Starts from " +
             "the camera's own mirrored flag; toggle in the headset if the " +
             "preview boxes sit upside-down relative to the cups.")]
    public bool invertFlip = false;

    [Header("Wiring")]
    public PassthroughCamera cam;

    // ------------------------------------------------------------------ output

    public struct Detection
    {
        /// <summary>Normalised box, 0..1, x right, y DOWN from the image top.</summary>
        public Rect box;
        public float score;
        public int classId;

        /// <summary>Fraction of red pixels in the box, or -1 if not measured.</summary>
        public float redFraction;

        /// <summary>When the frame arrived, Time.realtimeSinceStartup.</summary>
        public float frameTime;
    }

    public readonly List<Detection> Latest = new List<Detection>();

    /// <summary>Increments each time a new set of detections is published.</summary>
    public int ResultId { get; private set; }

    public float LastInferenceMs { get; private set; }
    public float InferencesPerSecond { get; private set; }
    public int RawCandidates { get; private set; }
    public int RejectedNotRed { get; private set; }
    public bool RedCheckUnavailable { get; private set; }
    public string Status { get; private set; } = "detector: not started";
    public string ModelReport { get; private set; } = "";

    // ------------------------------------------------------------------- state

    private Model model;
    private Worker worker;
    private Tensor<float> input;
    private IEnumerator schedule;
    private Tensor<float> pendingOutput;

    private enum Stage { Idle, Running, Reading }
    private Stage stage = Stage.Idle;

    private float runStart, frameTimeOfRun;
    private float ipsWindowStart;
    private int ipsWindowCount;
    private bool flipOfRun;
    private readonly HashSet<int> acceptIds = new HashSet<int>();

    public bool IsReady => worker != null;

    /// <summary>Whether the last run flipped the image - the preview copies it.</summary>
    public bool FlipApplied => flipOfRun;

    /// <summary>Load the model and allocate. Safe to call more than once.</summary>
    public void Begin()
    {
        if (worker != null) return;

        if (modelAsset == null)
        {
            Status = "detector: NO MODEL - put the .onnx in Assets/Models/CupDetect and re-run Set Up Cup Detect";
            return;
        }

        try
        {
            model = ModelLoader.Load(modelAsset);
            worker = new Worker(model, backend);
            input = new Tensor<float>(new TensorShape(1, 3, inputHeight, inputWidth));

            var outShape = model.outputs.Count > 0 ? "out '" + model.outputs[0].name + "'" : "no outputs";
            ModelReport = string.Format("model '{0}' in {1} {2}  {3}  backend {4}",
                modelAsset.name,
                model.inputs.Count > 0 ? model.inputs[0].name : "?",
                model.inputs.Count > 0 ? model.inputs[0].shape.ToString() : "?",
                outShape, backend);

            Status = "detector: loaded";
            ipsWindowStart = Time.realtimeSinceStartup;
            Debug.Log("[Cups] " + ModelReport);
        }
        catch (System.Exception e)
        {
            Status = "detector: LOAD FAILED - " + e.Message;
            Debug.LogError("[Cups] model load failed: " + e);
            DisposeAll();
        }
    }

    void Update()
    {
        if (worker == null || cam == null) return;

        acceptIds.Clear();
        if (cupClassIds != null) foreach (int id in cupClassIds) acceptIds.Add(id);

        switch (stage)
        {
            case Stage.Idle:
                // Only on a NEW camera frame - re-running on the same image
                // would just burn GPU for identical results.
                if (cam.IsRunning && cam.NewFrameThisUpdate) StartRun();
                break;

            case Stage.Running:
                StepRun();
                break;

            case Stage.Reading:
                if (pendingOutput != null && pendingOutput.IsReadbackRequestDone())
                    FinishRun();
                break;
        }

        float elapsed = Time.realtimeSinceStartup - ipsWindowStart;
        if (elapsed >= 1f)
        {
            InferencesPerSecond = ipsWindowCount / elapsed;
            ipsWindowCount = 0;
            ipsWindowStart = Time.realtimeSinceStartup;
        }
    }

    private void StartRun()
    {
        flipOfRun = cam.Texture.videoVerticallyMirrored ^ invertFlip;

        // TopLeft origin = row 0 of the tensor is the top of the picture, which
        // is what the model was trained on. The texture is resampled to the
        // model size on the GPU as part of the copy.
        var layout = new TextureTransform()
            .SetCoordOrigin(flipOfRun ? CoordOrigin.BottomLeft : CoordOrigin.TopLeft);

        TextureConverter.ToTensor(cam.Texture, input, layout);

        frameTimeOfRun = cam.LastFrameTime;
        runStart = Time.realtimeSinceStartup;
        schedule = worker.ScheduleIterable(input);
        stage = Stage.Running;
        StepRun();
    }

    private void StepRun()
    {
        int n = 0;
        while (schedule.MoveNext())
        {
            if (++n >= layersPerFrame) return;   // continue next frame
        }

        pendingOutput = worker.PeekOutput() as Tensor<float>;
        if (pendingOutput == null)
        {
            Status = "detector: output is not a float tensor - unexpected model";
            stage = Stage.Idle;
            return;
        }

        pendingOutput.ReadbackRequest();   // non-blocking GPU -> CPU copy
        stage = Stage.Reading;
    }

    private void FinishRun()
    {
        float[] data = pendingOutput.DownloadToArray();
        TensorShape shape = pendingOutput.shape;
        pendingOutput = null;

        LastInferenceMs = (Time.realtimeSinceStartup - runStart) * 1000f;
        ipsWindowCount++;

        var candidates = Decode(data, shape);
        RawCandidates = candidates.Count;

        RejectedNotRed = 0;
        if (requireRed) ApplyRedCheck(candidates);

        Latest.Clear();
        Latest.AddRange(Nms(candidates));
        ResultId++;

        Status = string.Format("detector: {0:F0} ms/inference  {1:F1}/s  raw {2}  kept {3}{4}",
            LastInferenceMs, InferencesPerSecond, RawCandidates, Latest.Count,
            requireRed ? (RedCheckUnavailable ? "  red-check N/A" : "  not-red " + RejectedNotRed) : "");

        stage = Stage.Idle;
    }

    // ------------------------------------------------------------------ decode

    private List<Detection> Decode(float[] d, TensorShape shape)
    {
        var list = new List<Detection>();
        if (shape.rank < 2) return list;

        int a = shape[shape.rank - 2];
        int b = shape[shape.rank - 1];

        // End-to-end exports: rows of [x1 y1 x2 y2 score class].
        if (b == 6 && a > b)
        {
            for (int i = 0; i < a; i++)
            {
                int o = i * 6;
                float score = d[o + 4];
                int cls = Mathf.RoundToInt(d[o + 5]);
                if (score < scoreThreshold || !acceptIds.Contains(cls)) continue;

                list.Add(Make(d[o], d[o + 1], d[o + 2], d[o + 3], score, cls, corners: true));
            }
            ScaleIfPixels(list);
            return list;
        }

        // Anchor exports. Channels are the short axis (4 + classes = 84 for
        // COCO), anchors the long one (2100 at 320 px).
        bool anchorsLast = a < b;
        int channels = anchorsLast ? a : b;
        int anchors = anchorsLast ? b : a;
        int classes = channels - 4;
        if (classes <= 0) return list;

        for (int n = 0; n < anchors; n++)
        {
            float best = 0f;
            int bestCls = -1;
            foreach (int c in acceptIds)
            {
                if (c < 0 || c >= classes) continue;
                float s = anchorsLast ? d[(4 + c) * anchors + n] : d[n * channels + 4 + c];
                if (s > best) { best = s; bestCls = c; }
            }
            if (best < scoreThreshold) continue;

            float cx, cy, w, h;
            if (anchorsLast)
            {
                cx = d[n]; cy = d[anchors + n]; w = d[2 * anchors + n]; h = d[3 * anchors + n];
            }
            else
            {
                int o = n * channels;
                cx = d[o]; cy = d[o + 1]; w = d[o + 2]; h = d[o + 3];
            }

            list.Add(Make(cx, cy, w, h, best, bestCls, corners: false));
        }

        ScaleIfPixels(list);
        return list;
    }

    private Detection Make(float p0, float p1, float p2, float p3, float score, int cls, bool corners)
    {
        Rect r = corners
            ? Rect.MinMaxRect(p0, p1, p2, p3)
            : new Rect(p0 - p2 * 0.5f, p1 - p3 * 0.5f, p2, p3);

        return new Detection
        {
            box = r, score = score, classId = cls,
            redFraction = -1f, frameTime = frameTimeOfRun,
        };
    }

    /// <summary>
    /// Most exports emit boxes in model-input pixels; some emit 0..1. Decide
    /// from the numbers rather than a setting nobody will remember to change.
    /// </summary>
    private void ScaleIfPixels(List<Detection> list)
    {
        float maxCoord = 0f;
        foreach (var det in list) maxCoord = Mathf.Max(maxCoord, det.box.xMax, det.box.yMax);
        if (maxCoord <= 1.5f) return;

        for (int i = 0; i < list.Count; i++)
        {
            var det = list[i];
            det.box = new Rect(det.box.x / inputWidth, det.box.y / inputHeight,
                               det.box.width / inputWidth, det.box.height / inputHeight);
            list[i] = det;
        }
    }

    // --------------------------------------------------------------- red check

    /// <summary>
    /// Keep boxes whose lower-middle region is mostly red.
    ///
    /// Samples the cup's body (the middle of the box, lower half) rather than
    /// the whole box, which also contains floor. Reads only the box's pixels,
    /// only for the few boxes that survived the score threshold.
    /// </summary>
    private void ApplyRedCheck(List<Detection> list)
    {
        var tex = cam.Texture;
        if (tex == null) return;

        int W = tex.width, H = tex.height;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var det = list[i];

            // Body region in normalised, y-down coordinates.
            float u0 = det.box.x + det.box.width * 0.25f;
            float u1 = det.box.x + det.box.width * 0.75f;
            float v0 = det.box.y + det.box.height * 0.40f;
            float v1 = det.box.y + det.box.height * 0.90f;

            int px = Mathf.Clamp(Mathf.FloorToInt(u0 * W), 0, W - 1);
            int pw = Mathf.Clamp(Mathf.CeilToInt((u1 - u0) * W), 1, W - px);

            // Texture rows count up from the bottom unless the image is flipped.
            float texV0 = flipOfRun ? v0 : 1f - v1;
            int py = Mathf.Clamp(Mathf.FloorToInt(texV0 * H), 0, H - 1);
            int ph = Mathf.Clamp(Mathf.CeilToInt((v1 - v0) * H), 1, H - py);

            Color[] px32;
            try { px32 = tex.GetPixels(px, py, pw, ph); }
            catch { RedCheckUnavailable = true; return; }

            int red = 0, nonBlack = 0;
            for (int k = 0; k < px32.Length; k += 2)   // every other pixel is plenty
            {
                Color c = px32[k];
                if (c.r + c.g + c.b > 0.02f) nonBlack++;
                Color.RGBToHSV(c, out float h, out float s, out float v);
                if ((h < 0.055f || h > 0.94f) && s > 0.40f && v > 0.18f) red++;
            }

            // An all-black readback means the platform cannot read this
            // texture on the CPU. Stop filtering rather than rejecting every
            // cup - and say so on the HUD.
            if (nonBlack == 0) { RedCheckUnavailable = true; return; }

            det.redFraction = red / Mathf.Max(1f, px32.Length * 0.5f);
            list[i] = det;

            if (det.redFraction < minRedFraction)
            {
                list.RemoveAt(i);
                RejectedNotRed++;
            }
        }
    }

    // --------------------------------------------------------------------- NMS

    private List<Detection> Nms(List<Detection> list)
    {
        list.Sort((x, y) => y.score.CompareTo(x.score));
        var keep = new List<Detection>();

        foreach (var det in list)
        {
            bool overlaps = false;
            foreach (var k in keep)
                if (Iou(det.box, k.box) > iouThreshold) { overlaps = true; break; }
            if (!overlaps) keep.Add(det);
            if (keep.Count >= 20) break;
        }
        return keep;
    }

    private static float Iou(Rect a, Rect b)
    {
        float ix = Mathf.Max(0f, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin));
        float iy = Mathf.Max(0f, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
        float inter = ix * iy;
        float uni = a.width * a.height + b.width * b.height - inter;
        return uni > 1e-6f ? inter / uni : 0f;
    }

    // ----------------------------------------------------------------- cleanup

    void OnDestroy() { DisposeAll(); }

    private void DisposeAll()
    {
        input?.Dispose(); input = null;
        worker?.Dispose(); worker = null;
        pendingOutput = null;
        stage = Stage.Idle;
    }
}
