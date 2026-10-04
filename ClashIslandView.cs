using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using WinIsland.Core;

namespace ClashVergeIsland;

/// <summary>
/// 岛上的视图：同一棵可视树同时承载「小岛（紧凑态）」和「大岛（展开态）」。
///
/// 紧凑态： [国旗] 节点名            45 ms
/// 展开态： 上面一行 + 模式 / 实时网速 + 分组 / 内核版本
///
/// 关于国旗：Windows 系统字体里没有国旗图案，打 🇯🇵 只会显示成「JP」两个字母，
/// 所以这里用插件自带的小国旗 PNG（assets/flags/&lt;代码&gt;.png），
/// 找不到图就退回「国家色 + 代码」徽章，认不出地区才用地球图标。
/// </summary>
public sealed class ClashIslandView : UserControl, IMorphView
{
    private const double CompactIconSize = 16;
    private const double ExpandedIconSize = 22;
    private const double CompactFlagWidth = 19;
    private const double CompactFlagHeight = 13;
    private const double ExpandedFlagWidth = 26;
    private const double ExpandedFlagHeight = 18;
    private const double CompactBadgeSize = 10;
    private const double ExpandedBadgeSize = 12;
    private const double CompactTitleSize = 13;
    private const double ExpandedTitleSize = 15;
    private const double ExpandedDetailHeight = 128;

    /// <summary>
    /// 标题和延迟都给一个宽度上限。
    ///
    /// 关键教训：宿主**不会**按插件声明或内容的期望宽度把岛体撑开 ——
    /// 小岛宽度是它自己定的（这台机器上约 180px），内容一旦超出就被边缘直接切掉
    /// （表现就是延迟只显示一半）。所以插件这边必须自己保证内容装得下：
    /// 名字用 MaxWidth 兜住、延迟用 MinWidth 定宽，绝不越界。
    /// </summary>
    private const double CompactTitleMaxWidth = 72;
    private const double ExpandedTitleMaxWidth = 226;

    /// <summary>
    /// 内容宽度自己钉死，不用宿主的宽度。
    ///
    /// 实测结论：宿主给出的内容框**比它画出来的胶囊更宽**（这台机器上胶囊约 169px）。
    /// 所以凡是"靠右对齐"的元素都会被推到胶囊外面直接切掉（延迟只剩一半就是这么来的）。
    /// 这里把根容器宽度固定住、左对齐，内容就永远在胶囊里面。
    /// </summary>
    private const double CompactContentWidth = 165;
    private const double ExpandedContentWidth = 400;
    private const double DelayMinWidth = 34;

    /// <summary>与宿主内置视图同一条 BackEase 曲线的幅度，手感保持一致。</summary>
    private const double BackAmplitude = 0.45;

    private readonly IIslandTheme _theme;
    private readonly FontIcon _icon;

    /// <summary>
    /// 真国旗（认得出地区、且插件里有对应 PNG 时显示）。
    ///
    /// 渲染方式很关键：用 Border + ImageBrush(UniformToFill)，而不是 Image + 白框。
    ///   · 各国国旗长宽比不一样（瑞士 1:1、尼泊尔 1:1.2、多数 3:2），
    ///     用 Uniform 放进固定框一定会留白边，看着就像糊了一层白框；
    ///     UniformToFill 让图填满整格、超出的部分裁掉，所有国旗形状统一。
    ///   · Border 会把背景裁到 CornerRadius，所以圆角也是白送的。
    /// </summary>
    private readonly Border _flagVisual = new()
    {
        CornerRadius = new CornerRadius(3),
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0,          // 认出地区且找得到国旗时才显示
    };

    private readonly ImageBrush _flagBrush = new() { Stretch = Stretch.UniformToFill };
    private readonly SolidColorBrush _flagEdgeBrush = new();

