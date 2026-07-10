using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MovieTheater.ArcadeGateway;

/// <summary>
/// The ONLY channel to Apollo's admin API (docs/arcade-heavy-lane-plan.md §7.2/§7.3). Apollo's web
/// UI/API is bound to the host and basic-auth'd (`origin_web_ui_allowed=pc`); the k8s site pod can
/// never reach it — the gateway proxies exactly the two operations the site needs: completing a
/// pairing PIN, and syncing the descriptor registry into Apollo's app list. Creds live in the
/// gateway's appsettings on the host, never in the site DB, never in the repo.
///
/// <para>Sync is UPSERT-ONLY and dry-run by default (destructive-bulk house rule): descriptors are
/// matched to Apollo apps by exact name; apps the registry doesn't know (Desktop, hand-authored
/// entries) are reported as unmanaged and left untouched. Nothing is ever deleted by automation —
/// removing an app is a deliberate act in Apollo's web UI.</para>
/// </summary>
public sealed class ApolloAdmin
{
    private readonly HttpClient http;
    private readonly ILogger log;

    public ApolloAdmin(HeavyOptions opt, ILogger log)
    {
        this.log = log;
        // Apollo serves a self-signed cert on localhost — bypass validation for exactly this client.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        http = new HttpClient(handler) { BaseAddress = new Uri(opt.ApolloBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrEmpty(opt.ApolloUser))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{opt.ApolloUser}:{opt.ApolloPassword}")));
    }

