using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace ArtForgeAI.Services;

public class ImageStorageService : IImageStorageService
{
    private readonly IWebHostEnvironment _env;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageStorageService> _logger;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

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
        ValidateFileExtension(fileName);

        var safeName = Path.GetFileName(fileName);
        var generatedDir = Path.Combine(_env.WebRootPath, "generated");
        Directory.CreateDirectory(generatedDir);

        var filePath = SafeResolvePath(Path.Combine("generated", safeName));
        var client = _httpClientFactory.CreateClient();
        var imageBytes = await client.GetByteArrayAsync(imageUrl);
        await File.WriteAllBytesAsync(filePath, imageBytes);

        // Verify downloaded content is a valid image
        await ValidateImageContent(filePath);

        return Path.Combine("generated", safeName).Replace("\\", "/");
    }

    public async Task<string> SaveImageFromBytesAsync(BinaryData imageBytes, string fileName)
    {
        ValidateFileExtension(fileName);

        var safeName = Path.GetFileName(fileName);
        var generatedDir = Path.Combine(_env.WebRootPath, "generated");
        Directory.CreateDirectory(generatedDir);

        var filePath = SafeResolvePath(Path.Combine("generated", safeName));
        await File.WriteAllBytesAsync(filePath, imageBytes.ToArray());

        // Verify content is a valid image
        await ValidateImageContent(filePath);

        return Path.Combine("generated", safeName).Replace("\\", "/");
    }

    public async Task<string> SaveUploadedFileAsync(Stream fileStream, string fileName)
    {
        ValidateFileExtension(fileName);

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var filePath = Path.Combine(uploadsDir, safeName);

        using (var fs = new FileStream(filePath, FileMode.Create))
        {
            await fileStream.CopyToAsync(fs);
        }

        // Verify the file is actually a valid image (file must be closed first)
        await ValidateImageContent(filePath);

        return Path.Combine("uploads", safeName).Replace("\\", "/");
    }

    public async Task<byte[]> GetImageAsBytesAsync(string localPath, string format)
    {
        var fullPath = SafeResolvePath(localPath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Image file not found.");

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
            var fullPath = SafeResolvePath(localPath);
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

    public string GetWebPath(string localPath)
    {
        // Validate the path stays within webroot (prevents traversal in URLs)
        SafeResolvePath(localPath);
        return $"/{localPath}";
    }

    /// <summary>
    /// Resolves a web-relative path to an absolute path within WebRootPath,
    /// preventing path traversal attacks.
    /// </summary>
    private string SafeResolvePath(string localPath)
    {
        var normalized = localPath.Replace("/", Path.DirectorySeparatorChar.ToString());
        var fullPath = Path.GetFullPath(Path.Combine(_env.WebRootPath, normalized));
        var webRoot = Path.GetFullPath(_env.WebRootPath);

        if (!fullPath.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access to the specified path is denied.");

        return fullPath;
    }

    /// <summary>
    /// Validates that the file extension is in the allowed list.
    /// </summary>
    private static void ValidateFileExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"File type '{ext}' is not allowed. Accepted types: {string.Join(", ", AllowedExtensions)}");
    }

    /// <summary>
    /// Verifies that the file at the given path is a valid image; deletes and throws if not.
    /// </summary>
    private static async Task ValidateImageContent(string filePath)
    {
        try
        {
            using var verifyStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var info = await Image.IdentifyAsync(verifyStream);
            if (info is null)
                throw new InvalidOperationException();
        }
        catch
        {
            try { File.Delete(filePath); } catch { /* best effort cleanup */ }
            throw new InvalidOperationException("The file is not a valid image.");
        }
    }
}
