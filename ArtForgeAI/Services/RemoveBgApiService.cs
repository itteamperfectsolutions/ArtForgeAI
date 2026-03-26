using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ArtForgeAI.Services;

/// <summary>
/// Background removal using remove.bg API (industry-best quality, used by Canva).
/// Free tier: 50 removals/month. Configure API key in appsettings as RemoveBg:ApiKey.
/// </summary>
public sealed class RemoveBgApiService
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly ILogger<RemoveBgApiService> _logger;
    private readonly string _outputDir;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    public RemoveBgApiService(
        HttpClient httpClient,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<RemoveBgApiService> logger)
    {
        _httpClient = httpClient;
        _apiKey = config["RemoveBg:ApiKey"];
        _logger = logger;
        _outputDir = Path.Combine(env.WebRootPath, "generated");
        Directory.CreateDirectory(_outputDir);
    }

    /// <summary>
    /// Remove background using remove.bg API. Returns transparent PNG bytes.
    /// </summary>
    public async Task<byte[]> RemoveBackgroundAsync(byte[] imageBytes)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("remove.bg API key not configured. Add RemoveBg:ApiKey to appsettings.");

        _logger.LogInformation("Sending image to remove.bg API ({Size} bytes)...", imageBytes.Length);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.remove.bg/v1.0/removebg");
        request.Headers.Add("X-Api-Key", _apiKey);

        using var formContent = new MultipartFormDataContent();
        formContent.Add(new ByteArrayContent(imageBytes), "image_file", "image.png");
        formContent.Add(new StringContent("auto"), "size");
        formContent.Add(new StringContent("person"), "type"); // optimize for person detection
        request.Content = formContent;

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("remove.bg API error: {Status} - {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"remove.bg API error: {response.StatusCode} - {errorBody}");
        }

        var resultBytes = await response.Content.ReadAsByteArrayAsync();
        _logger.LogInformation("remove.bg returned {Size} bytes", resultBytes.Length);

        return resultBytes;
    }

    /// <summary>
    /// Full pipeline: remove bg + save transparent + optional colored version.
    /// </summary>
    public async Task<BackgroundRemovalResult> RemoveAndSaveAsync(byte[] imageBytes, string backgroundColor = "white")
    {
        var transparentBytes = await RemoveBackgroundAsync(imageBytes);

        // Ensure RGBA format
        using var img = Image.Load<Rgba32>(transparentBytes);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 });
        transparentBytes = ms.ToArray();

        // Save transparent
        var transFile = $"{Guid.NewGuid():N}_transparent.png";
        var transPath = Path.Combine(_outputDir, transFile);
        await File.WriteAllBytesAsync(transPath, transparentBytes);

        // Save colored version
        using var colored = img.Clone();
        var bgColor = Rgba32.ParseHex(backgroundColor == "white" ? "#FFFFFF" : backgroundColor);
        using var canvas = new Image<Rgba32>(colored.Width, colored.Height, bgColor);
        canvas.ProcessPixelRows(colored, (bgAcc, fgAcc) =>
        {
            for (int y = 0; y < bgAcc.Height; y++)
            {
                var bgRow = bgAcc.GetRowSpan(y);
                var fgRow = fgAcc.GetRowSpan(y);
                for (int x = 0; x < bgAcc.Width; x++)
                {
                    var fg = fgRow[x];
                    if (fg.A == 0) continue;
                    float a = fg.A / 255f, ia = 1f - a;
                    bgRow[x] = new Rgba32(
                        (byte)(fg.R * a + bgRow[x].R * ia),
                        (byte)(fg.G * a + bgRow[x].G * ia),
                        (byte)(fg.B * a + bgRow[x].B * ia), 255);
                }
            }
        });

        var colorFile = $"{Guid.NewGuid():N}_colored.png";
        var colorPath = Path.Combine(_outputDir, colorFile);
        canvas.SaveAsPng(colorPath, new PngEncoder { ColorType = PngColorType.Rgb });

        return new BackgroundRemovalResult($"generated/{colorFile}", $"generated/{transFile}", transparentBytes);
    }
}
