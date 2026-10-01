namespace ClashVergeIsland;

/// <summary>
/// Clash Verge 的「测速网站」列表。
///
/// Clash Verge 界面里测延迟时可以选网站（默认是 Apple / GitHub / Google / YouTube），
/// 配置就写在它的 verge.yaml 的 test_list 段里。这里直接读那份配置，
/// 这样插件里的网站和 Clash Verge 界面里看到的**完全一致**（用户自己加过也能读到）；
/// 读不到就退回和出厂一致的四个。
/// </summary>
public static class ClashTestTargets
{
    public sealed record Target(string Name, string Url);

    /// <summary>出厂默认，和 Clash Verge 一致。</summary>
    private static readonly Target[] Defaults =
    [
        new("Apple", "https://www.apple.com"),
        new("GitHub", "https://www.github.com"),
        new("Google", "https://www.google.com"),
        new("YouTube", "https://www.youtube.com"),
    ];

    public static List<Target> Load()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "io.github.clash-verge-rev.clash-verge-rev",
                "verge.yaml");

            if (!System.IO.File.Exists(path)) return [.. Defaults];

            var loaded = ParseVergeYaml(System.IO.File.ReadAllLines(path));
            return loaded.Count > 0 ? loaded : [.. Defaults];
        }
        catch (Exception)
        {
            return [.. Defaults];
        }
    }

    /// <summary>
    /// 只解析 test_list: 这一段里的 name / url。
    /// 故意不引入 YAML 库：结构很固定，逐行扫足够，也少一个依赖。
    /// </summary>
    private static List<Target> ParseVergeYaml(string[] lines)
    {
        var result = new List<Target>();
        var inList = false;
        string? pendingName = null;

        foreach (var raw in lines)
        {
            if (!inList)
            {
                if (raw.StartsWith("test_list:", StringComparison.Ordinal)) inList = true;
                continue;
            }

            // 离开 test_list 段（遇到顶格的另一个键）
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0])) break;

            var line = raw.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal)) line = line[2..].Trim();

            if (line.StartsWith("name:", StringComparison.Ordinal))
            {
                pendingName = Unquote(line[5..].Trim());
            }
            else if (line.StartsWith("url:", StringComparison.Ordinal))
            {
                var url = Unquote(line[4..].Trim());
                if (!string.IsNullOrWhiteSpace(url))
                {
                    result.Add(new Target(pendingName ?? url, url));
                }
                pendingName = null;
            }
        }

        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
