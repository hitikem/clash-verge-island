using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClashVergeIsland;

/// <summary>一个节点（代理）以及它最近一次测速的延迟。</summary>
public sealed class ClashNode
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";

    /// <summary>最近一次测速的延迟（毫秒）；没有记录时为 -1。</summary>
    public int Delay { get; init; } = -1;

    /// <summary>内核认为这个节点是否可用（测速失败会被标记为不可用）。</summary>
    public bool Alive { get; init; } = true;
}

/// <summary>一个策略组（Selector / URLTest / Fallback / LoadBalance）。</summary>
public sealed class ClashGroup
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";

    /// <summary>当前选中的节点名（LoadBalance 类型没有）。</summary>
    public string Now { get; init; } = "";

    /// <summary>这个策略组自己配置的测速地址；有就优先用它（比插件写死的地址可靠）。</summary>
    public string TestUrl { get; init; } = "";

    public List<ClashNode> Nodes { get; } = new();
}

/// <summary>内核的流量计数（累计字节数 + 当前活动连接数）。</summary>
public sealed record ClashTraffic(long Up, long Down, int Connections);

/// <summary>/configs 里我们关心的那几项。</summary>
public sealed record ClashConfig(string Mode, bool TunEnabled, int MixedPort);

/// <summary>当前节点对某个测速网站的延迟。</summary>
public sealed record ClashSiteLatency(string Name, string Url, int Delay);

/// <summary>
/// 一条正在活动的连接（"谁在用流量"）。
/// Process 在 Windows 上不一定有值 —— 取决于内核有没有权限拿到进程名，
/// 拿不到就靠 Host 显示，功能不受影响。
/// </summary>
public sealed record ClashConnection(
    string Id, string Host, string Process, long Download, long Upload, string Rule, string Chains)
{
    public long Total => Download + Upload;
}

/// <summary>一次刷新拿到的全部状态。</summary>
public sealed class ClashSnapshot
{
    public bool Connected { get; set; }

    /// <summary>连接失败时的中文原因（连不上 / 密码不对 / 超时…）。</summary>
    public string? Error { get; set; }

    /// <summary>rule / global / direct</summary>
    public string Mode { get; set; } = "";

    /// <summary>内核版本，例如 v1.19.31</summary>
    public string Version { get; set; } = "";

    /// <summary>每秒上传 / 下载字节数；-1 表示还没算出来。</summary>
    public double UpPerSec { get; set; } = -1;
    public double DownPerSec { get; set; } = -1;

    public List<ClashGroup> Groups { get; set; } = new();

    public string ActiveGroup { get; set; } = "";
    public string ActiveNode { get; set; } = "";
    public int ActiveDelay { get; set; } = -1;

    /// <summary>最近若干次采样的上下行速度（字节/秒），聚光卡拿来画趋势曲线。</summary>
    public double[] DownHistory { get; set; } = Array.Empty<double>();
    public double[] UpHistory { get; set; } = Array.Empty<double>();

    /// <summary>当前活动连接数。</summary>
    public int Connections { get; set; }

    /// <summary>当前节点对各个测速网站的延迟（岛上和卡片里都显示它）。</summary>
    public List<ClashSiteLatency> Sites { get; set; } = new();

    /// <summary>延迟的历史采样（每次自动测速记一个点），用来看变化规律。</summary>
    public int[] DelayTrend { get; set; } = Array.Empty<int>();

    /// <summary>本次运行累计上传 / 下载字节数。</summary>
    public long UpTotal { get; set; }
    public long DownTotal { get; set; }

    /// <summary>TUN 模式是否开启。</summary>
    public bool TunEnabled { get; set; }

    /// <summary>混合代理端口。</summary>
    public int MixedPort { get; set; }

    /// <summary>按流量从大到小排的活动连接（卡片里显示"谁在用流量"）。</summary>
    public List<ClashConnection> TopConnections { get; set; } = new();

    /// <summary>今日 / 本月的累计流量（自己攒的，见 TrafficStats）。</summary>
    public long TodayUp { get; set; }
    public long TodayDown { get; set; }
    public long MonthUp { get; set; }
    public long MonthDown { get; set; }
}

/// <summary>接口返回了非 2xx 时抛出（带状态码，方便翻译成人话）。</summary>
internal sealed class ClashApiException : Exception
{
    public int StatusCode { get; }

    public ClashApiException(int statusCode, string body)
        : base($"HTTP {statusCode}: {body}") => StatusCode = statusCode;
}

