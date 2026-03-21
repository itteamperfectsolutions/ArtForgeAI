using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Production background removal pipeline:
/// 1. U²-Net  — quick subject detection (coarse mask at 320×320)
/// 2. MODNet  — alpha matting refinement (512×512, trimap-free)
/// 3. Edge Feathering — custom Gaussian-weighted feather for print-ready edges
/// 4. PNG Export — RGBA with proper premultiplied alpha
///
/// Implements IBackgroundRemovalService so every feature in the app
/// (EmbroideryArt, PassportPhoto, Merger, etc.) benefits automatically.
/// </summary>
public sealed class BgRemovalPipelineService : IBackgroundRemovalService, IDisposable
{
    private readonly OnnxBgRemovalService _u2net;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BgRemovalPipelineService> _logger;
    private readonly string _outputDir;

    // MODNet ONNX
    private readonly string _modnetModelDir;
    private readonly string _modnetModelPath;
    private readonly SemaphoreSlim _modnetLock = new(1, 1);
    private InferenceSession? _modnetSession;
    private string _modnetInputName = string.Empty;
    private string _modnetOutputName = string.Empty;

    private const string ModNetFileName = "modnet_photographic_portrait_matting.onnx";
    private const string ModNetUrl =
        "https://github.com/ZHKKKe/MODNet/raw/master/pretrained/modnet_photographic_portrait_matting.onnx";
    private const int ModNetSize = 512; // MODNet input resolution

    // Edge feathering config
    private const int FeatherRadius = 3;
    private const float FeatherSigma = 1.2f;

    public bool IsAvailable => true; // Both models auto-download

    public BgRemovalPipelineService(
        OnnxBgRemovalService u2net,
        IWebHostEnvironment env,
        ILogger<BgRemovalPipelineService> logger)
    {
        _u2net = u2net;
        _env = env;
        _logger = logger;
        _outputDir = Path.Combine(env.WebRootPath, "generated");
        Directory.CreateDirectory(_outputDir);
        _modnetModelDir = Path.Combine(AppContext.BaseDirectory, "Models");
        _modnetModelPath = Path.Combine(_modnetModelDir, ModNetFileName);
    }

    // ───────────────────── MODNet model management ─────────────────────

