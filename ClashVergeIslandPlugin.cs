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

    /// <summary>趋势曲线保留的采样点数（每次刷新一个点，约 2 秒一个）。</summary>
    private const int HistoryLength = 60;

    private readonly ClashVergeApi _api = new();
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

    /// <summary>上一次自动测速的时间（"自动测速"关掉时用不到）。</summary>
    private DateTimeOffset _lastAutoTest = DateTimeOffset.MinValue;

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

    protected override Task OnInitializeAsync()
    {
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}，插件目录 {PluginDirectory}");

        _flags = new FlagLibrary(PluginDirectory);
        _targets = ClashTestTargets.Load();
        Log.Info($"测速网站（{_targets.Count} 个）：{string.Join("、", _targets.Select(t => t.Name))}，当前用 {SelectedTargetName}");
        _view = new ClashIslandView(Manifest, Theme, _flags);

        _content = new IslandLiveContent
        {
            Priority = Settings.Get("priority", 30),
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF),
            MorphView = _view,
            CompactSize = new Windows.Foundation.Size(180, 40),
            ExpandedSize = new Windows.Foundation.Size(420, 140),
            OnTap = OpenSpotlight,          // 点击岛体 = 打开聚光卡
        };

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE774", BuildSettingsPage, order: 80));

        // 显示开关实时生效
        Context.Register(Context.OnSettingsChanged("enabled", () =>
        {
            SetContent(Settings.Get("enabled", true) ? _content : null);
        }));

        // 连接参数一改就立刻重新取数
        foreach (var key in new[] { "host", "port", "secret" })
        {
            Context.Register(Context.OnSettingsChanged(key, () => _ = RefreshAsync($"设置变更（{key}）")));
        }

        if (Settings.Get("enabled", true))
        {
            SetContent(_content);
        }

        _ = RefreshAsync("首次加载");

        // UI 线程定时器；插件停用时宿主自动停掉
        Context.CreateTimer(TimeSpan.FromSeconds(2), repeat: true, () => _ = RefreshAsync("定时刷新"));
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        Log.Info("已停用");

        // 退订主题事件，否则宿主会一直持有视图 → 插件程序集无法回收
        _view.Detach();
        _spotlight?.Detach();
        _spotlight = null;

        SetContent(null);
        _api.Dispose();
        return Task.CompletedTask;
    }

    // ---- 取数 ----

    private (string Host, int Port, string Secret) ReadConfig() => (
        Settings.Get("host", "127.0.0.1"),
        Settings.Get("port", DefaultPort),
        Settings.Get("secret", ""));

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
            Size = new Windows.Foundation.Size(780, 620),
            OnClosed = () => _spotlight?.OnHostClosed(),
        });

        // 打开瞬间先铺一次已有数据，别让卡片空着
        _spotlight.Apply(_snapshot);
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

    /// <summary>切换某个分组里选中的节点。</summary>
    public async Task SelectNodeAsync(string group, string node)
    {
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
                   "地址填 127.0.0.1:9097，并设置一个 Core Secret，然后把下面的端口和密码填成一样的。",
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

        var hostBox = new TextBox
        {
            Header = "地址",
            Text = Settings.Get("host", "127.0.0.1"),
            PlaceholderText = "127.0.0.1",
            MinWidth = 260,
        };

        var portBox = new TextBox
        {
            Header = "端口",
            Text = Settings.Get("port", DefaultPort).ToString(),
            PlaceholderText = DefaultPort.ToString(),
            MinWidth = 140,
        };

        var secretBox = new PasswordBox
        {
            Header = "Core Secret",
            Password = Settings.Get("secret", ""),
            PlaceholderText = "在 Clash Verge 里设置的那个密码",
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

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };

        var _loading = true;   // 页面填充期间不要回写设置（sdk-api §15.1）

        void Commit()
        {
            if (_loading) return;

            var host = (hostBox.Text ?? "").Trim();
            if (!string.Equals(host, Settings.Get("host", "127.0.0.1"), StringComparison.Ordinal))
            {
                Settings.Set("host", host.Length == 0 ? "127.0.0.1" : host);
            }

            if (int.TryParse((portBox.Text ?? "").Trim(), out var port) && port is > 0 and <= 65535)
            {
                if (port != Settings.Get("port", DefaultPort)) Settings.Set("port", port);
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
            Child = new StackPanel { Spacing = 8, Children = { hostBox, portBox, secretBox, targetBox, autoTestBox, actions, status } },
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
