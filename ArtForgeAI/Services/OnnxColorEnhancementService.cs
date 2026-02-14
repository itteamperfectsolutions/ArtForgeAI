using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ArtForgeAI.Services;

/// <summary>
/// Local ONNX-based color/light enhancement using SCI (Self-Calibrated Illumination).
/// Improves brightness, contrast, and color vibrancy for underexposed images.
/// Registered as singleton (model loaded once).
/// </summary>
public sealed class OnnxColorEnhancementService : IColorEnhancementService, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly string _outputDir;
    private readonly string _webRootPath;
    private readonly ILogger<OnnxColorEnhancementService> _logger;

    public bool IsAvailable => _session is not null;

    public OnnxColorEnhancementService(
        IWebHostEnvironment env,
        ILogger<OnnxColorEnhancementService> logger)
    {
        _logger = logger;
        _webRootPath = env.WebRootPath;
        _outputDir = Path.Combine(env.WebRootPath, "generated");
        Directory.CreateDirectory(_outputDir);

        var modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "sci-color-enhance.onnx");
        if (!File.Exists(modelPath))
        {
            _logger.LogWarning(
                "SCI color enhancement model not found at {ModelPath}. Color enhancement is unavailable.",
                modelPath);
            _inputName = string.Empty;
            _outputName = string.Empty;
            return;
        }

        try
        {
            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                InterOpNumThreads = 4,
                IntraOpNumThreads = 4,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };

            _session = new InferenceSession(modelPath, opts);
            _inputName = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();

            _logger.LogInformation(
                "SCI color enhancement model loaded (input={Input}, output={Output})",
                _inputName, _outputName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load SCI color enhancement model");
            _session = null;
            _inputName = string.Empty;
            _outputName = string.Empty;
        }
    }

    public async Task<string> EnhanceColorsAsync(string sourceImagePath)
    {
        if (_session is null)
            throw new InvalidOperationException("SCI color enhancement model is not loaded");

        var fullPath = Path.IsPathRooted(sourceImagePath)
            ? sourceImagePath
            : Path.Combine(_webRootPath, sourceImagePath.Replace("/", Path.DirectorySeparatorChar.ToString()));

        return await Task.Run(() => ProcessImage(fullPath));
    }

    private string ProcessImage(string sourcePath)
    {
        using var image = Image.Load<Rgb24>(sourcePath);
        var w = image.Width;
        var h = image.Height;

        _logger.LogInformation("Color-enhancing {W}x{H} image", w, h);

        // Convert image to NCHW tensor normalized to [0, 1]
        var inputTensor = ImageToTensor(image);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
        };

        using var results = _session!.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        // Convert output tensor back to image
        using var output = new Image<Rgb24>(w, h);
        output.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    float r = Math.Clamp(outputTensor[0, 0, y, x], 0f, 1f);
                    float g = Math.Clamp(outputTensor[0, 1, y, x], 0f, 1f);
                    float b = Math.Clamp(outputTensor[0, 2, y, x], 0f, 1f);

                    row[x] = new Rgb24(
                        (byte)(r * 255f + 0.5f),
                        (byte)(g * 255f + 0.5f),
                        (byte)(b * 255f + 0.5f));
                }
            }
        });

        var fileName = $"{Guid.NewGuid():N}_colorenhanced.png";
        var outputPath = Path.Combine(_outputDir, fileName);
        output.SaveAsPng(outputPath);

        _logger.LogInformation("Color enhancement complete: {Path}", fileName);
        return $"generated/{fileName}";
    }

    private static DenseTensor<float> ImageToTensor(Image<Rgb24> image)
    {
        int w = image.Width;
        int h = image.Height;
        var tensor = new DenseTensor<float>([1, 3, h, w]);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = pixel.R / 255f;
                    tensor[0, 1, y, x] = pixel.G / 255f;
                    tensor[0, 2, y, x] = pixel.B / 255f;
                }
            }
        });

        return tensor;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
