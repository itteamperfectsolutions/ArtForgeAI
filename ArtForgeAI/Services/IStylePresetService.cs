using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IStylePresetService
{
    Task<List<StylePreset>> GetActivePresetsAsync();
    Task<List<StylePreset>> GetPresetsByCategoryAsync(string category);
    Task<List<StylePreset>> GetAllAsync();
    Task<StylePreset?> GetByIdAsync(int id);
    Task CreateAsync(StylePreset preset);
    Task UpdateAsync(StylePreset preset);
    Task DeleteAsync(int id);
}
