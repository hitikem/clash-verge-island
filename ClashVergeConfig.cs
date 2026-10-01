namespace ClashVergeIsland;

/// <summary>
/// 从 Clash Verge 自己的配置里读出「外部控制」的地址和密码。
///
/// 为什么要这样：Clash Verge 从 v2.4.0 起默认**关闭**外部控制，
/// 用户要去它设置里手动打开并设一个 Core Secret。如果还要求用户把这个密码
/// 手抄到插件设置页，几乎没人愿意配。
///
/// 好在 Clash Verge 会把它正在使用的配置写到
/// %APPDATA%\io.github.clash-verge-rev.clash-verge-rev\clash-verge.yaml
/// （运行时配置，注意不是 config.yaml），里面就有生效的
/// external-controller 和 secret —— 直接读它就能自动配好。
/// </summary>
internal static class ClashVergeConfig
{
    internal sealed record Detected(string Host, int Port, string Secret);

    /// <summary>Clash Verge 的配置文件目录（各版本的固定位置）。</summary>
    private static string ConfigDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "io.github.clash-verge-rev.clash-verge-rev");

    /// <summary>
    /// 读取当前生效的地址与密码。读不到、或外部控制是关着的（值为空），返回 null。
    /// </summary>
    public static Detected? TryRead()
    {
        try
        {
            var path = System.IO.Path.Combine(ConfigDirectory, "clash-verge.yaml");
            if (!System.IO.File.Exists(path)) return null;

            return Parse(System.IO.File.ReadAllLines(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Detected? Parse(string[] lines)
    {
        string? controller = null;
        var secret = "";

        foreach (var raw in lines)
        {
            // 只看顶格的键（缩进的是子项，不要）
            if (raw.Length == 0 || char.IsWhiteSpace(raw[0])) continue;

            if (raw.StartsWith("external-controller:", StringComparison.Ordinal))
            {
                controller = Unquote(raw["external-controller:".Length..].Trim());
            }
            else if (raw.StartsWith("secret:", StringComparison.Ordinal))
            {
                secret = Unquote(raw["secret:".Length..].Trim());
            }
        }

        if (string.IsNullOrWhiteSpace(controller)) return null;   // 关着的时候是空串

        var host = controller;
        var port = 9097;
        var colon = controller.LastIndexOf(':');
        if (colon > 0)
        {
            host = controller[..colon].Trim();
            if (int.TryParse(controller[(colon + 1)..].Trim(), out var parsed) && parsed > 0) port = parsed;
        }

        if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";
        return new Detected(host, port, secret);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        // YAML 里空值可能写成 '' 或 ""
        return value == "''" || value == "\"\"" ? "" : value;
    }
}
