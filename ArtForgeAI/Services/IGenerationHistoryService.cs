using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IGenerationHistoryService
{
    Task<List<ImageGeneration>> GetHistoryAsync(string userId = "default", int page = 1, int pageSize = 20);
    Task<List<ImageGeneration>> GetAllHistoryAsync(int page = 1, int pageSize = 20);
    Task<ImageGeneration?> GetByIdAsync(int id);
    Task SaveGenerationAsync(ImageGeneration generation);
    Task<bool> DeleteGenerationAsync(int id);
    Task<int> GetTotalCountAsync(string userId = "default");
    Task<int> GetAllTotalCountAsync();
    Task<UserPreference> GetUserPreferencesAsync(string userId = "default");
    Task SaveUserPreferencesAsync(UserPreference preferences);
}
