using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Background removal using BRIA RMBG 2.0 ONNX model.
/// State-of-the-art quality, runs locally. Auto-downloads model on first use.
/// Model size: ~176 MB. Apache 2.0 license.
/// </summary>
public sealed class BriaRmbgService : IDisposable
{
    private readonly ILogger<BriaRmbgService> _logger;
    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly string _outputDir;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;

    private const string ModelFileName = "bria_rmbg_2.0.onnx";
    // BRIA RMBG 2.0 ONNX from HuggingFace
    private const string ModelUrl = "https://huggingface.co/briaai/RMBG-2.0/resolve/main/onnx/model.onnx";
    private const int ModelInputSize = 1024; // RMBG 2.0 uses 1024x1024

    public bool IsAvailable => true;

    public BriaRmbgService(
        IWebHostEnvironment env,
        ILogger<BriaRmbgService> logger)
    {
        _logger = logger;
        _modelDir = Path.Combine(AppContext.BaseDirectory, "Models");
        _modelPath = Path.Combine(_modelDir, ModelFileName);
        _outputDir = Path.Combine(env.WebRootPath, "generated");
        Directory.CreateDirectory(_outputDir);
    }

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
                _logger.LogInformation("Downloading BRIA RMBG 2.0 model from HuggingFace (~176MB)...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var bytes = await http.GetByteArrayAsync(ModelUrl);
                await File.WriteAllBytesAsync(_modelPath, bytes);
                _logger.LogInformation("BRIA RMBG 2.0 model downloaded ({Size:N0} bytes)", bytes.Length);
            }

            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                InterOpNumThreads = Environment.ProcessorCount,
                IntraOpNumThreads = Environment.ProcessorCount,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };

            _session = new InferenceSession(_modelPath, opts);
            _logger.LogInformation("BRIA RMBG 2.0 ONNX model loaded");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<byte[]> RemoveBackgroundAsync(byte[] imageBytes)
    {
        await EnsureModelAsync();

        return await Task.Run(() =>
        {
            using var original = Image.Load<Rgba32>(imageBytes);
            int origW = original.Width, origH = original.Height;

            // Resize to model input size
            using var resized = original.Clone(ctx => ctx.Resize(ModelInputSize, ModelInputSize));

            // Preprocess: normalize to [0, 1]
            var inputTensor = new DenseTensor<float>(new[] { 1, 3, ModelInputSize, ModelInputSize });
            resized.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < ModelInputSize; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < ModelInputSize; x++)
                    {
                        inputTensor[0, 0, y, x] = row[x].R / 255f;
                        inputTensor[0, 1, y, x] = row[x].G / 255f;
                        inputTensor[0, 2, y, x] = row[x].B / 255f;
                    }
                }
            });

            // Run inference
            var inputName = _session!.InputMetadata.Keys.First();
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            // Get output dimensions
            var shape = output.Dimensions;
            int outH = shape.Length >= 3 ? shape[^2] : ModelInputSize;
            int outW = shape.Length >= 2 ? shape[^1] : ModelInputSize;

            // Extract mask and resize to original dimensions
            var mask = new float[origH, origW];
            using var maskImg = new Image<L8>(outW, outH);

            // Find min/max for normalization
            float minVal = float.MaxValue, maxVal = float.MinValue;
            for (int i = 0; i < output.Length; i++)
            {
                var v = output.GetValue(i);
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
            }
            float range = maxVal - minVal;
            if (range < 0.001f) range = 1f;

            maskImg.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < outH; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < outW; x++)
                    {
                        float raw = output.GetValue(y * outW + x);
                        float sig = 1f / (1f + MathF.Exp(-raw)); // sigmoid
                        float normalized = (sig - minVal) / range;
                        row[x] = new L8((byte)(Math.Clamp(normalized, 0, 1) * 255));
                    }
                }
            });

            // Resize mask to original dimensions
            using var resizedMask = maskImg.Clone(ctx => ctx.Resize(origW, origH));

            // Apply mask as alpha channel
            original.ProcessPixelRows(resizedMask, (imgAcc, maskAcc) =>
            {
                for (int y = 0; y < origH; y++)
                {
                    var imgRow = imgAcc.GetRowSpan(y);
                    var maskRow = maskAcc.GetRowSpan(y);
                    for (int x = 0; x < origW; x++)
                    {
                        imgRow[x] = new Rgba32(imgRow[x].R, imgRow[x].G, imgRow[x].B, maskRow[x].PackedValue);
                    }
                }
            });

            using var ms = new MemoryStream();
            original.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 });
            return ms.ToArray();
        });
    }

    public async Task<BackgroundRemovalResult> RemoveAndSaveAsync(byte[] imageBytes, string backgroundColor = "white")
    {
        var transparentBytes = await RemoveBackgroundAsync(imageBytes);

        var transFile = $"{Guid.NewGuid():N}_bria_trans.png";
        var transPath = Path.Combine(_outputDir, transFile);
        await File.WriteAllBytesAsync(transPath, transparentBytes);

        // Create colored version
        using var fg = Image.Load<Rgba32>(transparentBytes);
        var bgColor = Rgba32.ParseHex(backgroundColor == "white" ? "#FFFFFF" : backgroundColor);
        using var canvas = new Image<Rgba32>(fg.Width, fg.Height, bgColor);
        canvas.ProcessPixelRows(fg, (bgAcc, fgAcc) =>
        {
            for (int y = 0; y < bgAcc.Height; y++)
            {
                var bgRow = bgAcc.GetRowSpan(y);
                var fgRow = fgAcc.GetRowSpan(y);
                for (int x = 0; x < bgAcc.Width; x++)
                {
                    var px = fgRow[x];
                    if (px.A == 0) continue;
                    float a = px.A / 255f, ia = 1f - a;
                    bgRow[x] = new Rgba32(
                        (byte)(px.R * a + bgRow[x].R * ia),
                        (byte)(px.G * a + bgRow[x].G * ia),
                        (byte)(px.B * a + bgRow[x].B * ia), 255);
                }
            }
        });

        var colorFile = $"{Guid.NewGuid():N}_bria_col.png";
        var colorPath = Path.Combine(_outputDir, colorFile);
        canvas.SaveAsPng(colorPath, new PngEncoder { ColorType = PngColorType.Rgb });

        return new BackgroundRemovalResult($"generated/{colorFile}", $"generated/{transFile}", transparentBytes);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }
}
