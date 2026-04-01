# Video Creator — Design Spec

## Overview

A new tool page at `/video-creator` that generates short video clips (5-8 seconds) using Google's Veo 2 API. Three input modes: Text-to-Video, Image-to-Video, and Video-to-Video. Costs **5 coins** per generation.

## User Flow

1. User selects a mode (Text-to-Video, Image-to-Video, or Video-to-Video)
2. Uploads a reference image or video (if applicable)
3. Writes a creative prompt describing the desired video (e.g., "the kid is fighting alongside Avengers in a destroyed city")
4. Selects aspect ratio and duration
5. Clicks Generate (5 coins debited)
6. Sees a progress timer while video generates (1-3 minutes)
7. Video auto-plays in a looping player on completion
8. Downloads as MP4

## Modes

### Text-to-Video
- No upload required
- Prompt only → generates video from scratch
- Good for: creating scenes, animations, abstract visuals

### Image-to-Video
- Upload a single image (.jpg, .png, .webp — max 10 MB)
- Image serves as the starting frame / visual reference
- Prompt describes what should happen in the video
- Good for: animating photos, creating scenes from portraits

### Video-to-Video
- Upload a short video clip (.mp4, .webm — max 10 seconds, max 20 MB)
- Two sub-modes via prompt:
  - **Transform**: Apply style/scene changes to the existing video
  - **Reference**: Use the video as inspiration for a new generation
- Good for: style transfer on video, re-imagining footage

## Page Layout

Follows the existing two-column pattern (EmotionTransfer, FaceAging).

### Left Pane — Controls

```
[Mode Tabs: Text-to-Video | Image-to-Video | Video-to-Video]

[Upload Area]                    ← hidden for Text-to-Video mode
  - Image mode: drag-drop or click, accepts .jpg/.png/.webp
  - Video mode: drag-drop or click, accepts .mp4/.webm
  - Shows thumbnail preview after upload
  - Shows uploaded video player for video mode

[Prompt Input]                   ← textarea, required
  Placeholder: "Describe what should happen in the video..."

[Settings Row]
  Aspect Ratio: [16:9 ▼]  [9:16]  [1:1]
  Duration:     [5s ▼]     [8s]

[Generate Video (5 coins)]       ← disabled until prompt filled
```

### Right Pane — Result

```
[Video Player]                   ← <video> tag, autoplay, loop, controls
  - Shows generated video on completion
  - Placeholder message before generation

[Download MP4]                   ← button, appears after generation

[Generation History]             ← session-only list of previous results
  - Thumbnail + prompt snippet for each
  - Click to re-display in main player
```

### Processing State

When generating (replaces right pane content):
```
[Generating your video...]
[Elapsed: 0:45 / Estimated: 1-3 min]
[Progress animation — pulsing gradient bar]
[Cancel button]
```

## Backend Architecture

### New Service: GeminiVideoService

**Interface:** `IGeminiVideoService`

```csharp
public interface IGeminiVideoService
{
    // Text-to-Video
    Task<byte[]> GenerateVideoAsync(
        string prompt, string aspectRatio, int durationSeconds,
        CancellationToken ct = default);

    // Image-to-Video
    Task<byte[]> GenerateVideoFromImageAsync(
        string prompt, byte[] imageData, string imageMimeType,
        string aspectRatio, int durationSeconds,
        CancellationToken ct = default);

    // Video-to-Video
    Task<byte[]> GenerateVideoFromVideoAsync(
        string prompt, byte[] videoData, string videoMimeType,
        string aspectRatio, int durationSeconds,
        CancellationToken ct = default);
}
```

**Implementation:** `GeminiVideoService`

- Uses `HttpClient` (consistent with `GeminiImageService`)
- Reads `GeminiOptions` for API key
- Model: `veo-2.0-generate-001` (configurable via `GeminiOptions.VideoModel`)
- API endpoint: `POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateVideos?key={apiKey}`

**Async Operation Flow:**

1. Submit generation request → receive operation name
2. Poll `GET https://generativelanguage.googleapis.com/v1beta/{operationName}?key={apiKey}` every 5 seconds
3. When `done: true`, extract video bytes from response
4. Support `CancellationToken` for user-initiated cancellation
5. Timeout after 5 minutes (safety limit)

