using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace ClashVergeIsland;

/// <summary>
/// Clash 小岛：把本机 Clash Verge（mihomo 内核）的状态搬到灵动岛上。
///
/// 小岛：当前节点 + 延迟；展开：模式 + 实时网速；点击：聚光卡（切节点、切模式、测延迟）。
/// 数据来自 Clash Verge 的本地 REST API —— 需要用户先在 Clash Verge 里打开「外部控制」。
/// </summary>
public sealed class ClashVergeIslandPlugin : IslandPluginBase
{
    private const int DefaultPort = 9097;

    /// <summary>
    /// 默认岛体优先级。取 150 而不是插件常见的 30：
    /// 内置音乐模块放歌时会把优先级抬到 200，定低了插件在放歌时永远看不见。
    /// 150 的效果是「放歌时让给音乐，不放歌时自己显示」——两边都不用抢。
    /// </summary>
    private const int DefaultPriority = 150;

    /// <summary>趋势曲线保留的采样点数（每次刷新一个点，约 2 秒一个）。</summary>
    private const int HistoryLength = 60;

    private readonly ClashVergeApi _api = new();

    /// <summary>今日 / 本月流量（内核只给"自启动以来"，所以自己攒并落盘）。</summary>
    private TrafficStats _traffic = new();
    private int _trafficSaveTick;
    private readonly Queue<double> _downHistory = new();
    private readonly Queue<double> _upHistory = new();

    /// <summary>国旗图片库（岛视图和聚光卡共用）。</summary>
    private FlagLibrary _flags = null!;

    /// <summary>刷新排队串行化：同一时刻只跑一次，但后来的请求排队而不是被丢掉（sdk-api §15.2）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ClashSnapshot _snapshot = new();
    private ClashIslandView _view = null!;
    private ClashIslandSpotlightView? _spotlight;
    private IslandLiveContent _content = null!;

    /// <summary>策略组列表比较贵（节点多时 JSON 很大），每 3 次刷新才拉一次。</summary>
    private List<ClashGroup> _groups = new();
    private int _tick;

    private long _lastUp = -1;
    private long _lastDown = -1;
    private DateTimeOffset _lastTrafficAt;

    /// <summary>平滑后的网速。-1 表示还没算出第一次。</summary>
    private double _upSmooth = -1;
    private double _downSmooth = -1;

    /// <summary>上一次取到的"当前节点对各个网站的延迟"，用于非重拉轮次沿用。</summary>
    private Dictionary<string, int> _siteDelays = new(StringComparer.Ordinal);

    /// <summary>上一次自动测速的时间（"自动测速"关掉时用不到）。</summary>
    private DateTimeOffset _lastAutoTest = DateTimeOffset.MinValue;

    /// <summary>上一次"全网站测速"的时间，卡片打开时用它判断数据够不够新。</summary>
    private DateTimeOffset _lastSiteTest = DateTimeOffset.MinValue;

    /// <summary>0/1：全网站测速是否正在跑（给 Interlocked 用）。入口防重入，见 TestAllSitesAsync。</summary>
    private int _siteTestRunning;

    /// <summary>
    /// 延迟的历史采样：每完成一轮网站测速就记一个点。
    /// 横轴间隔 = 刷新间隔，所以是均匀的 —— 这样才看得出"变化规律"，而不只是当前值。
    /// </summary>
    private const int DelayTrendLength = 48;
    private readonly Queue<int> _delayTrend = new();

    /// <summary>
    /// 卡片打开时调用：网站延迟超过 5 分钟没更新就自动测一遍，
    /// 这样点开卡片看到的就是新鲜数据，不用手动点「全部测试」。
    /// </summary>
    public void EnsureFreshSiteDelays()
    {
        if (_stopped || !_snapshot.Connected) return;
        if (DateTimeOffset.UtcNow - _lastSiteTest < TimeSpan.FromMinutes(5)) return;

        _ = TestAllSitesAsync();
    }

    /// <summary>网站延迟自动刷新间隔（秒）；0 = 关闭。默认 25 秒（一天约 2.8 MB）。</summary>
    private int SiteRefreshSeconds => Settings.Get("siterefresh", 25);

    /// <summary>距离上次"全网站测速"过去了多少秒；-1 表示还没测过。</summary>
    public int SiteTestAgeSeconds => _lastSiteTest == DateTimeOffset.MinValue
        ? -1
        : (int)(DateTimeOffset.UtcNow - _lastSiteTest).TotalSeconds;

    /// <summary>
    /// 到点了就重测一遍当前节点对各个网站的延迟 —— 这样卡片和岛上的那四个数字才是"实时"的。
    /// 必须从 _gate 外面调用（见 OnInitializeAsync 里的说明）。
    /// </summary>
    private async Task MaybeRefreshSiteDelaysAsync()
    {
        var seconds = SiteRefreshSeconds;
        if (seconds <= 0 || _stopped || !_snapshot.Connected) return;
        if (DateTimeOffset.UtcNow - _lastSiteTest < TimeSpan.FromSeconds(seconds)) return;

        // 防重入交给 TestAllSitesAsync 自己（它有三个调用方）
        await TestAllSitesAsync().ConfigureAwait(false);
    }

    private bool _stopped;

    /// <summary>聚光卡当前选中的分组（用户手动选过就记住）。</summary>
    public string SpotlightGroup { get; private set; } = "";

    /// <summary>节点列表要不要按延迟从快到慢排。默认关：开着的时候测速会让行不停跳动。</summary>
    public bool SortByDelay => Settings.Get("sort", false);

    /// <summary>记住"按延迟排序"这个开关。</summary>
    public void SetSortByDelay(bool value)
    {
        if (Settings.Get("sort", false) == value) return;
        Settings.Set("sort", value);
    }

    /// <summary>节点列表要不要藏掉订阅附带的信息条目（剩余流量 / 套餐到期…）。默认藏。</summary>
    public bool HideInfoEntries => Settings.Get("hideinfo", true);

    public void SetHideInfoEntries(bool value)
    {
        if (Settings.Get("hideinfo", true) == value) return;
        Settings.Set("hideinfo", value);
    }

    // ---- 测速网站（和 Clash Verge 界面上那套一致：Apple / GitHub / Google / YouTube）----

    /// <summary>Clash Verge 里配置的测速网站，启动时从它的 verge.yaml 读一次。</summary>
    private List<ClashTestTargets.Target> _targets = new();

    public IReadOnlyList<ClashTestTargets.Target> Targets => _targets;

    public string SelectedTargetName => Settings.Get("target", "GitHub");

