using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using SVL.Core.IO;
using SVL.Core.Logging;

namespace SVL.Desktop.Converters;

/// <summary>
/// 图片缓存转换器 - 自动下载并缓存远程图片
/// </summary>
public class ImageCacheConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string imageUrl || string.IsNullOrWhiteSpace(imageUrl))
        {
            return null!;
        }

        try
        {
            // 检查缓存
            var cachedPath = ImageCacheService.GetCachedImagePath(imageUrl);
            if (cachedPath != null && File.Exists(cachedPath))
            {
                Log.Debug($"[ImageCacheConverter] 缓存命中: {imageUrl}");
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(cachedPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze(); // 冻结以提高性能
                return bitmap;
            }

            // 缓存不存在，异步下载并更新
            _ = DownloadAndCacheAsync(imageUrl);

            // 返回占位符或 null
            return null!;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ImageCacheConverter] 加载图片失败: {imageUrl}", ex);
            return null!;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 异步下载并缓存图片，完成后更新界面
    /// </summary>
    private static async Task DownloadAndCacheAsync(string imageUrl)
    {
        try
        {
            Log.Info($"[ImageCacheConverter] 开始下载图片: {imageUrl}");
            var cachedPath = await ImageCacheService.DownloadAndCacheImageAsync(imageUrl);

            if (cachedPath != null && File.Exists(cachedPath))
            {
                Log.Info($"[ImageCacheConverter] 图片下载成功: {cachedPath}");
                // 通知界面更新（可以通过事件或消息机制）
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ImageCacheConverter] 下载图片失败: {imageUrl}", ex);
        }
    }
}
