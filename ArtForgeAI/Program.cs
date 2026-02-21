using System.Threading.RateLimiting;
using ArtForgeAI.Components;
using ArtForgeAI.Data;
using ArtForgeAI.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Enable detailed circuit errors in development
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(
    options => options.DetailedErrors = builder.Environment.IsDevelopment());

// Increase SignalR buffer for file uploads (cap at 10MB to match upload limits)
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 10 * 1024 * 1024);

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
builder.Services.AddScoped<ICinematicProfileService, CinematicProfileService>();

// Rate limiting: 100 requests per minute per IP
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));
});

var app = builder.Build();

// Ensure database is created + add missing tables for existing databases
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();

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

    // Seed 20x30 and 30x40 print sizes if missing
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM ImageSizeMasters WHERE Name = '20x30')
                INSERT INTO ImageSizeMasters (Name, Width, Height, IsActive, SortOrder, Unit, DisplayWidth, DisplayHeight)
                VALUES ('20x30', 1440, 2160, 1, 4, 'in', 20, 30)");
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM ImageSizeMasters WHERE Name = '30x40')
                INSERT INTO ImageSizeMasters (Name, Width, Height, IsActive, SortOrder, Unit, DisplayWidth, DisplayHeight)
                VALUES ('30x40', 1536, 2048, 1, 5, 'in', 30, 40)");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Additional print sizes seeding failed (non-fatal)");
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
                (25, 'Surreal', 'Dali-inspired surrealism', 'Transform into a surrealist artwork inspired by Salvador Dali. Dreamlike impossible geometry and melting forms. The subject existing in a bizarre landscape with floating objects, distorted perspectives, and impossible architecture. Rich detailed oil painting technique with hyperreal textures in an unreal context. Mysterious, thought-provoking, visually stunning.', 'Abstract', N'🌀', '#FF6F00', 1, 25),
                (26, 'Cinematic B&W Profile', 'Dramatic B&W layered side-profile portrait', 'Identify every person in the source photo. The output MUST contain the EXACT same number of people — do NOT skip or merge anyone. CRITICAL: Each person has a UNIQUE face — different age, gender, face shape, nose, jawline, forehead, hair. You MUST preserve each person''s individual facial identity exactly. Do NOT make them look alike. Render each person as a dramatic side profile facing left. Stack all profiles vertically against a pure black background, largest at top to smallest at bottom, slightly overlapping. Each face must look exactly like that specific person from the source — a viewer should be able to identify who is who. Black and white high-contrast monochrome. Dramatic rim lighting from one side. Deep blacks, bright highlights. Cinematic editorial portrait.', 'Professional', N'🎞️', '#212121', 1, 22);
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

    // Seed trending 2025-2026 style presets (guarded individually by Name)
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            -- ============================================================
            -- TRENDING 2025-2026: ARTISTIC (SortOrder 48-54)
            -- ============================================================
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Ghibli Dreamscape')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Ghibli Dreamscape', 'Studio Ghibli-inspired dreamy anime landscape',
                 'Transform into a breathtaking Studio Ghibli-inspired dreamscape. Soft hand-painted watercolor backgrounds with lush rolling green hills, towering cumulus clouds, and golden sunlight filtering through. Character rendered in Ghibli''s signature warm, gentle anime style — simple expressive features, soft rounded forms, natural earthy color palette. Hayao Miyazaki''s attention to wind movement in hair and clothing. Whimsical environmental details — wildflowers, butterflies, distant European-style cottages. Warm nostalgic atmosphere with the magical realism that defines Ghibli. My Neighbor Totoro and Spirited Away visual quality.',
                 'Artistic', N'🏡', '#66BB6A', 1, 48);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Dark Academia Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Dark Academia Portrait', 'Moody intellectual dark academia aesthetic',
                 'Transform into a dark academia portrait aesthetic. Rich mahogany and deep brown tones with warm candlelight illumination. Subject styled in classic scholarly attire — tweed blazer, wool vest, dark knit. Surrounded by leather-bound books, antique manuscripts, and classical architecture. Oil painting quality with Old Masters lighting — dramatic Rembrandt-style chiaroscuro. Gothic university library or Oxford study setting. Muted earth-tone palette: burnt umber, forest green, burgundy, aged gold. Atmospheric dust particles caught in light beams. Intellectual, mysterious, timeless scholarly elegance.',
                 'Artistic', N'📚', '#4E342E', 1, 49);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Y2K Cyber Glam')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Y2K Cyber Glam', 'Early 2000s futuristic cyber glamour',
                 'Transform into Y2K cyber glam aesthetic. Glossy metallic surfaces, iridescent holographic textures, and chrome reflections. Hot pink, electric blue, silver, and neon purple color palette. Futuristic early-2000s fashion — butterfly clips, tinted sunglasses, metallic fabrics. Matrix-inspired digital rain elements mixed with pop-princess sparkle. Glossy lip-gloss sheen, bedazzled rhinestone details, and space-age accessories. Studio lighting with colored gel filters creating vibrant shadows. Clean digital rendering with high-gloss finish. Nostalgic Y2K maximalism meets cyberpunk edge.',
                 'Artistic', N'💿', '#E040FB', 1, 50);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Ethereal Glow Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Ethereal Glow Portrait', 'Soft luminous glowing portrait with dreamy light',
                 'Transform into an ethereal glowing portrait. Soft diffused luminous light emanating from within and around the subject. Warm golden and pearl-white glow creating a heavenly halo effect. Delicate light particles and bokeh floating in the atmosphere. Skin rendered with soft inner radiance and subtle translucency. Pastel warm tones — soft gold, blush pink, lavender, cream. Hair catching backlight with individual strand detail. Dreamy soft-focus background with lens flare accents. Fantasy portrait photography quality with professional beauty lighting. Angelic, serene, otherworldly beauty.',
                 'Artistic', N'✨', '#FFD54F', 1, 51);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Cottagecore Fantasy')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Cottagecore Fantasy', 'Idyllic pastoral cottagecore aesthetic',
                 'Transform into a charming cottagecore fantasy aesthetic. Soft natural lighting through a country window with lace curtains. Subject surrounded by wildflowers, dried herbs, homemade bread, and vintage crockery. Warm pastoral color palette — sage green, dusty rose, cream, lavender, wheat gold. Hand-embroidered textures and linen fabrics. Quaint English countryside cottage interior or flower garden setting. Watercolor-meets-oil-painting artistic quality with soft edges. Butterflies, songbirds, and climbing roses as decorative elements. Nostalgic, peaceful, romantically rural. Beatrix Potter storybook illustration quality.',
                 'Artistic', N'🌿', '#A5D6A7', 1, 52);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Dark Fantasy Arcane')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Dark Fantasy Arcane', 'Dark painterly fantasy with magical energy',
                 'Transform into dark fantasy art style inspired by Arcane and League of Legends. Rich painterly digital art with visible brushstroke texture. Dramatic cinematic lighting — deep shadows with vibrant accent lights in purple, teal, and amber. Magical energy particles and glowing arcane runes floating in the scene. Steampunk-fantasy hybrid aesthetic with gritty urban-fantasy atmosphere. Subject rendered with sharp determined expression and dramatic pose. Color palette: deep indigo, burnt orange, electric violet, gunmetal gray. Atmospheric fog and volumetric lighting. Epic fantasy concept art quality by Riot Games Fortiche studio.',
                 'Artistic', N'🗡️', '#6A1B9A', 1, 53);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Retro Bollywood Saree')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Retro Bollywood Saree', 'Vintage 1960s-70s Bollywood glamour portrait',
                 'Transform into a vintage 1960s-70s Bollywood glamour portrait. Classic golden-era Hindi cinema aesthetic with soft-focus dreamy lens quality. Subject draped in an elegant silk saree with rich jewel-tone colors — deep red, royal blue, emerald, gold border. Traditional Indian jewelry — heavy gold necklace, jhumka earrings, maang tikka, glass bangles. Soft studio lighting with warm amber key light and gentle fill. Hand-tinted photograph quality with slight color saturation. Lush painted backdrop of a Mughal garden or palatial interior. Madhubala and Waheeda Rehman era elegance. Romantic, graceful, timelessly beautiful Indian cinema.',
                 'Artistic', N'🥻', '#FF6F00', 1, 54);

            -- ============================================================
            -- TRENDING 2025-2026: FUN (SortOrder 55-61)
            -- ============================================================
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Action Figure Box')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Action Figure Box', 'Collectible action figure in branded toy packaging',
                 'Transform into a hyper-realistic collectible action figure displayed inside branded toy packaging. The subject rendered as a detailed 3D plastic action figure with visible joint articulation points, glossy plastic skin texture, and molded hair. Packaged inside a clear plastic blister pack mounted on a vibrant cardboard backing with product branding, barcode, and age rating. Include miniature accessories relevant to the subject. Professional toy photography lighting with clean white background. Hasbro and Mattel premium collectible quality. Sharp focus on figure details and packaging design.',
                 'Fun', N'🎁', '#FF7043', 1, 55);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'LEGO Minifigure')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('LEGO Minifigure', 'Classic LEGO minifigure brick character',
                 'Transform into a classic LEGO minifigure character. Blocky cylindrical yellow head with simple printed facial features — dot eyes, curved smile line. C-shaped claw hands in yellow. Characteristic LEGO body proportions — short legs, rectangular torso with printed design details. Smooth ABS plastic texture with subtle mold lines and stud connections. Bright primary LEGO colors. Set against a LEGO baseplate environment with brick elements. Sharp macro photography lighting showing plastic material quality. Official LEGO set box-art quality rendering.',
                 'Fun', N'🧱', '#FDD835', 1, 56);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Chibi 3D Figurine')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Chibi 3D Figurine', 'Adorable chibi vinyl collectible figure',
                 'Transform into an adorable chibi-style 3D vinyl collectible figurine. Oversized head with 3:1 head-to-body ratio, large sparkly eyes, tiny nose, and cute expression. Small stubby body with simplified limbs. Smooth matte vinyl plastic texture with subtle factory sheen. Vibrant candy colors with clean solid paint application. Standing on a small round display base. Nendoroid and Funko Pop crossover aesthetic. Soft studio lighting with gentle shadows. Product photography quality against a clean gradient background. Kawaii Japanese figure collectible quality.',
                 'Fun', N'🧸', '#FF80AB', 1, 57);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Disney Pixar 3D')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Disney Pixar 3D', 'Disney Pixar animated movie character style',
                 'Transform into a Disney Pixar 3D animated movie character. Stylized proportions with large expressive eyes, smooth skin with subsurface scattering, and exaggerated but appealing facial features. Rich detailed clothing textures and materials. Pixar''s signature warm color palette with vibrant saturated hues. Professional studio lighting with rim light and soft ambient fill. Cinematic depth of field with bokeh background. Hair rendered with individual strand detail and natural movement. Emotional expressiveness in pose and facial features. Toy Story and Coco production quality. Rendered in high-quality 3D CGI.',
                 'Fun', N'🏰', '#7E57C2', 1, 58);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Trading Card Hero')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Trading Card Hero', 'Epic fantasy trading card game artwork',
                 'Transform into an epic fantasy trading card game artwork. Subject rendered as a legendary hero character with dramatic dynamic pose. Rich detailed digital painting with glowing magical effects — energy auras, elemental particles, rune symbols. Ornate card border frame with gold filigree and gem insets. Stats panel area at bottom. Dramatic lighting with volumetric god-rays and magical illumination. Vibrant saturated fantasy color palette. Background of an epic battlefield or enchanted temple. Magic: The Gathering and Hearthstone premium card art quality. Professional fantasy illustration.',
                 'Fun', N'🃏', '#7C4DFF', 1, 59);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Bollywood Retro Poster')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Bollywood Retro Poster', 'Hand-painted 1970s-80s Hindi film poster',
                 'Transform into a classic hand-painted Bollywood movie poster from the 1970s-80s golden era. Bold dramatic composition with the subject as the hero in a powerful pose. Vivid saturated hand-painted colors — deep reds, royal blues, bright yellows. Exaggerated dramatic expressions and dynamic action angles. Traditional Indian film poster layout with bold typography space. Painted texture with visible brushstrokes and slight paint drip details. Background collage of dramatic scenes — explosions, romance, action. Vintage Indian lithograph printing quality with slight color offset. Iconic Bollywood poster artist style.',
                 'Fun', N'🎬', '#D50000', 1, 60);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Indian TV Serial Drama')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Indian TV Serial Drama', 'Over-the-top dramatic Indian TV serial freeze frame',
                 'Transform into a dramatic Indian television serial freeze-frame moment. Extreme close-up with intense dramatic facial expression — shock, suspicion, or revelation. Heavy dramatic color grading with high saturation and contrast. Multiple angle repetition effect showing the same face 3 times from different angles in a single frame. Dramatic zoom blur radiating outward. Thunder and lightning flash effects in the background. Intense sound-wave ripple effects. Ekta Kapoor-style maximum drama aesthetic. Bold eyeliner, heavy jewelry, and elaborate traditional Indian attire. Peak soap opera melodrama quality.',
                 'Fun', N'📺', '#FF5722', 1, 61);

            -- ============================================================
            -- TRENDING 2025-2026: PROFESSIONAL (SortOrder 62-68)
            -- ============================================================
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Corporate Headshot')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Corporate Headshot', 'Professional LinkedIn corporate headshot',
                 'Transform into a polished professional corporate headshot. Clean studio photography with soft neutral gray gradient background. Professional business attire — formal suit, crisp shirt, or smart blazer. Perfect three-point lighting — key light at 45 degrees, fill light, and hair light for depth. Subtle skin retouching with natural look — even tone, reduced blemishes while maintaining texture. Sharp focus on eyes with gentle depth of field. Confident approachable expression with slight professional smile. Color-corrected with clean white balance. LinkedIn and corporate website-ready quality.',
                 'Professional', N'💼', '#1B3A5C', 1, 62);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Magazine Cover Star')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Magazine Cover Star', 'High-fashion glossy magazine cover portrait',
                 'Transform into a high-fashion magazine cover portrait. Vogue and GQ editorial photography quality with dramatic professional lighting. Subject posed with confident editorial body language. High-end fashion styling with designer clothing details. Beauty retouching with flawless skin, defined features, and editorial makeup. Rich cinematic color grading with magazine-quality post-processing. Space for masthead text at top and cover lines on sides. Bold saturated color palette with fashion-forward aesthetic. Studio or luxury location backdrop. Sharp focus with creamy bokeh. Cover-worthy star presence and charisma.',
                 'Professional', N'📰', '#8B0000', 1, 63);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Currency Engraving')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Currency Engraving', 'Detailed banknote currency engraving portrait',
                 'Transform into a detailed currency engraving portrait style. Fine parallel line hatching and cross-hatching technique used in banknote printing. Intricate intaglio engraving detail — every feature rendered through precise varying-thickness lines. Monochromatic green ink on crisp banknote paper with watermark texture. Formal dignified expression and pose. Ornate decorative border with geometric guilloche patterns and fine rosette details. Serial number and denomination elements framing the portrait. Reserve Bank banknote art quality. Distinguished, authoritative, institutional gravitas.',
                 'Professional', N'🏦', '#2E5233', 1, 64);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Double Exposure City')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Double Exposure City', 'Cinematic double exposure with city skyline',
                 'Transform into a cinematic double exposure artwork. The subject''s silhouette filled with a dramatic urban cityscape — glittering skyscrapers, busy streets, golden hour city lights. Seamless blend between the portrait and the metropolitan landscape within the head and shoulder outline. Second exposure layer showing detailed city architecture with iconic skyline landmarks. Moody cinematic color grading — deep teal shadows with warm amber city lights. Clean fade to white or dark background at edges. Professional photographic double exposure technique. Symbolic, aspirational, visually striking.',
                 'Professional', N'🏙️', '#1A237E', 1, 65);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Bronze Sculpture Bust')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Bronze Sculpture Bust', 'Classical bronze sculpture bust with patina',
                 'Transform into a classical bronze sculpture bust. Rich dark bronze metal surface with natural green verdigris patina in crevices and recesses. Detailed sculptural rendering of facial features with sharp chisel marks and smooth polished highlights. Mounted on a marble or granite pedestal base. Museum gallery lighting — dramatic single spotlight creating deep shadows and brilliant specular highlights on the metal surface. Classical Greek-Roman bust proportions with dignified noble bearing. Warm bronze tones from deep chocolate brown to golden copper highlights. Art gallery exhibition quality.',
                 'Professional', N'🗿', '#6D4C2A', 1, 66);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Passport Photo Pro')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Passport Photo Pro', 'Perfect specification-compliant passport photograph',
                 'Transform into a perfect passport-specification photograph. Clean pure white background. Subject facing directly forward with neutral expression — mouth closed, eyes open and clearly visible. Even flat lighting with no shadows on face or background. Head centered in frame with proper passport photo dimensions and head-size ratio. Professional color balance with natural skin tones. Sharp focus across the entire face. No glasses glare, no head covering unless religious. ICAO 9303 compliant biometric photograph quality. Immigration-ready professional passport photo.',
                 'Professional', N'🪪', '#1976D2', 1, 67);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Blueprint Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Blueprint Portrait', 'Technical architectural blueprint schematic',
                 'Transform into a technical architectural blueprint schematic portrait. White precise line drawing on deep Prussian blue cyanotype background. Subject rendered as a detailed technical drawing with dimension lines, measurement annotations, and engineering callouts. Cross-section views showing facial structure like architectural plans. Grid lines, compass roses, and scale bars as decorative elements. Handwritten technical notes in architect''s lettering. Fold-crease lines across the blueprint paper. Professional drafting quality with precise geometric construction lines. Engineering elegance meets artistic portraiture.',
                 'Professional', N'📐', '#0D47A1', 1, 68);

            -- ============================================================
            -- TRENDING 2025-2026: ABSTRACT (SortOrder 69-75)
            -- ============================================================
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Ink in Water')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Ink in Water', 'Hypnotic flowing ink dissolving in water',
                 'Transform into a mesmerizing ink-in-water artwork. The subject''s form composed of flowing colored ink tendrils dissolving and diffusing through crystal-clear water. Rich saturated ink colors — deep indigo, crimson, and gold — swirling and mixing with fluid dynamics. Delicate wispy ink filaments trailing from features and hair. High-speed photography aesthetic capturing the precise moment of ink dispersion. Pure white or deep black background to maximize contrast. Ethereal, hypnotic, ASMR-satisfying visual quality. Macro photography detail showing individual ink particles and micro-currents.',
                 'Abstract', N'🫧', '#1A237E', 1, 69);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Glitch Art')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Glitch Art', 'Digital corruption glitch art aesthetic',
                 'Transform into a striking glitch art digital corruption aesthetic. The subject fragmented with horizontal scan-line displacement, RGB channel splitting, and pixel sorting artifacts. Vibrant neon colors — electric cyan, hot magenta, acid green — bleeding through data corruption. VHS tracking error bands and digital noise grain. Parts of the image stretched, duplicated, and offset creating a broken-data visual. Chromatic aberration and moshing effects on edges. Dark background with bright glitch artifacts. Modern digital art blending human form with technological decay. Cyberpunk visual quality.',
                 'Abstract', N'📟', '#00BFA5', 1, 70);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Vaporwave')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Vaporwave', 'Retro-futuristic vaporwave aesthetic',
                 'Transform into a vaporwave retro-futuristic aesthetic. Neon pink and cyan color palette with purple gradients. 1980s-90s retrofuturism — chrome elements, marble bust references, Japanese text overlays. Sunset gradient background with palm tree silhouettes and wireframe grid landscape receding to horizon. VHS scan lines and CRT monitor glow effects. Pastel neon color scheme — hot pink, baby blue, lavender, mint green. Roman column and classical sculpture elements mixed with early internet graphics. Lo-fi dreamy atmosphere with nostalgic digital artifacts. Full vaporwave aesthetic quality.',
                 'Abstract', N'🌴', '#E040FB', 1, 71);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Kaleidoscope')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Kaleidoscope', 'Sacred geometry kaleidoscope mandala pattern',
                 'Transform into a kaleidoscopic sacred geometry artwork. The subject''s features multiplied and mirrored in perfect radial symmetry — 8-fold or 12-fold kaleidoscope pattern. Rich jewel-tone colors — deep ruby, sapphire blue, emerald green, amethyst purple. Intricate mandala-like geometric patterns radiating from the center. Indian rangoli and yantra sacred geometry influence. Crystalline faceted quality like viewing through a cut gem. Repeating fractal-like patterns becoming finer toward edges. Gold accent lines between segments. Meditative, hypnotic, spiritually resonant. Temple ceiling art quality.',
                 'Abstract', N'🔮', '#AA00FF', 1, 72);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Fractal Bloom')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Fractal Bloom', 'Mathematical fractal art with organic blooming patterns',
                 'Transform into a stunning fractal bloom artwork. The subject''s form integrated into infinitely recursive mathematical fractal patterns — Mandelbrot spirals, Julia set tendrils, and Fibonacci arrangements. Organic fractal branching structures mimicking neurons, coral, and fern fronds growing from the portrait. Deep space-like dark background with luminous fractal structures in electric blue, violet, gold, and white. Smooth gradient coloring based on iteration depth. Self-similar patterns visible at every scale. Digital mathematical art meets human portraiture. Ultra-detailed high-resolution fractal rendering quality.',
                 'Abstract', N'🧬', '#304FFE', 1, 73);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Aurora Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Aurora Portrait', 'Northern lights aurora borealis portrait',
                 'Transform into a celestial aurora borealis portrait. The subject illuminated by and partially composed of flowing northern lights ribbons — shimmering green, violet, pink, and blue aurora curtains dancing across a star-filled night sky. Ethereal light waves flowing through and around the subject''s form. Deep dark navy sky background filled with bright stars and the Milky Way. Aurora light reflecting off the subject creating magical iridescent skin tones. Distant snowy mountain silhouettes on the horizon. Dreamy, celestial, awe-inspiring. National Geographic astrophotography meets fantasy art quality.',
                 'Abstract', N'🌌', '#00C853', 1, 74);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Holographic Prism')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Holographic Prism', 'Iridescent holographic rainbow light refraction',
                 'Transform into an iridescent holographic prism artwork. The subject rendered with rainbow light refraction effects — prismatic color splitting creating spectral rainbows across all surfaces. Holographic foil-like iridescent skin and hair with shifting colors. Crystal prism elements refracting white light into vivid spectral bands. Clean bright studio lighting creating maximum holographic shimmer. Chrome and mirror-like reflective surfaces. Opalescent color palette — shifting pink, blue, purple, green seamlessly blending. Futuristic, mesmerizing, social-media-ready holographic quality.',
                 'Abstract', N'💠', '#00E5FF', 1, 75);

            -- ============================================================
            -- TRENDING 2025-2026: REGIONAL (SortOrder 76-82)
            -- ============================================================
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Madhubani Mithila')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Madhubani Mithila', 'Bihar Madhubani folk painting with natural dyes',
                 'CRITICAL: Preserve EVERY person in the image — do NOT remove, merge, or skip anyone. Transform into traditional Madhubani (Mithila) folk painting style from Bihar. Bold black ink outlines filled with vibrant natural dye colors — vermillion red, turmeric yellow, indigo blue, lamp-black. Distinctive double-line border technique with geometric and floral fill patterns. Large expressive fish-shaped eyes and elongated features. Signature motifs — fish, peacocks, lotus, sun, moon, and wedding scenes. Every empty space filled with intricate pattern work. Floral borders framing the composition. Hand-painted quality on handmade paper texture. UNESCO Intangible Heritage art quality.',
                 'Regional', N'🪷', '#C62828', 1, 76);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Warli Tribal')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Warli Tribal', 'Maharashtra Warli tribal art with geometric figures',
                 'CRITICAL: Preserve EVERY person in the image — do NOT remove, merge, or skip anyone. Transform into Warli tribal art style from Maharashtra. Simple geometric figures made from basic shapes — circles for heads, triangles for bodies, lines for limbs. White rice paste paint on dark red-brown mud wall background. Characteristic Warli circular tarpa dance formation patterns. Scenes of daily village life — farming, hunting, dancing, festivals. Trees as triangular forms, animals as simple geometric shapes. Minimalist yet deeply expressive folk art. White on earth-brown only. Rustic, primal, rhythmic visual energy. Ancient tribal painting quality.',
                 'Regional', N'🏠', '#5D4037', 1, 77);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Pattachitra Odisha')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Pattachitra Odisha', 'Odisha Pattachitra cloth scroll painting',
                 'CRITICAL: Preserve EVERY person in the image — do NOT remove, merge, or skip anyone. Transform into Pattachitra scroll painting style from Odisha. Rich vibrant colors on treated cloth — deep red, yellow ochre, green, white, and black from natural sources. Bold precise black outlines with intricate detailing. Ornate decorative borders with floral scroll patterns. Figures with characteristic large lotus-shaped eyes, sharp nose, ornate headdress. Lord Jagannath temple art influence. Dense composition with no empty space. Traditional chitrakara artisan quality with mythological narrative storytelling.',
                 'Regional', N'🎭', '#E65100', 1, 78);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Gond Tribal')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Gond Tribal', 'Madhya Pradesh Gond tribal dot-and-line art',
                 'CRITICAL: Preserve EVERY person in the image — do NOT remove, merge, or skip anyone. Transform into Gond tribal art style from Madhya Pradesh. Distinctive dot-and-dash fill technique creating intricate patterns within bold outlines. Vibrant earthy colors — cobalt blue, bright red, forest green, warm yellow on white or cream background. Organic flowing forms depicting humans, animals, and nature spirits. Signature Gond motifs — peacocks, deer, trees of life filled with microscopic dot patterns. Each area contains unique repetitive patterns. Jangarh Singh Shyam contemporary Gond art influence. Museum exhibition quality.',
                 'Regional', N'🦚', '#1565C0', 1, 79);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Kerala Mural')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Kerala Mural', 'Kerala temple mural painting tradition',
                 'CRITICAL: Preserve EVERY person in the image — do NOT remove, merge, or skip anyone. Transform into Kerala temple mural painting style. Rich warm palette based on Panchavarna — yellow ochre, red ochre, green, blue-black, and white. Bold precise black outlines with graduated shading. Figures with characteristic large elongated eyes, ornate gold crowns and jewelry, divine expressions. Decorative floral and geometric border patterns. Temple wall fresco texture with subtle aging. Traditional Malayalam aesthetic with mythological grandeur. Padmanabhapuram palace and Mattancherry temple art quality. Sacred, luminous, divinely graceful.',
                 'Regional', N'🪔', '#2E7D32', 1, 80);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Phulkari Punjab')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Phulkari Punjab', 'Punjab Phulkari embroidery textile art',
                 'CRITICAL: Preserve EVERY person in the image — do NOT remove, merge, or skip anyone. Transform into Phulkari embroidery art style from Punjab. Dense geometric floral patterns created with long satin-stitch embroidery on handspun cotton. Vibrant silk thread colors — golden yellow, bright orange, hot pink, parrot green, royal blue on rustic brown khaddar base. Characteristic Phulkari motifs — flowers, wheat sheaves, peacocks, geometric diamonds. Visible thread texture with directional stitch patterns catching light. Dense surface coverage where fabric barely shows. Wedding dupatta and bagh embroidery quality. Celebratory, vivid, textile art masterpiece.',
                 'Regional', N'🌸', '#FF6F00', 1, 81);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Rajput Miniature')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Rajput Miniature', 'Rajasthan Rajput court miniature painting',
                 'CRITICAL: Preserve EVERY person in the image — do NOT remove, merge, or skip anyone. Transform into Rajput miniature painting style from Rajasthan. Exquisite fine-detail court painting with vibrant opaque watercolors and gold leaf accents. Rich jewel-tone palette — deep blue, vermillion red, emerald green, pure gold. Figures with characteristic large almond eyes, sharp profiles, elaborate turbans or ornate jewelry. Decorative architectural elements — jharokha windows, palace courtyards, lotus pools. Flat perspective with intricate borders and floral margins. Mewar, Bundi, and Kishangarh school influences. Miniature painting masterpiece with microscopic detail.',
                 'Regional', N'👑', '#7B1FA2', 1, 82);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Trending 2025-2026 StylePresets seeding failed (non-fatal)");
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

app.UseHttpsRedirection();

// Security headers
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (!app.Environment.IsDevelopment())
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none'";
    await next();
});

app.UseRateLimiter();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
