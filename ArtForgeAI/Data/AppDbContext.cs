using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ImageGeneration> ImageGenerations => Set<ImageGeneration>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImageGeneration>(entity =>
        {
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<UserPreference>(entity =>
        {
            entity.HasIndex(e => e.UserId).IsUnique();
        });
    }
}
