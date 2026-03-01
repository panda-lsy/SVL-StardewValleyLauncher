using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace SVL.Desktop.Models;

/// <summary>
/// MOD 版本项数据模型
/// </summary>
public partial class ModVersionItem : ObservableObject
{
    [ObservableProperty]
    private string _fileId = "";  // 文件 ID

    [ObservableProperty]
    private string _version = "";  // 版本号

    [ObservableProperty]
    private string _fileName = "";  // 文件名

    [ObservableProperty]
    private string _gameVersion = "";  // 支持的星露谷版本

    [ObservableProperty]
    private long _fileSize = 0;  // 文件大小（字节）

    [ObservableProperty]
    private string _uploadTime = "";  // 上传时间

    [ObservableProperty]
    private string _downloadUrl = "";  // 下载 URL

    [ObservableProperty]
    private bool _isPrimary = true;  // 是否为主文件

    [ObservableProperty]
    private string _releaseType = "";  // 发布类型：Release、Beta、Alpha

    [ObservableProperty]
    private long _downloadCount = 0;  // 下载量

    [ObservableProperty]
    private bool _isLoadingPlaceholder = false;  // 是否为加载占位项

    /// <summary>
    /// 获取不带扩展名的文件名
    /// </summary>
    public string FileNameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(FileName);
}
