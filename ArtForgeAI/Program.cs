using System.Security.Claims;
using System.Threading.RateLimiting;
using ArtForgeAI.Components;
using ArtForgeAI.Data;
using ArtForgeAI.Middleware;
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

// Background removal — full pipeline: U²-Net → MODNet → Edge Feathering → PNG
builder.Services.AddSingleton<IBackgroundRemovalService, BgRemovalPipelineService>();

// Background removal via Gemini AI (uses GeminiOptions already configured above)
builder.Services.AddHttpClient<RemoveBgService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

// Local ONNX background removal — U-2-Net (singleton — model loaded once, auto-downloads)
builder.Services.AddSingleton<OnnxBgRemovalService>();

// BG Studio — additional removal models (all singleton, auto-download on first use)
builder.Services.AddHttpClient<RemoveBgApiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<BriaRmbgService>();   // BRIA RMBG 2.0 (~176MB ONNX)
builder.Services.AddSingleton<IsNetBgService>();     // IS-Net DIS (~176MB ONNX)
builder.Services.AddSingleton<BiRefNetBgService>();  // BiRefNet (~228MB ONNX)

// Python sidecar — PyTorch models (BRIA, BiRefNet, InSPyReNet) via local Flask server
builder.Services.AddHttpClient<PythonBgSidecarService>();

// Local ONNX image enhancement — Real-ESRGAN 4x upscale (singleton — model loaded once)
builder.Services.AddSingleton<IImageEnhancerService, OnnxImageEnhancerService>();

// Local ONNX color enhancement — SCI illumination (singleton — model loaded once)
builder.Services.AddSingleton<IColorEnhancementService, OnnxColorEnhancementService>();

// Image size master CRUD
builder.Services.AddScoped<IImageSizeMasterService, ImageSizeMasterService>();

// Style preset CRUD
builder.Services.AddScoped<IStylePresetService, StylePresetService>();
builder.Services.AddScoped<IStyleGroupService, StyleGroupService>();

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
builder.Services.AddScoped<TiledGeminiEnhanceService>();
builder.Services.AddScoped<ITemplateCollageService, TemplateCollageService>();
builder.Services.AddScoped<ICollageTemplateService, CollageTemplateService>();

