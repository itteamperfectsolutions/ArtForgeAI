using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ImageGeneration> ImageGenerations => Set<ImageGeneration>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<ImageSizeMaster> ImageSizeMasters => Set<ImageSizeMaster>();
    public DbSet<StylePreset> StylePresets => Set<StylePreset>();

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

        modelBuilder.Entity<ImageSizeMaster>().HasData(
            new ImageSizeMaster { Id = 1, Name = "Square", Width = 1024, Height = 1024, IsActive = true, SortOrder = 1, Unit = "px", DisplayWidth = 1024, DisplayHeight = 1024 },
            new ImageSizeMaster { Id = 2, Name = "Landscape", Width = 1792, Height = 1024, IsActive = true, SortOrder = 2, Unit = "px", DisplayWidth = 1792, DisplayHeight = 1024 },
            new ImageSizeMaster { Id = 3, Name = "Portrait", Width = 1024, Height = 1792, IsActive = true, SortOrder = 3, Unit = "px", DisplayWidth = 1024, DisplayHeight = 1792 }
        );

        // ── Style Presets seed data (25 styles) ──
        modelBuilder.Entity<StylePreset>().HasData(
            // Artistic (8)
            new StylePreset { Id = 1, Name = "Anime", Description = "Japanese anime illustration style", PromptTemplate = "Transform into high-quality anime illustration style. Clean cel-shaded coloring with soft gradients. Large expressive eyes. Fine detailed linework. Vibrant colors. Lush painted background. Professional anime production quality, Studio Ghibli inspired.", Category = "Artistic", IconEmoji = "\U0001F38C", AccentColor = "#E91E63", IsActive = true, SortOrder = 1 },
            new StylePreset { Id = 2, Name = "Manga", Description = "Black & white Japanese manga", PromptTemplate = "Transform into Japanese manga style. Clean precise ink linework. Screentone shading. Expressive eyes and dynamic poses. Black and white with dramatic contrast. Professional manga quality.", Category = "Artistic", IconEmoji = "\U0001F4D6", AccentColor = "#263238", IsActive = true, SortOrder = 2 },
            new StylePreset { Id = 3, Name = "Oil Painting", Description = "Classical oil painting on canvas", PromptTemplate = "Transform into a classical oil painting. Rich impasto brushstrokes, visible canvas texture. Deep saturated colors with luminous glazing technique. Dramatic chiaroscuro lighting. Museum-quality fine art.", Category = "Artistic", IconEmoji = "\U0001F5BC\uFE0F", AccentColor = "#8D6E63", IsActive = true, SortOrder = 3 },
            new StylePreset { Id = 4, Name = "Watercolor", Description = "Delicate watercolor painting", PromptTemplate = "Transform into a delicate watercolor painting. Soft translucent washes of color. Wet-on-wet bleeding edges. Visible paper grain. Light ethereal quality with gentle color transitions. Professional illustration.", Category = "Artistic", IconEmoji = "\U0001F3A8", AccentColor = "#42A5F5", IsActive = true, SortOrder = 4 },
            new StylePreset { Id = 5, Name = "Charcoal", Description = "Expressive charcoal drawing", PromptTemplate = "Transform into an expressive charcoal drawing. Rich dark values, dramatic contrast. Smudged soft edges with sharp accents. Textured paper grain visible. Raw, emotional artistic quality.", Category = "Artistic", IconEmoji = "\U0001F5A4", AccentColor = "#37474F", IsActive = true, SortOrder = 5 },
            new StylePreset { Id = 6, Name = "Impressionist", Description = "Monet/Renoir painting style", PromptTemplate = "Transform into an Impressionist painting in the style of Monet and Renoir. Loose visible brushstrokes capturing light and movement. Vibrant dappled colors, soft focus on forms. Emphasis on natural light, atmospheric effects, and fleeting moments. En plein air quality with warm luminous palette.", Category = "Artistic", IconEmoji = "\U0001F33B", AccentColor = "#7E57C2", IsActive = true, SortOrder = 6 },
            new StylePreset { Id = 7, Name = "Vintage/Retro", Description = "Faded retro photograph look", PromptTemplate = "Transform into a vintage retro photograph style. Faded warm color tones with slight sepia wash. Visible film grain and subtle light leaks. Soft vignetting around edges. Nostalgic 1970s Kodachrome aesthetic. Slightly desaturated with warm amber highlights and muted shadows.", Category = "Artistic", IconEmoji = "\U0001F4F7", AccentColor = "#FF8F00", IsActive = true, SortOrder = 7 },
            new StylePreset { Id = 8, Name = "Flower Petals", Description = "Portrait made of flower petals", PromptTemplate = "Transform into a stunning portrait composed entirely of flower petals and botanical elements. The subject's features are recreated using carefully arranged rose petals, cherry blossoms, lavender, and wildflowers. Delicate floral textures form the hair, skin, and clothing. Surrounded by floating petals and soft natural light. Ethereal, romantic botanical art quality.", Category = "Artistic", IconEmoji = "\U0001F338", AccentColor = "#F06292", IsActive = true, SortOrder = 8 },

            // Fun (7)
            new StylePreset { Id = 9, Name = "Cartoon", Description = "Vibrant cartoon illustration", PromptTemplate = "Transform into vibrant cartoon illustration style. Bold outlines, flat vibrant colors, exaggerated proportions. Playful and dynamic. Clean vector-like rendering. Professional animation quality.", Category = "Fun", IconEmoji = "\U0001F3AA", AccentColor = "#FF5722", IsActive = true, SortOrder = 9 },
            new StylePreset { Id = 10, Name = "Pop Art", Description = "Bold Warhol-style pop art", PromptTemplate = "Transform into bold pop art style. Bright saturated primary colors, halftone dot patterns, thick black outlines. Andy Warhol / Roy Lichtenstein inspired. High contrast, graphic, iconic.", Category = "Fun", IconEmoji = "\U0001F4A5", AccentColor = "#F44336", IsActive = true, SortOrder = 10 },
            new StylePreset { Id = 11, Name = "Pixel Art", Description = "Retro 16-bit pixel style", PromptTemplate = "Transform into retro pixel art style. Clean pixel grid, limited color palette, 16-bit aesthetic. Sharp pixels, no anti-aliasing. Nostalgic video game art quality.", Category = "Fun", IconEmoji = "\U0001F579\uFE0F", AccentColor = "#4CAF50", IsActive = true, SortOrder = 11 },
            new StylePreset { Id = 12, Name = "Comic", Description = "Comic book illustration", PromptTemplate = "Transform into comic book illustration. Bold ink outlines, dynamic shading with halftone dots. Vibrant flat colors. Action-pose energy. Professional Marvel/DC quality linework.", Category = "Fun", IconEmoji = "\U0001F4AC", AccentColor = "#2196F3", IsActive = true, SortOrder = 12 },
            new StylePreset { Id = 13, Name = "Clay/Claymation", Description = "Charming claymation style", PromptTemplate = "Transform into a charming clay/claymation style. Soft rounded shapes, visible fingerprint textures. Warm handmade quality. Stop-motion aesthetic. Miniature diorama feeling.", Category = "Fun", IconEmoji = "\U0001F3FA", AccentColor = "#FF9800", IsActive = true, SortOrder = 13 },
            new StylePreset { Id = 14, Name = "Caricature", Description = "Exaggerated fun caricature", PromptTemplate = "Transform into a fun exaggerated caricature. Amplify distinctive facial features — larger eyes, exaggerated nose or chin, oversized head on smaller body. Bold expressive lines, vibrant colors. Humorous and flattering with professional caricature artist quality. Maintain recognizable likeness while exaggerating proportions.", Category = "Fun", IconEmoji = "\U0001F61C", AccentColor = "#E040FB", IsActive = true, SortOrder = 14 },
            new StylePreset { Id = 15, Name = "Neon Glow", Description = "Glowing neon light style", PromptTemplate = "Transform into a striking neon glow style. The subject outlined and filled with bright neon light tubes — electric blue, hot pink, purple, and cyan. Dark black background to maximize glow effect. Light bloom and lens flare around neon edges. Cyberpunk aesthetic with luminous glowing contours. Dramatic volumetric light rays.", Category = "Fun", IconEmoji = "\U0001F4A1", AccentColor = "#00E5FF", IsActive = true, SortOrder = 15 },

            // Professional (6)
            new StylePreset { Id = 16, Name = "Sketch", Description = "Detailed pencil sketch", PromptTemplate = "Transform into a detailed pencil sketch. Fine graphite linework on white paper. Cross-hatching for shadows. Detailed texture work. Professional illustration quality. Clean precise lines with artistic shading.", Category = "Professional", IconEmoji = "\u270F\uFE0F", AccentColor = "#78909C", IsActive = true, SortOrder = 16 },
            new StylePreset { Id = 17, Name = "3D Render", Description = "Hyper-detailed 3D render", PromptTemplate = "Transform into a hyper-detailed 3D render. Smooth subsurface scattering on skin. Ray-traced reflections and global illumination. Octane/Blender quality. Sharp geometric detail. Photorealistic material textures.", Category = "Professional", IconEmoji = "\U0001F3B2", AccentColor = "#5C6BC0", IsActive = true, SortOrder = 17 },
            new StylePreset { Id = 18, Name = "Vector Art", Description = "Clean geometric vector style", PromptTemplate = "Transform into clean vector illustration. Flat colors, smooth curves, geometric precision. Bold simplified shapes. Modern graphic design aesthetic. Print-ready quality.", Category = "Professional", IconEmoji = "\U0001F4D0", AccentColor = "#26A69A", IsActive = true, SortOrder = 18 },
            new StylePreset { Id = 19, Name = "Line Art", Description = "Elegant ink line illustration", PromptTemplate = "Transform into elegant line art illustration. Single weight or varying line thickness. No fill colors, just expressive linework. Clean, minimal, sophisticated. Professional ink illustration.", Category = "Professional", IconEmoji = "\U0001F4DD", AccentColor = "#546E7A", IsActive = true, SortOrder = 19 },
            new StylePreset { Id = 20, Name = "Gold/Metallic", Description = "Golden statue effect", PromptTemplate = "Transform into a stunning golden metallic statue. The subject rendered as a polished gold sculpture with realistic metallic reflections and specular highlights. Rich 24-karat gold surface with subtle variations in tone. Dramatic studio lighting emphasizing the metallic sheen. Museum pedestal display quality. Luxurious and prestigious.", Category = "Professional", IconEmoji = "\U0001F451", AccentColor = "#FFD600", IsActive = true, SortOrder = 20 },
            new StylePreset { Id = 21, Name = "Paparazzi", Description = "Celebrity red carpet shot", PromptTemplate = "Transform into a glamorous celebrity paparazzi photograph. Dramatic camera flash lighting with slight overexposure. Red carpet or premiere event backdrop with bokeh lights. The subject looking camera-ready with star quality presence. High-fashion editorial quality. Slight motion blur suggesting a candid caught moment. Magazine cover worthy.", Category = "Professional", IconEmoji = "\U0001F4F8", AccentColor = "#D32F2F", IsActive = true, SortOrder = 21 },

            // Abstract (4)
            new StylePreset { Id = 22, Name = "Stained Glass", Description = "Cathedral stained glass design", PromptTemplate = "Transform into a stained glass window design. Bold black lead lines, jewel-toned translucent colors. Geometric segmentation. Cathedral-quality craftsmanship. Light shining through colored glass.", Category = "Abstract", IconEmoji = "\U0001FA9F", AccentColor = "#AB47BC", IsActive = true, SortOrder = 22 },
            new StylePreset { Id = 23, Name = "Low Poly", Description = "Geometric faceted art", PromptTemplate = "Transform into low-poly 3D art style. Geometric faceted surfaces, minimal polygon count. Clean flat-shaded triangles. Modern minimalist aesthetic. Vibrant gradient colors across faces.", Category = "Abstract", IconEmoji = "\U0001F537", AccentColor = "#1E88E5", IsActive = true, SortOrder = 23 },
            new StylePreset { Id = 24, Name = "Clouds/Dreamy", Description = "Ethereal cloud portrait", PromptTemplate = "Transform into an ethereal dreamy cloud artwork. The subject's form composed of soft billowing clouds and mist against a pastel sky. Wispy cirrus clouds trace the features and contours. Golden hour sunlight illuminating the cloud formations. Heavenly, serene, and otherworldly atmosphere. Soft focus with luminous edges.", Category = "Abstract", IconEmoji = "\u2601\uFE0F", AccentColor = "#90CAF9", IsActive = true, SortOrder = 24 },
            new StylePreset { Id = 25, Name = "Surreal", Description = "Dali-inspired surrealism", PromptTemplate = "Transform into a surrealist artwork inspired by Salvador Dali. Dreamlike impossible geometry and melting forms. The subject existing in a bizarre landscape with floating objects, distorted perspectives, and impossible architecture. Rich detailed oil painting technique with hyperreal textures in an unreal context. Mysterious, thought-provoking, visually stunning.", Category = "Abstract", IconEmoji = "\U0001F300", AccentColor = "#FF6F00", IsActive = true, SortOrder = 25 }
        );
    }
}
