using System.Security.Claims;
using System.Threading.RateLimiting;
using ArtForgeAI.Components;
using ArtForgeAI.Data;
using ArtForgeAI.Models;
using ArtForgeAI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Components.Authorization;
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
// and extend timeouts for long-running AI operations (face correction, bg removal, etc.)
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

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

// Background removal — no-op stub for interface (Home/StyleTransfer guards)
builder.Services.AddSingleton<IBackgroundRemovalService, NoOpBackgroundRemovalService>();

// Background removal via Gemini AI (uses GeminiOptions already configured above)
builder.Services.AddHttpClient<RemoveBgService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

// Local ONNX background removal — U-2-Net (singleton — model loaded once, auto-downloads)
builder.Services.AddSingleton<OnnxBgRemovalService>();

// Local ONNX image enhancement — Real-ESRGAN 4x upscale (singleton — model loaded once)
builder.Services.AddSingleton<IImageEnhancerService, OnnxImageEnhancerService>();

// Local ONNX color enhancement — SCI illumination (singleton — model loaded once)
builder.Services.AddSingleton<IColorEnhancementService, OnnxColorEnhancementService>();

// Image size master CRUD
builder.Services.AddScoped<IImageSizeMasterService, ImageSizeMasterService>();

// Style preset CRUD
builder.Services.AddScoped<IStylePresetService, StylePresetService>();

// Application services
builder.Services.AddHttpClient<IGeminiImageService, GeminiImageService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(180);
});
builder.Services.AddScoped<IReplicateImageService, ReplicateImageService>();
builder.Services.AddScoped<IImageStorageService, ImageStorageService>();
builder.Services.AddScoped<IPromptEnhancerService, PromptEnhancerService>();
builder.Services.AddScoped<IGenerationHistoryService, GenerationHistoryService>();
builder.Services.AddScoped<IImageGenerationService, ImageGenerationService>();
builder.Services.AddScoped<ICinematicProfileService, CinematicProfileService>();
builder.Services.AddScoped<IPassportPhotoService, PassportPhotoService>();
builder.Services.AddScoped<IFaceCorrectionService, FaceCorrectionService>();
builder.Services.AddScoped<IFormalAttireService, FormalAttireService>();
builder.Services.AddScoped<PhotoExpandService>();

// ── Coin, Subscription, Referral & Payment services ──
builder.Services.AddScoped<ICoinService, CoinService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IReferralService, ReferralService>();
builder.Services.Configure<RazorpayOptions>(
    builder.Configuration.GetSection(RazorpayOptions.SectionName));
builder.Services.AddHttpClient<IRazorpayService, RazorpayService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Authentication — Cookie + Google OAuth
var googleClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "";
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "";
var googleConfigured = !string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret);

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    if (googleConfigured)
        options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.LoginPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnValidatePrincipal = async context =>
    {
        var userIdStr = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        // Skip if NameIdentifier is not our DB integer ID
        // (e.g. during OAuth flow the Google ID is a long string)
        if (!int.TryParse(userIdStr, out var uid))
            return;

        var dbFactory = context.HttpContext.RequestServices
            .GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var dbUser = await db.AppUsers.FindAsync(uid);
        if (dbUser == null || !dbUser.IsActive)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // Refresh role claim from DB (fixes stale cookie)
        var identity = context.Principal!.Identity as ClaimsIdentity;
        if (identity != null)
        {
            var existing = identity.FindFirst(ClaimTypes.Role);
            if (existing != null) identity.RemoveClaim(existing);
            identity.AddClaim(new Claim(ClaimTypes.Role, dbUser.Role.ToString()));
        }
    };
});

if (googleConfigured)
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        options.CallbackPath = "/signin-google";
        options.Scope.Add("email");
        options.Scope.Add("profile");
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdmin", policy => policy.RequireRole("SuperAdmin"));
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAdminAnalyticsService, AdminAnalyticsService>();

// Rate limiting: apply only to API/page endpoints, not static files or Blazor SignalR
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path.Value ?? "";
        // Skip rate limiting for static files, Blazor framework, and SignalR
        if (path.StartsWith("/_blazor") ||
            path.StartsWith("/_framework") ||
            path.StartsWith("/_content") ||
            path.StartsWith("/css") ||
            path.StartsWith("/js") ||
            path.StartsWith("/images") ||
            path.StartsWith("/downloads") ||
            path.StartsWith("/favicon") ||
            path.EndsWith(".css") ||
            path.EndsWith(".js") ||
            path.EndsWith(".png") ||
            path.EndsWith(".jpg") ||
            path.EndsWith(".ico") ||
            path.EndsWith(".woff") ||
            path.EndsWith(".woff2"))
        {
            return RateLimitPartition.GetNoLimiter("static");
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1)
            });
    });
});

var app = builder.Build();

