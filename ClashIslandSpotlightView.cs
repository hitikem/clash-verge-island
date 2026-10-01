using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    private readonly ClashVergeIslandPlugin _plugin;
    private readonly IIslandTheme _theme;

    private readonly TextBlock _status = new() { FontSize = 13 };
    private readonly TextBlock _modeLine = new() { FontSize = 13 };
    private readonly TextBlock _speedLine = new() { FontSize = 13 };
    private readonly ComboBox _groupBox = new() { MinWidth = 200 };
    private readonly StackPanel _nodeList = new() { Spacing = 2 };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _dividerBrush = new();
    private readonly SolidColorBrush _rowBrush = new();
    private readonly SolidColorBrush _selectedRowBrush = new();
    private readonly SolidColorBrush _flagEdgeBrush = new();

    /// <summary>
    /// 延迟颜色用共享画刷。原来是每次刷新都给每个节点 new 一个画刷
    /// （300 个节点 × 每 2 秒一次），纯属浪费；延迟只有四档，缓存四支就够了。
    /// </summary>
    private readonly SolidColorBrush _delayUnknownBrush = new(ClashFormat.DelayColor(-1));
    private readonly SolidColorBrush _delayGoodBrush = new(ClashFormat.DelayColor(50));
    private readonly SolidColorBrush _delayWarnBrush = new(ClashFormat.DelayColor(200));
    private readonly SolidColorBrush _delayBadBrush = new(ClashFormat.DelayColor(400));

    private SolidColorBrush DelayBrush(int ms) => ms switch
    {
        < 0 => _delayUnknownBrush,
        < 150 => _delayGoodBrush,
        < 300 => _delayWarnBrush,
        _ => _delayBadBrush,
    };

    private readonly Dictionary<string, TextBlock> _delayLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _rowButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _rowTitles = new(StringComparer.Ordinal);

    private readonly List<Button> _modeButtons = new();

    private readonly FlagLibrary _flags;
    private readonly TextBox _search = new() { PlaceholderText = "搜索节点…", MinWidth = 180 };

    /// <summary>卡片左上角的大国旗，跟着当前节点变。</summary>
    private readonly Border _headerFlag = new()
    {
        Width = 28,
        Height = 19,
        CornerRadius = new CornerRadius(3),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0,
    };

    private string _headerFlagCode = "\u0000";
    private string _filter = "";

    private string _builtGroup = "";
    private string _builtSignature = "";
    private string _groupSignature = "";
    private bool _loading = true;

    public ClashIslandSpotlightView(
        PluginManifest manifest, IIslandTheme theme, ClashVergeIslandPlugin plugin, FlagLibrary flags)
    {
        _plugin = plugin;
        _theme = theme;
        _flags = flags;

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
        _modeLine.Foreground = _mutedBrush;
        _speedLine.Foreground = _mutedBrush;
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

        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await _plugin.RefreshNowAsync("聚光卡刷新");

        var test = new Button { Content = "测试本组延迟" };
        test.Click += async (_, _) =>
        {
            var group = _groupBox.SelectedItem as string;
            if (!string.IsNullOrEmpty(group)) await _plugin.TestGroupAsync(group);
        };
        groupRow.Children.Add(refresh);
        groupRow.Children.Add(test);
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

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 240,
            Content = _nodeList,
        };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(_headerFlag);
        titleRow.Children.Add(title);

        var root = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(32, 28, 32, 28),
        };
        root.Children.Add(titleRow);
        root.Children.Add(_status);
        root.Children.Add(new Border { Height = 1, Background = _dividerBrush });
        root.Children.Add(new StackPanel { Spacing = 2, Children = { _modeLine, _speedLine } });
        root.Children.Add(modeRow);
        root.Children.Add(groupRow);
        root.Children.Add(scroller);
        root.Children.Add(_hint);

        Content = root;

        _loading = false;
    }

    private Button MakeModeButton(string mode, string label)
    {
        var button = new Button { Content = label, Tag = mode };
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
            _modeLine.Text = snapshot.Error ?? "";
            _speedLine.Text = "";
            _hint.Text = "打开方法：Clash Verge → 设置 → 「Clash 设置」→ External → " +
                         "打开「Enable External Controller」，地址 127.0.0.1:9097，并设一个 Core Secret，" +
                         "然后把端口和密码填到本插件的设置页。";
            _nodeList.Children.Clear();
            _delayLabels.Clear();
            _rowButtons.Clear();
            _rowTitles.Clear();
            _builtGroup = "";
            _builtSignature = "";
            return;
        }

        _status.Text = $"Mihomo {snapshot.Version} · 当前节点 " +
                       $"{ClashFormat.CleanNodeKeepRegion(snapshot.ActiveNode)} · " +
                       $"延迟 {ClashFormat.Delay(snapshot.ActiveDelay)}";
        ApplyHeaderFlag(snapshot.ActiveNode);
        _modeLine.Text = $"模式：{ClashFormat.Mode(snapshot.Mode)}    分组：{snapshot.ActiveGroup}";
        _speedLine.Text = $"实时网速：↑ {ClashFormat.Speed(snapshot.UpPerSec)}    ↓ {ClashFormat.Speed(snapshot.DownPerSec)}";
        _hint.Text = "点任意节点即可切换；绿色为延迟低，红色为延迟高或不可用。";

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

        var filtered = string.IsNullOrEmpty(_filter)
            ? group.Nodes
            : group.Nodes.Where(n => n.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var visible = filtered.Take(MaxNodes).ToList();
        var signature = group.Name + "#" + _filter + "#" + string.Join(",", visible.Select(n => n.Name));

        if (group.Name != _builtGroup || signature != _builtSignature)
        {
            _builtGroup = group.Name;
            _builtSignature = signature;
            _delayLabels.Clear();
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

        // 延迟和「当前选中」会变，就地更新，不重建控件树
        foreach (var node in visible)
        {
            if (_delayLabels.TryGetValue(node.Name, out var label))
            {
                label.Text = ClashFormat.Delay(node.Delay);
                label.Foreground = DelayBrush(node.Delay);
            }

            if (_rowButtons.TryGetValue(node.Name, out var button))
            {
                var isCurrent = string.Equals(node.Name, group.Now, StringComparison.Ordinal);
                button.Background = isCurrent ? _selectedRowBrush : _rowBrush;
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
            Text = ClashFormat.Delay(node.Delay),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = DelayBrush(node.Delay),
        };

        var glyph = BuildFlagGlyph(node.Name);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 国旗
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 节点名
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // 延迟
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(delay, 2);
        grid.Children.Add(glyph);
        grid.Children.Add(name);
        grid.Children.Add(delay);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 6, 10, 6),
            Background = _rowBrush,
            BorderThickness = new Thickness(0),
            Tag = node.Name,
        };

        button.Click += async (_, _) => await _plugin.SelectNodeAsync(groupName, node.Name);

        _delayLabels[node.Name] = delay;
        _rowButtons[node.Name] = button;
        _rowTitles[node.Name] = name;
        return button;
    }

    /// <summary>卡片左上角的大国旗。只在地区真的变了才重建，别每 2 秒造一堆控件。</summary>
    private void ApplyHeaderFlag(string nodeName)
    {
        var code = ClashFormat.DetectRegionCode(nodeName) ?? "";
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
            _headerFlag.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            _headerFlag.Child = new Image { Source = flag, Stretch = Stretch.Uniform };
        }
        else
        {
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

        var code = ClashFormat.DetectRegionCode(nodeName);
        var flag = code is null ? null : _flags.Get(code);

        if (flag is not null)
        {
            return new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                BorderBrush = _flagEdgeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = margin,
                Child = new Image { Source = flag, Stretch = Stretch.Uniform },
            };
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

        return new Border { Width = width, Height = height, Margin = margin };
    }

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(205);
        _faintBrush.Color = Neutral(145);
        _dividerBrush.Color = Neutral(28);
        _rowBrush.Color = Neutral(14);
        _flagEdgeBrush.Color = Neutral(70);
        _selectedRowBrush.Color = _plugin.ThemeIsLight
            ? Windows.UI.Color.FromArgb(46, 0, 0, 0)
            : Windows.UI.Color.FromArgb(46, 255, 255, 255);
    }

    private Windows.UI.Color Neutral(byte alpha) => _plugin.ThemeIsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
}
