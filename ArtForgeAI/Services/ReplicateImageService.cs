using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ArtForgeAI.Services;

public class ReplicateImageService : IReplicateImageService
{
    private readonly HttpClient _httpClient;
    private readonly ReplicateOptions _options;
    private readonly ILogger<ReplicateImageService> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    public ReplicateImageService(
        HttpClient httpClient,
        IOptions<ReplicateOptions> options,
        ILogger<ReplicateImageService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<byte[]> GenerateImageAsync(string prompt, int width, int height)
    {
        var input = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["aspect_ratio"] = MapAspectRatio(width, height),
            ["output_format"] = "png",
            ["output_quality"] = 95,
            ["safety_tolerance"] = 2
        };

        var outputUrl = await RunPredictionAsync(_options.ImageModel, input);
        return await DownloadImageAsync(outputUrl);
    }

    public async Task<byte[]> EditImageAsync(string prompt, byte[] imageBytes, string mimeType, int width, int height)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var dataUrl = $"data:{mimeType};base64,{base64}";

        // Use FLUX Kontext Max for image editing — sends actual image + instruction
        var input = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["input_image"] = dataUrl,
            ["aspect_ratio"] = "match_input_image",
            ["output_format"] = "png",
            ["safety_tolerance"] = 2
        };

        var outputUrl = await RunPredictionAsync(_options.ImageEditModel, input);
        return await DownloadImageAsync(outputUrl);
    }

    private async Task<string> RunPredictionAsync(string model, Dictionary<string, object> input)
    {
        var url = $"https://api.replicate.com/v1/models/{model}/predictions";

        var requestBody = new { input };
        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = content;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        request.Headers.Add("Prefer", "wait=60");

        _logger.LogInformation("Creating Replicate prediction with model {Model}", model);

        var response = await _httpClient.SendAsync(request);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Replicate API error: {StatusCode} - {Response}", response.StatusCode, responseJson);
            throw new HttpRequestException($"Replicate API error ({response.StatusCode}): {responseJson}");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var status = root.GetProperty("status").GetString();

        // If Prefer: wait=60 returned a completed prediction
        if (status == "succeeded")
        {
            return ExtractOutputUrl(root);
        }

        if (status == "failed")
        {
            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
            throw new InvalidOperationException($"Replicate prediction failed: {error}");
        }

        // Otherwise poll until done
        var getUrl = root.GetProperty("urls").GetProperty("get").GetString()!;
        return await PollForResultAsync(getUrl);
    }

    private async Task<string> PollForResultAsync(string getUrl)
    {
        var deadline = DateTime.UtcNow + MaxWait;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);

            using var request = new HttpRequestMessage(HttpMethod.Get, getUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

            var response = await _httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Replicate poll error ({response.StatusCode}): {responseJson}");

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();

            _logger.LogDebug("Replicate prediction status: {Status}", status);

            if (status == "succeeded")
            {
                return ExtractOutputUrl(root);
            }

            if (status is "failed" or "canceled")
            {
                var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
                throw new InvalidOperationException($"Replicate prediction {status}: {error}");
            }
        }

        throw new TimeoutException("Replicate prediction timed out after 5 minutes.");
    }

    private static string ExtractOutputUrl(JsonElement root)
    {
        var output = root.GetProperty("output");

        // Output can be a string URL or an array of URLs
        if (output.ValueKind == JsonValueKind.String)
        {
            return output.GetString()!;
        }

        if (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0)
        {
            return output[0].GetString()!;
        }

        throw new InvalidOperationException("Replicate prediction succeeded but returned no output.");
    }

    private async Task<byte[]> DownloadImageAsync(string imageUrl)
    {
        _logger.LogInformation("Downloading generated image from Replicate");
        return await _httpClient.GetByteArrayAsync(imageUrl);
    }

    private static string MapAspectRatio(int width, int height)
    {
        if (width == height) return "1:1";
        var gcd = Gcd(width, height);
        var w = width / gcd;
        var h = height / gcd;
        return $"{w}:{h}";
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { (a, b) = (b, a % b); }
        return a;
    }
}