// Ensure database is created + add missing tables for existing databases
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();

    // Create AppUsers table for authentication
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppUsers')
            CREATE TABLE AppUsers (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                GoogleId NVARCHAR(100) NOT NULL,
                Email NVARCHAR(200) NOT NULL,
                DisplayName NVARCHAR(200) NOT NULL DEFAULT '',
                AvatarUrl NVARCHAR(500) NULL,
                Role INT NOT NULL DEFAULT 0,
                IsActive BIT NOT NULL DEFAULT 1,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                LastLoginAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT UQ_AppUsers_GoogleId UNIQUE (GoogleId),
                CONSTRAINT UQ_AppUsers_Email UNIQUE (Email)
            )");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "AppUsers table creation failed (non-fatal)");
    }

    // Add new columns to AppUsers for coin/subscription features
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'CoinBalance')
                ALTER TABLE AppUsers ADD CoinBalance INT NOT NULL DEFAULT 0;
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'ActiveSubscriptionId')
                ALTER TABLE AppUsers ADD ActiveSubscriptionId INT NULL;
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'ReferralCode')
                ALTER TABLE AppUsers ADD ReferralCode NVARCHAR(20) NULL;
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'ReferredByUserId')
                ALTER TABLE AppUsers ADD ReferredByUserId INT NULL;
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('AppUsers') AND name = 'LastDailyLoginReward')
                ALTER TABLE AppUsers ADD LastDailyLoginReward DATETIME2 NULL;");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "AppUsers new columns failed (non-fatal)");
    }

    // Create SubscriptionPlans table
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SubscriptionPlans')
            BEGIN
                CREATE TABLE SubscriptionPlans (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(50) NOT NULL,
                    PriceInr DECIMAL(10,2) NOT NULL DEFAULT 0,
                    GstAmount DECIMAL(10,2) NOT NULL DEFAULT 0,
                    TotalPriceInr DECIMAL(10,2) NOT NULL DEFAULT 0,
                    MonthlyCoins INT NOT NULL DEFAULT 0,
                    DurationDays INT NOT NULL DEFAULT 30,
                    AllowedFeatures NVARCHAR(500) NOT NULL DEFAULT '',
                    IsActive BIT NOT NULL DEFAULT 1,
                    SortOrder INT NOT NULL DEFAULT 0
                );
                SET IDENTITY_INSERT SubscriptionPlans ON;
                INSERT INTO SubscriptionPlans (Id, Name, PriceInr, GstAmount, TotalPriceInr, MonthlyCoins, DurationDays, AllowedFeatures, IsActive, SortOrder) VALUES
                    (1, 'Free',       0,    0,      0,       5,   30, 'QuickStyle', 1, 1),
                    (2, 'Starter',  199,   35.82, 234.82,   50,  30, 'QuickStyle,Home,StyleTransfer,Gallery', 1, 2),
                    (3, 'Pro',      499,   89.82, 588.82,  150,  30, 'QuickStyle,Home,StyleTransfer,Gallery,MosaicPoster,PassportPhoto,ImageViewer,Settings', 1, 3),
                    (4, 'Enterprise', 999, 179.82, 1178.82, 500, 30, 'QuickStyle,Home,StyleTransfer,Gallery,MosaicPoster,PassportPhoto,ImageViewer,Settings,PhotoExpand,GangSheet,ShapeCutSheet', 1, 4);
                SET IDENTITY_INSERT SubscriptionPlans OFF;
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "SubscriptionPlans table creation failed (non-fatal)");
    }

    // Create UserSubscriptions table
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'UserSubscriptions')
            CREATE TABLE UserSubscriptions (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                UserId INT NOT NULL,
                PlanId INT NOT NULL,
                Status INT NOT NULL DEFAULT 0,
                StartDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                EndDate DATETIME2 NOT NULL,
                AutoRenew BIT NOT NULL DEFAULT 1,
                RazorpaySubscriptionId NVARCHAR(100) NULL,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            )");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "UserSubscriptions table creation failed (non-fatal)");
    }

    // Create CoinTransactions table
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CoinTransactions')
            CREATE TABLE CoinTransactions (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                UserId INT NOT NULL,
                Type INT NOT NULL DEFAULT 0,
                Amount INT NOT NULL DEFAULT 0,
                BalanceAfter INT NOT NULL DEFAULT 0,
                Description NVARCHAR(200) NULL,
                ReferenceId NVARCHAR(100) NULL,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            )");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "CoinTransactions table creation failed (non-fatal)");
    }

    // Create CoinPacks table
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CoinPacks')
            BEGIN
                CREATE TABLE CoinPacks (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(50) NOT NULL,
                    CoinAmount INT NOT NULL DEFAULT 0,
                    BonusCoins INT NOT NULL DEFAULT 0,
                    PriceInr DECIMAL(10,2) NOT NULL DEFAULT 0,
                    GstAmount DECIMAL(10,2) NOT NULL DEFAULT 0,
                    TotalPriceInr DECIMAL(10,2) NOT NULL DEFAULT 0,
                    IsActive BIT NOT NULL DEFAULT 1,
                    SortOrder INT NOT NULL DEFAULT 0
                );
                SET IDENTITY_INSERT CoinPacks ON;
                INSERT INTO CoinPacks (Id, Name, CoinAmount, BonusCoins, PriceInr, GstAmount, TotalPriceInr, IsActive, SortOrder) VALUES
                    (1, 'Starter',  50,    0,  49,   8.82,  57.82,  1, 1),
                    (2, 'Value',   100,   20,  99,  17.82, 116.82,  1, 2),
                    (3, 'Pro',     200,   80, 199,  35.82, 234.82,  1, 3),
                    (4, 'Mega',    500,  300, 499,  89.82, 588.82,  1, 4);
                SET IDENTITY_INSERT CoinPacks OFF;
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "CoinPacks table creation failed (non-fatal)");
    }

    // Create Payments table
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Payments')
            CREATE TABLE Payments (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                UserId INT NOT NULL,
                Purpose INT NOT NULL DEFAULT 0,
                Status INT NOT NULL DEFAULT 0,
                AmountInr DECIMAL(10,2) NOT NULL DEFAULT 0,
                GstAmount DECIMAL(10,2) NOT NULL DEFAULT 0,
                TotalAmountInr DECIMAL(10,2) NOT NULL DEFAULT 0,
                RazorpayOrderId NVARCHAR(100) NULL,
                RazorpayPaymentId NVARCHAR(100) NULL,
                RazorpaySignature NVARCHAR(500) NULL,
                CoinPackId INT NULL,
                SubscriptionPlanId INT NULL,
                FailureReason NVARCHAR(500) NULL,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CompletedAt DATETIME2 NULL
            )");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Payments table creation failed (non-fatal)");
    }

    // Create Referrals table
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Referrals')
            CREATE TABLE Referrals (
                Id INT IDENTITY(1,1) PRIMARY KEY,
                ReferrerUserId INT NOT NULL,
                RefereeUserId INT NOT NULL,
                ReferrerBonusCoins INT NOT NULL DEFAULT 15,
                RefereeBonusCoins INT NOT NULL DEFAULT 10,
                IsRewarded BIT NOT NULL DEFAULT 0,
                CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT UQ_Referrals_Referee UNIQUE (RefereeUserId)
            )");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Referrals table creation failed (non-fatal)");
    }

    // Create DeletedStyleSeeds tracking table — records styles the user explicitly deleted
    // so that seed logic never re-inserts them on restart
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DeletedStyleSeeds')
            CREATE TABLE DeletedStyleSeeds (
                Name NVARCHAR(100) NOT NULL PRIMARY KEY
            )");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "DeletedStyleSeeds table creation failed (non-fatal)");
    }

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
                ('Bathukamma', 'Telangana Bathukamma floral festival art', 'Transform into a vibrant Bathukamma festival art style from Telangana. Conical floral tower composition using bright marigold orange, celosia pink, lotus magenta, and tangechi yellow. Circular mandala arrangement of flower layers. Festival celebration energy with people in colorful traditional attire — sarees, dhotis, or kurtas. Joyful, sacred festive energy. Vibrant folk art with floral abundance.', 'Regional', N'🌺', '#E91E63', 1, 34),
                ('Bonalu Festival', 'Vibrant Bonalu festival celebration style', 'Transform into vibrant Bonalu festival art style from Telangana. Rich vermillion red and turmeric yellow dominant palette. Decorated bonam pots with elaborate kolam designs. Festive energy with procession celebration mood. Traditional folk art elements — mirror work, rangoli borders, goddess Mahankali motifs. Bold, energetic, devotional folk art quality.', 'Regional', N'🪔', '#F44336', 1, 35),
                ('Kuchipudi Dance', 'Classical Kuchipudi dance-pose art', 'Transform into classical Kuchipudi dance art style from Andhra Pradesh. Graceful bharatanatyam-adjacent pose with expressive mudra hand gestures. Rich silk costume details in jewel tones. Traditional temple jewelry — gold jhumkas, maang tikka, waist belt. Dynamic frozen-motion capture with flowing fabric. Bronze sculpture-like quality with warm dramatic stage lighting.', 'Regional', N'💃', '#AD1457', 1, 36),
                ('Sankranti Rangoli', 'Makar Sankranti muggu/rangoli patterns', 'Transform into Sankranti muggu rangoli art style from Andhra Pradesh. White rice flour pattern on earthy red ground. Intricate geometric kolam with dot-grid symmetry — lotus flowers, peacocks, and sun motifs. Clean precise mathematical line patterns radiating outward. Festival morning freshness. Traditional South Indian floor art with vibrant color-filled sections.', 'Regional', N'🌀', '#FF6F00', 1, 37),
                ('Tollywood Poster', 'Telugu cinema dramatic poster style', 'Transform into dramatic Telugu cinema poster style. Bold high-contrast hero lighting with intense color grading — teal shadows, orange highlights. Dramatic low-angle composition with dynamic text-friendly negative space. Cinematic depth of field with particle effects. Mass hero energy with powerful stance. Professional movie marketing art quality.', 'Regional', N'🎬', '#1565C0', 1, 38),
                ('Charminar Heritage', 'Hyderabad Charminar architectural style', 'Transform into Hyderabad heritage architectural art style. Indo-Islamic Charminar and Qutb Shahi architecture aesthetic. Detailed stone archway patterns with geometric Islamic jali screens. Warm sandstone and pearl-white marble tones. Mughal miniature perspective with decorative floral borders. Historical heritage illustration with Deccani cultural elegance.', 'Regional', N'🕌', '#546E7A', 1, 39),
                ('Mangalagiri Fabric', 'Mangalagiri handloom cotton weave texture', 'Transform into Mangalagiri handloom textile art style from Andhra Pradesh. Fine cotton weave texture with characteristic nizam border pattern in gold zari. Clean geometric stripes and checks in natural cotton white with vibrant accent colors — mango yellow, parrot green, temple red. Crisp handloom quality with visible warp-weft structure. Elegant simplicity.', 'Regional', N'👘', '#2E7D32', 1, 40),
                ('Etikoppaka Lacquer', 'Etikoppaka lacquer-turned toy art', 'Transform into Etikoppaka lacquer toy art style from Andhra Pradesh. Bright vegetable-dye colors — lac red, turmeric yellow, indigo blue, leaf green. Smooth turned-wood rounded forms with concentric ring patterns. Glossy lacquer finish with warm wood undertones. Playful folk craft aesthetic with simplified charming proportions. Traditional lathe-turned toy quality.', 'Regional', N'🎎', '#EF6C00', 1, 41),
                ('Godavari Landscape', 'River Godavari natural scenic painting', 'Transform into a scenic River Godavari landscape painting. Lush tropical South Indian riverbank with coconut palms and paddy fields. Warm golden morning light reflecting on wide river waters. Traditional fishing boats and papyrus reeds. Rich green foliage with misty hills in background. Peaceful rural Andhra Pradesh atmosphere. Impressionistic plein-air painting quality.', 'Regional', N'🌊', '#00838F', 1, 42),
                ('Perini Warrior', 'Perini Sivatandavam warrior dance art', 'Transform into Perini Sivatandavam warrior dance art style from Telangana. Powerful warrior dance pose with dramatic energy. Bronze sculpture aesthetic with dynamic frozen motion. Traditional warrior costume — dhoti, angavastra, or battle attire as appropriate. Kakatiya dynasty era aesthetic. Deep dramatic lighting emphasizing dynamic form and fierce expression. Ancient temple relief sculpture quality.', 'Regional', N'⚔️', '#4E342E', 1, 43),
                ('Dharmavaram Silk', 'Dharmavaram pattu silk rich textile art', 'Transform into luxurious Dharmavaram pattu silk textile art style. Rich handwoven silk with heavy gold zari brocade borders. Deep jewel colors — temple red, royal purple, peacock blue with contrasting pallu. Traditional attire — saree, dhoti, veshti, or pattu as appropriate to the subject. Intricate traditional motifs — temple towers, mango buttas, peacock designs in metallic gold. Lustrous silk sheen with dramatic drape folds. Ceremonial elegance quality.', 'Regional', N'🥻', '#880E4F', 1, 44),
                ('Deccan Miniature', 'Deccan school miniature painting style', 'Transform into Deccan school miniature painting style. Deccani-Mughal fusion aesthetic with rich palette — gold, deep green, lapis blue, coral. Detailed figure painting with ornate costumes and architecture. Flat perspective with decorative floral borders. Persian-influenced faces with Indian features. Golconda and Hyderabad court painting tradition. Manuscript illumination quality.', 'Regional', N'🎴', '#5D4037', 1, 45),
                ('Araku Valley Nature', 'Araku Valley tribal nature landscape', 'Transform into Araku Valley tribal nature art style from Andhra Pradesh. Lush Eastern Ghats coffee plantation landscape with misty blue-green mountains. Tribal Dhimsa dance silhouettes and Borra Caves rock formations. Rich verdant palette with morning mist atmosphere. Indigenous tribal geometric patterns as decorative border elements. Serene hill-station landscape with waterfall elements.', 'Regional', N'🏔️', '#1B5E20', 1, 46);
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Regional StylePresets seeding failed (non-fatal)");
    }

    // ── Gender-neutral prompt updates for existing regional/artistic styles ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = N'Transform into a vibrant Bathukamma festival art style from Telangana. Conical floral tower composition using bright marigold orange, celosia pink, lotus magenta, and tangechi yellow. Circular mandala arrangement of flower layers. Festival celebration energy with people in colorful traditional attire — sarees, dhotis, or kurtas. Joyful, sacred festive energy. Vibrant folk art with floral abundance.' WHERE Name = N'Bathukamma';

            UPDATE StylePresets SET PromptTemplate = N'Transform into Perini Sivatandavam warrior dance art style from Telangana. Powerful warrior dance pose with dramatic energy. Bronze sculpture aesthetic with dynamic frozen motion. Traditional warrior costume — dhoti, angavastra, or battle attire as appropriate. Kakatiya dynasty era aesthetic. Deep dramatic lighting emphasizing dynamic form and fierce expression. Ancient temple relief sculpture quality.' WHERE Name = N'Perini Warrior';

            UPDATE StylePresets SET PromptTemplate = N'Transform into luxurious Dharmavaram pattu silk textile art style. Rich handwoven silk with heavy gold zari brocade borders. Deep jewel colors — temple red, royal purple, peacock blue with contrasting pallu. Traditional attire — saree, dhoti, veshti, or pattu as appropriate to the subject. Intricate traditional motifs — temple towers, mango buttas, peacock designs in metallic gold. Lustrous silk sheen with dramatic drape folds. Ceremonial elegance quality.',
                Description = N'Dharmavaram pattu silk rich textile art' WHERE Name = N'Dharmavaram Silk';

            UPDATE StylePresets SET PromptTemplate = N'Transform into a vintage 1960s-70s Bollywood glamour portrait. Classic golden-era Hindi cinema aesthetic with soft-focus dreamy lens quality. Subject in elegant traditional attire — silk saree, sherwani, kurta-pajama, or salwar suit as appropriate to their gender. Rich jewel-tone colors — deep red, royal blue, emerald, gold accents. Traditional Indian jewelry appropriate to gender. Soft studio lighting with warm amber key light and gentle fill. Hand-tinted photograph quality with slight color saturation. Lush painted backdrop of a Mughal garden or palatial interior. Golden-era Bollywood elegance. Romantic, graceful, timelessly beautiful Indian cinema.' WHERE Name = N'Retro Bollywood Saree';

            UPDATE StylePresets SET PromptTemplate = N'Transform into a classical Indian oil painting in the style of Raja Ravi Varma. European academic realism blended with Indian aesthetic sensibility. Rich saturated oil colors with luminous skin tones and smooth blending. Subject in traditional Indian attire — silk saree, dhoti, angavastram, or royal robes as appropriate to their gender. Ornate jewelry and classical drapery. Warm golden lighting with soft shadows. Lush Indian botanical background — tropical flowers, temple architecture, or palatial interiors. Museum-quality Indian fine art masterpiece.' WHERE Name = N'Ravi Varma Oil';

            UPDATE StylePresets SET PromptTemplate = N'Transform into the iconic Bapu Bomma illustration style of legendary Telugu artist Bapu. Distinctive clean precise ink linework with elegant flowing contours. Beautiful rounded faces with large expressive almond-shaped eyes, delicate pointed chins, and gentle smiles. Slender graceful figures with classical Indian poses and expressive hand gestures. Traditional South Indian attire — flowing silk saree, dhoti, or kurta with intricate border patterns. Jasmine flowers, temple jewelry. Soft pastel coloring with warm skin tones — peach, cream, and golden hues. Minimal uncluttered backgrounds with subtle floral or nature motifs. The unmistakable Bapu aesthetic — grace, beauty, and cultural elegance of Telugu tradition. Calendar art and Telugu cinema poster illustration quality.' WHERE Name = N'Bapu Bomma';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Gender-neutral prompt updates failed (non-fatal)");
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
                 'Transform into a vintage 1960s-70s Bollywood glamour portrait. Classic golden-era Hindi cinema aesthetic with soft-focus dreamy lens quality. Subject in elegant traditional attire — silk saree, sherwani, kurta-pajama, or salwar suit as appropriate to their gender. Rich jewel-tone colors — deep red, royal blue, emerald, gold accents. Traditional Indian jewelry appropriate to gender. Soft studio lighting with warm amber key light and gentle fill. Hand-tinted photograph quality with slight color saturation. Lush painted backdrop of a Mughal garden or palatial interior. Golden-era Bollywood elegance. Romantic, graceful, timelessly beautiful Indian cinema.',
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

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Silhouette Contour')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                ('Silhouette Contour', 'Elegant profile line art with golden filigree base',
                 'Transform into a pure line art portrait in elegant side profile view. STRICT RULES: The ENTIRE image must consist of thin outline strokes ONLY. ABSOLUTELY NO filled areas, NO solid colors, NO shading, NO color blocks, NO gradients anywhere in the image — not on clothing, not on hair, not on skin, not on any element. Every single part of the subject — face, hair, neck, shoulders, body, clothing — must be rendered as open unfilled outlines only, like a coloring book page. Do NOT fill or color any clothing — jackets, shirts, dresses must be just outline edges. The subject shown in graceful side-facing profile. Face profile with clearly defined line features: eye with lashes, nose bridge and tip, lips, chin and jawline contour. Hair flowing with elegant sweeping line strokes — no filled hair, just strand outlines. Neck, shoulders, and upper body as simple contour outlines. All lines in fine warm golden-brown or dark gold color on a soft pale cream or warm ivory flat matte background. At the bottom: ornate golden decorative filigree scrollwork — curling vine patterns, swirls, floral ornamental vector art as a decorative border. No trophy, no award, no glass, no pedestal, no 3D objects. Style: pure outline drawing, premium invitation line art.',
                 'Professional', N'👤', '#8D6E63', 1, 160);

            UPDATE StylePresets SET
                Description = 'Elegant profile line art with golden filigree base',
                PromptTemplate = 'Transform into a pure line art portrait in elegant side profile view. STRICT RULES: The ENTIRE image must consist of thin outline strokes ONLY. ABSOLUTELY NO filled areas, NO solid colors, NO shading, NO color blocks, NO gradients anywhere in the image — not on clothing, not on hair, not on skin, not on any element. Every single part of the subject — face, hair, neck, shoulders, body, clothing — must be rendered as open unfilled outlines only, like a coloring book page. Do NOT fill or color any clothing — jackets, shirts, dresses must be just outline edges. The subject shown in graceful side-facing profile. Face profile with clearly defined line features: eye with lashes, nose bridge and tip, lips, chin and jawline contour. Hair flowing with elegant sweeping line strokes — no filled hair, just strand outlines. Neck, shoulders, and upper body as simple contour outlines. All lines in fine warm golden-brown or dark gold color on a soft pale cream or warm ivory flat matte background. At the bottom: ornate golden decorative filigree scrollwork — curling vine patterns, swirls, floral ornamental vector art as a decorative border. No trophy, no award, no glass, no pedestal, no 3D objects. Style: pure outline drawing, premium invitation line art.',
                Category = 'Professional', IconEmoji = N'👤', AccentColor = '#8D6E63'
            WHERE Name = 'Silhouette Contour';

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

    // Seed Indian Mythological style presets (guarded individually by Name)
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            -- ============================================================
            -- MYTHOLOGICAL: Indian Puranic Character Styles (SortOrder 50-57)
            -- ============================================================

            -- 1. Vishnu Divine (Vishnu Purana)
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Vishnu Divine')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Vishnu Divine',
                 N'Lord Vishnu cosmic preserver from Vishnu Purana',
                 N'Transform the subject into Lord Vishnu, the cosmic preserver from the Vishnu Purana. CRITICAL: Preserve the subject''s exact facial features and identity. Render with radiant blue skin tone. Adorned with an elaborate golden Kiritam (towering crown) studded with rubies and emeralds. Four divine arms holding the Sudarshana Chakra (flaming discus), Panchajanya Shankha (sacred conch), Kaumodaki Gada (golden mace), and a blooming Padma (lotus). Wearing lustrous yellow Pitambara silk dhoti with golden border. Chest adorned with the Kaustubha gem and Vanamala (forest garland reaching the knees). Ornate gold armlets, wristlets, and anklets. Serene all-knowing compassionate expression. Background of Vaikuntha — celestial palace with cosmic ocean (Kshira Sagara), Shesha Naga (divine serpent throne), and golden pillared mandapam. Divine aura with golden light rays emanating outward. Classical Indian devotional calendar art quality with rich saturated colors.',
                 N'Mythological', N'🔹', '#1565C0', 1, 50);

            -- 2. Shiva Nataraja (Shiva Purana)
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Shiva Nataraja')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Shiva Nataraja',
                 N'Lord Shiva cosmic dancer from Shiva Purana',
                 N'Transform the subject into Lord Shiva Nataraja, the cosmic dancer from the Shiva Purana. CRITICAL: Preserve the subject''s exact facial features and identity. Render with ash-smeared fair skin with a third eye (Trinetra) glowing on the forehead. Matted jata (dreadlocked hair) flowing upward with the crescent moon (Chandrama) nestled within, the holy river Ganga cascading from the locks. Coiled serpent Vasuki around the neck. Wearing a tiger skin around the waist and rudraksha mala. Dynamic Tandava dance pose within a blazing Prabha Mandala (cosmic ring of fire) symbolizing the cycle of creation and destruction. Upper right hand holding the Damaru (drum of creation), upper left hand holding Agni (sacred fire). Lower right hand in Abhaya mudra (fear not gesture), lower left pointing to the raised foot granting liberation. Trampling the dwarf Apasmara (ignorance) underfoot. Background of Mount Kailash with snow peaks, cosmic nebulae, and starfields. Dramatic divine lighting with blue and gold tones. Sacred magnificent temple sculpture brought to life.',
                 N'Mythological', N'🔥', '#4527A0', 1, 51);

            -- 3. Durga Shakti (Markandeya Purana / Devi Mahatmya)
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Durga Shakti')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Durga Shakti',
                 N'Goddess Durga warrior from Markandeya Purana',
                 N'Transform the subject into Goddess Durga Mahishasuramardini, the invincible warrior goddess from the Devi Mahatmya of the Markandeya Purana. CRITICAL: Preserve the subject''s exact facial features and identity. Render with luminous golden skin radiating divine Shakti energy. Eight powerful arms wielding divine weapons — Trishul (trident) from Shiva, Sudarshana Chakra from Vishnu, bow and arrows from Vayu, thunderbolt from Indra, sword and shield, and a lotus. Wearing a magnificent red and gold silk saree with ornate temple jewelry — heavy gold necklace, elaborate crown, armlets, and nose ring. Fierce yet compassionate expression with blazing eyes of righteous anger. Riding the majestic lion (Simha Vahana) in a powerful stance. Background of an epic battlefield with dramatic stormy skies, divine light breaking through dark clouds, and celestial beings showering flowers from above. Rich red, gold, and saffron color palette. Bengal Durga Puja pandal art quality with dramatic cinematic lighting.',
                 N'Mythological', N'⚔️', '#C62828', 1, 52);

            -- 4. Krishna Murali (Bhagavata Purana)
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Krishna Murali')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Krishna Murali',
                 N'Lord Krishna the divine flutist from Bhagavata Purana',
                 N'Transform the subject into Lord Krishna, the enchanting divine flutist from the Bhagavata Purana. CRITICAL: Preserve the subject''s exact facial features and identity. Render with beautiful dark blue (Megha Shyam) skin complexion glowing with inner divine light. Adorned with a magnificent Peacock feather crown (Mor Mukut) with jewels. Playing the enchanting Murali (bamboo flute) with graceful fingers. Wearing lustrous yellow Pitambara silk dhoti and uttariya (upper cloth). Ornate gold Makara Kundala (crocodile-shaped earrings), Vaijayanti Mala (five-gem garland), gold armlets, and anklets with tiny bells. Gentle mischievous smile (Manda Hasa). Tribhanga (three-bend) graceful standing pose. Background of enchanted Vrindavan — lush Kadamba trees along the banks of the sacred Yamuna river, cows grazing peacefully, lotus ponds, full moon night with soft silver light. Fireflies and flower petals floating in the air. Classical Indian miniature painting quality with Pichwai and Rajasthani art influences. Warm golden divine illumination.',
                 N'Mythological', N'🦚', '#0D47A1', 1, 53);

            -- 5. Rama Maryada (Padma Purana / Ramayana)
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Rama Maryada')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Rama Maryada',
                 N'Lord Rama the noble warrior-king from Padma Purana',
                 N'Transform the subject into Lord Rama, Maryada Purushottam — the supreme ideal man from the Padma Purana and Ramayana. CRITICAL: Preserve the subject''s exact facial features and identity. Render with a noble dark green-blue complexion radiating dharmic righteousness. Regal yet humble expression of a warrior-prince. Adorned with a golden Mukuta (crown) set with precious stones. Wielding the mighty Kodanda (divine bow) with a quiver of celestial arrows on the back. Wearing royal green and gold silk Vanavasi attire — dhoti, angavastra with forest-style ornaments combining royal elegance with ascetic simplicity. Gold armlets (Keyura), sacred thread, and Tilaka on the forehead. Strong athletic build with a commanding noble posture. Background of the majestic forests of Dandakaranya — towering ancient trees, sacred rivers, golden sunlight filtering through the canopy, with distant view of a grand palatial Ayodhya silhouette. A subtle divine golden halo behind the head. Classical Tanjore and Mysore painting fusion style with rich greens, golds, and earth tones. Devotional calendar art masterpiece quality.',
                 N'Mythological', N'🏹', '#2E7D32', 1, 54);

            -- 6. Ganesha Vighnaharta (Ganesha Purana)
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Ganesha Vighnaharta')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Ganesha Vighnaharta',
                 N'Lord Ganesha the remover of obstacles from Ganesha Purana',
                 N'Transform the subject into a devotional portrait inspired by Lord Ganesha, Vighnaharta — the remover of obstacles from the Ganesha Purana. CRITICAL: Preserve the subject''s exact facial features and identity. Render the subject seated on a magnificent golden Simhasana (throne) in a regal divine pose. Adorned with an elaborate golden Mukuta (crown) studded with rubies and diamonds. Wearing rich red and gold silk dhoti with ornate waistband. Heavy gold temple jewelry — necklaces, armlets, sacred thread (Yajnopavita) across the chest. Holding Modaka (sweet dumpling) in one hand, lotus in another, and displaying Abhaya Mudra (blessing gesture). A broken tusk held gracefully. Surrounded by divine attributes — Mushika (mouse vehicle) at the feet, plates of sweets and fruits as offerings. Background of an opulent temple sanctum with carved pillars, oil lamps (diyas) casting warm golden light, flower garlands, and incense smoke curling upward. Rich palette of vermillion red, turmeric gold, and sacred saffron. Festive Ganesh Chaturthi celebration art quality with divine radiance.',
                 N'Mythological', N'🪷', '#E65100', 1, 55);

            -- 7. Hanuman Bajrang (Shiva Purana / Sundara Kanda)
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Hanuman Bajrang')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Hanuman Bajrang',
                 N'Lord Hanuman the mighty devotee from Shiva Purana',
                 N'Transform the subject into Lord Hanuman, Bajrang Bali — the mighty devotee warrior from the Shiva Purana and Sundara Kanda. CRITICAL: Preserve the subject''s exact facial features and identity. Render with a powerful muscular warrior physique and glowing vermillion-orange (sindoor) skin. Heroic fierce devotional expression with blazing determined eyes. Wearing a golden crown (Mukut) and golden armour chest plate. Adorned with gold armlets, wristlets, and a sacred thread. A flowing long tail curling upward with divine energy. Wielding the colossal golden Gada (mace) in one mighty hand. Wearing a short dhoti and carrying the Sanjeevani mountain in the other hand (showing his legendary feat of carrying the entire mountain to save Lakshmana). Open chest revealing the image of Lord Rama and Sita within his heart — the ultimate symbol of devotion. Background of a dramatic sky — flying through clouds at sunset with Lanka burning far below, epic Ramayana battlefield with divine armies. Intense warm palette of saffron, gold, vermillion, and crimson. Dramatic heroic lighting with divine glow. Powerful Indian mythological calendar art quality.',
                 N'Mythological', N'💪', '#FF6F00', 1, 56);

            -- 8. Saraswati Vidya (Brahmanda Purana)
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Saraswati Vidya')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Saraswati Vidya',
                 N'Goddess Saraswati the divine muse from Brahmanda Purana',
                 N'Transform the subject into Goddess Saraswati, Vidya Dayini — the divine goddess of knowledge, music, and wisdom from the Brahmanda Purana. CRITICAL: Preserve the subject''s exact facial features and identity. Render with luminous fair radiant skin glowing with the pure white light of knowledge. Wearing an elegant pristine white silk saree with subtle silver and gold threadwork (representing purity and Sattva). Adorned with delicate pearl and diamond jewelry — simple yet divine. Four graceful arms — playing the sacred Veena (stringed instrument) with two hands, holding the Pustaka (sacred scriptures) in one hand and a Sphatika Mala (crystal rosary) in another. Seated gracefully on a blooming white Padma (lotus) in Padmasana. A regal yet gentle serene expression of supreme wisdom. The sacred swan (Hamsa Vahana) at her side. Background of a serene sacred riverbank at dawn — the Saraswati river with lotus ponds, ancient Banyan trees, and a soft saffron-pink sunrise sky. White cranes, floating lotus petals, and gentle mist on the waters. Soft ethereal lighting with white, gold, and pale blue tones. Vasant Panchami devotional art quality. Classical Indian painting masterpiece.',
                 N'Mythological', N'🎶', '#F5F5F5', 1, 57);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Mythological StylePresets seeding failed (non-fatal)");
    }

    // ── Seed Photo Collage Mix style (Fun category) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Photo Collage Mix')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Photo Collage Mix', N'4-6 styled photo collage on 20x30 sheet',
                 N'Create a beautiful photo collage layout on a single 20×30 inch print-ready sheet. CRITICAL: Preserve EVERY person''s exact facial features and identity across ALL panels. Take the uploaded source image and generate 4 to 6 distinct artistic variations of the SAME subject arranged in a visually striking collage grid layout. Each panel must show the same person(s) but in a DIFFERENT artistic style: Panel 1 — vivid oil painting with thick impasto brushstrokes and rich warm colors. Panel 2 — clean modern pop art with bold halftone dots, primary colors, and thick black outlines. Panel 3 — soft dreamy watercolor with translucent pastel washes and bleeding edges. Panel 4 — high-contrast dramatic black and white charcoal sketch with expressive strokes. Panel 5 (if space) — neon cyberpunk glow with electric pink and cyan light tubes on black. Panel 6 (if space) — elegant pencil line art with fine cross-hatching. Arrange panels in an asymmetric magazine-style grid with thin white borders (roughly 8-10px) between each panel. Vary panel sizes for visual interest — mix large feature panels with smaller accent panels. The overall composition should feel like a professional photo book spread or gallery wall arrangement. Clean white or soft grey background border around the entire collage. Print-quality high resolution with sharp details in every panel.',
                 N'Fun', N'🖼️', '#7C4DFF', 1, 16);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Photo Collage Mix StylePreset seeding failed (non-fatal)");
    }

    // ── Seed additional Collage style presets (7 styles, Fun category) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            -- ============================================================
            -- COLLAGE TEMPLATES: Face-centric multi-panel collages (Fun)
            -- ============================================================

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Emotions Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Emotions Collage', N'Same face with 6 dramatic emotions collage',
                 N'Create a stunning emotions portrait collage on a single 20×30 inch print-ready sheet. CRITICAL: Preserve the subject''s EXACT facial features, face shape, skin tone, and identity across ALL panels — the same real person must be unmistakably recognizable in every frame. Generate 6 dramatic close-up face portraits of the SAME person, each showing a different powerful emotion: Panel 1 (large hero panel) — Pure Joy: an ecstatic beaming laugh with crinkled eyes, teeth showing, genuine radiant happiness, warm golden lighting. Panel 2 — Deep Thought: pensive contemplative expression, chin resting on hand, soft side-lighting with moody blue-grey tones. Panel 3 — Fierce Determination: intense focused gaze directly at camera, strong jaw set, dramatic chiaroscuro lighting with deep shadows. Panel 4 — Surprise & Wonder: wide eyes, raised eyebrows, slightly open mouth, bright ring-light catchlights, vivid colors. Panel 5 — Serene Peace: eyes gently closed, soft subtle smile, ethereal soft-focus backlighting with warm golden haze. Panel 6 — Bold Confidence: slight knowing smirk, one eyebrow raised, glamorous studio lighting with rim light highlighting the profile. Arrange in an asymmetric magazine editorial grid — one large hero panel occupying 40% of the sheet, remaining panels in varied smaller sizes. Thin white borders between panels. Each panel has cinematic movie-poster quality lighting and color grading. Professional portrait photography quality throughout.',
                 N'Fun', N'🎭', '#FF6F00', 1, 17);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Decades Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Decades Collage', N'Same face across 6 fashion eras collage',
                 N'Create a time-travel fashion decades collage on a single 20×30 inch print-ready sheet. CRITICAL: Preserve the subject''s EXACT facial features, face shape, bone structure, and identity across ALL panels — the same real person must be unmistakably recognizable in every era. Generate 6 portraits of the SAME person styled in different iconic fashion decades: Panel 1 — 1950s Glamour: classic Hollywood golden age, victory rolls or slicked hair, red lipstick, pearl necklace, black and white with subtle warm sepia tones, soft-focus Hurrell glamour portrait lighting. Panel 2 — 1970s Disco: big voluminous hair, wide collar shirt or sequin top, gold hoop earrings, warm amber and orange color palette, groovy psychedelic background patterns. Panel 3 — 1980s Neon: bold geometric earrings, power shoulders, teased hair with hairspray, bright neon pink and electric blue color palette, MTV-era pop aesthetic. Panel 4 — 1990s Grunge: flannel shirt, choker necklace, natural messy hair, muted desaturated earth tones, moody alternative film-grain aesthetic. Panel 5 — 2000s Y2K: glossy lip gloss, butterfly clips or frosted tips, low-rise fashion, metallic silver and bubblegum pink, early digital camera flash aesthetic. Panel 6 — 2020s Modern: clean minimal Korean-inspired styling, glass-skin glow, subtle natural makeup, soft ring-light illumination, Instagram editorial quality. Arrange in a horizontal timeline-style grid flowing left to right with era labels subtly overlaid. Thin cream borders between panels. Each panel authentically captures its decade''s photography style and color grading.',
                 N'Fun', N'⏳', '#FF8F00', 1, 18);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'World Cultures Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'World Cultures Collage', N'Same face in 6 cultural traditional attires',
                 N'Create a stunning world cultures portrait collage on a single 20×30 inch print-ready sheet. CRITICAL: Preserve the subject''s EXACT facial features, face shape, skin tone, and identity across ALL panels — the same real person must be unmistakably recognizable in every cultural look. Generate 6 respectful, celebratory portraits of the SAME person wearing traditional attire from different world cultures: Panel 1 (large feature) — Indian Royal: ornate gold and ruby Rajasthani jewelry, embroidered silk fabric draped elegantly, maang tikka headpiece, warm saffron and gold palette, Diwali festive lighting. Panel 2 — Japanese Elegance: beautiful floral kimono with obi sash, delicate kanzashi hair ornament, cherry blossom backdrop, soft pink and white palette. Panel 3 — African Royalty: vibrant Ankara/Kente cloth wrap, bold beaded necklace and headwrap (gele), rich earthy reds, greens, and gold palette, warm sunset lighting. Panel 4 — Korean Hanbok: elegant silk jeogori and chima in cerise and jade, traditional binyeo hairpin, palace courtyard background, soft pastel tones. Panel 5 — Arabian Nights: luxurious embroidered kaftan with gold thread, ornate henna patterns on hands, jeweled headpiece, deep emerald and gold palette, lantern-lit ambiance. Panel 6 — European Renaissance: rich velvet and lace gown, pearl jewelry, elaborate updo hairstyle, Flemish oil painting quality with dramatic window light. Arrange in an elegant mosaic grid with thin gold foil borders between panels. Each panel is a respectful celebration of cultural beauty with authentic details and lighting.',
                 N'Fun', N'🌍', '#00897B', 1, 19);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Movie Poster Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Movie Poster Collage', N'Same face in 6 film genre poster styles',
                 N'Create a cinematic movie poster collage on a single 20×30 inch print-ready sheet. CRITICAL: Preserve the subject''s EXACT facial features, face shape, and identity across ALL panels — the same real person must be the unmistakable star of every genre poster. Generate 6 dramatic portraits of the SAME person styled as the lead character in different movie genres: Panel 1 (large hero panel) — Action Blockbuster: intense determined expression, leather jacket, explosions and helicopters in background, dramatic orange and teal color grading, lens flare, bold action-movie typography space at top. Panel 2 — Romantic Drama: soft dreamy gaze, elegant outfit, golden hour sunset backdrop, warm rose-gold color palette, soft bokeh lights. Panel 3 — Horror/Thriller: half-face in shadow, wide terrified eyes, eerie green-blue desaturated tones, cracked mirror reflection, fog creeping in. Panel 4 — Sci-Fi Epic: futuristic armor or space suit, holographic HUD reflections on visor, distant planets and nebulae, electric blue and purple palette. Panel 5 — Film Noir Detective: fedora hat with shadow across eyes, trench coat, cigarette smoke wisps, dramatic black and white with high contrast, venetian blind shadow lines across face. Panel 6 — Comedy: big genuine laugh with bright cheerful expression, colorful casual outfit, bright saturated pop colors, confetti or paint splashes in background. Arrange in a dynamic asymmetric poster-wall grid with thin dark borders. Each panel includes subtle film-genre typography styling. Professional movie marketing art quality.',
                 N'Fun', N'🎬', '#D32F2F', 1, 20);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Seasons Portrait Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Seasons Portrait Collage', N'Same face across 4 seasons with 2 bonus panels',
                 N'Create a breathtaking four-seasons portrait collage on a single 20×30 inch print-ready sheet. CRITICAL: Preserve the subject''s EXACT facial features, face shape, skin tone, and identity across ALL panels — the same real person must be unmistakably recognizable in every season. Generate 6 portraits of the SAME person immersed in different seasonal atmospheres: Panel 1 (large feature) — Spring Bloom: subject surrounded by cherry blossom and magnolia petals gently falling, wearing a light floral outfit, soft pink and green pastel palette, fresh morning dew lighting, butterflies nearby, renewal and joy in expression. Panel 2 — Summer Golden: bright warm sunlight, wearing light breezy summer clothes, sunflower field or beach backdrop, rich golden-amber and turquoise palette, lens flare, confident happy expression. Panel 3 — Autumn Warmth: wrapped in a cozy scarf or sweater, surrounded by falling maple leaves in crimson, amber, and burnt orange, warm coffee-toned palette, soft diffused golden hour forest light. Panel 4 — Winter Magic: elegant winter attire, gentle snowflakes falling and resting on hair and shoulders, icy blue and silver palette with warm breath mist visible, twinkling fairy lights bokeh background, peaceful serene expression. Panel 5 — Monsoon Romance: rain droplets on face and hair, slightly wet clothes, dramatic grey sky with one shaft of golden light breaking through, reflective wet surfaces, cinematic rain-soaked beauty. Panel 6 — Twilight Dream: magical golden-hour-to-blue-hour transition, ethereal backlit silhouette with warm rim light on hair, fireflies and bokeh orbs floating, dreamy soft-focus romantic atmosphere. Arrange in an organic flowing grid — four main seasonal panels equally sized with two smaller accent panels. Thin white borders. Each panel captures the season''s mood through lighting, color, and atmosphere.',
                 N'Fun', N'🍃', '#4CAF50', 1, 21);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Fantasy Avatar Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Fantasy Avatar Collage', N'Same face as 6 fantasy characters collage',
                 N'Create an epic fantasy character avatar collage on a single 20×30 inch print-ready sheet. CRITICAL: Preserve the subject''s EXACT facial features, face shape, skin tone, and identity across ALL panels — the same real person must be unmistakably recognizable as every fantasy character. Generate 6 portraits of the SAME person transformed into different fantasy archetypes: Panel 1 (large hero panel) — Elven Royalty: elegant pointed ears, flowing silver-blonde hair with delicate leaf crown, ethereal luminous skin, wearing ornate elven armor with nature motifs, enchanted forest background with bioluminescent plants, soft green and gold palette. Panel 2 — Dark Warrior Knight: battle-scarred black plate armor with glowing red rune engravings, fierce war-paint on face, wielding a flaming sword, dark stormy castle background, dramatic crimson and steel palette. Panel 3 — Celestial Angel: magnificent white feathered wings spread wide, flowing white and gold robes, golden halo of light behind head, heavenly clouds and divine light rays, radiant white and gold palette. Panel 4 — Vampire Aristocrat: pale porcelain skin, sharp elegant features, blood-red eyes with vertical pupils, wearing Victorian gothic black velvet with high collar and ruby brooch, moonlit gothic castle, deep burgundy and midnight palette. Panel 5 — Ocean Siren/Mermaid: iridescent scales visible on shoulders and neck, flowing hair with seashell and pearl ornaments, underwater bioluminescent coral backdrop, aqua-teal and pearl palette. Panel 6 — Fire Mage Sorcerer: hands conjuring swirling flames and ember particles, glowing orange eyes, ornate mage robes with fire rune patterns, volcanic backdrop with lava rivers, intense orange and deep purple palette. Arrange in a dramatic asymmetric grid with thin dark obsidian borders. Each panel has cinematic fantasy game art quality with rich atmospheric lighting.',
                 N'Fun', N'🧝', '#6A1B9A', 1, 22);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Career Day Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Career Day Collage', N'Same face in 6 iconic professional roles',
                 N'Create a fun career day portrait collage on a single 20×30 inch print-ready sheet. CRITICAL: Preserve the subject''s EXACT facial features, face shape, skin tone, and identity across ALL panels — the same real person must be unmistakably recognizable in every professional role. Generate 6 portraits of the SAME person dressed and styled as different iconic professions: Panel 1 (large feature) — Astronaut: wearing a detailed NASA-style space suit with helmet visor open showing the face, Earth reflection visible in the visor glass, dramatic space station lighting with star field, deep blue and white palette. Panel 2 — Chef Master: crisp white double-breasted chef coat and tall toque hat, confidently holding a gleaming kitchen knife, warm kitchen with copper pots and flames in background, warm amber and white palette. Panel 3 — Fighter Pilot: aviator sunglasses pushed up on forehead, flight suit with patches and harness, confident smirk, fighter jet cockpit or runway with jets in background, cool steel-blue and orange palette. Panel 4 — Rock Star: leather jacket, electric guitar slung over shoulder, dramatic concert stage lighting with smoke machines and spotlights, audience silhouettes, electric purple and red palette. Panel 5 — Doctor/Surgeon: clean white coat with stethoscope, calm reassuring professional expression, modern hospital or operating room background with blue surgical lights, clinical blue-green and white palette. Panel 6 — Detective: classic trench coat and fedora, magnifying glass held near face with one eye enlarged through it, mysterious foggy street with vintage car and streetlamp, noir-style amber and shadow palette. Arrange in an engaging magazine-style grid with thin white borders. Each panel has professional-quality lighting and authentic costume details that sell the role convincingly.',
                 N'Fun', N'🧑‍🚀', '#1565C0', 1, 23);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Zodiac Signs Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Zodiac Signs Collage', N'Same face as 6 zodiac constellation portraits',
                 N'Create a mystical zodiac portrait collage on a single 20×30 inch print-ready sheet. CRITICAL: Preserve the subject''s EXACT facial features, face shape, skin tone, and identity across ALL panels — the same real person must be unmistakably recognizable as every zodiac embodiment. Generate 6 portraits of the SAME person embodying different zodiac sign aesthetics: Panel 1 (large feature) — Leo: regal golden mane-like flowing hair with warm highlights, fierce confident expression, ornate gold crown and royal armor, lion silhouette in golden light behind, rich gold and amber palette with warm sun rays. Panel 2 — Scorpio: mysterious intense gaze, dark smoky eye makeup, black velvet draped outfit with scorpion brooch, dark water reflections and crimson roses, deep burgundy and black palette with subtle red glow. Panel 3 — Aquarius: futuristic iridescent outfit with water and air elements swirling around hands, electric blue streaks in hair, ethereal cosmic water-bearer pouring starlight, electric blue and silver palette. Panel 4 — Aries: bold warrior crown with ram horns, fierce determined expression, metallic red and gold armor, fire and sparks erupting in background, intense crimson and molten gold palette. Panel 5 — Pisces: dreamy ethereal expression, flowing hair intertwined with water streams and tiny fish, iridescent scales on shoulders, underwater ocean light, soft lavender and aqua-marine palette. Panel 6 — Sagittarius: adventurous smile, wielding a glowing bow and arrow aimed at the stars, centaur constellation shimmering behind, wearing explorer leather outfit, warm purple and starfield gold palette. Arrange in a celestial mosaic grid with thin midnight-blue borders and subtle star constellation lines connecting the panels. Each panel has a deep cosmic night-sky quality with constellation star patterns subtly overlaid.',
                 N'Fun', N'♈', '#311B92', 1, 24);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Collage StylePresets seeding failed (non-fatal)");
    }

    // ── Seed PhotoCollage category ──
    // NOTE: Prompts with {{OCCASION}} and {{MESSAGE}} must be passed as SQL parameters
    // because ExecuteSqlRawAsync uses String.Format internally, which collapses {{ to {.
    try
    {
        const string greetingCardPrompt = "DO NOT simply edit or reproduce the source photo. You MUST generate a BRAND NEW composite image that is a SCRAPBOOK-STYLE GREETING CARD COLLAGE on a single vertical 2:3 portrait canvas (like a 20×30 inch print). Use the person in the source photo as the subject — preserve their EXACT face, hair, and appearance in every photo panel. LAYOUT — arrange 3 to 4 POLAROID-STYLE PHOTO PANELS scattered at slight random angles (tilted 2-8 degrees left or right) on the canvas. Each polaroid has a thick white border (especially wider at the bottom like a real instant photo). The photos should OVERLAP slightly in an organic scrapbook arrangement: Photo 1 (upper-left area, largest): a stylish three-quarter portrait of the subject, natural warm lighting. Photo 2 (upper-right, medium): a candid lifestyle shot — the subject laughing, walking, or in an everyday moment. Photo 3 (lower-right, medium): a close-up portrait with a warm genuine smile. Photo 4 (lower-left, optional smaller): a full-body or creative angle shot. DECORATIVE ELEMENTS scattered around the photos: realistic pink peonies and roses (soft pink, blush, and cream tones), green botanical leaves and fern fronds, small strips of washi tape (red gingham check pattern) holding photos in place, and tiny floral sprigs tucked behind the polaroids. LARGE CALLIGRAPHY TEXT: In the open space (centre-left area between the photos), render the text '{{OCCASION}}' in elegant flowing hand-lettered brush script font — dark charcoal/black ink colour, large and prominent, with natural brush stroke thickness variations. This must be clearly readable. HANDWRITTEN NOTE: In the lower portion of the canvas, render a small kraft-brown paper card/note (slightly tilted) with handwritten cursive text reading: '{{MESSAGE}}' — in a warm dark brown ink, casual handwriting style. BACKGROUND: warm cream/off-white textured paper with very faint vintage script writing watermark pattern. Overall colour palette: warm cream, soft blush pink, sage green, kraft brown — matching the mood and tones of the source photo. The final image must look like a beautifully styled flat-lay scrapbook greeting card photograph.";

        const string tornEdgePrompt = "DO NOT simply reproduce or lightly edit the source photo. You MUST generate a BRAND NEW composite image from scratch that is a COLLAGE of THREE SEPARATE PHOTOGRAPHS arranged vertically on a single canvas. Use the person's face and appearance from the source photo as reference ONLY for identity — every scene must be completely new and different. OUTPUT LAYOUT — vertical 2:3 portrait canvas divided into 3 stacked horizontal strips: TOP STRIP (35% of height): Generate a completely new wide-angle outdoor adventure scene — the same person standing next to a vintage vehicle on a scenic mountain road, or walking through a vibrant street market, or sitting on a dock at sunset. Full-body or three-quarter shot. Warm golden-hour lighting. MIDDLE STRIP (35% of height): Generate a completely new intimate close-up portrait — the same person in a cosy indoor setting like peeking out of a camping tent, leaning on a café window sill, or lying in a hammock. Contemplative or candid smiling expression. Soft diffused natural light. BOTTOM STRIP (30% of height): Generate a completely new rear-view or silhouette lifestyle shot — the same person from behind, arms raised or hands on head, gazing at a vast mountain valley, ocean horizon, or canyon overlook. Freedom and wanderlust mood. CRITICAL LAYOUT RULE: Between each strip, render a TORN PAPER EDGE — jagged irregular ripped-paper transition with visible white paper fibres and a faint drop shadow. The tear must look hand-ripped, NOT a straight line. Thin sliver of warm kraft/cream paper texture visible through each tear gap. COLOUR GRADING: All three panels must share a unified warm earthy colour palette — terracotta orange, olive green, dusty gold, muted teal, warm browns. Match the mood and dominant colours of the source image. Apply subtle film grain across the entire collage. The final output must clearly look like THREE DISTINCT PHOTOGRAPHS torn and stacked together, NOT one single photo.";

        // Insert if not exists (using parameterized prompts to preserve {{ braces)
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Greeting Card Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (N'Greeting Card Collage', N'Personalised greeting card with photos and custom text',
                        @p0, N'PhotoCollage', N'💌', '#E91E63', 1, 141)",
            greetingCardPrompt);

        // Force-update existing prompt to ensure {{OCCASION}} and {{MESSAGE}} placeholders are present
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = 'Greeting Card Collage'",
            greetingCardPrompt);

        // Torn Edge Story
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Torn Edge Story')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (N'Torn Edge Story', N'3-panel torn-paper collage matching source mood',
                        @p0, N'PhotoCollage', N'🖼️', '#8D6E63', 1, 140)",
            tornEdgePrompt);

        // Force-update existing Torn Edge Story prompt
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = 'Torn Edge Story'",
            tornEdgePrompt);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "PhotoCollage StylePresets seeding failed (non-fatal)");
    }

    // ── Seed 14 additional PhotoCollage templates ──
    try
    {
        // --- Styles WITH {{OCCASION}}/{{MESSAGE}} placeholders (parameterized to preserve braces) ---

        const string birthdayBashPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW BIRTHDAY PARTY COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 5 panels arranged in a dynamic burst pattern. Panel 1 (centre, largest, circular frame): a glamorous portrait of the subject wearing a party hat or tiara, big smile, confetti falling around them. Panel 2 (upper-left, tilted 5°): the subject blowing out candles on a birthday cake, warm candlelight glow. Panel 3 (upper-right, tilted -5°): the subject laughing with arms raised in celebration. Panel 4 (lower-left, small square): a close-up of the subject with a surprised joyful expression, opening a gift. Panel 5 (lower-right, small square): full-body shot of the subject dancing or jumping with balloons. DECORATIONS: colourful helium balloons (gold, pink, blue) floating around panels, scattered confetti and streamers, illustrated star bursts, glitter particle overlay. TEXT: Render '{{OCCASION}}' in large bold fun bubble-letter font across the top — bright pink or gold with a slight 3D shadow effect. Render '{{MESSAGE}}' in a playful handwritten font on a small banner ribbon at the bottom. BACKGROUND: warm gradient from soft pink to lavender with bokeh light circles. Overall mood: joyful, celebratory, vibrant.";

        const string goldenAnniversaryPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW ELEGANT ANNIVERSARY COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 4 panels in a symmetric arrangement. Panel 1 (left, tall portrait): the subject in elegant formal attire, standing in a garden with soft golden-hour backlighting, romantic three-quarter pose. Panel 2 (upper-right, square): a close-up portrait with a warm loving smile, soft-focus floral background. Panel 3 (lower-right, square): the subject sitting on an ornate bench or by a fountain, contemplative elegant pose. Panel 4 (centre overlay, small oval with gold ornate frame): an intimate close-up, eyes sparkling. DECORATIONS: gold foil filigree borders and corner ornaments, scattered red and blush rose petals, thin gold line separators between panels, small illustrated hearts. TEXT: Render '{{OCCASION}}' in elegant serif font with gold metallic effect — centred between the panels. Render '{{MESSAGE}}' in flowing italic calligraphy on a cream banner with gold edges at the bottom. BACKGROUND: deep burgundy to cream gradient with a subtle damask pattern watermark. Colour palette: gold, burgundy, cream, blush pink. Overall mood: romantic, timeless, luxurious.";

        const string graduationPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW GRADUATION COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 6 panels in a structured academic grid. Panel 1 (top, wide panoramic): the subject in graduation cap and gown, tossing the cap in the air on a grand university campus with classical architecture. Panel 2 (middle-left, square): a formal portrait — the subject holding a diploma scroll, proud expression, studio-quality lighting. Panel 3 (middle-right, square): the subject in cap and gown walking through a tree-lined campus path, golden autumn light. Panel 4 (bottom-left, small portrait): the subject studying at a desk with books, determined expression — the journey. Panel 5 (bottom-centre, small portrait): the subject in professional attire, confident power pose — the future. Panel 6 (bottom-right, small portrait): candid shot of the subject celebrating with confetti, pure joy. DECORATIONS: navy and gold colour blocking borders, illustrated diploma scrolls and laurel wreaths, small star scatter accents, thin gold rule lines between panels. TEXT: Render '{{OCCASION}}' in bold uppercase serif font — navy blue with gold outline — as a banner across the top. Render '{{MESSAGE}}' in elegant script below the panels on a cream ribbon. BACKGROUND: navy blue with subtle geometric pattern. Colour palette: navy, gold, white, cream.";

        const string visionBoardPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW VISION BOARD COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 8 panels arranged in an irregular magazine-cutout style on a cork board background. Panel 1 (centre-top, largest): the subject in powerful confident pose, wearing stylish professional attire, city skyline behind them. Panel 2 (upper-left, tilted 5°): the subject working out or doing yoga, healthy and strong. Panel 3 (upper-right, small): the subject at a desk with a laptop, productive and focused. Panel 4 (middle-left, rectangular): the subject travelling — standing at an airport or with luggage, excited. Panel 5 (middle-right, circular): close-up of the subject with a radiant smile, glowing skin. Panel 6 (lower-left, square): the subject in a luxury car or beautiful home, aspirational lifestyle. Panel 7 (lower-centre, tilted): the subject celebrating an achievement, arms raised. Panel 8 (lower-right, small): the subject with friends, laughing together. DECORATIONS: colourful washi tape strips holding panels, magazine-style word cutouts scattered between panels (DREAM, CREATE, BELIEVE, THRIVE, MANIFEST) in bold mixed fonts, star and checkmark stickers, small illustrated icons (rocket, diamond, crown). TEXT: Render '{{MESSAGE}}' in large bold motivational poster font across an open area. BACKGROUND: light cork board texture with pin holes. Colour palette: bright pink, gold, teal, white, black typography. Overall mood: empowering, aspirational, energetic.";

        const string voguePrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW FASHION EDITORIAL COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 4 panels in an asymmetric magazine spread. Panel 1 (left side, full height, 60% width): a dramatic full-body high-fashion editorial shot of the subject in a couture outfit, striking model pose, dramatic studio lighting with strong shadows. Panel 2 (upper-right, square): an intense close-up beauty shot, flawless skin, bold makeup, piercing gaze. Panel 3 (middle-right, wide landscape): the subject walking on a runway or in an architectural space, dynamic movement, editorial energy. Panel 4 (lower-right, square): an artistic profile silhouette shot, dramatic backlighting creating a rim-light effect. DECORATIONS: thin elegant black rule lines separating right panels, mock magazine masthead at very top in ultra-thin uppercase tracking (VOGUE ARTFORGE), small page number and section label. TEXT: Render '{{OCCASION}}' in massive ultra-bold condensed sans-serif font overlaid semi-transparently on the hero panel — black or white depending on contrast. BACKGROUND: pure white with clean negative space. Colour palette: black, white, one single accent colour pulled from the source photo. Overall mood: high fashion, editorial, powerful, minimalist.";

        const string babyMilestonePrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW BABY MILESTONE COLLAGE on a vertical 2:3 portrait canvas. Use the person (baby or child) from the source photo — preserve their EXACT face and appearance. LAYOUT: 6 panels in a 2-column staggered scrapbook layout. Panel 1 (top-left, large): the baby/child in a white onesie or cute outfit, sitting on a fluffy blanket, big curious eyes, soft studio lighting. Panel 2 (top-right, medium, slightly lower): the baby/child reaching for a toy or playing with blocks, candid joyful moment. Panel 3 (middle-left, medium): the baby/child crawling or taking first steps, milestone moment, proud expression. Panel 4 (middle-right, large): a close-up of the baby/child's face with a big gummy smile, sparkling eyes. Panel 5 (bottom-left, medium): the baby/child being held up, arms spread like flying, pure delight. Panel 6 (bottom-right, medium): the baby/child sleeping peacefully, angelic, soft warm glow. DECORATIONS: soft watercolour pastel washes between panels (pink, mint, lavender), illustrated clouds and tiny stars as spacers, small crescent moon, cute illustrated animals (bunny, teddy bear, duckling) peeking around panel edges, milestone number badges (1, 2, 3...) as small circles on each panel. TEXT: Render '{{OCCASION}}' in soft rounded playful font — pastel pink or blue — at the top. Render '{{MESSAGE}}' in gentle handwritten cursive on a small cloud shape at the bottom. BACKGROUND: soft cream with very faint polka dot pattern. Colour palette: pastel pink, mint green, lavender, cream, baby blue.";

        const string retroPolaroidPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW RETRO POLAROID PARTY COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 6 Polaroid-frame panels scattered at random rotations (-15° to +15°) across the canvas, overlapping slightly. Each polaroid has a thick white border (extra wide at the bottom for captions). Polaroid 1 (centre, largest, tilted 3°): the subject making a fun silly face, peace sign, bright colourful background. Polaroid 2 (upper-left, tilted -10°): the subject laughing with head thrown back, pure joy. Polaroid 3 (upper-right, tilted 8°): a group-style shot of the subject with arms around friends (generate generic friends), party setting. Polaroid 4 (lower-left, tilted -5°): the subject dancing or jumping, dynamic action shot, motion energy. Polaroid 5 (lower-right, tilted 12°): close-up of the subject blowing a kiss or winking, playful. Polaroid 6 (bottom-centre, tilted -3°): the subject posing with sunglasses or a fun prop, cool and confident. DECORATIONS: colourful illustrated stickers scattered around (hearts, stars, smiley faces, rainbows, lips), small doodle drawings, glitter scatter effect, each Polaroid has a handwritten-style date or fun caption on the white bottom border. TEXT: Render '{{MESSAGE}}' as a large playful hand-drawn text across an open area, in bright colours with a slight wobble effect. BACKGROUND: bright retro pattern — colourful chevron, confetti, or geometric shapes. Colour palette: bright yellow, hot pink, electric blue, orange, white. Overall mood: fun, nostalgic, playful party.";

        const string luxuryPortfolioPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW LUXURY PORTFOLIO COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 5 panels in an asymmetric editorial arrangement. Panel 1 (left side, full height, 55% width): a stunning full-body hero shot of the subject in elegant professional attire, confident powerful pose, clean studio backdrop with soft gradient lighting. Panel 2 (upper-right, square): a beauty close-up — perfect lighting, sharp focus on face, professional headshot quality. Panel 3 (middle-right, square): the subject in a different outfit, three-quarter pose, architectural or luxury interior background. Panel 4 (lower-right, landscape): the subject in motion — walking confidently through a modern space, editorial energy. Panel 5 (bottom, thin strip spanning right column): an artistic detail shot — the subject's silhouette or a creative angle with dramatic light. DECORATIONS: thin gold rule lines separating the right-side panels, small monogram or logo placeholder in upper-right corner, clean negative space. TEXT: Render '{{OCCASION}}' in ultra-thin uppercase widely-spaced sans-serif font as a small header label. BACKGROUND: warm cream or light grey. Colour palette: cream, gold, charcoal, one accent colour from the source photo. Overall mood: luxurious, professional, editorial, aspirational.";

        const string bffsPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW FRIENDSHIP COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 5 panels arranged in a diagonal zigzag from top-left to bottom-right, each slightly overlapping the next. Panel 1 (upper-left, large, tilted 3°): the subject and a friend (generate a friendly-looking companion) taking a selfie together, big smiles, fun background. Panel 2 (upper-right, medium, tilted -4°): the subject and friends at a café or restaurant, laughing over food and drinks. Panel 3 (centre, largest, straight): the subject in the middle of a friend group, arms linked, walking down a sunny street, candid joyful energy. Panel 4 (lower-left, medium, tilted 5°): the subject and a friend doing a fun activity — shopping, dancing, or at a concert, dynamic and energetic. Panel 5 (lower-right, medium, tilted -3°): a heartfelt moment — the subject and friends in a group hug or sitting together watching a sunset, warm and emotional. DECORATIONS: bold colour-blocked background sections in complementary hues (purple, teal, coral, yellow) behind each panel, illustrated heart bursts and star doodles in the gaps, small friendship-themed stickers (BFF, heart hands), confetti scatter. TEXT: Render '{{MESSAGE}}' in large outlined bubble-letter word art across an open space — vibrant purple or teal. BACKGROUND: multicolour geometric colour blocks. Colour palette: purple, teal, coral, sunshine yellow, white. Overall mood: warm, energetic, celebratory, friendship.";

        const string seasonsGreetingsPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW HOLIDAY GREETING CARD COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 4 photo panels arranged in an L-shape flanking a central text area. Panel 1 (upper-left, portrait): the subject in a cosy winter sweater, standing by a decorated Christmas tree or fireplace, warm candlelight and fairy lights. Panel 2 (lower-left, square): the subject in winter outdoor setting — snow falling, wearing a scarf and beanie, rosy cheeks, happy smile. Panel 3 (upper-right, square): a close-up portrait of the subject holding a wrapped gift or a mug of hot cocoa, warm intimate lighting. Panel 4 (lower-right, portrait): the subject in elegant holiday party attire, festive background with bokeh lights and ornaments. CENTRAL TEXT AREA: a tall centre panel reserved for text, framed by the 4 photo panels. TEXT: Render '{{OCCASION}}' in large elegant serif font with gold metallic effect — centred and prominent. Render '{{MESSAGE}}' below in flowing italic calligraphy, dark green or gold. DECORATIONS: illustrated pine branches and holly berries bordering the entire canvas edge, gold foil snowflake scatter, small red berries and pinecone accents, thin gold ornamental border around each photo panel. BACKGROUND: deep forest green with subtle snowflake pattern. Colour palette: forest green, gold, red, cream, warm white fairy-light glow. Overall mood: festive, elegant, warm, celebratory.";

        // Insert styles with placeholders (parameterized)
        var placeholderStyles = new[]
        {
            ("Birthday Bash Burst", "Festive birthday collage with confetti, balloons, and fun panels", birthdayBashPrompt, "\U0001F382", "#FF6B6B", 142),
            ("Golden Anniversary", "Elegant romantic collage with gold foil accents and roses", goldenAnniversaryPrompt, "\U0001F48D", "#C9A84C", 143),
            ("Cap & Tassel Graduation", "Academic graduation collage with formal grid and achievement banners", graduationPrompt, "\U0001F393", "#1B3A6B", 144),
            ("Vision Board Manifest", "Motivational vision board with magazine cutouts and inspiring words", visionBoardPrompt, "\u2728", "#E91E8C", 147),
            ("Vogue Editorial Spread", "High-fashion magazine editorial layout with bold typography", voguePrompt, "\U0001F457", "#1A1A1A", 148),
            ("Baby Milestone Journey", "Soft whimsical baby milestone collage with pastel decorations", babyMilestonePrompt, "\U0001F37C", "#F9C6D0", 150),
            ("Retro Polaroid Party", "Playful scattered Polaroids on a colourful background with stickers", retroPolaroidPrompt, "\U0001F4F8", "#FFD700", 152),
            ("Luxury Portfolio", "High-end portfolio layout with hero panel and editorial grid", luxuryPortfolioPrompt, "\U0001F4BC", "#8B7355", 153),
            ("BFFs Friendship Collage", "Warm energetic collage for friends with colour-block backgrounds", bffsPrompt, "\U0001F46F", "#7B2D8B", 154),
            ("Season's Greetings Card", "Festive holiday greeting card with seasonal borders and elegant text", seasonsGreetingsPrompt, "\U0001F384", "#165B33", 155),
        };

        foreach (var (name, desc, prompt, emoji, accent, sort) in placeholderStyles)
        {
            await db.Database.ExecuteSqlRawAsync(@"
                IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                    INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                    VALUES (@p0, @p1, @p2, N'PhotoCollage', @p3, @p4, 1, @p5)",
                name, desc, prompt, emoji, accent, sort);
            // Force-update prompt to latest version
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
                prompt, name);
        }

        // --- Styles WITHOUT placeholders (safe to inline) ---
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Passport Stamps Adventure')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Passport Stamps Adventure', N'Travel wanderlust collage with map background and vintage stamps',
                 N'DO NOT reproduce the source photo. Generate a BRAND NEW TRAVEL ADVENTURE COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 5 panels scattered organically as if travel snapshots tossed on a map. Panel 1 (centre-left, largest, slightly tilted): the subject with a backpack, standing before an iconic landmark (Eiffel Tower, Colosseum, or similar), golden-hour lighting. Panel 2 (upper-right, polaroid-style with white border): the subject sitting at a quaint European café, enjoying coffee. Panel 3 (lower-left, rounded corners): the subject hiking on a mountain trail, vast landscape behind them. Panel 4 (lower-right, tilted -8°): the subject at a tropical beach, relaxed and happy, turquoise water. Panel 5 (upper-left, small square): the subject in a busy Asian street market, vibrant colours and lanterns. DECORATIONS: vintage world map as background (muted sepia tones), circular passport stamp overlays on corners of each panel (with dates and city names), decorative compass rose, illustrated airplane trail dotted line connecting the panels, airmail stripe border on some panels. No text placeholders. BACKGROUND: aged parchment world map texture with coffee-stain effects. Colour palette: warm sepia, teal, terracotta, muted greens. Overall mood: adventurous, nostalgic, wanderlust.',
                 N'PhotoCollage', N'✈️', '#2E8B57', 1, 145);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Family Gallery Wall')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Family Gallery Wall', N'Cosy gallery wall collage with mismatched frames on a warm background',
                 N'DO NOT reproduce the source photo. Generate a BRAND NEW FAMILY GALLERY WALL COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 7 panels arranged as mismatched picture frames on a warm-toned wall. Frame 1 (centre, largest, ornate gold frame): a warm family-style portrait of the subject, smiling naturally in a cosy living room setting. Frame 2 (upper-left, simple white frame, portrait): the subject cooking in a kitchen, candid and happy. Frame 3 (upper-right, rustic wood frame, square): the subject reading a book on a window seat, soft natural light. Frame 4 (left, thin black frame, landscape): the subject walking in a park during autumn, golden leaves. Frame 5 (right, vintage oval frame): close-up portrait with a gentle smile, warm soft focus. Frame 6 (lower-left, distressed wood frame, small): the subject gardening or doing a hobby, hands busy. Frame 7 (lower-right, modern thin frame, small): the subject laughing, candid moment of pure joy. DECORATIONS: string lights draped across the top of the wall, small potted plant on a floating shelf between frames, the wall itself has a warm honey-beige paint texture with subtle imperfections. Each frame style is DIFFERENT (ornate, simple, rustic, vintage). No text placeholders. BACKGROUND: warm honey-beige painted wall texture. Colour palette: warm browns, honey, cream, forest green, burnt orange.',
                 N'PhotoCollage', N'🏡', '#D4845A', 1, 146);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Minimal Nordic Grid')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Minimal Nordic Grid', N'Clean 9-panel grid with generous white space and muted tones',
                 N'DO NOT reproduce the source photo. Generate a BRAND NEW MINIMALIST GRID COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 9 panels in a perfect 3x3 symmetric grid with equal white gutters (approximately 3% canvas width between each panel). All panels are identical square size. Panel 1 (top-left): soft portrait of the subject in neutral tones, looking away, natural window light. Panel 2 (top-centre): close-up of the subject hands holding a coffee cup or book, warm minimal setting. Panel 3 (top-right): the subject from behind, walking on a quiet minimalist street or hallway. Panel 4 (middle-left): the subject sitting in a clean Scandinavian-style interior, reading or resting. Panel 5 (middle-centre): a direct-gaze close-up portrait, soft natural light, gentle expression. Panel 6 (middle-right): the subject near a window, silhouette partially lit, contemplative. Panel 7 (bottom-left): the subject in a nature setting — misty forest or lake shore, solitude. Panel 8 (bottom-centre): an architectural detail shot with the subject small in frame, negative space. Panel 9 (bottom-right): the subject smiling softly, warm candid moment, slightly overexposed. DECORATIONS: NONE — absolutely clean, no borders beyond the white gutters, no text, no stickers. Only a thin hairline border around the entire grid. BACKGROUND: pure white. Colour palette: muted sage, warm grey, cream, soft blush — desaturated and cohesive across all 9 panels. Overall mood: calm, serene, Scandinavian minimalism.',
                 N'PhotoCollage', N'▪️', '#B8C4BB', 1, 149);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'A Day in My Life')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'A Day in My Life', N'Cinematic film-strip collage capturing morning-to-night moments',
                 N'DO NOT reproduce the source photo. Generate a BRAND NEW FILM-STRIP DAY-IN-THE-LIFE COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 6 panels arranged as a vertical film strip running down the canvas. Each panel is a wide cinematic landscape-ratio frame. Panel 1 (top, labelled 7:00 AM): the subject waking up, stretching in bed, soft golden morning light streaming through curtains. Panel 2 (labelled 9:00 AM): the subject having breakfast at a sunny kitchen table or café, coffee in hand. Panel 3 (labelled 12:00 PM): the subject at work or creative activity — at a desk, painting, or in a meeting, focused and productive. Panel 4 (labelled 3:00 PM): the subject outdoors during a break — walking in a park, at a café terrace, or exercising, relaxed energy. Panel 5 (labelled 7:00 PM): the subject at dinner — cooking at home or at a restaurant, warm ambient lighting. Panel 6 (labelled 10:00 PM): the subject relaxing at night — reading on a couch, watching stars from a balcony, or winding down, cosy and peaceful. DECORATIONS: authentic 35mm film perforation holes running along both sides of the strip, dark brown film border, each panel has a small timestamp label in monospace font (Courier style) below it. Thin frame lines between each panel. BACKGROUND: dark espresso brown filmstrip border with the canvas showing between the perforations. Colour palette: warm cinematic tones — golden morning, bright midday, amber evening, cool blue night, progressing naturally. Overall mood: cinematic, storytelling, warm nostalgia.',
                 N'PhotoCollage', N'🎞️', '#3D2B1F', 1, 151);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Additional PhotoCollage templates seeding failed (non-fatal)");
    }

    // ── Seed 6 new PhotoCollage layout styles ──
    try
    {
        const string pastoralPolaroidPrompt = "DO NOT simply reproduce the source photo. You MUST generate a BRAND NEW composite image that is a POLAROID COLLAGE OVER A FULL-BLEED BACKGROUND PHOTO on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance in every panel. BACKGROUND: A single large full-bleed lifestyle photograph filling the entire canvas — the same person standing or walking in a scenic countryside setting with a wooden fence, green meadows, and warm golden-hour sunlight. Soft warm vintage colour grading — faded golds, blush pinks, sage greens. The person wears a charming outfit like a gingham dress or linen blouse, holding wildflowers or a straw hat. THREE POLAROID FRAMES overlaid on the LEFT SIDE of the canvas, slightly overlapping, each tilted at different casual angles (3-8 degrees). Each polaroid has a thick white instant-photo border (wider at the bottom like a real instant photo). Polaroid 1 (top-left area): a close-up of the person sitting on a blanket, soft dreamy expression, flowers in hair. Polaroid 2 (middle-left area): a three-quarter portrait, person smelling a bouquet of wildflowers, gentle smile. Polaroid 3 (bottom-left area): the person from behind or side, arranging flowers on a picnic blanket, candid moment. Each polaroid casts a subtle drop shadow onto the background. COLOUR GRADING: unified warm pastoral palette — soft peach, cream, blush pink, sage green, warm gold. Subtle film grain and slight vignette across the entire image. The final output must look like physical polaroid prints casually placed on top of a printed background photograph.";

        const string natureCircleGridPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW GEOMETRIC NATURE COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: The canvas is divided into 5 panels by DIAGONAL WHITE LINES (approximately 4-5px thick) radiating from the centre, creating 4 triangular/trapezoidal corner sections. In the EXACT CENTRE of the canvas sits a LARGE CIRCLE (approximately 40% of canvas width) with a thick white circular border (4-5px). CORNER PANELS — Top-left triangle: lush green forest scene — tall pine trees beside a calm lake, the person standing small among the trees, peaceful contemplation. Deep green tones. Top-right triangle: dramatic mountain landscape — misty snow-capped peaks with overcast sky, the person hiking a trail, sense of adventure. Cool grey-blue tones. Bottom-left triangle: dramatic natural formation — a cave entrance or rocky gorge with green moss, the person exploring, sense of wonder. Warm earthy greens and browns. Bottom-right triangle: volcanic or coastal landscape — distant mountain with tropical vegetation in foreground, the person gazing at the vista. Teal and warm brown tones. CENTRE CIRCLE: a beautiful panoramic lake scene — crystal clear water reflecting blue sky and surrounding green hills, the person at the water's edge, serene moment. Vibrant blue and green. WHITE DIVIDERS between all panels must be clean, crisp, and consistent width. No decorations or text. COLOUR PALETTE: rich natural greens, deep blues, cool greys, earthy browns — each panel has its own dominant tone but all share a cohesive nature photography aesthetic. The final image must look like a professionally designed geometric photo collage of nature adventures.";

        const string fashionWavePrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW FASHION EDITORIAL COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: The canvas is divided into 5-6 panels separated by smooth CURVED S-WAVE DIVIDERS — elegant flowing sinusoidal curves that sweep from left to right edge, creating organic ribbon-like horizontal bands across the canvas. The curves should be smooth and flowing like gentle waves, NOT straight lines. PANELS — Panel 1 (top, largest ~30%): a dramatic hero portrait — the subject in an elegant dark outfit, flowing hair, confident pose, sophisticated neutral background. Rich warm colour with cinematic lighting. Panel 2 (upper-middle, ~15%): a black-and-white editorial shot — the subject seated on a chair, dynamic pose with interesting arm placement, dramatic studio lighting with hard shadows. High contrast B&W. Panel 3 (middle, ~20%): another black-and-white shot — the subject in motion, flowing skirt or dramatic gesture, artistic movement blur, high fashion editorial style. Panel 4 (lower-middle, ~15%): colour shot — the subject in an urban setting, walking past elegant architecture, street-style candid feeling. Warm muted tones. Panel 5 (bottom, ~20%): the subject in profile or three-quarter view, looking away, moody atmospheric lighting, dark sophisticated setting. CRITICAL: The curved wave dividers must be smooth and flowing. Some panels are full colour, some are black-and-white — this contrast is essential. COLOUR PALETTE: dark sophisticated — charcoal, warm taupe, cream, with muted warm accents. Black-and-white panels should be true monochrome with rich tonal range. Overall mood: high-fashion editorial magazine spread.";

        const string graduationCelebPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW GRADUATION CELEBRATION COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: The canvas is divided into TWO COLUMNS. LEFT COLUMN (approximately 55-60% width): A single LARGE hero photograph filling the entire left side from top to bottom. RIGHT COLUMN (approximately 40-45% width): THREE stacked horizontal panels of roughly equal height, separated by thin white gaps (3px). LEFT HERO PANEL: The subject photographed from behind, wearing a graduation cap and gown, walking along a tree-lined autumn campus path. Beautiful warm golden-hour backlighting creating a silhouette rim-light effect. Fallen autumn leaves on the ground — warm oranges, yellows, reds. Sense of stepping forward into the future. RIGHT TOP PANEL: The subject in cap and gown, facing camera, holding a diploma with a proud beaming smile. Indoor or outdoor ceremony setting, warm lighting. RIGHT MIDDLE PANEL: A candid celebration moment — the subject tossing the graduation cap in the air, arms raised in joy, big laugh. Blue sky or confetti background. RIGHT BOTTOM PANEL: A close-up portrait — the subject smiling warmly and genuinely, tassel hanging on the cap, soft bokeh background, emotional happy moment. COLOUR GRADING: warm golden autumn palette throughout — amber, burnt orange, warm brown, cream. Slightly desaturated and cohesive across all panels. Thin white borders separate the right panels. No text or decorations. The final image must look like a professional graduation photo collage celebrating achievement.";

        const string beachGeometricPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW GEOMETRIC BEACH COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. IMPORTANT: The person MUST appear in at least 5 of the 6 sections in different poses and beach activities. LAYOUT: The canvas is divided into 6 triangular and trapezoidal sections by SHARP DIAGONAL LINES crossing the canvas at various angles, creating a dynamic geometric mosaic. The dividers are thin white lines (3-4px). The diagonal cuts should create a mix of triangles and irregular quadrilaterals — like shards of a broken mirror reassembled. PANELS — Section 1 (top-left triangle): overhead beach view with white umbrellas and thatched palapas, turquoise water, the person walking barefoot on white sand. Bright and airy. Section 2 (top-right trapezoid): the person standing at a tropical beach resort — leaning against a palm tree, wearing stylish beachwear, crystal clear turquoise water behind them. Vibrant saturated blues and greens. Section 3 (centre-left): the person in a summer outfit and sun hat, relaxing on the beach, golden warm lighting, candid smile. Warm yellows and whites. Section 4 (centre-right): the person sitting at the water's edge with turquoise waves gently reaching their legs, holding a tropical cocktail, looking at the camera with a relaxed smile. Vivid teal and aqua tones. Section 5 (bottom-left triangle): the person splashing through shallow ocean waves with white foam, joyful and playful, dynamic movement. Dynamic water texture. Section 6 (bottom-right triangle): the person silhouetted or warmly lit against a panoramic sunset beach view — golden sand stretching to the horizon, tropical hillside in background, warm sunset glow. COLOUR PALETTE: vibrant tropical — turquoise, aqua, golden yellow, warm sand beige, bright white. High saturation and contrast. Each section should feel like a different vibrant beach photograph featuring the same person in a different moment. The final image must look like a dynamic geometric photo mosaic of a tropical beach vacation.";

        const string tornPaperMoodPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW TORN PAPER MOOD BOARD COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. LAYOUT: 4-5 photographs arranged in an organic, overlapping scrapbook layout. Each photo has IRREGULAR TORN/RIPPED PAPER EDGES — jagged, uneven tears with visible white paper fibres along the rip lines. Photos overlap casually at slight angles as if ripped from magazines and arranged on a surface. PHOTOS — Photo 1 (top-left, largest ~35%): two shots of the subject side by side or a wide editorial pose — full body in a chic all-black outfit (blazer, belt, wide-leg trousers), strong confident stance. Warm neutral studio background. Photo 2 (top-right area): extreme close-up of hands wearing delicate gold rings and bracelets, elegant manicure, reaching or gesturing artistically. Warm skin tones against muted background. Photo 3 (bottom-left): close-up detail shot — the subject's profile showing a statement earring, elegant hairstyle, neck and jawline. Soft warm side lighting. Photo 4 (bottom-right): the subject in another sophisticated dark outfit, candid three-quarter pose, carrying a designer bag, urban backdrop slightly visible. TEXT ELEMENTS: 1-2 small torn paper strips with hand-stamped or typewriter-style text — words like 'fine and detail' or 'AMAZING' in a grungy distressed font. These text strips are small accent pieces, not dominant. BACKGROUND: warm beige/cream paper texture visible through the gaps between torn photos. COLOUR PALETTE: dark moody fashion — black, charcoal, warm beige, cream, gold accents. The photos themselves are warm-toned with muted saturation. Overall mood: high-end fashion mood board or editorial tear sheet with an artisanal handmade feel.";

        var newCollageStyles = new[]
        {
            ("Pastoral Polaroid", "Polaroid frames scattered over a dreamy countryside backdrop", pastoralPolaroidPrompt, "\U0001F33B", "#F8BBD0", 160),
            ("Nature Circle Grid", "Diagonal grid with central circle showcasing nature scenes", natureCircleGridPrompt, "\U0001F332", "#2E7D32", 161),
            ("Fashion Wave", "Curved S-wave panels with editorial fashion photography", fashionWavePrompt, "\U0001F5DE\uFE0F", "#37474F", 162),
            ("Graduation Celebration", "Large hero panel with stacked milestone moments", graduationCelebPrompt, "\U0001F393", "#F57F17", 163),
            ("Beach Geometric", "Dynamic diagonal triangular cuts with vibrant beach scenes", beachGeometricPrompt, "\U0001F3D6\uFE0F", "#00ACC1", 164),
            ("Torn Paper Mood Board", "Ripped paper edges with moody fashion editorial aesthetic", tornPaperMoodPrompt, "\U0001F4CB", "#5D4037", 165),
        };

        foreach (var (name, desc, prompt, emoji, accent, sort) in newCollageStyles)
        {
            await db.Database.ExecuteSqlRawAsync(@"
                IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                    INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                    VALUES (@p0, @p1, @p2, N'PhotoCollage', @p3, @p4, 1, @p5)",
                name, desc, prompt, emoji, accent, sort);
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
                prompt, name);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "New PhotoCollage layout styles seeding failed (non-fatal)");
    }

    // ── Seed 3 calendar/reception PhotoCollage styles ──
    try
    {
        const string weddingCalendarPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW WEDDING CALENDAR COLLAGE on a vertical 2:3 portrait canvas. Use the subject(s) from the source photo — preserve their EXACT face(s), hair, and appearance. IMPORTANT: Include ONLY the exact number of people from the source image. If the source has 1 person, show only that person in elegant wedding attire throughout. If the source has 2 people, show them as a couple. LAYOUT: The canvas has a DARK BLACK/DEEP CHARCOAL BACKGROUND. RIGHT SIDE (55-60% width): A single large HERO PHOTOGRAPH of the subject(s) in traditional wedding attire — elegant formal wear, vibrant silk saree with gold jewellery or classic suit/sherwani. Seated in a lush garden or outdoor setting with green foliage, soft lantern glow, and romantic ambient lighting. Warm and intimate. This photo extends from top to about 75% of the canvas height. LEFT SIDE (40-45% width): 4-5 SMALLER POLAROID-STYLE PHOTOS stacked vertically, each slightly tilted at casual angles (2-6 degrees) with thick white instant-photo borders. Each polaroid shows a COMPLETELY DIFFERENT wedding moment: Polaroid 1 (top): a dramatic outdoor shot — flowing veil or dupatta caught in the wind, grand architectural backdrop. Polaroid 2: glamorous black evening attire at a pre-wedding event, fairy lights behind. Polaroid 3: a close-up emotional moment — soft warm lighting, intimate expression. Polaroid 4: a fun candid pose — dancing, laughing, or playful moment at the celebration. Polaroid 5 (bottom): a formal portrait with traditional decor — marigold garlands, ornate mandap background. Each polaroid casts a subtle drop shadow on the dark background. BOTTOM-LEFT AREA: {{CALENDAR}} BOTTOM-RIGHT AREA: Elegant calligraphy text reading '{{OCCASION}}' in warm gold or cream script font. Below that, '{{MESSAGE}}' in a decorative script with small ornamental flourishes. COLOUR PALETTE: deep black background, warm gold accents, vibrant colours (blue, red, green), white polaroid borders, warm skin tones. Overall mood: elegant, romantic, celebratory wedding.";

        const string birthdayCalendarPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW BIRTHDAY CALENDAR COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. BACKGROUND: Soft white/cream paper texture with delicate watercolour FLORAL DECORATIONS in the corners — soft pink roses, blush peonies, dusty rose blooms with sage green leaves and small golden leaf sprigs scattered organically. The flowers are concentrated in the TOP-RIGHT and BOTTOM-LEFT corners, fading gently into the white background. PHOTO LAYOUT: THREE CIRCULAR PHOTOS arranged in a triangular composition on the canvas. Each circle has a soft WATERCOLOUR SPLASH BORDER — irregular artistic paint splatter edges in muted warm tones (soft brown, dusty rose, warm grey) that bleed outward from the circle, creating a hand-painted frame effect. Circle 1 (top-left, largest ~35% canvas width): the subject outdoors in a beautiful outfit, soft golden bokeh background, dreamy three-quarter portrait with warm autumn light. Circle 2 (middle-right, medium ~30%): a close-up portrait of the subject with a big joyful smile, sparkling eyes, natural warm lighting. Circle 3 (bottom-centre, medium ~28%): the subject in a different outfit, full or three-quarter body, elegant pose, perhaps seated or in a playful candid moment. LEFT SIDE (below Circle 1): {{CALENDAR}} Simple elegant typography in dark grey. TOP-RIGHT AREA (above or near Circle 2): Large elegant CALLIGRAPHY TEXT reading 'Happy Birthday' in flowing script — dark charcoal or soft black ink, with the name '{{MESSAGE}}' below in matching calligraphic style. BOTTOM AREA: A handwritten-style QUOTE or birthday wish in elegant italic cursive — warm dark grey text, 2-3 lines of heartfelt message. Small decorative flourish underneath. COLOUR PALETTE: soft white, cream, dusty rose pink, sage green, warm grey, golden accents. Watercolour texture throughout. Overall mood: soft, feminine, elegant, celebratory.";

        const string receptionDoublePrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW CINEMATIC RECEPTION POSTER on a vertical 2:3 portrait canvas. Use the subject(s) from the source photo — preserve their EXACT face(s), hair, and appearance. IMPORTANT: Include ONLY the exact number of people from the source image. If the source has 1 person, show only that person throughout — use a single dramatic face in the double exposure and a single elegant portrait below. If the source has 2 people, show them as a couple. This should look like a MOVIE POSTER for a celebration/reception. LAYOUT — TOP HALF (60% of canvas): A dramatic DOUBLE EXPOSURE / OVERLAY effect. A large, softly DESATURATED close-up of the subject's face(s) in PROFILE — rendered in muted blue-grey/silver monochrome tones, large scale, filling the upper portion. The edges fade and dissolve into smoke, mist, or soft particle effects. The face(s) should be semi-transparent, blending into the dark background like a ghostly cinematic overlay. BOTTOM HALF (40% of canvas): A smaller but VIVID, FULL-COLOUR photograph of the subject(s) — in a gorgeous embellished outfit (lehenga, gown, saree with heavy jewellery, or sharp suit/sherwani). Elegant pose, smiling warmly. Professional studio-quality lighting with warm golden tones. This photo is crisp and vibrant, contrasting with the desaturated overlay above. TRANSITION between top and bottom: Smooth gradient dissolve with smoky/misty particles, soft bokeh light orbs (warm gold and cool blue), creating a dreamy cinematic blend. No hard edges. CENTRE TEXT AREA (between the overlay and colour photo): Elegant script text — '{{MESSAGE}}' in flowing calligraphy, warm gold or cream colour. Below that, the word 'RECEPTION' in large, wide-spaced, elegant serif or art-deco uppercase font — gold metallic effect with subtle glow. BACKGROUND: Deep dark charcoal to black gradient, with subtle blue-teal tint in the shadows. Floating bokeh light particles scattered throughout. COLOUR PALETTE: dark charcoal, silver-grey (for double exposure), warm gold (for text and highlights), rich warm tones in the colour photo. Overall mood: cinematic, dramatic, romantic, premium reception invitation.";

        var calendarStyles = new[]
        {
            ("Wedding Calendar", "Dark elegant wedding collage with calendar grid and polaroid moments", weddingCalendarPrompt, "\U0001F4C5", "#FFD700", 166),
            ("Birthday Calendar", "Soft floral birthday collage with watercolor frames and calendar", birthdayCalendarPrompt, "\U0001F382", "#F48FB1", 167),
            ("Reception Double Exposure", "Cinematic movie-poster style with smoky double exposure overlay", receptionDoublePrompt, "\U0001F3AC", "#263238", 168),
        };

        foreach (var (name, desc, prompt, emoji, accent, sort) in calendarStyles)
        {
            await db.Database.ExecuteSqlRawAsync(@"
                IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                    INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                    VALUES (@p0, @p1, @p2, N'PhotoCollage', @p3, @p4, 1, @p5)",
                name, desc, prompt, emoji, accent, sort);
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
                prompt, name);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Calendar/reception PhotoCollage styles seeding failed (non-fatal)");
    }

    // ── Seed 5 strip/poster/figurine/motherhood styles ──
    try
    {
        const string weddingStripPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW WEDDING STRIP COLLAGE on a vertical 2:3 portrait canvas. Use the subject(s) from the source photo — preserve their EXACT face(s), hair, and appearance in every panel. IMPORTANT: Include ONLY the exact number of people from the source image. If the source has 1 person, show only that person in different wedding outfits and poses throughout. If the source has 2 people, show them together as a couple. BACKGROUND: Soft cream/ivory textured linen canvas. LAYOUT — TOP HALF (55% of canvas): THREE VERTICAL RECTANGULAR PHOTO STRIPS arranged side by side with thin white gaps (4px) between them. Each strip is tall and narrow (roughly 30% canvas width × 50% canvas height). Strip 1 (left): the subject(s) in formal wedding attire — luxurious bridal outfit or classic suit/sherwani — standing in an ornate decorated venue with warm golden lighting, elegant chandeliers. Three-quarter body shot, elegant pose. Strip 2 (centre): a candid outdoor moment — walking through a beautiful garden or along a scenic path with soft golden-hour sunlight. Different outfit or slightly different styling from strip 1. Joyful natural expressions. Strip 3 (right): a close-up portrait — intimate expression, soft dreamy bokeh background, warm tones. Different setting from the other two strips. BOTTOM HALF (45% of canvas): A single LARGE HERO PHOTOGRAPH of the subject(s) — full body or three-quarter, in stunning formal attire, standing in a grand setting (palace steps, ornate archway, elegant ballroom). The top edge of this hero photo OVERLAPS slightly with the bottom edges of the three strips above, creating depth. Professional dramatic lighting, rich warm tones. TEXT OVERLAYS: Large translucent calligraphy text '{{MESSAGE}}' elegantly overlaid across the centre of the canvas in warm gold or soft white — the text flows gracefully over the transition between strips and hero photo. COLOUR PALETTE: warm ivory, cream, soft gold, rich warm skin tones, blush pink accents. Unified warm colour grading across all panels. Overall mood: elegant, romantic, premium wedding album.";

        const string trueLoveSmokePrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW DRAMATIC LOVE POSTER on a vertical 2:3 portrait canvas. Use the subject(s) from the source photo — preserve their EXACT face(s), hair, and appearance. IMPORTANT: Include ONLY the exact number of people from the source image. If the source has 1 person, show only that person in both sections — a solo dramatic profile top and solo portrait bottom. If the source has 2 people, show them together. LAYOUT — TOP HALF (50-55% of canvas): A large desaturated close-up of the subject(s) in PROFILE — rendered in muted warm grey/sepia tones, slightly faded and dreamlike. Dramatic GOLDEN-YELLOW SMOKE and fine particle effects dissolve upward from the bottom of this section, partially obscuring and blending with the subject's form(s). The smoke is thick, swirling, and vivid amber/gold in colour, creating a magical dissolving effect. The face(s) emerge from the smoke with an intense emotional gaze. The overall tone is cinematic and atmospheric. TOP-RIGHT CORNER: Bold elegant text '{{OCCASION}}' in large stylish serif or script font — cream or warm gold colour. Below it in smaller refined type: '{{MESSAGE}}'. BOTTOM HALF (45-50% of canvas): A VIVID FULL-COLOUR photograph of the same subject(s) — in an elegant dark outfit (black dress, formal wear, or complementary dark attire). Confident warm expression, holding flowers (roses or a bouquet). The background is a rich VIVID AMBER/GOLDEN-YELLOW gradient, warm and glowing. Professional studio-quality lighting with warm golden highlights. This bottom image is crisp, saturated, and vibrant — dramatically contrasting with the desaturated top. TRANSITION: The golden smoke from the top section gradually blends into the warm background of the bottom section. No hard dividing line — a smooth cinematic gradient dissolve with floating golden particles and soft bokeh orbs. COLOUR PALETTE: deep warm gold, amber, rich yellow, charcoal grey (in desaturated top), dark black outfits, warm skin tones. Overall mood: dramatic, cinematic love story poster.";

        const string birthdayStripPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW BIRTHDAY STRIP PORTRAIT COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance in every panel. BACKGROUND: Clean pure WHITE background with very subtle soft shadows. LAYOUT — LEFT SIDE (40-45% of canvas width): A single FULL-BODY PORTRAIT of the subject — seated or standing in an elegant outfit (stylish dress, sharp casual wear, or formal attire), relaxed confident pose. Professional studio lighting, soft shadows on the white background. Vibrant full colour with warm skin tones. RIGHT SIDE (55-60% of canvas width): 2-3 HORIZONTAL RECTANGULAR PHOTO STRIPS arranged at slight angles (3-7 degrees tilt), stacked vertically with casual spacing. Each strip is a different photo: Strip 1 (top, slight clockwise tilt): a COLOUR candid shot — the subject laughing or in a joyful pose, different outfit or setting from the main portrait. Warm golden tones. Strip 2 (middle, slight counter-clockwise tilt): a BLACK-AND-WHITE artistic portrait — the subject in a dramatic or contemplative pose, high contrast monochrome, studio lighting. Strip 3 (bottom, slight clockwise tilt): another COLOUR lifestyle shot — the subject outdoors or in a different setting, bright and cheerful, natural light. Each strip has a subtle drop shadow on the white background. DECORATIVE ELEMENTS: A small decorative butterfly or floral element near the strips — delicate, not overwhelming. BOTTOM-RIGHT AREA: Beautiful decorative calligraphy text 'Happy Birthday' in an elegant flowing script — warm gold, rose gold, or dark charcoal colour. Below it: '{{MESSAGE}}' in matching but slightly smaller decorative font. A thin ornamental swirl or flourish underneath. COLOUR PALETTE: clean white, warm skin tones, pops of colour from outfits, gold or rose gold text accents. Mix of colour and B&W photos for visual interest. Overall mood: clean, modern, elegant birthday celebration.";

        const string crystalFigurinePrompt = "Transform the subject(s) into a stunning MINIATURE CRYSTAL GLASS FIGURINE sculpture. Render ONLY the exact number of people from the source photo as delicate transparent crystal/glass statuettes — approximately 6-8 inches tall, standing under a tiny clear glass umbrella. If the source has 1 person, create 1 figurine only. If the source has 2 people, create 2 figurines together. The glass figures capture the exact facial features and appearance of the subject(s). The crystal is PERFECTLY TRANSPARENT with subtle internal refractions, prismatic rainbow light catches, and smooth polished surfaces. Fine glass details: flowing hair strands in glass, delicate clothing folds, tiny glass flowers or accessories. The figurine(s) stand on a small reflective surface — wet cobblestones or a polished stone base. ENVIRONMENT: Gentle rain falling around the figurine(s) — individual water droplets visible, some drops splashing on the umbrella and the surface around them. Scattered autumn leaves (tiny, in warm orange and red) around the base. The background is a soft dreamy BOKEH — warm golden and teal-green out-of-focus lights suggesting an autumn evening park scene. Shallow depth of field, macro photography style as if shot with a 100mm macro lens at f/2.8. LIGHTING: Warm golden backlight creating rim lighting on the glass edges, soft fill light illuminating the internal crystal structure, tiny sparkles and light refractions throughout. Water droplets on the glass surface catching the light. COLOUR PALETTE: crystal clear glass, warm amber/gold backlighting, cool teal-green bokeh, warm autumn leaf colours, silver rain streaks. The overall image should look like a professional macro photograph of an exquisite hand-blown glass art piece.";

        const string motherhoodPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW MOTHERHOOD CELEBRATION COLLAGE on a vertical 2:3 portrait canvas. Use the subject(s) from the source photo — preserve their EXACT face(s), hair, and appearance in every panel. IMPORTANT: Include ONLY the exact number of people from the source image. If the source has 1 person, show only that person throughout in different maternity poses. If the source has 2 people, show them together. BACKGROUND: A large FADED desaturated photograph of the subject fills the entire canvas as a background watermark — rendered in very soft sepia/olive tones at ~20-25% opacity, creating a subtle textured backdrop. The subject in this background is shown in a gentle maternal pose (cradling belly, looking down softly, or standing in profile). COLOUR OVERLAY on the background: warm olive-green and soft sepia gradient wash, muted and dreamy. PHOTO FRAMES: THREE ROUNDED-RECTANGLE PHOTO FRAMES arranged in a pleasing composition on the canvas — one large (top-right area, ~35% canvas width), one medium (left-centre area, ~30%), one medium (bottom-right area, ~28%). Each frame has THICK ROUNDED CORNERS (border-radius ~15-20px) and a subtle warm cream/gold border (3-4px). Frame 1 (top-right, largest): the subject(s) in elegant white/cream outfits during a maternity photoshoot — outdoors at golden hour, warm sunset light, showing the baby bump. Soft warm golden tones. Frame 2 (left-centre): a tender close-up — the subject looking down at the baby bump with hands gently cradling, soft natural light, intimate emotional moment. Warm sepia-touched colour grading. Frame 3 (bottom-right): the subject(s) in another setting — perhaps silhouetted against a sunset, or in a garden/beach, playful or joyful moment, different outfit from other frames. Warm amber tones. TEXT ELEMENTS — CENTRE-LEFT AREA: An elegant quote in refined serif italic font: '{{OCCASION}}' — rendered in warm dark olive or charcoal, 2-3 lines of poetic text. BOTTOM-CENTRE: Large bold artistic text '{{MESSAGE}}' in an elegant display font — warm olive-green or deep gold colour, commanding presence. A thin decorative botanical vine or leaf element above or below the text. COLOUR PALETTE: warm olive-green, soft sepia, cream, golden amber, muted warm tones throughout. Unified warm vintage colour grading. Overall mood: warm, tender, celebratory, maternal elegance.";

        var stripPosterStyles = new[]
        {
            ("Wedding Strip Collage", "Vertical photo strips with hero shot and elegant text overlay", weddingStripPrompt, "PhotoCollage", "\U0001F492", "#D4AF37", 169),
            ("True Love Smoke Poster", "Dramatic golden smoke double-tone poster with bold love text", trueLoveSmokePrompt, "PhotoCollage", "\U0001F49B", "#FFB300", 170),
            ("Birthday Strip Portrait", "Clean white layout with full-body shot and angled photo strips", birthdayStripPrompt, "PhotoCollage", "\U0001F381", "#E91E63", 171),
            ("Crystal Glass Figurine", "Miniature transparent glass sculpture in rain", crystalFigurinePrompt, "Artistic", "\U0001F48E", "#80DEEA", 172),
            ("Motherhood Collage", "Warm sepia-toned collage with rounded frames and motherhood text", motherhoodPrompt, "PhotoCollage", "\U0001F932", "#827717", 173),
        };

        foreach (var (name, desc, prompt, cat, emoji, accent, sort) in stripPosterStyles)
        {
            await db.Database.ExecuteSqlRawAsync(@"
                IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                    INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                    VALUES (@p0, @p1, @p2, @p3, @p4, @p5, 1, @p6)",
                name, desc, prompt, cat, emoji, accent, sort);
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
                prompt, name);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Strip/poster/figurine/motherhood styles seeding failed (non-fatal)");
    }

    // ── Seed 3 birthday calendar/double-exposure PhotoCollage styles ──
    try
    {
        const string darkBirthdayCalendarPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW DARK BIRTHDAY CALENDAR COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance in every panel. IMPORTANT: Every polaroid and the hero photo must show the SAME person with IDENTICAL facial features — same eyes, nose, mouth, jawline. Do NOT generate different-looking faces across panels. LAYOUT: The canvas has a DARK BLACK/DEEP CHARCOAL BACKGROUND with a subtle vignette. RIGHT SIDE (55-60% width): A single large HERO PHOTOGRAPH of the subject — the person in a stylish outfit, outdoors in a colourful or scenic setting (urban street, garden, park). Natural warm lighting with a slight cinematic colour grade. The subject should be looking towards the camera with a warm, confident expression. This photo extends from the top to about 75% of the canvas height. The photo has slightly rounded corners and a thin dark border separating it from the background. LEFT SIDE (35-40% width): 4-5 SMALLER POLAROID-STYLE PHOTOS stacked vertically, each slightly tilted at casual angles (2-6 degrees) with thick white instant-photo borders. Each polaroid shows a COMPLETELY DIFFERENT moment or pose but the SAME PERSON with identical face: Polaroid 1 (top): the subject laughing joyfully, candid moment, bright outdoor light. Polaroid 2: a close-up portrait with a playful or thoughtful expression, soft bokeh background. Polaroid 3: the subject in a different outfit, three-quarter body, dynamic pose. Polaroid 4: an artistic shot — the subject from behind or side angle, interesting lighting. Each polaroid casts a subtle drop shadow on the dark background. BOTTOM-LEFT AREA: {{CALENDAR}} BOTTOM-RIGHT AREA: Elegant calligraphy text reading 'Happy Birthday' in warm white or cream script font with subtle glow. Below that, '{{MESSAGE}}' in a large beautiful decorative calligraphy script — warm gold or cream colour. Small ornamental flourishes or sparkles around the text. TEXT RESTRICTION: Show ONLY the exact text specified above ('Happy Birthday' and the message). Do NOT add any other festival name, greeting, or event text anywhere in the image. COLOUR PALETTE: deep black/charcoal background, warm natural tones in photos, white polaroid borders, cream/gold text. Overall mood: stylish, modern, celebratory dark birthday collage.";

        const string birthdayGridCalendarPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW BIRTHDAY GRID CALENDAR COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance in every panel. IMPORTANT: Every photo in the grid must show the SAME person with IDENTICAL facial features — same eyes, nose, mouth, jawline. Do NOT generate different-looking faces across panels. BACKGROUND: Solid BLACK background with tiny scattered golden sparkle/star effects across the entire canvas for a festive look. TOP AREA (25% of canvas): {{CALENDAR}} The calendar should use white text on the dark background with clean modern typography. MIDDLE AREA (50% of canvas): A MOSAIC GRID of 6-8 photographs arranged in a dynamic grid layout with varying sizes — mix of square and rectangular frames. Some photos are larger (feature shots) and some smaller (accent shots). Thin white gaps (2-3px) separate each photo. The grid is asymmetric and visually interesting. Every photo must show the face large enough that all features are clearly visible. Photo variety: Photo 1 (large, top-left): close-up portrait of the subject with a joyful smile, warm lighting. Photo 2 (medium): the subject in a stylish outfit, three-quarter pose, urban or studio background. Photo 3 (small square): candid laughing moment, face clearly visible. Photo 4 (medium): the subject outdoors in golden-hour light, dreamy bokeh. Photo 5 (large, centre): the subject in an elegant outfit, waist-up, confident pose. Photo 6 (small): artistic close-up — profile view or detail shot. Photo 7 (medium): playful pose with props or in a fun setting. Photo 8 (small): a different outfit, lifestyle shot. Each photo has warm vibrant colour grading. CENTRE TEXT AREA (between photo rows): A decorative text block with a warm heartfelt birthday wish — '{{OCCASION}}' in elegant italic script, cream or soft gold colour, 2-3 lines. Small decorative emoji-style elements (hearts, stars, sparkles) scattered around the text. BOTTOM AREA (20% of canvas): Large bold text 'Happy Birthday' in a beautiful display font — warm white or cream with subtle glow effect. Below it: '{{MESSAGE}}' in elegant calligraphy script — soft pink or gold. Small decorative butterflies and floral sprigs on either side of the text — delicate, pastel pink and white. TEXT RESTRICTION: Show ONLY the exact text specified above ('Happy Birthday', the occasion, and the message). Do NOT add any other festival name, greeting, or event text anywhere in the image. COLOUR PALETTE: black background, golden sparkles, warm photo tones, cream/gold text, soft pink accents. Overall mood: festive, glamorous, modern birthday celebration.";

        const string birthdayDoubleExposurePrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW CINEMATIC BIRTHDAY POSTER on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, hair, and appearance. Both the monochrome overlay and the colour portrait must show the SAME person with IDENTICAL facial features. This should look like a dramatic CINEMATIC POSTER for a birthday celebration. LAYOUT — TOP HALF (55-60% of canvas): A dramatic DOUBLE EXPOSURE / OVERLAY effect. A large, softly DESATURATED close-up of the subject's face and upper body — rendered in muted GREY/SILVER monochrome tones, large scale, filling the upper portion. The image is semi-transparent and dreamlike, blending into the dark background. Show multiple angles or a mirrored/reflected version of the face creating a symmetrical artistic composition. The edges fade and dissolve into soft mist, light particles, or gentle bokeh effects. BOTTOM HALF (40-45% of canvas): A VIVID FULL-COLOUR photograph of the subject — in a smart casual or elegant outfit (blazer, dress, sharp shirt). Standing or posed confidently with warm natural lighting. This photo is crisp, vibrant, and saturated with warm golden-brown tones — dramatically contrasting with the grey overlay above. The subject is shown from approximately waist up. TRANSITION between top and bottom: Smooth gradient dissolve — the grey monochrome top fades gradually into the vivid colour bottom. No hard dividing line. Soft light particles and gentle bokeh orbs (warm gold) float across the transition zone. TEXT ELEMENTS — UPPER-CENTRE (overlaying the double exposure): 'HAPPY BIRTHDAY' in clean uppercase serif or sans-serif font — warm white or soft cream, medium size, elegant spacing. BOTTOM AREA (below or overlaying the colour photo): '{{MESSAGE}}' in large flowing calligraphy script — sage green, warm olive, or soft gold colour. Beautiful decorative flourishes extending from the text. TEXT RESTRICTION: Show ONLY the exact text specified above ('HAPPY BIRTHDAY' and the message). Do NOT add any other festival name, greeting, or event text anywhere in the image. COLOUR PALETTE: grey/silver monochrome (top), warm golden-brown (bottom colour photo), sage green or olive text accents. The contrast between desaturated and vivid is the key visual feature. Overall mood: cinematic, artistic, modern birthday celebration poster.";

        var birthdayCalendarStyles = new[]
        {
            ("Dark Birthday Calendar", "Dark cinematic birthday collage with polaroids, hero shot and calendar grid", darkBirthdayCalendarPrompt, "\U0001F382", "#212121", 174),
            ("Birthday Grid Calendar", "Black background photo grid with top calendar and birthday message", birthdayGridCalendarPrompt, "\U0001F388", "#FFD54F", 175),
            ("Birthday Double Exposure", "Cinematic desaturated double exposure with vivid portrait overlay", birthdayDoubleExposurePrompt, "\U0001F3AC", "#78909C", 176),
        };

        foreach (var (name, desc, prompt, emoji, accent, sort) in birthdayCalendarStyles)
        {
            await db.Database.ExecuteSqlRawAsync(@"
                IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                    INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                    VALUES (@p0, @p1, @p2, N'PhotoCollage', @p3, @p4, 1, @p5)",
                name, desc, prompt, emoji, accent, sort);
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
                prompt, name);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Birthday calendar/double-exposure styles seeding failed (non-fatal)");
    }

    // ── Seed Campus Collage Art PhotoCollage style ──
    try
    {
        const string campusCollagePrompt = "DO NOT simply reproduce the source photo. Generate a BRAND NEW MIXED-MEDIA PAPER COLLAGE ART piece on a vertical 2:3 portrait canvas. Use the subject(s) from the source photo — preserve their EXACT face(s), facial features, hair, skin tone, and appearance. IMPORTANT: Include ONLY the exact number of people from the source image. ART STYLE: The entire image must look like a handmade MIXED-MEDIA COLLAGE — made of torn paper, ripped newspaper clippings, magazine cutouts, watercolour washes, and textured paper fragments all layered together. Every element should have visible torn or cut paper edges with paper fibre texture. Newspaper text columns and book page fragments should be visible beneath and around elements. CENTRE FOCAL POINT (occupying 50-60% of the canvas): The subject(s) rendered as the dominant central figure(s) in this paper collage style. They should be standing confidently, full body or three-quarter, wearing casual college attire — denim jacket, jeans, t-shirt, hoodie, backpack, headphones around neck, carrying books or a coffee cup. Their clothing and body are composed of layered torn paper textures and fabric pattern cutouts — but their FACE must be rendered with high detail preserving exact identity. The figure(s) should feel like they are literally constructed from paper and magazine clippings. BACKGROUND — CAMPUS SCENE: A college campus environment created entirely from torn paper collage elements: TOP AREA: Blue sky made of torn blue and white paper layers. Green paper-cut trees and leaves scattered across the top. University buildings (brick, classical architecture with columns, clock towers) constructed from torn paper — brown, beige, and grey paper textures with visible newspaper text underneath. MIDDLE AREA: A grassy campus quad or walkway behind the central figure(s). Other students walking in the background, rendered as smaller paper collage figures carrying backpacks and books — all in the torn paper art style. BOTTOM AREA: Additional campus life elements as collage fragments — a lecture hall scene with rows of students, scientific diagrams (DNA helix, cell biology, molecular structures), textbook pages, notebook doodles, campus map fragments. These bottom elements should be smaller collage pieces creating a rich layered border. PAPER TEXTURES throughout: Visible torn edges on every element, newspaper text columns peeking through, watercolour paint splashes in green, blue, and warm tones, rough paper grain texture on the off-white/cream background. COLOUR PALETTE: cream/off-white paper base, denim blue, forest green, warm red accents, brown and beige torn paper, black newsprint text. The overall image must look like a physical handmade paper collage artwork — textured, layered, artistic, with visible craft materials.";

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'PhotoCollage', @p3, @p4, 1, @p5)",
            "Campus Collage Art", "Mixed-media torn paper collage with college campus life theme", campusCollagePrompt, "\U0001F3D3", "#1565C0", 178);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            campusCollagePrompt, "Campus Collage Art");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Campus Collage Art style seeding failed (non-fatal)");
    }

    // ── Seed Number Template PhotoCollage style ──
    try
    {
        const string numberTemplatePrompt = "Generate a number photo collage poster on a 2:3 portrait canvas. Giant 3D number '{{NUMBER}}' fills 70-80% of canvas height, with the ENTIRE number surface filled by a mosaic of tightly packed photos of the EXACT same person from the source. CRITICAL: Every single photo inside the number must show the IDENTICAL person — same face shape, same nose, same eyes, same mustache/beard, same skin colour, same hair. Do NOT generate different people. All photos must be unmistakably the same individual just in different poses and angles. Thin white gaps between photos, clipped inside digit outline. Clean light grey background. Top: '{{MESSAGE}}' in elegant calligraphy. Bottom: '{{OCCASION}}' in decorative script, then '{{DATE}}' smaller below. Soft drop shadow, warm vibrant photos, dark charcoal or gold text.";

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'PhotoCollage', @p3, @p4, 1, @p5)",
            "Number Template", "Giant number filled with photo collage for any occasion", numberTemplatePrompt, "\U0001F522", "#9E9E9E", 177);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0, Description = @p2 WHERE Name = @p1",
            numberTemplatePrompt, "Number Template", "Giant number filled with photo collage for any occasion");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Number Template style seeding failed (non-fatal)");
    }

    // ── Seed Trending styles ──
    try
    {
        const string traditionalMakeupPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW TRADITIONAL MAKEUP PORTRAIT on a vertical 2:3 portrait canvas. Preserve the subject's EXACT face, bone structure, skin tone, and every facial detail from the source photo. SCENE: Intimate candid close-up — the subject is being adorned for a traditional Indian ceremony. A hand (another person, partially out of frame, blurred foreground) carefully applies ceremonial marking to the subject's face. FOR WOMEN: kajal/kohl being lined on the eye, adorned with maang tikka, nath (nose chain), layered gold necklaces, fresh marigold and jasmine flowers in hair, rich silk drape at shoulder. FOR MEN: sandalwood tilak or vibhuti being applied on the forehead, adorned with gold chain, rudraksha mala, traditional turban or pagdi with brooch, silk angavastram/uttariyam at shoulder. FOR CHILDREN: age-appropriate simple tilak or bindi, small gold chain, fresh flower garland. EXPRESSION: Serene, focused, gazing slightly upward. LIGHTING: Warm golden candlelight-like ambient, moody cinematic atmosphere, shallow depth of field — face tack-sharp, foreground blurred with bokeh. PALETTE: warm golds, deep orange marigold, royal blue silk, deep black background.";

        const string classicalDancePrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW CLASSICAL DANCE PORTRAIT on a vertical 2:3 portrait canvas. Preserve the subject's EXACT face, bone structure, skin tone, and every facial detail from the source photo. SCENE: Full-body Bharatanatyam aramandi pose (half-seated, knees bent outward), hands in expressive dance mudras (Alapadma or Katakamukha). FOR WOMEN: rich red and gold Kanchipuram silk costume with pleated fan-shaped front, gold oddiyanam waist belt, vanki armlets, bangles, ghungroo ankle bells, temple jewellery (choker, jhumka earrings, surya/chandra headpiece, forehead tikka), hair in classical bun with jasmine gajra, bold kajal eye makeup, red bindi, alta on fingertips and feet. FOR MEN: rich red and gold silk dhoti with pleated front drape, bare chest with gold-bordered angavastram across one shoulder, gold oddiyanam waist belt, vanki armlets, gold chain necklace, small gold ear studs, ghungroo ankle bells, vibhuti or tilak on forehead, alta on fingertips and feet, hair neatly styled or traditional male dancer headpiece. FOR CHILDREN: age-appropriate simplified version of the above based on gender. BACKGROUND: Deep teal-to-navy studio gradient, soft spotlight centre. LIGHTING: Professional warm golden key light from front-left, silk and gold shimmering. PALETTE: rich red-gold costume, warm skin tones, deep teal-navy background, gleaming gold.";

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'Trending', @p3, @p4, 1, @p5)",
            "Traditional Makeup Portrait", "Intimate close-up of traditional makeup and adornment being applied", traditionalMakeupPrompt, "\U0001F48D", "#FF8F00", 179);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            traditionalMakeupPrompt, "Traditional Makeup Portrait");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'Trending', @p3, @p4, 1, @p5)",
            "Classical Dance Portrait", "Full-body classical Indian dance pose with traditional costume and mudra", classicalDancePrompt, "\U0001F483", "#C62828", 180);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            classicalDancePrompt, "Classical Dance Portrait");

        const string eventFilmmakerPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW FILMMAKER PORTRAIT on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, facial bone structure, skin tone, eye shape, nose shape, lip shape, jaw shape, facial hair (if any), hair texture, hair colour, hair length, and every facial detail with photorealistic accuracy. The person's gender, age, and ethnicity must remain exactly as in the source photo. SCENE: The subject is a professional cinematographer/filmmaker at a large indoor event or conference. They are standing confidently, smiling warmly at the camera while holding a professional cinema camera rig. EQUIPMENT: The subject holds a full cinema camera setup — a professional RED or ARRI-style digital cinema camera body mounted on a handheld gimbal stabiliser (like a DJI Ronin or Freefly MoVI). The camera has a large cinema lens, a compact on-camera monitor mounted on top, and a shotgun microphone attached to the top handle. The subject wears professional over-ear monitoring headphones around their head. They grip the gimbal handle with both hands in a natural working position. CLOTHING: Casual professional — a dark grey or charcoal hoodie/pullover, comfortable and practical filmmaker attire. BACKGROUND: A large indoor event venue or conference hall — rows of chairs visible behind, a stage with colourful LED stage lights creating beautiful blue and purple bokeh in the background. Warm amber and cool blue lighting mix. The background is heavily blurred (shallow depth of field, f/1.4-2.0) creating rich circular bokeh from the stage lights. LIGHTING: Mixed warm and cool event lighting — warm key light on the subject's face from the front, cool blue ambient fill from the stage lights behind. The subject's face is well-lit and the expression is friendly, approachable, and confident. COLOUR PALETTE: warm skin tones, charcoal grey clothing, blue and purple bokeh highlights, warm amber accents. Overall mood: professional, energetic, behind-the-scenes at a major event. IMPORTANT: This must work for any person regardless of age or gender. The face and ALL facial features MUST be an exact match to the source photo — do not alter facial structure, skin tone, or any identifying features.";

        const string outdoorFilmmakerPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW OUTDOOR FILMMAKER PORTRAIT on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, facial bone structure, skin tone, eye shape, nose shape, lip shape, jaw shape, facial hair (if any), hair texture, hair colour, hair length, and every facial detail with photorealistic accuracy. The person's gender, age, and ethnicity must remain exactly as in the source photo. SCENE: The subject is a professional cinematographer/filmmaker shooting on an outdoor film location. They are shown in a focused side-profile or three-quarter view, concentrating intently on their work, operating the camera. EQUIPMENT: The subject is operating a professional cinema camera (RED Komodo, Canon C70, or Sony FX6 style) mounted on a professional tripod with a fluid head. The camera has a large cinema zoom lens, a compact field monitor mounted on top showing the shot being filmed, and professional accessories (follow focus, matte box or lens hood). The subject's hands are on the camera controls, adjusting settings or framing the shot. CLOTHING: Practical outdoor filmmaker attire — a dark olive green, charcoal, or navy windbreaker/field jacket, layered for outdoor shooting. Hair slightly tousled by the wind, natural and candid. BACKGROUND: An outdoor rural or semi-urban location — bare trees, an old house or rustic structure slightly visible, soft overcast sky. The background is beautifully blurred with shallow depth of field (f/2.0-2.8), creating a soft dreamy bokeh with muted earthy tones. Slight haze or atmospheric mist adding depth and cinematic quality. LIGHTING: Soft diffused natural daylight — overcast sky providing even, flattering light on the subject. Subtle rim light on the hair and shoulders from behind. Cool desaturated colour grading with slight teal shadows and warm skin tone highlights — a cinematic colour grade. COLOUR PALETTE: muted earthy tones — olive green, charcoal, soft browns, desaturated greens from foliage, cool grey sky, warm natural skin tones. Overall mood: focused, artistic, behind-the-scenes on a film set, cinematic and atmospheric. IMPORTANT: This must work for any person regardless of age or gender. The face and ALL facial features MUST be an exact match to the source photo — do not alter facial structure, skin tone, or any identifying features.";

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'Trending', @p3, @p4, 1, @p5)",
            "Event Filmmaker", "Professional cinematographer at a live event with cinema camera and headphones", eventFilmmakerPrompt, "\U0001F3AC", "#5C6BC0", 181);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            eventFilmmakerPrompt, "Event Filmmaker");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'Trending', @p3, @p4, 1, @p5)",
            "Outdoor Filmmaker", "Cinematic filmmaker shooting on location with professional camera on tripod", outdoorFilmmakerPrompt, "\U0001F3AC", "#455A64", 182);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            outdoorFilmmakerPrompt, "Outdoor Filmmaker");
        const string raceCarDriverPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW RACE CAR DRIVER PORTRAIT on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, facial bone structure, skin tone, eye shape, nose shape, lip shape, jaw shape, facial hair (if any), hair texture, hair colour, hair length, and every facial detail with photorealistic accuracy. The person's gender, age, and ethnicity must remain exactly as in the source photo. SCENE: A dramatic cinematic close-up of the subject as a professional race car driver, seated inside the cockpit of a high-performance racing car. HELMET: The subject wears a sleek glossy BLACK full-face racing helmet with the tinted visor FLIPPED UP, fully revealing their face. The helmet has subtle metallic gold or silver accent stripes and a reflective visor surface. The helmet fits snugly and looks authentic — professional motorsport grade. The subject's face is clearly visible through the open visor — eyes looking ahead with intense focus and determination, or gazing slightly to one side with quiet confidence. COCKPIT: The interior of a racing car is visible — a roll cage with metal tubular bars behind and to the side, a racing bucket seat with harness straps over the shoulders, carbon fibre dashboard elements. The cockpit feels tight and authentic. CLOTHING: The subject wears a professional black racing suit (fireproof Nomex) with subtle sponsor logos or racing team patches. A HANS device or neck restraint may be partially visible. LIGHTING: Dramatic golden-hour sunlight streaming in from the side/front of the cockpit — warm amber light illuminating one side of the subject's face while the other side falls into soft shadow. Beautiful contrast between warm highlights and cool cockpit shadows. Slight lens flare from the sunlight adds cinematic quality. COLOUR PALETTE: deep blacks from the helmet and suit, warm golden amber from sunlight, metallic silver from the helmet visor, muted dark tones of the cockpit interior. Overall mood: intense, focused, powerful, cinematic motorsport photography. IMPORTANT: This must work for any person regardless of age or gender — the helmet, suit, and cockpit remain the same, only the face changes to match the source photo exactly. The face and ALL facial features MUST be an exact match to the source photo — do not alter facial structure, skin tone, or any identifying features.";

        const string neonSuperbikePrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW FULL NEON CYBERPUNK SUPERBIKE SCENE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, facial bone structure, skin tone, eye shape, nose shape, lip shape, jaw shape, facial hair (if any), hair texture, hair colour, hair length, and every facial detail with photorealistic accuracy. The person's gender, age, and ethnicity must remain exactly as in the source photo. ENTIRE IMAGE STYLE: The WHOLE image must be bathed in vivid neon colours — everything glows with neon light. This is a full cyberpunk neon aesthetic where every element radiates colour. SCENE: The subject is sitting astride a futuristic high-performance superbike in a dramatic posed shot. The motorcycle is parked or stationary, and the subject is seated on it in a confident, powerful stance — one hand on the handlebar, other hand resting on their thigh or removing their helmet. The subject's face is FULLY VISIBLE (helmet off, held under one arm or resting on the tank, or no helmet at all). MOTORCYCLE: A sleek, aggressive futuristic superbike with an aerodynamic black gloss body — sharp angular fairings, sculpted curves, and muscular proportions. The ENTIRE bike glows with vivid NEON PINK/MAGENTA and NEON CYAN/BLUE LED accent lights — glowing strips running along every fairing edge, wheel rims fully illuminated with neon pink light rings, engine area with intense magenta underglow, thin neon lines tracing every contour of the bodywork, brake calipers glowing, exhaust with neon accents. The wheels cast neon reflections on the ground. CLOTHING: The subject wears a sleek black leather motorcycle jacket with NEON PINK piping, glowing neon accents on the zippers and seams, neon-trimmed riding gloves, black riding pants with neon side-stripe accents, and boots with subtle glow. The outfit has a futuristic cyberpunk aesthetic with visible neon elements. BACKGROUND: A NEON-DRENCHED cyberpunk cityscape at night — dark buildings with vivid neon signs in pink, magenta, cyan, and electric blue. Neon light strips on building edges, glowing holographic advertisements, neon-lit storefronts. The entire environment pulses with colour. The floor/street is wet and reflective, creating stunning mirror reflections of all the neon lights — pink and blue streaks reflected across the wet ground. Atmospheric neon-tinted fog/mist fills the scene, catching light and creating volumetric neon rays. LIGHTING: The entire scene is lit by neon sources — vivid neon pink/magenta is the dominant colour, with neon cyan/blue as the secondary accent. Every surface catches and reflects neon light. The subject's face is illuminated by the neon glow — pink light on one side, cyan on the other, creating a striking split-tone effect while keeping facial details clearly visible. Neon light reflects off the subject's skin, hair, and clothing. COLOUR PALETTE: deep black base, vivid neon pink/magenta (dominant), electric cyan/blue (accent), neon purple where pink and blue mix, hot neon reflections on every wet/glossy surface. NO dull or neutral areas — the entire image should feel like it's glowing with neon energy. Overall mood: futuristic, electrifying, cyberpunk, visually stunning, every pixel alive with neon colour. IMPORTANT: This must work for any person regardless of age or gender. The subject MUST be sitting on the bike with their face clearly visible and matching the source photo exactly. The face and ALL facial features MUST be an exact match to the source photo — do not alter facial structure, skin tone, or any identifying features.";

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'Trending', @p3, @p4, 1, @p5)",
            "Race Car Driver", "Cinematic close-up of a race car driver in helmet inside a cockpit", raceCarDriverPrompt, "\U0001F3CE\uFE0F", "#212121", 183);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            raceCarDriverPrompt, "Race Car Driver");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'Trending', @p3, @p4, 1, @p5)",
            "Neon Superbike Rider", "Full neon cyberpunk scene with glowing superbike and futuristic rider", neonSuperbikePrompt, "\U0001F3CD\uFE0F", "#E91E63", 184);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            neonSuperbikePrompt, "Neon Superbike Rider");

        const string epicWarriorKingPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW EPIC WARRIOR KING MOVIE POSTER COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, facial bone structure, skin tone, eye shape, nose shape, lip shape, jaw shape, facial hair (if any), hair texture, hair colour, and every facial detail with photorealistic accuracy. The person's gender, age, and ethnicity must remain exactly as in the source photo. OVERALL STYLE: A dramatic multi-layered cinematic movie poster — like a Baahubali or ancient Indian warrior epic film poster. Multiple images of the same subject layered together in a rich, warm golden composite. LAYOUT — TOP AREA (upper 25-30%): A massive DOUBLE EXPOSURE of the subject's EYES AND UPPER FACE — just the intense, piercing eyes, eyebrows, and bridge of the nose, rendered in a warm desaturated sepia/golden tone, filling the entire width of the canvas. The expression is fierce, determined, and commanding. The eyes fade and dissolve downward into the layers below with a smoky, misty transition. BOLD TEXT across the top area or just below the eyes: 'THE REIGN OF' in elegant wide-spaced uppercase serif font, warm gold metallic colour. Below that in MASSIVE bold letters: '{{MESSAGE}}' — the user's name rendered in grand, ornate gold metallic text with subtle embossing, glow, and decorative flourishes. The text should feel royal and epic, like a movie title. MIDDLE AREA (centre 40-45%): The PRIMARY HERO SHOT — the subject as a powerful ancient Indian warrior king in a dynamic action pose. They wear ornate ancient warrior battle armour — a detailed breastplate with a large circular golden sun/bull emblem medallion on the chest, layered leather and metal shoulder guards, arm bracers with gold filigree, and a magnificent ornate CROWN or royal headpiece (like the iconic Mahishmati crown — an elaborate golden metallic crown with intricate carvings). The subject has long flowing wild hair (or their natural hair enhanced dramatically), and holds a gleaming golden SWORD raised high in a battle cry pose — arm extended upward, mouth open in a fierce war cry, muscles tensed. Behind this hero shot: a burning ancient city or fortress — massive stone architecture engulfed in orange flames and thick smoke, creating a dramatic fiery battlefield backdrop. Sparks and embers float in the air. LOWER-LEFT AREA: A second image of the subject — a CLOSE-UP three-quarter PORTRAIT as the warrior king, calm and regal. Wearing the same ornate armour and crown, looking slightly to the side with a confident, noble expression. Warm golden lighting highlights their face. This image overlaps slightly with the hero shot above. LOWER-RIGHT AND BOTTOM AREA: Additional layered elements — soldiers or warriors in silhouette charging into battle, ancient war elephants, flaming arrows arcing through the sky, a grand ancient Indian palace/fortress (Mahishmati-style) with towering walls and ornate architecture visible through the smoke. These background elements are rendered in warm golden-sepia tones, partially transparent, creating depth and scale. BOTTOM EDGE: Subtle decorative border with ancient Indian motifs — lotus patterns, sun symbols, or Sanskrit-inspired ornamental designs in gold. COLOUR PALETTE: Rich warm golden throughout — amber, burnt orange, deep gold, warm sepia brown, fiery orange-red from flames, dark charcoal shadows. The entire poster has a unified warm golden colour grading like a bronze/gold-toned cinematic poster. No cool colours. LIGHTING: Dramatic epic cinematic lighting — strong warm backlight creating rim-light on the hero, fire providing orange illumination, atmospheric haze and smoke catching golden light. IMPORTANT: This must work for any person regardless of age or gender — adapt the warrior attire appropriately (warrior queen armour for women, age-appropriate styling for younger subjects, but always epic and powerful). The face and ALL facial features MUST be an exact match to the source photo.";

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'Trending', @p3, @p4, 1, @p5)",
            "Epic Warrior King", "Baahubali-style epic warrior movie poster collage with name overlay", epicWarriorKingPrompt, "\U0001F451", "#FF8F00", 185);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            epicWarriorKingPrompt, "Epic Warrior King");

        const string wildlifePhotographerPrompt = "DO NOT reproduce the source photo. Generate a BRAND NEW WILDLIFE PHOTOGRAPHER COLLAGE on a vertical 2:3 portrait canvas. Use the person from the source photo — preserve their EXACT face, facial bone structure, skin tone, eye shape, nose shape, lip shape, jaw shape, facial hair (if any), hair texture, hair colour, hair length, and every facial detail with photorealistic accuracy. The person's gender, age, and ethnicity must remain exactly as in the source photo. LAYOUT: The canvas is divided into 4 horizontal panels stacked vertically, separated by thin white gaps (3px). Each panel is a different cinematic wildlife scene. NAME OVERLAY: The text '{{MESSAGE}}' must be displayed in LARGE stylish modern sans-serif or adventure-style lettering — bold, wide-spaced uppercase with a subtle emboss or 3D shadow effect. The text should be warm white or cream with a soft drop shadow, overlaid across the centre of the canvas spanning over the middle two panels. The letters should feel adventurous and premium — like a National Geographic photographer's personal brand logo. A thin elegant tagline below the name reading 'WILDLIFE PHOTOGRAPHER' in smaller wide-spaced uppercase letters. PANEL 1 (top, ~25% height): A breathtaking wide-angle wildlife scene — two or three majestic African elephants walking through tall golden savanna grass at golden hour. Warm amber sunlight, dust particles floating in the air, dramatic sky with warm clouds. Shot from behind/side showing the elephants' massive silhouettes. Lush green and golden tones. Cinematic and epic. PANEL 2 (upper-middle, ~30% height — the largest panel): The subject as a professional wildlife photographer in ACTION. They are crouching low in tall green grass or bush, eye pressed against the viewfinder of a massive professional camera with a huge WHITE telephoto lens (like a Canon 600mm f/4 or similar super-telephoto). They wear practical safari/outdoor clothing — an olive green or khaki field vest with multiple pockets, dark cargo pants, and a wide-brimmed safari hat or cap. Their posture is focused and stealthy — elbows braced, perfectly still, capturing the shot. The background shows soft-focus African savanna — green trees, open grassland. Natural warm lighting. This is the hero panel and should be the most detailed. PANEL 3 (lower-middle, ~20% height): A stunning close-up wildlife portrait — a majestic giraffe's head and long neck in profile, with beautiful spotted pattern, gentle eyes, and soft bokeh of green acacia trees behind. Warm natural savanna lighting. The giraffe appears calm and noble — as if the subject just photographed this magnificent creature. Rich warm brown, golden, and green tones. PANEL 4 (bottom, ~25% height): The subject standing in an open African landscape — full body or three-quarter shot, wearing the same safari outfit, holding the camera with big telephoto lens in one hand at their side, other hand raised pointing into the distance at something exciting (a herd, a bird, a distant animal). They look adventurous and passionate. Behind them: a wide open African savanna vista with scattered acacia trees, distant mountains or hills, warm golden-hour sky. The mood is adventurous and free. COLOUR PALETTE: Rich warm natural tones throughout — golden savanna yellow, deep olive green, warm amber sunlight, earthy khaki brown, soft sky blue. Each panel has warm natural-light photography aesthetic. Unified warm colour grading across all panels. Overall mood: adventurous, professional, National Geographic quality, celebrating wildlife and nature photography. IMPORTANT: This must work for any person regardless of age or gender. The face and ALL facial features MUST be an exact match to the source photo.";

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                VALUES (@p0, @p1, @p2, N'Trending', @p3, @p4, 1, @p5)",
            "Wildlife Photographer", "Multi-panel wildlife photography collage with name in stylish lettering", wildlifePhotographerPrompt, "\U0001F4F7", "#558B2F", 186);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE StylePresets SET PromptTemplate = @p0 WHERE Name = @p1",
            wildlifePhotographerPrompt, "Wildlife Photographer");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Trending styles seeding failed (non-fatal)");
    }

    // ── Seed International Super Heroes style presets (14 styles) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            -- ============================================================
            -- SUPER HEROES: International Iconic Heroes (SortOrder 60-73)
            -- ============================================================

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Superman Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Superman Style', N'Kryptonian hero with cape and shield emblem',
                 N'Transform the subject into a powerful Kryptonian superhero. CRITICAL: Preserve the subject''s exact facial features and identity. Render wearing an iconic blue bodysuit with a bold red and yellow shield emblem on the chest. Flowing crimson red cape billowing heroically in the wind. Red boots and belt. Strong muscular physique in a confident heroic pose — fists on hips or one arm raised in flight. Chiseled jawline with a confident determined expression. Perfectly styled dark hair with a single iconic curl on the forehead. Background of a metropolitan cityscape — soaring skyscrapers, blue sky with sunlight breaking through clouds. Flying above the city or landing with dramatic impact. Warm primary color palette of blue, red, and yellow. Dynamic comic book art quality with cinematic lighting.',
                 N'Super Heroes', N'🦸', '#1565C0', 1, 60);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Batman Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Batman Style', N'Dark Knight gothic vigilante hero',
                 N'Transform the subject into a dark vigilante knight detective. CRITICAL: Preserve the subject''s exact facial features and identity. Render wearing a dark grey and black armored tactical suit with a bat-shaped emblem on the chest. Pointed-ear cowl revealing only the lower face with a strong jawline. Long flowing dark cape with scalloped bat-wing edges. Utility belt loaded with gadgets. Muscular athletic build in a brooding intense stance — perched on a gargoyle or standing on a rooftop. Background of a dark gothic city at night — rain-slicked streets, Art Deco architecture, searchlight beam cutting through fog, full moon. Dark noir color palette of deep blacks, greys, and midnight blue with occasional amber light. Dramatic cinematic comic book art quality.',
                 N'Super Heroes', N'🦇', '#212121', 1, 61);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Wonder Woman Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Wonder Woman Style', N'Amazonian warrior princess of Themyscira',
                 N'Transform the subject into an Amazonian warrior princess. CRITICAL: Preserve the subject''s exact facial features and identity. Render wearing golden and crimson Amazonian battle armor — a tiara crown with a red star, gleaming metallic breastplate in gold and red, armored skirt with blue starfield accents. Indestructible silver bracelets on both wrists. Wielding a glowing golden lasso. A sword and shield nearby. Flowing dark hair with a regal yet fierce expression. Powerful athletic stance ready for battle. Background of paradise island Themyscira — ancient Greek marble temples, turquoise Mediterranean waters, and lush green cliffs. Warm golden sunlight. Majestic, powerful, and graceful. Cinematic action comic book art quality.',
                 N'Super Heroes', N'👸', '#C62828', 1, 62);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Spider-Man Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Spider-Man Style', N'Agile web-slinging acrobatic hero',
                 N'Transform the subject into an agile web-slinging superhero. CRITICAL: Preserve the subject''s exact facial features and identity — render in a half-mask style where the lower face is visible. Wearing a sleek red and blue bodysuit with intricate black web patterns across the red sections. Large white angular eye lenses on the mask with expressive shape. A black spider emblem on the chest. Dynamic acrobatic pose — swinging between skyscrapers on a web line, mid-flip, or crouching on a wall. Lean athletic build. Background of a vibrant New York City — towering glass skyscrapers, busy streets far below, sunset golden hour light. Web lines glistening in sunlight. Energetic dynamic perspective with motion blur. Vibrant red, blue, and gold comic book art quality.',
                 N'Super Heroes', N'🕷️', '#D32F2F', 1, 63);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Iron Man Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Iron Man Style', N'High-tech armored genius superhero',
                 N'Transform the subject into a high-tech armored genius superhero. CRITICAL: Preserve the subject''s exact facial features and identity — render with the helmet visor open revealing the face, or in a HUD holographic overlay. Wearing a sleek red and gold powered armor suit with glowing blue Arc Reactor on the chest. Repulsor beams glowing in the palms. Intricate mechanical panel lines and gold trim across the armor. Confident stance — one arm raised with repulsor charging, or landing in a three-point hero pose. Background of a futuristic tech laboratory with holographic displays, or flying through clouds at supersonic speed with jet trail. Metallic reflections and volumetric lighting. High-tech red, gold, and blue color palette with neon glow effects. Cinematic sci-fi quality.',
                 N'Super Heroes', N'🤖', '#F44336', 1, 64);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Captain America Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Captain America Style', N'Patriotic super-soldier with vibranium shield',
                 N'Transform the subject into a patriotic super-soldier hero. CRITICAL: Preserve the subject''s exact facial features and identity. Render wearing a blue tactical stealth suit with white star emblem on the chest. Red and white stripe accent details. A winged helmet or headpiece. Carrying the iconic circular vibranium shield — concentric rings of red, white, and blue with a central star. Peak-human muscular physique with a noble commanding stance. Determined righteous expression of leadership. Background of a battlefield — dramatic smoke, allies rallying behind. American flag colors reflected in the scene. Strong patriotic lighting — golden hour with dramatic cloud formations. Bold heroic primary color palette. Classic military propaganda poster meets cinematic comic book art quality.',
                 N'Super Heroes', N'🛡️', '#1976D2', 1, 65);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Thor Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Thor Style', N'Norse god of thunder with Mjolnir hammer',
                 N'Transform the subject into a mighty Norse god of thunder. CRITICAL: Preserve the subject''s exact facial features and identity. Render with long flowing blonde hair and a neatly trimmed beard. Wearing ornate Asgardian battle armor — dark leather and silver metallic plates with Norse rune engravings. A flowing crimson red cape fastened with circular Norse disc brooches. Wielding an enchanted hammer crackling with blue-white lightning. Powerful godlike muscular physique in a battle-ready stance. Lightning bolts arcing from the weapon and through the sky. Background of the Rainbow Bridge Bifrost with golden spires of Asgard, cosmic nebulae, and dramatic thunderstorm clouds. Electric blue, silver, and red color palette. Epic mythological cinematic quality.',
                 N'Super Heroes', N'⚡', '#4527A0', 1, 66);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Black Panther Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Black Panther Style', N'Wakandan vibranium warrior king',
                 N'Transform the subject into a vibranium-powered Wakandan warrior king. CRITICAL: Preserve the subject''s exact facial features and identity — render with the ceremonial necklace that forms the suit, face visible. Wearing a sleek black vibranium nanotech suit with subtle purple kinetic energy patterns pulsing across the surface. Silver vibranium claws extended from the fingertips. A ceremonial tooth necklace. Regal powerful stance combining royal dignity with warrior readiness. Background of the technologically advanced hidden nation of Wakanda — futuristic afro-futurist cityscape with vibranium-powered trains, lush African jungle, a great panther statue carved into a cliff face. Purple, black, and silver color palette with neon vibranium glow. Afro-futurist cinematic art quality.',
                 N'Super Heroes', N'🐆', '#6A1B9A', 1, 67);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Wolverine Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Wolverine Style', N'Adamantium-clawed feral mutant warrior',
                 N'Transform the subject into a feral adamantium-clawed mutant warrior. CRITICAL: Preserve the subject''s exact facial features and identity. Render with wild, swept-back dark hair in distinctive pointed sideburns style. Wearing the classic yellow and blue spandex suit with a black tiger-stripe pattern, or rugged leather jacket with dog tags. Three razor-sharp adamantium claws extending from each fist, gleaming metallic. Muscular compact powerful build with a fierce snarling expression showing intensity and rage. Aggressive battle-ready stance. Background of a dark forest or Canadian wilderness under a full moon, dramatic slashing claw marks in the environment. Dark intense color palette with metallic silver highlights. Gritty raw comic book art quality.',
                 N'Super Heroes', N'🐺', '#F9A825', 1, 68);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'The Flash Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'The Flash Style', N'Lightning-fast Speed Force speedster hero',
                 N'Transform the subject into a lightning-fast speedster hero. CRITICAL: Preserve the subject''s exact facial features and identity — render with the cowl pulled back or lower face exposed. Wearing a sleek scarlet red bodysuit with golden lightning bolt emblem on the chest. Gold boots and gold winged ear pieces. Speed Force lightning crackling in yellow and orange trails around the entire body. Dynamic speed-running pose with afterimage motion trails. Background of a city street turned into a blur of streaking lights — everything frozen in time while the speedster moves. Speed Force energy vortex with lightning. Red, gold, and electric yellow color palette. High-speed dynamic comic book art quality.',
                 N'Super Heroes', N'⚡', '#E65100', 1, 69);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Aquaman Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Aquaman Style', N'Powerful Atlantean ocean king with trident',
                 N'Transform the subject into a powerful Atlantean ocean king. CRITICAL: Preserve the subject''s exact facial features and identity. Render with long flowing hair and a beard, muscular warrior build. Wearing golden scale armor on the upper body — thousands of tiny metallic fish-scale plates gleaming. Green leggings and gauntlets with Atlantean tribal markings. Wielding a legendary golden trident crackling with ocean energy. A majestic regal yet fierce expression. Background of the underwater kingdom of Atlantis — bioluminescent coral architecture, schools of exotic fish, ancient stone ruins, shafts of sunlight filtering through deep ocean water. Underwater caustic light effects. Ocean teal, gold, and deep blue color palette. Epic underwater cinematic art quality.',
                 N'Super Heroes', N'🔱', '#00838F', 1, 70);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Doctor Strange Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Doctor Strange Style', N'Master of mystic arts sorcerer supreme',
                 N'Transform the subject into a master of the mystic arts sorcerer. CRITICAL: Preserve the subject''s exact facial features and identity. Render with distinguished goatee and mustache. Wearing an ornate blue tunic with golden sash, and the iconic crimson Cloak of Levitation with a high collar flowing dramatically. A glowing green amulet on the chest. Conjuring intricate golden mandala shields and spell circles — geometric sacred geometry patterns made of glowing orange-gold energy from the hands. Background of a mystical Victorian library filled with ancient artifacts, floating books, and interdimensional portals. Mandelbrot fractal dimensions folding. Purple, gold, orange, and cosmic blue color palette. Mystical psychedelic cinematic art quality.',
                 N'Super Heroes', N'🔮', '#7B1FA2', 1, 71);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Green Lantern Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Green Lantern Style', N'Emerald willpower cosmic ring-bearer',
                 N'Transform the subject into an emerald willpower-powered cosmic hero. CRITICAL: Preserve the subject''s exact facial features and identity. Wearing a sleek green and black bodysuit with the Green Lantern Corps emblem — a glowing green lantern symbol on the chest. A domino mask in green. The Power Ring on one hand glowing with intense emerald green energy. Conjuring a massive green energy construct — a giant fist, shield, or weapon — formed from pure willpower. Background of deep outer space — cosmic nebulae, distant galaxies, a green-glowing planet in the distance. Other Lanterns visible as green streaks of light. Green and black color palette with intense emerald glow effects. Cosmic sci-fi comic book art quality.',
                 N'Super Heroes', N'💚', '#2E7D32', 1, 72);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Deadpool Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Deadpool Style', N'Fourth-wall-breaking anti-hero mercenary',
                 N'Transform the subject into a wisecracking anti-hero mercenary. CRITICAL: Preserve the subject''s exact facial features and identity — render with the mask pulled up above the nose, or with a split-view showing half-mask half-face. Wearing a full red and black tactical suit with katana swords strapped in an X-pattern on the back. Utility belt loaded with pouches, guns, and grenades. White eye patches on the red mask with exaggerated expressive shapes. Breaking the fourth wall — pointing directly at the viewer with a finger gun, or holding a speech bubble. Dynamic irreverent pose. Background of explosive chaos — buildings crumbling, comedic graffiti, pop-culture references scattered around. Red, black, and white color palette with comic splatter effects. Irreverent comedic comic book art quality.',
                 N'Super Heroes', N'💀', '#B71C1C', 1, 73);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Super Heroes StylePresets seeding failed (non-fatal)");
    }

    // ── Seed Cartoon Characters style presets (14 styles) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            -- ============================================================
            -- CARTOON CHARACTERS: Classic & Modern Icons (SortOrder 80-93)
            -- ============================================================

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Mickey Mouse Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Mickey Mouse Style', N'Classic Disney round-ear animation',
                 N'Transform the subject into classic Disney animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the iconic 1930s-1940s Walt Disney animation style — perfectly round head with large circular black ears, big expressive oval eyes with white highlights, a wide cheerful smile with a button nose. Clean smooth animation lines. Wearing classic red shorts with two white buttons and large yellow shoes. White gloves on hands. Joyful exuberant expression with one arm waving. Background of a colorful Disney theme park or a magical castle with fireworks. Bright primary colors — red, yellow, black, and white. Classic hand-drawn cel animation quality with clean ink outlines and flat vibrant colors.',
                 N'Cartoon Characters', N'🐭', '#E53935', 1, 80);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Bugs Bunny Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Bugs Bunny Style', N'Classic Looney Tunes wise-cracking cartoon',
                 N'Transform the subject into classic Looney Tunes animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the iconic Warner Bros Looney Tunes style — large expressive eyes with a sly mischievous smirk. Exaggerated cartoon proportions with smooth fluid animation lines. Long grey rabbit ears, white cheeks and belly, big flat feet. Casually leaning against something while munching a carrot with a too-cool attitude. One eyebrow raised with a knowing expression. Background of a colorful cartoon landscape — desert mesas, rabbit holes, or a theatrical red curtain stage. Classic Tex Avery / Chuck Jones animation style. Vibrant saturated colors with bold black outlines. Vintage hand-drawn cel animation quality.',
                 N'Cartoon Characters', N'🐰', '#FF8F00', 1, 81);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'SpongeBob Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'SpongeBob Style', N'Nickelodeon Bikini Bottom undersea cartoon',
                 N'Transform the subject into Nickelodeon SpongeBob SquarePants animation style. CRITICAL: Preserve the subject''s facial likeness and identity mapped onto a cartoon character form. Render in the distinctive flat 2D SpongeBob style — bright optimistic blocky character design with enormous blue eyes, a gap-toothed enthusiastic grin, and rosy cheeks. Wearing the iconic white shirt, brown pants, and red tie. Square-shaped body proportions. Tiny limbs with exaggerated poses of excitement. Background of the colorful underwater Bikini Bottom — pineapple house, jellyfish fields, the Krusty Krab, coral and tropical fish. Bright cheerful color palette — yellow, blue, pink, and green. Flat cartoon illustration quality with bold outlines.',
                 N'Cartoon Characters', N'🧽', '#FDD835', 1, 82);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Tom & Jerry Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Tom & Jerry Style', N'Classic Hanna-Barbera chase cartoon',
                 N'Transform the subject into classic Hanna-Barbera Tom & Jerry animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the golden-age MGM cartoon style of the 1940s-1950s — fluid expressive animation with soft rounded character designs. Exaggerated slapstick expressions — wide eyes of surprise, stretched limbs, speed lines. Dynamic chase-scene action pose with one character mid-run and another mid-pounce. Rich watercolor-painted backgrounds — a warm homey 1950s kitchen or living room with detailed furniture. Classic cartoon sound-effect elements — stars from impact, sweat drops, motion lines. Warm nostalgic color palette. Vintage hand-painted cel animation quality with painterly backgrounds.',
                 N'Cartoon Characters', N'🐱', '#5D4037', 1, 83);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Scooby-Doo Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Scooby-Doo Style', N'Mystery-solving groovy retro cartoon',
                 N'Transform the subject into classic Hanna-Barbera Scooby-Doo animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the distinctive 1969 Scooby-Doo character design — bold simplified shapes with heavy dark outlines. Retro 1970s color palette with warm browns, oranges, and avocado greens. Slightly scared but brave expression. Holding a flashlight in a dark spooky hallway. Background of a haunted mansion — cobwebs, creaky doors, ghostly shadows, old portraits with moving eyes, foggy graveyard outside. Classic mystery-solving detective aesthetic. The Mystery Machine van visible through a window. Vintage Saturday morning cartoon quality with flat cel-shaded colors and groovy retro vibes.',
                 N'Cartoon Characters', N'🐕', '#6D4C41', 1, 84);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Simpsons Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Simpsons Style', N'Yellow Springfield TV cartoon style',
                 N'Transform the subject into The Simpsons animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the iconic Matt Groening Springfield style — bright yellow skin, large round bulging eyes with circular pupils, prominent overbite with visible teeth. Simplified cartoon body proportions with four-fingered hands. Bold clean black outlines with flat bright colors. Distinctive spiky or rounded hair silhouette. Casual everyday clothing. Background of iconic Springfield — the Simpsons house, Kwik-E-Mart, Springfield Nuclear Power Plant, or Moe''s Tavern. Signature Simpsons pastel color palette — yellow, blue, orange, and pink. Clean flat vector-like cel animation quality. Perfect Matt Groening character design.',
                 N'Cartoon Characters', N'💛', '#FBC02D', 1, 85);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Pokemon Trainer Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Pokemon Trainer Style', N'Pokemon anime creature-trainer adventure',
                 N'Transform the subject into a Pokemon anime character style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the colorful Pokemon anime style — large sparkling expressive eyes, spiky dynamic hair with vibrant highlights. Wearing a Pokemon Trainer outfit — a cap with a Pokeball logo, a jacket with bold color blocking, fingerless gloves, and a belt with six Pokeball holders. Confident adventure-ready pose with one arm extended holding a glowing Pokeball. A Pikachu companion with rosy cheeks and lightning tail on the shoulder. Background of a vibrant Pokemon world — lush Route with tall grass, wild Pokemon silhouettes, a Pokemon Center in the distance. Bright cheerful anime color palette. Professional Pokemon anime art quality.',
                 N'Cartoon Characters', N'⚡', '#F44336', 1, 86);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Doraemon Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Doraemon Style', N'Japanese robotic cat gadget cartoon',
                 N'Transform the subject into Doraemon Japanese animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the distinctive Fujiko F. Fujio manga/anime style — rounded soft character design with large expressive oval eyes. Clean simple linework with gentle pastel coloring. Friendly warm expression with rosy cheeks. Wearing school uniform or casual Japanese clothing. Standing next to the iconic blue robotic cat companion with bell collar and 4D pocket. Reaching into the magical pocket pulling out a fantastic gadget. Background of a typical Japanese suburban neighborhood — house, a grassy vacant lot where friends play, cherry blossom trees. Soft pastel blue, pink, and yellow palette. Warm wholesome Japanese children''s anime quality.',
                 N'Cartoon Characters', N'🔔', '#1E88E5', 1, 87);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Shin-chan Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Shin-chan Style', N'Mischievous Japanese kid cartoon comedy',
                 N'Transform the subject into Crayon Shin-chan animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the distinctive Yoshito Usui manga style — thick bold outlines, extremely simplified character design. Tiny dot eyes set wide apart, a mischievous smirking mouth, and prominent thick eyebrows. Compact stumpy body proportions with a big head. Cheeky naughty expression — sticking tongue out or doing a silly dance. Wearing a simple T-shirt and shorts. Background of a typical messy Japanese suburban home or kindergarten classroom. Bright bold flat colors — red, yellow, blue. Simple clean Japanese cartoon style with charming childlike simplicity. Comedy manga illustration quality.',
                 N'Cartoon Characters', N'😜', '#FF7043', 1, 88);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Ben 10 Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Ben 10 Style', N'Alien hero Omnitrix transformation',
                 N'Transform the subject into Cartoon Network Ben 10 animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in bold angular character shapes with sharp dynamic lines. Wearing a white T-shirt with a black stripe, cargo pants, and sneakers. The iconic Omnitrix watch on the wrist — a green and black alien device glowing with emerald energy. Mid-transformation pose — one arm morphing with green flash energy, alien DNA patterns swirling. Confident adventurous expression of a young hero. Background of an alien-tech landscape — crashed spacecraft, desert canyon, or futuristic base with holographic alien displays. Green, black, and white color palette with alien glow effects. Dynamic action cartoon quality.',
                 N'Cartoon Characters', N'👽', '#43A047', 1, 89);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Powerpuff Girls Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Powerpuff Girls Style', N'Sugar, spice, and everything nice superhero',
                 N'Transform the subject into Cartoon Network Powerpuff Girls animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in Craig McCracken''s iconic style — extremely simplified and adorable character design with an enormous round head, giant sparkly eyes taking up most of the face, tiny body with stubby limbs. Bright colorful outfit. Flying through the air leaving a colored energy streak trail. Cute yet powerful determined expression. Background of the colorful retro-futuristic city of Townsville — candy-colored buildings, blue sky, the hotline telephone, or a laboratory with Chemical X. Bright pastel candy color palette — pink, blue, green, and purple. Bold graphic flat cartoon style with thick outlines. Adorable yet action-packed quality.',
                 N'Cartoon Characters', N'💖', '#EC407A', 1, 90);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Dexters Lab Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Dexters Lab Style', N'Boy genius secret laboratory cartoon',
                 N'Transform the subject into Cartoon Network Dexter''s Laboratory animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in Genndy Tartakovsky''s distinctive angular style — exaggerated proportions with a small body and large head. Wearing a white lab coat, purple latex gloves, and black boots. Round glasses perched on the nose. A genius inventor expression — intense focused concentration. Standing in an enormous secret underground laboratory filled with impossible machines — bubbling beakers, Tesla coils sparking, giant computer screens, robotic arms, and flashing control panels. Bold geometric angular character design. Bright saturated color palette — purple, blue, green, and chrome silver. Retro-futuristic Cartoon Network animation quality.',
                 N'Cartoon Characters', N'🔬', '#7B1FA2', 1, 91);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Johnny Bravo Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Johnny Bravo Style', N'Muscular retro cool-dude cartoon',
                 N'Transform the subject into Cartoon Network Johnny Bravo animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in the iconic Van Partible style — massively exaggerated proportions with an enormous muscular upper body (huge chest and biceps) tapering to impossibly tiny legs. Towering blonde pompadour hairstyle. Wearing a fitted black T-shirt and blue jeans with a large belt buckle. Black sunglasses always on. Flexing muscles in a confident overconfident pose — pointing at the viewer, flexing biceps, or striking a karate stance. Background of a sunny suburban neighborhood, a gym, or a beach with palm trees. Bold retro pop-art inspired color palette — black, blue, and sun-yellow. 1990s Cartoon Network animation quality with thick bold outlines.',
                 N'Cartoon Characters', N'💪', '#FFD600', 1, 92);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Adventure Time Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Adventure Time Style', N'Whimsical candy kingdom adventure cartoon',
                 N'Transform the subject into Cartoon Network Adventure Time animation style. CRITICAL: Preserve the subject''s facial likeness and identity. Render in Pendleton Ward''s distinctive whimsical style — soft rounded noodle-arm character design with simple dot eyes and a wide expressive mouth. Clean minimal linework with flat pastel colors. Wearing a fun adventurer outfit — a bear-eared hat or backpack with a sword. A cute dog companion stretching and shape-shifting nearby. Background of the magical Land of Ooo — Candy Kingdom with pastel buildings, the Ice King mountain, Tree Fort house, rolling green hills with strange creatures. Psychedelic sunset sky with cotton-candy clouds. Pastel rainbow color palette — light blue, pink, yellow, and mint green. Whimsical hand-drawn indie animation quality.',
                 N'Cartoon Characters', N'🗡️', '#4FC3F7', 1, 93);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Cartoon Characters StylePresets seeding failed (non-fatal)");
    }

    // ── Seed Anime & Gaming style presets (14 styles) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            -- ============================================================
            -- ANIME & GAMING: Trending Anime/Game Styles (SortOrder 100-113)
            -- ============================================================

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Cyberpunk Edgerunner')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Cyberpunk Edgerunner', N'Neon cyberpunk anime mercenary style',
                 N'Transform the subject into a cyberpunk anime edgerunner mercenary. CRITICAL: Preserve the subject''s exact facial features and identity. Render in vivid anime style — sharp dynamic lines, intense expressions, and exaggerated action poses. Cybernetic augmentations visible — glowing circuit lines under the skin, a chrome cyberarm, optical implants with HUD display in the eyes. Wearing a punk-tech jacket with neon LED trim, tactical vest, and combat boots. Dual-wielding futuristic pistols. Background of a rain-soaked Night City at night — towering megabuildings with holographic advertisements, neon signs in pink and cyan, flying cars. Neon pink, cyan, chrome, and deep purple color palette. Cyberpunk anime art quality.',
                 N'Anime & Gaming', N'🌆', '#E040FB', 1, 100);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Pirate King Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Pirate King Style', N'Grand Line pirate captain adventure anime',
                 N'Transform the subject into a Grand Line pirate captain in One Piece anime style. CRITICAL: Preserve the subject''s exact facial features and identity. Render in Eiichiro Oda''s distinctive manga style — bold expressive features, dynamic exaggerated proportions, and infectious grin of adventure. Wearing a straw hat with a red band, an open red vest revealing a muscular chest, blue shorts, and sandals. A pirate flag with skull and crossbones flying in the background. Confident captain pose on the bow of a pirate ship — one foot on the railing, fist raised to the sky. Background of the vast Grand Line ocean — massive waves, a pirate ship, distant mysterious islands. Vibrant saturated color palette — warm reds, blues, and ocean teal. One Piece manga/anime art quality.',
                 N'Anime & Gaming', N'🏴‍☠️', '#D32F2F', 1, 101);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Saiyan Warrior Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Saiyan Warrior Style', N'Super powered energy warrior anime',
                 N'Transform the subject into a powerful Saiyan warrior in Dragon Ball Z anime style. CRITICAL: Preserve the subject''s exact facial features and identity. Render in Akira Toriyama''s iconic style — bold clean lines, sharp angular features, and intense determined expression. Hair transformed into Super Saiyan form — spiky golden blonde hair standing straight up, radiating golden ki energy aura. Eyes turned fierce teal green. Wearing Saiyan battle armor — blue bodysuit with white and gold chest plate, white gloves and boots. Powering up in a battle stance — fists clenched, ki energy crackling, ground cracking beneath. Background of a barren rocky battlefield — craters, shattered mountains, dramatic sky with energy beams. Intense gold, blue, and white color palette. Dragon Ball Z anime art quality.',
                 N'Anime & Gaming', N'🔥', '#FFD600', 1, 102);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Demon Slayer Hashira')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Demon Slayer Hashira', N'Breathing technique samurai swordsman',
                 N'Transform the subject into a Hashira-rank demon slayer swordsman. CRITICAL: Preserve the subject''s exact facial features and identity. Render in Ufotable''s stunning anime style — crisp detailed linework with vivid coloring and dramatic shading. Wearing the Demon Slayer Corps uniform — black gakuran jacket with a white belt, and a distinctive haori with unique geometric or floral design. Gripping a blade that changes color with breathing technique — the blade edge glowing with elemental energy. Performing a Breathing Form technique — dynamic slash pose with swirling elemental effects (water dragons, flame trails, or lightning arcs). Background of a moonlit bamboo forest with wisteria flowers. Rich saturated color palette with glowing technique effects. Ufotable-quality cinematic anime art.',
                 N'Anime & Gaming', N'⚔️', '#7B1FA2', 1, 103);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Jujutsu Sorcerer Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Jujutsu Sorcerer Style', N'Cursed energy dark fantasy sorcerer',
                 N'Transform the subject into a powerful jujutsu sorcerer. CRITICAL: Preserve the subject''s exact facial features and identity. Render in MAPPA''s Jujutsu Kaisen anime style — edgy dynamic character design with sharp features and intense eyes. Wearing the Tokyo Jujutsu High uniform — navy blue high-collar jacket and pants. Channeling cursed energy — dark purple and black swirling aura emanating from the body, geometric cursed technique patterns forming in the air. One hand extended performing a Domain Expansion hand seal. Background of a dark supernatural battlefield — cursed spirits as shadowy forms, shattered terrain, an eerie barrier forming as an inky void with floating cursed symbols. Dark moody color palette — midnight blue, purple, black, with electric blue highlights. Dark supernatural anime art quality.',
                 N'Anime & Gaming', N'👁️', '#311B92', 1, 104);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Titan Scout Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Titan Scout Style', N'Elite Scout Regiment soldier dark anime',
                 N'Transform the subject into an elite Scout Regiment soldier from Attack on Titan. CRITICAL: Preserve the subject''s exact facial features and identity. Render in semi-realistic anime style with intense dramatic expressions. Wearing the Survey Corps uniform — brown leather jacket with Wings of Freedom emblem on the back, white pants, knee-high brown boots. Equipped with Omni-Directional Mobility Gear — gas canisters, wire launchers, and dual ultra-hard steel blades drawn and ready. Dynamic mid-flight pose — soaring through the air with gear trailing wire cables, capes billowing. Background of a massive 50-meter Wall with colossal Titan fingers gripping the top, a Titan emerging from steam beyond. Muted military color palette — browns, greens, and grey with dramatic lighting. Dark epic anime art quality.',
                 N'Anime & Gaming', N'🏰', '#4E342E', 1, 105);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Shinobi Ninja Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Shinobi Ninja Style', N'Hidden Leaf Village ninja warrior anime',
                 N'Transform the subject into an elite shinobi ninja from the Hidden Leaf Village. CRITICAL: Preserve the subject''s exact facial features and identity. Render in Naruto anime style — clean sharp character design with expressive eyes and dynamic poses. Wearing a ninja outfit — dark tactical vest over a black bodysuit. A Hidden Leaf Village forehead protector with the leaf symbol engraved in metal. Ninja tool pouch on the thigh. Performing a powerful jutsu — hands forming a ninjutsu hand sign, with swirling chakra energy around the body. A giant technique visible (Rasengan sphere, fire dragon, or shadow clones). Background of Konohagakure — the Hidden Leaf Village with Hokage Rock faces carved into the mountain. Orange, blue, and green ninja color palette. Naruto anime art quality.',
                 N'Anime & Gaming', N'🍥', '#FF6F00', 1, 106);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Hero Academia Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Hero Academia Style', N'Quirk-powered superhero academy anime',
                 N'Transform the subject into a Pro Hero from the My Hero Academia universe. CRITICAL: Preserve the subject''s exact facial features and identity. Render in MHA manga/anime style — dynamic expressive character design with detailed hero costumes. Wearing a unique custom-designed hero costume — bold color scheme, armored gauntlets, utility belt, cape or scarf, and a distinctive mask or visor. Activating their Quirk — dramatic power manifestation with visual effects (energy blasts, elemental control, physical transformation). Dynamic Plus Ultra action pose with determination. Background of U.A. High School campus or a city under villain attack. Bright bold manga color palette with action speed lines. My Hero Academia anime art quality.',
                 N'Anime & Gaming', N'💥', '#1565C0', 1, 107);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Devil Hunter Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Devil Hunter Style', N'Chainsaw horror-action anime hunter',
                 N'Transform the subject into a Devil Hunter from Chainsaw Man. CRITICAL: Preserve the subject''s exact facial features and identity. Render in MAPPA''s gritty realistic anime style with raw visceral energy. Wearing a disheveled white shirt, loosened tie, and dark pants — battle-worn and splattered. Wild unkempt hair and intense exhausted yet determined eyes. A devil contract manifestation — chainsaw blades emerging from the arms and head, or a fiend transformation with devil horns and sharp teeth. Background of a dark Tokyo cityscape — demons emerging from shadows, destroyed city blocks, dramatic red sky. Dark gritty color palette — muted greys, blacks, with stark blood-red accents. Raw horror-action anime art quality.',
                 N'Anime & Gaming', N'🪚', '#B71C1C', 1, 108);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Shadow Monarch Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Shadow Monarch Style', N'S-Rank Hunter shadow army commander',
                 N'Transform the subject into an S-Rank Hunter and Shadow Monarch from Solo Leveling. CRITICAL: Preserve the subject''s exact facial features and identity. Render in Solo Leveling anime style — sharp handsome character design with piercing glowing blue-violet eyes radiating power. Wearing a dark black hunter outfit — long dark coat over tactical gear. Dark purple and black shadow energy swirling around the body. Commanding an army of shadow soldiers — ghostly dark warriors with glowing purple eyes rising from dark portals on the ground. One hand extended commanding the shadows. Background of a massive dungeon gate — towering stone gate with red ominous glow. Dark black and deep purple color palette with neon blue-violet highlights. Solo Leveling anime art quality.',
                 N'Anime & Gaming', N'👤', '#4A148C', 1, 109);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Elden Ring Tarnished')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Elden Ring Tarnished', N'Dark fantasy FromSoftware warrior',
                 N'Transform the subject into a Tarnished warrior from the Elden Ring dark fantasy world. CRITICAL: Preserve the subject''s exact facial features and identity. Render in FromSoftware''s dark fantasy aesthetic — weathered battle-scarred character with haunted determined eyes. Wearing ornate medieval Gothic plate armor with flowing tattered red cape, engravings and runes etched into the metal. Wielding a massive glowing rune-inscribed greatsword and a brass-trimmed shield. Grace site glowing faintly nearby. Background of the Lands Between — crumbling golden cathedral, vast desolate landscape with a massive golden glowing Erdtree in the distant sky, fog-shrouded ruins. Muted dark color palette — tarnished gold, deep crimson, fog grey. Dark fantasy video game art quality.',
                 N'Anime & Gaming', N'🗡️', '#BF360C', 1, 110);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Hyrule Champion Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Hyrule Champion Style', N'Legend of Zelda cel-shaded adventure',
                 N'Transform the subject into a Champion of Hyrule from The Legend of Zelda. CRITICAL: Preserve the subject''s exact facial features and identity. Render in Nintendo''s Breath of the Wild art style — soft cel-shaded anime-inspired character design with warm natural colors. Wearing the iconic blue Champion''s Tunic with Sheikah patterns, brown traveler pants and boots, and a hooded cloak. Pointed elf-like ears. Wielding a glowing blue sacred sword and a sturdy shield. Paraglider strapped to the back. Background of the vast Hyrule landscape — rolling green hills, distant volcano, castle in the distance, ancient towers on the horizon. Cel-shaded watercolor quality with soft golden hour sunlight. Pastel blue, green, and gold color palette. Nintendo art quality.',
                 N'Anime & Gaming', N'🧝', '#2E7D32', 1, 111);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Final Fantasy Crystal')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Final Fantasy Crystal', N'Legendary JRPG high-fantasy warrior',
                 N'Transform the subject into a legendary warrior from the Final Fantasy universe. CRITICAL: Preserve the subject''s exact facial features and identity. Render in iconic Final Fantasy character design — elegant elaborate costume with impossible fantasy fashion (zippers, belts, flowing asymmetric fabrics). Elaborate spiky or flowing hairstyle with silver streaks. Wielding an oversized ornate weapon — a massive sword, gunblade, or crystal staff. Dramatic battle-ready pose with magical aura. A crystal formation or magical rune circle glowing beneath. Background of a breathtaking landscape — floating islands, a crystal tower, airships in the sky, and waterfalls cascading into clouds. Luminous fantasy color palette — crystal blue, amethyst purple, and ethereal silver. Square Enix RPG character art quality.',
                 N'Anime & Gaming', N'💎', '#5C6BC0', 1, 112);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Genshin Vision Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Genshin Vision Style', N'Elemental Vision holder anime open world',
                 N'Transform the subject into an elemental Vision holder from the Genshin Impact universe. CRITICAL: Preserve the subject''s exact facial features and identity. Render in HoYoverse''s signature anime-meets-3D art style — beautiful polished character design with intricate costume details and soft cel-shaded coloring. Wearing an elaborate fantasy outfit inspired by a specific element — flowing fabrics, armor accents, elemental motifs (fire, ice, lightning, wind, water, nature, or stone). A glowing elemental Vision gem clipped to the outfit. Activating an Elemental Burst — dramatic elemental explosion with particles. Background of Teyvat — windmills, harbors, sakura trees, or rainforest. Vibrant anime color palette matching the element. Genshin Impact splash art quality.',
                 N'Anime & Gaming', N'✨', '#00BFA5', 1, 113);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Anime & Gaming StylePresets seeding failed (non-fatal)");
    }

    // ── Seed K-Culture style presets (14 styles) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            -- ============================================================
            -- K-CULTURE: Korean Pop Culture & Trending (SortOrder 120-133)
            -- ============================================================

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'K-Drama Lead')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'K-Drama Lead', N'Korean drama romantic lead character',
                 N'Transform the subject into a Korean drama lead character. CRITICAL: Preserve the subject''s exact facial features and identity. Render with flawless glass-skin K-beauty complexion with soft dewy highlights. Styled with a trendy Korean hairstyle — perfectly tousled curtain bangs or sleek side-part. Wearing a stylish Korean fashion outfit — tailored coat over a turtleneck, or a chic business-casual ensemble. Soft romantic expression — gentle smile with warm sparkling eyes. Cinematic Korean drama lighting — golden hour warm tones with soft bokeh background. Background of an iconic K-drama setting — a rooftop overlooking Seoul skyline at sunset, cherry blossom-lined street, or a cozy cafe. Romantic atmospheric haze. Warm golden, blush pink, and soft cream color palette. Korean drama promotional poster quality.',
                 N'K-Culture', N'🎭', '#E91E63', 1, 120);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'K-Pop Idol Stage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'K-Pop Idol Stage', N'K-Pop concert stage performance style',
                 N'Transform the subject into a K-Pop idol performing on stage. CRITICAL: Preserve the subject''s exact facial features and identity. Render with striking K-Pop stage makeup — bold eye look with glitter, perfectly contoured features, and flawless porcelain skin. Show-stopping hairstyle — vibrant colored (platinum, pastel pink, or electric blue) perfectly styled hair. Wearing an avant-garde stage outfit — sequined jacket, leather harness, or futuristic concept costume with metallic accents. Dynamic dance performance pose — sharp powerful choreography frozen mid-move. Background of a massive concert stage — LED screens with abstract visuals, thousands of lightsticks glowing in the audience, confetti and pyrotechnic sparks. Electric purple, hot pink, and laser blue color palette. K-Pop music video quality.',
                 N'K-Culture', N'🎤', '#AA00FF', 1, 121);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Webtoon Hero Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Webtoon Hero Style', N'Korean digital webtoon protagonist',
                 N'Transform the subject into a Korean webtoon protagonist. CRITICAL: Preserve the subject''s exact facial features and identity. Render in the distinctive Korean digital webtoon art style — clean polished linework with soft gradient cel-shading and luminous coloring. Large expressive Korean-style eyes with detailed iris reflections. Trendy Korean character design — fashionable modern outfit, perfect proportions. Dramatic close-up portrait with sparkle and screen-tone effects. Manhwa-style speed lines for dramatic emphasis. Background transitioning from realistic Seoul cityscape to fantasy — magical portals, gaming interface overlays, or romantic cherry blossom petals. Soft pastel gradients with selective vivid color pops. Professional Korean webtoon serial art quality.',
                 N'K-Culture', N'📱', '#2979FF', 1, 122);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Korean Hanbok Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Korean Hanbok Portrait', N'Classical traditional Korean Hanbok attire',
                 N'Transform the subject into a classical Korean Hanbok portrait. CRITICAL: Preserve the subject''s exact facial features and identity. Render wearing a magnificent traditional Korean Hanbok — a beautiful jeogori (upper garment) in rich vibrant silk with delicate gold embroidery and a flowing chima (skirt) in complementary color with ornate brocade patterns. Traditional Korean hair ornaments — a binyeo hairpin and daenggi ribbon in a classic updo, or a gat hat for men. Elegant refined posture with hands folded gracefully. Background of a beautiful Korean palace courtyard — Gyeongbokgung Palace with traditional wooden architecture, curved eaves, stone courtyard, and blooming chrysanthemums. Soft natural lighting. Rich hanbok colors — cerise pink, royal blue, jade green, and gold. Classical Korean portrait painting quality.',
                 N'K-Culture', N'👘', '#AD1457', 1, 123);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Seoul Neon Street')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Seoul Neon Street', N'Neon-lit Gangnam/Hongdae night style',
                 N'Transform the subject into a stylish figure in neon-lit Seoul nightlife. CRITICAL: Preserve the subject''s exact facial features and identity. Render with trendy Korean street fashion — oversized designer jacket, bucket hat or face mask pulled down, chunky sneakers, and layered accessories. Cool effortless confident pose — leaning against a wall or walking through a crosswalk. Background of iconic Seoul nightlife — Gangnam or Hongdae neon-lit streets with towering LED billboards showing K-Pop advertisements, glowing Korean Hangul signage, wet rain-reflective streets creating neon mirror effects. Street food stall steam rising. Dramatic neon lighting — electric pink, blue, and purple reflections on wet pavement. Urban night photography quality with cinematic color grading.',
                 N'K-Culture', N'🌃', '#6200EA', 1, 124);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'K-Beauty Editorial')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'K-Beauty Editorial', N'Flawless Korean glass-skin beauty magazine',
                 N'Transform the subject into a K-Beauty editorial magazine cover. CRITICAL: Preserve the subject''s exact facial features and identity. Render with the ultimate Korean glass-skin beauty aesthetic — luminous dewy porcelain skin with a lit-from-within glow. Perfect gradient lips in coral-to-pink. Soft puppy-eye or cat-eye makeup with shimmer eyeshadow. Naturally groomed fluffy brows. Flawless skin with visible dewdrop highlights on cheekbones and nose bridge. Hair styled with soft Korean waves. Minimal jewelry — delicate pearl earrings. Clean studio beauty lighting with soft ring-light catchlights in the eyes. Pure white or soft pastel gradient background with floating flower petals. Skin-tone, blush pink, and peach color palette. Korean beauty magazine editorial photography quality.',
                 N'K-Culture', N'💄', '#F48FB1', 1, 125);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Joseon Dynasty Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Joseon Dynasty Portrait', N'Historical Korean royal court portrait',
                 N'Transform the subject into a Joseon Dynasty royal court portrait. CRITICAL: Preserve the subject''s exact facial features and identity. Render in traditional Korean royal portrait style. Wearing elaborate Joseon royal court attire — a dragon-embroidered gonryongpo (royal robe) in deep crimson or blue with gold thread dragons and cloud motifs, an ikseongwan (winged cap) or hwagwan (flower crown). Formal seated royal pose on a throne with a folding screen behind. Rich brocade and silk textures with intricate embroidery details. Background of a Joseon royal court — painted irworobongdo screen behind the throne, wooden palace interior with latticed doors. Muted traditional Korean color palette — deep reds, indigo, gold, and cream. Classical Joseon Dynasty portrait painting quality.',
                 N'K-Culture', N'👑', '#8D6E63', 1, 126);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'K-Pop Album Cover')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'K-Pop Album Cover', N'Conceptual K-Pop album art photo',
                 N'Transform the subject into a conceptual K-Pop album cover photo. CRITICAL: Preserve the subject''s exact facial features and identity. Render with high-concept K-Pop visual aesthetic — ethereal otherworldly styling with avant-garde fashion. Artistic editorial makeup with graphic elements — rhinestones, metallic accents, or painted designs on the face. Wearing a conceptual outfit — either dark moody (black leather, chains, dark romanticism) or dreamy ethereal (flowing white fabrics, angel wings, crystals). Artistic pose with strong visual composition. Background of a surreal K-Pop set — floating geometric shapes, holographic surfaces, otherworldly landscapes, or abstract color gradients. High-fashion editorial color grading. K-Pop album concept photo quality with magazine-level retouching.',
                 N'K-Culture', N'💿', '#FF6D00', 1, 127);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Korean Folklore')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Korean Folklore', N'Mystical Korean mythology Gumiho spirit',
                 N'Transform the subject into a character from Korean folklore and mythology. CRITICAL: Preserve the subject''s exact facial features and identity. Render as a mystical Gumiho (nine-tailed fox spirit) with ethereal fox ears, nine flowing fox tails made of blue-white spirit fire, and luminous golden eyes with vertical slit pupils. Wearing an elegant white and red traditional hanbok that flows and transforms into fox-fire at the edges. Holding a yeouiju (magical fox bead) glowing with blue mystical energy. Supernatural beauty with an enchanting yet dangerous expression. Background of a moonlit Korean mountain temple — ancient pine trees, stone pagodas, misty valleys, full moon casting silver light. Fox-fire wisps floating in the air. Blue-white, silver, and deep red color palette. Korean folklore meets modern fantasy illustration quality.',
                 N'K-Culture', N'🦊', '#FF5722', 1, 128);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Manhwa Action Style')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Manhwa Action Style', N'Korean action manga protagonist',
                 N'Transform the subject into an action manhwa (Korean manga) protagonist. CRITICAL: Preserve the subject''s exact facial features and identity. Render in dynamic Korean manhwa webtoon action style — sharp angular character design with intense determined eyes and sleek modern features. Dark hair with dramatic shading. Wearing a stylish modern combat outfit — tactical jacket, dark pants, combat boots with subtle magical or tech enhancements glowing. In the middle of an explosive action scene — delivering a powerful punch with shockwave impact rings. Dynamic speed lines and impact effects radiating outward. Background of an urban Seoul battlefield — cracked streets, shattered buildings, dungeon gate portals opening in the sky. High-contrast dramatic color palette — dark blacks with vivid red and electric blue highlights. Korean manhwa action art quality.',
                 N'K-Culture', N'⚡', '#D50000', 1, 129);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Korean Cafe Aesthetic')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Korean Cafe Aesthetic', N'Cozy Seoul cafe lifestyle illustration',
                 N'Transform the subject into a cozy Korean cafe aesthetic scene. CRITICAL: Preserve the subject''s exact facial features and identity. Render with warm soft Korean illustration style — gentle linework with watercolor-like pastel coloring. Wearing a comfortable chic outfit — oversized knit sweater, beret, or cozy scarf. Sitting in a Seoul cafe — holding a beautifully crafted latte art coffee cup, reading a book with a slice of Korean honey toast nearby. Soft content smile with warm eyes. Background of an aesthetic Korean cafe interior — exposed brick walls, warm Edison bulb string lights, wooden furniture, green hanging plants, floor-to-ceiling windows showing a rainy Seoul street. Warm cozy color palette — cream, caramel brown, sage green, and blush pink. Korean lifestyle illustration quality.',
                 N'K-Culture', N'☕', '#795548', 1, 130);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Chibi K-Pop')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Chibi K-Pop', N'Adorable chibi K-Pop idol character',
                 N'Transform the subject into an adorable chibi K-Pop idol character. CRITICAL: Preserve the subject''s facial likeness and identity in chibi form. Render in Korean chibi (SD — super deformed) style — enormous head (3:1 ratio to body), giant sparkly eyes with star-shaped highlights, tiny adorable body with stubby limbs. Cute K-Pop stage outfit miniaturized — sparkly jacket, lightstick accessory, and tiny headset microphone. Performing an iconic K-Pop heart pose — finger heart, double heart, or aegyo pose with blushing cheeks. Surrounded by cute floating elements — hearts, stars, music notes, sparkles, mini lightsticks. Background of pastel concert stage with chibi audience holding lightsticks. Pastel candy color palette — bubblegum pink, baby blue, lavender, and mint. Adorable Korean fan-art chibi illustration quality.',
                 N'K-Culture', N'🧸', '#EC407A', 1, 131);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Korean Horror')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Korean Horror', N'Atmospheric Korean horror cinema style',
                 N'Transform the subject into a scene from Korean horror cinema. CRITICAL: Preserve the subject''s exact facial features and identity. Render in atmospheric Korean horror film aesthetic — pale desaturated skin with dark shadows under the eyes, wet black hair partially covering the face. Wearing a simple white or school uniform outfit, dampened and slightly tattered. A subtle uncanny expression — wide unblinking eyes staring slightly off-center, ambiguous between fear and menace. A single hand reaching forward from darkness. Background of a dark rain-soaked apartment corridor with flickering fluorescent lights, or an abandoned school hallway with peeling wallpaper. Water stains creeping down walls. A ghostly reflection in a window. Desaturated cold color palette — pale blue-grey, sickly green, deep black with a single red accent. Korean horror film cinematography quality.',
                 N'K-Culture', N'👻', '#37474F', 1, 132);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Korean Ink Brush')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Korean Ink Brush', N'Traditional Korean sumi ink brush painting',
                 N'Transform the subject into a traditional Korean ink brush painting (sumukhwa). CRITICAL: Preserve the subject''s facial likeness and identity. Render in elegant Korean sumi ink brush painting style — bold expressive black ink brushstrokes on textured hanji (Korean mulberry paper). Varying brush pressure creating thick-to-thin calligraphic lines — bold confident strokes for the main form with delicate thin strokes for details. Minimal color — primarily black ink with subtle washes of grey. A sparse touch of red cinnabar seal stamp in the corner. Negative space used masterfully — the empty paper as important as the painted areas. Background of natural Korean landscape elements — pine branches, bamboo, plum blossoms, or misty mountains in sansuhwa style. Monochrome ink with occasional pale blue-green or warm ochre wash accents. Traditional Korean literati painting quality.',
                 N'K-Culture', N'🖌️', '#263238', 1, 133);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "K-Culture StylePresets seeding failed (non-fatal)");
    }

    // ── PhotoArts StylePresets ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Portrait Oil Painting')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Portrait Oil Painting', N'Expressive oil painting portrait on canvas',
                 N'A professional portrait of [subject] rendered as an expressive oil painting on canvas. High contrast, thick impasto brushwork in the background, and fine-detail blending on the face. Rich, warm color palette. The lighting should be dramatic, highlighting the contours of the face like a studio portrait. Artistic, painterly, textured, and sophisticated.',
                 N'PhotoArts', N'🖼️', '#8D6E63', 1, 156);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Smudge Oil Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Smudge Oil Portrait', N'Ultra-smooth digital smudge painting with cinematic glow',
                 N'A high-quality digital oil painting of [subject]. The style is ""smudge painting"" with ultra-smooth skin textures, vibrant saturation, and visible but soft brushstrokes. The background is a soft-focus abstract bokeh with warm, moody lighting. Professional digital art, sharp facial features, deep shadows, and an artistic glow on the skin. 8k resolution, cinematic lighting.',
                 N'PhotoArts', N'🎨', '#E65100', 1, 157);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Vexel Art Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Vexel Art Portrait', N'Clean vexel illustration with detailed hair and vibrant colors',
                 N'A stylized digital illustration of [subject]. Clean lines, smooth color gradients, and a ""vexel art"" aesthetic. The hair is highly detailed with individual light-catching strands. Simple, elegant, blurred background to make the subject pop. High-end retouching style, vibrant colors, and sharp, soulful eyes.',
                 N'PhotoArts', N'✨', '#7C4DFF', 1, 158);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Vibrant Digital Oil')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Vibrant Digital Oil', N'Vibrant digital oil painting with dramatic warm lighting',
                 N'A vibrant, expressive digital oil painting of [subject]. The art style features smooth, blended brushstrokes and high-gloss textures, with more suttled pinkish tone particularly on the skin and lips. Use a warm, dramatic lighting scheme with strong golden-orange highlights on one side of the face and deep purple or charcoal shadows in the background. The person has dark, voluminous, hair that blends softly into a textured, painterly backdrop. Focus on hyper-realistic details in the eyes and teeth while maintaining a buttery ''oil paint'' finish throughout the composition.',
                 N'PhotoArts', N'🌟', '#FF8F00', 1, 159);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Chromatic Oil Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Chromatic Oil Portrait', N'Oil painting with vivid chromatic rainbow hair colors',
                 N'Transform into a rich oil painting on canvas with thick impasto brushstrokes. Change hair color to vivid chromatic rainbow gradient — streaks of electric blue, magenta, violet, emerald green, and golden amber. Abstract teal and ochre painted background. Deep saturated colors, dramatic lighting.',
                 N'PhotoArts', N'🌈', '#E040FB', 1, 160);

            UPDATE StylePresets SET PromptTemplate = N'Transform into a rich oil painting on canvas with thick impasto brushstrokes. Change hair color to vivid chromatic rainbow gradient — streaks of electric blue, magenta, violet, emerald green, and golden amber. Abstract teal and ochre painted background. Deep saturated colors, dramatic lighting.'
            WHERE Name = 'Chromatic Oil Portrait';

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Royal Oil Canvas')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Royal Oil Canvas', N'Rich royal oil painting for singles, couples & families of all ages',
                 N'Transform into a classical Renaissance oil painting on canvas. Rich impasto brushstrokes, warm burnt sienna undertones, sfumato skin blending, thick paint texture on fabric and jewelry. Warm maroon-gold-amber gradient background with visible canvas weave. Dramatic Rembrandt side lighting. Museum-quality fine art.',
                 N'PhotoArts', N'👑', '#FFD700', 1, 161);

            UPDATE StylePresets SET PromptTemplate = N'Transform into a classical Renaissance oil painting on canvas. Rich impasto brushstrokes, warm burnt sienna undertones, sfumato skin blending, thick paint texture on fabric and jewelry. Warm maroon-gold-amber gradient background with visible canvas weave. Dramatic Rembrandt side lighting. Museum-quality fine art.'
            WHERE Name = 'Royal Oil Canvas';

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Wedding Glam Oil')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Wedding Glam Oil', N'Premium digital smudge oil portrait with warm gradient backdrop',
                 N'Transform into a polished digital smudge oil painting. Ultra-smooth buttery brushwork with silky blending. Hyper-vibrant saturated colors — intensify all reds, golds, greens. Smooth warm gradient background from olive-gold through amber to deep maroon-red. Soft warm golden studio lighting with luminous skin glow. Premium wedding portrait art.',
                 N'PhotoArts', N'💍', '#E91E63', 1, 162);

            UPDATE StylePresets SET PromptTemplate = N'Transform into a polished digital smudge oil painting. Ultra-smooth buttery brushwork with silky blending. Hyper-vibrant saturated colors — intensify all reds, golds, greens. Smooth warm gradient background from olive-gold through amber to deep maroon-red. Soft warm golden studio lighting with luminous skin glow. Premium wedding portrait art.'
            WHERE Name = 'Wedding Glam Oil';

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Vivid Smudge Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Vivid Smudge Portrait', N'Vibrant digital smudge painting with colorful abstract backdrop',
                 N'Transform into a vivid digital smudge oil painting. Visible smooth brushstrokes on every surface, glossy wet-paint sheen. Warm golden-peach skin tones with painterly glow. Abstract background of teal, ochre, dusty rose, and sage green color patches with loose brushstrokes. All colors hyper-vibrant and deeply saturated. Must look like a hand-painted artwork.',
                 N'PhotoArts', N'🎭', '#26A69A', 1, 164);

            UPDATE StylePresets SET PromptTemplate = N'Transform into a vivid digital smudge oil painting. Visible smooth brushstrokes on every surface, glossy wet-paint sheen. Warm golden-peach skin tones with painterly glow. Abstract background of teal, ochre, dusty rose, and sage green color patches with loose brushstrokes. All colors hyper-vibrant and deeply saturated. Must look like a hand-painted artwork.'
            WHERE Name = 'Vivid Smudge Portrait';

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Dreamy Glow Oil')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Dreamy Glow Oil', N'Soft dreamy oil portrait with luminous skin glow and moody hues',
                 N'Transform into a dreamy digital oil painting with soft ethereal glow. Luminous dewy skin with warm peach-rose undertones. Ultra-smooth blended brushwork. Moody background gradient of warm greys, dusty mauve, purple haze, and amber with dark vignette corners. Warm golden rim light creating halo on hair edges. Intensify all original colors.',
                 N'PhotoArts', N'🌸', '#CE93D8', 1, 163);

            UPDATE StylePresets SET PromptTemplate = N'Transform into a dreamy digital oil painting with soft ethereal glow. Luminous dewy skin with warm peach-rose undertones. Ultra-smooth blended brushwork. Moody background gradient of warm greys, dusty mauve, purple haze, and amber with dark vignette corners. Warm golden rim light creating halo on hair edges. Intensify all original colors.'
            WHERE Name = 'Dreamy Glow Oil';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "PhotoArts StylePresets seeding failed (non-fatal)");
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

    // Clean up: remove any seeded styles that the user previously deleted
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DELETE FROM StylePresets
            WHERE Name IN (SELECT Name FROM DeletedStyleSeeds)");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Deleted style cleanup failed (non-fatal)");
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
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net https://checkout.razorpay.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: blob: https://lh3.googleusercontent.com; " +
        "connect-src 'self' ws: wss: https://cdn.jsdelivr.net https://accounts.google.com" +
            (app.Environment.IsDevelopment() ? " http://localhost:*" : "") + "; " +
        "frame-src https://api.razorpay.com https://checkout.razorpay.com; " +
        "frame-ancestors 'none'";
    await next();
});

