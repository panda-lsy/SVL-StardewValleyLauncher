using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SVL.Core.IO;
using System.Windows;

namespace SVL.Desktop.Models;

/// <summary>
/// MOD 搜索项数据模型
/// </summary>
public partial class ModSearchItem : ObservableObject
{
    [ObservableProperty]
    private string _id = "";  // 唯一标识（nexus-{id} 或 curse-{id}）

    [ObservableProperty]
    private string _name = "";  // MOD 名称

    [ObservableProperty]
    private string _summary = "";  // MOD 简短描述

    [ObservableProperty]
    private string _description = "";  // MOD 完整描述

    [ObservableProperty]
    private string _iconUrl = "";  // MOD 图标 URL

    [ObservableProperty]
    private string _localIconPath = "";  // 本地缓存图标路径

    [ObservableProperty]
    private string _author = "";  // 作者

    [ObservableProperty]
    private long _downloadCount = 0;  // 下载量

    [ObservableProperty]
    private string _lastUpdateTime = "";  // 最后更新时间

    [ObservableProperty]
    private string _source = "";  // 来源：Curseforge 或 NexusMods

    [ObservableProperty]
    private string _category = "";  // 类型/分类

    [ObservableProperty]
    private List<string> _supportedGameVersions = new();  // 支持的星露谷版本列表

    [ObservableProperty]
    private double _rating = 0;  // 评分（0-5）

    [ObservableProperty]
    private string _url = "";  // 详情页面 URL

    /// <summary>
    /// 异步加载并缓存图标
    /// </summary>
    public async Task LoadIconAsync()
    {
        if (string.IsNullOrWhiteSpace(IconUrl))
            return;

        // 检查缓存
        var cachedPath = ImageCacheService.GetCachedImagePath(IconUrl);
        if (cachedPath != null)
        {
            // 在 UI 线程上更新属性（使用高优先级确保立即更新）
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LocalIconPath = cachedPath;
            }, System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        // 下载并缓存
        var downloadedPath = await ImageCacheService.DownloadAndCacheImageAsync(IconUrl);
        if (downloadedPath != null)
        {
            // 在 UI 线程上更新属性（使用高优先级确保立即更新）
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LocalIconPath = downloadedPath;
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }
}
