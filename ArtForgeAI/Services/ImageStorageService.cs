using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace ArtForgeAI.Services;

public class ImageStorageService : IImageStorageService
{
    private readonly IWebHostEnvironment _env;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageStorageService> _logger;

    public ImageStorageService(
        IWebHostEnvironment env,
        IHttpClientFactory httpClientFactory,
        ILogger<ImageStorageService> logger)
    {
        _env = env;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> SaveImageFromUrlAsync(string imageUrl, string fileName)
    {
        var generatedDir = Path.Combine(_env.WebRootPath, "generated");
        Directory.CreateDirectory(generatedDir);

        var filePath = Path.Combine(generatedDir, fileName);
        var client = _httpClientFactory.CreateClient();
        var imageBytes = await client.GetByteArrayAsync(imageUrl);
        await File.WriteAllBytesAsync(filePath, imageBytes);

        return Path.Combine("generated", fileName).Replace("\\", "/");
    }

    public async Task<string> SaveUploadedFileAsync(Stream fileStream, string fileName)
    {
        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var filePath = Path.Combine(uploadsDir, safeName);

        using var fs = new FileStream(filePath, FileMode.Create);
        await fileStream.CopyToAsync(fs);

        return Path.Combine("uploads", safeName).Replace("\\", "/");
    }

    public async Task<byte[]> GetImageAsBytesAsync(string localPath, string format)
    {
        var fullPath = Path.Combine(_env.WebRootPath, localPath.Replace("/", Path.DirectorySeparatorChar.ToString()));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Image file not found.", fullPath);

        var imageBytes = await File.ReadAllBytesAsync(fullPath);

        using var image = Image.Load(imageBytes);
        using var ms = new MemoryStream();

        if (format.Equals("jpg", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("jpeg", StringComparison.OrdinalIgnoreCase))
        {
            await image.SaveAsync(ms, new JpegEncoder { Quality = 95 });
        }
        else
        {
            await image.SaveAsync(ms, new PngEncoder());
        }

        return ms.ToArray();
    }

    public Task<bool> DeleteImageAsync(string localPath)
    {
        try
        {
            var fullPath = Path.Combine(_env.WebRootPath, localPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete image at {Path}", localPath);
            return Task.FromResult(false);
        }
    }

    public string GetWebPath(string localPath) => $"/{localPath}";
}
