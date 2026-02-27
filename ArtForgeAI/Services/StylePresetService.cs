using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class StylePresetService : IStylePresetService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public StylePresetService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<StylePreset>> GetActivePresetsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.StylePresets
            .Where(s => s.IsActive)
            .OrderBy(s => s.Category)
            .ThenBy(s => s.SortOrder)
            .ToListAsync();
    }

    public async Task<List<StylePreset>> GetPresetsByCategoryAsync(string category)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.StylePresets
            .Where(s => s.IsActive && s.Category == category)
            .OrderBy(s => s.SortOrder)
            .ToListAsync();
    }

    public async Task<List<StylePreset>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.StylePresets
            .OrderBy(s => s.Category)
            .ThenBy(s => s.SortOrder)
            .ToListAsync();
    }

    public async Task<StylePreset?> GetByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.StylePresets.FindAsync(id);
    }

    public async Task CreateAsync(StylePreset preset)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var maxSort = await db.StylePresets.MaxAsync(s => (int?)s.SortOrder) ?? 0;
        preset.SortOrder = maxSort + 1;
        db.StylePresets.Add(preset);
        await db.SaveChangesAsync();

        // If the user re-creates a style they previously deleted, un-track it
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM DeletedStyleSeeds WHERE Name = {0}", preset.Name);
    }

    public async Task UpdateAsync(StylePreset preset)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.StylePresets.Update(preset);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var preset = await db.StylePresets.FindAsync(id);
        if (preset is not null)
        {
            // Record the name so seed logic won't re-insert it on restart
            await db.Database.ExecuteSqlRawAsync(
                "IF NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = {0}) INSERT INTO DeletedStyleSeeds (Name) VALUES ({0})",
                preset.Name);

            db.StylePresets.Remove(preset);
            await db.SaveChangesAsync();
        }
    }
}
