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
                responseMimeType = "text/plain"
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
                responseMimeType = "text/plain"
            }
        };

        return await SendRequestAsync(requestBody);
    }

    public async Task<string> AnalyzeImageAsync(byte[] imageData, string mimeType, string textPrompt)
    {
        // Use the dedicated analysis model (vision-capable, text-only output)
        // rather than the image generation model which is unreliable for text responses
        var model = _options.AnalysisModel;
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
            _logger.LogError("Gemini API error: {StatusCode} - {Response}", response.StatusCode, responseJson);
            throw new HttpRequestException($"Gemini API error ({response.StatusCode}). Please try again.");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var candidates = root.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini returned no candidates.");

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

    private async Task<(string? text, byte[] imageBytes)> SendRequestAsync(object requestBody)
    {
        var model = _options.ImageModel;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_options.ApiKey}";

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending request to Gemini model {Model}", model);

        var response = await _httpClient.PostAsync(url, content);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini API error: {StatusCode} - {Response}", response.StatusCode, responseJson);
            throw new HttpRequestException($"Gemini API error ({response.StatusCode}). Please try again.");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        string? textResult = null;
        byte[]? imageBytesResult = null;

        var candidates = root.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini returned no candidates.");

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
            _logger.LogWarning("Gemini returned text but no image. Text: {Text}", textResult);
            var msg = "Gemini did not return an image in the response.";
            if (!string.IsNullOrEmpty(textResult))
                msg += $" Gemini said: {(textResult.Length > 300 ? textResult[..300] + "..." : textResult)}";
            throw new InvalidOperationException(msg);
        }

        return (textResult, imageBytesResult);
    }
}
