using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Local ONNX-based background removal using U-2-Net (u2netp — portable variant).
/// Auto-downloads the model (~4.7 MB) on first use.
/// Registered as singleton (model loaded once).
/// </summary>
public sealed class OnnxBgRemovalService : IDisposable
{
    private readonly ILogger<OnnxBgRemovalService> _logger;
    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;
    private string _inputName = string.Empty;
    private string _outputName = string.Empty;

    private const string ModelFileName = "u2netp.onnx";
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx";
    private const int ModelSize = 320; // U-2-Net input resolution

    public bool IsAvailable => true; // Always available — model auto-downloads

    public OnnxBgRemovalService(
        IWebHostEnvironment env,
        ILogger<OnnxBgRemovalService> logger)
    {
        _logger = logger;
        _modelDir = Path.Combine(AppContext.BaseDirectory, "Models");
        _modelPath = Path.Combine(_modelDir, ModelFileName);
    }

    /// <summary>Ensure the ONNX model is downloaded and the session is loaded.</summary>
    private async Task EnsureModelAsync()
    {
        if (_session is not null) return;
        await _initLock.WaitAsync();
        try
        {
            if (_session is not null) return;

            Directory.CreateDirectory(_modelDir);

            if (!File.Exists(_modelPath))
            {
                _logger.LogInformation("Downloading U-2-Net model from {Url}...", ModelUrl);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var bytes = await http.GetByteArrayAsync(ModelUrl);
                await File.WriteAllBytesAsync(_modelPath, bytes);
                _logger.LogInformation("U-2-Net model downloaded ({Size:N0} bytes)", bytes.Length);
            }

            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                InterOpNumThreads = 4,
                IntraOpNumThreads = 4,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };

            _session = new InferenceSession(_modelPath, opts);
            _inputName = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();
            _logger.LogInformation("U-2-Net ONNX model loaded (input: {Input}, output: {Output})",
                _inputName, _outputName);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Result containing transparent PNG and subject bounding box (fractions 0-1).</summary>
    public record BgRemovalResult(
        byte[] TransparentPngBytes,
        double SubjectTop, double SubjectBottom,
        double SubjectLeft, double SubjectRight);

    /// <summary>
    /// Remove the background from an image.
    /// Returns PNG bytes with transparent background.
    /// </summary>
    public async Task<byte[]> RemoveBackgroundAsync(byte[] imageBytes)
    {
        var result = await RemoveBackgroundWithBoundsAsync(imageBytes);
        return result.TransparentPngBytes;
    }

    /// <summary>
    /// Remove background AND return subject bounding box (for face/crop positioning).
    /// Single U-2-Net pass gives both segmentation mask + subject bounds.
    /// </summary>
    public async Task<BgRemovalResult> RemoveBackgroundWithBoundsAsync(byte[] imageBytes)
    {
        await EnsureModelAsync();

        return await Task.Run(() =>
        {
            using var original = Image.Load<Rgba32>(imageBytes);
            int origW = original.Width;
            int origH = original.Height;

            // Preprocess: resize to 320×320, normalize with ImageNet stats
            using var resized = original.Clone(ctx => ctx.Resize(ModelSize, ModelSize));
            var inputTensor = PreprocessImage(resized);

            // Run inference
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };
            using var results = _session!.Run(inputs);
            var output = results.First().AsTensor<float>();

            // Postprocess: sigmoid → normalize → resize mask
            var mask = PostprocessMask(output, origW, origH);

            // Extract subject bounding box from mask (fractions of image dimensions)
            var bounds = ExtractSubjectBounds(mask, origW, origH);

            // Apply mask as alpha channel
            ApplyMask(original, mask);

            // Encode as RGBA PNG
            using var ms = new MemoryStream();
            var encoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };
            original.SaveAsPng(ms, encoder);

            return new BgRemovalResult(
                ms.ToArray(),
                bounds.top, bounds.bottom,
                bounds.left, bounds.right);
        });
    }

    /// <summary>Find subject bounding box from the segmentation mask. Returns fractions (0-1).</summary>
    private static (double top, double bottom, double left, double right) ExtractSubjectBounds(
        float[,] mask, int width, int height)
    {
        int minY = height, maxY = 0, minX = width, maxX = 0;
        const float threshold = 0.5f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (mask[y, x] >= threshold)
                {
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
            }
        }

        if (maxY <= minY || maxX <= minX)
            return (0.1, 0.9, 0.2, 0.8); // fallback: centered

        return (
            (double)minY / height,
            (double)maxY / height,
            (double)minX / width,
            (double)maxX / width);
    }

    /// <summary>Preprocess: RGB normalized to [0,1] then standardized with ImageNet mean/std. NCHW layout.</summary>
    private static DenseTensor<float> PreprocessImage(Image<Rgba32> image)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, ModelSize, ModelSize });

        // ImageNet normalization
        ReadOnlySpan<float> mean = stackalloc float[] { 0.485f, 0.456f, 0.406f };
        ReadOnlySpan<float> std = stackalloc float[] { 0.229f, 0.224f, 0.225f };

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < ModelSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < ModelSize; x++)
                {
                    var px = row[x];
                    tensor[0, 0, y, x] = (px.R / 255f - 0.485f) / 0.229f;
                    tensor[0, 1, y, x] = (px.G / 255f - 0.456f) / 0.224f;
                    tensor[0, 2, y, x] = (px.B / 255f - 0.406f) / 0.225f;
                }
            }
        });

        return tensor;
    }

    /// <summary>Sigmoid → min-max normalize → bilinear resize to target dimensions.</summary>
    private static float[,] PostprocessMask(Tensor<float> output, int targetW, int targetH)
    {
        // Apply sigmoid and find min/max for normalization
        var raw = new float[ModelSize, ModelSize];
        float minVal = float.MaxValue, maxVal = float.MinValue;

        for (int y = 0; y < ModelSize; y++)
        {
            for (int x = 0; x < ModelSize; x++)
            {
                float v = 1f / (1f + MathF.Exp(-output[0, 0, y, x]));
                raw[y, x] = v;
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
            }
        }

        // Min-max normalize to [0, 1]
        float range = maxVal - minVal;
        if (range > 0.001f)
        {
            for (int y = 0; y < ModelSize; y++)
                for (int x = 0; x < ModelSize; x++)
                    raw[y, x] = (raw[y, x] - minVal) / range;
        }

        // Bilinear interpolation to resize mask to original image dimensions
        var mask = new float[targetH, targetW];
        float scaleY = (ModelSize - 1f) / Math.Max(1, targetH - 1);
        float scaleX = (ModelSize - 1f) / Math.Max(1, targetW - 1);

        for (int y = 0; y < targetH; y++)
        {
            float srcY = y * scaleY;
            int y0 = (int)srcY;
            int y1 = Math.Min(y0 + 1, ModelSize - 1);
            float fy = srcY - y0;

            for (int x = 0; x < targetW; x++)
            {
                float srcX = x * scaleX;
                int x0 = (int)srcX;
                int x1 = Math.Min(x0 + 1, ModelSize - 1);
                float fx = srcX - x0;

                mask[y, x] = raw[y0, x0] * (1 - fx) * (1 - fy)
                           + raw[y0, x1] * fx * (1 - fy)
                           + raw[y1, x0] * (1 - fx) * fy
                           + raw[y1, x1] * fx * fy;
            }
        }

        return mask;
    }

    /// <summary>Apply the segmentation mask as alpha channel to the image.</summary>
    private static void ApplyMask(Image<Rgba32> image, float[,] mask)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    byte alpha = (byte)(Math.Clamp(mask[y, x], 0f, 1f) * 255);
                    row[x] = new Rgba32(row[x].R, row[x].G, row[x].B, alpha);
                }
            }
        });
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }
}
