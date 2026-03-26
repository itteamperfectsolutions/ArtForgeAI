using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Background removal using IS-Net (DIS — Dichotomous Image Segmentation).
/// Excellent for fine details like hair, fur, lace. Auto-downloads ONNX model on first use.
/// Model size: ~176 MB. Apache 2.0 license.
/// </summary>
public sealed class IsNetBgService : IDisposable
{
    private readonly ILogger<IsNetBgService> _logger;
    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly string _outputDir;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;

    private const string ModelFileName = "isnet-general-use.onnx";
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx";
    private const int ModelInputSize = 1024;

    // ImageNet normalization stats
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std  = { 0.229f, 0.224f, 0.225f };

    public bool IsAvailable => true;

    public IsNetBgService(
        IWebHostEnvironment env,
        ILogger<IsNetBgService> logger)
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
                _logger.LogInformation("Downloading IS-Net model (~176MB)...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var bytes = await http.GetByteArrayAsync(ModelUrl);
                await File.WriteAllBytesAsync(_modelPath, bytes);
                _logger.LogInformation("IS-Net model downloaded ({Size:N0} bytes)", bytes.Length);
            }

            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                InterOpNumThreads = Environment.ProcessorCount,
                IntraOpNumThreads = Environment.ProcessorCount,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };

            _session = new InferenceSession(_modelPath, opts);
            _logger.LogInformation("IS-Net ONNX model loaded");
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

            using var resized = original.Clone(ctx => ctx.Resize(ModelInputSize, ModelInputSize));

            // Preprocess with ImageNet normalization
            var inputTensor = new DenseTensor<float>(new[] { 1, 3, ModelInputSize, ModelInputSize });
            resized.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < ModelInputSize; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < ModelInputSize; x++)
                    {
                        inputTensor[0, 0, y, x] = (row[x].R / 255f - Mean[0]) / Std[0];
                        inputTensor[0, 1, y, x] = (row[x].G / 255f - Mean[1]) / Std[1];
                        inputTensor[0, 2, y, x] = (row[x].B / 255f - Mean[2]) / Std[2];
                    }
                }
            });

            var inputName = _session!.InputMetadata.Keys.First();
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
            using var results = _session.Run(inputs);

            // IS-Net outputs multiple maps — first is the finest
            var output = results.First().AsTensor<float>();
            var shape = output.Dimensions;
            int outH = shape[^2], outW = shape[^1];

            // Sigmoid + normalize mask
            using var maskImg = new Image<L8>(outW, outH);
            float minVal = float.MaxValue, maxVal = float.MinValue;

            // First pass: find range
            for (int y = 0; y < outH; y++)
                for (int x = 0; x < outW; x++)
                {
                    float v = 1f / (1f + MathF.Exp(-output[0, 0, y, x]));
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
                        float v = 1f / (1f + MathF.Exp(-output[0, 0, y, x]));
                        float norm = (v - minVal) / range;
                        row[x] = new L8((byte)(Math.Clamp(norm, 0, 1) * 255));
                    }
                }
            });

            using var resizedMask = maskImg.Clone(ctx => ctx.Resize(origW, origH));

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

        var transFile = $"{Guid.NewGuid():N}_isnet_trans.png";
        var transPath = Path.Combine(_outputDir, transFile);
        await File.WriteAllBytesAsync(transPath, transparentBytes);

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

        var colorFile = $"{Guid.NewGuid():N}_isnet_col.png";
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