**Request Payload (Image-to-Video example):**
```json
{
  "instances": [{
    "prompt": "the kid is fighting alongside Avengers",
    "image": {
      "bytesBase64Encoded": "<base64>",
      "mimeType": "image/png"
    }
  }],
  "config": {
    "aspectRatio": "16:9",
    "durationSeconds": 8,
    "numberOfVideos": 1
  }
}
```

**Response Parsing:**
- Poll response has `done` boolean and `response.generatedVideos[]`
- Each generated video has `video.bytesBase64Encoded` containing MP4 data
- Decode base64 → byte array → save to storage

### New Page: VideoCreator.razor

- Route: `@page "/video-creator"`
- Authorization: `@attribute [Authorize]`
- Follows EmotionTransfer.razor structure

**Injected Services:**
- `IGeminiVideoService` — video generation
- `IImageStorageService` — save uploads and results
- `ICoinService` — coin debit
- `ISubscriptionService` — feature access check
- `IJSRuntime` — download trigger
- `AuthenticationStateProvider` — user identity

**State Variables:**
- `_selectedMode` — enum: TextToVideo, ImageToVideo, VideoToVideo
- `_prompt` — user's text prompt
- `_aspectRatio` — "16:9", "9:16", "1:1"
- `_durationSeconds` — 5 or 8
- `_uploadedFilePath` — relative path of uploaded file
- `_uploadedDataUrl` — base64 data URL for preview
- `_isGenerating` — processing flag
- `_elapsedSeconds` — timer for progress display
- `_generatedVideoUrl` — data URL of result video
- `_generatedVideoBytes` — raw bytes for download
- `_sessionHistory` — list of previous results in this session
- `_errorMessage` — user-facing error text
- `_cancellationTokenSource` — for cancel button

**Key Methods:**
- `HandleFileUpload()` — validate and save uploaded image/video
- `GenerateVideo()` — debit coins, call service, update UI
- `StartElapsedTimer()` — increment `_elapsedSeconds` every second via `Timer`
- `CancelGeneration()` — cancel the CTS
- `DownloadVideo()` — trigger MP4 download via JS interop

### Video File Storage

- Generated videos saved to `wwwroot/generated/` as `{guid}.mp4`
- Served as data URLs (base64) for the video player — same pattern as images
- For large videos, consider switching to direct file serving via relative URL if base64 becomes too large (future optimization)

## Changes to Existing Files

### FeatureAccess.cs
- Add `public const string VideoCreator = "VideoCreator";`
- Add to `GenerationCosts`: `[VideoCreator] = 5`
- Add to `Pro` and `Enterprise` plan arrays
- Add to `AllFeatures` array

### NavMenu.razor
- Add nav item: `new("VideoCreator", "video-creator", "&#127909;", "Video Creator")`
- Add to `DescriptionToFeatureKey` mapping

### Program.cs
- Register `IGeminiVideoService` / `GeminiVideoService` as HttpClient with 360-second timeout (videos take longer)

### GeminiOptions.cs
- Add `public string VideoModel { get; set; } = "veo-2.0-generate-001";`

### appsettings.json
- Add `"VideoModel": "veo-2.0-generate-001"` under `Gemini` section

## New Files

| File | Purpose |
|------|---------|
| `Components/Pages/VideoCreator.razor` | Page component with UI + code-behind |
| `Services/IGeminiVideoService.cs` | Service interface |
| `Services/GeminiVideoService.cs` | Veo 2 API integration with async polling |
| `wwwroot/css/video-creator.css` | Tool-specific styles |

## Error Handling

- **Insufficient coins**: Show error, don't call API
- **No subscription access**: Redirect to pricing
- **Upload too large**: Show size limit error
- **Video too long** (Video-to-Video): Show "max 10 seconds" error
- **API failure**: Log error, show user-friendly message, refund coins (since generation didn't complete)
- **Timeout** (>5 min): Cancel operation, show timeout message, refund coins
- **Safety filter block**: Show "content blocked" message with suggestion to rephrase
- **User cancellation**: Cancel CTS, show "cancelled" message, refund coins

## Coin Refund Policy

If generation fails for any reason (API error, timeout, safety block, user cancel), coins are refunded. Only successful generations that produce a downloadable video keep the 5-coin charge.

## Future Enhancements (Not in scope)

- Multi-clip concatenation for longer videos
- Audio/music overlay
- Video upscaling (1080p)
- Generation history persistence (database)
