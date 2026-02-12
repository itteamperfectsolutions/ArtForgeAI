namespace ArtForgeAI.Services;

public interface IImageStorageService
{
    Task<string> SaveImageFromUrlAsync(string imageUrl, string fileName);
    Task<string> SaveImageFromBytesAsync(BinaryData imageBytes, string fileName);
    Task<string> SaveUploadedFileAsync(Stream fileStream, string fileName);
    Task<byte[]> GetImageAsBytesAsync(string localPath, string format);
    Task<bool> DeleteImageAsync(string localPath);
    string GetWebPath(string localPath);
}
