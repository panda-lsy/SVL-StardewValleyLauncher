using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SVL.Desktop.Models;

/// <summary>
/// 游戏版本文件分组数据模型
/// </summary>
public partial class GameVersionFilesGroup : ObservableObject
{
    private bool _suppressPageReset;
    private bool _isRemotePagingLoading;

    [ObservableProperty]
    private string _gameVersion = "";  // 游戏版本（Key 的替代）

    [ObservableProperty]
    private ObservableCollection<ModVersionItem> _files = new();  // 该版本的文件列表（Value 的替代）

    [ObservableProperty]
    private bool _isExpanded = false;  // 是否展开

    // ===== 分页（每页 5 条） =====

    public int PageSize { get; } = 5;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private bool _hasPreviousPage;

    [ObservableProperty]
    private bool _hasNextPage;

    [ObservableProperty]
    private ObservableCollection<ModVersionItem> _pagedFiles = new();

    public bool UseRemotePaging { get; set; }

    /// <summary>
    /// 用于 Relay 风格游标分页的当前游标
    /// </summary>
    public string? CurrentCursor { get; set; }

    /// <summary>
    /// 总数量（用于显示总共有多少条记录）
    /// </summary>
    public int? TotalCount { get; set; }

    public Func<int, int, Task<IReadOnlyList<ModVersionItem>>>? RemotePageLoader { get; set; }

    public string PageText => $"{Page}/{TotalPages}";

    public GameVersionFilesGroup()
    {
        Files.CollectionChanged += OnFilesCollectionChanged;
        UpdatePagedFiles();
    }

    partial void OnFilesChanged(ObservableCollection<ModVersionItem> oldValue, ObservableCollection<ModVersionItem> newValue)
    {
        if (oldValue != null)
            oldValue.CollectionChanged -= OnFilesCollectionChanged;

        if (newValue != null)
            newValue.CollectionChanged += OnFilesCollectionChanged;

        if (!UseRemotePaging || !_suppressPageReset)
            Page = 1;

        UpdatePagedFiles();
    }

    partial void OnPageChanged(int value)
    {
        UpdatePagedFiles();
    }

    private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdatePagedFiles();
    }

    private void UpdatePagedFiles()
    {
        if (UseRemotePaging)
        {
            var currentItems = Files ?? new ObservableCollection<ModVersionItem>();
            PagedFiles = new ObservableCollection<ModVersionItem>(currentItems);
            OnPropertyChanged(nameof(PageText));
            return;
        }

        var count = Files?.Count ?? 0;
        TotalPages = Math.Max(1, (int)Math.Ceiling(count / (double)PageSize));
        Page = Math.Min(Math.Max(1, Page), TotalPages);

        HasPreviousPage = Page > 1;
        HasNextPage = Page < TotalPages;

        var items = Files == null
            ? Enumerable.Empty<ModVersionItem>()
            : Files.Skip((Page - 1) * PageSize).Take(PageSize);

        PagedFiles = new ObservableCollection<ModVersionItem>(items);
        OnPropertyChanged(nameof(PageText));
    }

    [RelayCommand]
    private async Task PrevPage()
    {
        if (UseRemotePaging)
        {
            if (!HasPreviousPage || Page <= 1 || RemotePageLoader == null || _isRemotePagingLoading)
                return;

            await LoadRemotePageAsync(Page - 1);
            return;
        }

        if (HasPreviousPage)
            Page--;
    }

    [RelayCommand]
    private async Task NextPage()
    {
        if (UseRemotePaging)
        {
            if (!HasNextPage || RemotePageLoader == null || _isRemotePagingLoading)
                return;

            await LoadRemotePageAsync(Page + 1);
            return;
        }

        if (HasNextPage)
            Page++;
    }

    private async Task LoadRemotePageAsync(int targetPage)
    {
        if (RemotePageLoader == null)
            return;

        _isRemotePagingLoading = true;
        try
        {
            var items = await RemotePageLoader(targetPage, PageSize) ?? Array.Empty<ModVersionItem>();

            _suppressPageReset = true;
            Files = new ObservableCollection<ModVersionItem>(items);
            _suppressPageReset = false;

            Page = targetPage;
            HasPreviousPage = Page > 1;
            HasNextPage = items.Count >= PageSize;
            TotalPages = HasNextPage ? Page + 1 : Page;

            PagedFiles = new ObservableCollection<ModVersionItem>(Files);
            OnPropertyChanged(nameof(PageText));
        }
        finally
        {
            _suppressPageReset = false;
            _isRemotePagingLoading = false;
        }
    }
}
