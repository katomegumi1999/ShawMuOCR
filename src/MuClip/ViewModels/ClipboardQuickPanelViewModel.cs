using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MuClip.Interop;
using MuClip.Models;
using MuClip.Services;

namespace MuClip.ViewModels;

public partial class ClipboardQuickPanelViewModel : ObservableObject
{
    private readonly IMuClipService _service;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsPhrasesTabSelected))]
    private int _selectedTabIndex; // 0: 剪贴板, 1: 常用语

    public bool IsHistoryTabSelected => SelectedTabIndex == 0;
    public bool IsPhrasesTabSelected => SelectedTabIndex == 1;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private MuClipItem? _selectedItem;

    [ObservableProperty]
    private int _historyCount;

    [ObservableProperty]
    private int _phrasesCount;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editingTitle = "编辑内容";

    [ObservableProperty]
    private string _editingContent = string.Empty;

    private MuClipItem? _editingTarget;
    private bool _editingIsPhrase;

    [ObservableProperty]
    private bool _hasPairedDevices;

    [ObservableProperty]
    private string _syncTooltip = string.Empty;

    [ObservableProperty]
    private bool _isAnyPairedOnline;

    public ObservableCollection<MuClipItem> DisplayItems { get; } = new();

    public event Action? RequestClose;
    public event Action? RequestPaste;

    public ClipboardQuickPanelViewModel(IMuClipService service)
    {
        _service = service;
        _service.HistoryChanged += OnHistoryChanged;
        _service.PhrasesChanged += OnPhrasesChanged;
        _service.PairedDevicesChanged += OnSyncChanged;

        RefreshData();
        OnSyncChanged();
    }

    private void OnSyncChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var paired = _service.PairedDevices;
            HasPairedDevices = paired.Count > 0;
            int online = paired.Count(p => p.IsOnline);
            IsAnyPairedOnline = online > 0;
            SyncTooltip = HasPairedDevices
                ? $"局域网多端同步: 已配对 {paired.Count} 台设备 ({online} 台在线)"
                : "局域网同步: 暂无配对设备";
        });
    }

    private void OnHistoryChanged()
    {
        Dispatcher.UIThread.Post(ApplyFilter);
    }

    private bool _isReorderingInternally;

    private void OnPhrasesChanged()
    {
        if (_isReorderingInternally) return;
        Dispatcher.UIThread.Post(ApplyFilter);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedTabIndexChanged(int value) => ApplyFilter();

    public void RefreshData()
    {
        ApplyFilter();
    }

    private const int PageSize = 20;
    private const int SqliteBatchSize = 100;
    private int _loadedCount;
    private bool _hasMoreHistoryInDb;
    private List<MuClipItem> _cachedFilteredHistory = new();
    private List<MuClipPhraseItem> _cachedFilteredPhrases = new();

    private void ApplyFilter()
    {
        string q = SearchText.Trim();

        // 1. 过滤剪贴板历史：无搜索词时直接使用预载的 100 条纯内存热点记录（0ms 瞬时响应），后续滚动由 SQLite 按需分页加载
        if (string.IsNullOrEmpty(q))
        {
            _cachedFilteredHistory = _service.HistoryItems.ToList();
            int totalInDb = Math.Max(_cachedFilteredHistory.Count, _service.TotalHistoryCount);
            HistoryCount = totalInDb;
            _hasMoreHistoryInDb = _cachedFilteredHistory.Count < totalInDb;
        }
        else
        {
            _cachedFilteredHistory = _service.Search(q, 500);
            HistoryCount = _cachedFilteredHistory.Count;
            _hasMoreHistoryInDb = false;
        }

        // 2. 过滤常用语（直接通过 Rust search_phrases 查询，返回完整统计与预览字段）
        _cachedFilteredPhrases = (string.IsNullOrEmpty(q)
            ? _service.Phrases
            : _service.SearchPhrases(q)).ToList();

        PhrasesCount = _cachedFilteredPhrases.Count;

        DisplayItems.Clear();
        _loadedCount = 0;

        // 初始首批按需预载：只加载当前视口展示数量及预期下探几条（PageSize = 20）
        LoadNextPage();

        if (SelectedItem == null || !DisplayItems.Contains(SelectedItem))
        {
            SelectedItem = DisplayItems.FirstOrDefault();
        }
    }

    /// <summary>
    /// 当视口向下滚动接近底部时，按需预加载下一批数据（前 100 条来自内存热缓存，超出部分自动从 SQLite 分页拉取）
    /// </summary>
    public void LoadNextPage()
    {
        if (SelectedTabIndex == 0)
        {
            if (_loadedCount >= _cachedFilteredHistory.Count && _hasMoreHistoryInDb)
            {
                var nextBatch = _service.QueryHistoryPage(_cachedFilteredHistory.Count, SqliteBatchSize);
                if (nextBatch.Count > 0)
                {
                    var existingIds = _cachedFilteredHistory.Select(x => x.Id).ToHashSet();
                    foreach (var item in nextBatch)
                    {
                        if (existingIds.Add(item.Id))
                        {
                            _cachedFilteredHistory.Add(item);
                        }
                    }
                    if (nextBatch.Count < SqliteBatchSize || _cachedFilteredHistory.Count >= HistoryCount)
                    {
                        _hasMoreHistoryInDb = false;
                        HistoryCount = Math.Max(HistoryCount, _cachedFilteredHistory.Count);
                    }
                }
                else
                {
                    _hasMoreHistoryInDb = false;
                    HistoryCount = _cachedFilteredHistory.Count;
                }
            }

            if (_loadedCount >= _cachedFilteredHistory.Count) return;
            int countToTake = Math.Min(PageSize, _cachedFilteredHistory.Count - _loadedCount);
            for (int i = 0; i < countToTake; i++)
            {
                var it = _cachedFilteredHistory[_loadedCount + i];
                it.IsFavorite = _cachedFilteredPhrases.Any(p => p.Content == it.Content);
                DisplayItems.Add(it);
            }
            _loadedCount += countToTake;
        }
        else
        {
            if (_loadedCount >= _cachedFilteredPhrases.Count) return;
            int countToTake = Math.Min(PageSize, _cachedFilteredPhrases.Count - _loadedCount);
            for (int i = 0; i < countToTake; i++)
            {
                var p = _cachedFilteredPhrases[_loadedCount + i];
                DisplayItems.Add(new MuClipItem
                {
                    Id = p.Id,
                    Content = p.Content,
                    Preview = p.Preview,
                    ContentType = p.ContentType,
                    FormattedTime = p.FormattedTime,
                    CharCount = p.CharCount,
                    LineCount = p.LineCount,
                    ByteCount = p.ByteCount,
                    IsFavorite = true
                });
            }
            _loadedCount += countToTake;
        }
    }

    [RelayCommand]
    public void SelectHistoryTab()
    {
        SelectedTabIndex = 0;
    }

    [RelayCommand]
    public void SelectPhrasesTab()
    {
        SelectedTabIndex = 1;
    }

    [RelayCommand]
    public void SwitchTab(int index)
    {
        SelectedTabIndex = index;
    }

    [RelayCommand]
    public void SelectAndPaste(MuClipItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.Content)) return;

        if (SelectedTabIndex == 1)
        {
            // 更新常用语热度并写入系统剪贴板（自动前移）
            _service.UsePhrase(item.Id, item.Content);
        }
        else
        {
            // 写入系统剪贴板（此操作会自动将重复条目前移置顶）
            _service.CopyToSystemClipboard(item.Content);
        }

        // 关闭快捷面板并就地模拟粘贴
        if (RequestPaste != null)
        {
            RequestPaste.Invoke();
        }
        else
        {
            RequestClose?.Invoke();
            Win32CaretHelper.SimulatePaste(70);
        }
    }

    [RelayCommand]
    public void ToggleFavorite(MuClipItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Content)) return;

        if (SelectedTabIndex == 1)
        {
            // 在常用语列表中点击星标：取消收藏（删除常用语）
            _service.DeletePhrase(item.Id);
        }
        else
        {
            // 在剪贴板历史中点击星标：切换收藏状态
            var existing = _service.Phrases.FirstOrDefault(p => p.Content == item.Content);
            if (existing != null)
            {
                _service.DeletePhrase(existing.Id);
                item.IsFavorite = false;
            }
            else
            {
                _service.AddPhrase(item.Content);
                item.IsFavorite = true;
            }
        }
        ApplyFilter();
    }

    [RelayCommand]
    public void CopyOnly(string? content)
    {
        if (string.IsNullOrEmpty(content)) return;
        _service.CopyToSystemClipboard(content);
    }

    [RelayCommand]
    public void StartAddNewPhrase()
    {
        SelectedTabIndex = 1;
        _editingTarget = null;
        _editingIsPhrase = true;
        EditingTitle = "新增常用语";
        EditingContent = string.Empty;
        IsEditing = true;
    }

    [RelayCommand]
    public void StartEdit(MuClipItem? item)
    {
        if (item == null) return;
        _editingTarget = item;
        _editingIsPhrase = SelectedTabIndex == 1;
        EditingTitle = _editingIsPhrase ? "编辑常用语" : "编辑剪贴板内容";
        EditingContent = item.Content;
        IsEditing = true;
    }

    [RelayCommand]
    public void SaveEdit()
    {
        string newText = EditingContent.Trim();
        if (string.IsNullOrEmpty(newText))
        {
            CancelEdit();
            return;
        }

        if (_editingTarget != null)
        {
            if (_editingIsPhrase)
            {
                if (!_service.EditPhrase(_editingTarget.Id, newText))
                {
                    _service.DeletePhrase(_editingTarget.Id);
                    _service.AddPhrase(newText);
                }
            }
            else
            {
                _service.DeleteRecord(_editingTarget.Id);
                _service.AddRecord(newText, broadcast: false);
            }
        }
        else if (_editingIsPhrase)
        {
            _service.AddPhrase(newText);
        }

        IsEditing = false;
        EditingContent = string.Empty;
        _editingTarget = null;
        ApplyFilter();
    }

    public void CommitItemsReordered()
    {
        if (SelectedTabIndex == 1)
        {
            _isReorderingInternally = true;
            try
            {
                _service.ReorderPhrases(DisplayItems.Select(x => x.Id).ToList());
            }
            finally
            {
                _isReorderingInternally = false;
            }
        }
    }

    public void OnItemsReordered(int oldIndex, int newIndex)
    {
        // 拖拽过程中的中间移动仅在内存中调度，待用户松开时由 CommitItemsReordered 统一持久化
    }

    [RelayCommand]
    public void CancelEdit()
    {
        IsEditing = false;
        EditingContent = string.Empty;
        _editingTarget = null;
    }

    [RelayCommand]
    public void DeleteItem(MuClipItem? item)
    {
        if (item == null) return;

        if (SelectedTabIndex == 1)
        {
            _service.DeletePhrase(item.Id);
        }
        else
        {
            _service.DeleteRecord(item.Id);
        }
        ApplyFilter();
    }

    public void QuickSelectNumber(int number)
    {
        int index = number - 1;
        if (index >= 0 && index < DisplayItems.Count)
        {
            SelectAndPaste(DisplayItems[index]);
        }
    }

    public void SelectCurrent()
    {
        if (SelectedItem != null)
        {
            SelectAndPaste(SelectedItem);
        }
    }
}
