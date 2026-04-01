using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ArtForgeAI.Services;

public class GeminiVideoService : IGeminiVideoService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiVideoService> _logger;

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private const string UploadUrl = "https://generativelanguage.googleapis.com/upload/v1beta/files";
    private const int PollIntervalMs = 5000;
    private const int MaxPollDurationMs = 5 * 60 * 1000; // 5 minutes

    public GeminiVideoService(
        HttpClient httpClient,
        IOptions<GeminiOptions> options,
        ILogger<GeminiVideoService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public Task<byte[]> GenerateVideoAsync(
        string prompt, string aspectRatio, int durationSeconds,
        Action<int>? onProgress = null, CancellationToken ct = default)
    {
        var instance = new Dictionary<string, object>
        {
            ["prompt"] = SanitizePrompt(prompt)
        };
        return SubmitAndPollAsync(instance, aspectRatio, durationSeconds, onProgress, ct);
    }

    public Task<byte[]> GenerateVideoFromImageAsync(
        string prompt, byte[] imageData, string imageMimeType,
        string aspectRatio, int durationSeconds,
        Action<int>? onProgress = null, CancellationToken ct = default)
    {
        // Vertex AI predictLongRunning format: bytesBase64Encoded + mimeType at top level
        var instance = new Dictionary<string, object>
        {
            ["prompt"] = SanitizePrompt(prompt),
            ["image"] = new
            {
                bytesBase64Encoded = Convert.ToBase64String(imageData),
                mimeType = imageMimeType
            }
        };
        return SubmitAndPollAsync(instance, aspectRatio, durationSeconds, onProgress, ct);
    }

    public Task<byte[]> GenerateVideoFromVideoAsync(
        string prompt, byte[] videoData, string videoMimeType,
        string aspectRatio, int durationSeconds,
        Action<int>? onProgress = null, CancellationToken ct = default)
    {
        var instance = new Dictionary<string, object>
        {
            ["prompt"] = SanitizePrompt(prompt),
            ["video"] = new
            {
                bytesBase64Encoded = Convert.ToBase64String(videoData),
                mimeType = videoMimeType
            }
        };
        return SubmitAndPollAsync(instance, aspectRatio, durationSeconds, onProgress, ct);
    }

    /// <summary>
    /// Uploads a file to the Gemini Files API using resumable upload protocol.
    /// Returns the file URI to reference in subsequent API calls.
    /// </summary>
    private async Task<string> UploadFileAsync(byte[] data, string mimeType, string displayName, CancellationToken ct)
    {
        _logger.LogInformation("Uploading file ({Size} bytes, {MimeType}) to Files API", data.Length, mimeType);

        // Step 1: Initiate resumable upload
        var initRequest = new HttpRequestMessage(HttpMethod.Post, UploadUrl);
        initRequest.Headers.Add("x-goog-api-key", _options.ApiKey);
        initRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        initRequest.Headers.Add("X-Goog-Upload-Command", "start");
        initRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", data.Length.ToString());
        initRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
        initRequest.Content = new StringContent(
            JsonSerializer.Serialize(new { file = new { display_name = displayName } }),
            System.Text.Encoding.UTF8, "application/json");

        var initResponse = await _httpClient.SendAsync(initRequest, ct);

        if (!initResponse.IsSuccessStatusCode)
        {
            var errBody = await initResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("File upload init failed: {Status} - {Body}", initResponse.StatusCode, errBody);
            throw new HttpRequestException($"File upload failed ({initResponse.StatusCode}): {errBody}");
        }

        // Get the upload URL from the response header
        if (!initResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
            throw new InvalidOperationException("File upload init did not return an upload URL.");

        var uploadUrl = uploadUrls.First();

        // Step 2: Upload the actual file bytes
        var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        uploadRequest.Content = new ByteArrayContent(data);
        uploadRequest.Content.Headers.ContentLength = data.Length;

        var uploadResponse = await _httpClient.SendAsync(uploadRequest, ct);
        var uploadJson = await uploadResponse.Content.ReadAsStringAsync(ct);

        if (!uploadResponse.IsSuccessStatusCode)
        {
            _logger.LogError("File upload failed: {Status} - {Body}", uploadResponse.StatusCode, uploadJson);
            throw new HttpRequestException($"File upload failed ({uploadResponse.StatusCode}).");
        }

        // Parse the file URI from the response
        using var doc = JsonDocument.Parse(uploadJson);
        var fileUri = doc.RootElement.GetProperty("file").GetProperty("uri").GetString()
            ?? throw new InvalidOperationException("File upload succeeded but no URI returned.");

        _logger.LogInformation("File uploaded successfully: {FileUri}", fileUri);
        return fileUri;
    }

    private async Task<byte[]> SubmitAndPollAsync(
        Dictionary<string, object> instance, string aspectRatio, int durationSeconds,
        Action<int>? onProgress, CancellationToken ct)
    {
        var model = _options.VideoModel;
        var url = $"{BaseUrl}/models/{model}:predictLongRunning";

        // Build request body — parameters block is optional per the API docs
        var body = new Dictionary<string, object>
        {
            ["instances"] = new[] { instance }
        };

        body["parameters"] = new
        {
            aspectRatio,
            durationSeconds
        };

        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        _logger.LogInformation("Submitting video generation to Veo model {Model}", model);

        var response = await _httpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Veo API submit error: {StatusCode} - {Response}", response.StatusCode, responseJson);

            var detail = responseJson;
            try
            {
                using var errDoc = JsonDocument.Parse(responseJson);
                if (errDoc.RootElement.TryGetProperty("error", out var errObj) &&
                    errObj.TryGetProperty("message", out var errMsg))
                    detail = errMsg.GetString() ?? responseJson;
            }
            catch { }

            throw new HttpRequestException($"Video generation failed: {detail}");
        }

        // Parse the operation name from the response
        using var submitDoc = JsonDocument.Parse(responseJson);
        var operationName = submitDoc.RootElement.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("No operation name returned from video generation API.");

        _logger.LogInformation("Video generation operation started: {OperationName}", operationName);

        return await PollOperationAsync(operationName, onProgress, ct);
    }

    private async Task<byte[]> PollOperationAsync(
        string operationName, Action<int>? onProgress, CancellationToken ct)
    {
        var pollUrl = $"{BaseUrl}/{operationName}";
        var elapsed = 0;

        while (elapsed < MaxPollDurationMs)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(PollIntervalMs, ct);
            elapsed += PollIntervalMs;

            onProgress?.Invoke(elapsed / 1000);

            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, pollUrl);
            pollRequest.Headers.Add("x-goog-api-key", _options.ApiKey);

            var pollResponse = await _httpClient.SendAsync(pollRequest, ct);
            var pollJson = await pollResponse.Content.ReadAsStringAsync(ct);

            if (!pollResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Poll request failed: {StatusCode} - {Response}", pollResponse.StatusCode, pollJson);
                continue;
            }

            using var pollDoc = JsonDocument.Parse(pollJson);
            var root = pollDoc.RootElement;

            if (root.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
            {
                _logger.LogInformation("Video generation completed after {Elapsed}s. Response: {Json}", elapsed / 1000, pollJson);

                if (root.TryGetProperty("error", out var errorProp))
                {
                    var errorMsg = errorProp.TryGetProperty("message", out var msgProp)
                        ? msgProp.GetString() : "Unknown error";
                    throw new InvalidOperationException($"Video generation failed: {errorMsg}");
                }

                if (!root.TryGetProperty("response", out var videoResponse))
                {
                    _logger.LogError("No 'response' property in completed operation: {Json}", pollJson);
                    throw new InvalidOperationException("Video generation completed but response is missing. Check server logs.");
                }

                return await ExtractVideoBytes(videoResponse, pollJson, ct);
            }

            _logger.LogDebug("Video generation still in progress ({Elapsed}s)", elapsed / 1000);
        }

        throw new TimeoutException($"Video generation timed out after {MaxPollDurationMs / 1000} seconds.");
    }

    private async Task<byte[]> ExtractVideoBytes(JsonElement videoResponse, string fullJson, CancellationToken ct)
    {
        // Check for RAI (safety) filtering first
        if (videoResponse.TryGetProperty("generateVideoResponse", out var genVideoResp))
        {
            if (genVideoResp.TryGetProperty("raiMediaFilteredCount", out var filteredCount) &&
                filteredCount.GetInt32() > 0)
            {
                var reason = "Content was blocked by safety filters.";
                if (genVideoResp.TryGetProperty("raiMediaFilteredReasons", out var reasons) &&
                    reasons.GetArrayLength() > 0)
                    reason = reasons[0].GetString() ?? reason;

                throw new InvalidOperationException(reason);
            }
        }

        // Try: response.generateVideoResponse.generatedSamples[].video.uri
        if (genVideoResp.ValueKind != JsonValueKind.Undefined &&
            genVideoResp.TryGetProperty("generatedSamples", out var samples) &&
            samples.GetArrayLength() > 0)
        {
            var firstSample = samples[0];
            if (firstSample.TryGetProperty("video", out var videoObj) &&
                videoObj.TryGetProperty("uri", out var uriProp) &&
                !string.IsNullOrEmpty(uriProp.GetString()))
                return await DownloadFromUri(uriProp.GetString()!, ct);
        }

        // Fallback: response.generatedVideos[].video.uri or .bytesBase64Encoded
        if (videoResponse.TryGetProperty("generatedVideos", out var generatedVideos) &&
            generatedVideos.GetArrayLength() > 0)
        {
            var firstVideo = generatedVideos[0];
            if (firstVideo.TryGetProperty("video", out var videoObj2))
            {
                if (videoObj2.TryGetProperty("uri", out var uriProp2) &&
                    !string.IsNullOrEmpty(uriProp2.GetString()))
                    return await DownloadFromUri(uriProp2.GetString()!, ct);

                if (videoObj2.TryGetProperty("bytesBase64Encoded", out var b64Prop) &&
                    !string.IsNullOrEmpty(b64Prop.GetString()))
                    return Convert.FromBase64String(b64Prop.GetString()!);
            }
        }

        // Walk all properties to find any video URI or bytes — handle unknown structures
        var videoBytes = TryFindVideoInJson(videoResponse);
        if (videoBytes != null)
            return await DownloadFromUri(videoBytes, ct);

        _logger.LogError("Unexpected video response structure: {Response}", fullJson);
        throw new InvalidOperationException($"Could not extract video from response. Raw: {fullJson}");
    }

    /// <summary>Recursively search for a video URI in the JSON response.</summary>
    private static string? TryFindVideoInJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                // Look for any property named "uri" or "fileUri" with a value
                if ((prop.Name == "uri" || prop.Name == "fileUri") &&
                    prop.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(prop.Value.GetString()))
                    return prop.Value.GetString();

                var nested = TryFindVideoInJson(prop.Value);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = TryFindVideoInJson(item);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private async Task<byte[]> DownloadFromUri(string uri, CancellationToken ct)
    {
        _logger.LogInformation("Downloading generated video from URI");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static string SanitizePrompt(string prompt) =>
        System.Text.RegularExpressions.Regex.Replace(
            prompt.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " "),
            @"\s{2,}", " ").Trim();
}
