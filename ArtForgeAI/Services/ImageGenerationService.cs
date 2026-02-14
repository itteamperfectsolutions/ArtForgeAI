using ArtForgeAI.Models;
using Microsoft.Extensions.Options;
using OpenAI.Images;

namespace ArtForgeAI.Services;

public class ImageGenerationService : IImageGenerationService
{
    private readonly ImageClient _imageClient;
    private readonly ImageClient _imageEditClient;
    private readonly IPromptEnhancerService _promptEnhancer;
    private readonly IImageStorageService _imageStorage;
    private readonly IGenerationHistoryService _historyService;
    private readonly IGeminiImageService _geminiImageService;
    private readonly IReplicateImageService _replicateImageService;
    private readonly IBackgroundRemovalService _bgRemoval;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ImageGenerationService> _logger;

    public ImageGenerationService(
        IOptions<OpenAiOptions> options,
        IPromptEnhancerService promptEnhancer,
        IImageStorageService imageStorage,
        IGenerationHistoryService historyService,
        IGeminiImageService geminiImageService,
        IReplicateImageService replicateImageService,
        IBackgroundRemovalService bgRemoval,
        IWebHostEnvironment env,
        ILogger<ImageGenerationService> logger)
    {
        _imageClient = new ImageClient(options.Value.ImageModel, options.Value.ApiKey);
        _imageEditClient = new ImageClient(options.Value.ImageEditModel, options.Value.ApiKey);
        _promptEnhancer = promptEnhancer;
        _imageStorage = imageStorage;
        _historyService = historyService;
        _geminiImageService = geminiImageService;
        _replicateImageService = replicateImageService;
        _bgRemoval = bgRemoval;
        _env = env;
        _logger = logger;
    }