app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── Auth API endpoints (minimal API — Blazor Server can't set cookies via SignalR) ──
app.MapGet("/api/auth/google-login", (HttpContext ctx, string? returnUrl, string? refCode) =>
{
    var redirectUri = "/api/auth/google-callback";
    if (!string.IsNullOrEmpty(refCode))
        redirectUri += $"?ref={Uri.EscapeDataString(refCode)}";
    var props = new AuthenticationProperties { RedirectUri = redirectUri };
    return Results.Challenge(props, [GoogleDefaults.AuthenticationScheme]);
});

app.MapGet("/api/auth/google-callback", async (HttpContext ctx, IAuthService authService, IConfiguration config) =>
{
    var result = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    if (!result.Succeeded)
        return Results.Redirect("/login?error=Authentication+failed");

    var googleId = result.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    var email = result.Principal?.FindFirstValue(ClaimTypes.Email) ?? "";
    var name = result.Principal?.FindFirstValue(ClaimTypes.Name) ?? "";
    var avatar = result.Principal?.FindFirstValue("urn:google:picture")
              ?? result.Principal?.Claims.FirstOrDefault(c => c.Type == "picture")?.Value;

    if (string.IsNullOrEmpty(googleId))
        return Results.Redirect("/login?error=Missing+Google+ID");

    var superAdminEmail = config["SuperAdmin:Email"] ?? "";
    var referralCode = ctx.Request.Query["ref"].FirstOrDefault();
    var user = await authService.FindOrCreateUserAsync(googleId, email, name, avatar, superAdminEmail, referralCode);

    if (!user.IsActive)
        return Results.Redirect("/login?error=Account+deactivated");

    // Build claims and sign in
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Name, user.DisplayName),
        new("avatar", user.AvatarUrl ?? ""),
        new(ClaimTypes.Role, user.Role.ToString())
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

    return Results.Redirect("/");
});

