using ArtForgeAI.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace ArtForgeAI.Services;

public class PromptEnhancerService : IPromptEnhancerService
{
    private readonly ChatClient _chatClient;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PromptEnhancerService> _logger;

    private const string SystemPrompt = """
        You are an expert AI art prompt engineer. Your job is to take a user's rough idea
        and transform it into a detailed, vivid prompt optimized for DALL-E 3 image generation.

        Include specific details about:
        - Subject composition and positioning
        - Lighting (type, direction, intensity, color temperature)
        - Art style or photographic style
        - Color palette and mood
        - Texture and material details
        - Background and environment
        - Quality descriptors (8K, ultra-detailed, professional, etc.)

        Keep the enhanced prompt under 800 characters. Output ONLY the enhanced prompt, nothing else.
        Do not include any preamble, explanation, or quotes around the prompt.
        """;

    private const string VisionSystemPrompt = """
        Analyze this reference image and describe its key visual elements including:
        - Subject matter and composition
        - Color palette and lighting
        - Art style or photographic technique
        - Mood and atmosphere
        - Notable textures or patterns

        Provide a concise description (under 200 words) that captures the essence of the image.
        Output ONLY the description, nothing else.
        """;

    public PromptEnhancerService(
        IOptions<OpenAiOptions> options,
        IWebHostEnvironment env,
        ILogger<PromptEnhancerService> logger)
    {
        _chatClient = new ChatClient(options.Value.PromptModel, options.Value.ApiKey);
        _env = env;
        _logger = logger;
    }

    public async Task<PromptEnhancementResult> EnhancePromptAsync(string rawPrompt, string? referenceImagePath = null)
    {
        try
        {
            string? referenceDescription = null;

            if (!string.IsNullOrEmpty(referenceImagePath))
            {
                referenceDescription = await DescribeReferenceImageAsync(referenceImagePath);
            }

            var userMessage = string.IsNullOrEmpty(referenceDescription)
                ? rawPrompt
                : $"User's idea: {rawPrompt}\n\nReference image description: {referenceDescription}\n\nCombine the user's idea with the visual style and elements from the reference image.";

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(userMessage)
            };

            var completion = await _chatClient.CompleteChatAsync(messages);
            var enhanced = completion.Value.Content[0].Text.Trim();

            return new PromptEnhancementResult
            {
                Success = true,
                EnhancedPrompt = enhanced
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enhance prompt");
            return new PromptEnhancementResult
            {
                Success = false,
                EnhancedPrompt = rawPrompt,
                ErrorMessage = $"Prompt enhancement failed: {ex.Message}. Using original prompt."
            };
        }
    }

    private async Task<string?> DescribeReferenceImageAsync(string referenceImagePath)
    {
        try
        {
            var fullPath = Path.Combine(_env.WebRootPath, referenceImagePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            if (!File.Exists(fullPath))
                return null;

            var imageBytes = await File.ReadAllBytesAsync(fullPath);
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            var mediaType = extension switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };

            var messages = new List<ChatMessage>
            {
                new UserChatMessage(
                    ChatMessageContentPart.CreateTextPart(VisionSystemPrompt),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), mediaType)
                )
            };

            var completion = await _chatClient.CompleteChatAsync(messages);
            return completion.Value.Content[0].Text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to describe reference image");
            return null;
        }
    }
}
