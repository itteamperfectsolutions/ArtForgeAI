using ArtForgeAI.Models;
using Microsoft.Extensions.Options;
using OpenAI.Images;

namespace ArtForgeAI.Services;

public class ImageGenerationService : IImageGenerationService
{
    private readonly ImageClient _imageClient;
    private readonly IPromptEnhancerService _promptEnhancer;
    private readonly IImageStorageService _imageStorage;
    private readonly IGenerationHistoryService _historyService;
    private readonly ILogger<ImageGenerationService> _logger;

    public ImageGenerationService(
        IOptions<OpenAiOptions> options,
        IPromptEnhancerService promptEnhancer,
        IImageStorageService imageStorage,
        IGenerationHistoryService historyService,
        ILogger<ImageGenerationService> logger)
    {
        _imageClient = new ImageClient(options.Value.ImageModel, options.Value.ApiKey);
        _promptEnhancer = promptEnhancer;
        _imageStorage = imageStorage;
        _historyService = historyService;
        _logger = logger;
    }

    public async Task<GenerationResult> GenerateImageAsync(GenerationRequest request)
    {
        string finalPrompt = request.Prompt;
        string? enhancedPrompt = null;

        try
        {
            // Step 1: Enhance prompt if requested
            if (request.EnhancePrompt)
            {
                var enhancement = await _promptEnhancer.EnhancePromptAsync(
                    request.Prompt, request.ReferenceImagePath);

                if (enhancement.Success)
                {
                    finalPrompt = enhancement.EnhancedPrompt;
                    enhancedPrompt = enhancement.EnhancedPrompt;
                }
            }

            // Step 2: Map size
            var imageSize = request.Size switch
            {
                ImageSize.Landscape => GeneratedImageSize.W1792xH1024,
                ImageSize.Portrait => GeneratedImageSize.W1024xH1792,
                _ => GeneratedImageSize.W1024xH1024
            };

            // Step 3: Generate image
            var options = new ImageGenerationOptions
            {
                Size = imageSize,
                Quality = GeneratedImageQuality.High,
                ResponseFormat = GeneratedImageFormat.Uri
            };

            var image = await _imageClient.GenerateImageAsync(finalPrompt, options);
            var imageUrl = image.Value.ImageUri.ToString();

            // Step 4: Save locally
            var fileName = $"{Guid.NewGuid():N}.png";
            var localPath = await _imageStorage.SaveImageFromUrlAsync(imageUrl, fileName);

            // Step 5: Save to history
            var generation = new ImageGeneration
            {
                OriginalPrompt = request.Prompt,
                EnhancedPrompt = enhancedPrompt,
                ReferenceImagePath = request.ReferenceImagePath,
                GeneratedImageUrl = imageUrl,
                LocalImagePath = localPath,
                ImageSize = request.Size.ToString(),
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
            _logger.LogError(ex, "Image generation failed");

            // Save failed attempt to history
            var generation = new ImageGeneration
            {
                OriginalPrompt = request.Prompt,
                EnhancedPrompt = enhancedPrompt,
                ReferenceImagePath = request.ReferenceImagePath,
                ImageSize = request.Size.ToString(),
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
            await _historyService.SaveGenerationAsync(generation);

            return new GenerationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
