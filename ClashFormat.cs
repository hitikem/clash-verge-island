using System.Text;

namespace ClashVergeIsland;

/// <summary>把内核返回的英文值翻译成中文 / 格式化，供岛视图和聚光卡共用。</summary>
internal static class ClashFormat
{
    public static string Mode(string mode) => mode switch
    {
        "rule" => "规则",
        "global" => "全局",
        "direct" => "直连",
        _ => string.IsNullOrEmpty(mode) ? "未知" : mode,
    };

    /// <summary>把字节/秒格式化成 KB/s、MB/s。</summary>
    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond < 0) return "--";
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:0.0} KB/s";
        return $"{bytesPerSecond / (1024 * 1024):0.00} MB/s";
    }

    /// <summary>把累计字节数格式化成 MB、GB（不带 /s）。</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "--";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    /// <summary>模式胶囊的底色：三种模式用三种颜色，一眼能分辨。</summary>
    public static Windows.UI.Color ModeColor(string mode) => mode switch
    {
        "rule" => Windows.UI.Color.FromArgb(255, 0x3B, 0x82, 0xF6),     // 蓝
        "global" => Windows.UI.Color.FromArgb(255, 0x8B, 0x5C, 0xF6),   // 紫
        "direct" => Windows.UI.Color.FromArgb(255, 0x10, 0xB9, 0x81),   // 绿
        _ => Windows.UI.Color.FromArgb(255, 0x5C, 0x6B, 0x7A),
    };

    /// <summary>延迟文字；没有数据时是「--」。</summary>
    public static string Delay(int ms) => ms >= 0 ? $"{ms} ms" : "--";

    /// <summary>只要数字（延迟胶囊里用，和 Clash Verge 的显示一致）。</summary>
    public static string DelayShort(int ms) => ms >= 0 ? ms.ToString() : "--";

    /// <summary>延迟配色：绿 / 黄 / 红。这两套色在浅色和深色岛体上都读得清。</summary>
    public static Windows.UI.Color DelayColor(int ms) => ms switch
    {
        < 0 => Windows.UI.Color.FromArgb(160, 128, 128, 128),
        < 150 => Windows.UI.Color.FromArgb(255, 0x3F, 0xB9, 0x50),
        < 300 => Windows.UI.Color.FromArgb(255, 0xD2, 0x99, 0x22),
        _ => Windows.UI.Color.FromArgb(255, 0xF8, 0x51, 0x49),
    };

    /// <summary>
    /// 节点名的精简版，给小岛用。
    ///
    /// 机场节点名常写成「🇯🇵日本专线04|BGP|流媒体」，竖线后面多半是标签不是身份，
    /// 只保留竖线前那段，「04」这种关键区分信息才不会被截掉；国旗也一并去掉
    /// （Windows 渲染不出国旗，只会显示成「JP」两个字母白占宽度）。
    /// </summary>
    public static string ShortNode(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        var bar = name.IndexOf('|');
        var head = (bar > 0 ? name[..bar] : name).Trim();
        head = StripFlags(head).Trim();

        return head.Length <= 14 ? head : head[..14] + "…";
    }

    /// <summary>
    /// 认不出地区时的兜底图标。
    ///
    /// 节点列表里有一批"不是真节点"的条目（DIRECT / REJECT / 剩余流量 / 套餐到期…），
    /// 它们没有国旗。如果什么都不画，左边就是一排空白，看着像图标没加载出来。
    /// 这里按含义给一个字形，保证图标这一列永远是满的。
    /// </summary>
    public static string FallbackGlyph(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName)) return "\uE774";
        if (nodeName.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return "\uE72A";   // 直连箭头
        if (nodeName.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase)) return "\uE711"; // 禁止

        // 订阅附带的信息条目（流量、到期、重置…）
        if (nodeName.Contains("流量", StringComparison.Ordinal) ||
            nodeName.Contains("到期", StringComparison.Ordinal) ||
            nodeName.Contains("重置", StringComparison.Ordinal) ||
            nodeName.Contains("官网", StringComparison.Ordinal) ||
            nodeName.Contains("订阅", StringComparison.Ordinal) ||
            nodeName.Contains("套餐", StringComparison.Ordinal) ||
            nodeName.Contains("剩余", StringComparison.Ordinal))
        {
            return "\uE946";   // 信息
        }

        return "\uE968";       // 普通节点：服务器
    }

    /// <summary>
    /// 是不是订阅附带的信息条目（不是能连的节点）。
    /// 这类条目混在节点列表里既没用又碍眼，默认把它们藏掉。
    /// </summary>
    public static bool IsInfoEntry(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName)) return false;

        return nodeName.Contains("剩余流量", StringComparison.Ordinal) ||
               nodeName.Contains("距离下次重置", StringComparison.Ordinal) ||
               nodeName.Contains("套餐到期", StringComparison.Ordinal) ||
               nodeName.Contains("到期时间", StringComparison.Ordinal) ||
               nodeName.Contains("剩余", StringComparison.Ordinal) ||
               nodeName.Contains("重置", StringComparison.Ordinal) ||
               nodeName.Contains("官网", StringComparison.Ordinal) ||
               nodeName.Contains("订阅", StringComparison.Ordinal);
    }

    /// <summary>节点的完整名字，只去掉渲染不出来的国旗（展开态用）。</summary>
    public static string CleanNode(string name) =>
        string.IsNullOrEmpty(name) ? "" : StripFlags(name).Trim();

    /// <summary>
    /// 保留地区名、但去掉多余地区代码的显示名（聚光卡头部用）。
    /// 「🇸🇬sg新加坡高速05|BGP」→「新加坡高速05|BGP」
    /// </summary>
    public static string CleanNodeKeepRegion(string name)
    {
        var cleaned = CleanNode(name);
        var code = DetectRegionCode(name);
        if (string.IsNullOrEmpty(code)) return cleaned;

        return StripCodeToken(cleaned, code).Trim();
    }

    /// <summary>
    /// 删掉独立的地区代码（如 "sg"、"hk"）。
    /// 只删前后都不是字母数字的那些，避免把正常单词里的字母组合误伤掉。
    /// </summary>
    private static string StripCodeToken(string text, string code) =>
        string.IsNullOrEmpty(code)
            ? text
            : System.Text.RegularExpressions.Regex.Replace(
                text,
                $@"(?i)(?<![a-z0-9]){System.Text.RegularExpressions.Regex.Escape(code)}(?![a-z0-9])",
                "");

    /// <summary>
    /// 把名字里的地区词去掉。
    ///
    /// 岛上已经显示了国旗，「🇸🇬新加坡高速05」里的「新加坡」就是重复信息，
    /// 而小岛宽度很紧张（宿主只给约 180px）。去掉后变成「高速05」，又短又不丢信息。
    /// 全去光了就退回原名（比如节点就叫「新加坡」）。
    /// </summary>
    public static string RemoveRegionWords(string name, string code)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(code)) return name;

        var result = name;
        foreach (var (alias, aliasCode) in RegionAliases)
        {
            if (!string.Equals(aliasCode, code, StringComparison.Ordinal)) continue;
            result = result.Replace(alias, "", StringComparison.OrdinalIgnoreCase);
        }

        result = StripCodeToken(result, code);
        result = result.Trim(' ', '-', '_', '|', '·', '.', '(', ')', '[', ']', '、');
        while (result.Contains("  ", StringComparison.Ordinal)) result = result.Replace("  ", " ");
        while (result.Contains("--", StringComparison.Ordinal)) result = result.Replace("--", "-");
        while (result.Contains("||", StringComparison.Ordinal)) result = result.Replace("||", "|");

        return string.IsNullOrWhiteSpace(result) ? name : result.Trim();
    }

    // ---- 地区识别（给图标上的小徽章用）----

    /// <summary>
    /// 台湾节点是否按一个中国原则显示为中华人民共和国国旗（默认是）。
    ///
    /// 注意分工：<see cref="DetectRegionCode"/> 永远返回识别到的原始代码 TW
    /// （名字里的「台湾」两个字要按这个去重），只在**显示**这一步用
    /// <see cref="DisplayCode"/> 映射成 CN，从而取到五星红旗。
    /// </summary>
    public static bool TaiwanAsChina { get; set; } = true;

    /// <summary>把识别出的地区代码映射成"显示用"的代码：台湾 → 中国。</summary>
    public static string DisplayCode(string code) =>
        TaiwanAsChina && string.Equals(code, "TW", StringComparison.Ordinal) ? "CN" : code;

    /// <summary>
    /// 从节点名里认出地区，返回两个大写字母的国家/地区代码（如 JP / HK / US）。
    /// 认不出来返回 null —— 那时视图会退回显示地球图标。
    ///
    /// 先找国旗 emoji（最准，很多机场都用），找不到再按名字里的地名关键词匹配。
    /// </summary>
    public static string? DetectRegionCode(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName)) return null;

        var fromFlag = CodeFromFlag(nodeName);
        if (fromFlag is not null) return fromFlag;

        var lower = nodeName.ToLowerInvariant();
        foreach (var (alias, code) in RegionAliases)
        {
            if (lower.Contains(alias, StringComparison.Ordinal)) return code;
        }

        return null;
    }

    /// <summary>
    /// 地区徽章的底色。用「一眼能区分」的饱和色，而不是严格照搬国旗色 ——
    /// 因为一堆国家都是红白蓝，照搬反而分不清。同一地区永远是同一个色。
    /// </summary>
    public static Windows.UI.Color RegionColor(string code) => code switch
    {
        "CN" => C(0xDE, 0x29, 0x10),   // 中国红
        "HK" => C(0xD9, 0x3B, 0x3B),   // 红
        "TW" => C(0x3B, 0x5B, 0xDB),   // 靛蓝
        "JP" => C(0xC2, 0x25, 0x5C),   // 洋红
        "SG" => C(0xE8, 0x59, 0x0C),   // 橙
        "US" => C(0x70, 0x48, 0xE8),   // 紫
        "KR" => C(0x19, 0x71, 0xC2),   // 蓝
        "GB" => C(0x0C, 0xA6, 0x78),   // 青绿
        "DE" => C(0xA1, 0x62, 0x07),   // 古铜
        "FR" => C(0xAE, 0x3E, 0xC9),   // 品红
        "CA" => C(0xE0, 0x31, 0x31),   // 亮红
        "AU" => C(0x2F, 0x9E, 0x44),   // 绿
        "RU" => C(0x36, 0x4F, 0xC7),   // 深蓝
        "IN" => C(0xD9, 0x48, 0x0F),   // 深橙
        "TR" => C(0xC9, 0x2A, 0x2A),   // 暗红
        "NL" => C(0x10, 0x98, 0xAD),   // 青
        "SE" => C(0x0B, 0x72, 0x85),   // 深青
        "CH" => C(0x86, 0x2E, 0x9C),   // 深紫
        "BR" => C(0x2B, 0x8A, 0x3E),   // 深绿
        "IT" => C(0x1C, 0x7E, 0xD6),   // 天蓝
        "ES" => C(0xC2, 0x25, 0x5C),   // 洋红
        _ => C(0x5C, 0x6B, 0x7A),      // 未知地区：中性灰蓝
    };

    private static Windows.UI.Color C(byte r, byte g, byte b) =>
        Windows.UI.Color.FromArgb(255, r, g, b);

    /// <summary>去掉国旗（区域指示符）、变体选择符和零宽连接符。</summary>
    private static string StripFlags(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var codePoint = char.ConvertToUtf32(c, text[i + 1]);
                if (codePoint is >= 0x1F1E6 and <= 0x1F1FF)   // 国旗
                {
                    i++;
                    continue;
                }
            }

            if (c is '\uFE0F' or '\u200D') continue;          // 变体选择符 / 零宽连接符
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>把国旗 emoji（两个区域指示符）翻译成 "JP" 这样的代码。</summary>
    private static string? CodeFromFlag(string text)
    {
        for (var i = 0; i + 3 < text.Length; i++)
        {
            if (!char.IsHighSurrogate(text[i]) || !char.IsLowSurrogate(text[i + 1])) continue;
            if (!char.IsHighSurrogate(text[i + 2]) || !char.IsLowSurrogate(text[i + 3])) continue;

            var first = char.ConvertToUtf32(text[i], text[i + 1]);
            var second = char.ConvertToUtf32(text[i + 2], text[i + 3]);

            if (first is >= 0x1F1E6 and <= 0x1F1FF && second is >= 0x1F1E6 and <= 0x1F1FF)
            {
                return new string(
                    [(char)('A' + first - 0x1F1E6), (char)('A' + second - 0x1F1E6)]);
            }
        }

        return null;
    }

    /// <summary>地名关键词表。长词优先，避免「印度尼西亚」被「印度」抢先匹配。</summary>
    private static readonly (string Alias, string Code)[] RegionAliases = BuildAliases();

    private static (string Alias, string Code)[] BuildAliases()
    {
        (string, string)[] raw =
        [
            ("香港", "HK"), ("hongkong", "HK"), ("hong kong", "HK"),
            ("台湾", "TW"), ("台北", "TW"), ("taiwan", "TW"),
            ("日本", "JP"), ("东京", "JP"), ("大阪", "JP"), ("japan", "JP"), ("tokyo", "JP"),
            ("新加坡", "SG"), ("狮城", "SG"), ("singapore", "SG"),
            ("美国", "US"), ("洛杉矶", "US"), ("圣何塞", "US"), ("西雅图", "US"), ("纽约", "US"),
            ("硅谷", "US"), ("united states", "US"), ("usa", "US"), ("los angeles", "US"),
            ("韩国", "KR"), ("首尔", "KR"), ("korea", "KR"), ("seoul", "KR"),
            ("英国", "GB"), ("伦敦", "GB"), ("united kingdom", "GB"), ("london", "GB"),
            ("德国", "DE"), ("法兰克福", "DE"), ("germany", "DE"),
            ("法国", "FR"), ("巴黎", "FR"), ("france", "FR"),
            ("加拿大", "CA"), ("canada", "CA"),
            ("澳大利亚", "AU"), ("澳洲", "AU"), ("悉尼", "AU"), ("australia", "AU"),
            ("俄罗斯", "RU"), ("莫斯科", "RU"), ("russia", "RU"),
            ("印度尼西亚", "ID"), ("印尼", "ID"), ("indonesia", "ID"),
            ("印度", "IN"), ("india", "IN"),
            ("土耳其", "TR"), ("turkey", "TR"),
            ("马来西亚", "MY"), ("吉隆坡", "MY"), ("malaysia", "MY"),
            ("泰国", "TH"), ("曼谷", "TH"), ("thailand", "TH"),
            ("越南", "VN"), ("vietnam", "VN"),
            ("菲律宾", "PH"), ("philippines", "PH"),
            ("荷兰", "NL"), ("netherlands", "NL"),
            ("瑞典", "SE"), ("sweden", "SE"),
            ("瑞士", "CH"), ("switzerland", "CH"),
            ("阿根廷", "AR"), ("argentina", "AR"),
            ("巴西", "BR"), ("brazil", "BR"),
            ("南非", "ZA"), ("south africa", "ZA"),
            ("乌克兰", "UA"), ("ukraine", "UA"),
            ("波兰", "PL"), ("poland", "PL"),
            ("爱尔兰", "IE"), ("ireland", "IE"),
            ("意大利", "IT"), ("italy", "IT"),
            ("西班牙", "ES"), ("spain", "ES"),
            ("芬兰", "FI"), ("finland", "FI"),
            ("挪威", "NO"), ("norway", "NO"),
            ("丹麦", "DK"), ("denmark", "DK"),
            ("墨西哥", "MX"), ("mexico", "MX"),
            ("以色列", "IL"), ("israel", "IL"),
            ("阿联酋", "AE"), ("迪拜", "AE"),
            ("澳门", "MO"), ("蒙古", "MN"),
            ("柬埔寨", "KH"), ("缅甸", "MM"), ("尼泊尔", "NP"), ("巴基斯坦", "PK"),
            ("哈萨克斯坦", "KZ"), ("白俄罗斯", "BY"),
            ("奥地利", "AT"), ("比利时", "BE"), ("捷克", "CZ"), ("匈牙利", "HU"),
            ("罗马尼亚", "RO"), ("希腊", "GR"), ("葡萄牙", "PT"), ("冰岛", "IS"),
            ("卢森堡", "LU"), ("保加利亚", "BG"), ("塞尔维亚", "RS"), ("克罗地亚", "HR"),
            ("斯洛伐克", "SK"), ("斯洛文尼亚", "SI"), ("立陶宛", "LT"), ("拉脱维亚", "LV"),
            ("爱沙尼亚", "EE"), ("新西兰", "NZ"), ("智利", "CL"), ("哥伦比亚", "CO"),
            ("秘鲁", "PE"), ("埃及", "EG"), ("摩洛哥", "MA"), ("肯尼亚", "KE"),
            ("尼日利亚", "NG"), ("沙特", "SA"), ("卡塔尔", "QA"), ("科威特", "KW"),
            ("伊拉克", "IQ"), ("伊朗", "IR"), ("约旦", "JO"), ("黎巴嫩", "LB"),
            ("亚美尼亚", "AM"), ("阿塞拜疆", "AZ"), ("格鲁吉亚", "GE"), ("乌兹别克斯坦", "UZ"),
            ("孟加拉", "BD"), ("斯里兰卡", "LK"),
        ];

        return raw
            .OrderByDescending(x => x.Item1.Length)
            .Select(x => (x.Item1, x.Item2))
            .ToArray();
    }
}
