# Poster Mosaic Service

The `PosterMosaicService` generates photo mosaic images composed of movie poster thumbnails. Given a source image, it analyzes each region's dominant color and selects the best-matching poster from the database to create a mosaic that approximates the original image.

## Overview

- **Input**: Any image (JPEG, PNG, WebP, etc.)
- **Output**: A mosaic image where each tile is a movie poster
- **Aspect Ratio**: Output preserves the source image aspect ratio; posters maintain their 3:4 aspect ratio

## Quick Start

```csharp
// Basic usage with default options
var mosaicBytes = await mosaicService.BuildPosterMosaicBytes(
    sourceBytes: imageBytes,
    topK: 50,
    excludeRadius: 2,
    tileScale: 1.0
);

// Full control with MosaicOptions
var options = new MosaicOptions
{
    OutputScale = 2.0,      // Double the output size (4× more posters)
    TileScale = 0.5,        // Half-size posters (4× more posters)
    TopK = 100,
    OutputFormat = MosaicOutputFormat.WebP,
    Quality = 90
};
var mosaicBytes = await mosaicService.BuildPosterMosaicBytes(imageBytes, options);
```

---

## MosaicOptions Reference

### Size Control

These options control the output image dimensions and how many posters are used.

| Property | Type | Default | Range | Description |
|----------|------|---------|-------|-------------|
| `OutputScale` | `double` | `1.0` | 0.1 – 100.0 | Scale factor relative to source image. `2.0` = double size (4× posters). |
| `TileScale` | `double` | `1.0` | 0.01 – 10.0 | Poster size multiplier. Base is 150×200 px. `0.5` = 75×100 px posters. |
| `MaxOutputDimension` | `int` | `0` | 0+ | Maximum width or height in pixels. `0` = no limit. |

#### How Poster Count Scales

The number of posters in the mosaic is determined by:

```
posterCount ≈ (outputWidth × outputHeight) / (posterWidth × posterHeight)
```

| Scenario | OutputScale | TileScale | Effect on Poster Count |
|----------|-------------|-----------|------------------------|
| Default | 1.0 | 1.0 | Baseline |
| Larger output | 2.0 | 1.0 | 4× more posters |
| Smaller posters | 1.0 | 0.5 | 4× more posters |
| Both | 2.0 | 0.5 | 16× more posters |
| Reduced | 0.5 | 2.0 | 1/16 as many posters |

#### MaxOutputDimension

When set, the output is scaled down proportionally if either dimension exceeds the limit:

```csharp
// Limit largest dimension to 4K
options.MaxOutputDimension = 3840;
```

---

### Color Matching

These options control how posters are selected to match each region of the source image.

| Property | Type | Default | Range | Description |
|----------|------|---------|-------|-------------|
| `TopK` | `int` | `50` | 1 – 6000 | Number of color-matched candidates to consider per cell. |
| `ExcludeRadius` | `int` | `2` | 0 – 50 | Radius (in cells) to check for duplicate posters. |
| `ColorDecayFactor` | `double` | `10000.0` | 1.0 – 1M | Exponential decay divisor for color distance. Higher = more tolerant. |
| `AdjacencyPenaltyBase` | `double` | `0.1` | 0.001 – 1.0 | Penalty for adjacent duplicates. Lower = stronger penalty. |

#### TopK (Candidate Pool Size)

For each cell, the service finds the `TopK` posters with the closest dominant color match. A weighted random selection then picks from these candidates.

- **Low TopK (1-10)**: Strict color matching, less variety
- **Medium TopK (30-100)**: Balanced (recommended)
- **High TopK (500+)**: More variety, looser color matching

#### ExcludeRadius (Duplicate Prevention)

Prevents the same poster from appearing too close to itself. The algorithm penalizes posters that already appear within this radius.

```
ExcludeRadius = 2 means checking a 5×5 area centered on each cell
```

- `0`: No duplicate prevention (same poster can appear anywhere)
- `1`: Check immediate neighbors (3×3 area)
- `2`: Check 5×5 area (default, recommended)
- `3+`: Larger exclusion zones

#### ColorDecayFactor

Controls how quickly the weight drops off for posters with less-perfect color matches:

```
weight = exp(-colorDistance / ColorDecayFactor)
```

- **Lower values (1000-5000)**: Stricter color matching
- **Higher values (20000+)**: More tolerant of color differences

#### AdjacencyPenaltyBase

When a poster appears multiple times within the exclude radius, its weight is multiplied by this value for each occurrence:

```
weight *= AdjacencyPenaltyBase ^ adjacentCount
```

- `0.1` (default): Strong penalty (90% reduction per adjacent duplicate)
- `0.5`: Moderate penalty
- `1.0`: No penalty for adjacency