    /// <summary>
    /// 自绘的岛体药丸：盖住宿主画的那个黑色胶囊。
    /// 宿主不给材质接口（IslandLiveContent 只有 Priority / 视图 / 尺寸），
    /// 所以只能自己画一个圆角块压在它上面 —— 这样岛体才有了颜色和透明度。
    /// </summary>
    private readonly Border _pill;
    private readonly LinearGradientBrush _pillBrush;
    private readonly GradientStop _pillStopTop = new() { Offset = 0.0 };
    private readonly GradientStop _pillStopMid = new() { Offset = 0.55 };
    private readonly GradientStop _pillStopBottom = new() { Offset = 1.0 };
    private readonly SolidColorBrush _pillEdgeBrush = new();

    /// <summary>药丸比内容宽这么多：正好盖住宿主胶囊（实测胶囊比内容宽约 4px）。</summary>
    private const double PillBleed = 4;

    private const double PillRadiusCompact = 20;
    private const double PillRadiusExpanded = 24;

    /// <summary>国旗图片库（和聚光卡共用同一份缓存）。</summary>
    private readonly FlagLibrary _flags;

    /// <summary>退回用的地区徽章：带国家色的两个字母（缺国旗图时显示）。</summary>
    private readonly Border _badge;
    private readonly TextBlock _codeText;
    private readonly SolidColorBrush _badgeBrush = new();

    private readonly TextBlock _title;
    private readonly TextBlock _delay;
    private readonly Border _delayChip;
    private readonly SolidColorBrush _delayChipBrush = new();
    private readonly StackPanel _detail;

    /// <summary>展开态第一行：模式胶囊 + 彩色上下行速度。</summary>
    private readonly SolidColorBrush _modeChipBrush = new();

    /// <summary>未选中的模式芯片底色（很淡，选中那个才亮）。</summary>
    private readonly SolidColorBrush _chipIdleBrush = new();

    /// <summary>岛上那三个模式小芯片（规则 / 全局 / 直连）。</summary>
    private readonly List<(string Mode, Button Chip)> _modeChips;
    private readonly TextBlock _speedUp;
    private readonly TextBlock _speedDown;
    private readonly SolidColorBrush _upBrush = new();
    private readonly SolidColorBrush _downBrush = new();

    /// <summary>展开态第二行：最近 60 次采样的网速曲线。</summary>
    private readonly Canvas _sparkCanvas;
    private readonly Polyline _downLine;
    private readonly Polyline _upLine;
    private readonly SolidColorBrush _downLineBrush = new();
    private readonly SolidColorBrush _upLineBrush = new();

    /// <summary>展开态第三行：连接数 · 累计流量 · TUN 状态。</summary>
    private readonly TextBlock _sub;
    private readonly StackPanel _root;

    /// <summary>展开态第四行：当前节点对各个测速网站的延迟（Apple 87 · GitHub 127 …）。</summary>
    private readonly StackPanel _siteRow = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Dictionary<string, TextBlock> _siteTexts = new(StringComparer.Ordinal);
    private string _siteSignature = "\u0000";

    private readonly SolidColorBrush _siteGoodBrush = new(Windows.UI.Color.FromArgb(255, 0x3F, 0xB9, 0x50));
    private readonly SolidColorBrush _siteWarnBrush = new(Windows.UI.Color.FromArgb(255, 0xD2, 0x99, 0x22));
    private readonly SolidColorBrush _siteBadBrush = new(Windows.UI.Color.FromArgb(255, 0xF8, 0x51, 0x49));

    /// <summary>中性色一律用共享画刷：主题一变只改 Color，用到的地方当场跟着变。</summary>
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();

    /// <summary>外观（颜色 / 透明度 / 岛体自绘开关）都问插件要 —— 岛体和卡片共用一套。</summary>
    private readonly ClashVergeIslandPlugin _plugin;

    /// <summary>延迟那行自己的画刷（按绿/黄/红变色，不跟主题走）。</summary>
    private readonly SolidColorBrush _delayBrush = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;

    /// <summary>当前形态进度：0 = 紧凑态，1 = 展开态。反转动画时从这里接着走。</summary>
    private double _progress;

