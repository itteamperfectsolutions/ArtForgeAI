using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Background removal using BiRefNet (Bilateral Reference Network).
/// Current state-of-the-art for salient object detection.
/// Auto-downloads ONNX model on first use. Model size: ~228 MB.
/// </summary>
public sealed class BiRefNetBgService : IDisposable
{
    private readonly ILogger<BiRefNetBgService> _logger;
    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly string _outputDir;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;

    private const string ModelFileName = "birefnet-general.onnx";
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/BiRefNet-general-epoch_244.onnx";
    private const int ModelInputSize = 1024;

    // ImageNet normalization
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std  = { 0.229f, 0.224f, 0.225f };

    public bool IsAvailable => true;

    public BiRefNetBgService(
        IWebHostEnvironment env,
        ILogger<BiRefNetBgService> logger)
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
                _logger.LogInformation("Downloading BiRefNet model (~228MB)...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var bytes = await http.GetByteArrayAsync(ModelUrl);
                await File.WriteAllBytesAsync(_modelPath, bytes);
                _logger.LogInformation("BiRefNet model downloaded ({Size:N0} bytes)", bytes.Length);
            }

            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                InterOpNumThreads = Environment.ProcessorCount,
                IntraOpNumThreads = Environment.ProcessorCount,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };

            _session = new InferenceSession(_modelPath, opts);
            _logger.LogInformation("BiRefNet ONNX model loaded");
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

            // BiRefNet outputs the final mask
            var output = results.Last().AsTensor<float>(); // last output is the refined mask
            var shape = output.Dimensions;
            int outH = shape[^2], outW = shape[^1];

            using var maskImg = new Image<L8>(outW, outH);

            maskImg.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < outH; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < outW; x++)
                    {
                        float raw = output.GetValue(y * outW + x);
                        float sig = 1f / (1f + MathF.Exp(-raw));
                        row[x] = new L8((byte)(Math.Clamp(sig, 0, 1) * 255));
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

        var transFile = $"{Guid.NewGuid():N}_biref_trans.png";
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

        var colorFile = $"{Guid.NewGuid():N}_biref_col.png";
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
