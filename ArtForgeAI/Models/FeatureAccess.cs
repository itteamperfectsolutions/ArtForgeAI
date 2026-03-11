namespace ArtForgeAI.Models;

public static class FeatureAccess
{
    // Feature key constants matching page routes
    public const string QuickStyle = "QuickStyle";
    public const string Home = "Home";
    public const string StyleTransfer = "StyleTransfer";
    public const string Gallery = "Gallery";
    public const string MosaicPoster = "MosaicPoster";
    public const string PassportPhoto = "PassportPhoto";
    public const string ImageViewer = "ImageViewer";
    public const string Settings = "Settings";
    public const string PhotoExpand = "PhotoExpand";
    public const string GangSheet = "GangSheet";
    public const string ShapeCutSheet = "ShapeCutSheet";
    public const string StyleRemix = "StyleRemix";
    public const string NegativeScan = "NegativeScan";
    public const string AutoEnhance = "AutoEnhance";

    /// <summary>Plan name to allowed features mapping</summary>
    public static readonly Dictionary<string, string[]> PlanFeatures = new()
    {
        ["Free"] = [QuickStyle],
        ["Starter"] = [QuickStyle, Home, StyleTransfer, StyleRemix, Gallery],
        ["Pro"] = [QuickStyle, Home, StyleTransfer, StyleRemix, Gallery, MosaicPoster, PassportPhoto, ImageViewer, Settings, NegativeScan, AutoEnhance],
        ["Enterprise"] = [QuickStyle, Home, StyleTransfer, StyleRemix, Gallery, MosaicPoster, PassportPhoto, ImageViewer, Settings, PhotoExpand, GangSheet, ShapeCutSheet, NegativeScan, AutoEnhance]
    };

    /// <summary>Coin cost per generation for each feature</summary>
    public static readonly Dictionary<string, int> GenerationCosts = new()
    {
        [QuickStyle] = 2,
        [Home] = 3,
        [StyleTransfer] = 3,
        [PassportPhoto] = 2,
        [MosaicPoster] = 5,
        [PhotoExpand] = 3,
        [GangSheet] = 4,
        [ShapeCutSheet] = 4,
        [StyleRemix] = 3,
        [NegativeScan] = 2,
        [AutoEnhance] = 3
    };

    /// <summary>All feature keys</summary>
    public static readonly string[] AllFeatures =
    [
        QuickStyle, Home, StyleTransfer, StyleRemix, Gallery, MosaicPoster,
        PassportPhoto, ImageViewer, Settings, PhotoExpand, GangSheet, ShapeCutSheet, NegativeScan, AutoEnhance
    ];

    public static int GetCost(string featureKey) =>
        GenerationCosts.TryGetValue(featureKey, out var cost) ? cost : 0;
}
