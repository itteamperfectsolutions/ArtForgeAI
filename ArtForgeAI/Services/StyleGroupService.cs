using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class StyleGroupService : IStyleGroupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public StyleGroupService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<StyleGroup>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.StyleGroups
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<List<StyleGroup>> GetActiveAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.StyleGroups
            .Where(g => g.IsActive)
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<StyleGroup?> GetByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.StyleGroups.FindAsync(id);
    }

    public async Task CreateAsync(StyleGroup group)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var maxSort = await db.StyleGroups.MaxAsync(g => (int?)g.SortOrder) ?? 0;
        group.SortOrder = maxSort + 1;
        group.CreatedAtUtc = DateTime.UtcNow;
        db.StyleGroups.Add(group);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(StyleGroup group)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.StyleGroups.Update(group);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var group = await db.StyleGroups.FindAsync(id);
        if (group is not null)
        {
            // Clear StyleGroupId on any presets using this group
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE StylePresets SET StyleGroupId = NULL WHERE StyleGroupId = {0}", id);
            db.StyleGroups.Remove(group);
            await db.SaveChangesAsync();
        }
    }
}
