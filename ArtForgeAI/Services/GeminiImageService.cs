using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ArtForgeAI.Services;

public class GeminiImageService : IGeminiImageService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiImageService> _logger;

    public GeminiImageService(
        HttpClient httpClient,
        IOptions<GeminiOptions> options,
        ILogger<GeminiImageService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(string? text, byte[] imageBytes)> GenerateImageAsync(string prompt, int width, int height)
    {
        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                responseModalities = new[] { "TEXT", "IMAGE" },
                imageConfig = new
                {
                    aspectRatio = MapAspectRatio(width, height)
                }
            }
        };

        return await SendRequestAsync(requestBody);
    }

    public async Task<(string? text, byte[] imageBytes)> EditImageAsync(
        string prompt, List<(byte[] data, string mimeType)> images, int width, int height)
    {
        // Build parts: labeled reference images first, then text prompt last.
        // Placing images before the text prompt gives Gemini visual context
        // before it reads the composition instructions.
        var parts = new List<object>();

        for (int i = 0; i < images.Count; i++)
        {
            var (data, mimeType) = images[i];
            if (images.Count > 1)
            {
                parts.Add(new { text = $"[Reference Image {i + 1}]:" });
            }
            parts.Add(new
            {
                inline_data = new
                {
                    mime_type = mimeType,
                    data = Convert.ToBase64String(data)
                }
            });
        }

        parts.Add(new { text = prompt });

        var requestBody = new
        {
            contents = new[]
            {
                new { parts = parts.ToArray() }
            },
            generationConfig = new
            {
                responseModalities = new[] { "TEXT", "IMAGE" },
                imageConfig = new
                {
                    aspectRatio = MapAspectRatio(width, height)
                }
            }
        };

        return await SendRequestAsync(requestBody);
    }

    /// <summary>
    /// Maps pixel dimensions to the closest Gemini-supported aspect ratio string.
    /// Supported: "1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9"
    /// </summary>
    private static string MapAspectRatio(int width, int height)
    {
        var ratio = (double)width / height;

        // Landscape ratios (wide to narrow)
        if (ratio > 1.5)  return "16:9";  // 1.778
        if (ratio > 1.35) return "3:2";   // 1.500
        if (ratio > 1.15) return "4:3";   // 1.333
        if (ratio > 1.05) return "5:4";   // 1.250

        // Portrait ratios (tall to narrow)
        if (ratio < 0.6)  return "9:16";  // 0.5625
        if (ratio < 0.7)  return "2:3";   // 0.6667 — exact match for 20×30
        if (ratio < 0.82) return "3:4";   // 0.750
        if (ratio < 0.95) return "4:5";   // 0.800

        return "1:1";                      // square
    }

    public async Task<string> AnalyzeImageAsync(byte[] imageData, string mimeType, string textPrompt)
    {
        var primaryModel = _options.AnalysisModel;
        var fallbackModel = _options.FallbackAnalysisModel;

        try
        {
            return await SendAnalyzeToModelAsync(imageData, mimeType, textPrompt, primaryModel);
        }
        catch (Exception ex) when (!string.IsNullOrEmpty(fallbackModel) && fallbackModel != primaryModel)
        {
            _logger.LogWarning(ex, "Analysis model {PrimaryModel} failed, falling back to {FallbackModel}",
                primaryModel, fallbackModel);
            return await SendAnalyzeToModelAsync(imageData, mimeType, textPrompt, fallbackModel);
        }
    }

    private async Task<string> SendAnalyzeToModelAsync(byte[] imageData, string mimeType, string textPrompt, string model)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_options.ApiKey}";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mimeType,
                                data = Convert.ToBase64String(imageData)
                            }
                        },
                        new { text = textPrompt }
                    }
                }
            },
            generationConfig = new
            {
                responseModalities = new[] { "TEXT" },
                responseMimeType = "text/plain"
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending analyze request to Gemini model {Model}", model);

        var response = await _httpClient.PostAsync(url, content);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini analysis API error ({Model}): {StatusCode} - {Response}", model, response.StatusCode, responseJson);
            throw new HttpRequestException($"Gemini API error ({model}: {response.StatusCode}). Please try again.");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var candidates = root.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
            throw new InvalidOperationException($"Gemini ({model}) returned no candidates.");

        var parts = candidates[0].GetProperty("content").GetProperty("parts");

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textProp))
            {
                return textProp.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Sends the request to the primary model, falling back to FallbackImageModel on failure.
    /// </summary>
    private async Task<(string? text, byte[] imageBytes)> SendRequestAsync(object requestBody)
    {
        var primaryModel = _options.ImageModel;
        var fallbackModel = _options.FallbackImageModel;

        try
        {
            return await SendToModelAsync(requestBody, primaryModel);
        }
        catch (Exception ex) when (!string.IsNullOrEmpty(fallbackModel) && fallbackModel != primaryModel)
        {
            _logger.LogWarning(ex, "Primary model {PrimaryModel} failed, falling back to {FallbackModel}",
                primaryModel, fallbackModel);
            return await SendToModelAsync(requestBody, fallbackModel);
        }
    }

    private async Task<(string? text, byte[] imageBytes)> SendToModelAsync(object requestBody, string model)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_options.ApiKey}";

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending request to Gemini model {Model}", model);

        var response = await _httpClient.PostAsync(url, content);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini API error ({Model}): {StatusCode} - {Response}", model, response.StatusCode, responseJson);
            throw new HttpRequestException($"Gemini API error ({model}: {response.StatusCode}). Please try again.");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        string? textResult = null;
        byte[]? imageBytesResult = null;

        var candidates = root.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
            throw new InvalidOperationException($"Gemini ({model}) returned no candidates.");

        var parts = candidates[0].GetProperty("content").GetProperty("parts");

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textProp))
            {
                textResult = textProp.GetString();
            }
            else if (part.TryGetProperty("inlineData", out var inlineData))
            {
                var base64 = inlineData.GetProperty("data").GetString();
                if (base64 is not null)
                    imageBytesResult = Convert.FromBase64String(base64);
            }
        }

        if (imageBytesResult is null)
        {
            _logger.LogWarning("Gemini ({Model}) returned text but no image. Text: {Text}", model, textResult);
            var msg = $"Gemini ({model}) did not return an image.";
            if (!string.IsNullOrEmpty(textResult))
                msg += $" Gemini said: {(textResult.Length > 300 ? textResult[..300] + "..." : textResult)}";
            throw new InvalidOperationException(msg);
        }

        _logger.LogInformation("Successfully generated image using model {Model}", model);
        // Prefix model name so the caller/UI knows which model produced the result
        textResult = $"[{model}] {textResult ?? ""}";
        return (textResult, imageBytesResult);
    }
}
