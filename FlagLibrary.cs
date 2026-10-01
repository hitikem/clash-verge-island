using Microsoft.UI.Xaml.Media.Imaging;

namespace ClashVergeIsland;

/// <summary>
/// 国旗图片库：从插件目录的 assets/flags/&lt;小写地区代码&gt;.png 取图。
///
/// 为什么要自带图片：Windows 系统字体里没有国旗图案，打 🇯🇵 只会显示成「JP」两个字母。
///
/// 取到的图和「取不到」这个结果都会缓存，避免每 2 秒重复碰磁盘；
/// 只在 UI 线程用（BitmapImage 不能在后台线程创建）。
/// </summary>
public sealed class FlagLibrary
{
    private readonly string _directory;
    private readonly Dictionary<string, BitmapImage?> _cache = new(StringComparer.Ordinal);

    public FlagLibrary(string pluginDirectory) =>
        _directory = System.IO.Path.Combine(pluginDirectory, "assets", "flags");

    public string Directory => _directory;

    /// <summary>取国旗；没有这张图就返回 null（调用方退回徽章或地球图标）。</summary>
    public BitmapImage? Get(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        if (_cache.TryGetValue(code, out var cached)) return cached;

        BitmapImage? image = null;
        try
        {
            var path = System.IO.Path.Combine(_directory, code.ToLowerInvariant() + ".png");
            if (System.IO.File.Exists(path)) image = new BitmapImage(new Uri(path));
        }
        catch (Exception)
        {
            image = null;
        }

        _cache[code] = image;
        return image;
    }

    public void Clear() => _cache.Clear();
}
