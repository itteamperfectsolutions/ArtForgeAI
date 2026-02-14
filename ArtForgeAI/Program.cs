using ArtForgeAI.Components;
using ArtForgeAI.Data;
using ArtForgeAI.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Enable detailed circuit errors in development
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(
    options => options.DetailedErrors = builder.Environment.IsDevelopment());

// Increase SignalR buffer for file uploads (default 32KB is too small for images)
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = null);

// Database — use factory to avoid DbContext concurrency issues in Blazor Server
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// OpenAI configuration
builder.Services.Configure<OpenAiOptions>(
    builder.Configuration.GetSection(OpenAiOptions.SectionName));

// Gemini configuration
builder.Services.Configure<GeminiOptions>(
    builder.Configuration.GetSection(GeminiOptions.SectionName));

// Replicate configuration
builder.Services.Configure<ReplicateOptions>(
    builder.Configuration.GetSection(ReplicateOptions.SectionName));

// HTTP client for downloading images
builder.Services.AddHttpClient();

// Local ONNX background removal (singleton — model loaded once)
builder.Services.AddSingleton<IBackgroundRemovalService, OnnxBackgroundRemovalService>();

// Local ONNX image enhancement — Real-ESRGAN 4x upscale (singleton — model loaded once)
builder.Services.AddSingleton<IImageEnhancerService, OnnxImageEnhancerService>();

// Local ONNX color enhancement — SCI illumination (singleton — model loaded once)
builder.Services.AddSingleton<IColorEnhancementService, OnnxColorEnhancementService>();

// Image size master CRUD
builder.Services.AddScoped<IImageSizeMasterService, ImageSizeMasterService>();

// Style preset CRUD
builder.Services.AddScoped<IStylePresetService, StylePresetService>();

// Application services
builder.Services.AddScoped<IGeminiImageService, GeminiImageService>();
builder.Services.AddScoped<IReplicateImageService, ReplicateImageService>();
builder.Services.AddScoped<IImageStorageService, ImageStorageService>();
builder.Services.AddScoped<IPromptEnhancerService, PromptEnhancerService>();
builder.Services.AddScoped<IGenerationHistoryService, GenerationHistoryService>();
builder.Services.AddScoped<IImageGenerationService, ImageGenerationService>();

var app = builder.Build();