/// <summary>
/// Clash Verge（mihomo 内核）的本地 REST API 客户端。
///
/// 前提：Clash Verge → 设置 → Clash 设置 → External → 打开「Enable External Controller」。
/// 从 v2.4.0 起它默认是关的，关着的时候这里是「连接被拒绝」，不是密码错。
/// </summary>
internal sealed class ClashVergeApi : IDisposable
{
    private readonly HttpClient _http;
    private string _base = "http://127.0.0.1:9097";

    public ClashVergeApi()
    {
        // 关键：绕开系统代理。Clash Verge 通常会设置系统代理，
        // 走代理去访问 127.0.0.1 是多余的，还可能被规则拦下来。
        var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    public string BaseUrl => _base;

    /// <summary>每次取数前用最新设置重新配置（设置可能随时被改）。</summary>
    public void Configure(string host, int port, string secret)
    {
        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (port is <= 0 or > 65535) port = 9097;
        _base = $"http://{host}:{port}";

        _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(secret)
            ? null
            : new AuthenticationHeaderValue("Bearer", secret.Trim());
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _base + path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ClashApiException((int)response.StatusCode, Trim(body));
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];

    /// <summary>GET /version —— 同时用作「连通性 + 密码是否正确」的探针。</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/version", null, ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
    }

    /// <summary>GET /configs → 当前模式、TUN 开关、混合端口。</summary>
    public async Task<ClashConfig> GetConfigAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/configs", null, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
        var mixedPort = root.TryGetProperty("mixed-port", out var p) && p.TryGetInt32(out var pv) ? pv : 0;

        var tun = false;
        if (root.TryGetProperty("tun", out var t) && t.ValueKind == JsonValueKind.Object &&
            t.TryGetProperty("enable", out var e))
        {
            tun = e.ValueKind == JsonValueKind.True;
        }