    /// <summary>当前选中网站的测速地址；没读到配置就退回第一个。</summary>
    private string SelectedTargetUrl =>
        _targets.FirstOrDefault(t => string.Equals(t.Name, SelectedTargetName, StringComparison.Ordinal))?.Url
        ?? _targets.FirstOrDefault()?.Url
        ?? "";

    /// <summary>切换测速网站：换完之后立刻按新网站重新读一遍延迟。</summary>
    public void SetTarget(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.Equals(name, SelectedTargetName, StringComparison.Ordinal)) return;

        Settings.Set("target", name);
        _tick = 0;                      // 强制下一轮重拉策略组
        _ = RefreshAsync($"测速网站 → {name}");
    }

    /// <summary>岛体当前是否浅色（聚光卡配色要用）。</summary>
    public bool ThemeIsLight => Theme.IsLight;

    // ---- 聚光卡外观（用户可自定义）----

    /// <summary>2.0 起外观固定为「液态玻璃」，不再提供自定义。</summary>
    public string CardStyle => "glass";

    /// <summary>2.0 起外观固定：颜色不可调，这里只是占位值。</summary>
    public string CardColorHex => "#ECF2FF";

    /// <summary>2.0 起外观固定：不透明度不可调。</summary>
    public int CardOpacity => 62;

    /// <summary>2.0 起岛体不再自绘背景 —— 用宿主自带的外观，胶囊颜色不可调。</summary>
    public bool IslandOwnBackground => false;

    /// <summary>2.0 起外观固定：岛体与卡片的明暗一律跟着岛体主题走。</summary>
    public bool AppearanceIsLight => Theme.IsLight;

    /// <summary>2.0 起外观固定：基色就是默认的冷色玻璃。</summary>
    public Windows.UI.Color AppearanceBaseColor => Windows.UI.Color.FromArgb(255, 0xEC, 0xF2, 0xFF);

    /// <summary>2.0 起外观不可自定义 —— 设置页的相关入口已移除，这里保留空实现。</summary>
    public void SetCardAppearance(string? style = null, string? colorHex = null, int? opacity = null)
    {
        // 外观固定，什么都不做
    }

    /// <summary>把 "#RRGGBB" 解析成颜色；解析不了就给个中性深蓝。</summary>
    public static Windows.UI.Color ParseHexColor(string? hex, Windows.UI.Color fallback)
    {
        var text = (hex ?? "").Trim().TrimStart('#');
        if (text.Length == 6 &&
            byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(text[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(text[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }

        return fallback;
    }

    // ---- 订阅更新 / 场景切换 ----

    /// <summary>触发所有代理订阅立即更新。返回成功触发几个。</summary>
    public async Task<int> UpdateSubscriptionsAsync()
    {
        try
        {
            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var names = await _api.GetProxyProviderNamesAsync(cts.Token).ConfigureAwait(false);

            if (names.Count == 0)
            {
                Log.Warn("更新订阅：没找到代理订阅（provider）");
                return 0;
            }

            var ok = 0;
            foreach (var name in names)
            {
                try
                {
                    await _api.UpdateProviderAsync(name, cts.Token).ConfigureAwait(false);
                    ok++;
                    Log.Info($"订阅已触发更新：{name}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"订阅 {name} 更新失败：{ex.Message}");
                }
            }

            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"更新订阅失败：{ex.Message}");
            return 0;
        }
    }

    /// <summary>场景 → 节点名关键词。匹配不到就退回「切最快」，绝不什么都不做。</summary>
    private static readonly Dictionary<string, string[]> SceneKeywords = new(StringComparer.Ordinal)
    {
        ["video"] = new[] { "流媒体", "奈飞", "Netflix", "Disney", "YouTube", "解锁", "影视", "媒体" },
        ["game"] = new[] { "游戏", "低延迟", "IPLC", "IEPL", "专线", "加速", "Game" },
        ["download"] = new[] { "大带宽", "高速", "下载", "不限", "GB" },
    };

    /// <summary>按场景一键切换：按节点名关键词挑，多个匹配取当前延迟最低的那个。</summary>
    public async Task<string?> SwitchBySceneAsync(string scene)
    {
        var group = _snapshot.Groups.FirstOrDefault(
            g => string.Equals(g.Name, _snapshot.ActiveGroup, StringComparison.Ordinal));

        if (group is null || !SceneKeywords.TryGetValue(scene, out var keywords)) return null;

        var nodes = HideInfoEntries
            ? group.Nodes.Where(n => !ClashFormat.IsInfoEntry(n.Name)).ToList()
            : group.Nodes.ToList();

        var matched = nodes
            .Where(n => keywords.Any(k => n.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matched.Count == 0)
        {
            Log.Info($"场景 {scene}：没有匹配的节点，退回一键切最快");
            return await SwitchToFastestAsync().ConfigureAwait(false);
        }

        var pick = matched
            .Where(n => n.Delay > 0)
            .OrderBy(n => n.Delay)
            .Select(n => n.Name)
            .FirstOrDefault() ?? matched[0].Name;

        Log.Info($"场景 {scene} → {pick}");
        await SelectNodeAsync(group.Name, pick).ConfigureAwait(false);
        return pick;
    }

    /// <summary>
    /// 真实出口 IP 的归属地（两位国家码）。空 = 还没查到，视图会退回按节点名判断。
    /// 节点名经常骗人（「自动选择」「故障转移」），只有出口 IP 是真的。
    /// </summary>
    public string ExitCountry { get; private set; } = "";

    private DateTimeOffset _lastGeoAt;

    /// <summary>立刻重查出口归属地（设置页 / 岛上的刷新按钮）。</summary>
    public async Task RefreshExitNowAsync()
    {
        _lastGeoAt = default;   // 清掉节流时间戳，下一次调用就会真的发请求
        await MaybeRefreshExitCountryAsync().ConfigureAwait(false);
    }

    /// <summary>按间隔查一次出口归属地（默认 300 秒，和 Clash Verge 的 IP 信息一致）。</summary>
    private async Task MaybeRefreshExitCountryAsync()
    {
        if (!Settings.Get("ipgeo", true)) return;

        var interval = Math.Clamp(Settings.Get("ipgeosec", 300), 60, 3600);
        if (_lastGeoAt != default && (DateTimeOffset.UtcNow - _lastGeoAt).TotalSeconds < interval) return;

        _lastGeoAt = DateTimeOffset.UtcNow;

        // 注意用**混合代理端口**（7897），不是外部控制端口（9097）——
        // 前者才是 HTTP 代理，后者是 Clash 的 REST 接口，当代理用必然连不上。
        var proxyPort = _snapshot.MixedPort > 0 ? _snapshot.MixedPort : 7897;

        // 多服务回退：单一服务在国内网络经常不通（ipinfo.io 尤其不稳），
        // 挨个试，哪个先回就用哪个。用户也可以在设置里指定 ipgeourl 只走那一个。
        var configured = Settings.Get("ipgeourl", "");
        var candidates = string.IsNullOrWhiteSpace(configured)
            ? new[]
              {
                  "https://api.ip.sb/geoip",
                  "https://ipwho.is/",
                  "https://ipinfo.io/json",
              }
            : new[] { configured };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        var code = "";
        foreach (var url in candidates)
        {
            code = await ClashVergeApi.GetExitCountryAsync(proxyPort, url, cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(code))
            {
                Log.Info($"出口归属地：{code}（来源 {url}）");
                break;
            }
        }

        if (string.IsNullOrEmpty(code))
        {
            Log.Warn($"出口归属地查询失败（代理端口 {proxyPort}，试了 {candidates.Length} 个服务）");
            return;
        }

        if (string.Equals(code, ExitCountry, StringComparison.Ordinal)) return;

        ExitCountry = code;
        Log.Info($"出口归属地：{code}");

        // 归属地变了要立刻重画国旗
        Context.RunOnUI(() =>
        {
            _view.Apply(_snapshot);
            _spotlight?.Apply(_snapshot);
        });
    }

    protected override Task OnInitializeAsync()
    {
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}，插件目录 {PluginDirectory}");

        // 今日/本月流量是攒出来的，从磁盘接着算
        _traffic = TrafficStats.Load(PluginDirectory);

        _flags = new FlagLibrary(PluginDirectory);
        _targets = ClashTestTargets.Load();

        // 一个中国原则：台湾节点显示中华人民共和国国旗（设置页可改）
        ClashFormat.TaiwanAsChina = Settings.Get("twchina", true);

        Log.Info($"测速网站（{_targets.Count} 个）：{string.Join("、", _targets.Select(t => t.Name))}，当前用 {SelectedTargetName}");
        _view = new ClashIslandView(Manifest, Theme, _flags, this);

        _content = CreateContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE774", BuildSettingsPage, order: 80));

        // 显示开关实时生效
        Context.Register(Context.OnSettingsChanged("enabled", () =>
        {
            SetContent(Settings.Get("enabled", true) ? _content : null);
        }));

        // 优先级改了要**重新注册**才生效：它是注册时交给宿主的，改设置本身不会动岛体
        Context.Register(Context.OnSettingsChanged("priority", () =>
        {
            _content = CreateContent();
            if (Settings.Get("enabled", true)) SetContent(_content);
            Log.Info($"岛体优先级已改为 {Settings.Get("priority", DefaultPriority)}");
        }));

        // 连接参数一改就立刻重新取数
        foreach (var key in new[] { "host", "port", "secret" })
        {
            Context.Register(Context.OnSettingsChanged(key, () => _ = RefreshAsync($"设置变更（{key}）")));
        }

        // 旗帜规则改了，下一帧就要重画（把当前快照再铺一遍）
        Context.Register(Context.OnSettingsChanged("twchina", () =>
        {
            ClashFormat.TaiwanAsChina = Settings.Get("twchina", true);
            _view.Apply(_snapshot);
            _spotlight?.Apply(_snapshot);
        }));

        // 2.0：外观固定，不再监听外观相关设置（自定义入口已全部移除）

        if (Settings.Get("enabled", true))
        {
            SetContent(_content);
        }

        _ = RefreshAsync("首次加载");

        // UI 线程定时器（1 秒一跳）；插件停用时宿主自动停掉。
        // 注意：网站延迟的自动重测要放在 RefreshAsync **外面**——
        // RefreshAsync 持着 _gate，而重测结束后又要进 RefreshAsync，会自己把自己锁死。
        Context.CreateTimer(TimeSpan.FromSeconds(1), repeat: true, () => _ = TickAsync());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 组装岛体内容。抽成方法是为了改优先级时能重建一份 ——
    /// 优先级是注册时交给宿主的，光改设置不会让已经注册的内容换优先级。
    /// </summary>
    private IslandLiveContent CreateContent() => new()
    {
        Priority = Settings.Get("priority", DefaultPriority),
        OwnerLabel = Manifest.Name,
        OwnerGlyph = Manifest.IconGlyph,
        OwnerAccent = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF),
        MorphView = _view,
        CompactSize = new Windows.Foundation.Size(180, 40),
        ExpandedSize = new Windows.Foundation.Size(420, 176),
        OnTap = OpenSpotlight,          // 点击岛体 = 打开聚光卡
    };

    /// <summary>1 秒一跳，但取数本身按各自该有的节奏走（别每样都 1 秒一次）。</summary>
    private int _tickCount;

    private async Task TickAsync()
    {
        _tickCount++;

        // 模式 / 网速 / 连接数：2 秒一次足够，没必要跟着 1 秒跑
        if (_tickCount % 2 == 0)
        {
            await RefreshAsync("定时刷新").ConfigureAwait(false);
        }

        // 网站延迟：按设置走（最快可以到 1 秒）
        await MaybeRefreshSiteDelaysAsync().ConfigureAwait(false);

        // 出口归属地：每 30 秒探一次（内部还会按 ipgeosec 再节流，默认 300 秒才真发请求）
        if (_tickCount % 30 == 0)
        {
            await MaybeRefreshExitCountryAsync().ConfigureAwait(false);
        }
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        Log.Info("已停用");

        // 流量统计落盘，下次启动接着算
        _traffic.Save(PluginDirectory);

        // 退订主题事件，否则宿主会一直持有视图 → 插件程序集无法回收
        _view.Detach();
        _spotlight?.Detach();
        _spotlight = null;

        SetContent(null);
        _api.Dispose();
        return Task.CompletedTask;
    }

    // ---- 取数 ----

    /// <summary>
    /// 连接参数。用户在设置页填了就用他的；**没填的部分自动从 Clash Verge 的配置里读**。
    ///
    /// 这样新用户只要在 Clash Verge 里打开「外部控制」，插件这边一个字都不用填就能连上 ——
    /// 否则要用户把 Core Secret 手抄过来，几乎没人愿意配。
    /// </summary>
    private (string Host, int Port, string Secret) ReadConfig()
    {
        var host = Settings.Get("host", "");
        var port = Settings.Get("port", 0);
        var secret = Settings.Get("secret", "");

        if (Settings.Get("autodetect", true))
        {
            var detected = ClashVergeConfig.TryRead();
            if (detected is not null)
            {
                if (string.IsNullOrWhiteSpace(host)) host = detected.Host;
                if (port <= 0) port = detected.Port;
                if (string.IsNullOrWhiteSpace(secret)) secret = detected.Secret;
            }
        }

        if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";
        if (port is <= 0 or > 65535) port = DefaultPort;
        return (host, port, secret);
    }

    /// <summary>手动刷新入口（设置页按钮、聚光卡按钮都用它）。</summary>
    public Task RefreshNowAsync(string reason) => RefreshAsync(reason);

    private async Task RefreshAsync(string reason)
    {
        if (_stopped) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) return;

            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);

            var snapshot = await FetchAsync(reason).ConfigureAwait(false);
            _snapshot = snapshot;

            Context.RunOnUI(() =>
            {
                try
                {
                    _view.Apply(snapshot);
                    _spotlight?.Apply(snapshot);
                }
                catch (Exception ex)
                {
                    // 视图出错不能把宿主的未处理异常计数顶上去
                    Log.Warn($"界面刷新失败（已忽略）：{ex.Message}");
                }
            });

            // 到点了就整组测一次速（设置里可以关掉）
            await MaybeAutoTestAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"刷新异常（{reason}，已忽略）", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ClashSnapshot> FetchAsync(string reason)
    {
        var snapshot = new ClashSnapshot();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = cts.Token;

        try
        {
            snapshot.Version = await _api.GetVersionAsync(ct).ConfigureAwait(false);

            var config = await _api.GetConfigAsync(ct).ConfigureAwait(false);
            snapshot.Mode = config.Mode;
            snapshot.TunEnabled = config.TunEnabled;
            snapshot.MixedPort = config.MixedPort;

            var traffic = await _api.GetTrafficAsync(ct).ConfigureAwait(false);
            snapshot.Connections = traffic.Connections;
            snapshot.UpTotal = traffic.Up;
            snapshot.DownTotal = traffic.Down;

            // 今日 / 本月：把这次采样并进统计，并铺到快照上给卡片显示
            _traffic.Add(traffic.Up, traffic.Down, DateTimeOffset.Now);
            snapshot.TodayUp = _traffic.TodayUp;
            snapshot.TodayDown = _traffic.TodayDown;
            snapshot.MonthUp = _traffic.MonthUp;
            snapshot.MonthDown = _traffic.MonthDown;

            // 每 30 次刷新（约 1 分钟）落一次盘，别每 2 秒写文件
            if (++_trafficSaveTick >= 30)
            {
                _trafficSaveTick = 0;
                _traffic.Save(PluginDirectory);
            }

            // 「谁在用流量」只有卡片打开时才看得见，没必要一直拉
            if (_spotlight is not null)
            {
                try
                {
                    using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connCts.CancelAfter(TimeSpan.FromSeconds(4));
                    var connections = await _api.GetConnectionsAsync(connCts.Token).ConfigureAwait(false);
                    // 只取前 4 条：12 条会把卡片撑爆（12×30 = 360px，而整卡只有 670）
                    snapshot.TopConnections = connections.Take(4).ToList();
                }
                catch (Exception)
                {
                    // 拉不到就不显示这一块，不能影响其它数据
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (_lastUp >= 0 && _lastTrafficAt != default)
            {
                var seconds = (now - _lastTrafficAt).TotalSeconds;
                if (seconds > 0.2)
                {
                    var upRaw = Math.Max(0, (traffic.Up - _lastUp) / seconds);
                    var downRaw = Math.Max(0, (traffic.Down - _lastDown) / seconds);

                    // 指数平滑：原始值是每 2 秒一次的瞬时差值，直接显示会疯狂跳动，
                    // 数字根本看不清。平滑一半一半，既跟得上又读得清。
                    _upSmooth = _upSmooth < 0 ? upRaw : _upSmooth * 0.5 + upRaw * 0.5;
                    _downSmooth = _downSmooth < 0 ? downRaw : _downSmooth * 0.5 + downRaw * 0.5;

                    snapshot.UpPerSec = _upSmooth;
                    snapshot.DownPerSec = _downSmooth;

                    _upHistory.Enqueue(_upSmooth);
                    _downHistory.Enqueue(_downSmooth);
                    while (_upHistory.Count > HistoryLength) _upHistory.Dequeue();
                    while (_downHistory.Count > HistoryLength) _downHistory.Dequeue();
                }
            }
            _lastUp = traffic.Up;
            _lastDown = traffic.Down;
            _lastTrafficAt = now;

            snapshot.UpHistory = _upHistory.ToArray();
            snapshot.DownHistory = _downHistory.ToArray();

            // 策略组每 3 次刷新拉一次，省掉大 JSON 的开销
            _tick++;
            if (_tick % 3 == 1 || _groups.Count == 0)
            {
                _groups = await _api.GetGroupsAsync(SelectedTargetUrl, ct).ConfigureAwait(false);
            }
            snapshot.Groups = _groups;

            var active = PickGroup(_groups, snapshot.Mode);
            snapshot.ActiveGroup = active?.Name ?? "";
            snapshot.ActiveNode = active?.Now ?? "";
            snapshot.ActiveDelay = active?.Nodes.FirstOrDefault(n => n.Name == snapshot.ActiveNode)?.Delay ?? -1;

            // 当前节点对每个测速网站的延迟。只在重拉策略组那一轮取，别每 2 秒多发一次请求。
            if (_tick % 3 == 1 && !string.IsNullOrEmpty(snapshot.ActiveNode))
            {
                _siteDelays = await _api
                    .GetNodeSiteDelaysAsync(snapshot.ActiveNode, _targets.Select(t => t.Url).ToList(), ct)
                    .ConfigureAwait(false);
            }

            // 摊平成「网站名 + 延迟」，视图直接照着画（靶子顺序跟 Clash Verge 里一致）
            snapshot.Sites = _targets
                .Select(t => new ClashSiteLatency(t.Name, t.Url, _siteDelays.TryGetValue(t.Url, out var d) ? d : -1))
                .ToList();

            snapshot.DelayTrend = _delayTrend.ToArray();

            snapshot.Connected = true;

            if (reason != "定时刷新")
            {
                Log.Info($"取数成功（{reason}）：节点 {snapshot.ActiveNode}，延迟 {snapshot.ActiveDelay}ms，" +
                         $"模式 {snapshot.Mode}，分组 {snapshot.ActiveGroup}");
            }
        }
        catch (Exception ex)
        {
            snapshot.Connected = false;
            snapshot.Error = Describe(ex);
            snapshot.Groups = _groups;      // 保留上一次的节点列表，别把界面清空

            if (reason != "定时刷新")
            {
                Log.Warn($"取数失败（{reason}）：{snapshot.Error}");
            }
        }

        return snapshot;
    }

    /// <summary>挑一个「主角」策略组：优先用户指定的，其次看模式，最后退化成第一个 Selector。</summary>
    private ClashGroup? PickGroup(List<ClashGroup> groups, string mode)
    {
        if (groups.Count == 0) return null;

        var preferred = Settings.Get("group", "");
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = groups.FirstOrDefault(g => g.Name == preferred);
            if (match is not null) return match;
        }

        // 全局模式下真正决定走哪个节点的是 GLOBAL 组，其它组只是备选
        if (string.Equals(mode, "global", StringComparison.OrdinalIgnoreCase))
        {
            var global = groups.FirstOrDefault(g => g.Name == "GLOBAL");
            if (global is not null) return global;
        }

        return groups.FirstOrDefault(g => g.Type == "Selector" && g.Name != "GLOBAL")
               ?? groups.FirstOrDefault(g => g.Name != "GLOBAL")
               ?? groups.FirstOrDefault();
    }

    /// <summary>
    /// 把当前节点对每个测速网站都测一遍（卡片里"每个网站各自的延迟"靠它刷新）。
    ///
    /// 三条路都会调它：定时自动重测、打开卡片、手动点「全部测试」。
    /// 入口必须自己防重入 —— 否则会同时跑好几轮，日志里同一秒出现好几次同样的测速，
    /// 既白费流量，数值也因为互相抢带宽而失真。
    /// </summary>
    public async Task TestAllSitesAsync()
    {
        if (Interlocked.Exchange(ref _siteTestRunning, 1) == 1) return;

        try
        {
            var node = _snapshot.ActiveNode;
            if (string.IsNullOrEmpty(node) || _targets.Count == 0) return;

            _lastSiteTest = DateTimeOffset.UtcNow;
            Log.Info($"开始全网站测速：{node}");
            Context.RunOnUI(() => Context.Island.ShowMessage(new IslandMessage
            {
                Title = Manifest.Name,
                Text = $"正在测 {_targets.Count} 个网站…",
                Glyph = Manifest.IconGlyph ?? "\uE774",
                Duration = TimeSpan.FromSeconds(2),
            }));

            foreach (var target in _targets)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var delay = await _api.TestNodeAsync(node, target.Url, cts.Token).ConfigureAwait(false);
                    Log.Info($"  {target.Name} → {delay} ms");
                }
                catch (Exception ex)
                {
                    Log.Warn($"  {target.Name} 测速失败（已忽略）：{ex.Message}");
                }
            }

            _tick = 0;
            await RefreshAsync($"全网站测速 → {node}").ConfigureAwait(false);

            // 记一个延迟采样点（用当前选中网站测出来的那个值，没有就退回节点延迟）
            var sample = _siteDelays.TryGetValue(SelectedTargetUrl, out var measured) && measured >= 0
                ? measured
                : _snapshot.ActiveDelay;

            if (sample >= 0)
            {
                _delayTrend.Enqueue(sample);
                while (_delayTrend.Count > DelayTrendLength) _delayTrend.Dequeue();
            }
        }
        catch (Exception ex)
        {
            Log.Error("全网站测速失败", ex);
        }
        finally
        {
            // 无论成功失败都要放开：漏了这行，之后所有测速都会被永久挡在门外
            Interlocked.Exchange(ref _siteTestRunning, 0);
        }
    }

    /// <summary>当前该用哪个测速地址：优先用户选的网站，没配就退回策略组自带的。</summary>
    private string TestUrlFor(string group)
    {
        var url = SelectedTargetUrl;
        if (!string.IsNullOrWhiteSpace(url)) return url;
        return _groups.FirstOrDefault(g => g.Name == group)?.TestUrl ?? "";
    }

    /// <summary>按设置里的间隔自动整组测速（0 = 关闭）。</summary>
    private async Task MaybeAutoTestAsync()
    {
        var minutes = Settings.Get("autotest", 0);
        if (minutes <= 0 || !_snapshot.Connected) return;

        if (_lastAutoTest != DateTimeOffset.MinValue &&
            DateTimeOffset.UtcNow - _lastAutoTest < TimeSpan.FromMinutes(minutes))
        {
            return;
        }

        var group = PickGroup(_groups, _snapshot.Mode);
        if (group is null) return;

        _lastAutoTest = DateTimeOffset.UtcNow;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var ok = await _api.TestGroupAsync(group.Name, TestUrlFor(group.Name), cts.Token).ConfigureAwait(false);
            Log.Info($"自动测速完成：{group.Name}（网站 {SelectedTargetName}），{ok} 个节点有结果");
            _tick = 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"自动测速失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>把异常翻译成用户看得懂的一句话。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        ClashApiException { StatusCode: 401 } => "Core Secret 不对（密码错误）",
        ClashApiException { StatusCode: 404 } => "接口路径不存在（版本不匹配？）",
        ClashApiException api => $"接口返回 HTTP {api.StatusCode}",
        HttpRequestException => "连不上 Clash Verge —— 多半是「外部控制」没打开",
        OperationCanceledException => "请求超时",
        _ => ex.Message,
    };

    // ---- 聚光卡回调 ----

    private void OpenSpotlight()
    {
        _spotlight ??= new ClashIslandSpotlightView(Manifest, Theme, this, _flags);

        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlight,
            Size = new Windows.Foundation.Size(960, 670),
            OnClosed = () => _spotlight?.OnHostClosed(),
        });

        // 打开瞬间先铺一次已有数据，别让卡片空着
        _spotlight.Apply(_snapshot);

        // 数据太旧就自动测一遍（5 分钟内测过就跳过）
        EnsureFreshSiteDelays();
    }

    public void SetSpotlightGroup(string group)
    {
        group ??= "";

        // 值没变就别写设置：Settings.Set 会触发通知，白写一次就白刷一次（sdk-api §15.1）
        if (string.Equals(group, SpotlightGroup, StringComparison.Ordinal)) return;

        SpotlightGroup = group;
        if (!string.IsNullOrWhiteSpace(group))
        {
            Settings.Set("group", SpotlightGroup);   // 记住用户的选择，下次直接用它当主角
        }
    }

    /// <summary>只测一个节点（卡片里每行的「测」按钮）。</summary>
    public async Task<int> TestSingleNodeAsync(string node)
    {
        if (string.IsNullOrWhiteSpace(node)) return -1;

        try
        {
            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var delay = await _api.TestNodeAsync(node, TestUrlFor(_snapshot.ActiveGroup), cts.Token)
                .ConfigureAwait(false);

            Log.Info($"单节点测速：{node} → {delay} ms");
            await RefreshAsync("单节点测速").ConfigureAwait(false);
            return delay;
        }
        catch (Exception ex)
        {
            Log.Warn($"单节点测速失败：{ex.Message}");
            return -1;
        }
    }

    /// <summary>在岛上直接切到「上一个 / 下一个」节点，不用点开卡片。</summary>
    public async Task<bool> SwitchNodeRelativeAsync(int delta)
    {
        var group = _snapshot.Groups.FirstOrDefault(
            g => string.Equals(g.Name, _snapshot.ActiveGroup, StringComparison.Ordinal));

        if (group is null || group.Nodes.Count == 0) return false;

        var nodes = HideInfoEntries
            ? group.Nodes.Where(n => !ClashFormat.IsInfoEntry(n.Name)).ToList()
            : group.Nodes.ToList();

        if (nodes.Count == 0) return false;

        var index = nodes.FindIndex(n => string.Equals(n.Name, group.Now, StringComparison.Ordinal));
        if (index < 0) index = 0;

        var next = ((index + delta) % nodes.Count + nodes.Count) % nodes.Count;
        await SelectNodeAsync(group.Name, nodes[next].Name).ConfigureAwait(false);
        return true;
    }

    /// <summary>切到本组延迟最低的节点。</summary>
    ///
    /// 这是相对 Clash Verge 原界面**最有价值**的一个操作：
    /// 那边要「点开策略组 → 点测速 → 等一圈 → 找到最快的 → 点它」，
    /// 这里一步到位。信息条目（剩余流量 / 套餐到期…）不参与评选。
    /// </summary>
    public async Task<string?> SwitchToFastestAsync()
    {
        var group = _snapshot.ActiveGroup;
        if (string.IsNullOrWhiteSpace(group))
        {
            Log.Warn("一键切最快：当前没有选中的分组");
            return null;
        }

        try
        {
            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var delays = await _api.TestGroupDelaysAsync(group, TestUrlFor(group), cts.Token)
                .ConfigureAwait(false);

            var best = delays
                .Where(kv => kv.Value > 0 && !ClashFormat.IsInfoEntry(kv.Key))
                .OrderBy(kv => kv.Value)
                .Select(kv => (kv.Key, kv.Value))
                .FirstOrDefault();

            if (string.IsNullOrEmpty(best.Key))
            {
                Log.Warn("一键切最快：本组没测出可用节点");
                return null;
            }

            Log.Info($"一键切最快：{group} → {best.Key}（{best.Value} ms）");
            await SelectNodeAsync(group, best.Key).ConfigureAwait(false);
            return best.Key;
        }
        catch (Exception ex)
        {
            Log.Warn($"一键切最快失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>断开一条连接（「谁在用流量」里那一行的 × 按钮）。</summary>
    public async Task CloseConnectionAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        try
        {
            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);
            await _api.CloseConnectionAsync(id, CancellationToken.None).ConfigureAwait(false);
            Log.Info("已断开一条连接");
            await RefreshAsync("断开连接").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"断开连接失败：{ex.Message}");
        }
    }

    /// <summary>断开全部连接。</summary>
    public async Task CloseAllConnectionsAsync()
    {
        try
        {
            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);
            await _api.CloseAllConnectionsAsync(CancellationToken.None).ConfigureAwait(false);
            Log.Info("已断开全部连接");
            await RefreshAsync("断开全部连接").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"断开全部连接失败：{ex.Message}");
        }
    }

    /// <summary>开关 TUN 模式（游戏 / 需要全局接管时用得上）。</summary>
    public async Task SetTunAsync(bool enabled)
    {
        try
        {
            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);
            await _api.SetTunAsync(enabled, CancellationToken.None).ConfigureAwait(false);
            Log.Info($"TUN 模式 → {(enabled ? "开" : "关")}");
            await RefreshAsync("切换 TUN").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"切换 TUN 失败：{ex.Message}");
        }
    }

    /// <summary>2.0 起强调色不可自定义 —— 返回 null，各元素一律用各自的默认配色。</summary>
    public Windows.UI.Color? AccentColor => null;

    /// <summary>切换某个分组里选中的节点。</summary>
    public async Task SelectNodeAsync(string group, string node)    {
        try
        {
            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);

            await _api.SelectNodeAsync(group, node, CancellationToken.None).ConfigureAwait(false);
            Log.Info($"切换节点：{group} → {node}");

            // 顺手测一下新节点的延迟，否则界面上还挂着上一个节点的数字，看着像没切成功
            try
            {
                using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                var delay = await _api.TestNodeAsync(node, TestUrlFor(group), testCts.Token).ConfigureAwait(false);
                Log.Info($"新节点测速：{node} → {delay} ms");
            }
            catch (Exception ex)
            {
                Log.Warn($"新节点测速失败（已忽略）：{ex.Message}");
            }

            _tick = 0;                                  // 强制下一次刷新重拉策略组
            await RefreshAsync($"切换节点 → {node}").ConfigureAwait(false);

            Context.RunOnUI(() => Context.Island.ShowMessage(new IslandMessage
            {
                Title = Manifest.Name,
                Text = $"已切到 {node}",
                Glyph = Manifest.IconGlyph ?? "\uE774",
                Duration = TimeSpan.FromSeconds(2),
            }));
        }
        catch (Exception ex)
        {
            var message = Describe(ex);
            Log.Error($"切换节点失败：{group} → {node}（{message}）", ex);
            Context.RunOnUI(() => Context.Island.ShowMessage(new IslandMessage
            {
                Title = Manifest.Name,
                Text = $"切换失败：{message}",
                Glyph = Manifest.IconGlyph ?? "\uE774",
                Duration = TimeSpan.FromSeconds(3),
            }));
        }
    }

    /// <summary>切换全局模式（规则 / 全局 / 直连）。</summary>
    public async Task SetModeAsync(string mode)
    {
        try
        {
            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);

            await _api.SetModeAsync(mode, CancellationToken.None).ConfigureAwait(false);
            Log.Info($"切换模式 → {mode}");

            await RefreshAsync($"切换模式 → {mode}").ConfigureAwait(false);

            Context.RunOnUI(() => Context.Island.ShowMessage(new IslandMessage
            {
                Title = Manifest.Name,
                Text = $"模式：{ClashFormat.Mode(mode)}",
                Glyph = Manifest.IconGlyph ?? "\uE774",
                Duration = TimeSpan.FromSeconds(2),
            }));
        }
        catch (Exception ex)
        {
            Log.Error($"切换模式失败：{mode}", ex);
        }
    }

    /// <summary>给某个组的全部节点测延迟。</summary>
    public async Task TestGroupAsync(string group)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(group)) return;

            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);

            Log.Info($"开始测速：{group}");
            Context.RunOnUI(() => Context.Island.ShowMessage(new IslandMessage
            {
                Title = Manifest.Name,
                Text = $"正在测速：{group}…",
                Glyph = Manifest.IconGlyph ?? "\uE774",
                Duration = TimeSpan.FromSeconds(2),
            }));

            // 用当前选中的测速网站 —— 和 Clash Verge 界面里「选网站测延迟」是同一回事
            await _api.TestGroupAsync(group, TestUrlFor(group), CancellationToken.None).ConfigureAwait(false);

            _tick = 0;
            await RefreshAsync($"测速完成 → {group}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"测速失败：{group}", ex);
        }
    }

    // ---- 设置页 ----

    /// <summary>设置页工厂：每次点进页面都会调用一次，必须返回新实例。</summary>
    private UIElement BuildSettingsPage()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = Manifest.Name,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });

        panel.Children.Add(new TextBlock
        {
            Text = "数据来自 Clash Verge 的本地接口。请先在 Clash Verge 里打开：\n" +
                   "设置 → 「Clash 设置」→ External → 打开「Enable External Controller」，" +
                   "地址填 127.0.0.1:9097，并设置一个 Core Secret。\n" +
                   "下面的三项**留空即自动从 Clash Verge 读取**（推荐），只有自动读不到时才需要手填。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
        });

        var enabled = new ToggleSwitch
        {
            Header = "在灵动岛上显示",
            IsOn = Settings.Get("enabled", true),
        };
        enabled.Toggled += (_, _) => Settings.Set("enabled", enabled.IsOn);
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = enabled,
        });

        // 一个中国原则：台湾节点显示中华人民共和国国旗
        var twChina = new CheckBox
        {
            Content = "台湾节点显示中华人民共和国国旗（一个中国原则）",
            IsChecked = Settings.Get("twchina", true),
        };
        twChina.Checked += (_, _) => Settings.Set("twchina", true);
        twChina.Unchecked += (_, _) => Settings.Set("twchina", false);
        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = twChina,
        });

        // 灵动岛优先级：多个插件 / 内置模块抢岛时，数值大的占岛，其余进展开队列。
        // 必须能在界面上调 —— 冲突是必然会发生的（内置音乐模块放歌时会把自己抬到 200）。
        var priorityOptions = new (string Label, int Value)[]
        {
            ("最低 30（几乎总让位）", 30),
            ("低 50", 50),
            ("中 100", 100),
            ("高 150（推荐：放歌时让给音乐）", 150),
            ("最高 210（始终占岛，压过音乐）", 210),
        };

        var priorityBox = new ComboBox { Header = "灵动岛优先级", MinWidth = 300 };
        foreach (var option in priorityOptions) priorityBox.Items.Add(option.Label);

        var currentPriority = Settings.Get("priority", DefaultPriority);
        var priorityIndex = Array.FindIndex(priorityOptions, o => o.Value == currentPriority);
        priorityBox.SelectedIndex = priorityIndex >= 0 ? priorityIndex : 3;
        priorityBox.SelectionChanged += (_, _) =>
        {
            var i = priorityBox.SelectedIndex;
            if (i < 0 || i >= priorityOptions.Length) return;

            var value = priorityOptions[i].Value;
            if (Settings.Get("priority", DefaultPriority) != value) Settings.Set("priority", value);
        };

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    priorityBox,
                    new TextBlock
                    {
                        Text = "岛体同一时刻只显示优先级最高的那个内容，其它的在「悬停展开」后的队列里。\n" +
                               "内置音乐模块在放歌时会把优先级抬到 200：选 150 就是「放歌时让给音乐，不放歌时显示本插件」。",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7,
                        FontSize = 12,
                    },
                },
            },
        });


        // ── IP 归属地 ────────────────────────────────────────────
        // 节点名经常骗人（「自动选择」「故障转移」「香港01」），国旗按**真实出口 IP** 显示。
        // 刷新间隔的选项排布和上面「网站延迟」用同一个模板。
        var ipOptions = new (string Label, int Seconds)[]
        {
            ("1 分钟", 60),
            ("5 分钟（推荐，同 Clash Verge）", 300),
            ("15 分钟", 900),
            ("30 分钟", 1800),
            ("1 小时", 3600),
        };

        var ipBox = new ComboBox { Header = "IP 归属地刷新间隔", MinWidth = 300 };
        foreach (var option in ipOptions) ipBox.Items.Add(option.Label);

        var currentIpSeconds = Math.Clamp(Settings.Get("ipgeosec", 300), 60, 3600);
        var ipIndex = Array.FindIndex(ipOptions, o => o.Seconds == currentIpSeconds);
        ipBox.SelectedIndex = ipIndex >= 0 ? ipIndex : 1;
        ipBox.SelectionChanged += (_, _) =>
        {
            var i = ipBox.SelectedIndex;
            if (i < 0 || i >= ipOptions.Length) return;

            var seconds = ipOptions[i].Seconds;
            if (Settings.Get("ipgeosec", 300) != seconds) Settings.Set("ipgeosec", seconds);
        };

        var refreshIp = new Button
        {
            Content = "立即刷新 IP 归属地",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        refreshIp.Click += async (_, _) =>
        {
            refreshIp.IsEnabled = false;
            try
            {
                await RefreshExitNowAsync();
                refreshIp.Content = string.IsNullOrEmpty(ExitCountry)
                    ? "刷新失败（检查代理是否可用）"
                    : $"已识别：{ExitCountry}";
            }
            finally
            {
                refreshIp.IsEnabled = true;
            }
        };

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    ipBox,
                    refreshIp,
                    new TextBlock
                    {
                        Text = "国旗按**真实出口 IP** 的归属地显示，不再依赖节点名 —— " +
                               "「自动选择」「故障转移」这类名字看不出国家，「香港01」也可能实际落在日本。\n" +
                               "查询会经过代理向第三方归属地服务发一个请求（和 Clash Verge 的「IP 信息」同一做法）；" +
                               "查不到时会退回按节点名判断。",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75,
                        FontSize = 12,
                    },
                },
            },
        });

        // 自动检测到的值只作为占位提示；输入框留空就走自动检测
        var detected = ClashVergeConfig.TryRead();
        var detectedHint = detected is null ? "（未检测到，请先在 Clash Verge 打开外部控制）" : "（留空则自动使用）";

        var hostBox = new TextBox
        {
            Header = "地址" + detectedHint,
            Text = Settings.Get("host", ""),
            PlaceholderText = detected?.Host ?? "127.0.0.1",
            MinWidth = 260,
        };

        var portBox = new TextBox
        {
            Header = "端口" + detectedHint,
            Text = Settings.Get("port", 0) > 0 ? Settings.Get("port", 0).ToString() : "",
            PlaceholderText = (detected?.Port ?? DefaultPort).ToString(),
            MinWidth = 140,
        };

        var secretBox = new PasswordBox
        {
            Header = "Core Secret" + detectedHint,
            Password = Settings.Get("secret", ""),
            PlaceholderText = detected is null ? "在 Clash Verge 里设置的那个密码" : "已自动读取",
            MinWidth = 260,
        };

        // 测速网站：和 Clash Verge 界面上那套一致
        var targetBox = new ComboBox { Header = "测速网站", MinWidth = 180 };
        foreach (var target in _targets) targetBox.Items.Add(target.Name);
        var targetIndex = _targets.FindIndex(t => t.Name == SelectedTargetName);
        targetBox.SelectedIndex = targetIndex >= 0 ? targetIndex : 0;
        targetBox.SelectionChanged += (_, _) =>
        {
            if (targetBox.SelectedItem is string name) SetTarget(name);
        };

        // 自动测速间隔：0 = 关闭
        var autoTestBox = new ComboBox { Header = "自动测速", MinWidth = 180 };
        autoTestBox.Items.Add("关闭");
        autoTestBox.Items.Add("每 5 分钟");
        autoTestBox.Items.Add("每 15 分钟");
        autoTestBox.Items.Add("每 30 分钟");
        autoTestBox.SelectedIndex = Settings.Get("autotest", 0) switch { 5 => 1, 15 => 2, 30 => 3, _ => 0 };
        autoTestBox.SelectionChanged += (_, _) =>
        {
            var minutes = autoTestBox.SelectedIndex switch { 1 => 5, 2 => 15, 3 => 30, _ => 0 };
            if (Settings.Get("autotest", 0) != minutes) Settings.Set("autotest", minutes);
        };

        // 网站延迟自动刷新：这就是"那四个数字多久更新一次"。
        // 用 (标签, 秒数) 数组而不是 switch —— 加一档只改一处，不会再对错索引。
        var siteOptions = new (string Label, int Seconds)[]
        {
            ("关闭", 0),
            ("每 1 秒（不现实：一轮就要 2 秒，很费流量）", 1),
            ("每 5 秒（费流量，一天约 700 MB）", 5),
            ("每 10 秒（一天约 350 MB）", 10),
            ("每 25 秒（推荐，一天约 2.8 MB）", 25),
            ("每 1 分钟", 60),
            ("每 2 分钟", 120),
            ("每 5 分钟", 300),
        };

        var siteRefreshBox = new ComboBox { Header = "网站延迟刷新", MinWidth = 260 };
        foreach (var option in siteOptions) siteRefreshBox.Items.Add(option.Label);

        var currentRefresh = Settings.Get("siterefresh", 25);
        var refreshIndex = Array.FindIndex(siteOptions, o => o.Seconds == currentRefresh);
        siteRefreshBox.SelectedIndex = refreshIndex >= 0 ? refreshIndex : 4;

        siteRefreshBox.SelectionChanged += (_, _) =>
        {
            var i = siteRefreshBox.SelectedIndex;
            if (i < 0 || i >= siteOptions.Length) return;

            var seconds = siteOptions[i].Seconds;
            if (Settings.Get("siterefresh", 25) != seconds) Settings.Set("siterefresh", seconds);
        };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };

        var _loading = true;   // 页面填充期间不要回写设置（sdk-api §15.1）

        void Commit()
        {
            if (_loading) return;

            var host = (hostBox.Text ?? "").Trim();
            if (!string.Equals(host, Settings.Get("host", ""), StringComparison.Ordinal))
            {
                // 留空就存空串 —— 表示"走自动检测"，不要硬填 127.0.0.1 把自动检测顶掉
                Settings.Set("host", host);
            }

            var portText = (portBox.Text ?? "").Trim();
            if (portText.Length == 0)
            {
                if (Settings.Get("port", 0) != 0) Settings.Set("port", 0);
            }
            else if (int.TryParse(portText, out var port) && port is > 0 and <= 65535)
            {
                if (port != Settings.Get("port", 0)) Settings.Set("port", port);
            }

            var secret = secretBox.Password ?? "";
            if (!string.Equals(secret, Settings.Get("secret", ""), StringComparison.Ordinal))
            {
                Settings.Set("secret", secret);
            }
        }

        // 三个提交点都要覆盖：失焦、回车、动作按钮之前
        hostBox.LostFocus += (_, _) => Commit();
        hostBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };
        portBox.LostFocus += (_, _) => Commit();
        portBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };
        secretBox.LostFocus += (_, _) => Commit();
        secretBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };

        var testButton = new Button { Content = "保存并测试连接" };
        testButton.Click += async (_, _) =>
        {
            Commit();                      // 先提交，否则测的是旧值（按钮点击早于 LostFocus）
            status.Text = "正在连接…";

            var (host, port, secret) = ReadConfig();
            _api.Configure(host, port, secret);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var version = await _api.GetVersionAsync(cts.Token).ConfigureAwait(true);
                status.Text = $"✅ 连接成功：Mihomo {version}（{_api.BaseUrl}）";
                await RefreshAsync("设置页测试连接").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                status.Text = $"❌ 连接失败：{Describe(ex)}\n" +
                              "检查：Clash Verge 是否在运行、「外部控制」是否打开、端口和 Core Secret 是否一致。";
            }
        };

        var refreshButton = new Button { Content = "立即刷新" };
        refreshButton.Click += async (_, _) =>
        {
            Commit();
            status.Text = "已刷新";
            await RefreshAsync("设置页手动刷新").ConfigureAwait(true);
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(testButton);
        actions.Children.Add(refreshButton);

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel { Spacing = 8, Children = { hostBox, portBox, secretBox, targetBox, siteRefreshBox, autoTestBox, actions, status } },
        });

        var messageButton = new Button { Content = "发一条测试消息" };
        messageButton.Click += (_, _) => Context.Island.ShowMessage(new IslandMessage
        {
            Title = Manifest.Name,
            Text = "这是来自插件设置页的测试消息",
            Glyph = Manifest.IconGlyph ?? "\uE774",
            Duration = TimeSpan.FromSeconds(3),
        });
        panel.Children.Add(messageButton);

        _loading = false;
        return panel;
    }
}