    /// <summary>Complete a Moonlight pairing: the 4-digit PIN the client shows + the device name the
    /// site user typed. Success means Apollo saved the client cert — the caller records the
    /// device→user mapping (HeavyClient) on its side.</summary>
    public async Task<(bool ok, string detail)> PairAsync(string pin, string deviceName, CancellationToken ct)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("api/pin", new { pin, name = deviceName }, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return (false, $"Apollo answered {(int)resp.StatusCode}");
            // Sunshine-family answers {"status":"true"|"false"} (string) or a bool — accept either.
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var st) ? st.ToString() : "";
            bool ok = status.Equals("true", StringComparison.OrdinalIgnoreCase);
            return (ok, ok ? "paired" : "Apollo rejected the PIN (expired or mistyped — PINs are short-lived; retry from the client)");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Apollo pair call failed");
            return (false, "Apollo is unreachable on the host.");
        }
    }

    /// <summary>Apollo's current app list (name → app object), or null when unreachable.</summary>
    public async Task<List<JsonObject>?> GetAppsAsync(CancellationToken ct)
    {
        try
        {
            var body = await http.GetStringAsync("api/apps", ct);
            var root = JsonNode.Parse(body)!.AsObject();
            return root["apps"]!.AsArray().Select(n => n!.AsObject()).ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Apollo app list fetch failed");
            return null;
        }
    }

    /// <summary>Upsert one app (index -1 = append, else replace in place — Sunshine API semantics).</summary>
    public async Task<bool> UpsertAppAsync(JsonObject app, int index, CancellationToken ct)
    {
        app["index"] = index;
        try
        {
            var resp = await http.PostAsync("api/apps",
                new StringContent(app.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Apollo app upsert failed for {Name}", (string?)app["name"]);
            return false;
        }
    }

    /// <summary>
    /// Compile the descriptor registry into Apollo's app list. Returns the diff; applies only when
    /// <paramref name="apply"/> — so the first run is always a reviewable dry run.
    /// </summary>
    public async Task<object> SyncAppsAsync(HeavyAppRegistry registry, HeavyStager? stager, HeavyOptions opt, bool apply, CancellationToken ct)
    {
        var current = await GetAppsAsync(ct);
        if (current == null) return new { ok = false, error = "Apollo is unreachable — is the service running?" };

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < current.Count; i++)
        {
            var name = (string?)current[i]["name"];
            if (!string.IsNullOrEmpty(name)) byName[name] = i;
        }

        var adds = new List<string>();
        var updates = new List<string>();
        var skipped = new List<object>();
        var applied = new List<string>();

        foreach (var app in registry.All())
        {
            if (!app.Enabled) { skipped.Add(new { app.Id, reason = "disabled" }); continue; }
            if (!File.Exists(app.Exe)) { skipped.Add(new { app.Id, reason = $"exe not found: {app.Exe}" }); continue; }
            if (app.NeedsStaging && stager != null && !stager.IsStaged(app))
            { skipped.Add(new { app.Id, reason = "not staged yet — Prepare it first" }); continue; }

            var compiled = Compile(app, stager, opt);
            bool exists = byName.TryGetValue(app.Title, out int idx);
            // Unchanged? Compare the fields we own; leave hand-tuned extras alone.
            if (exists && SameManagedFields(current[idx], compiled)) continue;
            (exists ? updates : adds).Add(app.Title);

            if (apply)
            {
                // Preserve any fields Apollo/hand-editing added that we don't manage.
                var target = exists ? MergeInto(current[idx], compiled) : compiled;
                if (await UpsertAppAsync(target, exists ? idx : -1, ct)) applied.Add(app.Title);
                else return new { ok = false, error = $"Upsert failed at '{app.Title}' — aborted (list may be partially applied).", applied };
            }
        }

        var managedNames = registry.All().Select(a => a.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmanaged = byName.Keys.Where(n => !managedNames.Contains(n)).OrderBy(n => n).ToList();

        return new { ok = true, dryRun = !apply, adds, updates, skipped, unmanaged, applied };
    }

    /// <summary>Descriptor → Apollo app object (plan §4: every synced app launches through the
    /// heavy-launch contract so the lane lock + staging + saves all happen; without a configured
    /// launch script it falls back to the raw emulator command).</summary>
    internal static JsonObject Compile(HeavyApp app, HeavyStager? stager, HeavyOptions opt)
    {
        string rom = app.NeedsStaging && stager != null ? stager.TargetPathFor(app) : "";
        string args = (app.ArgsTemplate ?? "").Replace("{rom}", rom);

        string cmd;
        var prepCmd = new JsonArray();
        if (!string.IsNullOrEmpty(opt.LaunchScript))
        {
            cmd = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{opt.LaunchScript}\" -AppId \"{app.Id}\"";
            // Belt-and-suspenders finish (plan §4): undo runs even when the client force-quits the
            // app, so the lane lock releases whichever way the session ends.
            prepCmd.Add(new JsonObject
            {
                ["do"] = "",
                ["undo"] = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{opt.LaunchScript}\" -AppId \"{app.Id}\" -Finish",
                ["elevated"] = false,
            });
        }
        else
        {
            cmd = string.IsNullOrEmpty(args) ? $"\"{app.Exe}\"" : $"\"{app.Exe}\" {args}";
        }

        var o = new JsonObject
        {
            ["name"] = app.Title,
            ["cmd"] = cmd,
            ["exclude-global-prep-cmd"] = false,
            ["elevated"] = false,
            ["auto-detach"] = true,
            ["wait-all"] = true,
            ["exit-timeout"] = "10",
        };
        if (prepCmd.Count > 0) o["prep-cmd"] = prepCmd;
        if (!string.IsNullOrEmpty(app.WorkingDir)) o["working-dir"] = app.WorkingDir;
        if (!string.IsNullOrEmpty(app.BoxArt)) o["image-path"] = app.BoxArt;
        return o;
    }

    private static bool SameManagedFields(JsonObject existing, JsonObject compiled)
    {
        foreach (var key in new[] { "cmd", "working-dir", "image-path", "exit-timeout" })
        {
            var a = existing[key]?.ToString() ?? "";
            var b = compiled[key]?.ToString() ?? "";
            if (!string.Equals(a, b, StringComparison.Ordinal)) return false;
        }
        var ap = existing["prep-cmd"]?.ToJsonString() ?? "";
        var bp = compiled["prep-cmd"]?.ToJsonString() ?? "";
        return string.Equals(ap, bp, StringComparison.Ordinal);
    }

    private static JsonObject MergeInto(JsonObject existing, JsonObject compiled)
    {
        var merged = JsonNode.Parse(existing.ToJsonString())!.AsObject();
        foreach (var (k, v) in compiled)
            merged[k] = v is null ? null : JsonNode.Parse(v.ToJsonString());
        return merged;
    }
}