// Ensure database is created + add missing tables for existing databases
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated won't add new tables to an existing DB, so create ImageSizeMasters if missing
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ImageSizeMasters')
            BEGIN
                CREATE TABLE ImageSizeMasters (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(50) NOT NULL,
                    Width INT NOT NULL,
                    Height INT NOT NULL,
                    IsActive BIT NOT NULL DEFAULT 1,
                    SortOrder INT NOT NULL DEFAULT 0,
                    Unit NVARCHAR(5) NOT NULL DEFAULT 'px',
                    DisplayWidth FLOAT NOT NULL DEFAULT 0,
                    DisplayHeight FLOAT NOT NULL DEFAULT 0
                );
                SET IDENTITY_INSERT ImageSizeMasters ON;
                INSERT INTO ImageSizeMasters (Id, Name, Width, Height, IsActive, SortOrder, Unit, DisplayWidth, DisplayHeight)
                VALUES (1, 'Square', 1024, 1024, 1, 1, 'px', 1024, 1024),
                       (2, 'Landscape', 1792, 1024, 1, 2, 'px', 1792, 1024),
                       (3, 'Portrait', 1024, 1792, 1, 3, 'px', 1024, 1792);
                SET IDENTITY_INSERT ImageSizeMasters OFF;
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "ImageSizeMasters table creation failed (non-fatal)");
    }

    // Add Unit/DisplayWidth/DisplayHeight columns if missing (separate call so table creation errors don't block this)
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ImageSizeMasters') AND name = 'Unit')
                ALTER TABLE ImageSizeMasters ADD Unit NVARCHAR(5) NOT NULL DEFAULT 'px'");
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ImageSizeMasters') AND name = 'DisplayWidth')
                ALTER TABLE ImageSizeMasters ADD DisplayWidth FLOAT NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ImageSizeMasters') AND name = 'DisplayHeight')
                ALTER TABLE ImageSizeMasters ADD DisplayHeight FLOAT NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE ImageSizeMasters SET DisplayWidth = Width, DisplayHeight = Height WHERE DisplayWidth = 0");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "ImageSizeMasters column migration failed (non-fatal)");
    }

    // Create StylePresets table if missing (same pattern as ImageSizeMasters)
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'StylePresets')
            BEGIN
                CREATE TABLE StylePresets (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(50) NOT NULL,
                    Description NVARCHAR(200) NOT NULL DEFAULT '',
                    PromptTemplate NVARCHAR(MAX) NOT NULL,
                    Category NVARCHAR(50) NOT NULL,
                    IconEmoji NVARCHAR(10) NOT NULL DEFAULT '',
                    AccentColor NVARCHAR(10) NULL,
                    IsActive BIT NOT NULL DEFAULT 1,
                    SortOrder INT NOT NULL DEFAULT 0
                );
                SET IDENTITY_INSERT StylePresets ON;
                INSERT INTO StylePresets (Id, Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (1, 'Anime', 'Japanese anime illustration style', 'Transform into high-quality anime illustration style. Clean cel-shaded coloring with soft gradients. Large expressive eyes. Fine detailed linework. Vibrant colors. Lush painted background. Professional anime production quality, Studio Ghibli inspired.', 'Artistic', N'🎌', '#E91E63', 1, 1),
                (2, 'Manga', 'Black & white Japanese manga', 'Transform into Japanese manga style. Clean precise ink linework. Screentone shading. Expressive eyes and dynamic poses. Black and white with dramatic contrast. Professional manga quality.', 'Artistic', N'📖', '#263238', 1, 2),
                (3, 'Oil Painting', 'Classical oil painting on canvas', 'Transform into a classical oil painting. Rich impasto brushstrokes, visible canvas texture. Deep saturated colors with luminous glazing technique. Dramatic chiaroscuro lighting. Museum-quality fine art.', 'Artistic', N'🖼️', '#8D6E63', 1, 3),
                (4, 'Watercolor', 'Delicate watercolor painting', 'Transform into a delicate watercolor painting. Soft translucent washes of color. Wet-on-wet bleeding edges. Visible paper grain. Light ethereal quality with gentle color transitions. Professional illustration.', 'Artistic', N'🎨', '#42A5F5', 1, 4),
                (5, 'Charcoal', 'Expressive charcoal drawing', 'Transform into an expressive charcoal drawing. Rich dark values, dramatic contrast. Smudged soft edges with sharp accents. Textured paper grain visible. Raw, emotional artistic quality.', 'Artistic', N'🖤', '#37474F', 1, 5),
                (6, 'Impressionist', 'Monet/Renoir painting style', 'Transform into an Impressionist painting in the style of Monet and Renoir. Loose visible brushstrokes capturing light and movement. Vibrant dappled colors, soft focus on forms. Emphasis on natural light, atmospheric effects, and fleeting moments. En plein air quality with warm luminous palette.', 'Artistic', N'🌻', '#7E57C2', 1, 6),
                (7, 'Vintage/Retro', 'Faded retro photograph look', 'Transform into a vintage retro photograph style. Faded warm color tones with slight sepia wash. Visible film grain and subtle light leaks. Soft vignetting around edges. Nostalgic 1970s Kodachrome aesthetic. Slightly desaturated with warm amber highlights and muted shadows.', 'Artistic', N'📷', '#FF8F00', 1, 7),
                (8, 'Flower Petals', 'Portrait made of flower petals', 'Transform into a stunning portrait composed entirely of flower petals and botanical elements. The subject features are recreated using carefully arranged rose petals, cherry blossoms, lavender, and wildflowers. Delicate floral textures form the hair, skin, and clothing. Surrounded by floating petals and soft natural light. Ethereal, romantic botanical art quality.', 'Artistic', N'🌸', '#F06292', 1, 8),
                (9, 'Cartoon', 'Vibrant cartoon illustration', 'Transform into vibrant cartoon illustration style. Bold outlines, flat vibrant colors, exaggerated proportions. Playful and dynamic. Clean vector-like rendering. Professional animation quality.', 'Fun', N'🎪', '#FF5722', 1, 9),
                (10, 'Pop Art', 'Bold Warhol-style pop art', 'Transform into bold pop art style. Bright saturated primary colors, halftone dot patterns, thick black outlines. Andy Warhol / Roy Lichtenstein inspired. High contrast, graphic, iconic.', 'Fun', N'💥', '#F44336', 1, 10),
                (11, 'Pixel Art', 'Retro 16-bit pixel style', 'Transform into retro pixel art style. Clean pixel grid, limited color palette, 16-bit aesthetic. Sharp pixels, no anti-aliasing. Nostalgic video game art quality.', 'Fun', N'🕹️', '#4CAF50', 1, 11),
                (12, 'Comic', 'Comic book illustration', 'Transform into comic book illustration. Bold ink outlines, dynamic shading with halftone dots. Vibrant flat colors. Action-pose energy. Professional Marvel/DC quality linework.', 'Fun', N'💬', '#2196F3', 1, 12),
                (13, 'Clay/Claymation', 'Charming claymation style', 'Transform into a charming clay/claymation style. Soft rounded shapes, visible fingerprint textures. Warm handmade quality. Stop-motion aesthetic. Miniature diorama feeling.', 'Fun', N'🏺', '#FF9800', 1, 13),
                (14, 'Caricature', 'Exaggerated fun caricature', 'Transform into a fun exaggerated caricature. Amplify distinctive facial features - larger eyes, exaggerated nose or chin, oversized head on smaller body. Bold expressive lines, vibrant colors. Humorous and flattering with professional caricature artist quality. Maintain recognizable likeness while exaggerating proportions.', 'Fun', N'😜', '#E040FB', 1, 14),
                (15, 'Neon Glow', 'Glowing neon light style', 'Transform into a striking neon glow style. The subject outlined and filled with bright neon light tubes - electric blue, hot pink, purple, and cyan. Dark black background to maximize glow effect. Light bloom and lens flare around neon edges. Cyberpunk aesthetic with luminous glowing contours. Dramatic volumetric light rays.', 'Fun', N'💡', '#00E5FF', 1, 15),
                (16, 'Sketch', 'Detailed pencil sketch', 'Transform into a detailed pencil sketch. Fine graphite linework on white paper. Cross-hatching for shadows. Detailed texture work. Professional illustration quality. Clean precise lines with artistic shading.', 'Professional', N'✏️', '#78909C', 1, 16),
                (17, '3D Render', 'Hyper-detailed 3D render', 'Transform into a hyper-detailed 3D render. Smooth subsurface scattering on skin. Ray-traced reflections and global illumination. Octane/Blender quality. Sharp geometric detail. Photorealistic material textures.', 'Professional', N'🎲', '#5C6BC0', 1, 17),
                (18, 'Vector Art', 'Clean geometric vector style', 'Transform into clean vector illustration. Flat colors, smooth curves, geometric precision. Bold simplified shapes. Modern graphic design aesthetic. Print-ready quality.', 'Professional', N'📐', '#26A69A', 1, 18),
                (19, 'Line Art', 'Elegant ink line illustration', 'Transform into elegant line art illustration. Single weight or varying line thickness. No fill colors, just expressive linework. Clean, minimal, sophisticated. Professional ink illustration.', 'Professional', N'📝', '#546E7A', 1, 19),
                (20, 'Gold/Metallic', 'Golden statue effect', 'Transform into a stunning golden metallic statue. The subject rendered as a polished gold sculpture with realistic metallic reflections and specular highlights. Rich 24-karat gold surface with subtle variations in tone. Dramatic studio lighting emphasizing the metallic sheen. Museum pedestal display quality. Luxurious and prestigious.', 'Professional', N'👑', '#FFD600', 1, 20),
                (21, 'Paparazzi', 'Celebrity red carpet shot', 'Transform into a glamorous celebrity paparazzi photograph. Dramatic camera flash lighting with slight overexposure. Red carpet or premiere event backdrop with bokeh lights. The subject looking camera-ready with star quality presence. High-fashion editorial quality. Slight motion blur suggesting a candid caught moment. Magazine cover worthy.', 'Professional', N'📸', '#D32F2F', 1, 21),
                (22, 'Stained Glass', 'Cathedral stained glass design', 'Transform into a stained glass window design. Bold black lead lines, jewel-toned translucent colors. Geometric segmentation. Cathedral-quality craftsmanship. Light shining through colored glass.', 'Abstract', N'🪟', '#AB47BC', 1, 22),
                (23, 'Low Poly', 'Geometric faceted art', 'Transform into low-poly 3D art style. Geometric faceted surfaces, minimal polygon count. Clean flat-shaded triangles. Modern minimalist aesthetic. Vibrant gradient colors across faces.', 'Abstract', N'🔷', '#1E88E5', 1, 23),
                (24, 'Clouds/Dreamy', 'Ethereal cloud portrait', 'Transform into an ethereal dreamy cloud artwork. The subject form composed of soft billowing clouds and mist against a pastel sky. Wispy cirrus clouds trace the features and contours. Golden hour sunlight illuminating the cloud formations. Heavenly, serene, and otherworldly atmosphere. Soft focus with luminous edges.', 'Abstract', N'☁️', '#90CAF9', 1, 24),
                (25, 'Surreal', 'Dali-inspired surrealism', 'Transform into a surrealist artwork inspired by Salvador Dali. Dreamlike impossible geometry and melting forms. The subject existing in a bizarre landscape with floating objects, distorted perspectives, and impossible architecture. Rich detailed oil painting technique with hyperreal textures in an unreal context. Mysterious, thought-provoking, visually stunning.', 'Abstract', N'🌀', '#FF6F00', 1, 25);
                SET IDENTITY_INSERT StylePresets OFF;
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "StylePresets table creation failed (non-fatal)");
    }

    // Seed AP & Telangana regional style presets (auto-assigned IDs, guarded by Category)
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Category = 'Regional')
            BEGIN
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Kalamkari', 'Traditional Kalamkari hand-painted textile art', 'Transform into traditional Kalamkari art style from Andhra Pradesh. Hand-painted textile aesthetic using natural earth-tone dyes — deep indigo, madder red, iron black, turmeric yellow. Intricate pen-drawn outlines with fine botanical motifs, mythological figures, and paisley patterns. Visible fabric texture with block-printed quality. Rich storytelling composition in ancient Indian textile art tradition.', 'Regional', N'🎨', '#8B4513', 1, 26),
                ('Cheriyal Scroll', 'Telangana Cheriyal scroll narrative painting', 'Transform into Cheriyal scroll painting style from Telangana. Bold narrative art with vivid red background. Flat bright colors — vermillion, yellow ochre, green, white. Simplified expressive human figures with large eyes and dynamic poses. Sequential storytelling composition. Folk art tradition with thick outlines and decorative borders. Traditional nakashi art quality.', 'Regional', N'📜', '#D84315', 1, 27),
                ('Kondapalli Toys', 'Colorful Kondapalli wooden toy style', 'Transform into the style of Kondapalli wooden toys from Andhra Pradesh. Bright cheerful colors — vivid red, yellow, green, blue painted on soft wood grain texture. Rounded simplified forms with charming naive proportions. Smooth lacquered finish with visible brush marks. Folk craft aesthetic with playful, whimsical character. Miniature diorama quality.', 'Regional', N'🪆', '#FFB300', 1, 28),
                ('Bidri Ware', 'Bidar metalwork with silver inlay patterns', 'Transform into Bidri metalwork art style from Deccan region. Dark gunmetal black oxidized background with intricate silver inlay patterns. Geometric and floral arabesques — Persian-influenced vine scrollwork, poppy flowers, and chevron borders. Metallic sheen contrast between matte black zinc and bright silver. Luxurious handcrafted Deccani artisan quality.', 'Regional', N'⚱️', '#37474F', 1, 29),
                ('Pochampally Ikat', 'Pochampally ikat weaving geometric patterns', 'Transform into Pochampally ikat textile pattern style from Telangana. Geometric diamond and zigzag patterns with characteristic ikat blur at edges. Rich jewel-tone colors — deep purple, magenta, teal, saffron. Woven fabric texture with visible thread grain. Traditional tie-dye resist pattern with symmetrical repeating motifs. Handloom textile art quality.', 'Regional', N'🧵', '#7B1FA2', 1, 30),
                ('Nirmal Painting', 'Nirmal art gold-leaf painting tradition', 'Transform into Nirmal painting style from Telangana. Rich gold-leaf background with detailed figures and landscapes. Warm palette of deep reds, greens, and gold. Soft shading technique with luminous glow effect. Hindu mythological and nature themes. Deccan miniature painting influence with ornate decorative borders. Museum-quality traditional art.', 'Regional', N'🖌️', '#C62828', 1, 31),
                ('Tirupati Golden', 'Tirumala temple golden divine aesthetic', 'Transform into a divine golden temple art style inspired by Tirumala Tirupati. Radiant gold leaf background with sacred aura. Rich ornamental details — temple gopuram carvings, lotus motifs, divine halo effects. Warm golden-amber light suffusing the entire composition. Sacred, devotional, majestic atmosphere. Traditional South Indian temple art aesthetic.', 'Regional', N'🛕', '#FFD600', 1, 32),
                ('Lepakshi Mural', 'Vijayanagara-era Lepakshi temple fresco', 'Transform into Lepakshi temple mural painting style from Andhra Pradesh. Vijayanagara-era fresco aesthetic with earth pigments — red ochre, yellow, black, white on plaster texture. Large graceful figures with elongated eyes and ornate jewelry. Mythological narrative scenes with architectural framing. Ancient wall painting quality with subtle aging patina.', 'Regional', N'🏛️', '#E65100', 1, 33),
                ('Bathukamma', 'Telangana Bathukamma floral festival art', 'Transform into a vibrant Bathukamma festival art style from Telangana. Conical floral tower composition using bright marigold orange, celosia pink, lotus magenta, and tangechi yellow. Circular mandala arrangement of flower layers. Festival celebration energy with women in colorful sarees. Joyful, sacred feminine energy. Vibrant folk art with floral abundance.', 'Regional', N'🌺', '#E91E63', 1, 34),
                ('Bonalu Festival', 'Vibrant Bonalu festival celebration style', 'Transform into vibrant Bonalu festival art style from Telangana. Rich vermillion red and turmeric yellow dominant palette. Decorated bonam pots with elaborate kolam designs. Festive energy with procession celebration mood. Traditional folk art elements — mirror work, rangoli borders, goddess Mahankali motifs. Bold, energetic, devotional folk art quality.', 'Regional', N'🪔', '#F44336', 1, 35),
                ('Kuchipudi Dance', 'Classical Kuchipudi dance-pose art', 'Transform into classical Kuchipudi dance art style from Andhra Pradesh. Graceful bharatanatyam-adjacent pose with expressive mudra hand gestures. Rich silk costume details in jewel tones. Traditional temple jewelry — gold jhumkas, maang tikka, waist belt. Dynamic frozen-motion capture with flowing fabric. Bronze sculpture-like quality with warm dramatic stage lighting.', 'Regional', N'💃', '#AD1457', 1, 36),
                ('Sankranti Rangoli', 'Makar Sankranti muggu/rangoli patterns', 'Transform into Sankranti muggu rangoli art style from Andhra Pradesh. White rice flour pattern on earthy red ground. Intricate geometric kolam with dot-grid symmetry — lotus flowers, peacocks, and sun motifs. Clean precise mathematical line patterns radiating outward. Festival morning freshness. Traditional South Indian floor art with vibrant color-filled sections.', 'Regional', N'🌀', '#FF6F00', 1, 37),
                ('Tollywood Poster', 'Telugu cinema dramatic poster style', 'Transform into dramatic Telugu cinema poster style. Bold high-contrast hero lighting with intense color grading — teal shadows, orange highlights. Dramatic low-angle composition with dynamic text-friendly negative space. Cinematic depth of field with particle effects. Mass hero energy with powerful stance. Professional movie marketing art quality.', 'Regional', N'🎬', '#1565C0', 1, 38),
                ('Charminar Heritage', 'Hyderabad Charminar architectural style', 'Transform into Hyderabad heritage architectural art style. Indo-Islamic Charminar and Qutb Shahi architecture aesthetic. Detailed stone archway patterns with geometric Islamic jali screens. Warm sandstone and pearl-white marble tones. Mughal miniature perspective with decorative floral borders. Historical heritage illustration with Deccani cultural elegance.', 'Regional', N'🕌', '#546E7A', 1, 39),
                ('Mangalagiri Fabric', 'Mangalagiri handloom cotton weave texture', 'Transform into Mangalagiri handloom textile art style from Andhra Pradesh. Fine cotton weave texture with characteristic nizam border pattern in gold zari. Clean geometric stripes and checks in natural cotton white with vibrant accent colors — mango yellow, parrot green, temple red. Crisp handloom quality with visible warp-weft structure. Elegant simplicity.', 'Regional', N'👘', '#2E7D32', 1, 40),
                ('Etikoppaka Lacquer', 'Etikoppaka lacquer-turned toy art', 'Transform into Etikoppaka lacquer toy art style from Andhra Pradesh. Bright vegetable-dye colors — lac red, turmeric yellow, indigo blue, leaf green. Smooth turned-wood rounded forms with concentric ring patterns. Glossy lacquer finish with warm wood undertones. Playful folk craft aesthetic with simplified charming proportions. Traditional lathe-turned toy quality.', 'Regional', N'🎎', '#EF6C00', 1, 41),
                ('Godavari Landscape', 'River Godavari natural scenic painting', 'Transform into a scenic River Godavari landscape painting. Lush tropical South Indian riverbank with coconut palms and paddy fields. Warm golden morning light reflecting on wide river waters. Traditional fishing boats and papyrus reeds. Rich green foliage with misty hills in background. Peaceful rural Andhra Pradesh atmosphere. Impressionistic plein-air painting quality.', 'Regional', N'🌊', '#00838F', 1, 42),
                ('Perini Warrior', 'Perini Sivatandavam warrior dance art', 'Transform into Perini Sivatandavam warrior dance art style from Telangana. Powerful masculine dance pose with dramatic warrior energy. Bronze sculpture aesthetic with dynamic frozen motion. Traditional warrior costume with ankle bells and dhoti. Kakatiya dynasty era aesthetic. Deep dramatic lighting emphasizing muscular form and fierce expression. Ancient temple relief sculpture quality.', 'Regional', N'⚔️', '#4E342E', 1, 43),
                ('Dharmavaram Silk', 'Dharmavaram pattu saree rich silk art', 'Transform into luxurious Dharmavaram pattu silk saree art style. Rich handwoven silk with heavy gold zari brocade borders. Deep jewel colors — temple red, royal purple, peacock blue with contrasting pallu. Intricate traditional motifs — temple towers, mango buttas, peacock designs in metallic gold. Lustrous silk sheen with dramatic drape folds. Bridal elegance quality.', 'Regional', N'🥻', '#880E4F', 1, 44),
                ('Deccan Miniature', 'Deccan school miniature painting style', 'Transform into Deccan school miniature painting style. Deccani-Mughal fusion aesthetic with rich palette — gold, deep green, lapis blue, coral. Detailed figure painting with ornate costumes and architecture. Flat perspective with decorative floral borders. Persian-influenced faces with Indian features. Golconda and Hyderabad court painting tradition. Manuscript illumination quality.', 'Regional', N'🎴', '#5D4037', 1, 45),
                ('Araku Valley Nature', 'Araku Valley tribal nature landscape', 'Transform into Araku Valley tribal nature art style from Andhra Pradesh. Lush Eastern Ghats coffee plantation landscape with misty blue-green mountains. Tribal Dhimsa dance silhouettes and Borra Caves rock formations. Rich verdant palette with morning mist atmosphere. Indigenous tribal geometric patterns as decorative border elements. Serene hill-station landscape with waterfall elements.', 'Regional', N'🏔️', '#1B5E20', 1, 46);
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Regional StylePresets seeding failed (non-fatal)");
    }

    // Add ThumbnailPath column to StylePresets if missing
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('StylePresets') AND name = 'ThumbnailPath')
                ALTER TABLE StylePresets ADD ThumbnailPath NVARCHAR(500) NULL");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "StylePresets ThumbnailPath column migration failed (non-fatal)");
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
