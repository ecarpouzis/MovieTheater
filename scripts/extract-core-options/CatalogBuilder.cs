using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MovieTheater.Tools.ExtractCoreOptions
{
    /// <summary>
    /// Folds the per-core extractions into the site's <c>core-options-catalog.json</c> and writes the
    /// drift report. Rules that matter:
    ///
    /// <list type="bullet">
    /// <item>A core's DLL is chosen by what <c>config.worker-gl.yaml</c> actually loads (<c>lib:</c>), so a
    ///       stock/<c>_custom</c> pair contributes evidence from both but only one to the catalog.</item>
    /// <item><b>policy.json decides disposition.</b> <c>hand-only</c> cores are extracted and reported but
    ///       never written to the catalog — their C# entries encode decisions (bridge-broken tokens,
    ///       load-bearing render-mode pins, line-by-line exclusions) that regeneration would bulldoze.</item>
    /// <item><b>A failed extraction never deletes data.</b> If a core the old catalog covered crashed or
    ///       timed out, its OLD block is carried over verbatim and flagged — a crash is not "no options".</item>
    /// <item>Existing keys keep their old <c>label</c>/<c>category</c> (and their hand-authored
    ///       <c>isRange</c> bounds) so the diff stays reviewable; tokens/defaults/descriptions are refreshed
    ///       from the DLL, because those are the things that silently rot.</item>
    /// <item>Renderer-selecting keys ARE emitted. <c>ArcadeCoreOptionCatalog.LoadExtracted</c> filters them
    ///       at fold time, and Phase 4's profile-token validation needs them present in the extraction.</item>
    /// </list>
    /// </summary>
    internal static class CatalogBuilder
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private sealed class Policy
        {
            public string Disposition = "fold";
            public string Reason = "";
        }

        internal static int Run(Args a)
        {
            var extractDir = a.GetPath("extract-dir");
            var policyPath = a.GetPath("policy");
            var oldPath = a.GetPath("old");
            var configPath = a.GetPath("config");
            var outPath = a.GetPath("out");
            var reportPath = a.GetPath("report");

            var extractions = Directory.GetFiles(extractDir, "*.json")
                .Select(f => JsonSerializer.Deserialize<Harness.Result>(File.ReadAllText(f)))
                .Where(r => r != null)
                .OrderBy(r => r.file, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (extractions.Count == 0) { Console.Error.WriteLine("no extractions in " + extractDir); return 1; }

            var policy = ReadPolicy(policyPath);
            var configLibs = ReadConfigLibs(configPath);
            var oldRoot = JsonNode.Parse(File.ReadAllText(oldPath)).AsObject();
            var oldCores = oldRoot["cores"].AsObject();

            // ── Which DLL is the catalog's source of truth for each core key ────────────────────────────
            var byCore = extractions.GroupBy(r => r.coreKey, StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var chosen = new Dictionary<string, Harness.Result>(StringComparer.OrdinalIgnoreCase);
            var unusedDlls = new List<Harness.Result>();
            foreach (var (core, list) in byCore)
            {
                var live = list.Where(r => configLibs.Contains(Path.GetFileNameWithoutExtension(r.file))).ToList();
                var pick = live.FirstOrDefault()
                           ?? list.FirstOrDefault(r => !r.custom)
                           ?? list[0];
                chosen[core] = pick;
                unusedDlls.AddRange(list.Where(r => !ReferenceEquals(r, pick)));
            }

            // ── Build the new catalog ───────────────────────────────────────────────────────────────────
            var newCores = new JsonObject();
            var report = new StringBuilder();
            var diffs = new List<CoreDiff>();

            foreach (var core in chosen.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var pick = chosen[core];
                var pol = policy.TryGetValue(core, out var p) ? p : new Policy();
                var oldBlock = oldCores.TryGetPropertyValue(core, out var ob) ? ob?.AsObject() : null;
                var oldOpts = OldOptions(oldBlock);

                var diff = new CoreDiff { Core = core, Extraction = pick, Policy = pol, WasInOldCatalog = oldBlock != null };
                diffs.Add(diff);

                if (pol.Disposition == "hand-only") { diff.Note = "hand-only: not written to the catalog"; continue; }

                if (pick.outcome != "ok" && pick.outcome != "ok-after-retro_init")
                {
                    if (oldBlock != null)
                    {
                        // A crash/timeout must never look like "this core lost its options".
                        newCores[core] = oldBlock.DeepClone();
                        diff.Note = $"extraction {pick.outcome} — OLD catalog block carried over verbatim";
                    }
                    else diff.Note = $"extraction {pick.outcome} — no catalog entry (nothing to carry over)";
                    continue;
                }

                var block = new JsonObject
                {
                    ["dll"] = pick.file,
                    ["libraryName"] = pick.libraryName,
                    ["libraryVersion"] = pick.libraryVersion,
                    ["source"] = pick.source,
                };
                var arr = new JsonArray();
                foreach (var o in pick.options)
                {
                    oldOpts.TryGetValue(o.key, out var prev);
                    arr.Add(BuildOption(o, prev, pick, diff));
                }
                block["options"] = arr;
                newCores[core] = block;

                foreach (var k in oldOpts.Keys)
                    if (!pick.options.Any(o => string.Equals(o.key, k, StringComparison.Ordinal)))
                        diff.Removed.Add(k);
            }

            // Cores the old catalog had that we saw no DLL for at all — keep them, flag them loudly.
            foreach (var kv in oldCores)
            {
                if (newCores.ContainsKey(kv.Key)) continue;
                if (policy.TryGetValue(kv.Key, out var pol2) && pol2.Disposition == "hand-only") continue;
                newCores[kv.Key] = kv.Value.DeepClone();
                diffs.Add(new CoreDiff { Core = kv.Key, WasInOldCatalog = true, Note = "NO DEPLOYED DLL FOUND — old block kept as-is" });
            }

            // ── Renderer-key sidecar (plan Phase 4.1) ───────────────────────────────────────────────────
            // Renderer-selecting keys are owned by the Graphics selector and filtered out of the module's
            // catalog at fold time, so nothing validates RenderProfile.Options today (D5). This section is
            // what the Phase 4 test validates profile tokens against. It covers EVERY extracted core,
            // including the hand-only ones — parallel_n64's gfxplugin/rspplugin tokens exist nowhere else,
            // because its core block is deliberately not written above.
            var rendererKeys = new JsonObject();
            foreach (var core in chosen.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var pick = chosen[core];
                var hits = pick.options.Where(o => RendererKeys.Contains(o.key, StringComparer.Ordinal)).ToList();
                if (hits.Count == 0) continue;
                var arr = new JsonArray();
                foreach (var o in hits)
                {
                    var vals = new JsonArray();
                    foreach (var v in o.values) vals.Add(new JsonObject { ["token"] = v.token, ["label"] = v.label });
                    arr.Add(new JsonObject
                    {
                        ["key"] = o.key,
                        ["label"] = o.descCategorized ?? o.desc ?? o.key,
                        ["default"] = o.@default,
                        ["values"] = vals,
                    });
                }
                rendererKeys[core] = new JsonObject { ["dll"] = pick.file, ["options"] = arr };
            }

            var coreCount = newCores.Count;
            var root = new JsonObject
            {
                ["_comment"] = "Full per-CORE libretro option catalog for the arcade per-game config module. " +
                               "Tokens are the cores' EXACT values (validation allowlist) — do not hand-edit. " +
                               "GENERATED by scripts/extract-core-options (runtime harness; driver " +
                               "scripts/extract-core-options.ps1) from the DEPLOYED core DLLs in " +
                               "D:/ArcadeStorage/worker-gl/assets/cores. Per-core disposition (fold vs hand-only) " +
                               "lives in scripts/extract-core-options/policy.json; hand-only cores are catalogued " +
                               "in ArcadeCoreOptionCatalog.cs instead and deliberately absent here. Renderer-selecting " +
                               "keys ARE present under 'cores' and are filtered at fold time by LoadExtracted; " +
                               "'rendererKeys' is the sidecar every deployed core's renderer tokens (incl. hand-only " +
                               "cores) so RenderProfile.Options can be validated against real tokens.",
                ["_generator"] = "scripts/extract-core-options.ps1",
                ["_generated"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["cores"] = SortedCores(newCores),
                ["rendererKeys"] = rendererKeys,
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(root, Json) + "\n", new UTF8Encoding(false));

            WriteReport(reportPath, extractions, chosen, byCore, unusedDlls, diffs, policy, configLibs, oldCores);
            Console.Error.WriteLine($"catalog: {coreCount} cores -> {outPath}");
            Console.Error.WriteLine($"report : {reportPath}");
            return 0;
        }

        private static JsonObject SortedCores(JsonObject cores)
        {
            var o = new JsonObject();
            foreach (var k in cores.Select(k => k.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
            {
                var v = cores[k];
                cores.Remove(k);
                o[k] = v;
            }
            return o;
        }

        private sealed class CoreDiff
        {
            public string Core;
            public Harness.Result Extraction;
            public Policy Policy = new();
            public bool WasInOldCatalog;
            public string Note;
            public List<string> Added = new();
            public List<string> Removed = new();
            public List<string> DefaultChanged = new();
            public List<string> ValuesChanged = new();
            public List<string> LabelChanged = new();
            public List<string> RangePreserved = new();
            /// <summary>Keys the OLD snapshot carried with an EMPTY value list. ParseExtracted drops an enum
            /// option with no tokens ("unusable"), so these were silently invisible in the config module —
            /// the runtime harness recovers their real token lists.</summary>
            public List<string> RecoveredEmpty = new();
        }

        private static Dictionary<string, JsonObject> OldOptions(JsonObject block)
        {
            var d = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            if (block?["options"] is JsonArray arr)
                foreach (var n in arr)
                    if (n?["key"]?.GetValue<string>() is string k) d[k] = n.AsObject();
            return d;
        }

        private static JsonObject BuildOption(Harness.ExOption o, JsonObject prev, Harness.Result src, CoreDiff diff)
        {
            var isRange = prev?["isRange"]?.GetValue<bool>() ?? false;
            // Prefer v2's desc_categorized: our UI groups by category, so the core's SHORT label is the
            // right one ("Renderer", not "Video > Renderer").
            var coreLabel = !string.IsNullOrWhiteSpace(o.descCategorized) ? o.descCategorized
                          : !string.IsNullOrWhiteSpace(o.desc) ? o.desc : o.key;
            var label = prev?["label"]?.GetValue<string>() ?? coreLabel;
            var category = prev?["category"]?.GetValue<string>() ?? MapCategory(o.categoryKey);

            if (prev == null) diff.Added.Add(o.key);
            else
            {
                var pd = prev["default"]?.GetValue<string>();
                if (!isRange && pd != o.@default) diff.DefaultChanged.Add($"{o.key}: `{pd}` -> `{o.@default}`");
                var pv = (prev["values"] as JsonArray)?.Select(v => v?["token"]?.GetValue<string>()).ToList() ?? new List<string>();
                var nv = o.values.Select(v => v.token).ToList();
                if (!isRange && pv.Count == 0 && nv.Count > 0) diff.RecoveredEmpty.Add(o.key);
                else if (!isRange && !pv.SequenceEqual(nv, StringComparer.Ordinal))
                {
                    var gone = pv.Except(nv, StringComparer.Ordinal).ToList();
                    var came = nv.Except(pv, StringComparer.Ordinal).ToList();
                    var what = gone.Count == 0 && came.Count == 0
                        ? "order only"
                        : (gone.Count > 0 ? "-[" + string.Join(", ", gone) + "] " : "") +
                          (came.Count > 0 ? "+[" + string.Join(", ", came) + "]" : "");
                    diff.ValuesChanged.Add($"{o.key}: {what.Trim()}");
                }
                var pl = prev["label"]?.GetValue<string>();
                if (pl != null && pl != coreLabel)
                    diff.LabelChanged.Add($"{o.key}: `{pl}` (kept) vs core's `{coreLabel}`");
                if (isRange) diff.RangePreserved.Add(o.key);
            }

            var node = new JsonObject
            {
                ["key"] = o.key,
                ["label"] = label,
            };

            var values = new JsonArray();
            if (!isRange)
                foreach (var v in o.values)
                    values.Add(new JsonObject { ["token"] = v.token, ["label"] = v.label });
            node["values"] = values;
            node["default"] = isRange ? prev["default"]?.GetValue<string>() : o.@default;
            if (!string.IsNullOrWhiteSpace(o.info)) node["desc"] = o.info;
            else if (prev?["desc"] is JsonNode pdesc) node["desc"] = pdesc.GetValue<string>();
            node["category"] = category;
            if (isRange)
            {
                node["isRange"] = true;
                node["rangeMin"] = prev["rangeMin"]?.GetValue<int>() ?? 0;
                node["rangeMax"] = prev["rangeMax"]?.GetValue<int>() ?? 0;
            }
            if (!string.IsNullOrWhiteSpace(o.categoryKey)) node["coreCategory"] = o.categoryKey;
            return node;
        }

        /// <summary>The core's own v2 category_key -> the config UI's broad grouping (ArcadeCoreOptionCatalog.Category).
        /// Anything we don't recognise lands in "other" rather than being guessed into a group.</summary>
        internal static string MapCategory(string categoryKey)
        {
            if (string.IsNullOrWhiteSpace(categoryKey)) return "other";
            var k = categoryKey.Trim().ToLowerInvariant();
            var exact = k switch
            {
                "video" or "gfx" or "graphics" or "display" or "screen" => "video",
                "audio" or "sound" => "audio",
                "input" or "controls" or "controller" => "input",
                "timing" or "performance" or "speed" or "cpu" => "performance",
                "hack" or "hacks" or "enhancement" or "enhancements" => "hack",
                "system" or "region" or "core" or "bios" or "hardware" or "machine" => "system",
                _ => null,
            };
            if (exact != null) return exact;
            // Compound keys the cores really ship (pcsx2 "hw_hacks", mupen "gliden64_frame_buffer", …).
            if (k.Contains("hack")) return "hack";
            if (k.Contains("video") || k.Contains("gfx") || k.Contains("graphic") || k.Contains("display")
                || k.Contains("screen") || k.Contains("render")) return "video";
            if (k.Contains("audio") || k.Contains("sound")) return "audio";
            if (k.Contains("input") || k.Contains("control") || k.Contains("pad")) return "input";
            if (k.Contains("timing") || k.Contains("perf") || k.Contains("speed") || k.Contains("cpu")) return "performance";
            if (k.Contains("system") || k.Contains("region") || k.Contains("bios") || k.Contains("hardware")) return "system";
            return "other";
        }

        private static Dictionary<string, Policy> ReadPolicy(string path)
        {
            var d = new Dictionary<string, Policy>(StringComparer.OrdinalIgnoreCase);
            var root = JsonNode.Parse(File.ReadAllText(path)).AsObject();
            if (root["cores"] is JsonObject cores)
                foreach (var kv in cores)
                    d[kv.Key] = new Policy
                    {
                        Disposition = kv.Value?["disposition"]?.GetValue<string>() ?? "fold",
                        Reason = kv.Value?["reason"]?.GetValue<string>() ?? "",
                    };
            return d;
        }

        /// <summary>Every <c>lib:</c> value in config.worker-gl.yaml — the authority for which DLL a core key
        /// really loads. Read-only; this file is never written by the tool.</summary>
        private static HashSet<string> ReadConfigLibs(string path)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (!line.StartsWith("lib:", StringComparison.Ordinal)) continue;
                var v = line.Substring(4).Trim().Trim('"', '\'');
                var hash = v.IndexOf('#');
                if (hash >= 0) v = v.Substring(0, hash).Trim();
                if (v.Length > 0) set.Add(v);
            }
            return set;
        }

        // ── The drift report ────────────────────────────────────────────────────────────────────────────
        private static readonly string[] RendererKeys =
        {
            "pcsx2_renderer", "beetle_psx_hw_renderer",
            "mupen64plus-rdp-plugin", "mupen64plus-rsp-plugin",
            "parallel-n64-gfxplugin", "parallel-n64-rspplugin",
        };

        private static void WriteReport(
            string path, List<Harness.Result> all, Dictionary<string, Harness.Result> chosen,
            Dictionary<string, List<Harness.Result>> byCore, List<Harness.Result> unused,
            List<CoreDiff> diffs, Dictionary<string, Policy> policy, HashSet<string> configLibs,
            JsonObject oldCores)
        {
            var s = new StringBuilder();
            void L(string t = "") => s.Append(t).Append('\n');

            L("# Arcade core-option drift report — " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            L();
            L("**Generated** by `scripts/extract-core-options.ps1` (runtime harness, one child process per DLL)");
            L("against the DEPLOYED cores. Regenerate with:");
            L();
            L("```powershell");
            L("pwsh -File scripts/extract-core-options.ps1 -Force");
            L("```");
            L();
            L("Method: each DLL is `LoadLibrary`d in its own process and handed a `retro_environment_t` that");
            L("answers `GET_CORE_OPTIONS_VERSION` = 2 and captures `SET_VARIABLES` (16) / `SET_CORE_OPTIONS` (53) /");
            L("`_INTL` (54) / `SET_CORE_OPTIONS_V2` (67) / `_V2_INTL` (68). The core hands over the real structs, so");
            L("this sidesteps the linker string-pooling trap that made the earlier static read of `pcsx2_renderer`");
            L("wrong. A crash or timeout is RECORDED as such and never read as \"this core has no options\".");
            L();

            // §1 outcomes
            L("## 1. Extraction outcome — every deployed DLL");
            L();
            L("| dll | core key | in config `lib:` | outcome | source | options | library (version) |");
            L("|---|---|---|---|---|---:|---|");
            foreach (var r in all.OrderBy(r => r.coreKey, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.file, StringComparer.OrdinalIgnoreCase))
            {
                var live = configLibs.Contains(Path.GetFileNameWithoutExtension(r.file)) ? "yes" :
                    (chosen.TryGetValue(r.coreKey, out var c) && ReferenceEquals(c, r) ? "stock default" : "no");
                L($"| `{r.file}` | `{r.coreKey}` | {live} | **{r.outcome}** | {Src(r.source)} | {r.options.Count} | {Esc(r.libraryName)} ({Esc(r.libraryVersion)}) |");
            }
            L();
            var bad = all.Where(r => r.outcome != "ok" && r.outcome != "ok-after-retro_init").ToList();
            L(bad.Count == 0
                ? "Every deployed DLL yielded its option table. No crashers, no timeouts, no empties."
                : "⚠ Not clean — see the non-`ok` rows above:");
            foreach (var r in bad) L($"- `{r.file}`: **{r.outcome}**" + (r.notes.Count > 0 ? " — " + string.Join("; ", r.notes) : ""));
            L();

            // §2 renderer tokens — the withdrawn-D4 answer
            L("## 2. Renderer tokens the DEPLOYED cores really declare (answers the withdrawn D4 claim)");
            L();
            L("Evidence, not inference: these are the value arrays the cores themselves passed to the frontend.");
            L();
            foreach (var key in RendererKeys)
            {
                var hits = all.Where(r => r.options.Any(o => o.key == key)).ToList();
                L($"### `{key}`");
                L();
                if (hits.Count == 0) { L("- **not declared by any deployed DLL.**"); L(); continue; }
                foreach (var r in hits)
                {
                    var o = r.options.First(x => x.key == key);
                    L($"- `{r.file}`{(r.custom ? " *(custom build)*" : "")} — default `{o.@default}`, {o.values.Count} tokens:");
                    L($"  `{string.Join("` · `", o.values.Select(v => v.token))}`");
                }
                L();
            }

            // §3 stock vs custom
            L("## 3. Stock vs `_custom` deltas");
            L();
            var pairs = byCore.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (pairs.Count == 0) L("_No core key had more than one deployed DLL._");
            foreach (var kv in pairs)
            {
                var live = chosen[kv.Key];
                L($"### `{kv.Key}` — catalog uses `{live.file}`");
                L();
                foreach (var other in kv.Value.Where(r => !ReferenceEquals(r, live)))
                {
                    var a = live.options.ToDictionary(o => o.key, StringComparer.Ordinal);
                    var b = other.options.ToDictionary(o => o.key, StringComparer.Ordinal);
                    var onlyLive = a.Keys.Except(b.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var onlyOther = b.Keys.Except(a.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var tokenDiff = a.Keys.Intersect(b.Keys, StringComparer.Ordinal)
                        .Where(k => !a[k].values.Select(v => v.token).SequenceEqual(b[k].values.Select(v => v.token), StringComparer.Ordinal))
                        .OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var defDiff = a.Keys.Intersect(b.Keys, StringComparer.Ordinal)
                        .Where(k => a[k].@default != b[k].@default)
                        .OrderBy(x => x, StringComparer.Ordinal).ToList();
                    L($"vs `{other.file}` ({other.options.Count} options, outcome {other.outcome}):");
                    L();
                    L($"- only on the deployed build ({onlyLive.Count}): " + Keys(onlyLive));
                    L($"- only on the other build ({onlyOther.Count}): " + Keys(onlyOther));
                    L($"- different token lists ({tokenDiff.Count}): " + Keys(tokenDiff));
                    L($"- different defaults ({defDiff.Count}): " + string.Join(", ", defDiff.Select(k => $"`{k}` {a[k].@default} vs {b[k].@default}")));
                    L();
                }
            }

            // §4 catalog diff
            L("## 4. Catalog diff — old committed JSON vs this extraction");
            L();
            L("| core | disposition | old | new | +added | -removed | default changed | tokens changed |");
            L("|---|---|---:|---:|---:|---:|---:|---:|");
            foreach (var d in diffs.OrderBy(d => d.Core, StringComparer.OrdinalIgnoreCase))
            {
                var oldN = oldCores.TryGetPropertyValue(d.Core, out var ob) && ob?["options"] is JsonArray oa ? oa.Count : 0;
                var newN = d.Policy.Disposition == "hand-only" ? 0 : (d.Extraction?.options.Count ?? oldN);
                L($"| `{d.Core}` | {d.Policy.Disposition} | {(oldN == 0 ? "—" : oldN.ToString())} | {(d.Policy.Disposition == "hand-only" ? "—" : newN.ToString())} | {d.Added.Count} | {d.Removed.Count} | {d.DefaultChanged.Count} | {d.ValuesChanged.Count} |");
            }
            L();
            foreach (var d in diffs.OrderBy(d => d.Core, StringComparer.OrdinalIgnoreCase))
            {
                var interesting = d.Added.Count + d.Removed.Count + d.DefaultChanged.Count
                                  + d.ValuesChanged.Count + d.RecoveredEmpty.Count > 0
                                  || d.Note != null;
                if (!interesting) continue;
                L($"### `{d.Core}`");
                L();
                if (d.Note != null) L($"- ⚠ {d.Note}");
                if (d.Policy.Disposition == "hand-only")
                    L($"- policy **hand-only**: {d.Policy.Reason}"
                      + (d.Extraction != null ? $" (extracted anyway: {d.Extraction.options.Count} options, outcome {d.Extraction.outcome})" : ""));
                if (!d.WasInOldCatalog && d.Policy.Disposition != "hand-only")
                    L($"- **NEW CORE** in the catalog ({d.Extraction?.options.Count ?? 0} options) — closes a D7.1 gap.");
                else
                {
                    if (d.Added.Count > 0) L($"- added keys ({d.Added.Count}): " + Keys(d.Added));
                    if (d.Removed.Count > 0) L($"- **removed keys** ({d.Removed.Count}): " + Keys(d.Removed));
                    if (d.DefaultChanged.Count > 0) { L($"- changed defaults ({d.DefaultChanged.Count}):"); foreach (var x in d.DefaultChanged) L("  - " + x); }
                    if (d.RecoveredEmpty.Count > 0)
                        L($"- **token lists RECOVERED** ({d.RecoveredEmpty.Count}) — the old snapshot carried these with an EMPTY " +
                          $"value list, so `ParseExtracted` dropped them and the config module never showed them at all: " + Keys(d.RecoveredEmpty));
                    if (d.ValuesChanged.Count > 0) { L($"- changed token lists ({d.ValuesChanged.Count}):"); foreach (var x in d.ValuesChanged) L("  - " + x); }
                    if (d.LabelChanged.Count > 0) { L($"- label drift (old label KEPT for reviewability) ({d.LabelChanged.Count}):"); foreach (var x in d.LabelChanged) L("  - " + x); }
                    if (d.RangePreserved.Count > 0) L($"- hand-authored ranges preserved: " + Keys(d.RangePreserved));
                }
                L();
            }

            // §5 deployed but unused
            L("## 5. Deployed DLLs the catalog does not use");
            L();
            if (unused.Count == 0) L("_None._");
            foreach (var r in unused.OrderBy(r => r.file, StringComparer.OrdinalIgnoreCase))
                L($"- `{r.file}` — core key `{r.coreKey}`, {r.options.Count} options, outcome {r.outcome}. " +
                  $"Not the DLL `config.worker-gl.yaml` loads for this core.");
            L();
            var notInConfig = all.Where(r => !configLibs.Contains(Path.GetFileNameWithoutExtension(r.file))).ToList();
            L("DLLs with no `lib:` line in `config.worker-gl.yaml` (CloudRetro stock defaults, or simply unused):");
            L();
            foreach (var r in notInConfig.OrderBy(r => r.file, StringComparer.OrdinalIgnoreCase))
                L($"- `{r.file}`");
            L();

            L("## 6. Policy");
            L();
            L("| core | disposition | reason |");
            L("|---|---|---|");
            foreach (var kv in policy.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                L($"| `{kv.Key}` | {kv.Value.Disposition} | {kv.Value.Reason} |");
            L();

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
        }

        private static string Keys(IEnumerable<string> keys)
        {
            var l = keys.ToList();
            return l.Count == 0 ? "—" : string.Join(", ", l.Select(k => "`" + k + "`"));
        }

        private static string Src(string s) => string.IsNullOrEmpty(s) ? "—" : "`" + s + "`";
        private static string Esc(string s) => string.IsNullOrEmpty(s) ? "—" : s.Replace("|", "\\|");
    }
}
