using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Local ONNX-based image enhancement using Real-ESRGAN (4x upscale).
/// Uses tiled inference to keep memory usage bounded on CPU.
/// Registered as singleton (model loaded once).
/// </summary>
public sealed class OnnxImageEnhancerService : IImageEnhancerService, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly string _outputDir;
    private readonly string _webRootPath;
    private readonly ILogger<OnnxImageEnhancerService> _logger;

    private const int Scale = 4;
    private const int TileSize = 192;  // Process in 192x192 tiles
    private const int TilePad = 10;    // Overlap padding to avoid seams

    public bool IsAvailable => _session is not null;

    public OnnxImageEnhancerService(
        IWebHostEnvironment env,
        ILogger<OnnxImageEnhancerService> logger)
    {
        _logger = logger;
        _webRootPath = env.WebRootPath;
        _outputDir = Path.Combine(env.WebRootPath, "generated");
        Directory.CreateDirectory(_outputDir);

        var modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "realesrgan-x4plus.onnx");
        if (!File.Exists(modelPath))
        {
            _logger.LogWarning(
                "Real-ESRGAN model not found at {ModelPath}. Local image enhancement is unavailable.",
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
                "Real-ESRGAN model loaded (input={Input}, output={Output}, tile={Tile}x{Tile})",
                _inputName, _outputName, TileSize, TileSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Real-ESRGAN model");
            _session = null;
            _inputName = string.Empty;
            _outputName = string.Empty;
        }
    }

    public async Task<string> EnhanceImageAsync(string sourceImagePath, IProgress<int>? progress = null)
    {
        if (_session is null)
            throw new InvalidOperationException("Real-ESRGAN model is not loaded");

        var fullPath = Path.IsPathRooted(sourceImagePath)
            ? sourceImagePath
            : Path.Combine(_webRootPath, sourceImagePath.Replace("/", Path.DirectorySeparatorChar.ToString()));

        return await Task.Run(() => ProcessImage(fullPath, progress));
    }

    private string ProcessImage(string sourcePath, IProgress<int>? progress = null)
    {
        using var image = Image.Load<Rgb24>(sourcePath);
        var srcW = image.Width;
        var srcH = image.Height;
        var outW = srcW * Scale;
        var outH = srcH * Scale;

        _logger.LogInformation("Enhancing {W}x{H} -> {OW}x{OH} (4x)", srcW, srcH, outW, outH);

        using var output = new Image<Rgb24>(outW, outH);

        // Tile-based inference
        int tilesX = (srcW + TileSize - 1) / TileSize;
        int tilesY = (srcH + TileSize - 1) / TileSize;
        int totalTiles = tilesX * tilesY;
        int processed = 0;

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                // Source tile bounds (without padding)
                int srcX = tx * TileSize;
                int srcY = ty * TileSize;
                int tileW = Math.Min(TileSize, srcW - srcX);
                int tileH = Math.Min(TileSize, srcH - srcY);

                // Padded source bounds (clamped to image)
                int padLeft = Math.Min(TilePad, srcX);
                int padTop = Math.Min(TilePad, srcY);
                int padRight = Math.Min(TilePad, srcW - srcX - tileW);
                int padBottom = Math.Min(TilePad, srcH - srcY - tileH);

                int cropX = srcX - padLeft;
                int cropY = srcY - padTop;
                int cropW = tileW + padLeft + padRight;
                int cropH = tileH + padTop + padBottom;

                // Extract padded tile
                using var tile = image.Clone(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH)));

                // Run inference on this tile
                var inputTensor = ImageToTensor(tile);
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
                };

                using var results = _session!.Run(inputs);
                var outputTensor = results.First().AsTensor<float>();

                // Extract the non-padded region from the upscaled output
                int outPadLeft = padLeft * Scale;
                int outPadTop = padTop * Scale;
                int outTileW = tileW * Scale;
                int outTileH = tileH * Scale;

                // Write to output image
                int dstX = srcX * Scale;
                int dstY = srcY * Scale;

                output.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < outTileH; y++)
                    {
                        var row = accessor.GetRowSpan(dstY + y);
                        for (int x = 0; x < outTileW; x++)
                        {
                            float r = Math.Clamp(outputTensor[0, 0, outPadTop + y, outPadLeft + x], 0f, 1f);
                            float g = Math.Clamp(outputTensor[0, 1, outPadTop + y, outPadLeft + x], 0f, 1f);
                            float b = Math.Clamp(outputTensor[0, 2, outPadTop + y, outPadLeft + x], 0f, 1f);

                            row[dstX + x] = new Rgb24(
                                (byte)(r * 255f + 0.5f),
                                (byte)(g * 255f + 0.5f),
                                (byte)(b * 255f + 0.5f));
                        }
                    }
                });

                processed++;
                progress?.Report(processed * 100 / totalTiles);
                if (processed % 10 == 0 || processed == totalTiles)
                    _logger.LogInformation("Enhanced tile {N}/{Total}", processed, totalTiles);
            }
        }

        var fileName = $"{Guid.NewGuid():N}_enhanced.png";
        var outputPath = Path.Combine(_outputDir, fileName);
        output.SaveAsPng(outputPath);

        _logger.LogInformation("Enhancement complete: {Path}", fileName);
        return $"generated/{fileName}";
    }

    /// <summary>
    /// Converts an ImageSharp tile to NCHW tensor normalized to [0, 1].
    /// </summary>
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
