using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IPromptEnhancerService
{
    /// <summary>
    /// Enhances a prompt for text-to-image generation.
    /// Provider-aware: generates optimized prompts for each model (DALL-E 3, Gemini, FLUX).
    /// When referenceImagePath is provided, uses GPT-4o vision for identity preservation (DALL-E fallback).
    /// </summary>
    Task<PromptEnhancementResult> EnhancePromptAsync(
        string rawPrompt, ImageProvider provider, string? referenceImagePath = null);

    /// <summary>
    /// Enhances a prompt for image editing (model receives the actual reference image).
    /// Uses two-pass: intent classification → provider-optimized prompt generation.
    /// When referenceImageCount > 1, generates a composition-aware prompt that
    /// explicitly instructs the model to combine subjects from all reference images.
    /// </summary>
    Task<PromptEnhancementResult> EnhanceForImageEditAsync(
        string rawPrompt, ImageProvider provider, int referenceImageCount = 1,
        List<string>? referenceImagePaths = null);

    /// <summary>
    /// Returns true if the prompt is a pure background removal request
    /// (no additional edits like blur, style changes, or compound instructions).
    /// Used to short-circuit to local ONNX processing.
    /// </summary>
    bool IsPureBackgroundRemoval(string prompt);
}