---

### Output Format

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `OutputFormat` | `MosaicOutputFormat` | `Png` | Output image format. |
| `Quality` | `int` | `85` | Quality for JPEG/WebP (1-100). |
| `PngCompressionLevel` | `PngCompressionLevel` | `DefaultCompression` | PNG compression (1-9). |

#### MosaicOutputFormat Enum

| Value | Use Case |
|-------|----------|
| `Png` | Lossless, large files, best for archival |
| `Jpeg` | Lossy, smaller files, good for web |
| `WebP` | Lossy, smallest files, modern browsers |

#### Quality (JPEG/WebP only)

- `90-100`: High quality, larger files
- `75-89`: Good balance (recommended)
- `50-74`: Smaller files, visible compression
- `<50`: Not recommended for mosaics

#### PngCompressionLevel

| Value | Speed | File Size |
|-------|-------|-----------|
| `Level1` | Fastest | Largest |
| `DefaultCompression` | Balanced | Medium |
| `Level9` | Slowest | Smallest |

---

## API Endpoints

### POST `/api/postermosaic`

Upload an image to generate a mosaic.

**Form Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `image` | file | Yes | — | Source image file |
| `topK` | int | No | 50 | Color match candidates |
| `excludeRadius` | int | No | 2 | Duplicate exclusion radius |
| `tileScale` | double | No | 1.0 | Poster size multiplier |
| `outputScale` | double | No | 1.0 | Output size multiplier |
| `maxDimension` | int | No | 0 | Max output dimension |
| `format` | string | No | png | Output format (png/jpeg/webp) |
| `quality` | int | No | 85 | JPEG/WebP quality |

**Example:**
```bash
curl -X POST "https://localhost/api/postermosaic" \
  -F "image=@photo.jpg" \
  -F "tileScale=0.5" \
  -F "outputScale=2" \
  -F "format=webp" \
  -o mosaic.webp
```

### GET `/api/postermosaic`

Generate a mosaic from an image URL.

**Query Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | Yes | — | URL of source image |
| `topK` | int | No | 50 | Color match candidates |
| `excludeRadius` | int | No | 2 | Duplicate exclusion radius |
| `tileScale` | double | No | 1.0 | Poster size multiplier |
| `outputScale` | double | No | 1.0 | Output size multiplier |
| `maxDimension` | int | No | 0 | Max output dimension |
| `format` | string | No | png | Output format |
| `quality` | int | No | 85 | JPEG/WebP quality |

**Example:**
```
GET /api/postermosaic?url=https://example.com/photo.jpg&tileScale=0.5&format=webp
```

---

## Caching

The service caches poster color data in a k-d tree for fast nearest-neighbor lookups.

```csharp
// Check cache status
DateTime? builtAt = mosaicService.CacheBuiltAt;
int posterCount = mosaicService.CachedPosterCount;

// Invalidate cache (e.g., after poster data changes)
await mosaicService.InvalidateCacheAsync();
```

---

## Size Limits

- **Maximum output size**: 2 GB (RGBA32 pixel data)
- If the estimated size exceeds this, an `InvalidOperationException` is thrown with guidance to reduce `OutputScale` or increase `TileScale`.

---

## Examples

### High-Detail Print (Large Poster, Many Tiles)

```csharp
var options = new MosaicOptions
{
    OutputScale = 4.0,          // 4× source size
    TileScale = 0.25,           // Tiny posters (37×50 px)
    MaxOutputDimension = 8000,  // Cap at 8K
    TopK = 200,
    OutputFormat = MosaicOutputFormat.Png
};
```

### Web Thumbnail (Fast, Small)

```csharp
var options = new MosaicOptions
{
    OutputScale = 0.5,
    TileScale = 2.0,            // Large posters (300×400 px)
    MaxOutputDimension = 800,
    TopK = 20,
    OutputFormat = MosaicOutputFormat.WebP,
    Quality = 75
};
```

### Balanced Default

```csharp
var options = new MosaicOptions
{
    OutputScale = 1.0,
    TileScale = 1.0,
    TopK = 50,
    ExcludeRadius = 2,
    OutputFormat = MosaicOutputFormat.Jpeg,
    Quality = 85
};
```

---

## Algorithm Summary

1. **Calculate grid dimensions** based on output size and tile scale, preserving source aspect ratio
2. **Resize source image** to grid dimensions (one pixel per cell)
3. **For each cell**, use k-d tree to find top-K color-matched posters
4. **Apply weights** based on:
   - Color distance (exponential decay)
   - Global usage count (diversity bonus)
   - Local adjacency (duplicate penalty)
5. **Select poster** via weighted random sampling
6. **Compose output** by drawing resized poster images at each grid position
7. **Encode** to requested output format
