using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class GenerationHistoryService : IGenerationHistoryService
{
    private readonly AppDbContext _db;
    private readonly IImageStorageService _imageStorage;
    private readonly ILogger<GenerationHistoryService> _logger;

    public GenerationHistoryService(
        AppDbContext db,
        IImageStorageService imageStorage,
        ILogger<GenerationHistoryService> logger)
    {
        _db = db;
        _imageStorage = imageStorage;
        _logger = logger;
    }

    public async Task<List<ImageGeneration>> GetHistoryAsync(string userId = "default", int page = 1, int pageSize = 20)
    {
        return await _db.ImageGenerations
            .Where(g => g.UserId == userId && g.IsSuccess)
            .OrderByDescending(g => g.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<ImageGeneration?> GetByIdAsync(int id)
    {
        return await _db.ImageGenerations.FindAsync(id);
    }

    public async Task SaveGenerationAsync(ImageGeneration generation)
    {
        _db.ImageGenerations.Add(generation);
        await _db.SaveChangesAsync();
    }

    public async Task<bool> DeleteGenerationAsync(int id)
    {
        var generation = await _db.ImageGenerations.FindAsync(id);
        if (generation is null)
            return false;

        // Delete local files
        if (!string.IsNullOrEmpty(generation.LocalImagePath))
            await _imageStorage.DeleteImageAsync(generation.LocalImagePath);

        if (!string.IsNullOrEmpty(generation.ReferenceImagePath))
            await _imageStorage.DeleteImageAsync(generation.ReferenceImagePath);

        _db.ImageGenerations.Remove(generation);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> GetTotalCountAsync(string userId = "default")
    {
        return await _db.ImageGenerations
            .CountAsync(g => g.UserId == userId && g.IsSuccess);
    }

    public async Task<UserPreference> GetUserPreferencesAsync(string userId = "default")
    {
        var prefs = await _db.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);

        return prefs ?? new UserPreference { UserId = userId };
    }

    public async Task SaveUserPreferencesAsync(UserPreference preferences)
    {
        var existing = await _db.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == preferences.UserId);

        if (existing is null)
        {
            _db.UserPreferences.Add(preferences);
        }
        else
        {
            existing.DefaultImageSize = preferences.DefaultImageSize;
            existing.DarkMode = preferences.DarkMode;
            existing.AutoEnhancePrompt = preferences.AutoEnhancePrompt;
            existing.DefaultDownloadFormat = preferences.DefaultDownloadFormat;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }
}