    public async Task<GenerationResult> GenerateImageAsync(GenerationRequest request)
    {
        string finalPrompt = request.Prompt;
        string? enhancedPrompt = null;

        try
        {
            // ── Local ONNX background removal short-circuit ──
            if (request.HasReferenceImages
                && !request.ForceCloudProvider
                && _bgRemoval.IsAvailable
                && _promptEnhancer.IsPureBackgroundRemoval(request.Prompt))
            {
                _logger.LogInformation("Pure background removal detected — using local ONNX processing");

                var bgColor = BackgroundColorParser.ExtractBackgroundColor(request.Prompt);
                var refFullPath = Path.Combine(_env.WebRootPath,
                    request.ReferenceImagePath!.Replace("/", Path.DirectorySeparatorChar.ToString()));

                // Enhance prompt locally (template-based, zero AI cost)
                string? localEnhanced = null;
                if (request.EnhancePrompt)
                {
                    var enhancement = await _promptEnhancer.EnhanceForImageEditAsync(
                        request.Prompt, request.Provider, request.ReferenceImagePaths.Count, request.ReferenceImagePaths);
                    if (enhancement.Success)
                        localEnhanced = enhancement.EnhancedPrompt;
                }

                var bgResult = await _bgRemoval.RemoveBackgroundAsync(refFullPath, bgColor);

                _logger.LogInformation("Local background removal completed (bg={BgColor})", bgColor);

                return new GenerationResult
                {
                    Success = true,
                    LocalImagePath = bgResult.ColoredImagePath,
                    TransparentImagePath = bgResult.TransparentImagePath,
                    TransparentPngBytes = bgResult.TransparentPngBytes,
                    EnhancedPrompt = localEnhanced ?? $"Background removed locally (color: {bgColor})",
                    WasLocalProcessing = true
                };
            }

            _logger.LogInformation("Starting image generation with provider: {Provider}", request.Provider);

            string localPath;
            string? imageUrl = null;

            if (request.Provider == ImageProvider.Gemini)
            {
                (localPath, enhancedPrompt) = await GenerateWithGeminiAsync(request);
            }
            else if (request.Provider == ImageProvider.Replicate)
            {
                (localPath, enhancedPrompt) = await GenerateWithReplicateAsync(request);
            }
            else if (request.HasReferenceImages)
            {
                // Reference image provided → use gpt-image-1 edit (sends actual pixels)
                // with DALL-E 3 + vision analysis as fallback
                (localPath, imageUrl, enhancedPrompt) = await GenerateWithReferenceAsync(request);
            }
            else
            {
                // No reference → standard DALL-E 3 text-to-image
                if (request.EnhancePrompt)
                {
                    var enhancement = await _promptEnhancer.EnhancePromptAsync(request.Prompt, request.Provider);
                    if (enhancement.Success)
                    {
                        finalPrompt = enhancement.EnhancedPrompt;
                        enhancedPrompt = enhancement.EnhancedPrompt;
                    }
                }

                (localPath, imageUrl) = await GenerateTextToImageAsync(finalPrompt, request.Width, request.Height);
            }

            // Save to history (store all reference paths joined by semicolon)
            var refPaths = request.ReferenceImagePaths.Count > 0
                ? string.Join(";", request.ReferenceImagePaths)
                : null;

            var generation = new ImageGeneration
            {
                OriginalPrompt = Truncate(request.Prompt, 2000)!,
                EnhancedPrompt = Truncate(enhancedPrompt, 4000),
                ReferenceImagePath = Truncate(refPaths, 500),
                GeneratedImageUrl = Truncate(imageUrl, 2000),
                LocalImagePath = Truncate(localPath, 500),
                ImageSize = request.SizeName,
                IsSuccess = true
            };

            await _historyService.SaveGenerationAsync(generation);

            return new GenerationResult
            {
                Success = true,
                ImageUrl = imageUrl,
                LocalImagePath = localPath,
                EnhancedPrompt = enhancedPrompt,
                GenerationId = generation.Id
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image generation failed with provider {Provider}", request.Provider);

            var errorMsg = $"[{request.Provider}] {ex.Message}";

            var errRefPaths = request.ReferenceImagePaths.Count > 0
                ? string.Join(";", request.ReferenceImagePaths)
                : null;

            var generation = new ImageGeneration
            {
                OriginalPrompt = Truncate(request.Prompt, 2000)!,
                EnhancedPrompt = Truncate(enhancedPrompt, 4000),
                ReferenceImagePath = Truncate(errRefPaths, 500),
                ImageSize = request.SizeName,
                IsSuccess = false,
                ErrorMessage = Truncate(errorMsg, 1000)
            };
            await _historyService.SaveGenerationAsync(generation);

            return new GenerationResult
            {
                Success = false,
                ErrorMessage = errorMsg
            };
        }
    }

    /// <summary>
    /// Handles generation via Google Gemini.
    /// Supports both text-to-image and image-to-image (with reference).
    /// </summary>
    private async Task<(string localPath, string? enhancedPrompt)> GenerateWithGeminiAsync(GenerationRequest request)
    {
        var finalPrompt = request.Prompt;
        string? enhancedPrompt = null;

        if (request.EnhancePrompt)
        {
            var enhancement = request.HasReferenceImages
                ? await _promptEnhancer.EnhanceForImageEditAsync(
                    request.Prompt, request.Provider, request.ReferenceImagePaths.Count, request.ReferenceImagePaths)
                : await _promptEnhancer.EnhancePromptAsync(request.Prompt, request.Provider);

            if (enhancement.Success)
            {
                finalPrompt = enhancement.EnhancedPrompt;
                enhancedPrompt = enhancement.EnhancedPrompt;
            }
        }
        else if (request.HasReferenceImages && request.ReferenceImagePaths.Count > 1)
        {
            // Even without AI enhancement, prepend multi-image composition instructions
            finalPrompt = $"Combine the subjects from all {request.ReferenceImagePaths.Count} provided reference images into a single scene. "
                        + $"Every subject from every image MUST appear. Preserve exact appearance. "
                        + finalPrompt;
        }

        // Pre-load reference images (used for both attempts)
        List<(byte[] data, string mimeType)>? images = null;
        if (request.HasReferenceImages)
        {
            images = new List<(byte[] data, string mimeType)>();
            foreach (var refPath in request.ReferenceImagePaths)
            {
                var fullPath = Path.Combine(_env.WebRootPath,
                    refPath.Replace("/", Path.DirectorySeparatorChar.ToString()));

                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("Reference image not found.", fullPath);

                var refBytes = await File.ReadAllBytesAsync(fullPath);
                var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                var mimeType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/png"
                };
                images.Add((refBytes, mimeType));
            }
        }

        byte[] imageBytes;

        try
        {
            if (images is not null)
            {
                _logger.LogInformation("Sending image-to-image request to Gemini with {Count} reference images", images.Count);
                (_, imageBytes) = await _geminiImageService.EditImageAsync(finalPrompt, images, request.Width, request.Height);
            }
            else
            {
                _logger.LogInformation("Sending text-to-image request to Gemini");
                (_, imageBytes) = await _geminiImageService.GenerateImageAsync(finalPrompt, request.Width, request.Height);
            }
        }
        catch (InvalidOperationException ex) when (request.EnhancePrompt && finalPrompt != request.Prompt)
        {
            // Enhanced prompt failed — retry with original user prompt
            _logger.LogWarning(ex, "Gemini failed with enhanced prompt, retrying with original prompt");

            if (images is not null)
            {
                (_, imageBytes) = await _geminiImageService.EditImageAsync(request.Prompt, images, request.Width, request.Height);
            }
            else
            {
                (_, imageBytes) = await _geminiImageService.GenerateImageAsync(request.Prompt, request.Width, request.Height);
            }

            enhancedPrompt = $"[Retry with original] {request.Prompt}";
        }

        var fileName = $"{Guid.NewGuid():N}.png";
        var localPath = await _imageStorage.SaveImageFromBytesAsync(BinaryData.FromBytes(imageBytes), fileName);

        return (localPath, enhancedPrompt);
    }

