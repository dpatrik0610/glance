using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glance.Services;

/// One deployment of a Worker or Pages project. Status: "success", "failure", "active" (building), "canceled"…
public record Deploy(string Key, string Name, string Id, string Status, string Message, string Branch, string Author, string Url, DateTime When)
{
    public bool Failed => Status == "failure";
    public bool Final => Status is "success" or "failure" or "canceled" or "skipped";
    public string WhenText => When == default ? "" : When.ToLocalTime().ToString("MMM d, HH:mm");
    public string Line => string.Join(" · ", new[] { Branch, Message, Author }.Where(s => s.Length > 0));
}

public record Zone(string Name, string Status, string Plan);
public record Store(string Kind, string Name, string Size);
public record Usage(long Requests, long Errors, double CpuMs);

/// Read-only Cloudflare: Workers, Pages, zones, storage and Worker traffic, through the user's own API token.
/// The token is kept encrypted for this Windows user (DPAPI) in %AppData%\Glance\cloudflare.dat.
public static class Cloudflare
{
    public const string TokenPage = "https://dash.cloudflare.com/profile/api-tokens";
    static readonly string TokenFile = Path.Combine(Settings.Dir, "cloudflare.dat");
    static readonly HttpClient http = new() { BaseAddress = new("https://api.cloudflare.com/client/v4/"), Timeout = TimeSpan.FromSeconds(15) };
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
    static (string Id, string Name)? account;

    public static bool Connected => File.Exists(TokenFile);
    public static string AccountName => account?.Name ?? "";

