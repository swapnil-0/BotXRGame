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

    [Header("Colour detection")]
    [Tooltip("Also find cups as red blobs, without the model.\n\nThe pretrained " +
             "COCO model only knows upright cups - an upside-down or knocked-over " +
             "solo cup scores nothing, which is most of the cups in this game. A " +
             "red blob of cup size on the floor is a cup in any pose. Size is " +
             "checked by the tracker, which knows the distance.")]
    public bool colourDetect = true;

    [Tooltip("Width of the colour grid in cells; the stride is picked from the " +
             "frame width to match. 640 on a 1280-wide frame reads every 2nd " +
             "pixel, so a cup 4 m away is still ~6 cells across.")]
    public int colourGridWidth = 640;

    [Tooltip("Smallest blob, in grid cells, that can be a cup.")]
    public int minBlobCells = 6;

    [Tooltip("Red cells a blob needs before its pose is read. Below this the " +
             "shape is a few pixels and any pose would be a guess - the cup " +
             "shows with no arrow instead.")]
    public int minCellsForPose = 15;

    [Tooltip("Colour-only blobs must sit on open floor: at most this fraction " +
             "of the ring around them may be red. Cups in this game stand in the " +
             "open; the red things that fooled the first build (dustpans, a bin, " +
             "a mat) were all in clutter or against other red.")]
    [Range(0f, 1f)] public float maxRingRed = 0.10f;

    [Tooltip("...and at most this fraction of the ring may be dark. Clutter has " +
             "black parts next to its red ones; open floor does not.")]
    [Range(0f, 1f)] public float maxRingDark = 0.25f;

    [Header("Tuning frames")]
    [Tooltip("Save a camera frame every few seconds while running, to " +
             "<persistentDataPath>/cupframes, for tuning the colour pass offline " +
             "with tools/cup_pose_check.py on real headset images. Pull with " +
             "tools/pull-cup-frames.sh.")]
    public bool saveTuningFrames = true;
    public float tuningFrameInterval = 8f;
    public int maxTuningFrames = 40;

    [Tooltip("A blob whose long axis is this many times its short axis, lying " +
             "near horizontal in the image, is a cup on its side.")]
    public float tippedElongation = 1.35f;

    [Tooltip("White inside visible above the red, per red pixel, that makes a " +
             "cup upright. Test photos: 0.59 and 0.84 upright, at most 0.03 " +
             "for upside-down or tipped cups.")]
    public float whiteAboveUpright = 0.20f;

    [Tooltip("Top width / bottom width below which a standing cup is upside " +
             "down. Test photos: 0.95 upside down, 1.18 lying pointing away.")]
    public float upsideDownTaper = 1.05f;

    [Tooltip("Flip the image vertically before the model sees it. Starts from " +
             "the camera's own mirrored flag; toggle in the headset if the " +
             "preview boxes sit upside-down relative to the cups.")]
    public bool invertFlip = false;

    [Header("Wiring")]
    public PassthroughCamera cam;

    // ------------------------------------------------------------------ output

    public enum CupPose { Unknown, Upright, UpsideDown, Tipped }

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

        /// <summary>True when found as a red blob rather than by the model.</summary>
        public bool fromColour;

        /// <summary>Pose read from the shape of the red pixels in the box.</summary>
        public CupPose pose;

        /// <summary>
        /// Tipped cups only: image points (normalised, y down) of the base and
        /// the mouth, so the tracker can turn them into a direction on the floor.
        /// </summary>
        public Vector2 baseUv, mouthUv;

        /// <summary>Top width / bottom width when standing; wide end / narrow end when tipped.</summary>
        public float taper;

        /// <summary>White inside seen above the red, per red cell. High = upright.</summary>
        public float whiteAbove;
    }

    public readonly List<Detection> Latest = new List<Detection>();

    /// <summary>Increments each time a new set of detections is published.</summary>
    public int ResultId { get; private set; }

    public float LastInferenceMs { get; private set; }
    public float InferencesPerSecond { get; private set; }
    public int RawCandidates { get; private set; }
    public int RejectedNotRed { get; private set; }
    public bool RedCheckUnavailable { get; private set; }
    public int ColourBlobs { get; private set; }
    public int RejectedClutter { get; private set; }
    public bool ColourUnavailable { get; private set; }
    public int TuningFramesSaved { get; private set; }
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

    // Red mask of the frame the model is running on, top row first.
    private Color32[] pixels;
    private bool[] mask;
    private float[] cellS, cellV;
    private readonly List<float> ringV = new List<float>();
    private int[] labels;
    private int gridW, gridH;
    private bool maskValid;
    private readonly Stack<int> fill = new Stack<int>();
    private readonly List<Detection> colourCandidates = new List<Detection>();

    private Texture2D tuningTex;
    private float nextTuningSave;

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

        // Same frame as the model input, so colour blobs, model boxes and the
        // pose read from the mask all describe one moment.
        ReadMask();
        FindBlobs(colourCandidates);
        MaybeSaveTuningFrame();

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

        // Model boxes win NMS against a blob on the same cup (higher score);
        // either way the pose comes from the mask, so it does not matter which.
        if (colourDetect) candidates.AddRange(colourCandidates);

        Latest.Clear();
        foreach (var kept in Nms(candidates))
        {
            var det = kept;
            EstimatePose(ref det);
            Latest.Add(det);
        }
        ResultId++;

        string colour = !colourDetect ? "  colour off"
                      : ColourUnavailable ? "  colour N/A"
                      : "  colour " + ColourBlobs + " (clutter " + RejectedClutter + ")";
        Status = string.Format("detector: {0:F0} ms/inference  {1:F1}/s  model {2}  kept {3}{4}{5}",
            LastInferenceMs, InferencesPerSecond, RawCandidates, Latest.Count,
            requireRed ? (RedCheckUnavailable ? "  red-check N/A" : "  not-red " + RejectedNotRed) : "",
            colour);

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

    // ------------------------------------------------------------ colour mask

    /// <summary>
    /// Downsampled red mask of the current camera frame, top row first. Read
    /// even with colour detection off: the pose of model boxes comes from it.
    /// </summary>
    private void ReadMask()
    {
        maskValid = false;

        var tex = cam.Texture;
        if (tex == null || tex.width < 16 || tex.height < 16) return;
        int W = tex.width, H = tex.height;

        if (pixels == null || pixels.Length != W * H) pixels = new Color32[W * H];
        try { tex.GetPixels32(pixels); }
        catch { ColourUnavailable = true; return; }

        int s = Mathf.Max(1, W / Mathf.Max(64, colourGridWidth));
        gridW = W / s;
        gridH = H / s;
        int cells = gridW * gridH;
        if (mask == null || mask.Length != cells)
        {
            mask = new bool[cells];
            cellS = new float[cells];
            cellV = new float[cells];
        }

        int nonBlack = 0;
        for (int gy = 0; gy < gridH; gy++)
        {
            // Grid rows run top-down; texture rows run bottom-up unless flipped.
            int row = flipOfRun ? gy * s : H - 1 - gy * s;
            int o = row * W;
            for (int gx = 0; gx < gridW; gx++)
            {
                Color32 c = pixels[o + gx * s];
                int r = c.r, g = c.g, b = c.b;
                if (r + g + b > 5) nonBlack++;

                // HSV by hand, integers where possible: Color.RGBToHSV on a
                // 640x480 grid five times a second is real main-thread time.
                int max = r > g ? (r > b ? r : b) : (g > b ? g : b);
                int min = r < g ? (r < b ? r : b) : (g < b ? g : b);
                int delta = max - min;
                float sat = max > 0 ? delta / (float)max : 0f;

                // Same thresholds as the red check (hue < 0.055 or > 0.94,
                // s > 0.40, v > 0.18). With red the largest channel, hue is
                // (g - b) / delta / 6, so the hue window is a (g - b) window.
                bool red = max == r && delta > 0 && max > 46 && sat > 0.40f &&
                           (g - b) < 0.33f * delta && (g - b) > -0.36f * delta;

                int k = gy * gridW + gx;
                mask[k] = red;
                cellS[k] = sat;
                cellV[k] = max / 255f;
            }
        }

        // All black = this platform cannot read the texture on the CPU.
        ColourUnavailable = nonBlack == 0;
        maskValid = !ColourUnavailable;
    }

    /// <summary>Connected red regions of cup-like shape, as detections.</summary>
    private void FindBlobs(List<Detection> outList)
    {
        outList.Clear();
        ColourBlobs = 0;
        RejectedClutter = 0;
        if (!maskValid) return;

        int n = gridW * gridH;
        if (labels == null || labels.Length != n) labels = new int[n];
        System.Array.Clear(labels, 0, n);

        int next = 0;
        for (int i = 0; i < n; i++)
        {
            if (!mask[i] || labels[i] != 0) continue;

            next++;
            int count = 0, x0 = gridW, y0 = gridH, x1 = -1, y1 = -1;
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            fill.Clear();
            fill.Push(i);
            labels[i] = next;

            while (fill.Count > 0)
            {
                int k = fill.Pop();
                int x = k % gridW, y = k / gridW;
                count++;
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;

                // 8-connected: the stride can leave diagonal gaps in a thin rim.
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= gridW || ny >= gridH) continue;
                    int nk = ny * gridW + nx;
                    if (!mask[nk] || labels[nk] != 0) continue;
                    labels[nk] = next;
                    fill.Push(nk);
                }
            }

            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            float fillRatio = count / (float)(bw * bh);
            float aspect = bw / (float)bh;

            // Cups are compact. Long thin red things (a dustpan handle, a cable)
            // and huge regions are not cups, whatever their size on the floor.
            if (count < minBlobCells || count > n / 4) continue;
            if (fillRatio < 0.30f || aspect < 0.3f || aspect > 3.5f) continue;

            // A cup is at most ~2.5x as long as it is wide, even lying down. A
            // thin diagonal red edge passes the box tests but not this one.
            if (Elongation(sx, sy, sxx, syy, sxy, count) > 3f) continue;

            if (InClutter(x0, x1, y0, y1)) { RejectedClutter++; continue; }

            outList.Add(new Detection
            {
                box = new Rect(x0 / (float)gridW, y0 / (float)gridH, bw / (float)gridW, bh / (float)gridH),
                score = 0.45f,
                classId = -1,
                redFraction = fillRatio,
                frameTime = frameTimeOfRun,
                fromColour = true,
            });
        }
        ColourBlobs = outList.Count;
    }

    /// <summary>
    /// Every few seconds, write the frame the detector just read to disk, so
    /// the colour pass can be tuned on real headset images with
    /// tools/cup_pose_check.py rather than guessed at from screenshots.
    /// </summary>
    private void MaybeSaveTuningFrame()
    {
        if (!saveTuningFrames || !maskValid || TuningFramesSaved >= maxTuningFrames) return;
        if (Time.realtimeSinceStartup < nextTuningSave) return;
        nextTuningSave = Time.realtimeSinceStartup + tuningFrameInterval;

        try
        {
            var tex = cam.Texture;
            if (tuningTex == null || tuningTex.width != tex.width || tuningTex.height != tex.height)
            {
                if (tuningTex != null) Destroy(tuningTex);
                tuningTex = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            }
            tuningTex.SetPixels32(pixels);
            tuningTex.Apply(false);

            string dir = System.IO.Path.Combine(Application.persistentDataPath, "cupframes");
            System.IO.Directory.CreateDirectory(dir);
            // "flip" = the image is stored upside down relative to what the model saw.
            string name = string.Format("frame_{0:000}{1}.jpg", TuningFramesSaved, flipOfRun ? "_flip" : "");
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), tuningTex.EncodeToJPG(90));
            TuningFramesSaved++;
            Debug.LogFormat("[Cups] tuning frame saved: {0} ({1}x{2})", name, tex.width, tex.height);
        }
        catch (System.Exception e)
        {
            saveTuningFrames = false;
            Debug.LogWarning("[Cups] tuning frame save failed, turning it off: " + e.Message);
        }
    }

    /// <summary>
    /// Is the ring around this box mostly something other than open floor?
    /// A ring half the box's size on each side; too much red means the blob is
    /// part of a bigger red thing, too much dark means it sits in clutter.
    /// </summary>
    private bool InClutter(int x0, int x1, int y0, int y1)
    {
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        int padX = Mathf.Max(2, bw / 2), padY = Mathf.Max(2, bh / 2);
        int rx0 = Mathf.Max(0, x0 - padX), rx1 = Mathf.Min(gridW - 1, x1 + padX);
        int ry0 = Mathf.Max(0, y0 - padY), ry1 = Mathf.Min(gridH - 1, y1 + padY);

        int total = 0, red = 0, dark = 0;
        for (int y = ry0; y <= ry1; y++)
        for (int x = rx0; x <= rx1; x++)
        {
            if (x >= x0 && x <= x1 && y >= y0 && y <= y1) continue;
            int k = y * gridW + x;
            total++;
            if (mask[k]) red++;
            else if (cellV[k] < 0.20f) dark++;
        }
        if (total == 0) return false;
        return red > maxRingRed * total || dark > maxRingDark * total;
    }

    private static float Elongation(double sx, double sy, double sxx, double syy, double sxy, int count)
    {
        double mx = sx / count, my = sy / count;
        double cxx = sxx / count - mx * mx, cyy = syy / count - my * my, cxy = sxy / count - mx * my;
        double half = (cxx + cyy) * 0.5;
        double disc = System.Math.Sqrt(System.Math.Max(0.0, half * half - (cxx * cyy - cxy * cxy)));
        return (float)System.Math.Sqrt((half + disc) / System.Math.Max(1e-4, half - disc));
    }

    private Vector2 GridToUv(float gx, float gy) =>
        new Vector2((gx + 0.5f) / gridW, (gy + 0.5f) / gridH);

    /// <summary>
    /// Upright, upside down or on its side, from the pixels in and above the box.
    ///
    /// A solo cup is red outside, white inside, mouth 9.5 cm, base 6 cm. In order:
    ///   1. red long and lying near horizontal in the image  -> on its side
    ///   2. white interior visible above the red             -> upright
    ///   3. red narrower at the top than the bottom          -> upside down
    ///   4. otherwise: wider at the top with no white inside -> on its side,
    ///      pointing away (which looks a lot like upright in one image)
    /// Mirrored in tools/cup_pose_check.py - try a change on photos there first.
    /// Checked against the four test photos:
    /// white-above was 0.59 and 0.84 upright, 0.00 and 0.03 otherwise.
    /// Reads the mask of the frame the box came from, nothing new from the camera.
    /// </summary>
    private void EstimatePose(ref Detection det)
    {
        det.pose = CupPose.Unknown;
        if (!maskValid) return;

        int x0 = Mathf.Clamp(Mathf.FloorToInt(det.box.xMin * gridW), 0, gridW - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(det.box.xMax * gridW) - 1, x0, gridW - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(det.box.yMin * gridH), 0, gridH - 1);
        int y1 = Mathf.Clamp(Mathf.CeilToInt(det.box.yMax * gridH) - 1, y0, gridH - 1);

        // Moments of the red cells.
        int count = 0;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (!mask[y * gridW + x]) continue;
            count++;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
        }
        if (count < minCellsForPose) return;

        float mx = (float)(sx / count), my = (float)(sy / count);
        float cxx = (float)(sxx / count) - mx * mx;
        float cyy = (float)(syy / count) - my * my;
        float cxy = (float)(sxy / count) - mx * my;

        float half = (cxx + cyy) * 0.5f;
        float disc = Mathf.Sqrt(Mathf.Max(0f, half * half - (cxx * cyy - cxy * cxy)));
        float major = half + disc, minor = Mathf.Max(1e-4f, half - disc);
        float elongation = Mathf.Sqrt(major / minor);

        // Major axis angle from image +x; image y runs down. In -90..90.
        float theta = 0.5f * Mathf.Atan2(2f * cxy, cxx - cyy);
        float fromHorizontal = Mathf.Abs(theta) * Mathf.Rad2Deg;

        if (elongation >= tippedElongation && fromHorizontal < 40f)
        {
            EstimateTipped(ref det, x0, x1, y0, y1, mx, my, theta);
            return;
        }

        // Upright: the white inside shows above the red, seen from standing
        // height. "White" is relative to the floor around the cup, so a light
        // floor in a dim room is not mistaken for it.
        det.whiteAbove = WhiteAboveRatio(x0, x1, y0, y1, count);
        if (det.whiteAbove >= whiteAboveUpright)
        {
            det.pose = CupPose.Upright;
            return;
        }

        // Widest red row in the top half vs the bottom half. Widest, not mean:
        // the elliptical ends of a cup make the end rows narrow either way up.
        float mid = (y0 + y1) * 0.5f;
        int topMax = 0, bottomMax = 0, rowsWithRed = 0;
        for (int y = y0; y <= y1; y++)
        {
            int lo = -1, hi = -1;
            for (int x = x0; x <= x1; x++)
            {
                if (!mask[y * gridW + x]) continue;
                if (lo < 0) lo = x;
                hi = x;
            }
            if (lo < 0) continue;
            rowsWithRed++;
            int w = hi - lo + 1;
            if (y <= mid) topMax = Mathf.Max(topMax, w);
            else bottomMax = Mathf.Max(bottomMax, w);
        }
        if (rowsWithRed < 3 || bottomMax == 0) return;

        det.taper = topMax / (float)bottomMax;
        if (det.taper < upsideDownTaper)
        {
            det.pose = CupPose.UpsideDown;
            return;
        }

        // Wider at the top, no white inside: lying down, pointing away (or
        // toward). The end widths along the axis say which end is the mouth.
        EstimateTipped(ref det, x0, x1, y0, y1, mx, my, theta);
    }

    /// <summary>
    /// Non-red bright cells in the top half of the box and above it, per red
    /// cell. Brightness is compared against the median of a ring around the box.
    /// </summary>
    private float WhiteAboveRatio(int x0, int x1, int y0, int y1, int redCount)
    {
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;

        ringV.Clear();
        int rx0 = Mathf.Max(0, x0 - bw / 2), rx1 = Mathf.Min(gridW - 1, x1 + bw / 2);
        int ry0 = Mathf.Max(0, y0 - bh / 2), ry1 = Mathf.Min(gridH - 1, y1 + bh / 2);
        for (int y = ry0; y <= ry1; y++)
        for (int x = rx0; x <= rx1; x++)
        {
            if (x >= x0 - 1 && x <= x1 + 1 && y >= y0 - 1 && y <= y1 + 1) continue;
            ringV.Add(cellV[y * gridW + x]);
        }
        if (ringV.Count == 0) return 0f;
        ringV.Sort();
        float floorV = ringV[ringV.Count / 2];

        int white = 0;
        int wy0 = Mathf.Max(0, y0 - Mathf.RoundToInt(0.6f * bh)), wy1 = y0 + bh / 2;
        for (int y = wy0; y <= wy1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int k = y * gridW + x;
            if (!mask[k] && cellS[k] < 0.25f && cellV[k] > floorV + 0.10f) white++;
        }
        return white / (float)Mathf.Max(1, redCount);
    }

    private void EstimateTipped(ref Detection det, int x0, int x1, int y0, int y1,
                                float mx, float my, float theta)
    {
        float ax = Mathf.Cos(theta), ay = Mathf.Sin(theta);

        // Extent along the axis.
        float sMin = float.MaxValue, sMax = float.MinValue;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (!mask[y * gridW + x]) continue;
            float s = (x - mx) * ax + (y - my) * ay;
            if (s < sMin) sMin = s;
            if (s > sMax) sMax = s;
        }

        // Thickness across the axis at each end. The mouth end is the wide one.
        float cut = 0.3f * (sMax - sMin);
        float loA = float.MaxValue, hiA = float.MinValue, loB = float.MaxValue, hiB = float.MinValue;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (!mask[y * gridW + x]) continue;
            float s = (x - mx) * ax + (y - my) * ay;
            float p = -(x - mx) * ay + (y - my) * ax;
            if (s <= sMin + cut) { loA = Mathf.Min(loA, p); hiA = Mathf.Max(hiA, p); }
            if (s >= sMax - cut) { loB = Mathf.Min(loB, p); hiB = Mathf.Max(hiB, p); }
        }
        float wA = hiA >= loA ? hiA - loA + 1f : 0f;
        float wB = hiB >= loB ? hiB - loB + 1f : 0f;

        bool mouthAtMax = wB >= wA;
        Vector2 endMin = GridToUv(mx + ax * sMin, my + ay * sMin);
        Vector2 endMax = GridToUv(mx + ax * sMax, my + ay * sMax);

        det.pose = CupPose.Tipped;
        det.mouthUv = mouthAtMax ? endMax : endMin;
        det.baseUv = mouthAtMax ? endMin : endMax;
        det.taper = Mathf.Max(wA, wB) / Mathf.Max(1f, Mathf.Min(wA, wB));
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

    void OnDestroy()
    {
        DisposeAll();
        if (tuningTex != null) Destroy(tuningTex);
    }

    private void DisposeAll()
    {
        input?.Dispose(); input = null;
        worker?.Dispose(); worker = null;
        pendingOutput = null;
        stage = Stage.Idle;
    }
}
