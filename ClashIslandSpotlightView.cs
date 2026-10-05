using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using WinIsland.Core;

namespace ClashVergeIsland;

/// <summary>
/// 「超级展开」聚光卡：点击岛体后从岛体位置带倾角飞入的大卡片。
/// 飞入飞回 / 遮罩 / 圆角 / Esc 收起全由宿主负责，这里只管最终形态。
///
/// 内容：当前状态 + 模式切换 + 分组选择 + 节点列表（点一下切过去）。
/// 必须是独立于岛视图的另一棵可视树。
/// </summary>
public sealed class ClashIslandSpotlightView : UserControl
{
    /// <summary>节点太多时只显示前若干个，避免一次性造几千个按钮。</summary>
    private const int MaxNodes = 300;

    /// <summary>
    /// 聚光卡的期望高度（要和插件里 OpenSpotlight 的 Size.Height 保持一致）。
    /// 用来给根容器兜底撑高 —— 宿主按内容的期望高度给尺寸，内容一短玻璃就只剩半块。
    /// </summary>
    private const double CardHeight = 670;

    private readonly ClashVergeIslandPlugin _plugin;
    private readonly IIslandTheme _theme;

    private readonly TextBlock _status = new() { FontSize = 13 };
    private readonly TextBlock _speedUp = new() { FontSize = 13 };
    private readonly TextBlock _speedDown = new() { FontSize = 13 };
    private readonly SolidColorBrush _upBrush = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xB4, 0x54));
    private readonly SolidColorBrush _downBrush = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF));
    private readonly ComboBox _groupBox = new() { MinWidth = 200 };
    private readonly StackPanel _nodeList = new() { Spacing = 2 };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };

    /// <summary>今日 / 本月流量那一行。</summary>
    private readonly TextBlock _trafficText = new();

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _rowBrush = new();
    private readonly SolidColorBrush _rowHoverBrush = new();
    private readonly SolidColorBrush _rowEdgeBrush = new();
    private readonly SolidColorBrush _selectedRowBrush = new();
    private readonly SolidColorBrush _selectedRowEdgeBrush = new();
    private readonly SolidColorBrush _flagEdgeBrush = new();
    private readonly SolidColorBrush _pillEdgeBrush = new();

    // 分隔线做成「两端淡出」：在深色卡片上一条通到底的实线显得很硬
    private readonly GradientStop _dividerStopA = new() { Offset = 0.0 };
    private readonly GradientStop _dividerStopB = new() { Offset = 0.5 };
    private readonly GradientStop _dividerStopC = new() { Offset = 1.0 };
    private readonly LinearGradientBrush _dividerBrush;

    // 卡片顶沿的一道高光：模拟玻璃受光面，是「有厚度」和「一块色板」的分界
    private readonly GradientStop _glossStopA = new() { Offset = 0.0 };
    private readonly GradientStop _glossStopB = new() { Offset = 0.5 };
    private readonly GradientStop _glossStopC = new() { Offset = 1.0 };
    private readonly LinearGradientBrush _glossBrush;

    // 曲线下方的面积渐变（顶端有色 → 底端透明）
    private readonly GradientStop _areaStopTop = new() { Offset = 0.0 };
    private readonly GradientStop _areaStopBottom = new() { Offset = 1.0 };
    private readonly LinearGradientBrush _areaBrush;

    // ── 液态玻璃 ────────────────────────────────────────────────
    // 宿主画的是**不透明**的深色卡片底，插件没法换掉它。
    // 既然改不了下面那层，就整张**盖一层自己的玻璃**：半透明提亮 + 上亮下暗的边 + 左上角高光。
    private readonly GradientStop _glassStopTop = new() { Offset = 0.0 };
    private readonly GradientStop _glassStopMid = new() { Offset = 0.55 };
    private readonly GradientStop _glassStopBottom = new() { Offset = 1.0 };
    private readonly LinearGradientBrush _glassBrush;

    private readonly GradientStop _glassEdgeTop = new() { Offset = 0.0 };
    private readonly GradientStop _glassEdgeMid = new() { Offset = 0.45 };
    private readonly GradientStop _glassEdgeBottom = new() { Offset = 1.0 };
    private readonly LinearGradientBrush _glassEdgeBrush;

    /// <summary>左上角那道斜向反光。玻璃最关键的信号不是模糊，是这道高光。</summary>
    private readonly GradientStop _sheenStart = new() { Offset = 0.0 };
    private readonly GradientStop _sheenEnd = new() { Offset = 1.0 };
    private readonly LinearGradientBrush _sheenBrush;

    /// <summary>玻璃内圈：贴边一圈极淡的亮线，让"玻璃有厚度"。</summary>
    private readonly SolidColorBrush _glassInnerBrush = new();

    /// <summary>
    /// 延迟颜色用共享画刷。原来是每次刷新都给每个节点 new 一个画刷
    /// （300 个节点 × 每 2 秒一次），纯属浪费；延迟只有四档，缓存四支就够了。
    /// </summary>
    /// <summary>
    /// 延迟胶囊的底色。用饱和色而不是亮色，因为胶囊里是白字 ——
    /// 这样和 Clash Verge 节点列表里那种"绿底白字小胶囊"是一致的观感。
    /// </summary>
    private readonly SolidColorBrush _delayUnknownBrush = new(Windows.UI.Color.FromArgb(255, 0x5C, 0x6B, 0x7A));
    private readonly SolidColorBrush _delayGoodBrush = new(Windows.UI.Color.FromArgb(255, 0x2E, 0xA0, 0x43));
    private readonly SolidColorBrush _delayWarnBrush = new(Windows.UI.Color.FromArgb(255, 0xC7, 0x8A, 0x0E));
    private readonly SolidColorBrush _delayBadBrush = new(Windows.UI.Color.FromArgb(255, 0xD9, 0x3B, 0x3B));

    private SolidColorBrush DelayBrush(int ms) => ms switch
    {
        <= 0 => _delayUnknownBrush,
        < 150 => _delayGoodBrush,
        < 300 => _delayWarnBrush,
        _ => _delayBadBrush,
    };

    private readonly Dictionary<string, TextBlock> _delayLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _delayPills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _rowButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _rowTitles = new(StringComparer.Ordinal);

    /// <summary>延迟胶囊里的字：胶囊底色是饱和色，所以恒用白字。</summary>
    private readonly SolidColorBrush _pillTextBrush =
        new(Windows.UI.Color.FromArgb(255, 255, 255, 255));

    private readonly List<Button> _modeButtons = new();
    private readonly Dictionary<string, Button> _targetButtons = new(StringComparer.Ordinal);

    /// <summary>TUN 开关。用 _syncingTun 防止"程序填值"被当成用户操作（和下拉框一个坑）。</summary>
    private readonly ToggleSwitch _tunSwitch = new()
    {
        Header = "TUN",
        OnContent = "开",
        OffContent = "关",
        MinWidth = 96,
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private bool _syncingTun;

    /// <summary>「谁在用流量」的列表容器。</summary>
    private readonly StackPanel _connList = new() { Spacing = 2 };
    private readonly TextBlock _connEmpty = new() { FontSize = 13, Margin = new Thickness(4, 8, 0, 8) };
    private readonly Dictionary<string, Border> _connRows = new(StringComparer.Ordinal);

    /// <summary>地区筛选条 + 当前选中的地区（空 = 全部）。</summary>
    private readonly StackPanel _regionRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private string _regionFilter = "";

    /// <summary>本组里延迟最低的节点名（空 = 还没测出可用节点），行里用它标「最快」。</summary>
    private string _fastestNode = "";

    /// <summary>最快节点那一行的描边色（一眼看出来该选哪个）。</summary>
    private readonly SolidColorBrush _fastestEdgeBrush =
        new(Windows.UI.Color.FromArgb(200, 0x3F, 0xB9, 0x50));

    /// <summary>每个测速网站芯片里的延迟文字（键是网站 URL），实时更新。</summary>
    private readonly Dictionary<string, TextBlock> _targetDelays = new(StringComparer.Ordinal);

    /// <summary>延迟趋势曲线：光看当前值看不出规律，得有一段历史。</summary>
    private readonly TextBlock _trendCaption = new()
    {
        Text = "延迟趋势",
        FontSize = 13.5,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Canvas _trendCanvas = new() { Height = 30, Width = 800 };
    private readonly Polyline _trendLine;

    /// <summary>曲线下方的渐变面积 —— 单单一根细线在深色卡片上太空、太单薄。</summary>
    private readonly Polygon _trendArea = new() { StrokeThickness = 0 };

    /// <summary>曲线末端的亮点，标出「现在」在哪。</summary>
    private readonly Ellipse _trendDot = new() { Width = 6, Height = 6, Visibility = Visibility.Collapsed };

    private readonly SolidColorBrush _trendBrush = new(Windows.UI.Color.FromArgb(235, 0x5C, 0xC8, 0xFF));
    private readonly SolidColorBrush _trendDotBrush = new(Windows.UI.Color.FromArgb(255, 0xA8, 0xE0, 0xFF));

    private readonly FlagLibrary _flags;
    private readonly TextBox _search = new() { PlaceholderText = "搜索节点…", MinWidth = 180 };

    /// <summary>卡片左上角的大国旗，跟着当前节点变。</summary>
    private const double HeaderFlagWidth = 34;
    private const double HeaderFlagHeight = 23;

    private readonly Border _headerFlag = new()
    {
        Width = HeaderFlagWidth,
        Height = HeaderFlagHeight,
        CornerRadius = new CornerRadius(4),
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0,
    };

    private string _headerFlagCode = "\u0000";
    private string _filter = "";

    private string _builtGroup = "";
    private string _builtSignature = "";
    private string _groupSignature = "";

    /// <summary>当前分组选中的节点名。鼠标移出节点行时要靠它决定还原成"选中"还是"普通"底色。</summary>
    private string _currentNodeName = "";
    private bool _loading = true;

    public ClashIslandSpotlightView(
        PluginManifest manifest, IIslandTheme theme, ClashVergeIslandPlugin plugin, FlagLibrary flags)
    {
        _plugin = plugin;
        _theme = theme;
        _flags = flags;

        // 三支渐变画刷在这里建：GradientStops 要装的是上面那几个字段实例，
        // 换主题时直接改那些 stop 的颜色，画刷不用重建。
        _dividerBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
            GradientStops = { _dividerStopA, _dividerStopB, _dividerStopC },
        };
        _glossBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
            GradientStops = { _glossStopA, _glossStopB, _glossStopC },
        };
        _areaBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops = { _areaStopTop, _areaStopBottom },
        };

        // 玻璃主体：从上到下略微变淡，模拟"上面受光多"
        _glassBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops = { _glassStopTop, _glassStopMid, _glassStopBottom },
        };

        // 玻璃的边：顶上那圈最亮，往下收掉 —— 这一条决定了它像不像一块有厚度的玻璃
        _glassEdgeBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops = { _glassEdgeTop, _glassEdgeMid, _glassEdgeBottom },
        };

        // 左上角斜向高光（135° 扫过去）
        _sheenBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = { _sheenStart, _sheenEnd },
        };

        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = _textBrush,
        };

        _status.Foreground = _mutedBrush;
        _hint.Foreground = _faintBrush;

        // 模式：三个按钮，当前模式高亮（点一下直接切）
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        modeRow.Children.Add(new TextBlock
        {
            Text = "模式",
            FontSize = 13,
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _mutedBrush,
        });
        modeRow.Children.Add(MakeModeButton("rule", "规则"));
        modeRow.Children.Add(MakeModeButton("global", "全局"));
        modeRow.Children.Add(MakeModeButton("direct", "直连"));

        // TUN 开关（游戏 / 需要全局接管时用得上）
        _tunSwitch.Toggled += async (_, _) =>
        {
            if (_syncingTun) return;      // 程序填值不算用户操作
            await _plugin.SetTunAsync(_tunSwitch.IsOn);
        };
        modeRow.Children.Add(_tunSwitch);

        // 分组选择
        var groupRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        groupRow.Children.Add(new TextBlock
        {
            Text = "分组",
            FontSize = 13,
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _mutedBrush,
        });
        groupRow.Children.Add(_groupBox);

        var refresh = new Button
        {
            Content = "刷新",
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 5, 14, 5),
        };
        refresh.Click += async (_, _) => await _plugin.RefreshNowAsync("聚光卡刷新");

        // 文字别超宽：原来写「测试本组延迟」6 个字，被按钮宽度裁成了「测试本组延」
        var test = new Button
        {
            Content = "本组测速",
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 5, 14, 5),
        };
        test.Click += async (_, _) =>
        {
            var group = _groupBox.SelectedItem as string;
            if (!string.IsNullOrEmpty(group)) await _plugin.TestGroupAsync(group);
        };
        // 一键切最快：这个插件相对 Clash Verge 原界面最有价值的一步
        var fastest = new Button
        {
            Content = "切最快",
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 5, 14, 5),
        };
        fastest.Click += async (_, _) =>
        {
            fastest.IsEnabled = false;
            try
            {
                var picked = await _plugin.SwitchToFastestAsync();
                _hint.Text = picked is null ? "没测出可用节点" : $"已切到最快：{picked}";
            }
            finally
            {
                fastest.IsEnabled = true;
            }
        };

        groupRow.Children.Add(refresh);
        groupRow.Children.Add(test);
        groupRow.Children.Add(fastest);
        groupRow.Children.Add(_search);

        // 搜索：过滤节点名（机场节点动辄上百个，没搜索很难找）
        _search.TextChanged += (_, _) =>
        {
            var text = (_search.Text ?? "").Trim();
            if (string.Equals(text, _filter, StringComparison.Ordinal)) return;
            _filter = text;
            _builtSignature = "";   // 强制重建列表
        };

        _groupBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;   // 填充候选项时会触发一次，别当成用户操作
            _builtGroup = "";       // 强制重建节点列表
            _plugin.SetSpotlightGroup(_groupBox.SelectedItem as string ?? "");
        };

        // 测速网站：和 Clash Verge 界面里那套一致（Apple / GitHub / Google / YouTube）
        var targetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        targetRow.Children.Add(new TextBlock
        {
            Text = "测速",
            FontSize = 13,
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _mutedBrush,
        });
        foreach (var target in _plugin.Targets)
        {
            // 每个网站芯片 = 网站名 + 当前节点对它的延迟（实时刷新）
            var nameText = new TextBlock
            {
                Text = target.Name,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var delayText = new TextBlock
            {
                Text = "--",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(nameText);
            content.Children.Add(delayText);

            var button = new Button
            {
                Content = content,
                Tag = target.Name,
                Padding = new Thickness(12, 5, 12, 5),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = _rowEdgeBrush,
            };
            button.Click += (_, _) => _plugin.SetTarget(target.Name);
            _targetButtons[target.Name] = button;
            _targetDelays[target.Url] = delayText;
            targetRow.Children.Add(button);
        }

        var testAll = new Button
        {
            Content = "全部测试",
            FontSize = 12,
            Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = _rowEdgeBrush,
        };
        testAll.Click += async (_, _) => await _plugin.TestAllSitesAsync();
        targetRow.Children.Add(testAll);

        var sort = new CheckBox
        {
            Content = "排序",
            IsChecked = _plugin.SortByDelay,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        sort.Checked += (_, _) => { _plugin.SetSortByDelay(true); _builtSignature = ""; };
        sort.Unchecked += (_, _) => { _plugin.SetSortByDelay(false); _builtSignature = ""; };
        targetRow.Children.Add(sort);

        // 订阅常附带「剩余流量 / 套餐到期」这类信息条目，它们不是能连的节点，默认藏掉
        var hideInfo = new CheckBox
        {
            Content = "隐藏信息",
            IsChecked = _plugin.HideInfoEntries,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        hideInfo.Checked += (_, _) => { _plugin.SetHideInfoEntries(true); _builtSignature = ""; };
        hideInfo.Unchecked += (_, _) => { _plugin.SetHideInfoEntries(false); _builtSignature = ""; };
        targetRow.Children.Add(hideInfo);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 240,
            Content = _nodeList,
        };

        var titleRow = new Grid { VerticalAlignment = VerticalAlignment.Center };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 国旗
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 标题
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 弹簧
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 网速
        Grid.SetColumn(_headerFlag, 0);
        Grid.SetColumn(title, 1);
        titleRow.Children.Add(_headerFlag);
        titleRow.Children.Add(title);

        // 实时网速挪到标题行右侧：这样就不用再多占一行（原来的「模式：/分组：/实时网速：」三行全是重复信息）
        _speedUp.Foreground = _upBrush;
        _speedDown.Foreground = _downBrush;
        var speedRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        speedRow.Children.Add(_speedUp);
        speedRow.Children.Add(_speedDown);
        Grid.SetColumn(speedRow, 3);
        titleRow.Children.Add(speedRow);

        var root = new StackPanel
        {
            // 行距 12 → 8：卡片里十几行，每行省 4px 就是 50px —— 比压缩任何单个控件都值
            Spacing = 8,
            Padding = new Thickness(32, 24, 32, 24),
        };
        root.Children.Add(titleRow);
        root.Children.Add(_status);
        root.Children.Add(new Border { Height = 1, Background = _dividerBrush });

        // 延迟趋势：横轴间隔 = 刷新间隔（均匀），纵轴是延迟，越低的点画得越高
        _trendCaption.Foreground = _faintBrush;
        _trendLine = new Polyline
        {
            Stroke = _trendBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _trendArea.Fill = _areaBrush;
        _trendDot.Fill = _trendDotBrush;

        // 图层顺序：面积在最下，线压在上面，末端的亮点在最上层
        _trendCanvas.Children.Add(_trendArea);
        _trendCanvas.Children.Add(_trendLine);
        _trendCanvas.Children.Add(_trendDot);

        var trendRow = new Grid();
        trendRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trendRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_trendCaption, 0);
        Grid.SetColumn(_trendCanvas, 1);
        _trendCaption.Margin = new Thickness(0, 0, 12, 0);
        trendRow.Children.Add(_trendCaption);
        trendRow.Children.Add(_trendCanvas);
        root.Children.Add(trendRow);

        // 今日 / 本月流量（自己攒的，见 TrafficStats）
        _trafficText.Foreground = _mutedBrush;
        _trafficText.FontSize = 12.5;
        root.Children.Add(_trafficText);

        // 顺序：先看数据（趋势 + 网站延迟）→ 再动设置（模式 / 分组）→ 最后是节点列表
        root.Children.Add(targetRow);
        root.Children.Add(modeRow);

        // 场景切换 + 更新订阅：不想研究节点名的时候，点一下就行
        var sceneRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sceneRow.Children.Add(new TextBlock
        {
            Text = "场景",
            FontSize = 13,
            Width = 56,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _mutedBrush,
        });

        foreach (var (scene, label) in new[] { ("video", "看视频"), ("game", "游戏"), ("download", "下载") })
        {
            var captured = scene;
            var sceneButton = new Button
            {
                Content = label,
                FontSize = 12,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = _rowEdgeBrush,
            };
            sceneButton.Click += async (_, _) =>
            {
                sceneButton.IsEnabled = false;
                try
                {
                    var picked = await _plugin.SwitchBySceneAsync(captured);
                    _hint.Text = picked is null ? "没有可用的节点" : $"已切到：{picked}";
                }
                finally
                {
                    sceneButton.IsEnabled = true;
                }
            };
            sceneRow.Children.Add(sceneButton);
        }

        var updateSubs = new Button
        {
            Content = "更新订阅",
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = _rowEdgeBrush,
            Margin = new Thickness(8, 0, 0, 0),
        };
        updateSubs.Click += async (_, _) =>
        {
            updateSubs.IsEnabled = false;
            try
            {
                var count = await _plugin.UpdateSubscriptionsAsync();
                _hint.Text = count == 0 ? "没找到代理订阅" : $"已触发 {count} 个订阅更新";
            }
            finally
            {
                updateSubs.IsEnabled = true;
            }
        };
        sceneRow.Children.Add(updateSubs);

        // 立刻重查出口归属地（国旗是跟 IP 走的，换节点后想马上看到结果就点它）
        var refreshIp = new Button
        {
            Content = "IP",
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = _rowEdgeBrush,
        };
        refreshIp.Click += async (_, _) =>
        {
            refreshIp.IsEnabled = false;
            try
            {
                await _plugin.RefreshExitNowAsync();
                _hint.Text = string.IsNullOrEmpty(_plugin.ExitCountry)
                    ? "IP 归属地刷新失败"
                    : $"出口归属地：{_plugin.ExitCountry}";
            }
            finally
            {
                refreshIp.IsEnabled = true;
            }
        };
        sceneRow.Children.Add(refreshIp);

        root.Children.Add(sceneRow);
        root.Children.Add(groupRow);
        // 地区筛选：节点一多（日本/香港/新加坡… 几十个）就靠它一眼缩到想要的区
        _regionRow.Margin = new Thickness(0, 0, 0, 2);
        root.Children.Add(_regionRow);

        root.Children.Add(scroller);

        // 「谁在用流量」：回答"我没下东西，为什么网速这么慢"
        var connHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        connHeader.Children.Add(new TextBlock
        {
            Text = "在用流量",
            FontSize = 13,
            Width = 56,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _mutedBrush,
        });

        var closeAll = new Button
        {
            Content = "全部断开",
            FontSize = 12,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 4, 12, 4),
            BorderThickness = new Thickness(1),
            BorderBrush = _rowEdgeBrush,
        };
        closeAll.Click += async (_, _) =>
        {
            closeAll.IsEnabled = false;
            try { await _plugin.CloseAllConnectionsAsync(); }
            finally { closeAll.IsEnabled = true; }
        };
        connHeader.Children.Add(closeAll);

        _connEmpty.Foreground = _faintBrush;
        _connEmpty.Text = "没有活动连接";

        var connSection = new StackPanel { Spacing = 6 };
        connSection.Children.Add(connHeader);
        connSection.Children.Add(_connEmpty);
        connSection.Children.Add(_connList);
        root.Children.Add(connSection);

        root.Children.Add(_hint);

        // 2.0 起**不再自绘卡片的底** —— 卡片的背景完全交给宿主的默认外观。
        //
        // 这里原本叠了四层：玻璃主体 / 斜向反光 / 内圈亮线 / 顶沿高光。
        // 那等于给卡片套了一层插件自己的颜色（就是那层"白色玻璃"），
        // 颜色不再可调之后这四层就没有存在意义了，只会盖住宿主的默认背景。
        //
        // MinHeight 必须保留：宿主是按内容的**期望高度**给卡片尺寸的，
        // 不顶住的话内容一短（比如节点列表为空），卡片就会缩成一小块。
        var cardRoot = new Grid { MinHeight = CardHeight };
        cardRoot.Children.Add(root);        // 只留内容

        Content = cardRoot;

        _loading = false;
    }

    private Button MakeModeButton(string mode, string label)
    {
        var button = new Button
        {
            Content = label,
            Tag = mode,
            // 圆角和内边距和「刷新 / 本组测速」统一，否则一排按钮高矮不一
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 5, 16, 5),
            MinWidth = 66,
        };
        button.Click += async (_, _) => await _plugin.SetModeAsync(mode);
        _modeButtons.Add(button);
        return button;
    }

    /// <summary>宿主收起卡片时的收尾（这里没有长连接要断，只做提示清理）。</summary>
    public void OnHostClosed() => _hint.Text = "";

    /// <summary>插件停用时退订主题事件，避免宿主一直持有本视图导致程序集无法回收。</summary>
    public void Detach()
    {
        _theme.Changed -= ApplyThemeColors;
        _delayLabels.Clear();
        _delayPills.Clear();
        _rowButtons.Clear();
        _rowTitles.Clear();
        _nodeList.Children.Clear();
    }

    /// <summary>把最新状态铺到卡片上（UI 线程调用，由插件的定时器驱动）。</summary>
    public void Apply(ClashSnapshot snapshot)
    {
        if (!snapshot.Connected)
        {
            _status.Text = "未连接 Clash Verge";
            _status.Text = snapshot.Error ?? "未连接 Clash Verge";
            _speedUp.Text = "";
            _speedDown.Text = "";
            _hint.Text = "打开方法：Clash Verge → 设置 → 「Clash 设置」→ External → " +
                         "打开「Enable External Controller」，地址 127.0.0.1:9097，并设一个 Core Secret，" +
                         "然后把端口和密码填到本插件的设置页。";
            _nodeList.Children.Clear();
            _delayLabels.Clear();
            _delayPills.Clear();
            _rowButtons.Clear();
            _rowTitles.Clear();
            _builtGroup = "";
            _builtSignature = "";
            return;
        }

        _status.Text = $"{ClashFormat.CleanNodeKeepRegion(snapshot.ActiveNode)}   ·   " +
                       $"延迟 {ClashFormat.Delay(snapshot.ActiveDelay)}   ·   Mihomo {snapshot.Version}";
        ApplyHeaderFlag(snapshot.ActiveNode);
        RefreshTargetButtons(snapshot);
        DrawTrend(snapshot.DelayTrend);

        // TUN 开关跟着实际状态走（填值时屏蔽事件，别把"显示"当成"用户操作"）
        _syncingTun = true;
        try { _tunSwitch.IsOn = snapshot.TunEnabled; }
        finally { _syncingTun = false; }

        SyncConnections(snapshot);

        _trafficText.Text =
            $"今日 ↑{ClashFormat.Bytes(snapshot.TodayUp)}  ↓{ClashFormat.Bytes(snapshot.TodayDown)}" +
            $"     本月 ↑{ClashFormat.Bytes(snapshot.MonthUp)}  ↓{ClashFormat.Bytes(snapshot.MonthDown)}";
        _speedUp.Text = $"↑ {ClashFormat.Speed(snapshot.UpPerSec)}";
        _speedDown.Text = $"↓ {ClashFormat.Speed(snapshot.DownPerSec)}";
        _hint.Text = "点任意节点即可切换；绿色为延迟低，红色为延迟高或不可用。网站延迟：" +
                     (_plugin.SiteTestAgeSeconds switch
                     {
                         < 0 => "尚未测速",
                         < 5 => "刚刚更新",
                         var s => $"{s} 秒前更新",
                     });

        foreach (var button in _modeButtons)
        {
            var isCurrent = string.Equals(button.Tag as string, snapshot.Mode, StringComparison.OrdinalIgnoreCase);
            button.Background = isCurrent ? _selectedRowBrush : _rowBrush;
        }

        SyncGroups(snapshot);
        SyncNodes(snapshot);
    }

    private void SyncGroups(ClashSnapshot snapshot)
    {
        var signature = string.Join("|", snapshot.Groups.Select(g => g.Name));
        if (signature != _groupSignature)
        {
            _groupSignature = signature;
            _loading = true;
            var keep = _plugin.SpotlightGroup;
            _groupBox.Items.Clear();
            foreach (var group in snapshot.Groups)
            {
                _groupBox.Items.Add(group.Name);
            }

            var index = snapshot.Groups.FindIndex(g => g.Name == keep);
            _groupBox.SelectedIndex = index >= 0 ? index : 0;

            // 填充候选项和设置选中项都会触发 SelectionChanged，必须等两件事都做完再解除屏蔽，
            // 否则「程序自己填的默认值」会被当成用户的选择写进设置（sdk-api §15.1 的 ComboBox 坑）。
            _loading = false;
        }
    }

    private ClashGroup? SelectedGroup(ClashSnapshot snapshot)
    {
        var name = _groupBox.SelectedItem as string;
        if (string.IsNullOrEmpty(name)) name = snapshot.ActiveGroup;
        return snapshot.Groups.FirstOrDefault(g => g.Name == name) ?? snapshot.Groups.FirstOrDefault();
    }

    private void SyncNodes(ClashSnapshot snapshot)
    {
        var group = SelectedGroup(snapshot);
        if (group is null) return;

        _currentNodeName = group.Now;

        // 「剩余流量 / 套餐到期」这类订阅信息条目不是能连的节点，默认藏掉，列表才干净
        var pool = _plugin.HideInfoEntries
            ? group.Nodes.Where(n => !ClashFormat.IsInfoEntry(n.Name)).ToList()
            : group.Nodes;

        var filtered = string.IsNullOrEmpty(_filter)
            ? pool
            : pool.Where(n => n.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        // 地区筛选：拿节点名里识别出来的地区码比对（DetectRegionCode 已经处理了中英文别名）
        if (!string.IsNullOrEmpty(_regionFilter))
        {
            filtered = filtered
                .Where(n => string.Equals(ClashFormat.DetectRegionCode(n.Name), _regionFilter, StringComparison.Ordinal))
                .ToList();
        }

        // 按延迟从快到慢排（没测过的排最后），让"哪个节点快"一眼可见
        if (_plugin.SortByDelay)
        {
            filtered = filtered.OrderBy(n => n.Delay < 0 ? int.MaxValue : n.Delay).ToList();
        }

        var visible = filtered.Take(MaxNodes).ToList();
        var signature = group.Name + "#" + _filter + "#" + _plugin.HideInfoEntries + "#" +
                        string.Join(",", visible.Select(n => n.Name));

        if (group.Name != _builtGroup || signature != _builtSignature)
        {
            _builtGroup = group.Name;
            _builtSignature = signature;
            BuildRegionChips(group, pool);
            _delayLabels.Clear();
            _delayPills.Clear();
            _rowButtons.Clear();
            _rowTitles.Clear();
            _nodeList.Children.Clear();

            if (visible.Count == 0)
            {
                _nodeList.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(_filter) ? "这个分组没有节点" : $"没有匹配「{_filter}」的节点",
                    FontSize = 13,
                    Margin = new Thickness(4, 12, 0, 12),
                    Foreground = _faintBrush,
                });
            }

            foreach (var node in visible)
            {
                var row = BuildRow(group.Name, node);
                _nodeList.Children.Add(row);
            }

            if (filtered.Count > MaxNodes)
            {
                _nodeList.Children.Add(new TextBlock
                {
                    Text = $"（共 {filtered.Count} 个节点，只显示前 {MaxNodes} 个）",
                    FontSize = 12,
                    Foreground = _faintBrush,
                });
            }
        }

        // 「最快」是随延迟实时变的，所以放在就地更新这一轮算，不跟重建走
        _fastestNode = visible
            .Where(n => n.Delay > 0 && !ClashFormat.IsInfoEntry(n.Name))
            .OrderBy(n => n.Delay)
            .Select(n => n.Name)
            .FirstOrDefault() ?? "";

        // 延迟和「当前选中」会变，就地更新，不重建控件树
        foreach (var node in visible)
        {
            if (_delayLabels.TryGetValue(node.Name, out var label))
            {
                label.Text = ClashFormat.DelayShort(node.Delay);
            }

            if (_delayPills.TryGetValue(node.Name, out var pill))
            {
                pill.Background = DelayBrush(node.Delay);
            }

            if (_rowButtons.TryGetValue(node.Name, out var button))
            {
                var isCurrent = string.Equals(node.Name, group.Now, StringComparison.Ordinal);
                var isFastest = !string.IsNullOrEmpty(_fastestNode) &&
                                string.Equals(node.Name, _fastestNode, StringComparison.Ordinal);

                button.Background = isCurrent ? _selectedRowBrush : _rowBrush;
                // 最快的那行用绿边勾出来 —— 一眼知道该点哪个
                button.BorderBrush = isFastest
                    ? _fastestEdgeBrush
                    : (isCurrent ? _selectedRowEdgeBrush : _rowEdgeBrush);
            }

            if (_rowTitles.TryGetValue(node.Name, out var title))
            {
                var isCurrent = string.Equals(node.Name, group.Now, StringComparison.Ordinal);
                title.FontWeight = isCurrent
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }

    private Button BuildRow(string groupName, ClashNode node)
    {
        var name = new TextBlock
        {
            // 显示时去掉国旗文字：Windows 渲染不出国旗，只会变成「JP」两个字母看着像乱码
            Text = ClashFormat.CleanNodeKeepRegion(node.Name),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = node.Alive ? _textBrush : _faintBrush,   // 不可用的节点压暗
        };

        var delay = new TextBlock
        {
            Text = ClashFormat.DelayShort(node.Delay),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _pillTextBrush,
        };

        // 延迟做成小胶囊（和 Clash Verge 节点列表一个观感）：定宽，所有行的胶囊右边缘对齐。
        // 加一圈极淡的亮描边 —— 实色底直接贴在深色卡片上像色块，有边才像玻璃标签。
        var delayPill = new Border
        {
            Width = 52,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(0, 2, 0, 2),
            Background = DelayBrush(node.Delay),
            BorderBrush = _pillEdgeBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = delay,
        };

        var glyph = BuildFlagGlyph(node.Name);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 国旗
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 节点名
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 单测
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 延迟

        // 单节点测速：只测这一行，不用整组跑一遍
        var testOne = new Button
        {
            Content = "测",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        testOne.Click += async (_, _) =>
        {
            testOne.IsEnabled = false;
            try { await _plugin.TestSingleNodeAsync(node.Name); }
            finally { testOne.IsEnabled = true; }
        };

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(testOne, 2);
        Grid.SetColumn(delayPill, 3);
        grid.Children.Add(glyph);
        grid.Children.Add(name);
        grid.Children.Add(testOne);
        grid.Children.Add(delayPill);

        var isCurrent = string.Equals(node.Name, _currentNodeName, StringComparison.Ordinal);

        // 玻璃条：圆角 + 1px 描边。原来是无圆角、无边框的纯色块，整片看下来像砖墙
        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(7),
            Background = isCurrent ? _selectedRowBrush : _rowBrush,
            BorderBrush = isCurrent ? _selectedRowEdgeBrush : _rowEdgeBrush,
            BorderThickness = new Thickness(1),
            Tag = node.Name,
        };

        // 悬停提亮一档：共享一支画刷就够，同一时刻只可能悬停在一行上
        button.PointerEntered += (_, _) => button.Background = _rowHoverBrush;
        button.PointerExited += (_, _) =>
        {
            var stillCurrent = string.Equals(button.Tag as string, _currentNodeName, StringComparison.Ordinal);
            button.Background = stillCurrent ? _selectedRowBrush : _rowBrush;
        };

        button.Click += async (_, _) => await _plugin.SelectNodeAsync(groupName, node.Name);

        _delayLabels[node.Name] = delay;
        _delayPills[node.Name] = delayPill;
        _rowButtons[node.Name] = button;
        _rowTitles[node.Name] = name;
        return button;
    }

    /// <summary>
    /// 画延迟趋势。纵轴不从头开始，而是贴着这段数据自己的最小/最大值铺满 ——
    /// 延迟天生上下浮动几十毫秒，从 0 开始画会变成一条直线，看不出任何规律。
    /// </summary>
    private void DrawTrend(int[] values)
    {
        _trendLine.Points.Clear();
        _trendArea.Points.Clear();
        _trendDot.Visibility = Visibility.Collapsed;
        _trendCaption.Text = values.Length < 2 ? "延迟趋势（采集中…）" : $"延迟趋势（{values.Length} 次）";

        if (values.Length < 2) return;

        var width = _trendCanvas.Width;
        var height = _trendCanvas.Height;
        var min = values.Min();
        var max = values.Max();
        var span = Math.Max(1, max - min);
        var step = width / (values.Length - 1);
        var bottom = height - 3;

        for (var i = 0; i < values.Length; i++)
        {
            var x = i * step;
            // 延迟越小越好 → 数值越小画得越高，一眼看出"什么时候变差了"
            var y = 2 + (height - 4) * (1 - (values[i] - min) / (double)span);
            _trendLine.Points.Add(new Windows.Foundation.Point(x, y));
            _trendArea.Points.Add(new Windows.Foundation.Point(x, y));
        }

        // 面积要闭合：沿底线折回来，否则 Polygon 会自动首尾相连成一条斜线
        _trendArea.Points.Add(new Windows.Foundation.Point(width, bottom));
        _trendArea.Points.Add(new Windows.Foundation.Point(0, bottom));

        // 末端亮点：标出"现在"
        var last = _trendLine.Points[_trendLine.Points.Count - 1];
        Canvas.SetLeft(_trendDot, last.X - _trendDot.Width / 2);
        Canvas.SetTop(_trendDot, last.Y - _trendDot.Height / 2);
        _trendDot.Visibility = Visibility.Visible;
    }

    /// <summary>把「当前选中的测速网站」那颗按钮点亮，
    /// 并把每个网站对**当前节点**的延迟填进各自的芯片里。
    /// </summary>
    private void RefreshTargetButtons(ClashSnapshot snapshot)
    {
        foreach (var (name, button) in _targetButtons)
        {
            var selected = string.Equals(name, _plugin.SelectedTargetName, StringComparison.Ordinal);
            button.Background = selected ? _selectedRowBrush : _rowBrush;
        }

        foreach (var site in snapshot.Sites)
        {
            if (!_targetDelays.TryGetValue(site.Url, out var text)) continue;

            if (site.Delay >= 0)
            {
                text.Text = ClashFormat.DelayShort(site.Delay);
                text.Foreground = DelayBrush(site.Delay);
            }
            else
            {
                text.Text = "--";
                text.Foreground = _faintBrush;
            }
        }
    }

    /// <summary>
    /// 把国旗画成填满整格 + 圆角：Border 背景用 ImageBrush(UniformToFill)。
    /// 不能用 Image + Uniform 配白框：各国长宽比不同，一定留白边，看着像糊了一层白框。
    /// </summary>
    private static Border MakeFlagVisual(BitmapImage source, double width, double height) => new()
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(3),
        VerticalAlignment = VerticalAlignment.Center,
        Background = new ImageBrush { ImageSource = source, Stretch = Stretch.UniformToFill },
    };

    /// <summary>卡片左上角的大国旗。只在地区真的变了才重建，别每 2 秒造一堆控件。</summary>
    private void ApplyHeaderFlag(string nodeName)
    {
        // 和岛体同一个规则：优先用**真实出口 IP** 的归属地，查不到才退回节点名；
        // 两条路都要过 DisplayCode（台湾 → 中华人民共和国的国旗）。
        var raw = string.IsNullOrEmpty(_plugin.ExitCountry)
            ? (ClashFormat.DetectRegionCode(nodeName) ?? "")
            : _plugin.ExitCountry;

        var code = ClashFormat.DisplayCode(raw);
        if (string.Equals(code, _headerFlagCode, StringComparison.Ordinal)) return;
        _headerFlagCode = code;

        _headerFlag.BorderBrush = _flagEdgeBrush;

        if (string.IsNullOrEmpty(code))
        {
            _headerFlag.Opacity = 0;
            _headerFlag.Child = null;
            return;
        }

        var flag = _flags.Get(code);
        if (flag is not null)
        {
            _headerFlag.BorderThickness = new Thickness(0);
            _headerFlag.Child = null;
            _headerFlag.Background = new ImageBrush { ImageSource = flag, Stretch = Stretch.UniformToFill };
        }
        else
        {
            _headerFlag.BorderThickness = new Thickness(0);
            _headerFlag.Background = new SolidColorBrush(ClashFormat.RegionColor(code));
            _headerFlag.Child = new TextBlock
            {
                Text = code,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        _headerFlag.Opacity = 1;
    }

    /// <summary>
    /// 节点行前面那面小国旗。固定 19×13，保证所有行的节点名左边缘对齐；
    /// 没有对应国旗图就用「国家色 + 代码」，认不出地区就留一块空位。
    /// </summary>
    private FrameworkElement BuildFlagGlyph(string nodeName)
    {
        const double width = 19;
        const double height = 13;
        var margin = new Thickness(0, 0, 9, 0);

        var code = ClashFormat.DisplayCode(ClashFormat.DetectRegionCode(nodeName) ?? "");
        var flag = string.IsNullOrEmpty(code) ? null : _flags.Get(code);

        if (flag is not null)
        {
            var visual = MakeFlagVisual(flag, width, height);
            visual.Margin = margin;
            return visual;
        }

        if (!string.IsNullOrEmpty(code))
        {
            return new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(ClashFormat.RegionColor(code)),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = margin,
                Child = new TextBlock
                {
                    Text = code,
                    FontSize = 8,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        // 认不出地区（DIRECT / REJECT / 订阅信息条目…）：给一个兜底字形，
        // 不留空白 —— 空着看着像图标没加载出来
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(3),
            Background = _rowBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin,
            Child = new FontIcon
            {
                Glyph = ClashFormat.FallbackGlyph(nodeName),
                FontSize = 10,
                Foreground = _faintBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private void ApplyThemeColors()
    {
        // 文字对比度往 Apple 那套靠：主文字纯色，次要文字也保持高对比。
        // 之前的 205 / 145 在玻璃上会发灰、看着"不清晰"，这是最直接的元凶。
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(238);
        _faintBrush.Color = Neutral(200);
        _flagEdgeBrush.Color = Neutral(70);

        // 节点行：平时几乎看不出底，鼠标移上去提亮一档 —— 有反馈才像能点。
        // 数值比玻璃提亮前调高了一档：底变亮之后，原来的透明度就看不见了。
        _rowBrush.Color = Neutral(18);
        _rowHoverBrush.Color = Neutral(44);
        _rowEdgeBrush.Color = Neutral(36);

        _selectedRowBrush.Color = Neutral(46);
        _selectedRowEdgeBrush.Color = Neutral(80);

        // 延迟胶囊的描边：深色底挂亮边、浅色底勾暗边，否则白色卡片上完全看不见
        _pillEdgeBrush.Color = CardIsLight
            ? Windows.UI.Color.FromArgb(60, 0, 0, 0)
            : Windows.UI.Color.FromArgb(64, 255, 255, 255);

        // 分隔线：中段可见，两端淡出
        _dividerStopA.Color = Neutral(0);
        _dividerStopB.Color = Neutral(54);
        _dividerStopC.Color = Neutral(0);

        // 顶沿高光：中段最亮，两端收掉
        _glossStopA.Color = Neutral(0);
        _glossStopB.Color = Neutral(96);
        _glossStopC.Color = Neutral(0);

        // 曲线下方的面积：贴着线一点蓝，往下很快化掉
        _areaStopTop.Color = Windows.UI.Color.FromArgb(104, 0x4C, 0xC2, 0xFF);
        _areaStopBottom.Color = Windows.UI.Color.FromArgb(0, 0x4C, 0xC2, 0xFF);

        // 卡片外观由用户选的风格决定（见 ApplyCardAppearance）
        ApplyCardAppearance();
    }

    // ── 卡片外观（用户可选：跟随主题 / 深色 / 白色 / 液态玻璃 / 自定义）────

    /// <summary>
    /// 卡片当前算浅色还是深色。**统一由插件的 AppearanceIsLight 决定**，
    /// 这样岛体和卡片永远是同一套明暗，不会一个白一个黑。
    /// </summary>
    private bool CardIsLight => _plugin.AppearanceIsLight;

    /// <summary>弹出「谁在用流量」的每一行：进程名 / 域名 + 流量 + 断开按钮。</summary>
    private void SyncConnections(ClashSnapshot snapshot)
    {
        var items = snapshot.TopConnections ?? new List<ClashConnection>();

        _connEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _connList.Children.Clear();
        foreach (var connection in items)
        {
            _connList.Children.Add(MakeConnectionRow(connection));
        }
    }

    private Border MakeConnectionRow(ClashConnection connection)
    {
        // 有进程名就用进程名（"是哪个软件在吃带宽"），没有就退回域名
        var title = "未知";
        if (!string.IsNullOrWhiteSpace(connection.Process))
        {
            try { title = System.IO.Path.GetFileName(connection.Process); }
            catch (Exception) { title = connection.Process; }
        }
        else if (!string.IsNullOrWhiteSpace(connection.Host))
        {
            title = connection.Host;
        }

        var subtitle = string.IsNullOrWhiteSpace(connection.Host) ||
                       string.Equals(connection.Host, title, StringComparison.OrdinalIgnoreCase)
            ? ClashFormat.Bytes(connection.Total)
            : $"{connection.Host} · {ClashFormat.Bytes(connection.Total)}";

        var texts = new Grid();
        texts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        texts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textBrush,
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _faintBrush,
        };

        Grid.SetColumn(nameText, 0);
        Grid.SetColumn(subText, 1);
        texts.Children.Add(nameText);
        texts.Children.Add(subText);

        var cut = new Button
        {
            Content = "断开",
            FontSize = 11,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cut.Click += async (_, _) => await _plugin.CloseConnectionAsync(connection.Id);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 0);
        Grid.SetColumn(cut, 1);
        row.Children.Add(texts);
        row.Children.Add(cut);

        return new Border
        {
            Background = _rowBrush,
            BorderBrush = _rowEdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Child = row,
        };
    }

    /// <summary>按当前分组里出现过的地区重建筛选条。只有一个地区时不显示（没意义）。</summary>
    private void BuildRegionChips(ClashGroup group, IReadOnlyList<ClashNode> pool)
    {
        _regionRow.Children.Clear();

        var codes = pool
            .Select(n => ClashFormat.DetectRegionCode(n.Name))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        if (codes.Count <= 1) return;

        _regionRow.Children.Add(MakeRegionChip("全部", ""));
        foreach (var code in codes)
        {
            _regionRow.Children.Add(MakeRegionChip(ClashFormat.DisplayCode(code), code));
        }
    }

    private Button MakeRegionChip(string label, string code)
    {
        var selected = string.Equals(code, _regionFilter, StringComparison.Ordinal);
        var button = new Button
        {
            Content = label,
            FontSize = 12,
            Padding = new Thickness(10, 3, 10, 3),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = selected ? _selectedRowEdgeBrush : _rowEdgeBrush,
            Background = selected ? _selectedRowBrush : _rowBrush,
        };

        button.Click += (_, _) =>
        {
            if (string.Equals(_regionFilter, code, StringComparison.Ordinal)) return;
            _regionFilter = code;
            _builtSignature = "";   // 筛选变了 → 重建节点列表
            _builtGroup = "";       // 同时重建筛选条本身，更新选中态
        };

        return button;
    }

    /// <summary>按当前外观铺整块玻璃。改完要调 RefreshAppearance() 才会重画。</summary>
    private void ApplyCardAppearance()
    {
        PaintGlass(_plugin.AppearanceBaseColor, _plugin.CardOpacity / 100.0);

        // 强调色：趋势线和它下面的面积一起换色
        var accent = _plugin.AccentColor ?? Windows.UI.Color.FromArgb(255, 0x5C, 0xC8, 0xFF);
        _trendBrush.Color = Windows.UI.Color.FromArgb(235, accent.R, accent.G, accent.B);
        _areaStopTop.Color = Windows.UI.Color.FromArgb(104, accent.R, accent.G, accent.B);
        _areaStopBottom.Color = Windows.UI.Color.FromArgb(0, accent.R, accent.G, accent.B);
        _trendDotBrush.Color = Windows.UI.Color.FromArgb(255, accent.R, accent.G, accent.B);
    }

    /// <summary>把一块玻璃画出来：主体上亮下暗、边两端挂光、再一道斜向反光。</summary>
    private void PaintGlass(Windows.UI.Color baseColor, double opacity)
    {
        byte Alpha(double k) => (byte)Math.Clamp(opacity * 255 * k, 0, 255);

        _glassStopTop.Color = WithAlpha(baseColor, Alpha(1.00));
        _glassStopMid.Color = WithAlpha(baseColor, Alpha(0.88));
        _glassStopBottom.Color = WithAlpha(baseColor, Alpha(0.76));

        // 边：浅色卡片用暗边勾轮廓，深色卡片用亮边挂光；中间都收掉。
        var edge = CardIsLight
            ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255);

        _glassEdgeTop.Color = WithAlpha(edge, CardIsLight ? (byte)52 : (byte)170);
        _glassEdgeMid.Color = WithAlpha(edge, CardIsLight ? (byte)16 : (byte)34);
        _glassEdgeBottom.Color = WithAlpha(edge, CardIsLight ? (byte)34 : (byte)100);
        _glassInnerBrush.Color = WithAlpha(edge, CardIsLight ? (byte)14 : (byte)30);

        var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        _sheenStart.Color = WithAlpha(white, CardIsLight ? (byte)80 : (byte)56);
        _sheenEnd.Color = WithAlpha(white, 0);
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color c, byte a) =>
        Windows.UI.Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>设置页改了卡片外观后，立刻重刷配色。</summary>
    public void RefreshAppearance() => ApplyThemeColors();

    /// <summary>中性色：**看卡片的明暗**，不是看岛体主题 —— 白卡片上必须用黑字。</summary>
    private Windows.UI.Color Neutral(byte alpha) => CardIsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
}
