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
    private readonly string? user;
    private readonly string? password;
    private readonly SemaphoreSlim loginSem = new(1, 1);
    private bool loggedIn;

    public ApolloAdmin(HeavyOptions opt, ILogger log)
    {
        this.log = log;
        user = opt.ApolloUser;
        password = opt.ApolloPassword;
        // Apollo serves a self-signed cert on localhost — bypass validation for exactly this client.
        // Auth is SESSION-COOKIE based (verified against Apollo 0.4.6 on 2026-07-10): basic auth
        // 401s; the web UI POSTs /api/login {username,password} and rides the returned cookie. The
        // CookieContainer carries it for us; EnsureLoginAsync re-logins when the session lapses.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
        };
        http = new HttpClient(handler) { BaseAddress = new Uri(opt.ApolloBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
    }

    private async Task<bool> EnsureLoginAsync(CancellationToken ct, bool force = false)
    {
        if (loggedIn && !force) return true;
        await loginSem.WaitAsync(ct);
        try
        {
            if (loggedIn && !force) return true;
            var resp = await http.PostAsJsonAsync("api/login", new { username = user, password }, ct);
            loggedIn = resp.IsSuccessStatusCode;
            if (!loggedIn) log.LogWarning("Apollo login failed: {Status}", (int)resp.StatusCode);
            return loggedIn;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Apollo login failed");
            loggedIn = false;
            return false;
        }
        finally { loginSem.Release(); }
    }

    // One authed call with a single re-login retry on 401 (session lapse / Apollo restart).
    private async Task<HttpResponseMessage?> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        if (!await EnsureLoginAsync(ct)) return null;
        var resp = await http.SendAsync(make(), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && await EnsureLoginAsync(ct, force: true))
            resp = await http.SendAsync(make(), ct);
        return resp;
    }

    /// <summary>Complete a Moonlight pairing: the 4-digit PIN the client shows + the device name the
    /// site user typed. Success means Apollo saved the client cert — the caller records the
    /// device→user mapping (HeavyClient) on its side.</summary>
    public async Task<(bool ok, string detail)> PairAsync(string pin, string deviceName, CancellationToken ct)
    {
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/pin")
            { Content = JsonContent.Create(new { pin, name = deviceName }) }, ct);
            if (resp == null) return (false, "Apollo login failed — check the host credentials.");
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

    /// <summary>Apollo's current app list, or null when unreachable.</summary>
    public async Task<List<JsonObject>?> GetAppsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/apps"), ct);
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(body)!.AsObject();
            return root["apps"]!.AsArray().Select(n => n!.AsObject()).ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Apollo app list fetch failed");
            return null;
        }
    }

    /// <summary>Upsert one app. Apollo (unlike stock Sunshine's index scheme) keys edits by the app's
    /// <c>uuid</c>: include it to edit in place, omit it and Apollo creates the app (verified against
    /// its own web UI code, apps-*.js).</summary>
    public async Task<bool> UpsertAppAsync(JsonObject app, CancellationToken ct)
    {
        try
        {
            var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/apps")
            { Content = new StringContent(app.ToJsonString(), Encoding.UTF8, "application/json") }, ct);
            return resp is { IsSuccessStatusCode: true };
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
        if (current == null) return new { ok = false, error = "Apollo is unreachable — is the service running (and are the host creds right)?" };

        var byName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in current)
        {
            var name = (string?)a["name"];
            if (!string.IsNullOrEmpty(name)) byName[name] = a;
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
            bool exists = byName.TryGetValue(app.Title, out var existing);
            // Unchanged? Compare the fields we own; leave hand-tuned extras alone.
            if (exists && SameManagedFields(existing!, compiled)) continue;
            (exists ? updates : adds).Add(app.Title);

            if (apply)
            {
                // Preserve any fields Apollo/hand-editing added that we don't manage — CRUCIALLY the
                // uuid, which is how Apollo knows this is an edit and not a new app.
                var target = exists ? MergeInto(existing!, compiled) : compiled;
                if (await UpsertAppAsync(target, ct)) applied.Add(app.Title);
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
            ["exit-timeout"] = 10, // Apollo stores this as a number (its hand-authored apps show 10)
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
