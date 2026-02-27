using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IFaceCorrectionService
{
    Task<FacePoseAnalysis> AnalyzeFacePoseAsync(byte[] imageData, string mimeType);
    Task<byte[]> CorrectFaceAsync(string sourcePath);
}
