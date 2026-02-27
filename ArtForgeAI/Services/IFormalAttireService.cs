namespace ArtForgeAI.Services;

public interface IFormalAttireService
{
    Task<byte[]> ApplyFormalAttireAsync(
        string sourcePath, string backgroundColor = "#FFFFFF",
        string suitColor = "auto", string tieColor = "auto");
}
