namespace ArtForgeAI.Services;

public interface IImageStorageService
{
    Task<string> SaveImageFromUrlAsync(string imageUrl, string fileName);
    Task<string> SaveImageFromBytesAsync(BinaryData imageBytes, string fileName);
    Task<string> SaveUploadedFileAsync(Stream fileStream, string fileName);
    Task<byte[]> GetImageAsBytesAsync(string localPath, string format);
    Task<string> PrepareHighResDownloadAsync(string localPath, string format, int targetWidth = 3600, int targetHeight = 5400, int dpi = 300);
    Task<bool> DeleteImageAsync(string localPath);
    string GetWebPath(string localPath);
}
