# Face Aging & De-aging Tool — Design Spec

## Overview

A tool that transforms a uploaded portrait to show how a person would look at different ages. Supports both single age transforms (before/after) and full timeline generation (all age stages at once).

## Age Stages

Five fixed presets plus a custom slider:

| Preset | Label | Target Age |
|--------|-------|------------|
| Child | Child | ~5 |
| Teen | Teen | ~15 |
| Young Adult | Young Adult | ~25 |
| Middle-aged | Middle-aged | ~45 |
| Senior | Senior | ~70 |

Custom slider range: 5–80 years.

## User Flow

### Upload & Validation
1. User uploads a portrait photo (same upload pattern as PassportPhoto)
2. ONNX background removal service runs face detection (`RemoveBackgroundWithBoundsAsync`) to confirm a face is present
3. If no face detected, show error: "No face detected. Please upload a clear portrait photo."
4. On successful upload, debit **2 coins** (feature key: `FaceAging`, description: "Face Aging generation")
5. Show the uploaded photo in the left preview area

### Single Age Transform
1. User selects a preset button or adjusts the custom age slider
2. User clicks "Transform" button
3. Call Gemini `EditImageAsync` with the uploaded photo as reference and an age-specific prompt
4. Show before/after comparison modal (same pattern as PassportPhoto AI adjust):
   - Side-by-side: Original | Aged result
   - "Accept" and "Reject" buttons
5. On accept: show result in preview, enable download
6. On reject: dismiss modal, user can try a different age

### Timeline Generation
1. User clicks "Generate Timeline" button
2. Debit **5 coins** (description: "Face Aging Timeline")
3. Fire 5 parallel Gemini `EditImageAsync` calls — one per preset age stage
4. Show progress indicator with count (e.g., "Generating 3/5...")
5. Display results in a horizontal scrollable strip: Child → Teen → Young Adult → Middle-aged → Senior
6. Each stage labeled with its age name
7. User can click any stage to see it enlarged
8. Download options: individual stage PNG or full timeline strip PNG

### Gemini Prompt Strategy

Single transform prompt template:
```
Transform this person's face to look like they are approximately {targetAge} years old.
Maintain the person's identity, facial features, and ethnicity.
Only change age-related features (skin texture, wrinkles, facial structure, hair color/style).
Keep the same pose, expression, lighting, and background.
The result should look natural and photorealistic.
```

Timeline uses the same prompt template per stage, fired in parallel.

## UI Layout

Two-column layout (matching existing tool patterns):

### Left Column — Controls Card
- **Upload area** (compact style with camera icon)
- **Age Presets section** — 5 styled buttons in a row (Child, Teen, Young Adult, Middle-aged, Senior)
- **Custom Age section** — slider (5–80) with numeric display
- **"Transform" button** — primary action, transforms to selected age
- **Divider**
- **"Generate Timeline" button** — secondary action, generates all 5 stages
- **Cost info** — "Transform: 2 coins | Timeline: 5 coins"

### Right Column — Preview/Results
- **Before upload:** empty state with placeholder text
- **After upload:** shows uploaded photo
- **After single transform:** shows accepted result with download button
- **After timeline:** horizontal scrollable strip with all 5 stages, download buttons for individual or full strip

### Comparison Modal (overlay)
- Side-by-side original vs result
- Accept / Reject buttons
- Same z-index and styling as PassportPhoto comparison modal

## Architecture

### Files to Create

1. **`Components/Pages/FaceAging.razor`** — Blazor page component
   - Route: `/face-aging`
   - Injects: `ICoinService`, `ISubscriptionService`, `IImageGenerationService`, `OnnxBgRemovalService`, `IImageStorageService`
   - Feature access check in `OnInitializedAsync` → redirect to `/pricing` if no access
   - Upload handler with coin debit
   - Single transform method calling Gemini
   - Timeline generation method with parallel `Task.WhenAll`
   - Before/after modal state management

2. **`wwwroot/js/face-aging.js`** — JS module (IIFE pattern)
   - `loadImage(containerId, dataUrl)` — display uploaded photo
   - `showComparison(originalDataUrl, resultDataUrl)` — before/after rendering
   - `renderTimeline(canvasId, imageDataUrls[], labels[])` — horizontal strip on canvas
   - `exportTimelineDataUrl()` — export full strip as PNG
   - `exportStageDataUrl(index)` — export individual stage
   - `dispose()` — cleanup

3. **`wwwroot/css/face-aging.css`** — Tool-specific styles
   - Age preset button styles (pill buttons with active state)
   - Custom age slider styling
   - Timeline strip layout (horizontal scroll, responsive)
   - Comparison modal styling
   - Processing overlay

### Files to Modify

4. **`Models/FeatureAccess.cs`**
   - Add `public const string FaceAging = "FaceAging";`
   - Add to `Pro` and `Enterprise` plan feature lists
   - Add to `GenerationCosts`: `{"FaceAging", 2}`
   - Add to `AllFeatures` list

5. **`Components/Layout/NavMenu.razor`**
   - Add `FaceAging` entry to `AllFeatureNavItems` array
   - Icon: aging-related emoji (e.g., hourglass `&#9203;`)
   - Label: "Face Aging"
   - Href: `/face-aging`
   - Add to `DescriptionToFeatureKey` mapping: `"Face Aging" => FaceAging`

## Coin Costs

| Action | Cost | Description |
|--------|------|-------------|
| Single transform | 2 coins | "Face Aging generation" |
| Timeline (5 stages) | 5 coins | "Face Aging Timeline" |

## Subscription Access

- **Free**: No access
- **Starter**: No access
- **Pro**: Access
- **Enterprise**: Access

## State Variables

```csharp
string _uploadedPath;              // Uploaded file path
string _uploadedDataUrl;           // Base64 for JS display
byte[] _uploadedBytes;             // Raw bytes for Gemini
int _selectedAge = 25;             // Current slider/preset value
string _selectedPreset = "";       // Active preset name
bool _isProcessing;                // Processing overlay
bool _isTimelineProcessing;        // Timeline progress
int _timelineProgress;             // Count of completed stages
string _resultDataUrl;             // Single transform result
string[] _timelineDataUrls;        // Timeline results (5 entries)
bool _showComparison;              // Comparison modal visible
string _comparisonOriginal;        // Before image for modal
string _comparisonResult;          // After image for modal
```

## Error Handling

- No face detected → show inline error, no coin debit
- Insufficient coins → show message with link to `/wallet`
- Gemini API failure → show error toast, no additional coin debit (coins already debited)
- Timeline partial failure → show completed stages, indicate which failed, offer retry for failed ones

## Scope Exclusions (v1)

- Multi-face support (future enhancement)
- Video aging
- Age estimation of uploaded photo
- Undo/redo history