app.MapGet("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/api/auth/user-info", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Json(new { authenticated = false });

    return Results.Json(new
    {
        authenticated = true,
        id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier),
        email = ctx.User.FindFirstValue(ClaimTypes.Email),
        name = ctx.User.FindFirstValue(ClaimTypes.Name),
        avatar = ctx.User.FindFirstValue("avatar"),
        role = ctx.User.FindFirstValue(ClaimTypes.Role)
    });
});

// ── Payment API endpoints ──
app.MapPost("/api/payment/create-order", async (HttpContext ctx, IRazorpayService razorpay) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var userIdStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!int.TryParse(userIdStr, out var userId))
        return Results.BadRequest("Invalid user");

    var body = await ctx.Request.ReadFromJsonAsync<CreateOrderRequest>();
    if (body == null) return Results.BadRequest("Invalid request");

    try
    {
        var result = await razorpay.CreateOrderAsync(userId, body.Purpose, body.CoinPackId, body.SubscriptionPlanId);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/payment/verify", async (HttpContext ctx, IRazorpayService razorpay) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var body = await ctx.Request.ReadFromJsonAsync<VerifyPaymentRequest>();
    if (body == null) return Results.BadRequest("Invalid request");

    var success = await razorpay.CompletePaymentAsync(body.PaymentId, body.RazorpayPaymentId, body.RazorpaySignature);
    return success ? Results.Ok(new { success = true }) : Results.BadRequest(new { error = "Payment verification failed" });
});

app.MapPost("/api/payment/webhook", async (HttpContext ctx, IRazorpayService razorpay) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var payload = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Razorpay-Signature"].FirstOrDefault() ?? "";

    await razorpay.HandleWebhookAsync(payload, signature);
    return Results.Ok();
}).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// ── Request DTOs for payment endpoints ──
public record CreateOrderRequest(ArtForgeAI.Models.PaymentPurpose Purpose, int? CoinPackId, int? SubscriptionPlanId);
public record VerifyPaymentRequest(int PaymentId, string RazorpayPaymentId, string RazorpaySignature);