    /// <summary>节点名：小岛用精简版，展开用全名（展开态宽度够）。</summary>
    private string _shortName = "Clash 小岛";
    private string _fullName = "Clash 小岛";

    public ClashIslandView(PluginManifest manifest, IIslandTheme theme, FlagLibrary flags, ClashVergeIslandPlugin plugin)
    {
        _plugin = plugin;
        _theme = theme;
        _flags = flags;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _icon = new FontIcon
        {
            Glyph = manifest.IconGlyph ?? "\uE774",
            FontSize = CompactIconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF)),
        };

        // 真国旗：Border 背景填满 + 圆角裁切，不留白边、不加白框
        _flagVisual.Background = _flagBrush;
        _flagVisual.Width = CompactFlagWidth;
        _flagVisual.Height = CompactFlagHeight;

        // 缺国旗图时的退路：带国家色的小徽章
        _badgeBrush.Color = ClashFormat.RegionColor("");
        _codeText = new TextBlock
        {
            Text = "",
            FontSize = CompactBadgeSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badge = new Border
        {
            Background = _badgeBrush,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _codeText,
            Opacity = 0,
        };

        _title = new TextBlock
        {
            Text = "Clash 小岛",
            FontSize = CompactTitleSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _textBrush,
            Width = CompactTitleMaxWidth,     // 兜住宽度：溢出会被宿主直接切掉
        };

        _delay = new TextBlock
        {
            Text = "--",
            FontSize = 11,
            MinWidth = DelayMinWidth,         // 固定最小宽度：让 "97 ms" 和 "1234 ms" 一样宽
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _delayBrush,
        };

        // 延迟做成一个带底色的胶囊：比裸着一段绿字更像"设计过的"
        _delayChip = new Border
        {
            Background = _delayChipBrush,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Child = _delay,
        };

        // 国旗 / 徽章 / 地球图标叠在同一个格子里，靠 Opacity 切换 —— 布局宽度稳定，切换时不会跳
        var iconHost = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        };
        iconHost.Children.Add(_flagVisual);
        iconHost.Children.Add(_badge);
        iconHost.Children.Add(_icon);

        // 节点名那列必须是 Auto，不能是 Star：
        // 弹性列在测量阶段只上报最小宽度，宿主会以为内容很窄 → 岛体被定窄 → 文字被自己截断。
        // 想要「延迟靠右」，用一个空的 Star 列当弹簧即可（空列不贡献期望宽度）。
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 国旗 / 徽章 / 地球
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 节点名
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 弹簧
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 延迟
        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_delayChip, 3);
        header.Children.Add(iconHost);
        header.Children.Add(_title);
        header.Children.Add(_delayChip);

        // ---- 展开态的内容：三行，全部常驻可视树，靠 Height/Opacity 收起 ----

        // 第一行：模式（可点）+ 上下行速度
        //
        // 原来这里是一个只读的「全局」胶囊。现在把它换成三个可点的小芯片 ——
        // **位置一点没动**，只是让"模式指示器"变成"模式控制器"：
        // 左边还是它、右边还是上下行速度，前后关系保持原样，不会显得挤或者空。
        _modeChips = new List<(string Mode, Button Chip)>();
        foreach (var (mode, label) in new[] { ("rule", "规则"), ("global", "全局"), ("direct", "直连") })
        {
            var chip = new Button
            {
                Content = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                },
                Padding = new Thickness(7, 1, 7, 1),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = _chipIdleBrush,
            };

            var captured = mode;
            chip.Click += async (_, _) => await _plugin.SetModeAsync(captured);
            _modeChips.Add((mode, chip));
        }

        _upBrush.Color = Windows.UI.Color.FromArgb(255, 0xFF, 0xB4, 0x54);
        _downBrush.Color = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
        _speedUp = new TextBlock
        {
            Text = "↑ --",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _upBrush,
        };
        _speedDown = new TextBlock
        {
            Text = "↓ --",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = _downBrush,
        };

        var speedRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (_, chip) in _modeChips) speedRow.Children.Add(chip);
        speedRow.Children.Add(_speedUp);
        speedRow.Children.Add(_speedDown);

        // 第二行：网速曲线（最近 60 次采样）
        _downLineBrush.Color = Windows.UI.Color.FromArgb(210, 0x4C, 0xC2, 0xFF);
        _upLineBrush.Color = Windows.UI.Color.FromArgb(170, 0xFF, 0xB4, 0x54);
        _downLine = new Polyline { Stroke = _downLineBrush, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
        _upLine = new Polyline { Stroke = _upLineBrush, StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round };
        _sparkCanvas = new Canvas { Height = 26, Width = ExpandedContentWidth - 20 };
        _sparkCanvas.Children.Add(_upLine);
        _sparkCanvas.Children.Add(_downLine);

        // 第三行：连接数 · 累计流量 · TUN
        _sub = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _mutedBrush,
        };

        _detail = new StackPanel { Height = 0, Opacity = 0, Spacing = 6 };
        _detail.Children.Add(speedRow);
        _detail.Children.Add(_sparkCanvas);
        _detail.Children.Add(_sub);
        _detail.Children.Add(_siteRow);

        // 岛上直接操作：不用点开卡片就能换节点 / 切最快 / 立刻测速。
        // 宿主会顺着可视树上溯识别 ButtonBase，所以这里的按钮不会被当成"点了岛体"。
        // 一排塞下全部操作：短标签 + 紧凑内边距。
        // 分两排会让岛"变胖"，而灵动岛的美感靠的是扁、宽、克制。
        var controlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        controlRow.Children.Add(MakeIslandButton("◀", () => _plugin.SwitchNodeRelativeAsync(-1), 34));
        controlRow.Children.Add(MakeIslandButton("▶", () => _plugin.SwitchNodeRelativeAsync(1), 34));
        controlRow.Children.Add(MakeIslandButton("最快", () => _plugin.SwitchToFastestAsync()));
        controlRow.Children.Add(MakeIslandButton("测速", () => _plugin.RefreshNowAsync("岛上立即测速")));
        controlRow.Children.Add(MakeIslandButton("视频", () => _plugin.SwitchBySceneAsync("video")));
        controlRow.Children.Add(MakeIslandButton("游戏", () => _plugin.SwitchBySceneAsync("game")));
        controlRow.Children.Add(MakeIslandButton("下载", () => _plugin.SwitchBySceneAsync("download")));
        controlRow.Children.Add(MakeIslandButton("订阅", async () =>
        {
            await _plugin.UpdateSubscriptionsAsync();
        }));
        controlRow.Children.Add(MakeIslandButton("IP", () => _plugin.RefreshExitNowAsync()));
        _detail.Children.Add(controlRow);

        _root = new StackPanel
        {
            Width = CompactContentWidth,
            Spacing = 8,
            Padding = new Thickness(10, 10, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _root.Children.Add(header);
        _root.Children.Add(_detail);

        // 药丸的底做成上下渐变：平涂一层灰最丑，有明暗才像一块材料
        _pillBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops = { _pillStopTop, _pillStopMid, _pillStopBottom },
        };

        _pill = new Border
        {
            // 宽度**绝对不要写死**：宿主会把内容拉伸到"所有活动里最大的展开宽度"，
            // 写死成 400 时，超出 400 的那截就露出宿主的黑胶囊 —— 这就是"没铺满"。
            // 交给 Stretch，让它铺满宿主给的整个内容框。
            MinWidth = CompactContentWidth + PillBleed,
            Background = _pillBrush,
            BorderBrush = _pillEdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(PillRadiusCompact),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // 高度同理：也要铺满，不能按内容自适应
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = _root,
        };

        Content = _pill;

        // 文字清晰度：岛体现在是半透明玻璃，细体字压在会变化的背景上会发虚。
        // 统一提到 SemiBold 是最直接的可读性补救（比调颜色有效得多）。
        Loaded += (_, _) => EmboldenAll(_pill);

        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    /// <summary>把当前模式那个芯片点亮（用户设了强调色就用强调色）。</summary>
    private void HighlightMode(string mode)
    {
        _modeChipBrush.Color = _plugin.AccentColor ?? ClashFormat.ModeColor(mode);

        foreach (var (m, chip) in _modeChips)
        {
            chip.Background = string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)
                ? _modeChipBrush
                : _chipIdleBrush;
        }
    }

    /// <summary>把可视树里所有文字提到 SemiBold（半透明底上细体字会发虚）。</summary>
    private static void EmboldenAll(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text) text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            EmboldenAll(child);
        }
    }

    /// <summary>岛上那一排小按钮。样式统一，别让它们看起来像卡片里的大按钮。</summary>
    private Button MakeIslandButton(string label, Func<Task> action, double minWidth = 0)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 12,
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = _pillEdgeBrush,
            MinWidth = minWidth,
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>把最新状态铺到界面上（UI 线程调用）。</summary>
    public void Apply(ClashSnapshot snapshot)
    {
        if (!snapshot.Connected)
        {
            _fullName = "未连接 Clash Verge";
            _shortName = "Clash 未连接";
            _delay.Text = "";
            _delayChipBrush.Color = Windows.UI.Color.FromArgb(0, 0, 0, 0);

            HighlightMode("");
            _speedUp.Text = "";
            _speedDown.Text = "";
            _downLine.Points.Clear();
            _upLine.Points.Clear();

            _sub.Text = "请先在 Clash Verge 打开「外部控制」";
            UpdateTitleText();
            ApplyRegion(null);               // 连不上就用地球图标
            return;
        }

        var raw = snapshot.ActiveNode;

        // 国旗优先用**真实出口 IP 的归属地**。
        // 节点名经常骗人：「自动选择」「故障转移」「香港01」的实际落地可能在日本。
        // 只有查不到时（没开 ipgeo / 网络失败）才退回按节点名判断。
        //
        // 注意：IP 这条路**必须同样过一遍 DisplayCode**（台湾 → 中华人民共和国的国旗），
        // 否则出口 IP 报 TW 时会漏掉这条映射、显示出不该出现的旗。
        var code = string.IsNullOrEmpty(_plugin.ExitCountry)
            ? ClashFormat.DetectRegionCode(raw)
            : ClashFormat.DisplayCode(_plugin.ExitCountry);

        _fullName = string.IsNullOrEmpty(raw) ? "未选节点" : ClashFormat.CleanNode(raw);

        // 国旗已经表明国家了，名字里重复的地区词就多余 —— 小岛宽度很紧张
        var shortName = ClashFormat.ShortNode(raw);
        if (!string.IsNullOrEmpty(code))
        {
            var trimmed = ClashFormat.RemoveRegionWords(shortName, code);
            if (!string.IsNullOrWhiteSpace(trimmed)) shortName = trimmed;
        }
        _shortName = shortName;

        UpdateTitleText();
        ApplyRegion(code);

        _delay.Text = ClashFormat.Delay(snapshot.ActiveDelay);
        var delayColor = ClashFormat.DelayColor(snapshot.ActiveDelay);

        // 延迟芯片：**白字 + 实心色底**。
        // 之前是「文字和底色同一个色相、底色只有 18% 不透明」—— 绿字压浅绿底，
        // 对比度几乎为零；在纯黑岛上勉强能看，背景一浅就彻底读不出来。
        // 实心底 + 白字是胶囊标签的标准做法，任何背景下都清晰。
        _delayBrush.Color = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        _delayChipBrush.Color = delayColor;

        // 模式胶囊 + 彩色上下行
        HighlightMode(snapshot.Mode);
        _speedUp.Text = $"↑ {ClashFormat.Speed(snapshot.UpPerSec)}";
        _speedDown.Text = $"↓ {ClashFormat.Speed(snapshot.DownPerSec)}";

        UpdateSparkline(snapshot.DownHistory, snapshot.UpHistory);
        UpdateSites(snapshot.Sites);

        // 第三行：分组 · 连接数 · 累计流量 · TUN 状态
        var tun = snapshot.TunEnabled ? "TUN 开" : "TUN 关";
        var group = string.IsNullOrEmpty(snapshot.ActiveGroup) ? "—" : snapshot.ActiveGroup;

        // 层级：标签压暗、数字提亮 —— 一眼先看到数字。
        // 这段塞了 4 个数字，必须用最省字符的写法，否则整行溢出 400px 会被裁掉。
        static string Tiny(long b) => b switch
        {
            < 0 => "--",
            < 1024L * 1024 => $"{b / 1024}K",
            < 1024L * 1024 * 1024 => $"{b / (1024L * 1024)}M",
            _ => $"{b / (1024L * 1024 * 1024.0):0.#}G",
        };

        _sub.Inlines.Clear();
        Append($"{group} · {snapshot.Connections}连接 · ", false);
        Append(tun, true);                       // tun 本身已经是「TUN 开 / TUN 关」，别再加前缀
        Append("   今日 ", false);
        Append($"↑{Tiny(snapshot.TodayUp)}↓{Tiny(snapshot.TodayDown)}", true);
        Append("  本月 ", false);
        Append($"↑{Tiny(snapshot.MonthUp)}↓{Tiny(snapshot.MonthDown)}", true);

        void Append(string text, bool strong)
        {
            _sub.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = text,
                Foreground = strong ? _textBrush : _faintBrush,
            });
        }
    }

    /// <summary>
    /// 展开态最后一行：当前节点对每个测速网站的延迟。
    /// 网站列表是从 Clash Verge 的配置读来的，所以这里按名字动态建行，只在网站集合变化时重建。
    /// </summary>
    private void UpdateSites(List<ClashSiteLatency> sites)
    {
        var signature = string.Join(",", sites.Select(s => s.Name));
        if (signature != _siteSignature)
        {
            _siteSignature = signature;
            _siteTexts.Clear();
            _siteRow.Children.Clear();

            for (var i = 0; i < sites.Count; i++)
            {
                if (i > 0)
                {
                    _siteRow.Children.Add(new TextBlock
                    {
                        Text = "·",
                        FontSize = 11,
                        Margin = new Thickness(7, 0, 7, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = _faintBrush,
                    });
                }

                _siteRow.Children.Add(new TextBlock
                {
                    Text = sites[i].Name,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _faintBrush,
                });

                var delay = new TextBlock
                {
                    Text = "--",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _faintBrush,
                };

                _siteRow.Children.Add(delay);
                _siteTexts[sites[i].Url] = delay;
            }
        }

        foreach (var site in sites)
        {
            if (!_siteTexts.TryGetValue(site.Url, out var text)) continue;

            text.Text = ClashFormat.DelayShort(site.Delay);
            text.Foreground = site.Delay switch
            {
                <= 0 => _faintBrush,
                < 150 => _siteGoodBrush,
                < 300 => _siteWarnBrush,
                _ => _siteBadBrush,
            };
        }
    }

    /// <summary>
    /// 把最近若干次采样的网速画成曲线。
    /// 上下行共用一个纵向标尺，否则两条线各自归一化会看不出谁大谁小。
    /// </summary>
    private void UpdateSparkline(double[] down, double[] up)
    {
        _downLine.Points.Clear();
        _upLine.Points.Clear();

        if (down.Length < 2 && up.Length < 2) return;

        var width = _sparkCanvas.Width;
        var height = _sparkCanvas.Height;

        var max = 1d;
        foreach (var v in down) if (v > max) max = v;
        foreach (var v in up) if (v > max) max = v;

        Fill(_downLine, down, width, height, max);
        Fill(_upLine, up, width, height, max);
    }

    private static void Fill(Polyline line, double[] values, double width, double height, double max)
    {
        if (values.Length < 2) return;

        var step = width / (values.Length - 1);
        for (var i = 0; i < values.Length; i++)
        {
            var x = i * step;
            var y = height - Math.Clamp(values[i] / max, 0, 1) * height;
            line.Points.Add(new Windows.Foundation.Point(x, y));
        }
    }

    /// <summary>小岛显示精简名，展开后切成全名。</summary>
    private void UpdateTitleText() => _title.Text = _progress >= 0.5 ? _fullName : _shortName;

    /// <summary>
    /// 认得出地区：优先显示真国旗；插件里没这张图就退回「国家色 + 代码」徽章。
    /// 完全认不出地区才用地球图标。
    /// </summary>
    private void ApplyRegion(string? code)
    {
        if (!string.IsNullOrEmpty(code))
        {
            // 显示前先映射：台湾 → 中国（一个中国原则）
            code = ClashFormat.DisplayCode(code);

            var flag = _flags.Get(code);
            if (flag is not null)
            {
                _flagBrush.ImageSource = flag;
                _flagVisual.Opacity = 1;
                _badge.Opacity = 0;
                _icon.Opacity = 0;
                return;
            }

            _codeText.Text = code;
            _badgeBrush.Color = ClashFormat.RegionColor(code);
            _badge.Opacity = 1;
            _flagVisual.Opacity = 0;
            _icon.Opacity = 0;
            return;
        }

        _flagVisual.Opacity = 0;
        _badge.Opacity = 0;
        _icon.Opacity = 1;
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(expanded: true, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(expanded: false, duration);

    /// <summary>
    /// 插件停用时退订主题事件。
    /// 不退订的话，宿主的主题对象会一直持有这个视图 → 持有整个插件程序集 →
    /// 日志里就会出现「插件程序集已请求卸载，但未能回收（可能存在残留引用）」。
    /// </summary>
    public void Detach()
    {
        _theme.Changed -= ApplyThemeColors;
        _morphTimer?.Stop();
        _flagBrush.ImageSource = null;
    }

    /// <summary>
    /// 岛体配色。**跟插件的"外观"走，不是跟岛体主题走** ——
    /// 用户把外观选成白色玻璃时，盖在胶囊上的药丸是浅色的，文字必须翻成深色。
    /// </summary>
    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(238);
        _faintBrush.Color = Neutral(200);
        _flagEdgeBrush.Color = Neutral(70);
        _chipIdleBrush.Color = Neutral(44);

        // 浅色底上"白底旗"（日本、塞浦路斯…）会和底色糊在一起，加一圈极淡的描边分开；
        // 深色底本来就不需要，保持 0 边框最干净。
        _flagVisual.BorderBrush = _flagEdgeBrush;
        _flagVisual.BorderThickness = _plugin.AppearanceIsLight ? new Thickness(1) : new Thickness(0);

        RefreshAppearance();
    }

    /// <summary>给自绘的岛体药丸上色。用户关掉自绘时就把药丸做成全透明，露出宿主原样。</summary>
    public void RefreshAppearance()
    {
        if (!_plugin.IslandOwnBackground)
        {
            _pillStopTop.Color = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            _pillStopMid.Color = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            _pillStopBottom.Color = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            _pillEdgeBrush.Color = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            return;
        }

        var baseColor = _plugin.AppearanceBaseColor;
        var alpha = _plugin.CardOpacity / 100.0;

        byte A(double k) => (byte)Math.Clamp(alpha * 255 * k, 0, 255);

        // 上亮下暗：平涂那层灰看着像塑料，有梯度才像"受光的一块材料"
        _pillStopTop.Color = Windows.UI.Color.FromArgb(A(1.12), baseColor.R, baseColor.G, baseColor.B);
        _pillStopMid.Color = Windows.UI.Color.FromArgb(A(0.92), baseColor.R, baseColor.G, baseColor.B);
        _pillStopBottom.Color = Windows.UI.Color.FromArgb(A(0.74), baseColor.R, baseColor.G, baseColor.B);

        // 浅色玻璃用暗边勾轮廓，深色玻璃用亮边挂光
        _pillEdgeBrush.Color = _plugin.AppearanceIsLight
            ? Windows.UI.Color.FromArgb(72, 0, 0, 0)
            : Windows.UI.Color.FromArgb(96, 255, 255, 255);

        // 强调色：用户设了就盖掉网速曲线的默认蓝
        if (_plugin.AccentColor is Windows.UI.Color accent)
        {
            _downLineBrush.Color = Windows.UI.Color.FromArgb(210, accent.R, accent.G, accent.B);
        }
        else
        {
            _downLineBrush.Color = Windows.UI.Color.FromArgb(210, 0x4C, 0xC2, 0xFF);
        }
    }

    /// <summary>中性色：看**当前外观**的明暗，不是看岛体主题。</summary>
    private Windows.UI.Color Neutral(byte alpha) => _plugin.AppearanceIsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>
    /// 形态动画走「逐帧属性赋值」，刻意不用 Storyboard —— 属性路径动画在动态加载的插件程序集里
    /// 解析不出类型信息，会在动画 tick 上抛 COMException (0x800F1001)，调用点的 try/catch 拦不住。
    /// </summary>
    private void StartMorph(bool expanded, TimeSpan duration)
    {
        var target = expanded ? 1d : 0d;

        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / durationMs, 0, 1);

        ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * LiquidEase(t));

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);   // 收尾锁终态，避免残留中间值
        }
    }

    /// <summary>
    /// 液态玻璃的动效曲线：一条欠阻尼的"铺开—回稳"响应。
    /// 比 BackEase 更柔、余韵更长，像一滴液体摊开，而不是机械回弹。
    /// </summary>
    private static double LiquidEase(double t)
    {
        if (t >= 1) return 1;

        const double omega = 10.5;   // 快慢
        const double zeta = 0.62;    // 阻尼比：越大越稳，越小越弹
        var wd = omega * Math.Sqrt(1 - zeta * zeta);
        var decay = Math.Exp(-zeta * omega * t);

        return 1 - decay * (Math.Cos(wd * t) + zeta / Math.Sqrt(1 - zeta * zeta) * Math.Sin(wd * t));
    }

    /// <summary>把 0~1 的形态进度铺到各元素上。progress 会因 BackEase 略微越过 0/1。</summary>
    private void ApplyMorph(double progress)
    {
        _progress = progress;

        _detail.Height = Math.Max(0, ExpandedDetailHeight * progress);
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        _icon.FontSize = CompactIconSize + (ExpandedIconSize - CompactIconSize) * progress;
        _flagVisual.Width = CompactFlagWidth + (ExpandedFlagWidth - CompactFlagWidth) * progress;
        _flagVisual.Height = CompactFlagHeight + (ExpandedFlagHeight - CompactFlagHeight) * progress;
        _codeText.FontSize = CompactBadgeSize + (ExpandedBadgeSize - CompactBadgeSize) * progress;
        _title.FontSize = CompactTitleSize + (ExpandedTitleSize - CompactTitleSize) * progress;
        _title.Width = CompactTitleMaxWidth + (ExpandedTitleMaxWidth - CompactTitleMaxWidth) * progress;
        _root.Width = CompactContentWidth + (ExpandedContentWidth - CompactContentWidth) * progress;
        _delay.FontSize = 11 + 2 * progress;

        // 药丸跟着一起变圆（宽度交给宿主 Stretch，不再自己算）
        _pill.CornerRadius = new CornerRadius(
            PillRadiusCompact + (PillRadiusExpanded - PillRadiusCompact) * progress);

        // 宽度在变：过了一半就换成全名，正好配合展开动画
        UpdateTitleText();
    }
}
