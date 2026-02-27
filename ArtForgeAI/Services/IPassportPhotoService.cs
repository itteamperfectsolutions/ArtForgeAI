using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IPassportPhotoService
{
    Task<FaceDetectionResult> DetectFaceAsync(string sourcePath);

    Task<byte[]> CropToPassportSizeAsync(
        string sourcePath, string backgroundColor, CropRectFractions cropRect);

    Task<byte[]> GenerateMultiUpSheetAsync(
        byte[] photoBytes, PaperSize paperSize, double spacingMm, double marginMm,
        bool cutMarks, bool cropMarks, bool landscape = false);
}