    private async Task EnsureModNetAsync()
    {
        if (_modnetSession is not null) return;
        await _modnetLock.WaitAsync();
        try
        {
            if (_modnetSession is not null) return;
            Directory.CreateDirectory(_modnetModelDir);

            if (!File.Exists(_modnetModelPath))
            {
                _logger.LogInformation("Downloading MODNet model from {Url}...", ModNetUrl);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var bytes = await http.GetByteArrayAsync(ModNetUrl);
                await File.WriteAllBytesAsync(_modnetModelPath, bytes);
                _logger.LogInformation("MODNet model downloaded ({Size:N0} bytes)", bytes.Length);
            }

            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                InterOpNumThreads = 4,
                IntraOpNumThreads = 4,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };
            _modnetSession = new InferenceSession(_modnetModelPath, opts);
            _modnetInputName = _modnetSession.InputMetadata.Keys.First();
            _modnetOutputName = _modnetSession.OutputMetadata.Keys.Last(); // MODNet has multiple outputs; last is the matte
            _logger.LogInformation("MODNet ONNX model loaded (input: {In}, output: {Out})",
                _modnetInputName, _modnetOutputName);
        }
        finally
        {
            _modnetLock.Release();
        }
    }

    // ───────────────────── Core pipeline ─────────────────────

    /// <summary>
    /// Full pipeline: U²-Net → MODNet → Edge Feathering → PNG.
    /// </summary>
    private async Task<(byte[] pngBytes, float[,] mask, int w, int h)> RunPipelineAsync(byte[] imageBytes)
    {
        // Step 1: U²-Net coarse mask + bounds
        _logger.LogInformation("Pipeline step 1/3: U²-Net subject detection...");
        var u2Result = await _u2net.RemoveBackgroundWithBoundsAsync(imageBytes);

        // Load original for dimensions
        using var original = Image.Load<Rgba32>(imageBytes);
        int origW = original.Width, origH = original.Height;

        // Step 2: MODNet alpha matting refinement
        float[,] refinedMask;
        try
        {
            await EnsureModNetAsync();
            _logger.LogInformation("Pipeline step 2/3: MODNet alpha matting refinement...");
            var coarseMask = ExtractMaskFromTransparentPng(u2Result.TransparentPngBytes, origW, origH);
            refinedMask = await RunModNetAsync(imageBytes, coarseMask, origW, origH);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MODNet refinement failed, falling back to U²-Net mask only");
            refinedMask = ExtractMaskFromTransparentPng(u2Result.TransparentPngBytes, origW, origH);
        }

        // Step 3: Edge feathering
        _logger.LogInformation("Pipeline step 3/3: Edge feathering...");
        var feathered = ApplyEdgeFeathering(refinedMask, origW, origH);

        // Apply final mask and encode PNG
        ApplyFinalMask(original, feathered);

        using var ms = new MemoryStream();
        var encoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };
        original.SaveAsPng(ms, encoder);

        return (ms.ToArray(), feathered, origW, origH);
    }

    /// <summary>Extract alpha channel as float mask from a transparent PNG.</summary>
    private static float[,] ExtractMaskFromTransparentPng(byte[] pngBytes, int expectedW, int expectedH)
    {
        using var img = Image.Load<Rgba32>(pngBytes);
        var mask = new float[expectedH, expectedW];

        // If dimensions differ, resize first
        if (img.Width != expectedW || img.Height != expectedH)
            img.Mutate(ctx => ctx.Resize(expectedW, expectedH));

        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < expectedH; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < expectedW; x++)
                    mask[y, x] = row[x].A / 255f;
            }
        });
        return mask;
    }

    // ───────────────────── MODNet inference ─────────────────────

    private async Task<float[,]> RunModNetAsync(byte[] imageBytes, float[,] coarseMask, int origW, int origH)
    {
        return await Task.Run(() =>
        {
            using var img = Image.Load<Rgba32>(imageBytes);

            // Pad to square before resizing to MODNet's expected input
            int maxDim = Math.Max(origW, origH);
            // Make divisible by 32 for MODNet
            int padSize = ((maxDim + 31) / 32) * 32;
            int targetSize = Math.Min(padSize, ModNetSize);
            // Clamp to reasonable size
            targetSize = Math.Max(targetSize, 256);
            // Round to nearest multiple of 32
            targetSize = ((targetSize + 31) / 32) * 32;

            using var resized = img.Clone(ctx => ctx.Resize(targetSize, targetSize));
            var inputTensor = PreprocessForModNet(resized, targetSize);

            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_modnetInputName, inputTensor) };
            using var results = _modnetSession!.Run(inputs);

            // MODNet outputs the matte directly (last output)
            var output = results.Last().AsTensor<float>();
            var modnetMask = PostprocessModNetMask(output, targetSize, origW, origH);

            // Combine: use MODNet where it's confident, blend with U²-Net coarse mask
            return CombineMasks(coarseMask, modnetMask, origW, origH);
        });
    }

    /// <summary>Preprocess for MODNet: normalize to [0,1], then ImageNet standardization.</summary>
    private static DenseTensor<float> PreprocessForModNet(Image<Rgba32> image, int size)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < size; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < size; x++)
                {
                    var px = row[x];
                    // MODNet expects BGR normalized with ImageNet stats
                    tensor[0, 0, y, x] = (px.R / 255f - 0.485f) / 0.229f;
                    tensor[0, 1, y, x] = (px.G / 255f - 0.456f) / 0.224f;
                    tensor[0, 2, y, x] = (px.B / 255f - 0.406f) / 0.225f;
                }
            }
        });
        return tensor;
    }

    /// <summary>Extract matte from MODNet output and resize to original dimensions.</summary>
    private static float[,] PostprocessModNetMask(Tensor<float> output, int modelSize, int targetW, int targetH)
    {
        // MODNet output shape: [1, 1, H, W] — alpha matte in [0, 1]
        int outH = output.Dimensions[2];
        int outW = output.Dimensions[3];

        var mask = new float[targetH, targetW];
        float scaleY = (outH - 1f) / Math.Max(1, targetH - 1);
        float scaleX = (outW - 1f) / Math.Max(1, targetW - 1);

        for (int y = 0; y < targetH; y++)
        {
            float srcY = y * scaleY;
            int y0 = (int)srcY;
            int y1 = Math.Min(y0 + 1, outH - 1);
            float fy = srcY - y0;

            for (int x = 0; x < targetW; x++)
            {
                float srcX = x * scaleX;
                int x0 = (int)srcX;
                int x1 = Math.Min(x0 + 1, outW - 1);
                float fx = srcX - x0;

                float val = output[0, 0, y0, x0] * (1 - fx) * (1 - fy)
                          + output[0, 0, y0, x1] * fx * (1 - fy)
                          + output[0, 0, y1, x0] * (1 - fx) * fy
                          + output[0, 0, y1, x1] * fx * fy;

                mask[y, x] = Math.Clamp(val, 0f, 1f);
            }
        }
        return mask;
    }

    /// <summary>
    /// Combine U²-Net coarse mask with MODNet refined mask.
    /// MODNet excels at hair/fur detail; U²-Net gives better overall subject detection.
    /// Strategy: where both agree → use MODNet (finer). Where they disagree → favour the one
    /// that says "foreground" in the core region (U²-Net) and MODNet at edges.
    /// </summary>
    private static float[,] CombineMasks(float[,] coarse, float[,] modnet, int w, int h)
    {
        var result = new float[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float c = coarse[y, x];
                float m = modnet[y, x];

                // Core foreground: both models agree (>0.8) → trust MODNet for detail
                if (c > 0.8f && m > 0.5f)
                    result[y, x] = m;
                // Core background: both agree it's bg → 0
                else if (c < 0.2f && m < 0.3f)
                    result[y, x] = 0f;
                // Edge region: weighted blend — MODNet has better alpha detail at edges
                else
                    result[y, x] = c * 0.4f + m * 0.6f;
            }
        }
        return result;
    }

    // ───────────────────── Edge Feathering ─────────────────────

    /// <summary>
    /// Apply Gaussian-weighted edge feathering to soften mask boundaries.
    /// Only affects pixels near the alpha boundary (0 < alpha < 1 transition zone).
    /// </summary>
    private static float[,] ApplyEdgeFeathering(float[,] mask, int w, int h)
    {
        // Build 1D Gaussian kernel
        int kernelSize = FeatherRadius * 2 + 1;
        var kernel = new float[kernelSize];
        float kernelSum = 0f;
        for (int i = 0; i < kernelSize; i++)
        {
            float d = i - FeatherRadius;
            kernel[i] = MathF.Exp(-(d * d) / (2f * FeatherSigma * FeatherSigma));
            kernelSum += kernel[i];
        }
        for (int i = 0; i < kernelSize; i++)
            kernel[i] /= kernelSum;

        // Detect edge pixels: any pixel where a neighbour has a significantly different alpha
        var isEdge = new bool[h, w];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                float m = mask[y, x];
                // Check 4 neighbours for transition
                if (MathF.Abs(m - mask[y - 1, x]) > 0.3f ||
                    MathF.Abs(m - mask[y + 1, x]) > 0.3f ||
                    MathF.Abs(m - mask[y, x - 1]) > 0.3f ||
                    MathF.Abs(m - mask[y, x + 1]) > 0.3f)
                {
                    // Mark a region around this edge pixel
                    for (int dy = -FeatherRadius; dy <= FeatherRadius; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -FeatherRadius; dx <= FeatherRadius; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= w) continue;
                            isEdge[ny, nx] = true;
                        }
                    }
                }
            }
        }

        // Separable Gaussian blur on edge pixels only
        // Horizontal pass
        var temp = (float[,])mask.Clone();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!isEdge[y, x]) continue;
                float sum = 0f;
                for (int k = 0; k < kernelSize; k++)
                {
                    int sx = Math.Clamp(x + k - FeatherRadius, 0, w - 1);
                    sum += mask[y, sx] * kernel[k];
                }
                temp[y, x] = sum;
            }
        }

        // Vertical pass
        var result = (float[,])temp.Clone();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!isEdge[y, x]) continue;
                float sum = 0f;
                for (int k = 0; k < kernelSize; k++)
                {
                    int sy = Math.Clamp(y + k - FeatherRadius, 0, h - 1);
                    sum += temp[sy, x] * kernel[k];
                }
                result[y, x] = sum;
            }
        }

        return result;
    }

    // ───────────────────── Mask application with defringe ─────────────────────

    /// <summary>Apply refined mask as alpha, with foreground color sampling for edge defringe.</summary>
    private static void ApplyFinalMask(Image<Rgba32> image, float[,] mask)
    {
        int w = image.Width, h = image.Height;
        const float opaqueThresh = 0.95f;
        const int sampleRadius = 4;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    float m = mask[y, x];
                    if (m <= 0.01f)
                    {
                        row[x] = new Rgba32(0, 0, 0, 0);
                        continue;
                    }

                    byte alpha = (byte)(Math.Clamp(m, 0f, 1f) * 255);

                    if (m >= opaqueThresh)
                    {
                        row[x] = new Rgba32(row[x].R, row[x].G, row[x].B, alpha);
                        continue;
                    }

                    // Semi-transparent: sample nearby opaque foreground pixels for defringe
                    int rSum = 0, gSum = 0, bSum = 0, count = 0;
                    int yStart = Math.Max(0, y - sampleRadius);
                    int yEnd = Math.Min(h - 1, y + sampleRadius);
                    int xStart = Math.Max(0, x - sampleRadius);
                    int xEnd = Math.Min(w - 1, x + sampleRadius);

                    for (int sy = yStart; sy <= yEnd; sy++)
                    {
                        var sRow = accessor.GetRowSpan(sy);
                        for (int sx = xStart; sx <= xEnd; sx++)
                        {
                            if (mask[sy, sx] >= opaqueThresh)
                            {
                                rSum += sRow[sx].R;
                                gSum += sRow[sx].G;
                                bSum += sRow[sx].B;
                                count++;
                            }
                        }
                    }

                    if (count > 0)
                        row[x] = new Rgba32((byte)(rSum / count), (byte)(gSum / count), (byte)(bSum / count), alpha);
                    else
                        row[x] = new Rgba32(0, 0, 0, 0);
                }
            }
        });
    }

    // ───────────────────── IBackgroundRemovalService implementation ─────────────────────

    public async Task<BackgroundRemovalResult> RemoveBackgroundAsync(string sourceImagePath, string backgroundColor = "white")
    {
        var fullPath = ResolveFullPath(sourceImagePath);
        var imageBytes = await File.ReadAllBytesAsync(fullPath);

        var (pngBytes, _, _, _) = await RunPipelineAsync(imageBytes);

        // Save transparent PNG
        var transparentName = $"{Guid.NewGuid():N}_transparent.png";
        var transparentPath = Path.Combine(_outputDir, transparentName);
        await File.WriteAllBytesAsync(transparentPath, pngBytes);

        // Create colored version with specified background
        var coloredPath = await RecolorBackgroundFromBytesAsync(pngBytes, backgroundColor);

        return new BackgroundRemovalResult(coloredPath, $"generated/{transparentName}", pngBytes);
    }

    public async Task<string> RecolorBackgroundAsync(string transparentImagePath, string backgroundColor)
    {
        var fullPath = ResolveFullPath(transparentImagePath);
        var bytes = await File.ReadAllBytesAsync(fullPath);
        return await RecolorBackgroundFromBytesAsync(bytes, backgroundColor);
    }

    public async Task<string> RecolorBackgroundFromBytesAsync(byte[] transparentPngBytes, string backgroundColor)
    {
        return await Task.Run(() =>
        {
            using var fg = Image.Load<Rgba32>(transparentPngBytes);
            var parsedColor = BackgroundColorParser.ParseBackgroundColor(backgroundColor);
            var bgColor = parsedColor ?? new Rgba32(0, 0, 0, 0);

            using var result = new Image<Rgba32>(fg.Width, fg.Height);
            result.Mutate(ctx => ctx.BackgroundColor(new Color(bgColor)));

            // Composite foreground over background
            result.ProcessPixelRows(fg, (bgAcc, fgAcc) =>
            {
                for (int y = 0; y < fg.Height; y++)
                {
                    var bgRow = bgAcc.GetRowSpan(y);
                    var fgRow = fgAcc.GetRowSpan(y);
                    for (int x = 0; x < fg.Width; x++)
                    {
                        var f = fgRow[x];
                        if (f.A == 255) { bgRow[x] = f; continue; }
                        if (f.A == 0) continue;
                        float a = f.A / 255f;
                        bgRow[x] = new Rgba32(
                            (byte)(f.R * a + bgRow[x].R * (1 - a)),
                            (byte)(f.G * a + bgRow[x].G * (1 - a)),
                            (byte)(f.B * a + bgRow[x].B * (1 - a)),
                            255);
                    }
                }
            });

            var fileName = $"{Guid.NewGuid():N}_colored.png";
            var filePath = Path.Combine(_outputDir, fileName);
            result.SaveAsPng(filePath, new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 });
            return $"generated/{fileName}";
        });
    }

    public async Task<string> CompositeOverImageAsync(string transparentImagePath, string backgroundImagePath)
    {
        var fullPath = ResolveFullPath(transparentImagePath);
        var bytes = await File.ReadAllBytesAsync(fullPath);
        return await CompositeOverImageFromBytesAsync(bytes, backgroundImagePath);
    }

    public async Task<string> CompositeOverImageFromBytesAsync(byte[] transparentPngBytes, string backgroundImagePath)
    {
        return await Task.Run(() =>
        {
            using var fg = Image.Load<Rgba32>(transparentPngBytes);
            var bgFullPath = ResolveFullPath(backgroundImagePath);
            using var bg = Image.Load<Rgba32>(bgFullPath);

            // Resize bg to match fg
            bg.Mutate(ctx => ctx.Resize(fg.Width, fg.Height));

            bg.ProcessPixelRows(fg, (bgAcc, fgAcc) =>
            {
                for (int y = 0; y < fg.Height; y++)
                {
                    var bgRow = bgAcc.GetRowSpan(y);
                    var fgRow = fgAcc.GetRowSpan(y);
                    for (int x = 0; x < fg.Width; x++)
                    {
                        var f = fgRow[x];
                        if (f.A == 255) { bgRow[x] = f; continue; }
                        if (f.A == 0) continue;
                        float a = f.A / 255f;
                        bgRow[x] = new Rgba32(
                            (byte)(f.R * a + bgRow[x].R * (1 - a)),
                            (byte)(f.G * a + bgRow[x].G * (1 - a)),
                            (byte)(f.B * a + bgRow[x].B * (1 - a)),
                            255);
                    }
                }
            });

            var fileName = $"{Guid.NewGuid():N}_composite.png";
            var filePath = Path.Combine(_outputDir, fileName);
            bg.SaveAsPng(filePath);
            return $"generated/{fileName}";
        });
    }

    public async Task<SmartSelectResult> DetectSubjectAsync(byte[] imageBytes,
        int? regionX = null, int? regionY = null, int? regionW = null, int? regionH = null)
    {
        var (pngBytes, mask, w, h) = await RunPipelineAsync(imageBytes);

        // Compute bounding box
        int minX = w, minY = h, maxX = 0, maxY = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y, x] >= 0.5f)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }

        int bboxW = maxX > minX ? maxX - minX + 1 : w;
        int bboxH = maxY > minY ? maxY - minY + 1 : h;
        if (maxX <= minX) { minX = 0; minY = 0; }

        // Generate edge outline
        using var outlineImg = Image.Load<Rgba32>(imageBytes);
        var edgeColor = new Rgba32(0, 200, 255, 200);
        outlineImg.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    if (mask[y, x] < 0.5f) continue;
                    bool isEdge = (x > 0 && mask[y, x - 1] < 0.5f)
                               || (x < w - 1 && mask[y, x + 1] < 0.5f)
                               || (y > 0 && mask[y - 1, x] < 0.5f)
                               || (y < h - 1 && mask[y + 1, x] < 0.5f);
                    if (isEdge)
                    {
                        row[x] = edgeColor;
                        if (x + 1 < w) row[x + 1] = edgeColor;
                    }
                }
            }
        });

        var rgbaEnc = new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };

        var outlineName = $"{Guid.NewGuid():N}_outline.png";
        outlineImg.SaveAsPng(Path.Combine(_outputDir, outlineName), rgbaEnc);

        var transparentName = $"{Guid.NewGuid():N}_transparent.png";
        await File.WriteAllBytesAsync(Path.Combine(_outputDir, transparentName), pngBytes);

        return new SmartSelectResult(
            minX, minY, bboxW, bboxH, w, h,
            $"generated/{outlineName}", $"generated/{transparentName}", pngBytes);
    }

    public async Task<byte[]> CropWithMarginAsync(byte[] imageBytes, int x, int y, int width, int height)
    {
        return await Task.Run(() =>
        {
            using var img = Image.Load<Rgba32>(imageBytes);
            int cx = Math.Max(0, x);
            int cy = Math.Max(0, y);
            int cw = Math.Min(width, img.Width - cx);
            int ch = Math.Min(height, img.Height - cy);
            img.Mutate(ctx => ctx.Crop(new Rectangle(cx, cy, cw, ch)));
            using var ms = new MemoryStream();
            img.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 });
            return ms.ToArray();
        });
    }

    public async Task<byte[]> GenerateCutLineAsync(byte[] transparentPngBytes)
    {
        return await Task.Run(() => CutLineGenerator.Generate(transparentPngBytes));
    }

    // ───────────────────── Helpers ─────────────────────

    private string ResolveFullPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        // Strip leading slash
        var clean = path.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString());
        return Path.Combine(_env.WebRootPath, clean);
    }

    public void Dispose()
    {
        _modnetSession?.Dispose();
        _modnetLock.Dispose();
    }
}