    static string? Token()
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is IOException or CryptographicException) { return null; } // gone, or another user's: connect again
    }

    /// Checks the token by finding its account, and only then keeps it. Returns the account's name.
    public static async Task<string> Connect(string token)
    {
        account = null;
        await Account(token.Trim());
        Directory.CreateDirectory(Settings.Dir);
        File.WriteAllBytes(TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser));
        Db.Info($"cloudflare connected: {AccountName}");
        return AccountName;
    }

    public static void Disconnect()
    {
        account = null;
        File.Delete(TokenFile);
    }

    sealed record Envelope<T>(T? Result, bool Success, List<ApiError>? Errors);
    sealed record ApiError(int Code, string Message);

    static async Task<T> Get<T>(string path, string? token = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token() ?? throw new InvalidOperationException("Not connected"));
        using var res = await http.SendAsync(req);
        var env = await res.Content.ReadFromJsonAsync<Envelope<T>>(Json);
        if (env is { Success: true, Result: { } r }) return r;
        throw new HttpRequestException(env?.Errors?.FirstOrDefault()?.Message ?? $"HTTP {(int)res.StatusCode}");
    }

    sealed record AccountJson(string Id, string Name);

    // ponytail: the token's first account; a picker if more than one ever matters.
    static async Task<string> Account(string? token = null)
    {
        if (account is { } a) return a.Id;
        var list = await Get<List<AccountJson>>("accounts", token);
        if (list.Count == 0) throw new HttpRequestException("This token can't see any account");
        account = (list[0].Id, list[0].Name);
        return list[0].Id;
    }

    // ---- Workers ----

    sealed record ScriptJson(string Id, DateTime ModifiedOn);
    sealed record WorkerDeploys(List<WorkerDeployJson> Deployments);
    sealed record WorkerDeployJson(string Id, DateTime CreatedOn, string? Source, string? AuthorEmail, Dictionary<string, string>? Annotations);

    static Deploy Of(string name, WorkerDeployJson d) => new("worker:" + name, name, d.Id, "success",
        d.Annotations?.GetValueOrDefault("workers/message") ?? "", d.Source ?? "", d.AuthorEmail ?? "", "", d.CreatedOn);

    static async Task<List<Deploy>> WorkerHistory(string a, string name) =>
        (await Get<WorkerDeploys>($"accounts/{a}/workers/scripts/{Uri.EscapeDataString(name)}/deployments")).Deployments
            .OrderByDescending(d => d.CreatedOn).Select(d => Of(name, d)).ToList();

    /// Each Worker with its newest deployment.
    public static async Task<List<Deploy>> Workers()
    {
        var a = await Account();
        var scripts = await Get<List<ScriptJson>>($"accounts/{a}/workers/scripts");
        // ponytail: one request per Worker; fine for a personal account, a batched endpoint if it ever has hundreds.
        var latest = await Task.WhenAll(scripts.Select(async s =>
            (await WorkerHistory(a, s.Id)).FirstOrDefault() ?? new Deploy("worker:" + s.Id, s.Id, "", "success", "", "", "", "", s.ModifiedOn)));
        return latest.OrderByDescending(d => d.When).ToList();
    }

    // ---- Pages ----

    sealed record ProjectJson(string Name, string? Subdomain, List<string>? Domains, PagesDeployJson? LatestDeployment);
    sealed record PagesDeployJson(string Id, string? Url, string? Environment, DateTime CreatedOn, StageJson? LatestStage, TriggerJson? DeploymentTrigger);
    sealed record StageJson(string Name, string Status);
    sealed record TriggerJson(TriggerMeta? Metadata);
    sealed record TriggerMeta(string? Branch, string? CommitMessage);

    static Deploy Of(ProjectJson p, PagesDeployJson? d)
    {
        var url = p.Domains?.FirstOrDefault() is { } domain ? "https://" + domain : d?.Url ?? "";
        if (d is null) return new("pages:" + p.Name, p.Name, "", "success", "", "", "", url, default);
        // A finished build stage isn't live yet: only the deploy stage's success is.
        var status = d.LatestStage is { Status: "success", Name: not "deploy" } ? "active" : d.LatestStage?.Status ?? "";
        var message = (d.DeploymentTrigger?.Metadata?.CommitMessage ?? "").Split('\n')[0].Trim();
        return new("pages:" + p.Name, p.Name, d.Id, status, message, d.DeploymentTrigger?.Metadata?.Branch ?? d.Environment ?? "", "", url, d.CreatedOn);
    }

    /// Each Pages project with its newest deployment.
    public static async Task<List<Deploy>> Pages()
    {
        var a = await Account();
        return (await Get<List<ProjectJson>>($"accounts/{a}/pages/projects")).Select(p => Of(p, p.LatestDeployment))
            .OrderByDescending(d => d.When).ToList();
    }

    /// The last 10 deployments of a Worker or Pages project (Deploy.Key).
    public static async Task<List<Deploy>> History(string key)
    {
        var a = await Account();
        var (kind, name) = (key.Split(':', 2)[0], key.Split(':', 2)[1]);
        if (kind == "worker") return (await WorkerHistory(a, name)).Take(10).ToList();
        var project = new ProjectJson(name, null, null, null);
        return (await Get<List<PagesDeployJson>>($"accounts/{a}/pages/projects/{Uri.EscapeDataString(name)}/deployments?per_page=10"))
            .Select(d => Of(project, d)).ToList();
    }

    // ---- Zones, storage, traffic ----

    sealed record ZoneJson(string Name, string Status, PlanJson? Plan);
    sealed record PlanJson(string Name);

    public static async Task<List<Zone>> Zones()
    {
        var a = await Account();
        return (await Get<List<ZoneJson>>($"zones?account.id={a}&per_page=50")).Select(z => new Zone(z.Name, z.Status, z.Plan?.Name ?? "")).ToList();
    }

    sealed record D1Json(string Name, long? FileSize);
    sealed record KvJson(string Title);
    sealed record R2List(List<R2Json> Buckets);
    sealed record R2Json(string Name);

    /// D1, KV and R2. A kind the token may not read is left out rather than failing the rest.
    public static async Task<List<Store>> Storage(IReadOnlyDictionary<string, long> r2Sizes)
    {
        var a = await Account();
        async Task<IEnumerable<Store>> Try(Func<Task<IEnumerable<Store>>> f)
        {
            try { return await f(); }
            catch (HttpRequestException e) { Db.Warn($"cloudflare storage: {e.Message}"); return []; }
        }
        var parts = await Task.WhenAll(
            Try(async () => (await Get<List<D1Json>>($"accounts/{a}/d1/database?per_page=100")).Select(d => new Store("D1", d.Name, Size(d.FileSize)))),
            Try(async () => (await Get<List<KvJson>>($"accounts/{a}/storage/kv/namespaces?per_page=100")).Select(k => new Store("KV", k.Title, ""))),
            Try(async () => (await Get<R2List>($"accounts/{a}/r2/buckets")).Buckets
                .Select(b => new Store("R2", b.Name, Size(r2Sizes.TryGetValue(b.Name, out var s) ? s : null)))));
        return parts.SelectMany(p => p).ToList();
    }

    static string Size(long? bytes) => bytes is not { } b ? "" : b < 1 << 20 ? $"{b / 1024.0:0.#} KB" : b < 1 << 30 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    const string WorkersQuery = """
        query($a: string!, $from: Time!, $to: Time!) { viewer { accounts(filter: {accountTag: $a}) {
          workersInvocationsAdaptive(limit: 1000, filter: {datetime_geq: $from, datetime_leq: $to}) {
            sum { requests errors } quantiles { cpuTimeP50 } dimensions { scriptName } } } } }
        """;
    const string R2Query = """
        query($a: string!, $from: Time!, $to: Time!) { viewer { accounts(filter: {accountTag: $a}) {
          r2StorageAdaptiveGroups(limit: 1000, filter: {datetime_geq: $from, datetime_leq: $to}, orderBy: [datetime_DESC]) {
            max { payloadSize metadataSize } dimensions { bucketName } } } } }
        """;

    /// GraphQL analytics (needs Account Analytics: Read) over the last `days`; returns the account's node.
    static async Task<JsonNode?> Graph(string query, int days)
    {
        var a = await Account();
        var now = DateTime.UtcNow;
        using var req = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = JsonContent.Create(new { query, variables = new { a, from = now.AddDays(-days).ToString("O"), to = now.ToString("O") } }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token() ?? throw new InvalidOperationException("Not connected"));
        using var res = await http.SendAsync(req);
        var root = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (root?["errors"] is JsonArray { Count: > 0 } errors) throw new HttpRequestException(errors[0]?["message"]?.GetValue<string>() ?? "analytics");
        return root?["data"]?["viewer"]?["accounts"]?[0];
    }

    /// Requests, errors and median CPU per Worker over the last 24 hours.
    public static async Task<Dictionary<string, Usage>> Traffic()
    {
        var workers = new Dictionary<string, Usage>();
        foreach (var g in (await Graph(WorkersQuery, 1))?["workersInvocationsAdaptive"]?.AsArray() ?? [])
        {
            var name = g!["dimensions"]!["scriptName"]!.GetValue<string>();
            var u = new Usage(g["sum"]!["requests"]!.GetValue<long>(), g["sum"]!["errors"]!.GetValue<long>(), g["quantiles"]!["cpuTimeP50"]!.GetValue<double>() / 1000);
            workers[name] = workers.TryGetValue(name, out var o) ? new(o.Requests + u.Requests, o.Errors + u.Errors, Math.Max(o.CpuMs, u.CpuMs)) : u;
        }
        return workers;
    }

    /// Each R2 bucket's current size in bytes (Cloudflare updates it a few times a day).
    public static async Task<Dictionary<string, long>> R2Sizes()
    {
        var r2 = new Dictionary<string, long>();
        foreach (var g in (await Graph(R2Query, 2))?["r2StorageAdaptiveGroups"]?.AsArray() ?? []) // newest first: the first row per bucket is its size
            r2.TryAdd(g!["dimensions"]!["bucketName"]!.GetValue<string>(), g["max"]!["payloadSize"]!.GetValue<long>() + g["max"]!["metadataSize"]!.GetValue<long>());
        return r2;
    }

    /// Remembers each project's newest deployment and reports the ones that newly finished (live or failed).
    /// The first look only remembers, so what already exists doesn't alert. A build still running isn't remembered,
    /// so it reports once it ends.
    public sealed class Watch
    {
        readonly Dictionary<string, string> seen = [];
        bool primed;

        public List<Deploy> Check(IEnumerable<Deploy> now)
        {
            var fresh = new List<Deploy>();
            foreach (var d in now)
            {
                if (d.Id.Length == 0 || !d.Final) continue;
                var state = d.Id + "|" + d.Status;
                if (seen.GetValueOrDefault(d.Key) == state) continue;
                seen[d.Key] = state;
                if (primed && d.Status is "success" or "failure") fresh.Add(d);
            }
            primed = true;
            return fresh;
        }
    }
}