    /// <summary>
    /// Handles generation via Replicate (FLUX 1.1 Pro).
    /// Supports text-to-image and image-guided generation via image_prompt.
    /// </summary>
    private async Task<(string localPath, string? enhancedPrompt)> GenerateWithReplicateAsync(GenerationRequest request)
    {
        var finalPrompt = request.Prompt;
        string? enhancedPrompt = null;

        if (request.EnhancePrompt)
        {
            var enhancement = request.HasReferenceImages
                ? await _promptEnhancer.EnhanceForImageEditAsync(
                    request.Prompt, request.Provider, request.ReferenceImagePaths.Count, request.ReferenceImagePaths)
                : await _promptEnhancer.EnhancePromptAsync(request.Prompt, request.Provider);

            if (enhancement.Success)
            {
                finalPrompt = enhancement.EnhancedPrompt;
                enhancedPrompt = enhancement.EnhancedPrompt;
            }
        }

        byte[] imageBytes;

        // Replicate Kontext only supports a single input image — use the first reference
        if (request.HasReferenceImages)
        {
            var fullPath = Path.Combine(_env.WebRootPath,
                request.ReferenceImagePath!.Replace("/", Path.DirectorySeparatorChar.ToString()));

            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Reference image not found.", fullPath);

            var refBytes = await File.ReadAllBytesAsync(fullPath);
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            var mimeType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/png"
            };

            _logger.LogInformation("Sending image-guided request to Replicate (first of {Count} reference images)", request.ReferenceImagePaths.Count);
            imageBytes = await _replicateImageService.EditImageAsync(finalPrompt, refBytes, mimeType, request.Width, request.Height);
        }
        else
        {
            _logger.LogInformation("Sending text-to-image request to Replicate");
            imageBytes = await _replicateImageService.GenerateImageAsync(finalPrompt, request.Width, request.Height);
        }

        var fileName = $"{Guid.NewGuid():N}.png";
        var localPath = await _imageStorage.SaveImageFromBytesAsync(BinaryData.FromBytes(imageBytes), fileName);

