using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Books.Services
{
    /// <summary>A detected block of page text, all coordinates normalised to 0–1 of the page.</summary>
    /// <param name="X">Tight text box — left. This box (X/Y/Width/Height) is what Bubble Zoom magnifies,
    /// so the words fill the loupe.</param>
    /// <param name="HitX">Tap target — left. This box (HitX/Y/Width/Height) is a little larger so a tap
    /// near the text still selects it.</param>
    /// <param name="Pol">Polarity the block was read at: 1 = dark-on-light, 2 = light-on-dark. (Tuning aid.)</param>
    /// <param name="Glyphs">How many letter-like marks formed the block. (Tuning aid — phantom art blocks
    /// tend to be small.)</param>
    public record TextRegion(
        float X, float Y, float Width, float Height,
        float HitX, float HitY, float HitWidth, float HitHeight,
        byte Pol, int Glyphs);

    /// <summary>
    /// Bubble Zoom: where the TEXT is on a page, so the reader can magnify a balloon under a tap.
    ///
    /// <para><b>There is no model file and no download.</b> The detector below is entirely classical —
    /// adaptive binarisation, connected components, stroke-width consistency — so it ships with the binary, runs
    /// on any machine, and produces the SAME regions for the same page forever. Nothing here needs a
    /// <c>Books:</c> path, and there is no "model missing" state to degrade into; the only empty result is a page
    /// with no detectable lettering. <see cref="BooksOptions.EnableTextRegions"/> exists so the whole pass can be
    /// switched off on a host that does not want to spend the CPU, and returns an empty list when it is.</para>
    ///
    /// <para>Results are memoized per (item, page) for the process — the reader asks for the current page and
    /// prefetches the next two, so the same page is requested more than once per session.</para>
    /// </summary>
    public sealed class TextRegionService
    {
        private readonly ConcurrentDictionary<(long, int), TextRegion[]> _cache = new();
        private readonly bool enabled;

        public TextRegionService(BooksOptions options) => enabled = options.EnableTextRegions;

        public async Task<TextRegion[]> GetRegionsAsync(long itemId, int pageIndex, Func<Task<Stream>> getPageStream)
        {
            if (!enabled) return [];
            if (_cache.TryGetValue((itemId, pageIndex), out var cached))
                return cached;

            await using var stream = await getPageStream();
            var regions = await DetectRegionsAsync(stream);
            _cache[(itemId, pageIndex)] = regions;
            return regions;
        }

        private readonly record struct Glyph(int MinX, int MinY, int MaxX, int MaxY, byte Pol, float StrokeW)
        {
            public int W => MaxX - MinX + 1;
            public int H => MaxY - MinY + 1;
        }

        // Classical (pre-ML) text-block finder — the MSER/SWT-era lineage. Instead of hunting for
        // speech balloons, it finds the LETTERS and groups the ones that line up, so it works on open
        // balloons, captions, SFX and text laid straight over artwork.
        //
        //   1. Adaptive binarisation (Bradley–Roth) at BOTH polarities: a pixel is ink when it's far
        //      from its LOCAL mean — darker (black-on-white type) or lighter (white-on-black type). Local,
        //      so gradients, colour and busy artwork behind the text don't matter, and no balloon is needed.
        //   2. Connected components → candidate glyphs, each with a stroke-width estimate (2·area÷perimeter).
        //   3. Glyph filter: keep letter-sized, letter-ish, partially-filled, suitably thin-stroked marks.
        //   4. Grouping: union glyphs whose height-dilated boxes touch AND whose height and stroke width
        //      match (a line of type shares an x-height and a pen weight).
        //   5. Keep blocks of ≥ minGlyphs whose glyphs are CONSISTENT in height and stroke width. This is
        //      the stroke-width-transform idea: real lettering is uniform; hatching / screentone / linework
        //      is erratic, so dense artwork fails the consistency test instead of forming a phantom block.
        //
        // Boxes are normalised, so the processing resolution only affects fidelity, not coordinates.
        private static async Task<TextRegion[]> DetectRegionsAsync(Stream imageStream)
        {
            using var loaded = await Image.LoadAsync<L8>(imageStream);
            return DetectRegions(loaded);
        }

        /// <summary>Same detector over an ALREADY-decoded page — lets the perceptual harvest reuse its single
        /// decode instead of re-reading the NAS. Does not mutate <paramref name="source"/>.</summary>
        public static TextRegion[] DetectRegions(Image<L8> source)
        {
            // ── Tunables ──
            const int   maxProcessWidth = 1400;  // text needs resolution to resolve as separate glyphs.
            const float threshT       = 0.18f;   // Bradley strength: ink when value is ±t away from local mean.
            const float minGlyphHFrac = 0.006f;  // glyph height bounds as a share of page height: letters …
            const float maxGlyphHFrac = 0.055f;  // … are small but not tiny — bigger is a logo / artwork.
            const float minFill       = 0.10f;   // area ÷ bbox — drop hollow boxes …
            const float maxFill       = 0.90f;   // … and solid blocks (a filled square ≈ 1.0).
            const float minAspect     = 0.06f;   // w/h — keep an 'I' or 'l' …
            const float maxAspect     = 3.0f;    // … reject long rules / borders.
            const float minElongation = 2.1f;    // longest side ÷ stroke width — a glyph is a stroke, not a chunk.
            const float dilateX       = 0.80f;   // grow each glyph by this × its height across (joins letters & words) …
            const float dilateY       = 0.55f;   // … and down (joins stacked lines into a paragraph block).
            const float heightRatioMax = 2.3f;   // only group glyphs of similar height (a line shares an x-height) …
            const float strokeRatioMax = 2.5f;   // … and similar pen weight.
            const int   minGlyphs     = 4;        // a block needs ≥ this many letter-like marks. n=4
                                                  // blocks additionally face the pristine-ring gate below
                                                  // ("WAIT!"-class one-word balloons vs 4-blob art noise).
            const float heightConsist = 0.78f;   // … and ≥ this share of them must share a height …
            const float strokeConsist = 0.70f;   // … and a stroke width. Erratic artwork fails these.
            const int   maxGlyphs     = 2500;     // safety cap for a pathologically busy page.
            const float textMinFrac   = 0.0010f;  // ignore blocks tinier than this share of the page.
            const float textCleanFrac = 0.0015f;  // …blocks between the two floors face the pristine-ring gate.
            const float textMaxFrac   = 0.12f;
            const int   maxRegions    = 40;
            const float tinyFracMax   = 0.40f;   // consistency is judged on letter-sized glyphs only, but
                                                 // only while tiny marks stay a minority of the block.
            const float ringPol1Min   = 130f;    // dark-on-light text needs a LIGHT immediate surround …
            const float ringPol2Max   = 105f;    // … light-on-dark needs a DARK one (polarity–surround test).

            using var image = source.Width > maxProcessWidth
                ? source.Clone(c => c.Resize(maxProcessWidth,
                    Math.Max(1, (int)(source.Height * (double)maxProcessWidth / source.Width)), KnownResamplers.Box))
                : source.Clone();
            int w = image.Width, h = image.Height, total = w * h;
            var px = new L8[total];
            image.CopyPixelDataTo(px);

            // ── 1. Adaptive binarisation (Bradley–Roth) via an integral image: O(1) local mean per pixel. ──
            var integral = new long[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                long rowSum = 0;
                int ro = (y + 1) * (w + 1), po = y * w;
                for (int x = 0; x < w; x++)
                {
                    rowSum += px[po + x].PackedValue;
                    integral[ro + (x + 1)] = integral[ro - (w + 1) + (x + 1)] + rowSum;
                }
            }
            int half = Math.Max(4, w / 24); // local window ≈ a few text heights
            // Ink of either polarity: 1 = dark-on-light (pixel well below its local mean),
            // 2 = light-on-dark (well above). Labelled and grouped separately below — a block of type is
            // one colour. A uniform background sits AT its local mean, so it is never flagged (no phantom
            // full-page component); only strokes that stand out from their surroundings register.
            var inkPol = new byte[total];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - half), y1 = Math.Min(h - 1, y + half);
                int rowTop = y0 * (w + 1), rowBot = (y1 + 1) * (w + 1), po = y * w;
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - half), x1 = Math.Min(w - 1, x + half);
                    long count = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                    long sum = integral[rowBot + (x1 + 1)] - integral[rowTop + (x1 + 1)]
                             - integral[rowBot + x0] + integral[rowTop + x0];
                    long v = (long)px[po + x].PackedValue * count; // value × count, compared to (1±t) × sum
                    if      (v <= sum * (1 - threshT)) inkPol[po + x] = 1; // darker than local mean
                    else if (v >= sum * (1 + threshT)) inkPol[po + x] = 2; // lighter than local mean
                }
            }

            // ── 2/3. Connected components → glyphs (with stroke width from area÷perimeter), filtered. ──
            // The glyph floor exists to reject sub-resolvable noise, which only exists at NATIVE scan
            // resolution: a page downscaled to maxProcessWidth has had halftone/speck noise blurred away
            // by the Box resample, so its small glyphs are trustworthy. Scaling the floor with page
            // HEIGHT over-punishes tall high-res pages (a 2152px-tall page demanded 12px letters and
            // dropped every small-lettered "whisper" balloon); ~6px is enough to measure glyph shape.
            bool wasDownscaled = source.Width > maxProcessWidth;
            int minGlyphH = wasDownscaled ? Math.Max(6, (int)(0.0033f * h)) : Math.Max(3, (int)(minGlyphHFrac * h));
            int maxGlyphH = Math.Max(minGlyphH + 1, (int)(maxGlyphHFrac * h));
            var labels = new int[total];
            var stack = new Stack<int>();
            var glyphs = new List<Glyph>();
            int label = 0;
            for (int s = 0; s < total && glyphs.Count < maxGlyphs; s++)
            {
                if (inkPol[s] == 0 || labels[s] != 0) continue;
                byte pol = inkPol[s]; // a glyph is one polarity — only flood pixels of the same kind
                label++;
                labels[s] = label; stack.Push(s);
                int minX = w, maxX = 0, minY = h, maxY = 0, area = 0, perim = 0;
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); area++;
                    int x = i % w, y = i / w;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    // 4-neighbours that aren't this polarity (or off-page) are boundary edges → perimeter.
                    if (x == 0     || inkPol[i - 1] != pol) perim++;
                    if (x == w - 1 || inkPol[i + 1] != pol) perim++;
                    if (y == 0     || inkPol[i - w] != pol) perim++;
                    if (y == h - 1 || inkPol[i + w] != pol) perim++;
                    // 8-connected flood for the component itself.
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy; if (ny < 0 || ny >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx; if (nx < 0 || nx >= w || (dx == 0 && dy == 0)) continue;
                            int j = ny * w + nx;
                            if (inkPol[j] == pol && labels[j] == 0) { labels[j] = label; stack.Push(j); }
                        }
                    }
                }
                int gh = maxY - minY + 1, gw = maxX - minX + 1;
                if (gh < minGlyphH || gh > maxGlyphH) continue;
                float aspect = (float)gw / gh;
                if (aspect < minAspect || aspect > maxAspect) continue;
                float fill = (float)area / (gw * gh);
                if (fill < minFill || fill > maxFill) continue;
                float strokeW = perim > 0 ? 2f * area / perim : 0f;
                if (strokeW <= 0) continue;
                if (Math.Max(gw, gh) / strokeW < minElongation) continue; // a blob, not a stroke
                glyphs.Add(new Glyph(minX, minY, maxX, maxY, pol, strokeW));
            }

            if (glyphs.Count < minGlyphs) return [];

            // ── 4. Group glyphs into blocks (union-find over height-dilated overlaps, same polarity,
            // similar height and stroke width). ──
            int n = glyphs.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }

            var box = new (float x0, float y0, float x1, float y1)[n];
            for (int i = 0; i < n; i++)
            {
                var g = glyphs[i]; float gh = g.H;
                box[i] = (g.MinX - dilateX * gh, g.MinY - dilateY * gh, g.MaxX + dilateX * gh, g.MaxY + dilateY * gh);
            }
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (glyphs[i].Pol != glyphs[j].Pol) continue; // dark and light type don't share a block
                    int hi = glyphs[i].H, hj = glyphs[j].H;
                    float hr = hi > hj ? (float)hi / hj : (float)hj / hi;
                    if (hr > heightRatioMax) continue; // not the same line of type
                    float si = glyphs[i].StrokeW, sj = glyphs[j].StrokeW;
                    float sr = si > sj ? si / sj : sj / si;
                    if (sr > strokeRatioMax) continue; // not the same pen weight
                    if (box[i].x0 <= box[j].x1 && box[j].x0 <= box[i].x1 &&
                        box[i].y0 <= box[j].y1 && box[j].y0 <= box[i].y1)
                    {
                        int ra = Find(i), rb = Find(j);
                        if (ra != rb) parent[ra] = rb;
                    }
                }

            var members = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!members.TryGetValue(r, out var list)) { list = new List<int>(); members[r] = list; }
                list.Add(i);
            }

            // ── 5. Emit a region per consistent text block. ──
            var regions = new List<TextRegion>();
            foreach (var idxs in members.Values)
            {
                if (idxs.Count < minGlyphs) continue;

                // Consistency: most glyphs must share a height and a stroke width (the SWT discriminator).
                // Judged on the LETTER-SIZED CORE of the block: marks well below the median height
                // (periods, commas, apostrophes, stray dots) are neither evidence for nor against
                // lettering, so they are excluded from the ratios — counting them in the denominator
                // let punctuation-heavy balloons and small-glyph pages fail consistency they should
                // pass. The core is only trusted while tiny marks are a minority (tinyFracMax) and the
                // core alone still clears minGlyphs; otherwise the whole block is judged as before.
                //
                // NOTE — a "linearity/lattice" test (reject blocks whose per-row glyph spacing is too
                // REGULAR, meant to kill brick/stripe/scale textures) used to live here. It was removed
                // 2026-07-28: hand-lettered caps have genuinely uniform pitch, so real dialogue scores
                // gap-CoV 0.09–0.21 — inside the texture range — and every single block the test
                // rejected across an 11-page, 5-era sample was a REAL balloon (verified by crop review),
                // while the textures it was written for were already killed by the consistency filters.
                // Do not reintroduce a spacing-regularity test without era-spanning crop-verification.
                var hs = idxs.Select(i => (float)glyphs[i].H).OrderBy(v => v).ToList();
                var sws = idxs.Select(i => glyphs[i].StrokeW).OrderBy(v => v).ToList();
                float medH = hs[hs.Count / 2], medSW = sws[sws.Count / 2];
                var coreIdx = idxs.Where(i => glyphs[i].H >= 0.6f * medH).ToList();
                if (coreIdx.Count >= minGlyphs && 1f - (float)coreIdx.Count / idxs.Count <= tinyFracMax)
                {
                    var coreH = coreIdx.Select(i => (float)glyphs[i].H).OrderBy(v => v).ToList();
                    var coreSw = coreIdx.Select(i => glyphs[i].StrokeW).OrderBy(v => v).ToList();
                    float cMedH = coreH[coreH.Count / 2], cMedSW = coreSw[coreSw.Count / 2];
                    int hOk = coreH.Count(v => v >= 0.6f * cMedH && v <= 1.7f * cMedH);
                    int swOk = coreSw.Count(v => v >= 0.5f * cMedSW && v <= 2.0f * cMedSW);
                    if ((float)hOk / coreH.Count < heightConsist) continue;
                    if ((float)swOk / coreH.Count < strokeConsist) continue;
                }
                else
                {
                    int hOk = hs.Count(v => v >= 0.6f * medH && v <= 1.7f * medH);
                    int swOk = sws.Count(v => v >= 0.5f * medSW && v <= 2.0f * medSW);
                    if ((float)hOk / idxs.Count < heightConsist) continue;
                    if ((float)swOk / idxs.Count < strokeConsist) continue;
                }

                int minX = w, minY = h, maxX = 0, maxY = 0;
                foreach (int i in idxs)
                {
                    var g = glyphs[i];
                    if (g.MinX < minX) minX = g.MinX; if (g.MinY < minY) minY = g.MinY;
                    if (g.MaxX > maxX) maxX = g.MaxX; if (g.MaxY > maxY) maxY = g.MaxY;
                }
                float avgH = hs.Average();
                float bw = maxX - minX + 1, bh = maxY - minY + 1;

                float padX = avgH * 0.35f, padY = avgH * 0.30f;
                float nx = Math.Clamp((minX - padX) / w, 0f, 1f);
                float ny = Math.Clamp((minY - padY) / h, 0f, 1f);
                float nw = Math.Clamp((bw + 2 * padX) / w, 0f, 1f - nx);
                float nh = Math.Clamp((bh + 2 * padY) / h, 0f, 1f - ny);

                float frac = nw * nh;
                if (frac < textMinFrac || frac > textMaxFrac) continue;

                // ── Polarity–surround test (2026-07-28): readable dark-on-light text sits on a LIGHT
                // ground and light-on-dark text on a DARK one — measured on the mean luminance of the
                // non-ink pixels in a tight ring (0.6×median glyph height) around each glyph. Phantoms
                // violate this constantly: the Bradley pass flags bright halos around dark artwork as
                // "light ink" on a bright ground (the dominant pol-2 phantom class), and dark art marks
                // in dark surroundings as "dark ink" with no light behind them. The ring (not the block
                // bbox) is what keeps open balloons safe — their bbox includes art, but the pixels
                // immediately around the letters are still balloon fill. Measured on the 11-page 5-era
                // sample: real pol-1 blocks ring ≥ 149, real pol-2 (caption) blocks ≤ 85, so 130/105
                // leave ~20pt margins; the filter removed the majority of phantoms (Flintstones pages
                // went to zero) with no real balloon lost. Known blind spot: text on strongly-tinted
                // mid-luminance fills (dark text on red, white text on mid-gray) would be rejected —
                // none observed in the sample; revisit the thresholds if such a book surfaces.
                {
                    byte bp = glyphs[idxs[0]].Pol;
                    int pad = Math.Max(2, (int)(0.6f * medH));
                    double rSum = 0, rSum2 = 0; long rN = 0, rOpp = 0;
                    foreach (int gi in idxs)
                    {
                        var g = glyphs[gi];
                        int rx0 = Math.Max(0, g.MinX - pad), rx1 = Math.Min(w - 1, g.MaxX + pad);
                        int ry0 = Math.Max(0, g.MinY - pad), ry1 = Math.Min(h - 1, g.MaxY + pad);
                        for (int yy = ry0; yy <= ry1; yy++)
                            for (int xx = rx0; xx <= rx1; xx++)
                            {
                                if (xx >= g.MinX && xx <= g.MaxX && yy >= g.MinY && yy <= g.MaxY) continue;
                                int pi = yy * w + xx;
                                if (inkPol[pi] == bp) continue;
                                if (inkPol[pi] != 0) { rOpp++; continue; }
                                double v = px[pi].PackedValue;
                                rSum += v; rSum2 += v * v; rN++;
                            }
                    }
                    if (rN > 0)
                    {
                        double ringMean = rSum / rN;
                        if (bp == 1 && ringMean < ringPol1Min) continue;
                        if (bp == 2 && ringMean > ringPol2Max) continue;

                        // Marginal blocks — those only alive thanks to the n=4 / small-frac
                        // relaxations — must sit in a PRISTINE balloon interior: near-zero
                        // opposite-polarity ink, flat ring, strong polarity contrast. Real one-word
                        // balloons measure rSd ≤ ~12 and rOpp ≈ 0; art blobs that squeak past the
                        // basic polarity test fail one of the three.
                        if (idxs.Count == 4 || frac < textCleanFrac)
                        {
                            double ringSd = Math.Sqrt(Math.Max(0, rSum2 / rN - ringMean * ringMean));
                            double oppFrac = (double)rOpp / (rN + rOpp);
                            bool clean = oppFrac <= 0.35 && ringSd <= 15 &&
                                         (bp == 1 ? ringMean >= 170 : ringMean <= 90);
                            if (!clean) continue;
                        }
                    }
                }

                float hmX = avgH * 0.80f, hmY = avgH * 0.60f;
                float hx = Math.Clamp((minX - hmX) / w, 0f, 1f);
                float hy = Math.Clamp((minY - hmY) / h, 0f, 1f);
                float hw = Math.Clamp((bw + 2 * hmX) / w, 0f, 1f - hx);
                float hh = Math.Clamp((bh + 2 * hmY) / h, 0f, 1f - hy);

                regions.Add(new TextRegion(nx, ny, nw, nh, hx, hy, hw, hh, glyphs[idxs[0]].Pol, idxs.Count));
            }

            regions.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            if (regions.Count > maxRegions) regions = regions.GetRange(0, maxRegions);
            return [.. regions];
        }
    }
}
