using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IImageSizeMasterService
{
    Task<List<ImageSizeMaster>> GetActiveSizesAsync();
    Task<List<ImageSizeMaster>> GetAllAsync();
    Task<ImageSizeMaster?> GetByIdAsync(int id);
    Task CreateAsync(ImageSizeMaster size);
    Task UpdateAsync(ImageSizeMaster size);
    Task DeleteAsync(int id);
}
