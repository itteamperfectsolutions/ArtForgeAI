using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface ICollageTemplateService
{
    // CRUD
    Task<List<CollageTemplate>> GetAllAsync();
    Task<List<CollageTemplate>> GetActiveAsync();
    Task<List<CollageTemplate>> GetByCategoryAsync(string category);
    Task<List<string>> GetCategoriesAsync();
    Task<CollageTemplate?> GetByIdAsync(int id);
    Task CreateAsync(CollageTemplate template);
    Task UpdateAsync(CollageTemplate template);
    Task DeleteAsync(int id);

    // Generation
    Task<byte[]> GenerateSlotImageAsync(CollageTemplate template, byte[] masterPhoto, string mimeType,
        int slotIndex, int width, int height);
    Task<byte[]> ComposeCollageAsync(CollageTemplate template, List<byte[]> slotImages,
        int width, int height, CollagePersonalisation? personalisation = null);
}

/// <summary>User-provided personalisation data for the collage</summary>
public class CollagePersonalisation
{
    public string? Name { get; set; }
    public string? Occasion { get; set; }
    public DateTime? Date { get; set; }
    public string? Message { get; set; }
}
