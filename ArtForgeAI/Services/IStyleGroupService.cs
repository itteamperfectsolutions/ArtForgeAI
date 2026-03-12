using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IStyleGroupService
{
    Task<List<StyleGroup>> GetAllAsync();
    Task<List<StyleGroup>> GetActiveAsync();
    Task<StyleGroup?> GetByIdAsync(int id);
    Task CreateAsync(StyleGroup group);
    Task UpdateAsync(StyleGroup group);
    Task DeleteAsync(int id);
}
