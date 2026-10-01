using Microsoft.UI.Xaml.Media.Imaging;

namespace ClashVergeIsland;

/// <summary>
/// 国旗图片库：从插件目录的 flags/&lt;小写地区代码&gt;.png 取图。
///
/// 为什么要自带图片：Windows 系统字体里没有国旗图案，打 🇯🇵 只会显示成「JP」两个字母。
/// 目录不叫 assets：WinUI 会把 Assets\** 再当资源拷一份，而 Windows 路径不区分大小写，
/// 叫 assets 会让国旗在包里出现两遍。
///
/// 取到的图和「取不到」这个结果都会缓存，避免每 2 秒重复碰磁盘；
/// 只在 UI 线程用（BitmapImage 不能在后台线程创建）。
/// </summary>
public sealed class FlagLibrary
{
    /// <summary>解码宽度：既够铺满最小的 19px 小旗，也够 34px 的大旗用。</summary>
    private const int DecodeWidth = 40;

    private readonly string _directory;
    private readonly Dictionary<string, BitmapImage?> _cache = new(StringComparer.Ordinal);

    public FlagLibrary(string pluginDirectory) =>
        _directory = System.IO.Path.Combine(pluginDirectory, "flags");

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
            if (System.IO.File.Exists(path))
            {
                image = new BitmapImage
                {
                    // 关键：让解码器直接缩到接近显示尺寸再交给合成器。
                    // 源图是 160px 宽，实际最大只画到 34px；不指定的话会先整张解码再由合成器缩，
                    // 边缘细节反而更糊。40 是"能同时喂饱 19px 小旗和 34px 大旗"的尺寸。
                    DecodePixelWidth = DecodeWidth,
                    UriSource = new Uri(path),
                };
            }
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
