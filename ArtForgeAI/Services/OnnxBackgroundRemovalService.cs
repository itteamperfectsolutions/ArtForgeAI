using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Local ONNX-based background removal using RMBG-2.0 (BiRefNet).
/// Runs on CPU at zero API cost. Registered as singleton (model loaded once).
/// </summary>
public sealed class OnnxBackgroundRemovalService : IBackgroundRemovalService, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly string _outputDir;
    private readonly string _webRootPath;
    private readonly ILogger<OnnxBackgroundRemovalService> _logger;

    private const int ModelSize = 1024;

    public bool IsAvailable => _session is not null;

    public OnnxBackgroundRemovalService(
        IWebHostEnvironment env,
        ILogger<OnnxBackgroundRemovalService> logger)
    {
        _logger = logger;
        _webRootPath = env.WebRootPath;
        _outputDir = Path.Combine(env.WebRootPath, "generated");
        Directory.CreateDirectory(_outputDir);

        var modelsDir = Path.Combine(AppContext.BaseDirectory, "Models");
        var modelPath = Path.Combine(modelsDir, "rmbg-2.0.onnx");

        if (!File.Exists(modelPath))
        {
            _logger.LogWarning(
                "RMBG-2.0 model not found at {Path}. Local background removal is unavailable. " +
                "Place rmbg-2.0.onnx in ArtForgeAI/Models/", modelPath);
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

            // Log all available outputs for diagnostics
            var outputKeys = _session.OutputMetadata.Keys.ToList();
            foreach (var key in outputKeys)
            {
                var meta = _session.OutputMetadata[key];
                _logger.LogInformation("  RMBG-2.0 output '{Name}': dims=[{Dims}]",
                    key, string.Join(", ", meta.Dimensions));
            }

            // RMBG-2.0 (BiRefNet) ONNX output selection:
            // BiRefNet produces multiple decoder outputs at different refinement levels.
            // The LAST output is the most refined mask (d0 in PyTorch = final prediction).
            _outputName = outputKeys.Last();

            _logger.LogInformation(
                "RMBG-2.0 loaded successfully (input={Input}, selectedOutput={Output}, totalOutputs={Count})",
                _inputName, _outputName, outputKeys.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load RMBG-2.0 from {Path}", modelPath);
            _session = null;
            _inputName = string.Empty;
            _outputName = string.Empty;
        }
    }

    public async Task<BackgroundRemovalResult> RemoveBackgroundAsync(string sourceImagePath, string backgroundColor = "white")
    {
        if (_session is null)
            throw new InvalidOperationException("RMBG-2.0 model is not loaded");

        return await Task.Run(() => ProcessImage(sourceImagePath, backgroundColor));
    }

    public async Task<string> RecolorBackgroundAsync(string transparentImagePath, string backgroundColor)
    {
        return await Task.Run(() =>
        {
            var fullPath = ResolveFullPath(transparentImagePath);
            using var fg = Image.Load<Rgba32>(fullPath);
            var bgColor = BackgroundColorParser.ParseBackgroundColor(backgroundColor);

            if (bgColor is not null)
            {
                var bg = bgColor.Value;
                fg.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < accessor.Width; x++)
                        {
                            var pixel = row[x];
                            float alpha = pixel.A / 255f;
                            row[x] = new Rgba32(
                                (byte)(pixel.R * alpha + bg.R * (1f - alpha)),
                                (byte)(pixel.G * alpha + bg.G * (1f - alpha)),
                                (byte)(pixel.B * alpha + bg.B * (1f - alpha)),
                                255);
                        }
                    }
                });
            }

            var fileName = $"{Guid.NewGuid():N}.png";
            var outputPath = Path.Combine(_outputDir, fileName);
            fg.SaveAsPng(outputPath);
            return $"generated/{fileName}";
        });
    }

    public async Task<string> RecolorBackgroundFromBytesAsync(byte[] transparentPngBytes, string backgroundColor)
    {
        return await Task.Run(() =>
        {
            using var fg = Image.Load<Rgba32>(transparentPngBytes);
            var bgColor = BackgroundColorParser.ParseBackgroundColor(backgroundColor);

            if (bgColor is not null)
            {
                var bg = bgColor.Value;
                fg.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < accessor.Width; x++)
                        {
                            var pixel = row[x];
                            float alpha = pixel.A / 255f;
                            row[x] = new Rgba32(
                                (byte)(pixel.R * alpha + bg.R * (1f - alpha)),
                                (byte)(pixel.G * alpha + bg.G * (1f - alpha)),
                                (byte)(pixel.B * alpha + bg.B * (1f - alpha)),
                                255);
                        }
                    }
                });
            }

            var fileName = $"{Guid.NewGuid():N}.png";
            var outputPath = Path.Combine(_outputDir, fileName);
            fg.SaveAsPng(outputPath);
            return $"generated/{fileName}";
        });
    }

    public async Task<string> CompositeOverImageAsync(string transparentImagePath, string backgroundImagePath)
    {
        return await Task.Run(() =>
        {
            var fgFullPath = ResolveFullPath(transparentImagePath);
            var bgFullPath = ResolveFullPath(backgroundImagePath);

            using var fg = Image.Load<Rgba32>(fgFullPath);
            using var bg = Image.Load<Rgba32>(bgFullPath);

            bg.Mutate(ctx => ctx
                .Resize(fg.Width, fg.Height)
                .DrawImage(fg, 1f));

            var fileName = $"{Guid.NewGuid():N}.png";
            var outputPath = Path.Combine(_outputDir, fileName);
            bg.SaveAsPng(outputPath);
            return $"generated/{fileName}";
        });
    }

    public async Task<string> CompositeOverImageFromBytesAsync(byte[] transparentPngBytes, string backgroundImagePath)
    {
        return await Task.Run(() =>
        {
            var bgFullPath = ResolveFullPath(backgroundImagePath);

            using var fg = Image.Load<Rgba32>(transparentPngBytes);
            using var bg = Image.Load<Rgba32>(bgFullPath);

            bg.Mutate(ctx => ctx
                .Resize(fg.Width, fg.Height)
                .DrawImage(fg, 1f));

            var fileName = $"{Guid.NewGuid():N}.png";
            var outputPath = Path.Combine(_outputDir, fileName);
            bg.SaveAsPng(outputPath);
            return $"generated/{fileName}";
        });
    }

    private string ResolveFullPath(string webRelativePath)
    {
        return Path.Combine(_webRootPath, webRelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private BackgroundRemovalResult ProcessImage(string sourceImagePath, string backgroundColor)
    {
        using var image = Image.Load<Rgba32>(sourceImagePath);
        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // Preprocess: resize to 1024x1024 and build NCHW tensor
        using var resized = image.Clone(ctx => ctx.Resize(ModelSize, ModelSize));
        var inputTensor = ImageToTensor(resized);

        // Run inference – request ALL outputs so we can pick the best mask
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
        };

        using var results = _session!.Run(inputs);

        // Evaluate each output to find the one with the best foreground/background separation.
        // The most refined mask will have values closest to 0 and 1 (widest effective range).
        Tensor<float>? bestTensor = null;
        string bestName = _outputName;
        float bestRange = -1f;

        foreach (var result in results)
        {
            var tensor = result.AsTensor<float>();
            if (tensor.Dimensions.Length != 4 || tensor.Dimensions[1] != 1)
                continue; // skip non-mask outputs

            // Sample to find min/max
            float mn = float.MaxValue, mx = float.MinValue;
            int h = tensor.Dimensions[2], w = tensor.Dimensions[3];
            for (int y = 0; y < h; y += 8)
            {
                for (int x = 0; x < w; x += 8)
                {
                    float v = tensor[0, 0, y, x];
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                }
            }

            float range = mx - mn;
            _logger.LogInformation("  Output '{Name}' dims=[{D0},{D1},{D2},{D3}] range=[{Min:F3}..{Max:F3}]",
                result.Name, tensor.Dimensions[0], tensor.Dimensions[1], tensor.Dimensions[2], tensor.Dimensions[3], mn, mx);

            if (range > bestRange)
            {
                bestRange = range;
                bestTensor = tensor;
                bestName = result.Name;
            }
        }

        if (bestTensor is null)
        {
            // Fallback to configured output
            bestTensor = results.Where(r => r.Name == _outputName).First().AsTensor<float>();
            bestName = _outputName;
        }

        var outputTensor = bestTensor;
        _logger.LogInformation("RMBG-2.0 selected output '{Name}' dims=[{Dims}]",
            bestName, string.Join(", ", Enumerable.Range(0, outputTensor.Dimensions.Length).Select(i => outputTensor.Dimensions[i])));

        // Postprocess: resize mask to original dimensions via bilinear interpolation
        var mask = ResizeMask(outputTensor, originalWidth, originalHeight);

        // Save transparent version first (always useful for reprocessing)
        var transparentImage = image.Clone();
        ApplyMask(transparentImage, mask, null);
        var transparentFileName = $"{Guid.NewGuid():N}_transparent.png";
        var transparentPath = Path.Combine(_outputDir, transparentFileName);

        var rgbaEncoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };
        byte[] transparentBytes;
        using (var ms = new MemoryStream())
        {
            transparentImage.SaveAsPng(ms, rgbaEncoder);
            transparentBytes = ms.ToArray();
        }
        File.WriteAllBytes(transparentPath, transparentBytes);
        transparentImage.Dispose();

        // Apply mask with requested background color
        var bgColor = BackgroundColorParser.ParseBackgroundColor(backgroundColor);
        ApplyMask(image, mask, bgColor);

        // Save colored result
        var fileName = $"{Guid.NewGuid():N}.png";
        var outputPath = Path.Combine(_outputDir, fileName);
        image.SaveAsPng(outputPath);

        return new BackgroundRemovalResult($"generated/{fileName}", $"generated/{transparentFileName}", transparentBytes);
    }

    /// <summary>
    /// Converts an ImageSharp image to a DenseTensor in NCHW format.
    /// Uses ImageNet normalization: (pixel/255 - mean) / std
    /// </summary>
    private static DenseTensor<float> ImageToTensor(Image<Rgba32> image)
    {
        var tensor = new DenseTensor<float>([1, 3, ModelSize, ModelSize]);

        const float meanR = 0.485f, meanG = 0.456f, meanB = 0.406f;
        const float stdR = 0.229f, stdG = 0.224f, stdB = 0.225f;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < ModelSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < ModelSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = (pixel.R / 255f - meanR) / stdR;
                    tensor[0, 1, y, x] = (pixel.G / 255f - meanG) / stdG;
                    tensor[0, 2, y, x] = (pixel.B / 255f - meanB) / stdB;
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// Bilinear interpolation to resize mask from model output to original image dimensions.
    /// Auto-detects whether sigmoid activation is needed based on value range.
    /// </summary>
    private float[,] ResizeMask(Tensor<float> outputTensor, int targetWidth, int targetHeight)
    {
        var srcH = outputTensor.Dimensions[2];
        var srcW = outputTensor.Dimensions[3];

        // Sample min/max to decide if sigmoid is needed
        float minVal = float.MaxValue, maxVal = float.MinValue;
        for (int y = 0; y < srcH; y += 4)
        {
            for (int x = 0; x < srcW; x += 4)
            {
                float v = outputTensor[0, 0, y, x];
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
            }
        }

        // If values extend outside [0,1], they are raw logits → apply sigmoid
        // If already in [0,1], no sigmoid needed (already activated)
        bool needsSigmoid = minVal < -0.5f || maxVal > 1.5f;
        _logger.LogInformation(
            "RMBG-2.0 mask range: min={Min:F3}, max={Max:F3}, sigmoid={Sigmoid}",
            minVal, maxVal, needsSigmoid);

        var mask = new float[targetHeight, targetWidth];

        for (int y = 0; y < targetHeight; y++)
        {
            float srcY = (float)y / targetHeight * srcH;
            int y0 = Math.Min((int)srcY, srcH - 1);
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float fy = srcY - y0;

            for (int x = 0; x < targetWidth; x++)
            {
                float srcX = (float)x / targetWidth * srcW;
                int x0 = Math.Min((int)srcX, srcW - 1);
                int x1 = Math.Min(x0 + 1, srcW - 1);
                float fx = srcX - x0;

                // Bilinear interpolation
                float v00 = outputTensor[0, 0, y0, x0];
                float v01 = outputTensor[0, 0, y0, x1];
                float v10 = outputTensor[0, 0, y1, x0];
                float v11 = outputTensor[0, 0, y1, x1];

                float value = v00 * (1 - fx) * (1 - fy)
                            + v01 * fx * (1 - fy)
                            + v10 * (1 - fx) * fy
                            + v11 * fx * fy;

                if (needsSigmoid)
                    value = 1f / (1f + MathF.Exp(-value));

                mask[y, x] = Math.Clamp(value, 0f, 1f);
            }
        }

        return mask;
    }

    /// <summary>
    /// Applies the foreground mask to the image. Alpha-blends foreground over the target
    /// background color. If bgColor is null, produces a transparent background.
    /// </summary>
    private static void ApplyMask(Image<Rgba32> image, float[,] mask, Rgba32? bgColor)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    float alpha = mask[y, x];

                    if (bgColor is null)
                    {
                        row[x] = new Rgba32(row[x].R, row[x].G, row[x].B, (byte)(alpha * 255));
                    }
                    else
                    {
                        var bg = bgColor.Value;
                        var fg = row[x];
                        row[x] = new Rgba32(
                            (byte)(fg.R * alpha + bg.R * (1 - alpha)),
                            (byte)(fg.G * alpha + bg.G * (1 - alpha)),
                            (byte)(fg.B * alpha + bg.B * (1 - alpha)),
                            255);
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