        return new ClashConfig(mode, tun, mixedPort);
    }

    /// <summary>GET /connections → 累计上传 / 下载字节数 + 活动连接数。</summary>
    public async Task<ClashTraffic> GetTrafficAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/connections", null, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var up = root.TryGetProperty("uploadTotal", out var u) && u.TryGetInt64(out var uv) ? uv : 0;
        var down = root.TryGetProperty("downloadTotal", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
        var count = root.TryGetProperty("connections", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.GetArrayLength()
            : 0;

        return new ClashTraffic(up, down, count);
    }

    /// <summary>
    /// GET /proxies → 所有策略组及其节点、当前选择、延迟。
    ///
    /// <paramref name="testUrl"/> 是当前选中的测速网站：内核把不同网站的测速结果
    /// 分开存在每个节点的 extra 里（extra["https://www.youtube.com"].history），
    /// 所以要看哪个网站的延迟就得按那个网址去取；取不到才退回默认测速的 history。
    /// </summary>
    public async Task<List<ClashGroup>> GetGroupsAsync(string testUrl, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/proxies", null, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("proxies", out var proxies) ||
            proxies.ValueKind != JsonValueKind.Object)
        {
            return new List<ClashGroup>();
        }

        // 先把每个代理的「类型」「是否可用」「默认延迟」「指定网站的延迟」收集起来
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var delays = new Dictionary<string, int>(StringComparer.Ordinal);
        var urlDelays = new Dictionary<string, int>(StringComparer.Ordinal);
        var alive = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var property in proxies.EnumerateObject())
        {
            types[property.Name] = ReadString(property.Value, "type");

            alive[property.Name] =
                !(property.Value.TryGetProperty("alive", out var a) && a.ValueKind == JsonValueKind.False);

            var fromHistory = LastDelay(property.Value, "history");
            if (fromHistory >= 0) delays[property.Name] = fromHistory;

            if (!string.IsNullOrWhiteSpace(testUrl))
            {
                var fromUrl = LastDelayOf(property.Value, testUrl);
                if (fromUrl >= 0) urlDelays[property.Name] = fromUrl;
            }
        }

        var groups = new List<ClashGroup>();
        foreach (var property in proxies.EnumerateObject())
        {
            // 有 all 数组的才是策略组
            if (!property.Value.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var group = new ClashGroup
            {
                Name = property.Name,
                Type = ReadString(property.Value, "type"),
                Now = ReadString(property.Value, "now"),
                TestUrl = ReadString(property.Value, "testUrl"),
            };

            foreach (var element in all.EnumerateArray())
            {
                var name = element.GetString();
                if (string.IsNullOrEmpty(name)) continue;

                group.Nodes.Add(new ClashNode
                {
                    Name = name,
                    Type = types.TryGetValue(name, out var t) ? t : "",
                    // 优先用「当前选中网站」测出来的结果；那个网站还没测过才退回默认延迟
                    Delay = urlDelays.TryGetValue(name, out var byUrl)
                        ? byUrl
                        : delays.TryGetValue(name, out var byDefault) ? byDefault : -1,
                    Alive = !alive.TryGetValue(name, out var av) || av,
                });
            }

            groups.Add(group);
        }

        return groups;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>读 element["history"] 里最后一条的 delay；没测出来（0）也算没有，返回 -1。</summary>
    private static int LastDelay(JsonElement element, string historyName)
    {
        if (!element.TryGetProperty(historyName, out var history) ||
            history.ValueKind != JsonValueKind.Array || history.GetArrayLength() == 0)
        {
            return -1;
        }

        var last = history[history.GetArrayLength() - 1];
        if (!last.TryGetProperty("delay", out var delay) || !delay.TryGetInt32(out var ms)) return -1;

        // 内核在超时/失败时给的是 0；0 不是"很快"，是"没测出来"
        return ms > 0 ? ms : -1;
    }

    /// <summary>读 element["extra"][url]["history"] 里最后一条的 delay。</summary>
    private static int LastDelayOf(JsonElement element, string url)
    {
        if (!element.TryGetProperty("extra", out var extra) || extra.ValueKind != JsonValueKind.Object)
        {
            return -1;
        }

        if (!extra.TryGetProperty(url, out var entry) || entry.ValueKind != JsonValueKind.Object)
        {
            return -1;
        }

        return LastDelay(entry, "history");
    }

    /// <summary>PUT /proxies/{组} —— 切换该策略组选中的节点。</summary>
    public Task SelectNodeAsync(string group, string node, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "/proxies/" + Uri.EscapeDataString(group),
            JsonSerializer.Serialize(new { name = node }), ct);

    /// <summary>PATCH /configs —— 切换模式。</summary>
    public Task SetModeAsync(string mode, CancellationToken ct) =>
        SendAsync(new HttpMethod("PATCH"), "/configs",
            JsonSerializer.Serialize(new { mode }), ct);

    /// <summary>
    /// GET /group/{组}/delay —— 给整组节点测速。
    /// 优先用策略组自己配的 testUrl；它没配才退回一个通用地址。
    /// </summary>
    public async Task<int> TestGroupAsync(string group, string testUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(testUrl))
        {
            testUrl = "https://www.gstatic.com/generate_204";
        }

        var path = $"/group/{Uri.EscapeDataString(group)}/delay" +
                   $"?url={Uri.EscapeDataString(testUrl)}&timeout=5000";

        using var doc = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);

        var ok = 0;
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.TryGetInt32(out var ms) && ms > 0) ok++;
        }

        return ok;
    }

    /// <summary>
    /// GET /connections → 正在活动的连接，按流量从大到小排。
    /// 这是"我没下东西为什么这么慢"的答案：能看到是哪个域名 / 哪个进程在吃带宽。
    /// </summary>
    public async Task<List<ClashConnection>> GetConnectionsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/connections", null, ct).ConfigureAwait(false);
        var list = new List<ClashConnection>();

        if (!doc.RootElement.TryGetProperty("connections", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var up = item.TryGetProperty("upload", out var u) && u.TryGetInt64(out var uv) ? uv : 0;
            var down = item.TryGetProperty("download", out var d) && d.TryGetInt64(out var dv) ? dv : 0;

            var host = "";
            var process = "";
            var rule = ReadString(item, "rule");

            if (item.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                host = ReadString(meta, "host");
                if (string.IsNullOrWhiteSpace(host)) host = ReadString(meta, "destinationIP");
                process = ReadString(meta, "process");
                if (string.IsNullOrWhiteSpace(rule)) rule = ReadString(meta, "rule");
            }

            var chains = "";
            if (item.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array)
            {
                chains = string.Join(" → ", ch.EnumerateArray().Select(x => x.GetString() ?? ""));
            }

            list.Add(new ClashConnection(ReadString(item, "id"), host, process, down, up, rule, chains));
        }

        return list.OrderByDescending(c => c.Total).ToList();
    }

    /// <summary>
    /// 查**真实出口 IP** 的归属地（走代理发请求）。
    ///
    /// 为什么必须这样：节点名经常骗人 —— 「自动选择」「故障转移」「香港01」的实际落地
    /// 可能在日本。只有出口 IP 的归属地是真的。
    /// 代价：这个请求会经过代理到达第三方归属地服务，对方能看到你的出口 IP
    /// （Clash Verge 自己的「IP 信息」面板也是同一做法）。
    /// </summary>
    public static async Task<string> GetExitCountryAsync(
        int proxyPort, string url, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                // 这里**必须走代理**（和访问本地接口相反）—— 要的就是"同一个出口"
                UseProxy = true,
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort}"),
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            var body = await client.GetStringAsync(url, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 各家字段名不一样，都试一遍
            foreach (var key in new[] { "country", "country_code", "countryCode" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var code = value.GetString() ?? "";
                    if (code.Length == 2) return code.ToUpperInvariant();
                }
            }
        }
        catch (Exception)
        {
            // 查不到就让调用方退回按节点名判断
        }

        return "";
    }

    /// <summary>GET /providers/proxies → 所有「代理订阅」的名字。</summary>
    public async Task<List<string>> GetProxyProviderNamesAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/providers/proxies", null, ct).ConfigureAwait(false);

        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("providers", out var providers) &&
            providers.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providers.EnumerateObject())
            {
                list.Add(property.Name);
            }
        }

        return list;
    }

    /// <summary>PUT /providers/proxies/{名} —— 让这个订阅立刻去拉最新的节点。</summary>
    public Task UpdateProviderAsync(string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "/providers/proxies/" + Uri.EscapeDataString(name), null, ct);

    /// <summary>DELETE /connections/{id} —— 断开一条连接。</summary>
    public Task CloseConnectionAsync(string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, "/connections/" + Uri.EscapeDataString(id), null, ct);

    /// <summary>DELETE /connections —— 断开全部连接。</summary>
    public Task CloseAllConnectionsAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, "/connections", null, ct);

    /// <summary>PATCH /configs —— 开关 TUN 模式。</summary>
    public Task SetTunAsync(bool enabled, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, "/configs",
            "{\"tun\":{\"enable\":" + (enabled ? "true" : "false") + "}}", ct);

    /// <summary>
    /// GET /group/{组}/delay —— 整组测速，返回「节点名 → 延迟」。
    /// 「一键切最快节点」就是靠它：测完挑最小的那个切过去。
    /// </summary>
    public async Task<Dictionary<string, int>> TestGroupDelaysAsync(
        string group, string testUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(testUrl))
        {
            testUrl = "https://www.gstatic.com/generate_204";
        }

        var path = $"/group/{Uri.EscapeDataString(group)}/delay" +
                   $"?url={Uri.EscapeDataString(testUrl)}&timeout=5000";

        using var doc = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.TryGetInt32(out var ms) && ms > 0)
            {
                result[property.Name] = ms;
            }
        }

        return result;
    }

    /// <summary>
    /// GET /proxies/{节点} → 这一个节点对**每个**测速网站的延迟。
    /// 内核按网站分开存（extra["https://www.youtube.com"].history），所以能一次全取出来。
    /// </summary>
    public async Task<Dictionary<string, int>> GetNodeSiteDelaysAsync(
        string nodeName, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(nodeName) || urls.Count == 0) return result;

        using var doc = await SendAsync(
            HttpMethod.Get, "/proxies/" + Uri.EscapeDataString(nodeName), null, ct).ConfigureAwait(false);

        foreach (var url in urls)
        {
            var delay = LastDelayOf(doc.RootElement, url);
            if (delay >= 0) result[url] = delay;
        }

        return result;
    }

    /// <summary>GET /proxies/{节点}/delay —— 给单个节点测速（切完节点顺手测一下）。</summary>
    public async Task<int> TestNodeAsync(string node, string testUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(testUrl))
        {
            testUrl = "https://www.gstatic.com/generate_204";
        }

        var path = $"/proxies/{Uri.EscapeDataString(node)}/delay" +
                   $"?url={Uri.EscapeDataString(testUrl)}&timeout=5000";

        using var doc = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);

        return doc.RootElement.TryGetProperty("delay", out var d) && d.TryGetInt32(out var ms) && ms > 0
            ? ms
            : -1;
    }

    public void Dispose() => _http.Dispose();
}