        return (localPath, enhancedPrompt);
    }

    /// <summary>
    /// Handles generation with a reference image.
    /// Primary: gpt-image-1 edit endpoint (sends actual image pixels for identity preservation).
    /// On content policy error: retries with simplified prompt.
    /// Fallback: DALL-E 3 with GPT-4o vision analysis.
    /// </summary>
    private async Task<(string localPath, string? imageUrl, string? enhancedPrompt)> GenerateWithReferenceAsync(
        GenerationRequest request)
    {
        // Step 1: Enhance prompt for image edit mode (scene-focused, no person descriptions)
        var editPrompt = request.Prompt;
        string? enhancedPrompt = null;

        if (request.EnhancePrompt)
        {
            var enhancement = await _promptEnhancer.EnhanceForImageEditAsync(
                request.Prompt, request.Provider, request.ReferenceImagePaths.Count, request.ReferenceImagePaths);
            if (enhancement.Success)
            {
                editPrompt = enhancement.EnhancedPrompt;
                enhancedPrompt = enhancement.EnhancedPrompt;
            }
        }

        // Step 2: Try gpt-image-1 edit with enhanced prompt
        try
        {
            _logger.LogInformation("Attempting gpt-image-1 edit with enhanced prompt");
            var localPath = await GenerateWithEditAsync(editPrompt, request.ReferenceImagePath!, request.Width, request.Height);
            return (localPath, null, enhancedPrompt);
        }
        catch (Exception ex) when (ex.Message.Contains("content_policy", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Content policy triggered with enhanced prompt, retrying with simplified prompt");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gpt-image-1 edit failed with enhanced prompt");
        }

        // Step 3: Retry gpt-image-1 with a minimal, safe prompt (just the scene description)
        try
        {
            var safePrompt = StripIdentityLanguage(request.Prompt);
            _logger.LogInformation("Retrying gpt-image-1 edit with simplified prompt");
            var localPath = await GenerateWithEditAsync(safePrompt, request.ReferenceImagePath!, request.Width, request.Height);
            enhancedPrompt = safePrompt;
            return (localPath, null, enhancedPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gpt-image-1 edit retry failed, falling back to DALL-E 3");
        }

        // Step 4: Final fallback — DALL-E 3 with GPT-4o vision analysis
        if (request.EnhancePrompt)
        {
            var fallbackEnhancement = await _promptEnhancer.EnhancePromptAsync(
                request.Prompt, request.Provider, request.ReferenceImagePath);
            if (fallbackEnhancement.Success)
            {
                editPrompt = fallbackEnhancement.EnhancedPrompt;
                enhancedPrompt = fallbackEnhancement.EnhancedPrompt;
            }
        }

        var (fallbackPath, fallbackUrl) = await GenerateTextToImageAsync(editPrompt, request.Width, request.Height);
        return (fallbackPath, fallbackUrl, enhancedPrompt);
    }

    /// <summary>
    /// Strips identity/appearance language that may trigger content policy.
    /// Keeps only scene/action/setting words.
    /// </summary>
    private static string StripIdentityLanguage(string prompt)
    {
        // Remove common identity-triggering phrases
        var stripped = prompt;
        string[] patternsToRemove = [
            "don't change the face",
            "dont change the face",
            "keep the face",
            "preserve the face",
            "same face",
            "same person",
            "exact face",
            "facial features",
            "face features",
            "don't change",
            "dont change",
            "preserve",
            "maintain",
            "keep the same"
        ];

        foreach (var pattern in patternsToRemove)
        {
            stripped = stripped.Replace(pattern, "", StringComparison.OrdinalIgnoreCase);
        }

        // Clean up extra spaces/punctuation
        stripped = string.Join(' ', stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        stripped = stripped.Trim('.', ',', ' ');

        return string.IsNullOrWhiteSpace(stripped)
            ? "Transform this image into an artistic scene, high quality, photorealistic"
            : stripped;
    }

    /// <summary>
    /// gpt-image-1 edit endpoint — sends the actual reference image pixels to the model.
    /// The model "sees" the real image and can preserve identity, facial features, etc.
    /// </summary>
    private async Task<string> GenerateWithEditAsync(string prompt, string referenceImagePath, int width, int height)
    {
        var fullPath = Path.Combine(_env.WebRootPath,
            referenceImagePath.Replace("/", Path.DirectorySeparatorChar.ToString()));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Reference image not found.", fullPath);

#pragma warning disable OPENAI001
        var imageSize = MapToEditSize(width, height);
#pragma warning restore OPENAI001

        var options = new ImageEditOptions
        {
            Size = imageSize,
            ResponseFormat = GeneratedImageFormat.Bytes
        };

        using var imageStream = File.OpenRead(fullPath);
        var imageFileName = Path.GetFileName(fullPath);

        var result = await _imageEditClient.GenerateImageEditAsync(
            imageStream, prompt, imageFileName, options);

        var fileName = $"{Guid.NewGuid():N}.png";
        var localPath = await _imageStorage.SaveImageFromBytesAsync(result.Value.ImageBytes, fileName);

        return localPath;
    }

    /// <summary>
    /// DALL-E 3 text-to-image generation.
    /// </summary>
    private async Task<(string localPath, string imageUrl)> GenerateTextToImageAsync(string prompt, int width, int height)
    {
        var imageSize = MapToGenerateSize(width, height);

        var options = new ImageGenerationOptions
        {
            Size = imageSize,
            Quality = GeneratedImageQuality.High,
            ResponseFormat = GeneratedImageFormat.Uri
        };

        var image = await _imageClient.GenerateImageAsync(prompt, options);
        var imageUrl = image.Value.ImageUri.ToString();

        var fileName = $"{Guid.NewGuid():N}.png";
        var localPath = await _imageStorage.SaveImageFromUrlAsync(imageUrl, fileName);

        return (localPath, imageUrl);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;

    /// <summary>Map width/height to the closest DALL-E 3 text-to-image size.</summary>
    private static GeneratedImageSize MapToGenerateSize(int width, int height)
    {
        var ratio = (double)width / height;
        if (ratio > 1.2) return GeneratedImageSize.W1792xH1024;   // landscape
        if (ratio < 0.8) return GeneratedImageSize.W1024xH1792;   // portrait
        return GeneratedImageSize.W1024xH1024;                     // square
    }

    /// <summary>Map width/height to the closest gpt-image-1 edit size.</summary>
    private static GeneratedImageSize MapToEditSize(int width, int height)
    {
        var ratio = (double)width / height;
#pragma warning disable OPENAI001
        if (ratio > 1.2) return GeneratedImageSize.W1536xH1024;   // landscape
        if (ratio < 0.8) return GeneratedImageSize.W1024xH1536;   // portrait
#pragma warning restore OPENAI001
        return GeneratedImageSize.W1024xH1024;                     // square
    }
}
