using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class ImageSizeMasterService : IImageSizeMasterService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ImageSizeMasterService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<ImageSizeMaster>> GetActiveSizesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImageSizeMasters
            .Where(s => s.IsActive)
            .OrderBy(s => s.SortOrder)
            .ToListAsync();
    }

    public async Task<List<ImageSizeMaster>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImageSizeMasters
            .OrderBy(s => s.SortOrder)
            .ToListAsync();
    }

    public async Task<ImageSizeMaster?> GetByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImageSizeMasters.FindAsync(id);
    }

    public async Task CreateAsync(ImageSizeMaster size)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var maxSort = await db.ImageSizeMasters.MaxAsync(s => (int?)s.SortOrder) ?? 0;
        size.SortOrder = maxSort + 1;
        db.ImageSizeMasters.Add(size);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(ImageSizeMaster size)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.ImageSizeMasters.Update(size);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var size = await db.ImageSizeMasters.FindAsync(id);
        if (size is not null)
        {
            db.ImageSizeMasters.Remove(size);
            await db.SaveChangesAsync();
        }
    }
}