// ── Clone Protection: Multi-layer anti-piracy system ──
builder.Services.AddSingleton<LicenseService>();              // Offline RSA-signed license
builder.Services.AddSingleton<DomainLockService>();           // Domain/URL lock
builder.Services.AddSingleton<TransactionIntegrityService>(); // HMAC-signed transactions
builder.Services.AddSingleton<OnlineLicenseValidationService>(); // Online phone-home heartbeat
builder.Services.AddHostedService<RuntimeProtectionHostedService>(); // Background security monitor

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

    // Add IntegrityHash column for tamper detection on coin transactions
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('CoinTransactions') AND name = 'IntegrityHash')
                ALTER TABLE CoinTransactions ADD IntegrityHash NVARCHAR(64) NULL;");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "CoinTransactions IntegrityHash column failed (non-fatal)");
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

    // ── Seed-version gate: skip heavy style seeding if already done ──
    const int CURRENT_SEED_VERSION = 4;
    var seedAlreadyDone = false;
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '__SeedVersion')
            CREATE TABLE __SeedVersion (Version INT NOT NULL)");
        var versionRows = await db.Database.SqlQueryRaw<int>(
            "SELECT ISNULL(MAX(Version),0) AS [Value] FROM __SeedVersion").ToListAsync();
        if (versionRows.Count > 0 && versionRows[0] >= CURRENT_SEED_VERSION)
        {
            seedAlreadyDone = true;
            app.Logger.LogInformation("Style seed version {v} already applied — skipping seed queries.", versionRows[0]);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "__SeedVersion check failed (non-fatal, will re-seed)");
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

    // Create StyleGroups table if missing
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'StyleGroups')
            BEGIN
                CREATE TABLE StyleGroups (
                    StyleGroupId INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(128) NOT NULL,
                    SortOrder INT NOT NULL DEFAULT 0,
                    IsActive BIT NOT NULL DEFAULT 1,
                    CreatedAtUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT UQ_StyleGroups_Name UNIQUE (Name)
                );
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "StyleGroups table creation failed (non-fatal)");
    }

    // ── CollageTemplates table ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CollageTemplates')
            BEGIN
                CREATE TABLE CollageTemplates (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Name NVARCHAR(100) NOT NULL,
                    Description NVARCHAR(500) NOT NULL DEFAULT '',
                    Category NVARCHAR(50) NOT NULL,
                    ThumbnailPath NVARCHAR(500) NULL,
                    SlotCount INT NOT NULL DEFAULT 5,
                    SlotDescriptionsJson NVARCHAR(MAX) NOT NULL DEFAULT '[]',
                    MasterSlotIndex INT NOT NULL DEFAULT 0,
                    ColorTheme NVARCHAR(200) NOT NULL DEFAULT '',
                    Mood NVARCHAR(200) NOT NULL DEFAULT '',
                    DecorativeElements NVARCHAR(500) NOT NULL DEFAULT '',
                    TextOverlay NVARCHAR(200) NOT NULL DEFAULT '',
                    LayoutDescription NVARCHAR(MAX) NULL,
                    IconEmoji NVARCHAR(10) NOT NULL DEFAULT '',
                    AskName BIT NOT NULL DEFAULT 0,
                    AskOccasion BIT NOT NULL DEFAULT 0,
                    AskDate BIT NOT NULL DEFAULT 0,
                    AskMessage BIT NOT NULL DEFAULT 0,
                    IsActive BIT NOT NULL DEFAULT 1,
                    SortOrder INT NOT NULL DEFAULT 0,
                    CreatedAtUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE()
                );
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "CollageTemplates table creation failed (non-fatal)");
    }

    // Add Ask* columns if missing (migration for existing tables)
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('CollageTemplates') AND name = 'AskName')
            BEGIN
                ALTER TABLE CollageTemplates ADD AskName BIT NOT NULL DEFAULT 0;
                ALTER TABLE CollageTemplates ADD AskOccasion BIT NOT NULL DEFAULT 0;
                ALTER TABLE CollageTemplates ADD AskDate BIT NOT NULL DEFAULT 0;
                ALTER TABLE CollageTemplates ADD AskMessage BIT NOT NULL DEFAULT 0;
            END");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "CollageTemplates Ask* columns migration failed (non-fatal)");
    }

    // Seed CollageTemplates — reseed with v2 pose-variation descriptions
    try
    {
        // Check if v2 seed is needed (look for old imaginary slot descriptions)
        var needsReseed = false;
        var collageCount = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM CollageTemplates").ToListAsync();
        if (collageCount.Count == 0 || collageCount[0] == 0)
        {
            needsReseed = true;
        }
        else
        {
            // Check if we need to reseed: old imaginary descriptions or missing new templates
            var oldRows = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS [Value] FROM CollageTemplates WHERE SlotDescriptionsJson LIKE '%blowing candles%' OR SlotDescriptionsJson LIKE '%throwing colorful%' OR SlotDescriptionsJson LIKE '%holding baby shoes%' OR SlotDescriptionsJson LIKE '%candlelit dinner%'").ToListAsync();
            var hasBWStrip = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS [Value] FROM CollageTemplates WHERE Name = 'B&W Strip Panels'").ToListAsync();
            var hasQueenPortrait = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS [Value] FROM CollageTemplates WHERE Name = 'Queen Portrait'").ToListAsync();
            if ((oldRows.Count > 0 && oldRows[0] > 0) || (hasBWStrip.Count > 0 && hasBWStrip[0] == 0) || (hasQueenPortrait.Count > 0 && hasQueenPortrait[0] == 0))
            {
                await db.Database.ExecuteSqlRawAsync("DELETE FROM CollageTemplates");
                needsReseed = true;
                app.Logger.LogInformation("Cleared CollageTemplates for reseed (old descriptions or missing new templates).");
            }
        }
        if (needsReseed)
        {
            await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO CollageTemplates (Name, Description, Category, SlotCount, SlotDescriptionsJson, MasterSlotIndex, ColorTheme, Mood, DecorativeElements, TextOverlay, LayoutDescription, IconEmoji, AskName, AskOccasion, AskDate, AskMessage, SortOrder)
VALUES
('Birthday Bash Burst', 'Vibrant birthday collage with balloons, confetti and 5 themed photos', 'Birthday', 5,
 '[""Center circle hero shot - looking straight at camera, big warm smile, head and shoulders"",""Top-left tilted frame - head tilted slightly left, looking to the side, playful smirk, waist-up"",""Top-right tilted frame - laughing with head thrown back slightly, joyful expression, close-up"",""Bottom-left tilted frame - looking down with gentle smile, chin slightly lowered, medium shot"",""Bottom-right tilted frame - looking over shoulder, side angle, confident grin, upper body""]',
 0, 'soft pink, magenta, purple gradient with gold accents', 'festive, celebratory, joyful, playful',
 'colorful balloons, confetti, shooting stars, sparkles, ribbons, party streamers',
 'Happy Birthday',
 'Center: large circular frame (40% of height) with hero portrait and white border ring. Top-left: rectangular frame tilted 5 degrees clockwise. Top-right: rectangular frame tilted 5 degrees counter-clockwise. Bottom-left: rectangular frame tilted 5 degrees clockwise. Bottom-right: rectangular frame tilted 5 degrees counter-clockwise. Title Happy Birthday at top in bold decorative font. Warm wish message in cursive at the bottom. Background: gradient from soft pink to light purple with scattered confetti and floating balloons.',
 '🎂', 1, 1, 0, 1, 1),

('Birthday Celebration Classic', 'Classic birthday collage with elegant gold-themed design', 'Birthday', 4,
 '[""Center - looking at camera, warm genuine smile, head and shoulders portrait"",""Left - three-quarter face angle turned left, serene expression, upper body"",""Right - close-up face, eyes looking up, bright happy expression"",""Bottom - wider medium shot, arms crossed confidently, calm smile""]',
 0, 'deep royal blue, gold, white', 'elegant, celebratory, warm',
 'gold stars, glitter, elegant frames, crown, candles',
 'Happy Birthday',
 'Center: large rectangular frame taking 50% width. Left: tall rectangular frame. Right: tall rectangular frame. Bottom center: wide rectangular frame spanning full width. Title text at very top in gold cursive. Background: deep royal blue with gold glitter particles.',
 '🎉', 1, 1, 0, 1, 2),

('Baby Shower Joy', 'Adorable baby shower collage with pastel colors', 'Baby Shower', 5,
 '[""Center circle - gentle smile looking at camera, soft warm lighting, head and shoulders"",""Top-left - looking to the right, peaceful serene expression, close-up"",""Top-right - slight head tilt, dreamy gentle smile, upper body shot"",""Bottom-left - looking down with tender expression, chin lowered, medium shot"",""Bottom-right - profile view facing right, calm and glowing, side portrait""]',
 0, 'pastel pink, mint green, soft lavender, cream', 'gentle, loving, warm, dreamy',
 'baby rattles, teddy bears, clouds, stars, hearts, tiny footprints, soft bows',
 'Welcome Little One',
 'Center: large circular frame with soft white border. Four corner rectangular frames with rounded corners. Title at top in soft handwritten font. Pastel gradient background with floating clouds and tiny stars.',
 '👶', 1, 1, 1, 1, 3),

('Wedding Bliss', 'Romantic wedding collage with floral elegance', 'Wedding', 5,
 '[""Center circle - elegant pose looking at camera, soft romantic lighting, head and shoulders"",""Top-left - looking over left shoulder, graceful three-quarter turn, upper body"",""Top-right - close-up face, eyes slightly lowered, subtle romantic smile"",""Bottom-left - profile view facing left, serene elegant expression"",""Bottom-right - head tilted right, looking up, dreamy joyful expression, medium shot""]',
 0, 'blush pink, rose gold, ivory, soft green', 'romantic, elegant, timeless, dreamy',
 'roses, peonies, gold vines, delicate lace patterns, floating petals, soft bokeh lights',
 'Happily Ever After',
 'Center: large circular frame with rose gold border. Four corner rectangular frames with soft rounded edges. Floral arrangements in corners. Title in elegant script font at top. Background: soft ivory with blush pink gradient and floating petals.',
 '💒', 1, 1, 1, 1, 4),

('Graduation Pride', 'Proud graduation collage celebrating academic achievement', 'Graduation', 4,
 '[""Center - proud confident smile looking at camera, head and shoulders"",""Left - three-quarter angle looking to the right, determined expression, close-up"",""Right - looking upward with hopeful expression, upper body, inspired pose"",""Bottom - wider shot, arms at sides standing tall, calm accomplished smile""]',
 0, 'navy blue, gold, white, crimson', 'proud, accomplished, celebratory, inspirational',
 'graduation caps, diplomas, gold laurel wreaths, stars, confetti, academic scrolls',
 'Congratulations Graduate',
 'Center: large square frame with gold border. Left: vertical rectangular frame. Right: vertical rectangular frame. Bottom: wide horizontal frame. Title in bold serif font at top with laurel wreath. Background: navy blue with gold accents and floating caps.',
 '🎓', 1, 1, 1, 1, 5),

('Anniversary Love', 'Heartfelt anniversary collage with romantic touches', 'Anniversary', 4,
 '[""Center circle - warm loving smile at camera, soft golden lighting, head and shoulders"",""Top - looking to the side with nostalgic gentle smile, wider upper body shot"",""Bottom-left - close-up face, eyes softly gazing downward, tender expression"",""Bottom-right - three-quarter turn looking over shoulder, warm romantic glow""]',
 0, 'deep red, gold, champagne, soft pink', 'romantic, loving, warm, nostalgic',
 'hearts, roses, champagne glasses, gold rings, candles, rose petals, soft sparkles',
 'Happy Anniversary',
 'Center: large circular frame with gold ring border. Top: wide rectangular frame spanning full width. Bottom: two equal rectangular frames side by side. Title in elegant gold script. Background: deep red to champagne gradient with floating rose petals and hearts.',
 '❤️', 1, 1, 1, 1, 6),

('Festival of Colors', 'Vibrant colorful celebration collage', 'Festival', 5,
 '[""Center circle - big energetic smile at camera, head and shoulders, vibrant lighting"",""Top-left - head tilted back, laughing with open mouth, joyful close-up"",""Top-right - looking to the left, wide grin, three-quarter face angle, upper body"",""Bottom-left - looking down with playful smirk, chin lowered, medium shot"",""Bottom-right - profile view facing right, big smile, side portrait with colorful glow""]',
 0, 'vibrant magenta, electric blue, sunny yellow, bright green, orange', 'joyful, energetic, vibrant, playful',
 'colorful powder clouds, flower petals, rangoli patterns, sparkles',
 'Festival of Colors',
 'Center: large circular frame with rainbow border. Four corner rectangular frames tilted slightly. Colorful effects between frames. Title in bold colorful block letters. Background: white with splashes of vibrant colors.',
 '🎨', 1, 1, 0, 1, 7),

('Mothers Day Love', 'Tender and beautiful Mothers Day tribute collage', 'Mothers Day', 4,
 '[""Center - warm motherly smile at camera, soft lighting, head and shoulders"",""Left - profile view facing right, peaceful serene expression, gentle glow"",""Right - looking down with tender loving smile, close-up face"",""Bottom - three-quarter angle, looking to the left, calm grateful expression, medium shot""]',
 0, 'lavender, soft pink, cream, mint green', 'loving, tender, warm, grateful',
 'carnations, roses, hearts, butterflies, soft ribbons, watercolor flowers',
 'Happy Mothers Day',
 'Center: large oval frame with floral border. Left and right: vertical rectangular frames with soft edges. Bottom: wide horizontal frame. Watercolor flower arrangements around frames. Title in elegant handwritten font. Background: soft lavender to cream gradient.',
 '💐', 1, 1, 0, 1, 8),

('Golden Cinematic Portrait', 'Elegant multi-exposure cinematic portrait with warm golden tones and dreamy bokeh', 'Portrait', 5,
 '[""Center foreground - large hero portrait, looking at camera with warm confident smile, head to waist, sharp focus"",""Top-left background - dreamy soft-focus close-up of face, eyes looking away, ethereal golden glow, faded overlay"",""Top-right background - gentle side profile facing right, soft serene expression, blended with golden light"",""Left background - medium shot from different angle, looking slightly upward, soft fade into background"",""Right background - three-quarter view, chin slightly raised, elegant expression, soft bokeh blend""]',
 0, 'warm golden, bronze, amber, soft champagne, dark brown undertones', 'cinematic, elegant, dreamy, warm, ethereal',
 'golden sparkles, bokeh light orbs, soft light rays, subtle lens flare, warm gradient overlay',
 '',
 'This is a CINEMATIC MULTI-EXPOSURE style collage — NOT a framed grid layout. The hero/master portrait is in the CENTER FOREGROUND, sharp and prominent, taking up about 60 percent of the image from mid-chest upward. The other photos are BLENDED INTO THE BACKGROUND using soft dissolve/fade/double-exposure technique — they should appear as dreamy overlays behind and around the hero shot, NOT in separate frames. Top-left: faded dreamy close-up. Top-right: soft side profile fading into golden light. Left behind hero: medium shot with low opacity blend. Right behind: three-quarter view fading out. Background is a warm golden-to-dark-brown gradient with floating bokeh orbs and subtle sparkles. Name text in elegant script at the bottom center. NO BORDERS, NO FRAMES — everything blends seamlessly like a cinematic movie poster.',
 '✨', 1, 0, 0, 0, 9),

('Royal Traditional Portrait', 'Grand traditional portrait collage with rich jewel tones and ornate styling', 'Portrait', 4,
 '[""Center - regal portrait looking at camera, confident poised expression, head and shoulders, sharp detail"",""Top-left - gentle three-quarter angle looking to the right, graceful expression, dreamy soft blend"",""Top-right - close-up face, serene peaceful expression, eyes slightly lowered, golden soft glow"",""Bottom background - wider medium shot, different angle, soft fade overlay behind main portrait""]',
 0, 'rich maroon, deep gold, royal purple, warm cream, burgundy', 'regal, traditional, majestic, warm, grand',
 'ornate gold filigree borders, mandala patterns, jewel-like sparkles, subtle paisley motifs, warm light rays',
 '',
 'Cinematic multi-exposure style. Center foreground: large sharp hero portrait. Other images blended as soft translucent overlays in the background using double-exposure fade technique. Rich warm gradient background from maroon to dark gold. Subtle ornate gold filigree pattern along edges. Name in elegant traditional calligraphy at bottom. NO separate frames — everything blends cinematically.',
 '👑', 1, 0, 0, 0, 10),

('B&W Strip Panels', 'Dramatic B&W background strips with colorful hero in front and stylish name text', 'Portrait', 5,
 '[""Center foreground - large full-body or waist-up hero portrait, colorful, sharp focus, looking at camera confidently"",""Background strip 1 (leftmost) - black and white, profile view facing right, soft desaturated, vertical strip panel"",""Background strip 2 - black and white, three-quarter angle looking up, hand near hair, desaturated vertical strip"",""Background strip 3 - black and white, gentle smile looking down, close-up face, desaturated vertical strip"",""Background strip 4 (rightmost) - black and white, looking over shoulder, soft expression, desaturated vertical strip""]',
 0, 'colorful hero against monochrome black and white background, white base', 'dramatic, editorial, stylish, modern, high-fashion',
 'vertical strip panel dividers, subtle gradient fade between strips, clean modern lines',
 '',
 'EDITORIAL STRIP PANEL style: Background has 4-5 VERTICAL STRIP PANELS side by side spanning the full height, each showing a different B&W (black and white desaturated) photo of the person in different poses. The strips should have subtle gaps or fade between them. The HERO portrait is in the CENTER FOREGROUND, FULLY COLORFUL and vibrant, overlapping the B&W strips, taking about 50-60 percent width. The hero is sharp and prominent while B&W strips are slightly softer. At the BOTTOM CENTER: the person name in LARGE STYLISH SCRIPT/CALLIGRAPHY FONT with appropriate color. Below the name: occasion text in smaller uppercase letters. White or very light background base visible between strips. Overall look: high-fashion editorial poster.',
 '🖤', 1, 1, 1, 1, 11),

('Neon Glow Portrait', 'Vibrant neon-lit portrait collage with electric colors and urban vibe', 'Portrait', 4,
 '[""Center - hero portrait facing camera, confident expression, neon-lit, head and shoulders"",""Left background - side profile with neon pink glow rim lighting, dreamy soft blend"",""Right background - three-quarter angle with neon blue glow, soft overlay blend"",""Top background - close-up face looking up, neon purple tones, faded dreamy overlay""]',
 0, 'electric neon pink, neon blue, neon purple, deep black, hot magenta', 'futuristic, urban, vibrant, edgy, electric',
 'neon light streaks, glowing lines, light trails, subtle smoke, electric sparks, lens flare',
 '',
 'Cinematic multi-exposure with NEON GLOW aesthetic. Dark/black background. Hero portrait center foreground with vivid neon rim lighting (pink on one side, blue on other). Other photos blended as translucent neon-tinted overlays. Neon light streaks and glowing lines between images. Name at bottom in neon-style glowing font. NO frames — seamless neon-lit blend.',
 '💜', 1, 1, 0, 1, 12),

('Floral Elegance', 'Beautiful floral-framed portrait collage with watercolor flowers', 'Portrait', 4,
 '[""Center - elegant portrait with warm smile, head and shoulders, sharp focus"",""Top-left - gentle three-quarter angle, soft floral glow, dreamy close-up blend"",""Top-right - looking to the side, peaceful expression, watercolor overlay blend"",""Bottom - wider medium shot, different angle, soft floral fade behind hero""]',
 0, 'soft blush pink, sage green, lavender, cream, rose gold', 'elegant, feminine, romantic, delicate, graceful',
 'watercolor roses, peonies, eucalyptus leaves, delicate baby breath flowers, gold leaf accents, soft petals',
 '',
 'Elegant FLORAL FRAME style. Hero portrait in center with a soft oval or circular floral frame made of watercolor roses and greenery. Other photos softly blended into background with floral overlay. Lush watercolor flower arrangements framing the entire composition — top corners and bottom. Name at bottom in elegant rose-gold script font. Background: soft cream to blush gradient. Delicate and feminine aesthetic.',
 '🌸', 1, 1, 1, 1, 13),

('Queen Portrait', 'Dramatic B&W background portrait with vibrant colorful foreground hero and elegant Queen text overlay', 'Portrait', 4,
 '[""Center foreground - vibrant full-color portrait, warm smile looking at camera, head to waist, sharp focus, slightly angled pose with hand near chin"",""Background large - same person in dramatic black and white, close-up face filling the background, soft ethereal fade, looking slightly to the side with gentle smile"",""Bottom-left accent - small full-color photo, different pose, playful confident expression, waist-up"",""Bottom-right accent - small full-color photo, looking over shoulder, elegant side glance, upper body""]',
 0, 'monochrome grayscale background, vibrant full-color foreground, soft pink and red accent hearts, black and white contrast', 'dramatic, elegant, empowering, bold, glamorous',
 'decorative hearts, subtle floral swirls, soft ink splash effects, elegant script flourishes, scattered small hearts',
 'Queen',
 'DRAMATIC B&W HERO OVERLAY style — NOT a framed grid. The BACKGROUND is a LARGE BLACK AND WHITE close-up portrait of the person face filling about 70 percent of the canvas with soft ethereal fade at edges. The person IDENTITY MUST remain EXACTLY the same — do NOT change the face, skin tone, or any facial features. In the CENTER-RIGHT FOREGROUND: a smaller FULLY COLORFUL vibrant portrait of the SAME person (waist-up, slightly angled, hand near chin) overlapping the B&W background, taking about 40 percent width. Two small colorful accent photos at bottom corners. At the BOTTOM CENTER: the word Queen in LARGE ELEGANT FLOWING SCRIPT font with decorative flourishes. Small scattered hearts in soft pink and red around the composition. Background base: soft white-to-light-gray gradient behind the B&W portrait with subtle ink splash or smoke effects at edges. Face must remain 100 percent photorealistic and untouched — no beautification, no skin smoothing, no style effects on face. Overall look: dramatic glamour portrait poster.',
 '👑', 1, 0, 0, 0, 14)
");
            app.Logger.LogInformation("Seeded {Count} CollageTemplates.", 14);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "CollageTemplates seeding failed (non-fatal)");
    }

    // Add StyleGroupId column to StylePresets if missing
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('StylePresets') AND name = 'StyleGroupId')
                ALTER TABLE StylePresets ADD StyleGroupId INT NULL");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "StylePresets StyleGroupId column migration failed (non-fatal)");
    }

    // Migrate preset thumbnails from generated/ to presets/ folder
    try
    {
        var webRoot = app.Environment.WebRootPath;
        var presetsDir = Path.Combine(webRoot, "presets");
        Directory.CreateDirectory(presetsDir);

        var thumbRows = await db.Database.SqlQueryRaw<ThumbnailRow>(
            "SELECT Id, ThumbnailPath FROM StylePresets WHERE ThumbnailPath IS NOT NULL AND ThumbnailPath NOT LIKE 'presets/%'")
            .ToListAsync();

        foreach (var row in thumbRows)
        {
            var srcFile = Path.Combine(webRoot, row.ThumbnailPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            if (!File.Exists(srcFile)) continue;

            var fileName = Path.GetFileName(srcFile);
            var destFile = Path.Combine(presetsDir, fileName);

            if (!File.Exists(destFile))
                File.Copy(srcFile, destFile);

            var newPath = $"presets/{fileName}";
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE StylePresets SET ThumbnailPath = {0} WHERE Id = {1}", newPath, row.Id);

            try { File.Delete(srcFile); } catch { /* best effort */ }
        }

        if (thumbRows.Count > 0)
            app.Logger.LogInformation("Migrated {Count} preset thumbnails to presets/ folder", thumbRows.Count);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Preset thumbnail migration failed (non-fatal)");
    }

    if (!seedAlreadyDone)
    {
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

    // ── Seed Classic Oil Painting & Watercolor Splash Portrait (PhotoArts) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = N'Classic Oil Painting')
            AND NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = N'Classic Oil Painting')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Classic Oil Painting', N'Warm classic oil portrait with textured canvas and vintage frame',
                 N'Transform into a classic oil painting portrait on textured canvas. Preserve the subject''s EXACT facial features, skin tone, and expression. Traditional oil painting with warm earthy palette: burnt sienna, raw umber, yellow ochre, and golden amber tones. Thick visible impasto brushstrokes on clothing and background, with finer blended brushwork on the face preserving realistic detail. Canvas texture visible throughout. Skin rendered with warm golden undertones and subtle paint texture while retaining natural features. Hair painted with flowing directional brushstrokes. Warm abstract mottled background in golden-brown, olive-ochre and amber tones like a classical Renaissance portrait backdrop. Soft tonal gradations with visible canvas weave texture. Add a subtle dark rounded-corner vignette border around the entire image, simulating a vintage gallery frame effect with dark edges fading inward. Warm dramatic side lighting with golden highlights and rich brown shadows. Museum-quality fine art portrait. No text, no watermarks, no signatures.',
                 N'PhotoArts', N'🎨', '#8B6914', 1, 220);

            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = N'Watercolor Splash Portrait')
            AND NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = N'Watercolor Splash Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Watercolor Splash Portrait', N'Vivid watercolor ink splash portrait on clean white background',
                 N'Transform into a stunning watercolor splash portrait. Preserve the subject''s EXACT facial features, skin tone, and expression. The subject should be rendered with sharp photorealistic detail on the face and upper body, wearing their original clothing and colors. BACKGROUND: Clean pure white background with NO solid backdrop. Instead, surround the subject with expressive watercolor and ink splash effects. Vibrant splashes of purple, indigo, cobalt blue, violet, and magenta watercolor pigment radiating outward from behind the subject. The splashes should look like wet ink dropped on paper, with organic flowing edges, paint drips, color bleeding, and soft diffusion. Mix thick saturated paint splatters with thin translucent washes. Some splashes should overlap the subject''s edges slightly, blending the person into the art. The watercolor effect fades to clean white at the outer edges, giving a floating, frameless look. The subject''s hair should have soft painterly strands blending into the watercolor splashes. Clothing rendered with slightly more painterly texture while keeping recognizable details and colors. Overall mood: vibrant, artistic, elegant. Style: modern mixed-media portrait combining photorealism with traditional watercolor art. No text, no watermarks, no signatures, no logos.',
                 N'PhotoArts', N'💜', '#7B1FA2', 1, 221);
        ");
        Console.WriteLine(">>> Classic Oil Painting & Watercolor Splash Portrait seeded!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($">>> Classic Oil Painting / Watercolor Splash seeding FAILED: {ex.Message}");
        app.Logger.LogWarning(ex, "Classic Oil Painting / Watercolor Splash seeding failed (non-fatal)");
    }

    // ── Memorial Frame StylePresets ──
    try
    {
        // First, delete old-format memorial presets so they get re-inserted with updated prompts
        await db.Database.ExecuteSqlRawAsync(@"
            DELETE FROM StylePresets WHERE Category = N'Memorial'");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = 'Floral Garland Memorial')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Floral Garland Memorial', N'Ornate floral garland memorial frame', N'Create a solemn and beautiful memorial photo frame. The reference photo person must be placed in the CENTER of the image inside a circular frame with a rich blue gradient background behind them (background removed, person only). The circular frame should have a golden border ring. Around the circular portrait: lush garlands of white jasmine, orange marigold, and red roses draped from the top forming an arch. Two ornate golden pillars on left and right. A glowing oil lamp (diya) at bottom center. Soft divine golden rays from behind the portrait. IMPORTANT: Leave the bottom 20% of the image COMPLETELY EMPTY with a plain dark background — NO text, NO letters, NO words, NO names, NO dates anywhere in the image. Background: deep maroon-to-black gradient. Mood: respectful, sacred, dignified. {{NAME}} {{DOB}} {{DOD}}', N'Memorial', N'🪷', '#8B0000', 1, 170);

            IF NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = 'Temple Pillar Memorial')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Temple Pillar Memorial', N'Traditional temple-style memorial frame', N'Create a grand South Indian temple-style memorial photo frame. The reference photo person must be placed in the CENTER inside a circular frame with a deep royal blue gradient background behind them (background removed, person only). Golden ornate border ring around the circle. Frame design: two intricately carved stone temple pillars (gopuram style) on each side. An ornate arch (thoranam) at top with temple tower silhouette. Brass oil lamps on both sides at bottom with warm flames. Marigold and jasmine garlands across the top arch. IMPORTANT: Leave the bottom 20% of the image COMPLETELY EMPTY with a plain dark background — NO text, NO letters, NO words, NO names, NO dates anywhere in the image. Background: deep sacred saffron-to-dark-maroon gradient. Divine golden light aura. Mood: sacred, reverential. {{NAME}} {{DOB}} {{DOD}}', N'Memorial', N'🛕', '#B8860B', 1, 171);

            IF NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = 'Lotus Divine Memorial')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Lotus Divine Memorial', N'Serene lotus and divine light memorial frame', N'Create a serene divine memorial photo frame with lotus theme. The reference photo person must be placed in the CENTER inside a circular frame with a soft purple-blue gradient background behind them (background removed, person only). Soft golden oval border ring. Frame design: beautiful pink and white lotus flowers around the portrait — large blooming lotuses at bottom, smaller buds along sides. Gentle water reflection at base. Soft divine white-golden light rays radiating from behind the portrait like a halo. Floating lotus petals. IMPORTANT: Leave the bottom 20% of the image COMPLETELY EMPTY with a plain dark background — NO text, NO letters, NO words, NO names, NO dates anywhere in the image. Background: gradient from heavenly white-gold at top to peaceful blue-purple at bottom. Mood: peaceful, divine, eternal rest. {{NAME}} {{DOB}} {{DOD}}', N'Memorial', N'🪷', '#4B0082', 1, 172);

            IF NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = 'Marigold Tribute Memorial')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Marigold Tribute Memorial', N'Traditional marigold garland tribute frame', N'Create a traditional Indian memorial tribute photo frame with marigold theme. The reference photo person must be placed in the CENTER inside a circular frame with a deep blue gradient background behind them (background removed, person only). Golden decorative border ring. Frame design: abundant bright orange and yellow marigold garlands draped thickly around — heavy garlands across top, flowing down sides. Red and white rose accents. Traditional Indian memorial flower arrangement style. Brass incense holders with smoke wisps on either side. IMPORTANT: Leave the bottom 20% of the image COMPLETELY EMPTY with a plain dark background — NO text, NO letters, NO words, NO names, NO dates anywhere in the image. Background: rich dark green-to-black gradient. Fresh flower texture and warm lighting. Mood: heartfelt tribute. {{NAME}} {{DOB}} {{DOD}}', N'Memorial', N'🌼', '#FF8C00', 1, 173);

            IF NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = 'Heavenly Light Memorial')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Heavenly Light Memorial', N'Spiritual heavenly light memorial frame', N'Create a spiritual heavenly memorial photo frame. The reference photo person must be placed in the CENTER inside a circular frame with a celestial blue-gold gradient background behind them (background removed, person only). Soft glowing golden border ring. Frame design: ethereal golden-white divine light beams radiating from behind the portrait. Soft golden and white clouds surrounding. Delicate white flowers (lilies and jasmine) at bottom. Golden stars and divine sparkles in the light rays. IMPORTANT: Leave the bottom 20% of the image COMPLETELY EMPTY with a plain dark background — NO text, NO letters, NO words, NO names, NO dates anywhere in the image. Background: gradient from golden-white center to deep royal blue at edges. Mood: celestial, peaceful, divine remembrance. {{NAME}} {{DOB}} {{DOD}}', N'Memorial', N'✨', '#FFD700', 1, 174);

            IF NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = 'Royal Memorial Frame')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Royal Memorial Frame', N'Regal ornate memorial with velvet and gold', N'Create a regal distinguished memorial photo frame. The reference photo person must be placed in the CENTER inside a circular frame with a rich navy-blue gradient background behind them (background removed, person only). Thick ornate golden baroque border ring with scroll and leaf carvings. Frame design: deep royal red velvet curtains draped on both sides, tied with golden tassels. Ornate golden crown motif at top center. Rich golden filigree patterns on borders. Tall golden candelabras with candles on either side. Red and white roses at the base. IMPORTANT: Leave the bottom 20% of the image COMPLETELY EMPTY with a plain dark background — NO text, NO letters, NO words, NO names, NO dates anywhere in the image. Background: deep royal maroon-burgundy with damask pattern. Warm candlelight. Mood: distinguished, royal tribute. {{NAME}} {{DOB}} {{DOD}}', N'Memorial', N'👑', '#800020', 1, 175);
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Memorial StylePresets seeding failed (non-fatal)");
    }

    // ── Seed Vibrant Digital Portrait (PhotoArts) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Vibrant Digital Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Vibrant Digital Portrait', N'Smooth vibrant digital painting with colorful gradient backdrop',
                 N'Transform into a vibrant digital painting portrait. CRITICAL RULES: 1) Preserve the subject''s EXACT facial features — same face shape, eyes, nose, lips, jawline, skin tone, and expression. The person MUST be immediately recognizable. Do NOT alter, beautify, or change any facial features. 2) This must be GENDER NEUTRAL — work equally well for men, women, children, elderly, any age and any ethnicity. 3) CHILD SAFE — absolutely no suggestive, revealing, or inappropriate content. Keep all clothing modest and as-is from the source. 4) HUMAN ANATOMY — maintain correct proportions. No extra fingers, no distorted limbs, no unnatural body proportions. Hands must have exactly 5 fingers each. STYLE: Smooth polished digital painting with soft blended brushwork. Ultra-vibrant saturated colors — intensify all original colors of clothing, jewelry, and accessories. Skin rendered with smooth airbrushed finish but retaining natural texture and pores subtly. Eyes sharp and detailed with natural catchlight reflections. Hair rendered with soft flowing strokes showing individual strand groups. BACKGROUND: Soft abstract gradient blending 2-3 warm complementary colors (greens, purples, golds, pinks, teals — pick colors that complement the subject''s outfit). Smooth painterly color washes with soft bokeh-like light spots. No text, no watermarks, no signatures, no logos anywhere in the image. Professional digital art portrait quality.',
                 N'PhotoArts', N'🎨', '#7C4DFF', 1, 165);

            UPDATE StylePresets SET
                PromptTemplate = N'Transform into a vibrant digital painting portrait. CRITICAL RULES: 1) Preserve the subject''s EXACT facial features — same face shape, eyes, nose, lips, jawline, skin tone, and expression. The person MUST be immediately recognizable. Do NOT alter, beautify, or change any facial features. 2) This must be GENDER NEUTRAL — work equally well for men, women, children, elderly, any age and any ethnicity. 3) CHILD SAFE — absolutely no suggestive, revealing, or inappropriate content. Keep all clothing modest and as-is from the source. 4) HUMAN ANATOMY — maintain correct proportions. No extra fingers, no distorted limbs, no unnatural body proportions. Hands must have exactly 5 fingers each. STYLE: Smooth polished digital painting with soft blended brushwork. Ultra-vibrant saturated colors — intensify all original colors of clothing, jewelry, and accessories. Skin rendered with smooth airbrushed finish but retaining natural texture and pores subtly. Eyes sharp and detailed with natural catchlight reflections. Hair rendered with soft flowing strokes showing individual strand groups. BACKGROUND: Soft abstract gradient blending 2-3 warm complementary colors (greens, purples, golds, pinks, teals — pick colors that complement the subject''s outfit). Smooth painterly color washes with soft bokeh-like light spots. No text, no watermarks, no signatures, no logos anywhere in the image. Professional digital art portrait quality.'
            WHERE Name = 'Vibrant Digital Portrait';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Vibrant Digital Portrait StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Storybook Enchanted Forest (PhotoArts) ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = N'Storybook Enchanted Forest')
            AND NOT EXISTS (SELECT 1 FROM DeletedStyleSeeds WHERE Name = N'Storybook Enchanted Forest')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Storybook Enchanted Forest', N'Whimsical illustrated forest adventure - kids safe, gender neutral',
                 N'Transform the subject into a whimsical storybook illustration set in an enchanted forest. CRITICAL IDENTITY RULES: 1) The subject''s face must remain 100% PHOTOREALISTIC and UNCHANGED - preserve exact facial features, face shape, eyes, nose, lips, jawline, expression, skin tone, undertone, and texture. Do NOT beautify, stylize, cartoonify, smooth, or alter the face in ANY way. The face must look like the real person, not illustrated. 2) FACE BIOMETRICS: Maintain exact interpupillary distance, nose bridge width, lip thickness ratio, ear position, and facial proportions pixel-for-pixel from the source. 3) AGE PRESERVATION: The subject must appear the EXACT same age as in the source photo - do NOT age up or age down. A child must remain a child of the same age, an adult must remain the same age adult. 4) GENDER NEUTRAL: Do not add or remove any gender-specific features. No added makeup, no beard modification, no gender-stereotyped accessories. Work identically for any gender. 5) KIDS SAFE: Absolutely no scary, violent, suggestive, or inappropriate elements. Keep all content wholesome and family-friendly. Clothing must remain modest. 6) HUMAN ANATOMY: Maintain natural proportions - correct number of fingers (exactly 5 per hand), no distorted limbs, no stretched or compressed body parts. STYLE: The BODY and CLOTHING should be rendered in a soft 3D animated storybook illustration style (similar to modern animated films) - slightly stylized proportions for the body while keeping the real face seamlessly composited. Clothing can be gently stylized into a charming adventure outfit (jacket, shirt, belt, trousers) in warm inviting colors, but must respect the original clothing silhouette and color palette. BACKGROUND: A lush enchanted forest scene with tall rounded stylized trees in rich greens, dappled golden sunlight filtering through the canopy, soft volumetric god rays, scattered wildflowers (pink tulips, blue hydrangeas) in the foreground, lush green foliage and leaves, gentle floating light particles. The forest should feel magical, warm, and inviting - a safe fairytale woodland. Soft depth-of-field blur on distant trees. Color palette: rich emerald greens, warm golden sunlight, soft pink and blue floral accents. COMPOSITION: Portrait orientation (12x18 ratio), subject centered, full body or three-quarter body shot, standing naturally in the forest clearing. Warm natural lighting consistent between face and scene. The photorealistic face must blend seamlessly with the illustrated body and background through matched lighting and color grading. No text, no watermarks, no signatures, no logos, no names anywhere in the image.',
                 N'PhotoArts', N'🌲', '#2E7D32', 1, 222);

            UPDATE StylePresets SET
                PromptTemplate = N'Transform the subject into a whimsical storybook illustration set in an enchanted forest. CRITICAL IDENTITY RULES: 1) The subject''s face must remain 100% PHOTOREALISTIC and UNCHANGED - preserve exact facial features, face shape, eyes, nose, lips, jawline, expression, skin tone, undertone, and texture. Do NOT beautify, stylize, cartoonify, smooth, or alter the face in ANY way. The face must look like the real person, not illustrated. 2) FACE BIOMETRICS: Maintain exact interpupillary distance, nose bridge width, lip thickness ratio, ear position, and facial proportions pixel-for-pixel from the source. 3) AGE PRESERVATION: The subject must appear the EXACT same age as in the source photo - do NOT age up or age down. A child must remain a child of the same age, an adult must remain the same age adult. 4) GENDER NEUTRAL: Do not add or remove any gender-specific features. No added makeup, no beard modification, no gender-stereotyped accessories. Work identically for any gender. 5) KIDS SAFE: Absolutely no scary, violent, suggestive, or inappropriate elements. Keep all content wholesome and family-friendly. Clothing must remain modest. 6) HUMAN ANATOMY: Maintain natural proportions - correct number of fingers (exactly 5 per hand), no distorted limbs, no stretched or compressed body parts. STYLE: The BODY and CLOTHING should be rendered in a soft 3D animated storybook illustration style (similar to modern animated films) - slightly stylized proportions for the body while keeping the real face seamlessly composited. Clothing can be gently stylized into a charming adventure outfit (jacket, shirt, belt, trousers) in warm inviting colors, but must respect the original clothing silhouette and color palette. BACKGROUND: A lush enchanted forest scene with tall rounded stylized trees in rich greens, dappled golden sunlight filtering through the canopy, soft volumetric god rays, scattered wildflowers (pink tulips, blue hydrangeas) in the foreground, lush green foliage and leaves, gentle floating light particles. The forest should feel magical, warm, and inviting - a safe fairytale woodland. Soft depth-of-field blur on distant trees. Color palette: rich emerald greens, warm golden sunlight, soft pink and blue floral accents. COMPOSITION: Portrait orientation (12x18 ratio), subject centered, full body or three-quarter body shot, standing naturally in the forest clearing. Warm natural lighting consistent between face and scene. The photorealistic face must blend seamlessly with the illustrated body and background through matched lighting and color grading. No text, no watermarks, no signatures, no logos, no names anywhere in the image.'
            WHERE Name = 'Storybook Enchanted Forest';
        ");
        Console.WriteLine(">>> Storybook Enchanted Forest (PhotoArts) seeded!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($">>> Storybook Enchanted Forest seeding FAILED: {ex.Message}");
        app.Logger.LogWarning(ex, "Storybook Enchanted Forest seeding failed (non-fatal)");
    }

    // ── Seed Trendy Photo Collage StylePresets ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Silhouette Double Exposure')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Silhouette Double Exposure', N'Silhouette double exposure with misty forest — works for singles and couples',
                 N'Create a stunning double-exposure silhouette photo art. CRITICAL: Preserve EVERY person''s exact facial features and identity. First, DETECT how many people are in the uploaded source image. IF ONE PERSON: In the FOREGROUND (bottom half): the person shown from waist up in their original clothing and colors, natural confident pose. Sharp, vivid, well-lit with natural warm tones. In the BACKGROUND (filling the full canvas): a large dark silhouette outline of the SAME single person in side profile. INSIDE the silhouette: fill with a dreamy misty pine forest landscape — tall evergreen trees fading into fog, layered mountain ridges, soft ethereal morning mist. IF TWO OR MORE PEOPLE (couple): In the FOREGROUND (bottom half): the couple shown from waist up in their original clothing and colors, in an intimate pose, looking at each other with foreheads nearly touching. In the BACKGROUND: a large dark silhouette outline of the same couple in side profile facing each other, heads nearly touching. INSIDE the silhouette: fill with dreamy misty pine forest landscape. FOR BOTH CASES: Above the silhouette: a flock of birds flying in V-formation against a pale grey-white sky. The silhouette edges blend softly into the white/light grey background using a smooth gradient dissolve effect. Color palette: silhouette interior is moody blue-grey forest tones, foreground subject keeps original vivid colors, background outside silhouette is clean white to light grey. Overall mood: cinematic, dreamy, elegant. Style: modern photo album double-exposure art. No text, no names, no dates, no watermarks anywhere in the image.',
                 N'Trendy Photo Collage', N'🖤', '#37474F', 1, 200);

            UPDATE StylePresets SET
                Description = N'Silhouette double exposure with misty forest — works for singles and couples',
                PromptTemplate = N'Create a stunning double-exposure silhouette photo art. CRITICAL: Preserve EVERY person''s exact facial features and identity. First, DETECT how many people are in the uploaded source image. IF ONE PERSON: In the FOREGROUND (bottom half): the person shown from waist up in their original clothing and colors, natural confident pose. Sharp, vivid, well-lit with natural warm tones. In the BACKGROUND (filling the full canvas): a large dark silhouette outline of the SAME single person in side profile. INSIDE the silhouette: fill with a dreamy misty pine forest landscape — tall evergreen trees fading into fog, layered mountain ridges, soft ethereal morning mist. IF TWO OR MORE PEOPLE (couple): In the FOREGROUND (bottom half): the couple shown from waist up in their original clothing and colors, in an intimate pose, looking at each other with foreheads nearly touching. In the BACKGROUND: a large dark silhouette outline of the same couple in side profile facing each other, heads nearly touching. INSIDE the silhouette: fill with dreamy misty pine forest landscape. FOR BOTH CASES: Above the silhouette: a flock of birds flying in V-formation against a pale grey-white sky. The silhouette edges blend softly into the white/light grey background using a smooth gradient dissolve effect. Color palette: silhouette interior is moody blue-grey forest tones, foreground subject keeps original vivid colors, background outside silhouette is clean white to light grey. Overall mood: cinematic, dreamy, elegant. Style: modern photo album double-exposure art. No text, no names, no dates, no watermarks anywhere in the image.'
            WHERE Name = 'Silhouette Double Exposure';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Trendy Photo Collage StylePresets seeding failed (non-fatal)");
    }

    // ── Seed Birthday Ghost Portrait Collage ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Birthday Ghost Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Birthday Ghost Portrait', N'Full-body photo with ghost duplicate, watercolor floral accents, and stylish Happy Birthday text',
                 N'Create a modern birthday greeting card photo composition. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — no modifications to the face whatsoever. LAYOUT: Light off-white / soft grey background. LEFT SIDE: Elegant watercolor floral decoration — large translucent purple and lavender leaves with flowing organic shapes, soft ink-wash texture, semi-transparent layering. Interspersed with delicate golden metallic botanical accents — thin golden branch stems, small golden leaf buds, and scattered fine gold speckle/dust particles for a luxurious touch. These floral elements should sweep upward from the bottom-left corner, curving gracefully along the left edge and partially into the upper-left area. The florals frame the composition but do NOT cover the person''s face or body. PERSON: The person shown from waist-up or full body standing naturally in their ORIGINAL clothing and colors, positioned right-of-center. Sharp, vivid, well-lit with natural tones. The person is the clear focal point of the composition. GHOST OVERLAY: A LARGE faded ghosted duplicate of the SAME person in the background, shown from chest-up, slightly enlarged. This ghost image must be DESATURATED (grayscale), very LOW OPACITY (around 10-20% transparency), blending softly into the background behind and to the right of the main figure. BOTTOM AREA (lower 20% of canvas): ONLY the text Happy Birthday in a stylish, modern display font — bold uppercase with a prominent dark drop shadow behind the letters for depth and contrast. No other text whatsoever — no names, no subtitles, no quotes, no dates, no messages. STYLE: Premium greeting card aesthetic combining photorealistic portrait with artistic watercolor and gold foil decorative elements. The person remains photorealistic while the decorative frame is artistic/illustrative. Overall mood: celebratory, elegant, luxurious. STRICT RULES: Face must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. The ONLY text in the entire image must be Happy Birthday — nothing else. No watermarks.',
                 N'Trendy Photo Collage', N'🎂', '#9C27B0', 1, 201);

            UPDATE StylePresets SET
                Description = N'Full-body photo with ghost duplicate, watercolor floral accents, and stylish Happy Birthday text',
                PromptTemplate = N'Create a modern birthday greeting card photo composition. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — no modifications to the face whatsoever. LAYOUT: Light off-white / soft grey background. LEFT SIDE: Elegant watercolor floral decoration — large translucent purple and lavender leaves with flowing organic shapes, soft ink-wash texture, semi-transparent layering. Interspersed with delicate golden metallic botanical accents — thin golden branch stems, small golden leaf buds, and scattered fine gold speckle/dust particles for a luxurious touch. These floral elements should sweep upward from the bottom-left corner, curving gracefully along the left edge and partially into the upper-left area. The florals frame the composition but do NOT cover the person''s face or body. PERSON: The person shown from waist-up or full body standing naturally in their ORIGINAL clothing and colors, positioned right-of-center. Sharp, vivid, well-lit with natural tones. The person is the clear focal point of the composition. GHOST OVERLAY: A LARGE faded ghosted duplicate of the SAME person in the background, shown from chest-up, slightly enlarged. This ghost image must be DESATURATED (grayscale), very LOW OPACITY (around 10-20% transparency), blending softly into the background behind and to the right of the main figure. BOTTOM AREA (lower 20% of canvas): ONLY the text Happy Birthday in a stylish, modern display font — bold uppercase with a prominent dark drop shadow behind the letters for depth and contrast. No other text whatsoever — no names, no subtitles, no quotes, no dates, no messages. STYLE: Premium greeting card aesthetic combining photorealistic portrait with artistic watercolor and gold foil decorative elements. The person remains photorealistic while the decorative frame is artistic/illustrative. Overall mood: celebratory, elegant, luxurious. STRICT RULES: Face must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. The ONLY text in the entire image must be Happy Birthday — nothing else. No watermarks.'
            WHERE Name = 'Birthday Ghost Portrait';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Birthday Ghost Portrait StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Romantic Love Overlay Collage ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Romantic Love Overlay')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Romantic Love Overlay', N'Couple portrait with dreamy close-up overlay and golden Love script — pre-wedding/anniversary style',
                 N'Create a romantic couple photo collage composition. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT — TWO LAYERS: BOTTOM HALF (foreground): The couple shown FULL BODY seated or standing together in a natural romantic pose, in their ORIGINAL clothing and colors. Place them in a lush, warm-toned outdoor garden or nature setting with soft greenery, warm golden-hour lighting, and gentle bokeh. Sharp, vivid, well-lit. TOP HALF (overlay): A LARGE close-up of the SAME couple''s faces — forehead to forehead or cheek to cheek, smiling intimately, filling the upper portion of the canvas. This close-up layer should have a SOFT DREAMY quality with warm color grading, gentle glow, and blend seamlessly into the bottom scene using a smooth gradient dissolve (no hard edges). The close-up should be slightly desaturated with a warm cinematic tone. DECORATIVE ELEMENTS: A single elegant golden cursive script word Love arching gracefully across the upper-middle area of the canvas, rendered as a thin metallic gold hand-lettered stroke with subtle glow. Below the close-up overlay, add a romantic quote in elegant italic white script font (e.g., a timeless love quote). OVERALL MOOD: Warm, cinematic, golden-hour romance. Color palette: deep warm greens, golden amber, soft browns, subtle warm highlights. The composition should feel like a premium pre-wedding or anniversary photo album page. STYLE: Photorealistic editorial — not illustrated, not cartoonish. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone, undertone, and natural proportions. No watermarks. No photographer branding. No phone numbers. No names — only the word Love as decorative script.',
                 N'Trendy Photo Collage', N'💕', '#D4AF37', 1, 202);

            UPDATE StylePresets SET
                Description = N'Couple portrait with dreamy close-up overlay and golden Love script — pre-wedding/anniversary style',
                PromptTemplate = N'Create a romantic couple photo collage composition. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT — TWO LAYERS: BOTTOM HALF (foreground): The couple shown FULL BODY seated or standing together in a natural romantic pose, in their ORIGINAL clothing and colors. Place them in a lush, warm-toned outdoor garden or nature setting with soft greenery, warm golden-hour lighting, and gentle bokeh. Sharp, vivid, well-lit. TOP HALF (overlay): A LARGE close-up of the SAME couple''s faces — forehead to forehead or cheek to cheek, smiling intimately, filling the upper portion of the canvas. This close-up layer should have a SOFT DREAMY quality with warm color grading, gentle glow, and blend seamlessly into the bottom scene using a smooth gradient dissolve (no hard edges). The close-up should be slightly desaturated with a warm cinematic tone. DECORATIVE ELEMENTS: A single elegant golden cursive script word Love arching gracefully across the upper-middle area of the canvas, rendered as a thin metallic gold hand-lettered stroke with subtle glow. Below the close-up overlay, add a romantic quote in elegant italic white script font (e.g., a timeless love quote). OVERALL MOOD: Warm, cinematic, golden-hour romance. Color palette: deep warm greens, golden amber, soft browns, subtle warm highlights. The composition should feel like a premium pre-wedding or anniversary photo album page. STYLE: Photorealistic editorial — not illustrated, not cartoonish. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone, undertone, and natural proportions. No watermarks. No photographer branding. No phone numbers. No names — only the word Love as decorative script.'
            WHERE Name = 'Romantic Love Overlay';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Romantic Love Overlay StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Romantic Palace & Doves ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Romantic Palace & Doves')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Romantic Palace & Doves', N'Couple in grand palace hall with chandelier and flying white doves — cinematic romance poster',
                 N'Create a cinematic romantic photo composition. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: Place the couple in a grand luxurious palace or mansion interior — ornate marble columns, a sparkling crystal chandelier hanging above, warm golden ambient lighting with soft candlelight glow from decorative pillars on both sides. The couple should be positioned center-frame in a dramatic romantic pose — the man standing tall, the woman leaning into him or held close, both in their ORIGINAL clothing and colors. DOVES: Multiple white doves in flight around the couple — some taking off from the floor, some mid-air with wings spread, creating a sense of ethereal beauty and celebration. The doves should be naturally positioned, not crowding the couple. ATMOSPHERE: Warm golden-hour lighting throughout, soft volumetric light rays, slight haze for cinematic depth. Rich warm tones — golds, ambers, deep browns, cream whites. The floor should be polished marble reflecting some of the light. STYLE: Cinematic movie poster aesthetic — grand, dramatic, romantic. Photorealistic — not illustrated or cartoonish. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no titles, no watermarks, no branding anywhere in the image.',
                 N'Trendy Photo Collage', N'🕊️', '#D4A017', 1, 203);

            UPDATE StylePresets SET
                Description = N'Couple in grand palace hall with chandelier and flying white doves — cinematic romance poster',
                PromptTemplate = N'Create a cinematic romantic photo composition. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: Place the couple in a grand luxurious palace or mansion interior — ornate marble columns, a sparkling crystal chandelier hanging above, warm golden ambient lighting with soft candlelight glow from decorative pillars on both sides. The couple should be positioned center-frame in a dramatic romantic pose — the man standing tall, the woman leaning into him or held close, both in their ORIGINAL clothing and colors. DOVES: Multiple white doves in flight around the couple — some taking off from the floor, some mid-air with wings spread, creating a sense of ethereal beauty and celebration. The doves should be naturally positioned, not crowding the couple. ATMOSPHERE: Warm golden-hour lighting throughout, soft volumetric light rays, slight haze for cinematic depth. Rich warm tones — golds, ambers, deep browns, cream whites. The floor should be polished marble reflecting some of the light. STYLE: Cinematic movie poster aesthetic — grand, dramatic, romantic. Photorealistic — not illustrated or cartoonish. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no titles, no watermarks, no branding anywhere in the image.'
            WHERE Name = 'Romantic Palace & Doves';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Romantic Palace & Doves StylePreset seeding failed (non-fatal)");
    }

    // ── Seed B&W Multi-Panel Wedding ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'B&W Multi-Panel Wedding')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'B&W Multi-Panel Wedding', N'Elegant grayscale multi-panel wedding collage with multiple poses in a structured grid',
                 N'Create an elegant black-and-white multi-panel wedding photo collage. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: A structured collage grid on a white/light grey background containing 5-6 panels of varying sizes. TOP SECTION: A large wide panel showing the couple in a close intimate pose — foreheads touching or cheek to cheek, shot from chest up. This is the hero panel and should be the largest. MIDDLE/BOTTOM SECTIONS: 4-5 smaller panels arranged in a clean grid showing different poses of the SAME couple — full body standing together, the couple walking hand-in-hand, a candid laughing moment, a close-up of hands held together, and a romantic dip pose. ALL panels must be in BLACK AND WHITE / GRAYSCALE — elegant monochrome processing with rich tonal range, deep blacks, clean whites, and smooth mid-tones. Each panel should have a thin white border/gap separating it from adjacent panels. STYLE: Premium wedding album page aesthetic — timeless, classic, elegant black and white photography. Photorealistic — not illustrated. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED across every panel. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact identity in every panel. No text, no names, no dates, no watermarks, no branding anywhere in the image.',
                 N'Trendy Photo Collage', N'🖤', '#424242', 1, 204);

            UPDATE StylePresets SET
                Description = N'Elegant grayscale multi-panel wedding collage with multiple poses in a structured grid',
                PromptTemplate = N'Create an elegant black-and-white multi-panel wedding photo collage. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: A structured collage grid on a white/light grey background containing 5-6 panels of varying sizes. TOP SECTION: A large wide panel showing the couple in a close intimate pose — foreheads touching or cheek to cheek, shot from chest up. This is the hero panel and should be the largest. MIDDLE/BOTTOM SECTIONS: 4-5 smaller panels arranged in a clean grid showing different poses of the SAME couple — full body standing together, the couple walking hand-in-hand, a candid laughing moment, a close-up of hands held together, and a romantic dip pose. ALL panels must be in BLACK AND WHITE / GRAYSCALE — elegant monochrome processing with rich tonal range, deep blacks, clean whites, and smooth mid-tones. Each panel should have a thin white border/gap separating it from adjacent panels. STYLE: Premium wedding album page aesthetic — timeless, classic, elegant black and white photography. Photorealistic — not illustrated. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED across every panel. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact identity in every panel. No text, no names, no dates, no watermarks, no branding anywhere in the image.'
            WHERE Name = 'B&W Multi-Panel Wedding';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "B&W Multi-Panel Wedding StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Vertical Strip Save-the-Date ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Vertical Strip Anniversary')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Vertical Strip Anniversary', N'Three vertical photo strips with floral borders on clean white background — anniversary celebration style',
                 N'Create a modern anniversary celebration photo collage. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: Clean white background. THREE VERTICAL PHOTO STRIPS arranged side by side in the center of the canvas, each strip tall and narrow (roughly 25% canvas width each with gaps between). STRIP 1 (left): The couple in a full-body romantic pose — standing together, arms around each other. STRIP 2 (center): A close-up intimate shot — foreheads touching or looking into each other''s eyes, chest-up framing. STRIP 3 (right): A candid joyful moment — the couple laughing, walking, or in a playful pose. Each strip should have softly rounded corners. FLORAL DECORATION: Delicate watercolor floral accents in soft pastels (blush pink, sage green, lavender) framing the strips — small floral clusters at the top and bottom of each strip, with thin vine tendrils connecting them. The florals should be subtle and elegant, not overwhelming. BOTTOM AREA: The text Happy Anniversary in an elegant modern serif or calligraphy font, centered below the strips. STYLE: Clean, airy, modern celebration stationery aesthetic. The photos are photorealistic; the floral elements are artistic watercolor. Light and fresh color palette. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No names, no dates, no branding, no watermarks — only the text Happy Anniversary.',
                 N'Trendy Photo Collage', N'💐', '#E8A0BF', 1, 205);

            DELETE FROM StylePresets WHERE Name = 'Vertical Strip Save-the-Date';

            UPDATE StylePresets SET
                Description = N'Three vertical photo strips with floral borders on clean white background — anniversary celebration style',
                PromptTemplate = N'Create a modern anniversary celebration photo collage. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: Clean white background. THREE VERTICAL PHOTO STRIPS arranged side by side in the center of the canvas, each strip tall and narrow (roughly 25% canvas width each with gaps between). STRIP 1 (left): The couple in a full-body romantic pose — standing together, arms around each other. STRIP 2 (center): A close-up intimate shot — foreheads touching or looking into each other''s eyes, chest-up framing. STRIP 3 (right): A candid joyful moment — the couple laughing, walking, or in a playful pose. Each strip should have softly rounded corners. FLORAL DECORATION: Delicate watercolor floral accents in soft pastels (blush pink, sage green, lavender) framing the strips — small floral clusters at the top and bottom of each strip, with thin vine tendrils connecting them. The florals should be subtle and elegant, not overwhelming. BOTTOM AREA: The text Happy Anniversary in an elegant modern serif or calligraphy font, centered below the strips. STYLE: Clean, airy, modern celebration stationery aesthetic. The photos are photorealistic; the floral elements are artistic watercolor. Light and fresh color palette. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No names, no dates, no branding, no watermarks — only the text Happy Anniversary.'
            WHERE Name = 'Vertical Strip Anniversary';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Vertical Strip Anniversary StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Golden Monochrome Romance ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Golden Monochrome Romance')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Golden Monochrome Romance', N'Warm golden sepia couple portrait with dreamy smoke overlay — cinematic romance art',
                 N'Create a warm golden monochrome romantic photo art. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: The entire composition uses a WARM GOLDEN / AMBER / SEPIA monochrome color palette — all tones should be in the range of deep gold, honey amber, warm brown, and burnt orange. FOREGROUND (bottom half): The couple in a romantic close pose — facing each other, foreheads nearly touching or in a gentle embrace, shot from waist up. They should be rendered in warm golden tones while maintaining sharp detail and photorealistic quality. Their original clothing silhouettes are preserved but color-graded into the golden palette. BACKGROUND (upper half): A dreamy, ethereal smoky/misty golden overlay showing a larger ghosted version of the same couple''s close-up — soft focus, very low opacity (15-25%), blending into golden smoke and haze. The smoke should have organic flowing shapes, swirling gently. OVERALL TONE: Entirely warm golden monochrome — like a premium sepia-toned photograph with rich depth. Soft golden light rays and volumetric golden haze throughout. Deep shadows in dark amber, highlights in bright gold. The mood is deeply romantic, intimate, and cinematic. STYLE: Cinematic fine-art romance photography with editorial color grading. Photorealistic — not illustrated. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact identity. No text, no names, no dates, no watermarks, no branding anywhere in the image.',
                 N'Trendy Photo Collage', N'✨', '#DAA520', 1, 206);

            UPDATE StylePresets SET
                Description = N'Warm golden sepia couple portrait with dreamy smoke overlay — cinematic romance art',
                PromptTemplate = N'Create a warm golden monochrome romantic photo art. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: The entire composition uses a WARM GOLDEN / AMBER / SEPIA monochrome color palette — all tones should be in the range of deep gold, honey amber, warm brown, and burnt orange. FOREGROUND (bottom half): The couple in a romantic close pose — facing each other, foreheads nearly touching or in a gentle embrace, shot from waist up. They should be rendered in warm golden tones while maintaining sharp detail and photorealistic quality. Their original clothing silhouettes are preserved but color-graded into the golden palette. BACKGROUND (upper half): A dreamy, ethereal smoky/misty golden overlay showing a larger ghosted version of the same couple''s close-up — soft focus, very low opacity (15-25%), blending into golden smoke and haze. The smoke should have organic flowing shapes, swirling gently. OVERALL TONE: Entirely warm golden monochrome — like a premium sepia-toned photograph with rich depth. Soft golden light rays and volumetric golden haze throughout. Deep shadows in dark amber, highlights in bright gold. The mood is deeply romantic, intimate, and cinematic. STYLE: Cinematic fine-art romance photography with editorial color grading. Photorealistic — not illustrated. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact identity. No text, no names, no dates, no watermarks, no branding anywhere in the image.'
            WHERE Name = 'Golden Monochrome Romance';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Golden Monochrome Romance StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Wedding Anniversary Festive ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Wedding Anniversary Festive')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Wedding Anniversary Festive', N'Colorful festive wedding anniversary collage with ghost overlay, bokeh, and celebration vibes',
                 N'Create a vibrant festive wedding anniversary photo collage. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: FOREGROUND (center-left): The couple shown full-body or three-quarter in their ORIGINAL clothing and colors, standing together in a natural celebratory pose. Sharp, vivid, well-lit. BACKGROUND: A colorful festive celebration backdrop — warm orange, red, and golden tones with soft bokeh light circles, subtle lens flares, and gentle sparkle effects. GHOST OVERLAY (right side): A large faded version of the same couple''s close-up portrait — slightly desaturated, low opacity (15-25%), blending softly into the festive background. DECORATIVE ELEMENTS: Subtle festive accents — soft colorful paint splash or watercolor wash effects around the edges (reds, oranges, greens, golds), creating a vibrant celebratory border without covering the couple. Small scattered light particles and warm glow effects. BOTTOM AREA: The text Happy Anniversary in an elegant decorative script font with a subtle golden glow effect. No other text — no names, no dates, no messages. STYLE: Vibrant, warm, festive celebration aesthetic. Photorealistic people with artistic decorative background elements. Rich saturated colors — predominantly warm reds, oranges, golds with pops of green. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No names, no dates, no branding, no watermarks — only the text Happy Anniversary.',
                 N'Trendy Photo Collage', N'🎉', '#E65100', 1, 207);

            UPDATE StylePresets SET
                Description = N'Colorful festive wedding anniversary collage with ghost overlay, bokeh, and celebration vibes',
                PromptTemplate = N'Create a vibrant festive wedding anniversary photo collage. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: FOREGROUND (center-left): The couple shown full-body or three-quarter in their ORIGINAL clothing and colors, standing together in a natural celebratory pose. Sharp, vivid, well-lit. BACKGROUND: A colorful festive celebration backdrop — warm orange, red, and golden tones with soft bokeh light circles, subtle lens flares, and gentle sparkle effects. GHOST OVERLAY (right side): A large faded version of the same couple''s close-up portrait — slightly desaturated, low opacity (15-25%), blending softly into the festive background. DECORATIVE ELEMENTS: Subtle festive accents — soft colorful paint splash or watercolor wash effects around the edges (reds, oranges, greens, golds), creating a vibrant celebratory border without covering the couple. Small scattered light particles and warm glow effects. BOTTOM AREA: The text Happy Anniversary in an elegant decorative script font with a subtle golden glow effect. No other text — no names, no dates, no messages. STYLE: Vibrant, warm, festive celebration aesthetic. Photorealistic people with artistic decorative background elements. Rich saturated colors — predominantly warm reds, oranges, golds with pops of green. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No names, no dates, no branding, no watermarks — only the text Happy Anniversary.'
            WHERE Name = 'Wedding Anniversary Festive';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Wedding Anniversary Festive StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Floral Frame Couple Portrait ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Floral Frame Couple Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Floral Frame Couple Portrait', N'Couple portrait framed by watercolor flowers, butterflies, and botanical accents on white background',
                 N'Create a romantic floral-framed couple portrait. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: Clean white or very light cream background. CENTER: The couple shown from waist-up or three-quarter length in a romantic pose — close together, one looking at the other or both smiling naturally. They must be in their ORIGINAL clothing and colors, sharp and vivid. FLORAL FRAME: Surround the couple with an elaborate watercolor botanical border — lush red roses, deep crimson and burgundy flowers, golden-yellow blooms, green leaves and trailing vines. The flowers should be arranged as a natural organic frame — heavier clusters at the top-left and bottom-right corners with trailing elements along the sides. The floral style should be soft watercolor with visible brush texture, semi-transparent petals, and ink-wash blending. BUTTERFLIES: Several delicate butterflies (2-4) in warm tones — golden, amber, soft brown — scattered naturally around the floral frame as if landing on or flying near the flowers. ACCENTS: Subtle golden sparkle dots or fine gold leaf particles scattered lightly through the composition for a premium touch. STYLE: Romantic editorial portrait with artistic watercolor frame. The couple is photorealistic; the flowers and butterflies are artistic/illustrative watercolor. Warm, soft, romantic color palette. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no watermarks, no branding anywhere in the image.',
                 N'Trendy Photo Collage', N'🦋', '#C62828', 1, 208);

            UPDATE StylePresets SET
                Description = N'Couple portrait framed by watercolor flowers, butterflies, and botanical accents on white background',
                PromptTemplate = N'Create a romantic floral-framed couple portrait. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: Clean white or very light cream background. CENTER: The couple shown from waist-up or three-quarter length in a romantic pose — close together, one looking at the other or both smiling naturally. They must be in their ORIGINAL clothing and colors, sharp and vivid. FLORAL FRAME: Surround the couple with an elaborate watercolor botanical border — lush red roses, deep crimson and burgundy flowers, golden-yellow blooms, green leaves and trailing vines. The flowers should be arranged as a natural organic frame — heavier clusters at the top-left and bottom-right corners with trailing elements along the sides. The floral style should be soft watercolor with visible brush texture, semi-transparent petals, and ink-wash blending. BUTTERFLIES: Several delicate butterflies (2-4) in warm tones — golden, amber, soft brown — scattered naturally around the floral frame as if landing on or flying near the flowers. ACCENTS: Subtle golden sparkle dots or fine gold leaf particles scattered lightly through the composition for a premium touch. STYLE: Romantic editorial portrait with artistic watercolor frame. The couple is photorealistic; the flowers and butterflies are artistic/illustrative watercolor. Warm, soft, romantic color palette. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no watermarks, no branding anywhere in the image.'
            WHERE Name = 'Floral Frame Couple Portrait';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Floral Frame Couple Portrait StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Landmark Wedding Portrait ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Landmark Wedding Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Landmark Wedding Portrait', N'Couple portrait composited onto iconic landmark or scenic heritage backdrop with cinematic lighting',
                 N'Create a cinematic wedding portrait with an iconic landmark backdrop. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: FOREGROUND: The couple shown full-body standing together in a natural, confident pose in their ORIGINAL clothing and colors. Sharp, vivid, well-lit with natural warm tones. Position them center or slightly off-center. BACKGROUND: A grand, iconic heritage landmark or scenic architectural backdrop — it could be a majestic palace, a grand historical building with domes and pillars, a beautiful temple, a famous monument, or a scenic coastal/mountain landscape. The landmark should be impressive and clearly recognizable as a prestigious location. The background should have warm golden-hour or blue-hour lighting with soft atmospheric haze for depth. LIGHTING: Cinematic warm lighting on the couple with soft rim light separating them from the background. Natural-looking light integration — the couple''s lighting should match the scene''s ambient light direction. Subtle lens flare or sun glow for a premium cinematic feel. GHOST OVERLAY (optional subtle): A very faint ghosted close-up of the couple''s faces in the sky/upper area at extremely low opacity (5-10%) for added depth — almost imperceptible but adding richness. STYLE: Premium destination wedding photography aesthetic — cinematic, grand, aspirational. Photorealistic — not illustrated. Rich, warm color palette with deep blues, golds, and warm earth tones. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no watermarks, no branding anywhere in the image.',
                 N'Trendy Photo Collage', N'🏛️', '#1565C0', 1, 209);

            UPDATE StylePresets SET
                Description = N'Couple portrait composited onto iconic landmark or scenic heritage backdrop with cinematic lighting',
                PromptTemplate = N'Create a cinematic wedding portrait with an iconic landmark backdrop. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT: FOREGROUND: The couple shown full-body standing together in a natural, confident pose in their ORIGINAL clothing and colors. Sharp, vivid, well-lit with natural warm tones. Position them center or slightly off-center. BACKGROUND: A grand, iconic heritage landmark or scenic architectural backdrop — it could be a majestic palace, a grand historical building with domes and pillars, a beautiful temple, a famous monument, or a scenic coastal/mountain landscape. The landmark should be impressive and clearly recognizable as a prestigious location. The background should have warm golden-hour or blue-hour lighting with soft atmospheric haze for depth. LIGHTING: Cinematic warm lighting on the couple with soft rim light separating them from the background. Natural-looking light integration — the couple''s lighting should match the scene''s ambient light direction. Subtle lens flare or sun glow for a premium cinematic feel. GHOST OVERLAY (optional subtle): A very faint ghosted close-up of the couple''s faces in the sky/upper area at extremely low opacity (5-10%) for added depth — almost imperceptible but adding richness. STYLE: Premium destination wedding photography aesthetic — cinematic, grand, aspirational. Photorealistic — not illustrated. Rich, warm color palette with deep blues, golds, and warm earth tones. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no watermarks, no branding anywhere in the image.'
            WHERE Name = 'Landmark Wedding Portrait';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Landmark Wedding Portrait StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Neon Outline Night ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Neon Outline Night')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Neon Outline Night', N'Dark night scene with glowing neon white outline tracing the person''s silhouette — moody cinematic style',
                 N'Create a moody neon-outline night photo. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person standing full-body in a confident or contemplative pose — hands in pockets, walking, or standing naturally — positioned center-frame on the road. IF TWO OR MORE PEOPLE: Show them together in a natural pose — standing close, embracing, or walking together — positioned center-frame. SCENE: A dark, atmospheric nighttime city street or road — wet asphalt reflecting distant warm streetlights and car headlights creating soft bokeh circles in the background. The road center line (yellow or white) visible beneath their feet. Very low ambient light, deep shadows, cinematic night mood. SUBJECT RENDERING: The person/people should be mostly in DEEP SHADOW, nearly silhouetted against the dark background, with only subtle warm highlights from distant lights catching edges of their hair, shoulders, and clothing. NEON OUTLINE EFFECT: A thin, glowing white or soft cyan neon line precisely tracing the ENTIRE outer contour/silhouette of the person/people — following every edge of their hair, head, shoulders, arms, torso, legs, and shoes. This outline should glow softly with a subtle luminous halo/bloom effect around it, as if drawn with a neon light tube. The outline must be clean, continuous, and accurately follow the body shape. The glow should be bright white or pale blue-white, contrasting dramatically against the dark scene. ATMOSPHERE: Moody, cinematic, dramatic. Dark blue-black tones with warm amber bokeh in the background. Slight haze or mist for atmospheric depth. STYLE: Cinematic night photography with digital neon-art overlay. The people and scene are photorealistic; only the outline is a stylized effect. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people — use ONLY the people from the source image. No text, no names, no dates, no watermarks, no branding anywhere in the image.',
                 N'Trendy Photo Collage', N'💫', '#00E5FF', 1, 210);

            UPDATE StylePresets SET
                Description = N'Dark night scene with glowing neon white outline tracing the person''s silhouette — moody cinematic style',
                PromptTemplate = N'Create a moody neon-outline night photo. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person standing full-body in a confident or contemplative pose — hands in pockets, walking, or standing naturally — positioned center-frame on the road. IF TWO OR MORE PEOPLE: Show them together in a natural pose — standing close, embracing, or walking together — positioned center-frame. SCENE: A dark, atmospheric nighttime city street or road — wet asphalt reflecting distant warm streetlights and car headlights creating soft bokeh circles in the background. The road center line (yellow or white) visible beneath their feet. Very low ambient light, deep shadows, cinematic night mood. SUBJECT RENDERING: The person/people should be mostly in DEEP SHADOW, nearly silhouetted against the dark background, with only subtle warm highlights from distant lights catching edges of their hair, shoulders, and clothing. NEON OUTLINE EFFECT: A thin, glowing white or soft cyan neon line precisely tracing the ENTIRE outer contour/silhouette of the person/people — following every edge of their hair, head, shoulders, arms, torso, legs, and shoes. This outline should glow softly with a subtle luminous halo/bloom effect around it, as if drawn with a neon light tube. The outline must be clean, continuous, and accurately follow the body shape. The glow should be bright white or pale blue-white, contrasting dramatically against the dark scene. ATMOSPHERE: Moody, cinematic, dramatic. Dark blue-black tones with warm amber bokeh in the background. Slight haze or mist for atmospheric depth. STYLE: Cinematic night photography with digital neon-art overlay. The people and scene are photorealistic; only the outline is a stylized effect. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people — use ONLY the people from the source image. No text, no names, no dates, no watermarks, no branding anywhere in the image.'
            WHERE Name = 'Neon Outline Night';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Neon Outline Night StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Beach Close-up Ghost Blend ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Beach Close-up Ghost Blend')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Beach Close-up Ghost Blend', N'Large intimate close-up with faded full-body ghost of same couple at beach or outdoor setting',
                 N'Create a romantic dual-layer couple portrait with ghost blend effect. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT — TWO LAYERS BLENDED: PRIMARY LAYER (dominant, fills most of canvas): A LARGE intimate close-up of the couple — shot from chest/shoulders up, filling the upper two-thirds of the canvas. The couple in a tender romantic pose — foreheads touching, eyes closed or gazing at each other, one person''s hand gently on the other''s face or head. Sharp focus, vivid natural colors, soft natural lighting. This is the hero image and must be crystal clear. GHOST LAYER (faded, lower portion): A full-body shot of the SAME couple in a romantic pose — sitting together, walking on a beach, or standing in a scenic outdoor location (beach shoreline, garden, open field). This full-body image should be rendered at LOW OPACITY (20-35%), soft focus, and blend seamlessly into the close-up layer above using a smooth vertical gradient dissolve. The ghost layer occupies roughly the lower 40-50% of the canvas, overlapping with the bottom of the close-up. BACKGROUND: The overall background should be soft, light, and airy — pale sky, soft beach tones, or gentle natural setting that doesn''t compete with the couple. Light pastel tones — whites, soft blues, gentle warm creams. MOOD: Tender, intimate, airy, romantic. Soft natural daylight throughout. Clean and modern editorial couple photography aesthetic. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no watermarks, no branding anywhere in the image.',
                 N'Trendy Photo Collage', N'🌊', '#80DEEA', 1, 211);

            UPDATE StylePresets SET
                Description = N'Large intimate close-up with faded full-body ghost of same couple at beach or outdoor setting',
                PromptTemplate = N'Create a romantic dual-layer couple portrait with ghost blend effect. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. DETECT how many people are in the uploaded source image and use them exactly as they appear. LAYOUT — TWO LAYERS BLENDED: PRIMARY LAYER (dominant, fills most of canvas): A LARGE intimate close-up of the couple — shot from chest/shoulders up, filling the upper two-thirds of the canvas. The couple in a tender romantic pose — foreheads touching, eyes closed or gazing at each other, one person''s hand gently on the other''s face or head. Sharp focus, vivid natural colors, soft natural lighting. This is the hero image and must be crystal clear. GHOST LAYER (faded, lower portion): A full-body shot of the SAME couple in a romantic pose — sitting together, walking on a beach, or standing in a scenic outdoor location (beach shoreline, garden, open field). This full-body image should be rendered at LOW OPACITY (20-35%), soft focus, and blend seamlessly into the close-up layer above using a smooth vertical gradient dissolve. The ghost layer occupies roughly the lower 40-50% of the canvas, overlapping with the bottom of the close-up. BACKGROUND: The overall background should be soft, light, and airy — pale sky, soft beach tones, or gentle natural setting that doesn''t compete with the couple. Light pastel tones — whites, soft blues, gentle warm creams. MOOD: Tender, intimate, airy, romantic. Soft natural daylight throughout. Clean and modern editorial couple photography aesthetic. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no watermarks, no branding anywhere in the image.'
            WHERE Name = 'Beach Close-up Ghost Blend';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Beach Close-up Ghost Blend StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Bridal Gold Magazine ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Bridal Gold Magazine')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Bridal Gold Magazine', N'Traditional bridal portrait with golden sepia ghost overlay and elegant magazine album layout',
                 N'Create an elegant bridal magazine-style photo composition. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. LAYOUT — MAGAZINE ALBUM PAGE: BACKGROUND (full canvas): A warm GOLDEN SEPIA toned ghost image of the person — a close-up or profile view showing face and upper body, rendered at LOW OPACITY (20-30%) in rich gold/sepia monochrome tones. This fills the entire canvas as a backdrop and should have a soft, dreamy quality with warm golden color grading. Subtle golden bokeh circles and soft floral/leaf decorative accents (golden leaves, delicate vine silhouettes) in the upper corners for an ornate premium feel. FOREGROUND (right side, lower two-thirds): The person shown in FULL BODY or three-quarter length, standing elegantly in their ORIGINAL clothing and colors — sharp, vivid, well-lit with natural warm tones. Position them on the right side of the canvas. This is the hero image — crystal clear and photorealistic. ACCENT PANEL (optional, bottom-left): A smaller inset photo panel with rounded corners showing a different angle or closer crop of the same person — waist-up or medium shot, also in original vivid colors. This panel should have a subtle white or gold border. DECORATIVE: Delicate golden leaf/floral ornamental accents in the upper portion of the canvas — thin, elegant, not overwhelming. Warm golden light particles or subtle sparkle. The overall color palette is rich gold, warm amber, deep cream, and the person''s original vivid colors in the foreground. STYLE: Premium bridal magazine album page aesthetic — luxurious, warm, golden. Photorealistic — not illustrated. IF THE BACKGROUND IS MAJORLY WHITE OR PLAIN: Add elegant floral designs, watercolor botanical elements, or ornate golden decorative patterns to fill the space and make the composition unique and rich. STRICT RULES: Face must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no watermarks, no branding anywhere in the image.',
                 N'Trendy Photo Collage', N'👑', '#B8860B', 1, 212);

            UPDATE StylePresets SET
                Description = N'Traditional bridal portrait with golden sepia ghost overlay and elegant magazine album layout',
                PromptTemplate = N'Create an elegant bridal magazine-style photo composition. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. LAYOUT — MAGAZINE ALBUM PAGE: BACKGROUND (full canvas): A warm GOLDEN SEPIA toned ghost image of the person — a close-up or profile view showing face and upper body, rendered at LOW OPACITY (20-30%) in rich gold/sepia monochrome tones. This fills the entire canvas as a backdrop and should have a soft, dreamy quality with warm golden color grading. Subtle golden bokeh circles and soft floral/leaf decorative accents (golden leaves, delicate vine silhouettes) in the upper corners for an ornate premium feel. FOREGROUND (right side, lower two-thirds): The person shown in FULL BODY or three-quarter length, standing elegantly in their ORIGINAL clothing and colors — sharp, vivid, well-lit with natural warm tones. Position them on the right side of the canvas. This is the hero image — crystal clear and photorealistic. ACCENT PANEL (optional, bottom-left): A smaller inset photo panel with rounded corners showing a different angle or closer crop of the same person — waist-up or medium shot, also in original vivid colors. This panel should have a subtle white or gold border. DECORATIVE: Delicate golden leaf/floral ornamental accents in the upper portion of the canvas — thin, elegant, not overwhelming. Warm golden light particles or subtle sparkle. The overall color palette is rich gold, warm amber, deep cream, and the person''s original vivid colors in the foreground. STYLE: Premium bridal magazine album page aesthetic — luxurious, warm, golden. Photorealistic — not illustrated. IF THE BACKGROUND IS MAJORLY WHITE OR PLAIN: Add elegant floral designs, watercolor botanical elements, or ornate golden decorative patterns to fill the space and make the composition unique and rich. STRICT RULES: Face must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. No text, no names, no dates, no watermarks, no branding anywhere in the image.'
            WHERE Name = 'Bridal Gold Magazine';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Bridal Gold Magazine StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Ink Wash Romance ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Ink Wash Romance')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Ink Wash Romance', N'Artistic ink wash illustration of couple with scattered flower petals — manga-inspired romantic art',
                 N'Create a romantic ink wash art portrait. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person lying down or reclining, looking up with a gentle smile, hair spread naturally. IF TWO OR MORE PEOPLE: Show them lying down facing each other from opposite ends — one person''s head at the top of the canvas (slightly rotated/inverted), the other at the bottom, gazing at each other with warm smiles. Their faces should be close together in the center of the canvas. STYLE: Elegant ink wash / watercolor illustration technique — visible brushstrokes, ink splatter accents, loose flowing black ink lines for hair and clothing. The rendering should feel like a premium hand-painted illustration while keeping facial features PHOTOREALISTIC and accurate. Hair rendered with dramatic flowing black ink strokes and subtle grey washes. Clothing in dark tones with loose watercolor texture. FLORAL ELEMENTS: Delicate scattered flower petals and small blossoms (soft pink, white, cream) around the edges and corners of the composition. Subtle ink-wash floral clusters in the background. Small white sparkle dots or light particles floating in the scene. BACKGROUND: Clean off-white / light grey with subtle ink wash texture and organic ink splatter marks. Minimal and airy. MOOD: Intimate, tender, artistic, romantic. Soft warm tones with dramatic black ink contrast. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED — the face is the ONE element that must NOT be stylized into illustration. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks.',
                 N'Trendy Photo Collage', N'🖋️', '#37474F', 1, 213);

            UPDATE StylePresets SET
                Description = N'Artistic ink wash illustration of couple with scattered flower petals — manga-inspired romantic art',
                PromptTemplate = N'Create a romantic ink wash art portrait. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person lying down or reclining, looking up with a gentle smile, hair spread naturally. IF TWO OR MORE PEOPLE: Show them lying down facing each other from opposite ends — one person''s head at the top of the canvas (slightly rotated/inverted), the other at the bottom, gazing at each other with warm smiles. Their faces should be close together in the center of the canvas. STYLE: Elegant ink wash / watercolor illustration technique — visible brushstrokes, ink splatter accents, loose flowing black ink lines for hair and clothing. The rendering should feel like a premium hand-painted illustration while keeping facial features PHOTOREALISTIC and accurate. Hair rendered with dramatic flowing black ink strokes and subtle grey washes. Clothing in dark tones with loose watercolor texture. FLORAL ELEMENTS: Delicate scattered flower petals and small blossoms (soft pink, white, cream) around the edges and corners of the composition. Subtle ink-wash floral clusters in the background. Small white sparkle dots or light particles floating in the scene. BACKGROUND: Clean off-white / light grey with subtle ink wash texture and organic ink splatter marks. Minimal and airy. MOOD: Intimate, tender, artistic, romantic. Soft warm tones with dramatic black ink contrast. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED — the face is the ONE element that must NOT be stylized into illustration. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks.'
            WHERE Name = 'Ink Wash Romance';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Ink Wash Romance StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Rain Umbrella Night ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Rain Umbrella Night')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Rain Umbrella Night', N'Couple under transparent umbrella in dramatic rain with bokeh lights — cinematic night romance',
                 N'Create a dramatic rain romance photo. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person standing under a transparent umbrella, looking contemplative or smiling softly, in a natural confident pose. IF TWO OR MORE PEOPLE: Show them standing close together under a single large transparent/clear umbrella, facing each other in an intimate romantic pose — embracing, foreheads touching, or holding each other close. SCENE: Dramatic nighttime setting with HEAVY RAIN falling all around. The rain should be clearly visible — individual raindrops and rain streaks illuminated by backlight, creating a beautiful curtain of water around the umbrella. The transparent umbrella should show rain droplets bouncing and splashing off its surface. LIGHTING: Strong backlighting from warm streetlights or venue lights behind the subject(s), creating dramatic rim lighting on their hair and shoulders. Soft colorful bokeh circles (warm amber, soft pink, white) in the blurred background from distant lights. The rain catches the backlight creating sparkle and shimmer. Wet ground reflecting all the lights with beautiful mirror-like reflections. ATMOSPHERE: Romantic, dramatic, cinematic. Dark background with warm light accents. The contrast between the cozy shelter under the umbrella and the dramatic rain creates an intimate mood. STYLE: Cinematic night photography — dramatic lighting, shallow depth of field, photorealistic. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Preserve original clothing and colors. Do NOT add extra people. No text, no names, no dates, no watermarks.',
                 N'Trendy Photo Collage', N'🌧️', '#5C6BC0', 1, 214);

            UPDATE StylePresets SET
                Description = N'Couple under transparent umbrella in dramatic rain with bokeh lights — cinematic night romance',
                PromptTemplate = N'Create a dramatic rain romance photo. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person standing under a transparent umbrella, looking contemplative or smiling softly, in a natural confident pose. IF TWO OR MORE PEOPLE: Show them standing close together under a single large transparent/clear umbrella, facing each other in an intimate romantic pose — embracing, foreheads touching, or holding each other close. SCENE: Dramatic nighttime setting with HEAVY RAIN falling all around. The rain should be clearly visible — individual raindrops and rain streaks illuminated by backlight, creating a beautiful curtain of water around the umbrella. The transparent umbrella should show rain droplets bouncing and splashing off its surface. LIGHTING: Strong backlighting from warm streetlights or venue lights behind the subject(s), creating dramatic rim lighting on their hair and shoulders. Soft colorful bokeh circles (warm amber, soft pink, white) in the blurred background from distant lights. The rain catches the backlight creating sparkle and shimmer. Wet ground reflecting all the lights with beautiful mirror-like reflections. ATMOSPHERE: Romantic, dramatic, cinematic. Dark background with warm light accents. The contrast between the cozy shelter under the umbrella and the dramatic rain creates an intimate mood. STYLE: Cinematic night photography — dramatic lighting, shallow depth of field, photorealistic. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Preserve original clothing and colors. Do NOT add extra people. No text, no names, no dates, no watermarks.'
            WHERE Name = 'Rain Umbrella Night';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Rain Umbrella Night StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Soft Pastel Watercolor Portrait ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Soft Pastel Watercolor Portrait')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Soft Pastel Watercolor Portrait', N'Dreamy warm watercolor couple portrait with soft floral background — romantic art style',
                 N'Create a dreamy pastel watercolor romantic portrait. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person in a gentle, elegant pose — looking slightly to the side with a soft smile, rendered from chest up or three-quarter length. IF TWO OR MORE PEOPLE: Show them in an intimate close pose — one person behind the other with arms wrapped around, or side by side with heads leaning together. The person behind resting their chin on the other''s shoulder or head, both with gentle warm expressions. STYLE: Soft pastel watercolor painting aesthetic — the entire image should feel like a hand-painted watercolor portrait. Skin rendered with soft, warm watercolor washes maintaining photorealistic facial features. Hair painted with flowing brushstrokes in natural tones. Clothing rendered with visible watercolor texture — soft washes, gentle color bleeds, subtle brush marks. COLOR PALETTE: Predominantly WARM CREAM, SOFT PEACH, MUTED DUSTY ROSE, and PALE GOLD — warm neutral pastel tones. IMPORTANT: Avoid heavy magenta, hot pink, or saturated purple tones. Keep pinks very muted and desaturated, leaning toward dusty rose and blush rather than magenta. The overall warmth should come from peach, cream, and gold tones, not from pink/magenta. BACKGROUND: Dreamy soft-focus watercolor floral background — large faded soft peach and muted dusty rose roses or peonies rendered in very soft, blurred watercolor washes. The flowers should be ethereal and semi-transparent, not sharp or dominant. Use warm cream and pale gold tones rather than lavender or magenta. Subtle paint splatter and watercolor bloom effects. Light, airy, and romantic. ACCENTS: Subtle sparkle or light particle effects. Soft warm golden glow. The overall feel should be like a premium watercolor illustration in a romance novel or wedding invitation. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED — faces are the ONE element that stays sharp and realistic within the watercolor style. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks.',
                 N'Trendy Photo Collage', N'🌸', '#FFCCBC', 1, 215);

            UPDATE StylePresets SET
                Description = N'Dreamy warm watercolor couple portrait with soft floral background — romantic art style',
                PromptTemplate = N'Create a dreamy pastel watercolor romantic portrait. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person in a gentle, elegant pose — looking slightly to the side with a soft smile, rendered from chest up or three-quarter length. IF TWO OR MORE PEOPLE: Show them in an intimate close pose — one person behind the other with arms wrapped around, or side by side with heads leaning together. The person behind resting their chin on the other''s shoulder or head, both with gentle warm expressions. STYLE: Soft pastel watercolor painting aesthetic — the entire image should feel like a hand-painted watercolor portrait. Skin rendered with soft, warm watercolor washes maintaining photorealistic facial features. Hair painted with flowing brushstrokes in natural tones. Clothing rendered with visible watercolor texture — soft washes, gentle color bleeds, subtle brush marks. COLOR PALETTE: Predominantly WARM CREAM, SOFT PEACH, MUTED DUSTY ROSE, and PALE GOLD — warm neutral pastel tones. IMPORTANT: Avoid heavy magenta, hot pink, or saturated purple tones. Keep pinks very muted and desaturated, leaning toward dusty rose and blush rather than magenta. The overall warmth should come from peach, cream, and gold tones, not from pink/magenta. BACKGROUND: Dreamy soft-focus watercolor floral background — large faded soft peach and muted dusty rose roses or peonies rendered in very soft, blurred watercolor washes. The flowers should be ethereal and semi-transparent, not sharp or dominant. Use warm cream and pale gold tones rather than lavender or magenta. Subtle paint splatter and watercolor bloom effects. Light, airy, and romantic. ACCENTS: Subtle sparkle or light particle effects. Soft warm golden glow. The overall feel should be like a premium watercolor illustration in a romance novel or wedding invitation. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED — faces are the ONE element that stays sharp and realistic within the watercolor style. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks.'
            WHERE Name = 'Soft Pastel Watercolor Portrait';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Soft Pastel Watercolor Portrait StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Wheat Field Romance ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Wheat Field Romance')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Wheat Field Romance', N'Couple in golden wheat or grass field with natural outdoor setting — rustic romantic portrait',
                 N'Create a rustic romantic outdoor portrait in a natural field setting. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person standing naturally in the field — relaxed, happy, looking at the camera or gazing into the distance, surrounded by tall grass or wheat. IF TWO OR MORE PEOPLE: Show them together in a warm, playful romantic pose — one person hugging the other from behind, both smiling joyfully, surrounded by the field. Natural, candid, authentic body language. SCENE: A beautiful golden wheat field or tall grass meadow — the subjects positioned in the lower half of the canvas, surrounded by tall golden-green grass and wheat stalks reaching up to their waist or chest. Lush tropical or rural greenery (palm trees, coconut trees, or dense foliage) visible in the soft-focus background above the field. LIGHTING: Warm, natural golden-hour sunlight — soft and diffused. The light should come from behind or to the side, creating a warm glow and gentle rim light on the subjects'' hair. Natural, flattering outdoor light. ATMOSPHERE: The sky should be soft, slightly overcast or hazy — pale blue-white, creating a dreamy outdoor mood. The overall color palette is warm greens, golden yellows, natural earth tones, and soft sky blues. Fresh, natural, organic feel. STYLE: Natural candid photography aesthetic — like a premium outdoor pre-wedding or engagement shoot. Photorealistic, warm, authentic. Shallow depth of field with the field slightly blurred in the immediate foreground and background. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Preserve original clothing and colors. Do NOT add extra people. No text, no names, no dates, no watermarks.',
                 N'Trendy Photo Collage', N'🌾', '#8D6E63', 1, 216);

            UPDATE StylePresets SET
                Description = N'Couple in golden wheat or grass field with natural outdoor setting — rustic romantic portrait',
                PromptTemplate = N'Create a rustic romantic outdoor portrait in a natural field setting. CRITICAL: Preserve EVERY person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Show the single person standing naturally in the field — relaxed, happy, looking at the camera or gazing into the distance, surrounded by tall grass or wheat. IF TWO OR MORE PEOPLE: Show them together in a warm, playful romantic pose — one person hugging the other from behind, both smiling joyfully, surrounded by the field. Natural, candid, authentic body language. SCENE: A beautiful golden wheat field or tall grass meadow — the subjects positioned in the lower half of the canvas, surrounded by tall golden-green grass and wheat stalks reaching up to their waist or chest. Lush tropical or rural greenery (palm trees, coconut trees, or dense foliage) visible in the soft-focus background above the field. LIGHTING: Warm, natural golden-hour sunlight — soft and diffused. The light should come from behind or to the side, creating a warm glow and gentle rim light on the subjects'' hair. Natural, flattering outdoor light. ATMOSPHERE: The sky should be soft, slightly overcast or hazy — pale blue-white, creating a dreamy outdoor mood. The overall color palette is warm greens, golden yellows, natural earth tones, and soft sky blues. Fresh, natural, organic feel. STYLE: Natural candid photography aesthetic — like a premium outdoor pre-wedding or engagement shoot. Photorealistic, warm, authentic. Shallow depth of field with the field slightly blurred in the immediate foreground and background. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Preserve original clothing and colors. Do NOT add extra people. No text, no names, no dates, no watermarks.'
            WHERE Name = 'Wheat Field Romance';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Wheat Field Romance StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Torn Paper Bridal Collage ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Torn Paper Bridal Collage')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Torn Paper Bridal Collage', N'Multiple dramatic close-ups with torn ripped paper edges — moody cinematic bridal or portrait collage',
                 N'Create a dramatic torn-paper collage portrait. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. LAYOUT: Three horizontal photo panels stacked vertically, separated by TORN PAPER EDGES — realistic ripped/torn white paper effect with ragged, organic, uneven edges creating a dramatic layered look. The torn edges should look like actual ripped paper with visible paper fiber texture and slight curling. TOP PANEL (upper third): A dramatic close-up or three-quarter shot of the person in their original clothing and styling. Moody, warm-toned lighting — deep reds, ambers, and rich warm shadows. The person looking elegant and intense. MIDDLE PANEL (center, largest): An EXTREME CLOSE-UP of the person''s face — focusing on the eyes and nose bridge area, or a dramatic profile view. This should be the most striking panel with cinematic lighting — rich warm tones, deep shadows, dramatic contrast. The close-up should show fine detail — eyelashes, skin texture, jewelry if worn. BOTTOM PANEL (lower third): A different pose or angle — full body or waist-up, showing the person in an expressive or graceful pose. Same warm, moody lighting with deep reds and golds. OVERALL AESTHETIC: Rich, warm, cinematic color grading throughout all panels — deep burgundy reds, warm golds, amber tones, dramatic shadows. Moody and editorial. The torn paper edges between panels should be crisp white, contrasting dramatically with the dark warm images. STYLE: High-fashion editorial or premium bridal album collage. Cinematic, dramatic, luxurious. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks.',
                 N'Trendy Photo Collage', N'📜', '#B71C1C', 1, 217);

            UPDATE StylePresets SET
                Description = N'Multiple dramatic close-ups with torn ripped paper edges — moody cinematic bridal or portrait collage',
                PromptTemplate = N'Create a dramatic torn-paper collage portrait. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. LAYOUT: Three horizontal photo panels stacked vertically, separated by TORN PAPER EDGES — realistic ripped/torn white paper effect with ragged, organic, uneven edges creating a dramatic layered look. The torn edges should look like actual ripped paper with visible paper fiber texture and slight curling. TOP PANEL (upper third): A dramatic close-up or three-quarter shot of the person in their original clothing and styling. Moody, warm-toned lighting — deep reds, ambers, and rich warm shadows. The person looking elegant and intense. MIDDLE PANEL (center, largest): An EXTREME CLOSE-UP of the person''s face — focusing on the eyes and nose bridge area, or a dramatic profile view. This should be the most striking panel with cinematic lighting — rich warm tones, deep shadows, dramatic contrast. The close-up should show fine detail — eyelashes, skin texture, jewelry if worn. BOTTOM PANEL (lower third): A different pose or angle — full body or waist-up, showing the person in an expressive or graceful pose. Same warm, moody lighting with deep reds and golds. OVERALL AESTHETIC: Rich, warm, cinematic color grading throughout all panels — deep burgundy reds, warm golds, amber tones, dramatic shadows. Moody and editorial. The torn paper edges between panels should be crisp white, contrasting dramatically with the dark warm images. STYLE: High-fashion editorial or premium bridal album collage. Cinematic, dramatic, luxurious. STRICT RULES: All faces must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks.'
            WHERE Name = 'Torn Paper Bridal Collage';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Torn Paper Bridal Collage StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Mountain Explorer Double Exposure ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Mountain Explorer Double Exposure')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Mountain Explorer Double Exposure', N'Large side profile silhouette filled with mountain landscape and hiking figure — adventure poster art',
                 N'Create an epic mountain explorer double-exposure photo art. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. LAYOUT: Clean white or very light grey background. The person''s HEAD AND UPPER BODY shown in LARGE SIDE PROFILE view (facing right) — occupying most of the canvas. The profile should be rendered in DESATURATED / near-monochrome tones (greys, charcoals, muted earth tones) while maintaining photorealistic facial detail — every pore, hair strand, and skin texture clearly visible. DOUBLE EXPOSURE FILL: INSIDE the silhouette of the head and upper body, composite a dramatic MOUNTAIN LANDSCAPE — rugged rocky mountain peaks, sweeping valley views, misty clouds rolling over ridgelines, layered mountain ranges fading into atmospheric haze. The landscape should fill the skull/hair area and blend naturally into the profile outline. ADVENTURE FIGURE: A small silhouette of the SAME person standing on a rocky mountain peak or cliff edge within the double-exposure area — shown in full body, small scale, as if conquering the summit. This figure should be positioned naturally on a rock formation inside the profile. BIRDS: A small flock of birds (5-8) flying in loose formation in the sky/cloud area within or just above the profile silhouette — adding a sense of freedom and adventure. BLENDING: The edges of the profile silhouette should dissolve softly into the white background — organic, feathered edges, especially around the hair and shoulders. The mountain landscape fades from vivid detail at the center to transparent at the edges. COLOR PALETTE: Predominantly monochrome with selective muted color — greys, charcoals, blacks, with subtle touches of muted green/blue in the mountain landscape for depth. The overall tone is dramatic and cinematic. IF THE BACKGROUND IS MAJORLY WHITE OR PLAIN: Add subtle texture or atmospheric elements to make the composition feel rich and complete. STYLE: Premium editorial double-exposure photo art — cinematic, dramatic, aspirational. The face remains photorealistic; the landscape overlay is artistic. STRICT RULES: Face must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks, no branding.',
                 N'Trendy Photo Collage', N'⛰️', '#455A64', 1, 218);

            UPDATE StylePresets SET
                Description = N'Large side profile silhouette filled with mountain landscape and hiking figure — adventure poster art',
                PromptTemplate = N'Create an epic mountain explorer double-exposure photo art. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. LAYOUT: Clean white or very light grey background. The person''s HEAD AND UPPER BODY shown in LARGE SIDE PROFILE view (facing right) — occupying most of the canvas. The profile should be rendered in DESATURATED / near-monochrome tones (greys, charcoals, muted earth tones) while maintaining photorealistic facial detail — every pore, hair strand, and skin texture clearly visible. DOUBLE EXPOSURE FILL: INSIDE the silhouette of the head and upper body, composite a dramatic MOUNTAIN LANDSCAPE — rugged rocky mountain peaks, sweeping valley views, misty clouds rolling over ridgelines, layered mountain ranges fading into atmospheric haze. The landscape should fill the skull/hair area and blend naturally into the profile outline. ADVENTURE FIGURE: A small silhouette of the SAME person standing on a rocky mountain peak or cliff edge within the double-exposure area — shown in full body, small scale, as if conquering the summit. This figure should be positioned naturally on a rock formation inside the profile. BIRDS: A small flock of birds (5-8) flying in loose formation in the sky/cloud area within or just above the profile silhouette — adding a sense of freedom and adventure. BLENDING: The edges of the profile silhouette should dissolve softly into the white background — organic, feathered edges, especially around the hair and shoulders. The mountain landscape fades from vivid detail at the center to transparent at the edges. COLOR PALETTE: Predominantly monochrome with selective muted color — greys, charcoals, blacks, with subtle touches of muted green/blue in the mountain landscape for depth. The overall tone is dramatic and cinematic. IF THE BACKGROUND IS MAJORLY WHITE OR PLAIN: Add subtle texture or atmospheric elements to make the composition feel rich and complete. STYLE: Premium editorial double-exposure photo art — cinematic, dramatic, aspirational. The face remains photorealistic; the landscape overlay is artistic. STRICT RULES: Face must remain 100% photorealistic and UNCHANGED. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks, no branding.'
            WHERE Name = 'Mountain Explorer Double Exposure';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Mountain Explorer Double Exposure StylePreset seeding failed (non-fatal)");
    }

    // ── Seed Dark Silhouette Double Profile ──
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = 'Dark Silhouette Double Profile')
                INSERT INTO StylePresets (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder) VALUES
                (N'Dark Silhouette Double Profile', N'Two overlapping profile silhouettes on dark background with warm rim lighting — dramatic cinematic portrait',
                 N'Create a dramatic dark silhouette double-profile portrait. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: LAYOUT: Deep dark background — near-black, rich dark brown or charcoal tones. Show TWO overlapping PROFILE SILHOUETTES of the SAME person. PROFILE 1 (foreground, left): A close-up side profile of the person facing RIGHT. This profile is mostly in deep shadow/silhouette with only subtle warm rim lighting catching the edge of the nose, lips, chin, and jawline. The person''s original clothing, jewelry, and accessories should be faintly visible where warm light catches fabric texture, beadwork, or ornamental details. PROFILE 2 (background, right, larger): A second, slightly larger side profile silhouette of the SAME person, also facing RIGHT but positioned behind and overlapping the first profile. This second silhouette is even darker — almost entirely in shadow — creating a dramatic layered depth effect. The overlap creates an artistic double-exposure feel. IF TWO OR MORE PEOPLE: Show each person as a separate profile silhouette, overlapping in depth — the first person''s profile in the foreground, the second person''s profile larger behind, creating a dramatic layered composition. Each person maintains their exact appearance. LIGHTING: Extremely low-key — 90% of the image is in deep shadow. Only subtle warm rim light (amber, golden-brown) traces along the profile edges — highlighting the nose bridge, lip contour, chin line, and neck. A faint warm glow on the shoulder/upper body area reveals hints of the person''s original clothing texture and colors. No direct front lighting — this is a backlit/rim-lit composition. COLOR PALETTE: Predominantly deep dark tones — blacks, dark browns, charcoals. The only color comes from subtle warm amber/golden rim lighting and faint hints of the person''s original clothing colors peeking through the shadows. Overall mood: mysterious, dramatic, cinematic, elegant. STYLE: Fine-art low-key portrait photography — dramatic chiaroscuro lighting, cinematic. The person remains photorealistic; the lighting creates the artistic effect. STRICT RULES: Face profile must remain 100% photorealistic and accurate to the source. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks.',
                 N'Trendy Photo Collage', N'🌑', '#3E2723', 1, 219);

            UPDATE StylePresets SET
                Description = N'Two overlapping profile silhouettes on dark background with warm rim lighting — dramatic cinematic portrait',
                PromptTemplate = N'Create a dramatic dark silhouette double-profile portrait. CRITICAL: Preserve the person''s EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. First, DETECT how many people are in the uploaded source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: LAYOUT: Deep dark background — near-black, rich dark brown or charcoal tones. Show TWO overlapping PROFILE SILHOUETTES of the SAME person. PROFILE 1 (foreground, left): A close-up side profile of the person facing RIGHT. This profile is mostly in deep shadow/silhouette with only subtle warm rim lighting catching the edge of the nose, lips, chin, and jawline. The person''s original clothing, jewelry, and accessories should be faintly visible where warm light catches fabric texture, beadwork, or ornamental details. PROFILE 2 (background, right, larger): A second, slightly larger side profile silhouette of the SAME person, also facing RIGHT but positioned behind and overlapping the first profile. This second silhouette is even darker — almost entirely in shadow — creating a dramatic layered depth effect. The overlap creates an artistic double-exposure feel. IF TWO OR MORE PEOPLE: Show each person as a separate profile silhouette, overlapping in depth — the first person''s profile in the foreground, the second person''s profile larger behind, creating a dramatic layered composition. Each person maintains their exact appearance. LIGHTING: Extremely low-key — 90% of the image is in deep shadow. Only subtle warm rim light (amber, golden-brown) traces along the profile edges — highlighting the nose bridge, lip contour, chin line, and neck. A faint warm glow on the shoulder/upper body area reveals hints of the person''s original clothing texture and colors. No direct front lighting — this is a backlit/rim-lit composition. COLOR PALETTE: Predominantly deep dark tones — blacks, dark browns, charcoals. The only color comes from subtle warm amber/golden rim lighting and faint hints of the person''s original clothing colors peeking through the shadows. Overall mood: mysterious, dramatic, cinematic, elegant. STYLE: Fine-art low-key portrait photography — dramatic chiaroscuro lighting, cinematic. The person remains photorealistic; the lighting creates the artistic effect. STRICT RULES: Face profile must remain 100% photorealistic and accurate to the source. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone and natural proportions. Do NOT add extra people. No text, no names, no dates, no watermarks.'
            WHERE Name = 'Dark Silhouette Double Profile';
        ");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Dark Silhouette Double Profile StylePreset seeding failed (non-fatal)");
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

    // Stamp seed version so we skip all this on next startup
    try
    {
        await db.Database.ExecuteSqlRawAsync($@"
            DELETE FROM __SeedVersion;
            INSERT INTO __SeedVersion (Version) VALUES ({CURRENT_SEED_VERSION})");
        app.Logger.LogInformation("Style seed version {v} stamped.", CURRENT_SEED_VERSION);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Seed version stamp failed (non-fatal)");
    }
    } // end if (!seedAlreadyDone)

    // ── Incremental style seeds ──────────────────────────────────────────
    // These run on EVERY startup, OUTSIDE the seed-version gate.
    // To add a new style: just add an entry to the array below. That's it.
    //   - Force-clears DeletedStyleSeeds so new dev-added styles always appear
    //   - Upserts: inserts if missing, updates prompt/metadata if already present
    //   - No need to bump CURRENT_SEED_VERSION
    // See docs/StylePreset-SeedingGuide.md for full instructions.
    var incrementalStyles = new[]
    {
        new {
            Name        = "Selective Ink Wash",
            Description = "Detailed ink sketch with selective color wash on clothing",
            Prompt      = @"Transform the subject into a detailed ink line-art illustration with selective color wash. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. LINE STYLE: Fine detailed pen-and-ink linework throughout the entire image — precise cross-hatching, contour hatching, and stippling for shading. Lines should be hand-drawn quality with varying thickness — thicker on outlines and shadow edges, thinner for detail and texture. Every element rendered with visible ink strokes — no smooth digital gradients on the figure. COLORING RULES: The body, skin, face, and background remain in greyscale ink — black, white, and grey tones only, like a pencil or ink sketch. ONLY the clothing and fabric accessories receive color washes — applied as translucent watercolor-like tones over the ink lines. Use a warm selective palette for clothing: vermillion red, saffron orange, deep crimson, burnt sienna, and golden amber for garments. Ornaments, jewelry, and metallic accessories rendered in warm gold and antique brass tones with ink-line detail preserved underneath. COMPOSITION: Clean white or off-white background with minimal or no background elements — the focus is entirely on the figure. Soft shadow beneath the subject for grounding. The ink lines remain visible through the color washes — the color is translucent, not opaque. MOOD: Devotional, classical, serene. The style evokes traditional Indian devotional calendar illustrations meets architectural ink rendering. High-contrast ink detail with restrained, elegant use of warm color only on fabrics. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone, natural proportions. No cartoon or artistic rendering of the face. Lighting and shadows must remain natural and consistent. Final output should look like a real person rendered in fine ink illustration with selective color.",
            Category    = "Artistic",
            Emoji       = "\U0001F58B\uFE0F",  // 🖋️
            Color       = "#8B4513",
            SortOrder   = 220
        },
        new {
            Name        = "Royal Palace Wedding",
            Description = "Grand Indian palace wedding oil painting with rich crimson and gold tones",
            Prompt      = @"Transform the subject into a grand royal Indian wedding oil painting portrait. CRITICAL: Preserve EVERY person's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to any face. First, DETECT how many people are in the source image. Use ONLY the people present — do NOT add or remove anyone. IF ONE PERSON: Place the subject in a majestic palace mandap setting, dressed in rich traditional wedding attire — ornate red bridal lehenga with heavy gold zari embroidery OR elegant dark formal sherwani/suit as appropriate to the subject. The subject posed gracefully in a ceremonial stance. IF TWO OR MORE PEOPLE: Compose as a romantic wedding portrait — one person kneeling or standing beside the other in a ring ceremony or hand-holding pose. Each person in their appropriate wedding attire — one in rich crimson red bridal lehenga with gold embroidery and dupatta, the other in a dark formal suit or sherwani. BACKGROUND: Grand palatial mandap interior with ornate golden Mughal arches, intricate carved pillars, and filigree jali screens. Warm golden chandeliers with glowing candlelight. Lush red and orange marigold garland drapes hanging from the archways. Scattered rose petals on the floor. Rich warm ambient lighting creating a regal ceremonial atmosphere. COLOR PALETTE: Deep crimson red, rich gold, warm amber, dark chocolate brown, burnt orange marigold accents. Overall warm golden-hour tone suffusing the entire scene. ART STYLE: Classical oil painting quality with visible brushstroke texture — rich impasto on fabrics and ornaments, smooth blending on skin. Luminous glazing technique creating depth and warmth. Dramatic chiaroscuro lighting — warm golden key light from chandeliers above with soft amber fill. Museum-quality fine art portrait painting feel. Romantic, regal, timeless. STRICT RULES: Every face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone, natural proportions. The oil painting style applies to clothing, background, lighting, and atmosphere ONLY — faces stay sharp and photorealistic. No text, no names, no dates, no watermarks.",
            Category    = "PhotoArts",
            Emoji       = "\U0001F3F0",  // 🏰
            Color       = "#B71C1C",
            SortOrder   = 223
        },
        new {
            Name        = "Lifestyle Cafe Portrait",
            Description = "Trendy cafe lifestyle editorial portrait with warm fashion tones",
            Prompt      = @"Transform the subject into a stylish lifestyle editorial portrait set inside a trendy modern cafe or restaurant. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. SETTING: Place the subject seated casually on a wooden stool or mid-century chair inside a chic contemporary cafe interior. Background features geometric wooden wall panels or exposed brick, hanging pendant lights with warm Edison bulbs, lush indoor plants, and curated decor. Warm ambient cafe lighting with natural window light from one side. STYLING: Dress the subject in smart-casual contemporary fashion — a stylish striped or patterned shirt with well-fitted trousers and clean sneakers or loafers. Relaxed confident pose — one hand resting on knee, body slightly angled, natural easy expression. The outfit colors should complement the warm interior tones. COLOR PALETTE: Warm earth tones — teal, forest green, warm wood brown, amber, cream, muted gold. Rich but natural color grading with warm highlights and soft shadows. Overall warm lifestyle editorial tone. ART STYLE: High-end fashion lifestyle photography quality. Shallow depth of field with the subject tack-sharp and background softly blurred. Natural skin tones with editorial color grading. Magazine editorial quality — GQ or lifestyle blog aesthetic. Clean, aspirational, effortlessly stylish. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone, natural proportions. No text, no names, no watermarks.",
            Category    = "Artistic",
            Emoji       = "\u2615",  // ☕
            Color       = "#5D4037",
            SortOrder   = 224
        },
        new {
            Name        = "Executive Studio Portrait",
            Description = "Polished executive studio portrait with dramatic professional lighting",
            Prompt      = @"Transform the subject into a polished executive studio portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a sharp tailored formal suit — charcoal grey, navy, or dark classic suit with a crisp white dress shirt and silk tie. Impeccable grooming with professional presence. Seated confidently in a slight three-quarter pose with hands relaxed, shoulders squared, direct eye contact with the camera. Exudes authority, confidence, and approachability. BACKGROUND: Clean dark gradient studio backdrop — deep charcoal to black, smooth and distraction-free. No props, no furniture — pure studio environment. LIGHTING: Professional three-point studio lighting setup. Strong key light at 45 degrees creating defined Rembrandt triangle on cheek. Subtle fill light softening shadows. Hair/rim light separating the subject from the dark background with a subtle luminous edge. Catchlights visible in the eyes. Dramatic but not harsh — refined corporate studio quality. COLOR PALETTE: Dark sophisticated tones — charcoal, slate grey, deep navy, crisp white, subtle skin warmth. Muted, professional color grading with rich contrast. ART STYLE: High-end corporate portrait photography — Fortune 500 CEO headshot quality. Razor-sharp focus on the face and upper body with flawless exposure and white balance. Clean post-processing with natural skin texture preserved. Magazine cover executive portrait quality — powerful, refined, commanding. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone, natural proportions. No text, no names, no watermarks.",
            Category    = "Artistic",
            Emoji       = "\U0001F454",  // 👔
            Color       = "#37474F",
            SortOrder   = 225
        },
        new {
            Name        = "Dramatic B&W Close-up",
            Description = "Moody black & white intimate close-up with dramatic side lighting",
            Prompt      = @"Transform the subject into a dramatic black and white fine-art close-up portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone structure, and expression — absolutely no modifications to the face. FRAMING: Tight intimate close-up — face and upper shoulders only, slightly cropped at the top of the head. The subject in a contemplative or introspective pose — one hand near the chin or jawline, fingers relaxed and natural. Slight head tilt with a thoughtful, intense gaze — either direct at camera or slightly off-axis. LIGHTING: Dramatic single-source side lighting from one direction creating deep chiaroscuro contrast. One side of the face brightly illuminated, the other falling into rich deep shadow. Strong highlight on the cheekbone, bridge of nose, and jawline edge. Subtle rim light catching individual hair strands. Deep blacks in the shadows with bright specular highlights. No fill light — embrace the darkness. COLOR: Entirely monochrome black and white. Rich tonal range from pure black to bright white with full spectrum of greys. No color tinting, no sepia, no blue tone — pure classic B&W. Deep grain texture reminiscent of classic Tri-X or HP5 film. ART STYLE: Fine-art portrait photography in the tradition of Peter Lindbergh, Helmut Newton, and Annie Leibovitz. Raw, authentic, emotionally powerful. Minimal retouching — skin texture, pores, and natural details fully visible. The drama comes from light and shadow, not post-processing. Cinematic, moody, gallery-exhibition quality. Background completely black — no distractions. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact facial structure and natural proportions. No text, no names, no watermarks.",
            Category    = "Artistic",
            Emoji       = "\U0001F3AD",  // 🎭
            Color       = "#212121",
            SortOrder   = 226
        },
        // ── Fun styles batch (from Test styles folder) ──
        new {
            Name        = "Street Fashion Stroll",
            Description = "Stylish cable-knit cardigan street fashion editorial portrait",
            Prompt      = @"Transform the subject into a stylish street fashion editorial portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a classic cable-knit shawl-collar cardigan in soft grey or neutral tone over a crisp white shirt with a striped tie. Well-fitted cream or khaki trousers. Stylish aviator or wayfarer sunglasses. A luxury wristwatch on one hand, other hand casually holding a dark overcoat. POSE: Standing confidently on an urban sidewalk, mid-stride or paused casually. One hand adjusting the cardigan, relaxed confident body language. SETTING: Modern city street with soft bokeh — blurred urban architecture, clean sidewalk, natural daylight. Shallow depth of field with subject tack-sharp. COLOR PALETTE: Soft neutrals — grey, cream, white, dark brown accents. Warm natural light with gentle shadows. ART STYLE: High-end fashion street photography — GQ editorial quality. Natural skin tones, crisp detail, aspirational lifestyle. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact skin tone, natural proportions. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F576\uFE0F",  // 🕶️
            Color       = "#78909C",
            SortOrder   = 227
        },
        new {
            Name        = "Home Vibes Casual",
            Description = "Casual hoodie and headphones in modern kitchen lifestyle shot",
            Prompt      = @"Transform the subject into a casual home lifestyle portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a comfortable navy blue zip-up hoodie over a white crew-neck t-shirt. Wireless headphones draped around the neck. Holding a smartphone in one hand, casually looking at it. SETTING: Modern stylish kitchen with dark cabinets, warm under-cabinet LED strip lighting, open shelving with bottles and accessories. A stainless steel pot visible on the counter. Warm ambient interior lighting with moody evening atmosphere. POSE: Standing casually, slightly leaning, absorbed in the phone. Relaxed natural body language. COLOR PALETTE: Deep navy, white, warm wood tones, dark kitchen tones with warm amber accent lighting. Cozy, intimate evening vibe. ART STYLE: Lifestyle photography — influencer content quality. Shallow depth of field with warm color grading. Natural, relatable, effortlessly cool. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3E0",  // 🏠
            Color       = "#1A237E",
            SortOrder   = 228
        },
        new {
            Name        = "Summer Clean Minimal",
            Description = "All-white minimalist summer look against warm terracotta wall",
            Prompt      = @"Transform the subject into a clean minimalist summer fashion portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a fitted white ribbed polo or open-collar knit shirt with cream or off-white tailored trousers. A thin gold chain necklace. Stylish dark sunglasses. Clean minimal look — no excess accessories. POSE: Dynamic confident pose — one hand adjusting the collar or shirt, the other on the hip. Body slightly angled. SETTING: Clean warm terracotta or burnt orange wall as background. Bright natural sunlight casting crisp shadows. Minimal environment — just the subject against the wall. COLOR PALETTE: White, cream, warm terracotta orange, golden skin tones. Bright, sun-kissed, Mediterranean summer vibe. ART STYLE: High-end minimalist fashion photography. Clean sharp focus, bright natural lighting, editorial quality. Fresh, confident, effortlessly stylish. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\u2600\uFE0F",  // ☀️
            Color       = "#E65100",
            SortOrder   = 229
        },
        new {
            Name        = "Metallic Puffer Editorial",
            Description = "Silver metallic oversized puffer jacket with dynamic fashion pose",
            Prompt      = @"Transform the subject into a high-fashion editorial portrait wearing a metallic outfit. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a highly reflective metallic silver oversized puffer jacket over a fitted black base layer. Dark fitted jeans or trousers with chunky metallic or black sneakers. Hair styled slick and sharp. POSE: Dynamic mid-stride or action pose — jacket flaring open with movement, arms slightly spread. Full-body shot showing the entire outfit. SETTING: Rich solid-colored studio backdrop — deep crimson red or burgundy. Clean studio floor. Professional studio flash lighting creating sharp reflections on the metallic jacket surface. LIGHTING: Multiple light sources creating dramatic specular highlights on the silver jacket. Rim light separating subject from background. COLOR PALETTE: Silver metallic, deep black, rich red backdrop. High contrast, bold, futuristic. ART STYLE: High-end fashion editorial — Vogue or avant-garde fashion magazine quality. Sharp, dramatic, statement-making. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001FA9E",  // 🪞
            Color       = "#B0BEC5",
            SortOrder   = 230
        },
        new {
            Name        = "Glass Cube Display",
            Description = "Standing inside geometric glass/metal cube frame in dark studio",
            Prompt      = @"Transform the subject into a dramatic fashion portrait inside a geometric display frame. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in an all-black modern minimalist outfit — sleek black jacket, black turtleneck, slim black trousers, polished black shoes. Clean sharp silhouette. POSE: Standing tall and confident inside a large rectangular open metal frame or glass cube structure — like a human display case. Feet shoulder-width apart, hands at sides or one hand in pocket. SETTING: Dark dramatic studio environment. The metal frame is matte black geometric structure surrounding the subject. Moody atmospheric lighting — single key light from one side, deep shadows everywhere else. Floor reflecting subtle light. LIGHTING: Low-key dramatic — one strong directional light creating sharp contrast. The metal frame casting geometric shadows. Theatrical, gallery-installation aesthetic. COLOR PALETTE: Pure black, dark grey, subtle steel highlights. Monochromatic dark palette. ART STYLE: Avant-garde fashion photography meets art installation. Minimal, architectural, powerful. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F532",  // ▪️🔲
            Color       = "#212121",
            SortOrder   = 231
        },
        new {
            Name        = "F1 Race Driver",
            Description = "Formula 1 racing driver in team suit sitting in race car cockpit",
            Prompt      = @"Transform the subject into a Formula 1 racing driver portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a professional F1 racing team suit — vivid red base with team sponsor patches and logos (generic racing-style patches, no real brand names). Racing gloves visible, a luxury chronograph watch on the wrist. Stylish aviator sunglasses pushed slightly down the nose. POSE: Seated in the driver's seat of a high-end sports car or racing cockpit. One hand on the steering wheel, the other thoughtfully touching the chin or adjusting sunglasses. Relaxed confident driver energy. SETTING: Inside a luxury or racing car cockpit — leather steering wheel, carbon fiber details, racing instruments visible. View through windshield slightly blurred. Moody interior car lighting. COLOR PALETTE: Vivid racing red, black, carbon grey with gold/yellow sponsor accent patches. Dramatic, high-octane energy. ART STYLE: Automotive lifestyle photography — motorsport magazine quality. Cinematic shallow depth of field, rich color grading. Fast, powerful, aspirational. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3CE\uFE0F",  // 🏎️
            Color       = "#D32F2F",
            SortOrder   = 232
        },
        new {
            Name        = "Leather Rebel Studio",
            Description = "Edgy black leather jacket on bold red studio backdrop",
            Prompt      = @"Transform the subject into an edgy fashion portrait with a bold studio setup. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a fitted black leather biker jacket over a black crew-neck top. Dark jeans or leather pants. Dark lace-up boots. Hair styled sharp and slicked. Confident intense expression. POSE: Seated on a minimalist black metal stool or chair, leaning forward slightly with elbows on knees or one hand on knee. Full-body shot. Commanding presence. SETTING: Bold solid red studio backdrop — vivid crimson or scarlet. Clean studio floor, no props except the stool. LIGHTING: Professional studio lighting — strong key light from above-left, creating sharp shadows. The red backdrop evenly lit and saturated. Subject well-separated from background. Dramatic but clean. COLOR PALETTE: Black leather, deep red backdrop, skin tones. High contrast, bold, rebellious. ART STYLE: High-end studio fashion photography — rock-star editorial quality. Clean sharp focus, dramatic color contrast. Edgy, magnetic, commanding. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F9F9",  // 🧥→
            Color       = "#B71C1C",
            SortOrder   = 233
        },
        new {
            Name        = "Velvet Blazer GQ",
            Description = "Rich velvet blazer editorial on muted olive studio backdrop",
            Prompt      = @"Transform the subject into a sophisticated fashion editorial portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a rich deep blue or navy velvet blazer over a dark open-collar shirt. Slim-fit dark trousers with polished black loafers. Refined grooming — neat beard or clean-shaven as per the subject's existing style. POSE: Seated dynamically on a large raw concrete or stone geometric block/bench. One leg crossed or extended. Relaxed confident editorial pose with one arm resting on the block. SETTING: Muted warm olive green or khaki-toned studio backdrop. Clean floor. Single geometric block as the only prop. Sophisticated minimal studio environment. LIGHTING: Soft directional studio light from upper left. Gentle shadows, flattering skin illumination. Subtle fill light. Warm tonal quality. COLOR PALETTE: Deep navy blue velvet, dark charcoal, warm olive green, skin tones. Muted, sophisticated, editorial. ART STYLE: GQ or Esquire magazine fashion editorial. Refined, intelligent, stylish. Professional studio photography quality. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F454",  // 👔
            Color       = "#1A237E",
            SortOrder   = 234
        },
        new {
            Name        = "Car Lean Boss",
            Description = "Smart casual leaning on white sedan with urban backdrop",
            Prompt      = @"Transform the subject into an aspirational car lifestyle portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a clean white zip-up knit sweater or cardigan over a white dress shirt with a slim dark tie. Dark fitted trousers. Stylish aviator sunglasses. A luxury wristwatch. Smart casual boss energy. POSE: Leaning casually against the hood or front of a clean white modern sedan. One hand on the car, the other in pocket or adjusting sunglasses. Full-body shot with the car prominent. Relaxed confident stance. SETTING: Urban outdoor setting — residential street or parking area. Overcast or soft daylight. The white car clean and polished. Buildings softly blurred in background. COLOR PALETTE: White, cream, dark accents (tie, trousers), automotive chrome. Clean, crisp, aspirational. ART STYLE: Automotive lifestyle photography — social media influencer quality. Sharp focus on subject, natural lighting, clean composition. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F697",  // 🚗
            Color       = "#607D8B",
            SortOrder   = 235
        },
        new {
            Name        = "Tonal Studio Editorial",
            Description = "Suit and turtleneck on solid color studio with geometric block",
            Prompt      = @"Transform the subject into a sophisticated tonal studio fashion portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a textured dark grey or charcoal suit — herringbone or subtle check pattern — over a black turtleneck. Dark polished oxford shoes. Clean minimal styling. POSE: Seated on a dark geometric cube or angular block. Relaxed but commanding — one hand on knee, leaning slightly. Full-body shot showing the complete outfit and shoes. SETTING: Solid rich-colored studio backdrop — deep emerald green, forest green, or teal. Clean studio floor with the geometric block as the only prop. LIGHTING: Soft directional studio lighting creating the subject's shadow on the colored wall. Even color saturation on the backdrop. Flattering skin lighting. COLOR PALETTE: Charcoal grey suit, black turtleneck, deep green backdrop. Sophisticated tonal harmony. ART STYLE: High-end studio fashion editorial — Esquire or luxury brand campaign quality. Refined, powerful, stylish. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F7E9",  // 🟩
            Color       = "#2E7D32",
            SortOrder   = 236
        },
        new {
            Name        = "White Ring Portal",
            Description = "All-white outfit standing inside large circular ring frame in white studio",
            Prompt      = @"Transform the subject into a futuristic minimalist fashion portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in an all-white monochrome outfit — white cable-knit sweater, white cargo pants or relaxed trousers, white chunky sneakers. Clean head-to-toe white look. POSE: Standing confidently inside a large suspended circular ring or hoop — a white matte architectural ring frame (about 2 meters diameter) hanging from above. The subject centered within the ring. Arms at sides or slightly out. Full-body shot. SETTING: Clean bright white studio — white floor, white background. The circular ring is the only structural element. Bright even lighting with minimal shadows. Ultra-clean minimalist environment. LIGHTING: High-key bright studio lighting — soft and even from all directions. Minimal shadows. Clean, futuristic, fashion-forward. COLOR PALETTE: Pure white on white with subtle grey shadows for depth. Pristine, clean, futuristic. ART STYLE: Avant-garde minimalist fashion photography — high-concept editorial. Clean, bold, architectural. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\u2B55",  // ⭕
            Color       = "#EEEEEE",
            SortOrder   = 237
        },
        new {
            Name        = "Earth Tone Artisan",
            Description = "Flowing rust/terracotta outfit next to large clay vase on warm backdrop",
            Prompt      = @"Transform the subject into an earthy artisan fashion portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a flowing rust, burnt sienna, or terracotta colored ensemble — a relaxed satin or linen shirt with wide-leg matching trousers. Minimal earth-tone accessories. Suede boots or sandals. Natural relaxed elegance. POSE: Standing in a relaxed fluid pose next to a large sculptural ceramic or clay vase (about waist height). One hand touching or resting on the vase. Body slightly turned, weight on one leg. Artistic, contemplative energy. SETTING: Warm monochromatic studio backdrop in matching earth tones — sandy beige, warm tan, or terracotta. The large artisan vase as the only prop. Clean warm studio floor. LIGHTING: Soft warm directional light creating gentle shadows. Golden-hour warmth. Flattering even skin illumination. COLOR PALETTE: Rust, terracotta, burnt sienna, sandy beige, warm brown — entirely warm earth-tone monochrome. ART STYLE: Artistic fashion photography with sculptural quality. Warm, organic, gallery-exhibition feel. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3FA",  // 🏺
            Color       = "#BF360C",
            SortOrder   = 238
        },
        new {
            Name        = "Neon Pillar Studio",
            Description = "Dark suit leaning against neon-glowing pillar on black backdrop",
            Prompt      = @"Transform the subject into a dramatic neon-accented studio fashion portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a sharp dark pinstripe or charcoal suit with a black turtleneck underneath. Polished black shoes. Clean sharp tailoring. POSE: Standing tall, leaning one shoulder casually against a tall vertical neon-lit pillar or column. Full-body shot — one leg crossed in front of the other. Confident, magnetic presence. SETTING: Pure black studio backdrop. A single tall cylindrical or rectangular pillar that glows with vivid neon color — bright yellow-green, electric lime, or chartreuse. The neon pillar is the only light source accent besides the studio key light. LIGHTING: Low-key dramatic — most of the image in dark shadow. The neon pillar casting vivid colored light on the side of the subject's suit and face. A separate key light illuminating the face clearly. Strong contrast between darkness and neon glow. COLOR PALETTE: Black, dark charcoal, vivid neon yellow-green accent. Dramatic contrast. ART STYLE: High-fashion editorial with avant-garde lighting — Tom Ford campaign quality. Sleek, dramatic, unforgettable. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F7E2",  // 🟢
            Color       = "#C6FF00",
            SortOrder   = 239
        },
        new {
            Name        = "Bold Color Block Pop",
            Description = "Vibrant sweater matching studio backdrop with white cube seat",
            Prompt      = @"Transform the subject into a bold color-block fashion portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a vibrant solid-colored ribbed knit sweater — bright orange, hot pink, electric blue, or vivid red. Clean white trousers or chinos. White fashion sneakers. Black sunglasses. Minimal, clean, bold. POSE: Seated on a clean white geometric cube or block. Relaxed confident pose — elbows on knees, leaning forward slightly, or one leg extended. Full-body shot. SETTING: The studio backdrop AND floor are the SAME vivid color as the sweater — creating a monochromatic color-drenched environment. The white cube and white trousers provide crisp contrast. Everything is one bold color except the white elements. LIGHTING: Bright even studio lighting. The colored backdrop evenly saturated. Clean shadows under the cube. Pop-art level color saturation. COLOR PALETTE: One dominant vivid color (orange, pink, blue) + crisp white contrast + dark sunglasses accent. Bold, fun, eye-catching. ART STYLE: Bold pop fashion photography — Instagram-viral, social-media-optimized editorial. Vibrant, energetic, statement-making. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F7E7",  // 🟧
            Color       = "#FF6D00",
            SortOrder   = 240
        },
        new {
            Name        = "Supermarket Freeze Frame",
            Description = "Funny action pose in grocery store with flying food items",
            Prompt      = @"Transform the subject into a comedic supermarket action-freeze portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a smart-casual blazer over a dark t-shirt with jeans and sneakers. Oversized glasses or round spectacles for extra character. Surprised or dramatically shocked facial expression with wide eyes and open mouth. POSE: Dynamic mid-action freeze-frame — the subject in mid-slip or dramatic reaction pose in the middle of a supermarket aisle. One foot sliding on a banana peel, arms flailing for balance. Exaggerated comedic body language frozen in motion. SETTING: Inside a brightly lit modern grocery supermarket. Neatly stocked shelves of colorful products on both sides forming an aisle. Various food items frozen mid-air around the subject — flying vegetables (lettuce, tomatoes, peppers), fruits, groceries tumbling off shelves. The banana peel visible on the floor. LIGHTING: Bright commercial supermarket lighting — even fluorescent overhead. Clean, sharp, high-key. COLOR PALETTE: Bright supermarket colors — vivid reds, greens, yellows of produce. Clean white lighting. Fun, chaotic, colorful. ART STYLE: Hyper-realistic cinematic freeze-frame photography — like a movie still from a comedy. Sharp detail, dynamic composition, humor-filled. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F6D2",  // 🛒
            Color       = "#43A047",
            SortOrder   = 241
        },
        new {
            Name        = "Pastel Mono Studio",
            Description = "Single pastel color head-to-toe outfit on transparent chair in white studio",
            Prompt      = @"Transform the subject into a minimalist pastel monochrome fashion portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in a complete monochromatic pastel outfit — head-to-toe in a single soft pastel color (mint green, baby blue, lavender, or blush pink). Relaxed-fit linen or satin shirt, matching wide-leg trousers, and matching sneakers or loafers. Everything the same soft pastel shade. POSE: Seated elegantly on a transparent acrylic ghost chair. Relaxed open pose — hands resting on armrests or knees. Full-body shot showing the complete outfit and transparent chair. SETTING: Clean bright white studio — white floor, white background. The transparent chair is the only prop. Ultra-minimal environment. High-key bright lighting. LIGHTING: Soft bright even studio lighting from multiple angles. Minimal shadows. Clean and fresh. The pastel outfit pops against the pure white background. COLOR PALETTE: Single soft pastel color + pure white. Pristine, fresh, fashion-forward. ART STYLE: High-concept minimalist fashion editorial — editorial campaign quality. Clean, sophisticated, Instagram-ready. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F49A",  // 💚
            Color       = "#A5D6A7",
            SortOrder   = 242
        },
        new {
            Name        = "Graffiti Self-Portrait",
            Description = "Subject sitting below a graffiti street art mural of themselves",
            Prompt      = @"Transform the subject into an urban street art portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject sitting on the ground at the base of a large concrete or brick wall. ABOVE AND BEHIND them on the wall is a large vibrant graffiti/street-art mural depicting the SAME person — their face and upper body painted in bold spray-paint style with vivid colors, drips, and urban art elements. The real person below and their painted mural version above create a striking mirror effect. STYLING: Dress the subject in casual urban streetwear — a hoodie or oversized graphic tee, distressed jeans, and sneakers (red Converse or similar). The mural version wears similar clothing. POSE: Sitting on the ground against the wall — one knee up, one hand behind the head or running through hair. Looking directly at camera. Cool relaxed urban attitude. SETTING: Gritty urban alley or street. Raw concrete/brick wall covered in colorful graffiti tags and spray paint around the main mural. Street debris, dim alley lighting. LIGHTING: Moody urban lighting — warm streetlight from one side. The wall slightly darker. Cinematic urban atmosphere. COLOR PALETTE: Vibrant graffiti colors (neon greens, pinks, blues) on the mural contrasting with the real subject's more muted clothing. Urban, raw, artistic. ART STYLE: Urban street photography meets street art culture. Gritty, authentic, culturally rich. STRICT RULES: Face must remain 100% photorealistic and identity-preserved — both the real person AND the mural must clearly look like the same person. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3A8",  // 🎨
            Color       = "#E91E63",
            SortOrder   = 243
        },
        new {
            Name        = "CCTV Surveillance Style",
            Description = "Walking on crosswalk with CCTV detection boxes and surveillance overlay",
            Prompt      = @"Transform the subject into a CCTV surveillance camera footage-style image. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject walking confidently across a zebra crosswalk on a busy urban street, captured as if from a mounted CCTV security camera at a slight elevated angle. OVERLAY ELEMENTS: Digital surveillance HUD overlay on the image including: a green or blue rectangle detection box around the subject's face labeled 'IDENTITY CONFIRMED' with a 'MATCH: 98.7%%' confidence score. Additional detection boxes around clothing items labeled 'OBJECT: LEATHER JACKET', 'OBJECT: DENIM JEANS' etc. Timestamp in the corner showing 'TIME: 14:37:19' and 'DATE: 2024-10-26'. A small 'FEED: LIVE' or 'RESOLUTION: 4K' indicator in the bottom corner. STYLING: Dress the subject in a stylish street outfit — leather jacket, dark jeans, boots, sunglasses. Holding a coffee cup. SETTING: Urban city crosswalk — cars blurred in background, buildings, traffic lights. Slight surveillance camera lens distortion at edges. COLOR PALETTE: Slightly desaturated, cool-toned surveillance footage look. The detection boxes in bright green or cyan lines with white text. ART STYLE: Hyper-realistic AI surveillance aesthetic — cinematic meets tech dystopian. Sharp, modern, thought-provoking. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text with real names — only generic detection labels.",
            Category    = "Fun",
            Emoji       = "\U0001F4F9",  // 📹
            Color       = "#00BFA5",
            SortOrder   = 244
        },
        new {
            Name        = "Motorcycle Pencil Sketch",
            Description = "Black and white pencil sketch riding a sport motorcycle in city",
            Prompt      = @"Transform the subject into a detailed black and white pencil sketch riding a motorcycle. CRITICAL: Preserve the subject's EXACT facial features and identity in the sketch. ART STYLE: Highly detailed graphite pencil illustration on white paper. Fine linework with cross-hatching and shading for depth. Visible pencil strokes and paper texture. No color — pure black and white graphite. COMPOSITION: The subject seated on a sleek sport motorcycle (generic design), riding through a city street. Three-quarter view showing both the rider and the bike. The subject wearing a leather jacket, looking forward with confidence. Hair flowing slightly with wind. SETTING: Urban city backdrop rendered in detailed pencil sketch — tall buildings, lampposts, street details, all in graphite linework. The city forms a detailed but slightly lighter background behind the sharp foreground subject. An artist's signature-style mark in the bottom corner. TECHNIQUE: Varying pencil pressure — dark heavy strokes on the motorcycle and jacket for depth, lighter delicate strokes on the face for softness, fine detail work on the city background. Professional illustration quality. STRICT RULES: The subject's face must be clearly recognizable and accurately rendered in the sketch — same facial structure, features, and proportions. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3CD\uFE0F",  // 🏍️
            Color       = "#424242",
            SortOrder   = 245
        },
        new {
            Name        = "Mount Rushmore Monument",
            Description = "Subject's face carved into a granite mountain monument like Mount Rushmore",
            Prompt      = @"Transform the subject into a Mount Rushmore-style monumental portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, and proportions — the carved faces must be clearly recognizable as the person. COMPOSITION: A massive granite mountain monument carved with the subject's face — shown FOUR times at slightly different angles (front, three-quarter left, three-quarter right, profile) side by side, exactly like Mount Rushmore. Each carved face is enormous, rising from the rocky mountainside. The carvings show head and upper shoulders emerging from the raw stone. SETTING: Dramatic outdoor natural landscape — the carved mountain set against a partly cloudy blue sky with sunlight illuminating the stone faces. Rocky terrain, sparse vegetation, and desert/mountain landscape at the base. Pine trees on the hillsides. STYLE: Hyper-realistic photographic quality — the carved stone faces look like real granite with natural rock texture, weathering, and shadows in the carved recesses. The surrounding landscape is photorealistic natural scenery. Warm golden sunlight creating dramatic shadows on the stone carvings. COLOR PALETTE: Natural granite grey, warm sandstone, blue sky, green vegetation. Monumental, grand, awe-inspiring. STRICT RULES: Each carved face must clearly look like the subject — same facial structure, nose, jawline. The carving should look like real stone, not painted. No text, no names.",
            Category    = "Fun",
            Emoji       = "\U0001F5FB",  // 🗻
            Color       = "#795548",
            SortOrder   = 246
        },
        new {
            Name        = "Mirror Shatter Portrait",
            Description = "Subject surrounded by shattered mirror fragments with blue reflective lighting",
            Prompt      = @"Transform the subject into a dramatic shattered mirror portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject at the center, surrounded by large shattered mirror fragments and broken glass shards floating in the air around them. Each mirror fragment reflects parts of the subject from different angles — creating a kaleidoscopic multi-reflection effect. The fragments are large, angular, and dramatically arranged. STYLING: Dress the subject in luxurious dark clothing — an ornate dark brocade jacket or embroidered blazer. Dark sunglasses or dramatic eyewear. Statement jewelry or chains. LIGHTING: Dramatic cool-toned lighting — deep electric blue and silver. The mirror fragments catching and bouncing light creating bright specular highlights and reflections. Strong directional key light from one side. The broken glass refracting light into sparkles and flares. SETTING: Dark atmospheric void — black background with the mirror fragments and subject as the focal point. Floating shards creating depth layers. COLOR PALETTE: Electric blue, silver, chrome, deep black. Cold, dramatic, powerful. ART STYLE: High-concept fashion photography — fragmented reality aesthetic. Cinematic, visually striking, art-gallery quality. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F48E",  // 💎
            Color       = "#1565C0",
            SortOrder   = 247
        },
        new {
            Name        = "Impressionist Fire Aura",
            Description = "Vivid oil painting with fire and smoke swirling around the subject",
            Prompt      = @"Transform the subject into a vivid impressionist oil painting with dynamic fire elements. CRITICAL: Preserve the subject's EXACT facial features and identity — the face must remain photorealistic even within the painterly style. ART STYLE: Rich impasto oil painting technique with thick visible brushstrokes. Bold expressive color mixing directly on canvas. Energetic, dynamic brushwork with raw texture and movement. COMPOSITION: Close-up portrait of the subject from shoulders up. Their hair and the space around them dissolving into swirling flames, smoke, and dynamic paint strokes. Fire and warm energy emanating from and around the subject — flames licking upward from the shoulders and through the hair. Smoke wisps and ember particles scattered throughout. The face remains sharp and realistic while everything around it becomes increasingly painterly and abstract. COLOR PALETTE: Vivid warm spectrum — deep burnt orange, bright cadmium yellow, crimson red, gold, with cool blue and white accents in highlights. Rich saturated oil paint colors. The background a mix of dark smoke and bright flame. TECHNIQUE: The face rendered with fine realistic detail. Hair transitions into flowing paint strokes. Background and surroundings are fully impressionist — loose, energetic, expressive. Fire elements feel alive and dynamic. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. Maintain exact facial features. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F525",  // 🔥
            Color       = "#E65100",
            SortOrder   = 248
        },
        new {
            Name        = "Street Art Mural Face",
            Description = "Large colorful graffiti-style mural of subject's face on urban wall",
            Prompt      = @"Transform the subject into a photorealistic scene of a large street art mural on an urban wall. CRITICAL: Preserve the subject's EXACT facial features and identity — the mural face must be clearly recognizable. COMPOSITION: A large-scale colorful graffiti/street-art mural painted on the side of a building or concrete wall. The mural depicts the subject's face and upper body in vivid spray-paint style — bold outlines, vibrant colors, dripping paint, geometric patterns mixed with realistic features. The mural is enormous — covering most of the wall. SETTING: Urban street environment — the wall is part of a building on a city sidewalk. Passers-by or the subject themselves standing at the base of the wall looking up at the mural, providing scale. Street elements — trash cans, fire hydrants, urban fixtures. Daytime with natural lighting on the wall. ART STYLE (for the mural): Bold spray-paint street art — thick black outlines, vivid fills in electric blue, magenta, yellow, teal. Paint drips running down. Geometric and abstract background patterns around the realistic face. Mixed techniques — stencil, freehand, and paste-up elements. ART STYLE (overall image): Photorealistic street photography of the wall and surroundings — the mural is painted art, but the photo of it is sharp and real. COLOR PALETTE: Vibrant graffiti colors on mural, urban grey/concrete surroundings. Bold contrast. STRICT RULES: The mural face must clearly look like the subject. No text with real names. No watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F5BC\uFE0F",  // 🖼️
            Color       = "#7B1FA2",
            SortOrder   = 249
        },
        new {
            Name        = "Storm Epic Backdrop",
            Description = "Standing heroically before a dramatic tornado or storm with cinematic scale",
            Prompt      = @"Transform the subject into an epic storm backdrop portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject standing in the foreground, facing the camera, with a MASSIVE dramatic storm system behind them. A huge tornado funnel cloud descending from dark swirling storm clouds directly behind the subject. The subject appears calm and grounded while nature rages behind them. Full-body or three-quarter shot showing the subject small against the enormous scale of the storm. STYLING: Dress the subject in practical outdoor clothing — a plaid flannel shirt, jeans, and boots. Optionally holding a guitar, book, or other personal item. Hair and clothes slightly windswept from the storm winds. SETTING: Wide open flat landscape — prairie, farmland, or plains stretching to the horizon. Dramatic dark storm clouds filling the sky. The tornado funnel cloud is massive and detailed — visible debris at the base, swirling cloud structure. Lightning bolts illuminating the clouds. LIGHTING: Dramatic contrast — dark stormy sky with bright breaks of light. The subject front-lit by a break in the clouds or golden rim light from the storm edge. Cinematic atmospheric lighting. COLOR PALETTE: Dark stormy greys, deep purple-blue clouds, warm golden breaks of light, earth tones. Epic, cinematic, awe-inspiring. ART STYLE: Hyper-realistic cinematic photography — blockbuster movie poster quality. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F32A\uFE0F",  // 🌪️
            Color       = "#455A64",
            SortOrder   = 250
        },
        new {
            Name        = "Sunset Double Exposure",
            Description = "Large close-up face blended with full-body walking shot at golden sunset",
            Prompt      = @"Transform the subject into a cinematic double-exposure sunset portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: Two layered exposures blended together. LAYER 1 (large, left side): A dramatic close-up of the subject's face — sharp detailed portrait showing eyes, nose, jawline. Filling roughly half the frame. LAYER 2 (within/overlapping): A full-body shot of the SAME subject walking confidently toward the camera on an open road or path, wearing casual stylish clothing — denim jacket, jeans. This full-body figure is blended into and through the close-up face using double-exposure technique. The golden sunset sky and landscape visible through both layers. BLENDING: Seamless double-exposure merge — the walking figure visible within the silhouette and features of the close-up face. Both versions clearly the same person. Golden sunset light unifying both layers. LIGHTING: Rich golden-hour sunset — warm amber and orange tones. Backlit walking figure with sun creating lens flare and rim light. The close-up warmly illuminated. COLOR PALETTE: Golden amber, warm orange, sunset pink, deep blue sky transitioning to warm tones. Romantic, cinematic, aspirational. ART STYLE: Cinematic double-exposure photography — movie poster quality. Emotional, dramatic, visually stunning. STRICT RULES: Both versions of the subject must clearly be the SAME person with identical features. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F305",  // 🌅
            Color       = "#FF8F00",
            SortOrder   = 251
        },
        new {
            Name        = "Moonlit Blue Portrait",
            Description = "Romantic portrait in blue outfit with blue roses under moonlit night sky",
            Prompt      = @"Transform the subject into a romantic moonlit blue portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: A dreamy two-panel or double-exposure style portrait. A large close-up of the subject's face looking directly at camera with gentle, serene expression — occupying the upper portion. Below or blended: a second view of the SAME subject from behind or side profile, dressed elegantly, looking at the moonlit scene. STYLING: Dress the subject in an elegant deep blue outfit — flowing blue dress/gown or tailored blue suit as appropriate. Blue roses as accessories — in hair, at the collar, or held. Silver or pearl jewelry — jhumka earrings, delicate chain. SETTING: Romantic night scene — a large glowing full moon in a deep blue night sky. Soft clouds drifting. Blue roses and floral elements framing the composition. A serene night landscape — moonlit water, distant buildings, or garden. LIGHTING: Soft cool moonlight — silver-blue illumination. The moon providing backlighting. Gentle blue ambient glow. Romantic and dreamy. COLOR PALETTE: Deep blue, silver, moonlight white, midnight sky. Monochromatic blue palette with silver accents. Romantic, ethereal, serene. ART STYLE: Romantic fantasy portrait photography — dreamy soft-focus edges with sharp face detail. Ethereal, beautiful, emotionally evocative. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F319",  // 🌙
            Color       = "#1A237E",
            SortOrder   = 252
        },
        new {
            Name        = "Cartoon Buddy Portrait",
            Description = "Standing next to a giant famous cartoon character in matching outfit",
            Prompt      = @"Transform the subject into a fun portrait standing next to a giant cartoon character. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject standing confidently next to a large 3D-rendered cartoon bird or animal character (a generic chubby angry-looking red bird character — round body, thick eyebrows, small beak — original design, NOT any copyrighted character). The cartoon character is about the same height as the subject or slightly larger. Both facing the camera. STYLING: Dress the subject in an outfit that COLOR-MATCHES the cartoon character — if the character is red, the subject wears an all-red outfit (red blazer/jacket, red pants, red boots/shoes, red sunglasses). Head-to-toe matching color coordination between the subject and the cartoon character. SETTING: Clean solid-colored studio backdrop matching the theme — soft pastel or matching the dominant color. Clean studio floor. Well-lit, fun, commercial feel. LIGHTING: Bright, even, commercial studio lighting. Fun advertising campaign quality. Both the real subject and the 3D character evenly lit. COLOR PALETTE: Bold dominant color (red, blue, or green) shared between subject's outfit and character. Bright, playful, eye-catching. ART STYLE: Commercial product photography meets 3D character rendering. The subject is photorealistic, the cartoon character is 3D CGI rendered. Fun, viral, social-media-ready. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No copyrighted characters — use original generic designs. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F414",  // 🐔
            Color       = "#F44336",
            SortOrder   = 253
        },
        new {
            Name        = "Luxury Car Skyline Sunset",
            Description = "Leaning on luxury car with city skyline at golden sunset",
            Prompt      = @"Transform the subject into a luxury automotive lifestyle portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. STYLING: Dress the subject in smart casual — a dark navy bomber jacket or blazer over a white t-shirt, light-colored chinos or jeans, clean white sneakers. Stylish aviator sunglasses. Luxury wristwatch. Effortlessly wealthy aesthetic. POSE: Leaning casually against the side or sitting on the hood of a sleek dark luxury sports car (generic design — dark blue or black coupe with aggressive styling). One hand on the car, relaxed confident posture. Full-body shot with the car prominent. SETTING: City skyline at golden sunset — dramatic urban panorama in the background with skyscrapers silhouetted against a golden-orange sky. The scene is on an elevated viewpoint — rooftop parking, hillside road, or waterfront with the city behind. Warm golden-hour light. LIGHTING: Rich golden-hour sunset — warm amber light from the setting sun creating long shadows and warm highlights on the subject and car. The car's paint reflecting the golden sky. Cinematic lens flare. COLOR PALETTE: Deep navy, golden sunset, warm amber, dark car paint, city silhouette. Aspirational, cinematic, luxurious. ART STYLE: Cinematic automotive lifestyle photography — luxury brand campaign quality. Sharp, aspirational, movie-poster feel. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No real brand logos on the car. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F307",  // 🌇
            Color       = "#FF6F00",
            SortOrder   = 254
        },
        new {
            Name        = "Vivid Pop Close-up",
            Description = "Bold extreme close-up with vibrant colored blazer and matching gradient backdrop",
            Prompt      = @"Transform the subject into a vivid pop-art style extreme close-up portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. FRAMING: Extreme tight close-up — face from mid-forehead to chin, filling the entire frame. Slightly angled three-quarter view. Intensely close and personal. STYLING: The subject wearing a bold vivid-colored blazer or jacket — bright orange, electric blue, or hot pink — visible at the collar and shoulders. Stylish tinted sunglasses or colored-lens aviators matching the outfit tone. Well-groomed appearance. Confident, magnetic expression — slight head turn, knowing gaze. SETTING: Smooth gradient backdrop matching the outfit color — transitioning from the vivid color to a warmer or deeper shade. Completely abstract, no environment. Just pure color behind the close-up face. LIGHTING: Bright, flat, editorial lighting — minimal shadows on the face. Even illumination emphasizing skin texture and the bold color of the clothing. Pop-art level brightness and saturation. COLOR PALETTE: One dominant vivid color (orange, blue, pink) saturating the outfit and backdrop. The face and skin provide warm natural contrast. Bold, punchy, attention-grabbing. ART STYLE: Fashion magazine extreme close-up — Vogue or GQ beauty editorial quality. Bold, confident, unforgettable. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F4A5",  // 💥
            Color       = "#FF6D00",
            SortOrder   = 255
        },
        new {
            Name        = "Dual Smoke Portrait",
            Description = "Dramatic portrait with red and blue colored smoke swirling around",
            Prompt      = @"Transform the subject into a dramatic dual-colored smoke portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. FRAMING: Close-up to mid-shot portrait — face and upper body. The subject looking directly at camera with an intense, penetrating gaze. STYLING: Dress the subject in a simple dark outfit — black t-shirt or dark jacket. Minimal styling so the smoke effect takes center stage. Natural hair, possibly slightly tousled. SMOKE EFFECT: Dense colored smoke billowing around the subject from both sides. LEFT SIDE: Rich deep red/crimson smoke curling and swirling around the left side of the face and body. RIGHT SIDE: Deep electric blue/cobalt smoke mirroring on the right side. The two colors meeting and mixing subtly behind the head — creating purple where they blend. The smoke is thick, volumetric, and atmospheric — like real smoke-bomb photography. SETTING: Pure dark/black background. The colored smoke provides all the visual atmosphere. No other environment elements. LIGHTING: The subject's face clearly lit by a frontal key light — face sharp and well-exposed. The smoke is backlit or side-lit to reveal its swirling texture and color density. Dramatic contrast between the lit face and the dark smoky atmosphere. COLOR PALETTE: Deep crimson red, electric blue, purple blend, dark black background. Dramatic, moody, cinematic. ART STYLE: Cinematic smoke-bomb portrait photography. Moody, atmospheric, visually striking. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No face swap, no beautification, no skin smoothing, no reshaping. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F32B\uFE0F",  // 🌫️
            Color       = "#6A1B9A",
            SortOrder   = 256
        },
        new {
            Name        = "Retro Rapper Ink Art",
            Description = "Red and black ink illustration in retro hip-hop style with boombox",
            Prompt      = @"Transform the subject into a retro hip-hop ink art illustration. CRITICAL: Preserve the subject's EXACT facial features and identity — the illustration must be clearly recognizable as the person. ART STYLE: Bold ink illustration using only RED and BLACK on white paper. Thick confident ink linework — heavy black outlines with red fill for accent areas. No other colors. Graphic novel / comic art quality with high-contrast bold strokes. Slight halftone dot pattern in shaded areas. COMPOSITION: The subject in a dynamic hip-hop pose — pointing at the camera or gesturing with one hand, the other arm holding or resting on a large retro boombox/ghetto blaster. Upper body and face prominent. Confident, commanding rapper energy. STYLING: Draw the subject wearing a hoodie or bomber jacket with the hood partially up. Chunky chain necklace. Dynamic confident expression. The boombox is large and detailed with speakers, dials, and antenna. TECHNIQUE: Bold black ink lines for outlines and shadows. Solid red fills for key elements — parts of clothing, boombox details, background accent shapes. White negative space used effectively. Splatter and ink-drip effects for raw energy. An artist's small signature in the corner. BACKGROUND: Minimal — white with red and black geometric shapes, splatter marks, or abstract urban elements. Not busy — the subject is the focus. STRICT RULES: The subject's face must be clearly recognizable. No text with real names. No watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3A4",  // 🎤
            Color       = "#C62828",
            SortOrder   = 257
        },
        new {
            Name        = "Chess Master Fisheye",
            Description = "Creative wide-angle fisheye shot playing chess with dramatic perspective",
            Prompt      = @"Transform the subject into a creative fisheye chess portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. CAMERA EFFECT: Shot with an extreme wide-angle or fisheye lens creating dramatic barrel distortion — the center of the image (hands/chess pieces) appears enlarged and close, while the edges curve away. Creative perspective distortion making the composition dynamic and immersive. COMPOSITION: The subject seated at a chess board, reaching forward with one hand to move or place a chess piece — the hand and chess piece are in the extreme foreground, appearing large due to the wide-angle distortion. The subject's face is behind/above, slightly distorted by the lens but still clearly recognizable. The chess board stretches across the lower frame. STYLING: Dress the subject in a casual open-collar white linen shirt, relaxed and intellectual. Slight stubble, focused intense expression — the strategist at work. SETTING: A clean modern room — soft grey or white walls. The chess board on a table with both black and white pieces in mid-game position. Large chess pieces in the foreground (black queen, king) appear dramatic due to the lens. LIGHTING: Soft overhead studio light with dramatic shadows on the chess pieces. Clean, intellectual atmosphere. COLOR PALETTE: Black and white chess pieces, warm skin tones, clean grey-white room. Sharp, intellectual, dramatic. ART STYLE: Creative perspective fashion/lifestyle photography with unusual lens choice. Visually engaging, artistically bold, social-media-stopping. STRICT RULES: Face must remain recognizable despite the lens distortion. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\u265F\uFE0F",  // ♟️
            Color       = "#37474F",
            SortOrder   = 258
        },
        new {
            Name        = "Luxury Convertible Drive",
            Description = "Sitting in luxury convertible on city street with cinematic 35mm look",
            Prompt      = @"Transform the subject into a cinematic luxury convertible driving portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject seated in the driver's seat of a luxury convertible sports car (generic design — sleek dark or silver open-top roadster) with the top down. Shot from outside the car at driver's side, slightly above — showing the subject, steering wheel, and the car's sleek interior. STYLING: Dress the subject in a fitted black t-shirt or dark casual top. Hands on the leather steering wheel. A luxury chronograph watch visible. Stylish aviator sunglasses. Wind-tousled hair. Natural confident driving expression — looking slightly toward camera or forward. SETTING: Urban city street — buildings and parked cars softly blurred in the background. City life happening around the car. Daytime with slightly overcast or soft natural light. The car parked or slowly moving on a tree-lined city road. CAMERA STYLE: Cinematic 35mm film aesthetic — subtle film grain, rich color depth, natural lens bokeh. Shallow depth of field — subject and car interior sharp, background creamy smooth. COLOR PALETTE: Dark tones — black, charcoal, leather brown, urban grey. Natural desaturated color grading with warm skin tones. Cinematic, classic, timeless. ART STYLE: Cinematic 35mm street photography — automotive magazine quality. Cool, effortless, aspirational. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No real brand logos. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3CE\uFE0F",  // 🏎️
            Color       = "#424242",
            SortOrder   = 259
        },
        new {
            Name        = "Giant 3D Letter Sculpture",
            Description = "Sitting casually in front of monumental 3D concrete letter sculpture",
            Prompt      = @"Transform the subject into a creative portrait with monumental 3D typography. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject sitting casually on the ground at the base of MASSIVE 3D concrete or stone letters/numbers that tower behind them. The letters are monumental — each one 3-4 meters tall, casting dramatic shadows. They spell a short word like 'KING', 'BOSS', 'STAR', or the subject's initials — use generic aspirational text. The subject is small relative to the giant letters, creating dramatic scale contrast. STYLING: Dress the subject in smart casual — crisp white shirt or polo, dark jeans, clean sneakers. Holding a single red rose or small prop for visual interest. Relaxed, contemplative pose — sitting cross-legged or with knees up. SETTING: Outdoor urban park or plaza. The 3D letters are raw concrete or weathered stone sculptures sitting on pavement. Trees and urban landscape softly visible in the background. Natural daylight — warm afternoon sun. LIGHTING: Natural golden-hour light casting long dramatic shadows from the giant letters. Warm, cinematic, monumental. COLOR PALETTE: Concrete grey, warm skin tones, white shirt, natural greens and sky. Clean, bold, architectural. ART STYLE: Creative architectural photography — scale play between human and massive sculpture. Cinematic, Instagram-worthy, visually impactful. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No real names in the text — use generic words. No watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F524",  // 🔤
            Color       = "#757575",
            SortOrder   = 260
        },
        new {
            Name        = "Airport Runway Travel",
            Description = "Standing on airport tarmac with airplane in background travel lifestyle",
            Prompt      = @"Transform the subject into a cinematic airport runway travel portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject standing confidently on an airport tarmac/runway with a large commercial passenger airplane directly behind them. Full-body shot — the subject in the center foreground, the aircraft's nose and fuselage filling the background. STYLING: Dress the subject in a sleek all-black travel outfit — black fitted t-shirt or polo, black joggers or slim pants, clean black sneakers. Dark aviator sunglasses. Pulling a black rolling carry-on suitcase with one hand. Confident power-walk stance as if just disembarked. SETTING: Airport tarmac — wide concrete runway or apron. The airplane is large (generic white commercial jet with a generic airline livery — no real airline branding). Airport ground vehicles, safety-vested crew members slightly visible in the background. Open sky. LIGHTING: Bright overcast daylight — even exposure across the scene. The white airplane reflecting soft daylight. Clean, crisp outdoor lighting. COLOR PALETTE: Black outfit, white airplane, grey tarmac, blue-grey sky. Clean, minimal, jet-setter. ART STYLE: Cinematic travel lifestyle photography — luxury travel magazine or social media influencer quality. Aspirational, powerful, world-traveler energy. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No real airline logos or names. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\u2708\uFE0F",  // ✈️
            Color       = "#546E7A",
            SortOrder   = 261
        },
        new {
            Name        = "Alien Bar Buddy",
            Description = "Cheersing drinks with a realistic alien at a dimly lit bar",
            Prompt      = @"Transform the subject into a fun sci-fi bar scene with an alien. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject and a realistic-looking alien creature sitting side by side at a bar counter, clinking glasses in a cheers toast. Both facing the camera in a casual buddy-photo pose. The subject smiling or grinning naturally. The alien friendly and relaxed — mirroring the subject's casual energy. THE ALIEN: A detailed realistic extraterrestrial — smooth grey-green skin, large dark almond eyes, no hair, elongated head. Wearing a casual outfit in a contrasting color to the subject (if subject wears blue, alien wears yellow or vice versa — like matching tracksuits or casual jackets with stripes). The alien has a friendly non-threatening expression. SETTING: A dimly lit atmospheric bar or pub — wooden bar counter, shelves of bottles and glasses behind, warm ambient bar lighting. Other bar patrons subtly visible in the background, some looking surprised or unfazed. STYLING: Dress the subject in a casual tracksuit or sporty jacket (e.g., blue with white stripes). Both holding beer glasses or cocktails. LIGHTING: Warm dim bar lighting — amber overhead lights, soft shadows, cozy atmospheric glow. COLOR PALETTE: Warm amber bar tones, blue and yellow outfit contrast, grey-green alien skin. Fun, warm, comedic. ART STYLE: Hyper-realistic cinematic still — like a scene from a sci-fi comedy film. The alien looks realistic (not cartoonish), the setting is photorealistic, creating a believable impossible scene. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F47D",  // 👽
            Color       = "#558B2F",
            SortOrder   = 262
        },
        new {
            Name        = "Supercar Bird Eye View",
            Description = "Stepping out of exotic sports car shot from dramatic top-down angle",
            Prompt      = @"Transform the subject into a dramatic top-down supercar portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. CAMERA ANGLE: Dramatic bird's-eye / top-down perspective — the camera looking straight down or at a steep angle from above. COMPOSITION: The subject stepping out of or sitting in a vibrant exotic sports car with the door open (gull-wing or scissor door lifted up). Shot from above showing the car's sleek roof, the open door, and the subject emerging. The car is bright and eye-catching — vivid red, yellow, or orange. STYLING: Dress the subject in casual smart summer wear — fitted polo or casual shirt, shorts or chinos, clean white sneakers. Aviator sunglasses. Relaxed confident posture — one foot on the ground stepping out, looking up toward the camera. SETTING: Clean bright environment from above — smooth concrete, a driveway, or a luxury hotel entrance. The car parked on clean pavement. Bright daylight from above creating short shadows. LIGHTING: Bright overhead natural sunlight — the top-down angle maximizes the sun's direct illumination. Clean bright exposure. The car's paint gleaming in the light. COLOR PALETTE: Vivid car color (red/yellow/orange), clean white/grey pavement, bright natural tones. Bold, luxury, aspirational. ART STYLE: Creative drone/overhead automotive photography — luxury lifestyle content. Unique angle, visually striking, attention-grabbing. STRICT RULES: Face must remain recognizable from the overhead angle. No real brand logos. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3CE\uFE0F",  // 🏎️
            Color       = "#D50000",
            SortOrder   = 263
        },
        new {
            Name        = "Rugged Truck Boss",
            Description = "All-black outfit standing in front of lifted off-road truck dramatic pose",
            Prompt      = @"Transform the subject into a rugged truck boss portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. COMPOSITION: The subject standing confidently in front of a large lifted off-road truck or pickup. Full-body shot — the subject centered, the massive truck looming behind and above them. The truck should be imposing — lifted suspension, big off-road tires, LED light bars, aggressive front grille. STYLING: Dress the subject in an all-black power outfit — black v-neck sweater or pullover, white dress shirt collar visible underneath, slim black trousers, polished black shoes. Dark aviator sunglasses. A luxury watch. One hand in pocket or thumbs in belt loops. Alpha confident stance. SETTING: Open outdoor environment — dirt road, rural area, or desert/farmland setting. Overcast dramatic sky — heavy grey clouds creating moody atmosphere. The truck parked on dirt or gravel. Dust or haze in the air. LIGHTING: Overcast dramatic — soft diffused light from the grey sky. The truck's LED lights glowing. Moody, powerful atmosphere. The subject well-lit despite the overcast sky. COLOR PALETTE: All black outfit, matte black truck, grey sky, brown dirt. Dark, powerful, commanding. ART STYLE: Automotive lifestyle photography — truck/SUV advertisement quality. Masculine, powerful, aspirational. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No real brand logos on the truck. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F6FB",  // 🛻
            Color       = "#263238",
            SortOrder   = 264
        },
        new {
            Name        = "Color Match Auto",
            Description = "Outfit perfectly color-matched to a sports car for bold fashion-auto fusion",
            Prompt      = @"Transform the subject into a bold color-matched automotive fashion portrait. CRITICAL: Preserve the subject's EXACT facial features, identity, skin tone, and expression — absolutely no modifications to the face. KEY CONCEPT: The subject's ENTIRE outfit perfectly matches the color of the car behind them — creating a bold monochromatic fashion-meets-automotive visual. If the car is red, the outfit is entirely red. If blue, entirely blue. One striking dominant color shared between human and machine. STYLING: Dress the subject in a head-to-toe outfit in one vivid color — a fitted v-neck sweater or knit polo, matching or complementary trousers. Dark sunglasses for contrast. One hand adjusting sunglasses or touching hair. Confident fashion-forward stance. COMPOSITION: The subject standing directly in front of a low-slung sports car (generic aggressive coupe design). The car and outfit are the SAME vivid color. Full-body or three-quarter shot showing the color coordination. The car's grille, headlights, and front clearly visible behind the subject. SETTING: Clean simple background — urban parking area or studio-like clean environment. The focus is entirely on the color-matched subject and car combo. LIGHTING: Bright clean lighting emphasizing the vivid color saturation. Both the car paint and outfit fabric catching light beautifully. Sharp, commercial, editorial. COLOR PALETTE: One dominant vivid color (red, blue, orange, green) shared by outfit and car + dark sunglasses + clean background. Bold, striking, scroll-stopping. ART STYLE: Automotive fashion photography — luxury brand x car collaboration campaign quality. Bold, creative, visually impactful. STRICT RULES: Face must remain 100% photorealistic and identity-preserved. No real brand logos. No text, no names, no watermarks.",
            Category    = "Fun",
            Emoji       = "\U0001F3A8",  // 🎨
            Color       = "#D32F2F",
            SortOrder   = 265
        },
        // ── Add future styles here as new entries in this array ──
    };

    foreach (var s in incrementalStyles)
    {
        try
        {
            // Force-clear from DeletedStyleSeeds so the style always appears
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM DeletedStyleSeeds WHERE Name = @p0", s.Name);

            // Insert if not already present; update if it exists (prompt may have changed)
            await db.Database.ExecuteSqlRawAsync($@"
                IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
                    INSERT INTO StylePresets
                        (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
                    VALUES
                        (@p0, @p1, @p2, @p3, @p4, @p5, 1, @p6)
                ELSE
                    UPDATE StylePresets SET
                        Description   = @p1,
                        PromptTemplate = @p2,
                        Category      = @p3,
                        IconEmoji     = @p4,
                        AccentColor   = @p5,
                        SortOrder     = @p6
                    WHERE Name = @p0",
                s.Name, s.Description, s.Prompt, s.Category, s.Emoji, s.Color, s.SortOrder);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Incremental style seed '{Name}' failed (non-fatal)", s.Name);
        }
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

// ── Clone Protection Layer 1: Tamper detection (checks binary integrity) ──
var tamperError = TamperDetectionService.VerifyIntegrity(app.Environment.IsProduction());
if (tamperError != null)
{
    if (app.Environment.IsProduction())
    {
        app.Logger.LogCritical("TAMPER DETECTED: {Error}", tamperError);
        Console.Error.WriteLine($"FATAL: Application integrity compromised.\n{tamperError}");
        Environment.Exit(1);
    }
    else
    {
        app.Logger.LogWarning("Integrity check (dev mode): {Warning}", tamperError);
    }
}

// ── Clone Protection Layer 2: Anti-debug/anti-decompilation scan ──
var securityScan = AntiTamperService.PerformSecurityScan(app.Environment.IsProduction());
if (securityScan != null)
{
    if (app.Environment.IsProduction())
    {
        app.Logger.LogCritical("SECURITY THREAT: {Threat}", securityScan);
        Console.Error.WriteLine($"FATAL: Security threat detected.\n{securityScan}");
        Environment.Exit(1);
    }
    else
    {
        app.Logger.LogWarning("Security scan (dev mode): {Warning}", securityScan);
    }
}

// ── Clone Protection Layer 3: Online license activation (phone-home) ──
{
    var licenseService = app.Services.GetRequiredService<LicenseService>();
    var offlineResult = licenseService.Validate();
    if (offlineResult.IsValid && offlineResult.License != null)
    {
        var onlineService = app.Services.GetRequiredService<OnlineLicenseValidationService>();
        var onlineResult = await onlineService.ActivateAsync(
            offlineResult.License.LicenseId,
            offlineResult.License.HardwareId);

        if (!onlineResult.IsAllowed)
        {
            app.Logger.LogCritical("ONLINE LICENSE CHECK FAILED: {Message}", onlineResult.Message);
            if (app.Environment.IsProduction())
            {
                Console.Error.WriteLine($"FATAL: License rejected by server.\n{onlineResult.Message}");
                Environment.Exit(1);
            }
        }
        else
        {
            app.Logger.LogInformation("License activated: {Status} — {Message}",
                onlineResult.ValidationStatus, onlineResult.Message);
        }
    }
}

// ── Clone Protection Layer 4: License + Domain enforcement middleware (every request) ──
app.UseMiddleware<LicenseMiddleware>();

// ── License info endpoint (available even without valid license) ──
app.MapGet("/api/license-info", () =>
{
    var hwid = HardwareFingerprintService.GetFingerprint();
    return Results.Ok(new { HardwareId = hwid, ShortId = hwid[..16] });
});

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

// Helper for preset thumbnail migration query
public class ThumbnailRow { public int Id { get; set; } public string ThumbnailPath { get; set; } = ""; }
