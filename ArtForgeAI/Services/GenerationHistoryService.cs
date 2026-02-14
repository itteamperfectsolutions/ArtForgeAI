using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class GenerationHistoryService : IGenerationHistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IImageStorageService _imageStorage;
    private readonly ILogger<GenerationHistoryService> _logger;

    public GenerationHistoryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IImageStorageService imageStorage,
        ILogger<GenerationHistoryService> logger)
    {
        _dbFactory = dbFactory;
        _imageStorage = imageStorage;
        _logger = logger;
    }

    public async Task<List<ImageGeneration>> GetHistoryAsync(string userId = "default", int page = 1, int pageSize = 20)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImageGenerations
            .Where(g => g.UserId == userId && g.IsSuccess)
            .OrderByDescending(g => g.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<ImageGeneration?> GetByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImageGenerations.FindAsync(id);
    }

    public async Task SaveGenerationAsync(ImageGeneration generation)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.ImageGenerations.Add(generation);
        await db.SaveChangesAsync();
    }

    public async Task<bool> DeleteGenerationAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var generation = await db.ImageGenerations.FindAsync(id);
        if (generation is null)
            return false;

        // Delete local files
        if (!string.IsNullOrEmpty(generation.LocalImagePath))
            await _imageStorage.DeleteImageAsync(generation.LocalImagePath);

        if (!string.IsNullOrEmpty(generation.ReferenceImagePath))
            await _imageStorage.DeleteImageAsync(generation.ReferenceImagePath);

        db.ImageGenerations.Remove(generation);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<int> GetTotalCountAsync(string userId = "default")
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImageGenerations
            .CountAsync(g => g.UserId == userId && g.IsSuccess);
    }

    public async Task<UserPreference> GetUserPreferencesAsync(string userId = "default")
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var prefs = await db.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId);

        return prefs ?? new UserPreference { UserId = userId };
    }

    public async Task SaveUserPreferencesAsync(UserPreference preferences)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == preferences.UserId);

        if (existing is null)
        {
            db.UserPreferences.Add(preferences);
        }
        else
        {
            existing.DefaultImageSize = preferences.DefaultImageSize;
            existing.DarkMode = preferences.DarkMode;
            existing.AutoEnhancePrompt = preferences.AutoEnhancePrompt;
            existing.DefaultDownloadFormat = preferences.DefaultDownloadFormat;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }
}
