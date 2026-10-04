using System.Text.Json;

namespace ClashVergeIsland;

/// <summary>
/// 今日 / 本月的流量累计。
///
/// 内核只给「自启动以来的累计字节数」，Clash Verge 一重启就归零 ——
/// 所以日/月统计必须自己攒：每次刷新算增量、按日期归集、定期落盘。
/// </summary>
internal sealed class TrafficStats
{
    public string Day { get; set; } = "";
    public string Month { get; set; } = "";

    public long TodayUp { get; set; }
    public long TodayDown { get; set; }
    public long MonthUp { get; set; }
    public long MonthDown { get; set; }

    /// <summary>上一次见到的内核累计值，用来算增量；-1 = 还没基线。</summary>
    public long LastUp { get; set; } = -1;
    public long LastDown { get; set; } = -1;

    private static string PathFor(string dir) => System.IO.Path.Combine(dir, "traffic.json");

    public static TrafficStats Load(string dir)
    {
        try
        {
            var path = PathFor(dir);
            if (System.IO.File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<TrafficStats>(System.IO.File.ReadAllText(path));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception)
        {
            // 文件坏了就当没有，重新开始攒，不能因此让插件起不来
        }

        return new TrafficStats();
    }

    public void Save(string dir)
    {
        try
        {
            System.IO.File.WriteAllText(PathFor(dir), JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // 落盘失败不影响运行，下一次刷新还会再试
        }
    }

    /// <summary>
    /// 把一个采样点并进统计。
    /// 跨天/跨月自动清零；内核重启（累计值变小）时重新取基线，绝不记成负数。
    /// </summary>
    public void Add(long up, long down, DateTimeOffset now)
    {
        var day = now.ToString("yyyy-MM-dd");
        var month = now.ToString("yyyy-MM");

        if (!string.Equals(Day, day, StringComparison.Ordinal))
        {
            Day = day;
            TodayUp = 0;
            TodayDown = 0;
        }

        if (!string.Equals(Month, month, StringComparison.Ordinal))
        {
            Month = month;
            MonthUp = 0;
            MonthDown = 0;
        }

        // 只有累计值在增长时才记增量 —— 变小说明内核重启过，重新取基线
        if (LastUp >= 0 && up >= LastUp && down >= LastDown)
        {
            var deltaUp = up - LastUp;
            var deltaDown = down - LastDown;

            TodayUp += deltaUp;
            TodayDown += deltaDown;
            MonthUp += deltaUp;
            MonthDown += deltaDown;
        }

        LastUp = up;
        LastDown = down;
    }
}
