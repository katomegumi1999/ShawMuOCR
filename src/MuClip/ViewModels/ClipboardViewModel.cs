using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MuClip.Interop;
using MuClip.Models;
using MuClip.Services;

namespace MuClip.ViewModels;

public partial class ClipboardViewModel : ObservableObject
{
    private readonly IMuClipService _service;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsPhrasesTabSelected))]
    private int _selectedTabIndex; // 0: 剪贴历史, 1: 常用语

    public bool IsHistoryTabSelected => SelectedTabIndex == 0;
    public bool IsPhrasesTabSelected => SelectedTabIndex == 1;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedTypeFilter = "全部";

    [ObservableProperty]
    private MuClipItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPhrase))]
    [NotifyPropertyChangedFor(nameof(PhraseDetailTitle))]
    private MuClipPhraseItem? _selectedPhrase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhraseDetailTitle))]
    private bool _isAddingNewPhrase;

    [ObservableProperty]
    private string _phraseEditorText = string.Empty;

    [ObservableProperty]
    private int _phrasesCount;

    public bool HasSelectedPhrase => SelectedPhrase != null && !IsAddingNewPhrase;
    public string PhraseDetailTitle => IsAddingNewPhrase ? "➕ 新增常用语" : "💬 常用语详情与编辑";

    partial void OnSelectedPhraseChanged(MuClipPhraseItem? value)
    {
        if (value != null)
        {
            IsAddingNewPhrase = false;
            PhraseEditorText = value.Content;
        }
        else if (!IsAddingNewPhrase)
        {
            PhraseEditorText = string.Empty;
        }
    }

    [ObservableProperty]
    private bool _isSyncPanelVisible;

    [ObservableProperty]
    private string _pairingPin = string.Empty;

    [ObservableProperty]
    private string _manualPeerInput = string.Empty;

    [ObservableProperty]
    private string _syncStatusText = "局域网同步未连接";

    [ObservableProperty]
    private int _onlinePeerCount;

    [ObservableProperty]
    private int _pairedDeviceCount;

    [ObservableProperty]
    private int _onlinePairedCount;

    [ObservableProperty]
    private bool _hasPairedDevices;

    [ObservableProperty]
    private bool _hasDiscoveredPeers;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    // 文件传输二级页面与状态
    [ObservableProperty]
    private bool _isFileTransferViewActive;

    [ObservableProperty]
    private int _activeTransferCount;

    [ObservableProperty]
    private string _fileSaveDirectory = string.Empty;

    [ObservableProperty]
    private TransferTargetDeviceItem? _selectedTransferTarget;

    [ObservableProperty]
    private string _manualTargetEndpoint = string.Empty;

    [ObservableProperty]
    private string _selectedSendFilePath = string.Empty;

    [ObservableProperty]
    private string _selectedSendFileName = string.Empty;

    [ObservableProperty]
    private string _selectedSendFileSizeText = string.Empty;

    [ObservableProperty]
    private bool _hasSelectedSendFile;

    [ObservableProperty]
    private bool _isSendingFile;

    public ObservableCollection<FileTransferItem> FileTransfers { get; } = new();
    public ObservableCollection<TransferTargetDeviceItem> AvailableTransferTargets { get; } = new();

    public Func<Task<string?>>? RequestPickFileFunc { get; set; }
    public Func<Task<string?>>? RequestPickFolderFunc { get; set; }

    // 发起方配对状态
    [ObservableProperty]
    private bool _isWaitingForPeerPinConfirmation;

    [ObservableProperty]
    private string _pendingPairDeviceName = string.Empty;

    [ObservableProperty]
    private string _pendingPairDeviceId = string.Empty;

    [ObservableProperty]
    private string _pendingPairPin = string.Empty;

    [ObservableProperty]
    private string _pendingPairIp = string.Empty;

    [ObservableProperty]
    private int _pendingPairPort;

    // 接收方配对状态与输入
    [ObservableProperty]
    private bool _isIncomingPairRequestVisible;

    [ObservableProperty]
    private string _incomingPairRequestId = string.Empty;

    [ObservableProperty]
    private string _incomingPairDeviceName = string.Empty;

    [ObservableProperty]
    private string _incomingPairDeviceId = string.Empty;

    [ObservableProperty]
    private string _incomingPairExpectedPin = string.Empty;

    [ObservableProperty]
    private string _incomingPairInputPin = string.Empty;

    [ObservableProperty]
    private string _incomingPairIp = string.Empty;

    [ObservableProperty]
    private int _incomingPairPort = 42424;

    [ObservableProperty]
    private string _incomingPairErrorMessage = string.Empty;

    [ObservableProperty]
    private string _panelBackgroundColor = "#F4F4F6";

    [ObservableProperty]
    private IBrush _currentPanelBackgroundBrush = new SolidColorBrush(Color.Parse("#F4F4F6"));

    public ObservableCollection<PaletteColorItem> PaletteColors { get; } = new()
    {
        new PaletteColorItem("#F4F4F6", "极简浅灰(默认)"),
        new PaletteColorItem("#FFFFFF", "纯净皓白"),
        new PaletteColorItem("#F7F5F0", "温润米白"),
        new PaletteColorItem("#EEF2F6", "清爽冰蓝"),
        new PaletteColorItem("#F5F0F7", "淡雅柔紫"),
        new PaletteColorItem("#E8ECEF", "雾霾雅灰"),
        new PaletteColorItem("#2D2D30", "深空暗灰"),
        new PaletteColorItem("#1E1E1E", "暗夜纯黑")
    };

    public event Action<string>? PanelBackgroundChanged;

    partial void OnPanelBackgroundColorChanged(string value)
    {
        if (Color.TryParse(value, out var c))
        {
            CurrentPanelBackgroundBrush = new SolidColorBrush(c);
        }
    }

    [RelayCommand]
    public void SelectPaletteColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color)) return;
        PanelBackgroundColor = color;
        PanelBackgroundChanged?.Invoke(color);
        StatusMessage = $"快捷面板底色已设置为 {color}";
    }

    [RelayCommand]
    public void ApplyPanelBackground()
    {
        if (string.IsNullOrWhiteSpace(PanelBackgroundColor)) return;
        PanelBackgroundChanged?.Invoke(PanelBackgroundColor);
        StatusMessage = $"快捷面板底色已应用: {PanelBackgroundColor}";
    }

    public ObservableCollection<MuClipItem> FilteredItems { get; } = new();
    public ObservableCollection<MuClipPhraseItem> FilteredPhrases { get; } = new();
    public ObservableCollection<PairedDeviceInfo> PairedDevices { get; } = new();
    public ObservableCollection<PeerDeviceInfo> PeerDevices { get; } = new();

    public string[] TypeFilters { get; } = ["全部", "文本", "代码", "链接", "JSON"];

    [ObservableProperty]
    private bool _autoDiscoveryEnabled = true;

    [ObservableProperty]
    private bool _isScanning;

    private bool _isHostViewActive = true;
    private System.Threading.CancellationTokenSource? _foregroundScanCts;
    private readonly object _scanLoopLock = new();

    public void SetHostViewActive(bool isActive)
    {
        if (_isHostViewActive == isActive) return;
        _isHostViewActive = isActive;
        UpdateForegroundDiscoveryLoop();
    }

    partial void OnIsSyncPanelVisibleChanged(bool value)
    {
        UpdateForegroundDiscoveryLoop();
    }

    partial void OnIsFileTransferViewActiveChanged(bool value)
    {
        UpdateForegroundDiscoveryLoop();
    }

    partial void OnAutoDiscoveryEnabledChanged(bool value)
    {
        _service.AutoDiscoveryEnabled = value;
        StatusMessage = value ? "已开启局域网设备自动扫描" : "已关闭局域网自动扫描（可手动点击扫描）";
        UpdateForegroundDiscoveryLoop();
    }

    private void UpdateForegroundDiscoveryLoop()
    {
        bool shouldScan = _isHostViewActive && AutoDiscoveryEnabled && (IsSyncPanelVisible || IsFileTransferViewActive);

        System.Threading.CancellationToken token;
        lock (_scanLoopLock)
        {
            if (!shouldScan)
            {
                _foregroundScanCts?.Cancel();
                _foregroundScanCts?.Dispose();
                _foregroundScanCts = null;
                return;
            }

            if (_foregroundScanCts != null && !_foregroundScanCts.IsCancellationRequested)
            {
                return;
            }

            _foregroundScanCts?.Dispose();
            _foregroundScanCts = new System.Threading.CancellationTokenSource();
            token = _foregroundScanCts.Token;
        }

        _ = RunForegroundScanLoopAsync(token);
    }

    private async Task RunForegroundScanLoopAsync(System.Threading.CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                _service.TriggerDiscoveryScan();
                await Task.Delay(800, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) break;
                _service.RefreshPeers();
                RefreshDevicesCollections();

                await Task.Delay(3200, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 前台局域网同步面板关闭或切换页面时正常停止扫描循环
        }
        catch
        {
        }
    }

    [RelayCommand]
    public async Task ScanPeersAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusMessage = "正在扫描局域网设备 (UDP 42110)...";
        _service.TriggerDiscoveryScan();
        await Task.Delay(1500);
        _service.RefreshPeers();
        RefreshDevicesCollections();
        IsScanning = false;
        StatusMessage = PeerDevices.Count > 0 ? $"✓ 扫描完成，发现 {PeerDevices.Count} 台未配对设备" : "扫描完成，当前局域网暂无未配对新设备";
    }

    public ClipboardViewModel(IMuClipService service)
    {
        _service = service;
        _autoDiscoveryEnabled = _service.AutoDiscoveryEnabled;
        _service.HistoryChanged += OnHistoryChanged;
        _service.PhrasesChanged += OnPhrasesChanged;
        _service.PeersChanged += RefreshDevicesCollections;
        _service.PairedDevicesChanged += RefreshDevicesCollections;
        _service.ClipboardReceived += OnClipboardReceived;
        _service.PairRequestReceived += OnPairRequestReceived;
        _service.IncomingPairRequested += OnIncomingPairRequested;
        _service.PairEstablished += OnPairEstablished;
        _service.RemotePeerUnpaired += OnRemotePeerUnpaired;
        _service.FileProgress += OnFileProgress;
        _service.FileCompleted += OnFileCompleted;
        _service.FileOfferReceived += OnFileOfferReceived;

        _fileSaveDirectory = _service.FileSaveDirectory;
        PairingPin = _service.PairingPin;
        RefreshDevicesCollections();
        UpdateSyncStatus();
        LoadPhrases();
        LoadItems();
    }

    private void OnHistoryChanged()
    {
        Dispatcher.UIThread.Post(LoadItems);
    }

    private void OnPhrasesChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LoadPhrases();
            SyncHistoryFavoriteStates();
        });
    }

    private void RefreshDevicesCollections()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateDevicesCollectionsCore();
        }
        else
        {
            try
            {
                Dispatcher.UIThread.Post(UpdateDevicesCollectionsCore);
            }
            catch
            {
                UpdateDevicesCollectionsCore();
            }
        }
    }

    private void UpdateDevicesCollectionsCore()
    {
        var pairedList = _service.PairedDevices;
        PairedDevices.Clear();
        foreach (var p in pairedList)
        {
            PairedDevices.Add(p);
        }
        PairedDeviceCount = PairedDevices.Count;
        OnlinePairedCount = PairedDevices.Count(p => p.IsOnline);
        HasPairedDevices = PairedDevices.Count > 0;

        var active = _service.ActivePeers;
        var pairedDeviceIds = new HashSet<string>(
            pairedList.Select(p => p.DeviceId).Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);
        var pairedEndpoints = new HashSet<string>(
            pairedList.Where(p => !string.IsNullOrEmpty(p.Ip))
                      .Select(p => p.Endpoint),
            StringComparer.OrdinalIgnoreCase);

        var unbonded = active.Where(p =>
            !p.IsPaired &&
            !pairedDeviceIds.Contains(p.DeviceId) &&
            !pairedEndpoints.Contains(p.Endpoint)).ToList();

        PeerDevices.Clear();
        foreach (var peer in unbonded)
        {
            PeerDevices.Add(peer);
        }
        HasDiscoveredPeers = PeerDevices.Count > 0;
        OnlinePeerCount = OnlinePairedCount + unbonded.Count(p => p.IsOnline);
        UpdateSyncStatus();
        UpdateAvailableTransferTargets();
    }

    private void OnClipboardReceived(string content)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = $"[同步] 收到远程设备剪贴板 ({content.Length} 字符)";
        });
    }

    private void OnPairRequestReceived(string deviceId, string deviceName, string pin)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var peerMatch = _service.ActivePeers.FirstOrDefault(p => p.DeviceId == deviceId);
            string ip = peerMatch?.Ip ?? string.Empty;
            int port = peerMatch?.SyncPort > 0 ? peerMatch.SyncPort : 42424;

            // 1. 本机作为发起方，正在等待对方确认配对
            if (IsWaitingForPeerPinConfirmation &&
                (deviceId == PendingPairDeviceId || string.Equals(pin, PendingPairPin, StringComparison.Ordinal)))
            {
                _service.AddOrUpdatePairedDevice(deviceId, deviceName, ip, port);
                IsWaitingForPeerPinConfirmation = false;
                StatusMessage = $"🎉 对方已验证配对码，与【{deviceName}】配对成功！";
                RefreshDevicesCollections();
                return;
            }

            // 2. 该设备已在已配对列表中（心跳或重连更新）
            if (_service.PairedDevices.Any(p => p.DeviceId == deviceId))
            {
                _service.AddOrUpdatePairedDevice(deviceId, deviceName, ip, port);
                StatusMessage = $"[已连接] 已更新与【{deviceName}】的配对状态";
                RefreshDevicesCollections();
                return;
            }

            // 3. 本机作为接收方，收到新设备的配对请求 -> 弹出输入配对码面板
            IncomingPairDeviceId = deviceId;
            IncomingPairDeviceName = !string.IsNullOrEmpty(deviceName) ? deviceName : "未命名局域网设备";
            IncomingPairExpectedPin = pin;
            IncomingPairInputPin = string.Empty;
            IncomingPairErrorMessage = string.Empty;
            IncomingPairIp = ip;
            IncomingPairPort = port;
            IsIncomingPairRequestVisible = true;
            StatusMessage = $"🔔 收到来自【{IncomingPairDeviceName}】的配对请求，等待输入配对码...";
        });
    }

    private void OnIncomingPairRequested(string requestId, string deviceId, string deviceName, string peerIp)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IncomingPairRequestId = requestId;
            IncomingPairDeviceId = deviceId;
            IncomingPairDeviceName = !string.IsNullOrEmpty(deviceName) ? deviceName : "未命名局域网设备";
            IncomingPairIp = peerIp;
            IncomingPairInputPin = string.Empty;
            IncomingPairErrorMessage = string.Empty;
            IsIncomingPairRequestVisible = true;
            StatusMessage = $"🔔 收到来自【{IncomingPairDeviceName}】的配对请求，等待输入配对码...";
        });
    }

    private void OnPairEstablished(string deviceId, string deviceName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsIncomingPairRequestVisible = false;
            IsWaitingForPeerPinConfirmation = false;
            IncomingPairInputPin = string.Empty;
            IncomingPairErrorMessage = string.Empty;
            PairingPin = _service.PairingPin;
            StatusMessage = $"🎉 配对成功！已与【{deviceName}】建立局域网加密连接";
            RefreshDevicesCollections();
        });
    }

    private void OnRemotePeerUnpaired(string deviceId, string deviceName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = $"🔌 对方设备【{deviceName}】已解除配对";
            RefreshDevicesCollections();
        });
    }

    private void UpdateSyncStatus()
    {
        if (_service.IsSyncEnabled)
        {
            SyncStatusText = $"局域网加密同步已运行 (端口: {_service.SyncPort})，已配对: {PairedDeviceCount} 台 (在线 {OnlinePairedCount} 台)";
        }
        else
        {
            SyncStatusText = "局域网加密同步未启用";
        }
    }

    [ObservableProperty]
    private int _totalHistoryCount;

    private int _dbLoadedOffset;
    private bool _hasMoreHistoryInDb;
    private const int DbPageSize = 100;

    partial void OnSearchTextChanged(string value)
    {
        LoadItems();
        LoadPhrases();
    }

    partial void OnSelectedTypeFilterChanged(string value) => LoadItems();

    private bool MatchesTypeFilter(MuClipItem it)
    {
        return SelectedTypeFilter switch
        {
            "文本" => it.ContentType is "text" or "plain_text",
            "代码" => it.ContentType == "code",
            "链接" => it.ContentType == "url",
            "JSON" => it.ContentType == "json",
            _ => true
        };
    }

    private void SyncHistoryFavoriteStates()
    {
        var phraseSet = _service.Phrases.Select(p => p.Content).ToHashSet(StringComparer.Ordinal);
        foreach (var item in FilteredItems)
        {
            item.IsFavorite = phraseSet.Contains(item.Content);
        }
    }

    public void LoadPhrases()
    {
        var allPhrases = _service.Phrases;
        PhrasesCount = allPhrases.Count;

        var matched = string.IsNullOrWhiteSpace(SearchText)
            ? allPhrases.ToList()
            : _service.SearchPhrases(SearchText.Trim());

        long? prevSelectedId = SelectedPhrase?.Id;
        FilteredPhrases.Clear();
        foreach (var p in matched)
        {
            FilteredPhrases.Add(p);
        }

        if (!IsAddingNewPhrase)
        {
            var restored = prevSelectedId.HasValue
                ? FilteredPhrases.FirstOrDefault(p => p.Id == prevSelectedId.Value)
                : null;
            SelectedPhrase = restored ?? FilteredPhrases.FirstOrDefault();
        }
    }

    public void LoadItems()
    {
        bool isUnfiltered = string.IsNullOrWhiteSpace(SearchText) && SelectedTypeFilter == "全部";
        var phraseSet = _service.Phrases.Select(p => p.Content).ToHashSet(StringComparer.Ordinal);

        if (isUnfiltered)
        {
            // 默认无过滤状态：直接加载内存预载的前 100 条热点数据（0ms 响应），超出部分滚动时从 SQLite 分页拉取
            var hotItems = _service.HistoryItems;
            FilteredItems.Clear();
            foreach (var item in hotItems)
            {
                item.IsFavorite = phraseSet.Contains(item.Content);
                FilteredItems.Add(item);
            }

            _dbLoadedOffset = FilteredItems.Count;
            int totalInDb = Math.Max(FilteredItems.Count, _service.TotalHistoryCount);
            TotalHistoryCount = totalInDb;
            _hasMoreHistoryInDb = _dbLoadedOffset < totalInDb;
        }
        else
        {
            // 搜索或分类筛选状态：从 SQLite 持久化库中检索匹配项
            int maxSearchLimit = Math.Max(500, _service.TotalHistoryCount);
            var rawItems = string.IsNullOrWhiteSpace(SearchText)
                ? _service.QueryHistoryPage(0, maxSearchLimit)
                : _service.Search(SearchText, maxSearchLimit);

            var filtered = rawItems.Where(MatchesTypeFilter).ToList();

            FilteredItems.Clear();
            foreach (var item in filtered)
            {
                item.IsFavorite = phraseSet.Contains(item.Content);
                FilteredItems.Add(item);
            }

            _dbLoadedOffset = rawItems.Count;
            _hasMoreHistoryInDb = false;
            TotalHistoryCount = FilteredItems.Count;
        }

        if (SelectedItem == null || !FilteredItems.Contains(SelectedItem))
        {
            SelectedItem = FilteredItems.FirstOrDefault();
        }
    }

    /// <summary>
    /// 当主列表滚动到底部时，从 SQLite 按需加载后续历史记录（第 101 条及以后）
    /// </summary>
    public void LoadMoreHistory()
    {
        if (!_hasMoreHistoryInDb) return;
        if (!string.IsNullOrWhiteSpace(SearchText) || SelectedTypeFilter != "全部") return;

        var moreItems = _service.QueryHistoryPage(_dbLoadedOffset, DbPageSize);
        if (moreItems.Count == 0)
        {
            _hasMoreHistoryInDb = false;
            TotalHistoryCount = FilteredItems.Count;
            return;
        }

        var phraseSet = _service.Phrases.Select(p => p.Content).ToHashSet(StringComparer.Ordinal);
        var existingIds = FilteredItems.Select(x => x.Id).ToHashSet();
        foreach (var item in moreItems)
        {
            if (existingIds.Add(item.Id))
            {
                item.IsFavorite = phraseSet.Contains(item.Content);
                FilteredItems.Add(item);
            }
        }

        _dbLoadedOffset += moreItems.Count;
        if (moreItems.Count < DbPageSize || _dbLoadedOffset >= _service.TotalHistoryCount)
        {
            _hasMoreHistoryInDb = false;
        }
        TotalHistoryCount = Math.Max(FilteredItems.Count, _service.TotalHistoryCount);
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
    public void ToggleFavorite(MuClipItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Content)) return;

        var existing = _service.Phrases.FirstOrDefault(p => p.Content == item.Content);
        if (existing != null)
        {
            _service.DeletePhrase(existing.Id);
            item.IsFavorite = false;
            StatusMessage = "☆ 已从常用语中移除";
        }
        else
        {
            _service.AddPhrase(item.Content);
            item.IsFavorite = true;
            StatusMessage = "★ 已收藏到常用语";
        }
        LoadPhrases();
        SyncHistoryFavoriteStates();
    }

    [RelayCommand]
    public void StartNewPhrase()
    {
        SelectedTabIndex = 1;
        IsAddingNewPhrase = true;
        SelectedPhrase = null;
        PhraseEditorText = string.Empty;
        StatusMessage = "请在右侧输入新常用语内容并点击「保存」";
    }

    [RelayCommand]
    public void SavePhrase()
    {
        string text = PhraseEditorText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = "⚠️ 常用语内容不能为空";
            return;
        }

        if (IsAddingNewPhrase || SelectedPhrase == null)
        {
            long newId = _service.AddPhrase(text);
            IsAddingNewPhrase = false;
            LoadPhrases();
            SyncHistoryFavoriteStates();
            SelectedPhrase = FilteredPhrases.FirstOrDefault(p => p.Id == newId) ?? FilteredPhrases.FirstOrDefault();
            StatusMessage = "✓ 已新增常用语";
        }
        else
        {
            long targetId = SelectedPhrase.Id;
            if (!_service.EditPhrase(targetId, text))
            {
                _service.DeletePhrase(targetId);
                targetId = _service.AddPhrase(text);
            }
            LoadPhrases();
            SyncHistoryFavoriteStates();
            SelectedPhrase = FilteredPhrases.FirstOrDefault(p => p.Id == targetId) ?? FilteredPhrases.FirstOrDefault();
            StatusMessage = "✓ 已保存常用语修改";
        }
    }

    [RelayCommand]
    public void CopyPhrase(MuClipPhraseItem? phrase)
    {
        var target = phrase ?? SelectedPhrase;
        if (target == null || string.IsNullOrEmpty(target.Content)) return;

        _service.UsePhrase(target.Id, target.Content);
        target.IsCopied = true;
        StatusMessage = "✓ 已复制常用语到系统剪贴板";

        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() => target.IsCopied = false);
        });
    }

    [RelayCommand]
    public async Task BroadcastPhraseAsync(MuClipPhraseItem? phrase)
    {
        var target = phrase ?? SelectedPhrase;
        if (target == null || string.IsNullOrEmpty(target.Content)) return;

        StatusMessage = "正在将常用语同步到局域网设备...";
        int sent = await Task.Run(() => _service.Broadcast(target.Content));
        StatusMessage = sent > 0 ? $"✓ 常用语已同步至 {sent} 台局域网设备" : "⚠️ 当前未发现任何在线局域网设备";
    }

    [RelayCommand]
    public void DeletePhrase(MuClipPhraseItem? phrase)
    {
        var target = phrase ?? SelectedPhrase;
        if (target == null) return;

        _service.DeletePhrase(target.Id);
        LoadPhrases();
        SyncHistoryFavoriteStates();
        StatusMessage = "已删除该条常用语";
    }

    [RelayCommand]
    public void CopyItem(MuClipItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.Content)) return;

        _service.CopyToSystemClipboard(item.Content);

        item.IsCopied = true;
        StatusMessage = $"已复制第 {item.Id} 条记录到系统剪贴板";

        // 1.5 秒后清除动画反馈
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() => item.IsCopied = false);
        });
    }

    [RelayCommand]
    public void DeleteItem(MuClipItem? item)
    {
        if (item == null) return;
        _service.DeleteRecord(item.Id);
        StatusMessage = $"已删除第 {item.Id} 条历史记录";
    }

    [RelayCommand]
    public void ClearAll()
    {
        _service.ClearRecords();
        StatusMessage = "已清空所有剪贴板记录";
    }

    [RelayCommand]
    public async Task BroadcastItemAsync(MuClipItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.Content)) return;
        StatusMessage = "正在同步到局域网设备...";
        int sent = await Task.Run(() => _service.Broadcast(item.Content));
        StatusMessage = sent > 0 ? $"✓ 已同步至 {sent} 台局域网设备" : "⚠️ 当前未发现任何在线局域网设备";
    }

    [RelayCommand]
    public void ToggleSyncPanel()
    {
        IsSyncPanelVisible = !IsSyncPanelVisible;
    }

    [RelayCommand]
    public void SavePin()
    {
        if (!string.IsNullOrWhiteSpace(PairingPin))
        {
            string trimmedPin = PairingPin.Trim();
            if (trimmedPin == "已信任 (长期密钥)")
            {
                StatusMessage = "当前已使用 ECDH 长期信任密钥";
                return;
            }
            _service.UpdatePairingPin(trimmedPin);
            StatusMessage = $"配对 PIN 码已更新: {trimmedPin}";
        }
    }

    [RelayCommand]
    public void RollNewRandomPin()
    {
        string newPin = MuClipService.GenerateRandomPin();
        PairingPin = newPin;
        _service.UpdatePairingPin(newPin);
        StatusMessage = $"已生成新随机 PIN 码: {newPin}";
    }

    [RelayCommand]
    public async Task PairWithPeer(PeerDeviceInfo? peer)
    {
        if (peer == null) return;
        string randomPin = MuClipService.GenerateRandomPin();
        PairingPin = randomPin;

        PendingPairDeviceName = peer.DeviceName;
        PendingPairDeviceId = peer.DeviceId;
        PendingPairPin = randomPin;
        PendingPairIp = peer.Ip;
        PendingPairPort = peer.SyncPort;
        IsWaitingForPeerPinConfirmation = true;
        StatusMessage = $"已向 [{peer.DeviceName}] 发起配对，配对码为: 【{randomPin}】，等待对方输入确认...";

        var result = await _service.RequestPairAsync(peer.Ip, peer.SyncPort, randomPin, 60);
        IsWaitingForPeerPinConfirmation = false;

        if (result.Success)
        {
            PairingPin = _service.PairingPin;
            StatusMessage = $"🎉 配对成功！已与【{result.DeviceName}】建立局域网加密连接";
            RefreshDevicesCollections();
        }
        else
        {
            PairingPin = _service.PairingPin;
            StatusMessage = $"与 [{peer.DeviceName}] 配对失败: {result.Message}";
        }
    }

    [RelayCommand]
    public void CancelOutgoingPair()
    {
        IsWaitingForPeerPinConfirmation = false;
        PendingPairDeviceName = string.Empty;
        PendingPairDeviceId = string.Empty;
        PendingPairPin = string.Empty;
        StatusMessage = "已取消配对请求";
    }

    [RelayCommand]
    public void ConfirmIncomingPair()
    {
        if (string.IsNullOrWhiteSpace(IncomingPairInputPin))
        {
            IncomingPairErrorMessage = "请输入对方屏幕上显示的 6 位配对码";
            return;
        }

        string trimmed = IncomingPairInputPin.Trim();

        // 1. 若存在底层 Rust 引擎单连接握手 RequestId，优先走 Rust 核心原子鉴权
        if (!string.IsNullOrEmpty(IncomingPairRequestId))
        {
            int code = _service.RespondPair(IncomingPairRequestId, trimmed, true);
            if (code == 0)
            {
                IsIncomingPairRequestVisible = false;
                IncomingPairErrorMessage = string.Empty;
                StatusMessage = $"🎉 配对成功！已与【{IncomingPairDeviceName}】建立局域网加密连接";
                PairingPin = _service.PairingPin;
                RefreshDevicesCollections();
            }
            else if (code == 2)
            {
                IncomingPairErrorMessage = "配对码错误，请核对对方屏幕显示的 6 位数字！（连续输错 3 次将作废）";
            }
            else if (code == 3)
            {
                IsIncomingPairRequestVisible = false;
                IncomingPairErrorMessage = string.Empty;
                StatusMessage = "✕ 配对码连续输错 3 次，本次配对请求已作废，请重新发起配对";
            }
            else
            {
                IsIncomingPairRequestVisible = false;
                IncomingPairErrorMessage = string.Empty;
                StatusMessage = "配对请求已超时或失效，请让对方重新发起配对";
            }
            return;
        }

        // 2. 兼容/单元测试模式：比对内存预期 PIN 码
        if (!string.Equals(trimmed, IncomingPairExpectedPin.Trim(), StringComparison.Ordinal))
        {
            IncomingPairErrorMessage = "配对码错误，请核对对方屏幕显示的 6 位数字！";
            return;
        }

        PairingPin = IncomingPairExpectedPin;
        _service.UpdatePairingPin(IncomingPairExpectedPin);
        _service.AddOrUpdatePairedDevice(IncomingPairDeviceId, IncomingPairDeviceName, IncomingPairIp, IncomingPairPort);

        if (!string.IsNullOrEmpty(IncomingPairIp))
        {
            string myDeviceId = "muclip-win-" + Environment.MachineName;
            string myDeviceName = Environment.MachineName;
            _service.SendPairRequest(IncomingPairIp, IncomingPairPort, IncomingPairExpectedPin, myDeviceId, myDeviceName);
        }

        IsIncomingPairRequestVisible = false;
        IncomingPairErrorMessage = string.Empty;
        StatusMessage = $"🎉 配对成功！已与【{IncomingPairDeviceName}】建立局域网加密连接";
        RefreshDevicesCollections();
    }

    [RelayCommand]
    public void RejectIncomingPair()
    {
        _service.RespondPair(IncomingPairRequestId, string.Empty, false);
        IsIncomingPairRequestVisible = false;
        IncomingPairErrorMessage = string.Empty;
        StatusMessage = $"已拒绝来自【{IncomingPairDeviceName}】的配对请求";
    }

    [RelayCommand]
    public void UnpairDevice(PairedDeviceInfo? device)
    {
        if (device == null) return;
        _service.RemovePairedDevice(device.DeviceId);
        StatusMessage = $"已解除与 [{device.DeviceName}] 的配对";
        RefreshDevicesCollections();
    }

    [RelayCommand]
    public async Task TestSyncWithDeviceAsync(PairedDeviceInfo? device)
    {
        if (device == null) return;

        StatusMessage = $"正在测试与 [{device.DeviceName}] ({device.Endpoint}) 的连接...";
        string pingMsg = $"[MuClip 同步测试] 来自 {Environment.MachineName} ({DateTime.Now:HH:mm:ss})";

        var (latency, sentCount) = await Task.Run(() =>
        {
            try
            {
                int rtt = _service.TestPeerConnection(device.Ip, device.SyncPort);
                if (rtt < 0)
                {
                    return (-1, 0);
                }
                _service.AddRecord(pingMsg, broadcast: false);
                int sent = _service.Broadcast(pingMsg);
                return (rtt, sent);
            }
            catch
            {
                return (-1, 0);
            }
        });

        RefreshDevicesCollections();

        if (latency >= 0 && sentCount > 0)
        {
            StatusMessage = $"✓ 同步测试成功！已送达 [{device.DeviceName}] (延迟 {latency} ms)";
        }
        else
        {
            StatusMessage = $"✕ 向 [{device.DeviceName}] 发送测试失败（设备已离线或不可达）";
        }
    }

    [RelayCommand]
    public void AddManualPeer()
    {
        if (!string.IsNullOrWhiteSpace(ManualPeerInput))
        {
            string input = ManualPeerInput.Trim();
            _service.AddManualPeer(input);
            ManualPeerInput = string.Empty;
            UpdateDevicesCollectionsCore();
            StatusMessage = $"已添加映射节点: {input}";
        }
    }

    [RelayCommand]
    public void RemoveManualPeer(object? target)
    {
        string? key = target switch
        {
            PeerDeviceInfo peer => !string.IsNullOrWhiteSpace(peer.Endpoint) ? peer.Endpoint : peer.DeviceId,
            string s => s,
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(key))
        {
            _service.RemoveManualPeer(key.Trim());
            UpdateDevicesCollectionsCore();
            StatusMessage = $"已移除映射节点: {key.Trim()}";
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        _service.RefreshHistory();
        _service.RefreshPhrases();
        _service.RefreshPeers();
        StatusMessage = "已刷新剪贴板、常用语与设备列表";
    }

    // =========================================================================
    // 文件传输二级页面与操作命令
    // =========================================================================

    [RelayCommand]
    public void ToggleFileTransferView()
    {
        IsFileTransferViewActive = !IsFileTransferViewActive;
    }

    [RelayCommand]
    public void OpenFileTransferView()
    {
        IsFileTransferViewActive = true;
    }

    [RelayCommand]
    public void CloseFileTransferView()
    {
        IsFileTransferViewActive = false;
    }

    private void UpdateAvailableTransferTargets()
    {
        var targets = new System.Collections.Generic.List<TransferTargetDeviceItem>();

        // 优先添加已配对设备
        foreach (var p in PairedDevices)
        {
            if (!string.IsNullOrEmpty(p.Endpoint))
            {
                targets.Add(new TransferTargetDeviceItem
                {
                    DeviceId = p.DeviceId,
                    DeviceName = p.DeviceName,
                    Endpoint = p.Endpoint,
                    IsPaired = true,
                    IsOnline = p.IsOnline
                });
            }
        }

        // 接着添加已发现的未配对设备
        foreach (var p in PeerDevices)
        {
            string ep = p.Endpoint;
            if (!targets.Any(t => string.Equals(t.Endpoint, ep, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(t.DeviceId) && t.DeviceId == p.DeviceId)))
            {
                targets.Add(new TransferTargetDeviceItem
                {
                    DeviceId = p.DeviceId,
                    DeviceName = p.DeviceName,
                    Endpoint = ep,
                    IsPaired = false,
                    IsOnline = p.IsOnline
                });
            }
        }

        AvailableTransferTargets.Clear();
        foreach (var t in targets)
        {
            AvailableTransferTargets.Add(t);
        }

        if (SelectedTransferTarget == null || !AvailableTransferTargets.Any(t => t.Endpoint == SelectedTransferTarget.Endpoint))
        {
            SelectedTransferTarget = AvailableTransferTargets.FirstOrDefault(t => t.IsOnline && t.IsPaired)
                                  ?? AvailableTransferTargets.FirstOrDefault();
        }
    }

    private void OnFileProgress(FileProgressEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = FileTransfers.FirstOrDefault(t => t.TransferId == e.TransferId)
                       ?? (e.IsSend ? FileTransfers.FirstOrDefault(t => t.IsSend && string.IsNullOrEmpty(t.TransferId)) : null);
            if (item != null)
            {
                if (string.IsNullOrEmpty(item.TransferId)) item.TransferId = e.TransferId;
                item.UpdateProgress(e.TransferredBytes, e.TotalBytes, e.SpeedBps);
            }
            else
            {
                var newItem = new FileTransferItem
                {
                    TransferId = e.TransferId,
                    IsSend = e.IsSend,
                    FileName = e.IsSend ? "发送任务" : "接收文件",
                    TotalBytes = e.TotalBytes,
                    Status = TransferStatus.Transferring
                };
                newItem.UpdateProgress(e.TransferredBytes, e.TotalBytes, e.SpeedBps);
                FileTransfers.Insert(0, newItem);
            }
            ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
        });
    }

    private void OnFileCompleted(FileCompletedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = FileTransfers.FirstOrDefault(t => t.TransferId == e.TransferId)
                       ?? (e.IsSend ? FileTransfers.FirstOrDefault(t => t.IsSend && string.IsNullOrEmpty(t.TransferId)) : null);
            if (item != null)
            {
                if (string.IsNullOrEmpty(item.TransferId)) item.TransferId = e.TransferId;
                item.MarkCompleted(e.FilePath, e.Success, e.ErrorMessage);
                StatusMessage = e.Success
                    ? $"✓ 文件传输完成: {item.FileName}"
                    : $"✕ 文件传输失败: {e.ErrorMessage}";
            }
            else
            {
                var newItem = new FileTransferItem
                {
                    TransferId = e.TransferId,
                    FilePath = e.FilePath,
                    FileName = System.IO.Path.GetFileName(e.FilePath),
                    IsSend = e.IsSend
                };
                newItem.MarkCompleted(e.FilePath, e.Success, e.ErrorMessage);
                FileTransfers.Insert(0, newItem);
            }
            ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
        });
    }

    private void OnFileOfferReceived(FileOfferEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = FileTransfers.FirstOrDefault(t => t.TransferId == e.TransferId);
            if (item == null)
            {
                item = new FileTransferItem
                {
                    TransferId = e.TransferId,
                    FileName = e.FileName,
                    TotalBytes = e.FileSize,
                    IsSend = false,
                    PeerName = e.FromDeviceName,
                    Status = TransferStatus.Transferring
                };
                FileTransfers.Insert(0, item);
            }
            StatusMessage = $"📥 正在接收来自【{e.FromDeviceName}】的文件: {e.FileName} ({FileTransferItem.FormatFileSize(e.FileSize)})";
            ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
        });
    }

    [RelayCommand]
    public async Task PickSendFileAsync()
    {
        if (RequestPickFileFunc != null)
        {
            string? file = await RequestPickFileFunc.Invoke();
            if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
            {
                SelectedSendFilePath = file;
                SelectedSendFileName = System.IO.Path.GetFileName(file);
                var fi = new System.IO.FileInfo(file);
                SelectedSendFileSizeText = FileTransferItem.FormatFileSize((ulong)fi.Length);
                HasSelectedSendFile = true;
                StatusMessage = $"已选定文件: {SelectedSendFileName} ({SelectedSendFileSizeText})";
            }
        }
    }

    [RelayCommand]
    public void ClearSelectedSendFile()
    {
        SelectedSendFilePath = string.Empty;
        SelectedSendFileName = string.Empty;
        SelectedSendFileSizeText = string.Empty;
        HasSelectedSendFile = false;
    }

    [RelayCommand]
    public async Task StartSendFileAsync()
    {
        if (IsSendingFile) return;

        if (!HasSelectedSendFile || string.IsNullOrEmpty(SelectedSendFilePath) || !System.IO.File.Exists(SelectedSendFilePath))
        {
            StatusMessage = "⚠️ 请先选择要发送的本地文件";
            return;
        }

        string targetEndpoint = SelectedTransferTarget?.Endpoint ?? ManualTargetEndpoint.Trim();
        if (string.IsNullOrWhiteSpace(targetEndpoint))
        {
            StatusMessage = "⚠️ 请先选择目标设备或输入目标 IP 与端口";
            return;
        }

        string targetName = SelectedTransferTarget?.DeviceName ?? targetEndpoint;
        var fi = new System.IO.FileInfo(SelectedSendFilePath);
        ulong totalBytes = (ulong)fi.Length;
        string fileName = SelectedSendFileName;
        string filePath = SelectedSendFilePath;

        var transferItem = new FileTransferItem
        {
            FileName = fileName,
            FilePath = filePath,
            IsSend = true,
            PeerName = targetName,
            PeerEndpoint = targetEndpoint,
            TotalBytes = totalBytes,
            Status = TransferStatus.Transferring
        };

        FileTransfers.Insert(0, transferItem);
        ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
        IsSendingFile = true;
        StatusMessage = $"🚀 正在连接 [{targetName}] 并启动高速加密传输...";

        string? tid = await Task.Run(() => _service.SendFile(targetEndpoint, filePath));
        IsSendingFile = false;

        if (string.IsNullOrEmpty(tid))
        {
            transferItem.MarkCompleted(filePath, false, "连接目标设备失败或对方未开启监听");
            StatusMessage = $"✕ 向 [{targetName}] 发送文件失败";
        }
        else
        {
            transferItem.TransferId = tid;
            var dup = FileTransfers.FirstOrDefault(t => t != transferItem && t.TransferId == tid);
            if (dup != null)
            {
                transferItem.UpdateProgress(dup.TransferredBytes, dup.TotalBytes, dup.SpeedBps);
                if (dup.Status != TransferStatus.Transferring)
                {
                    transferItem.MarkCompleted(dup.FilePath, dup.Status == TransferStatus.Completed, dup.ErrorMessage);
                }
                FileTransfers.Remove(dup);
            }
            StatusMessage = $"🚀 已启动传输任务: {fileName} -> {targetName}";
        }
        ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
    }

    [RelayCommand]
    public void CancelTransfer(FileTransferItem? item)
    {
        if (item == null) return;
        if (!string.IsNullOrEmpty(item.TransferId))
        {
            _service.CancelFileTransfer(item.TransferId);
        }
        item.MarkCompleted("", false, "已手动取消传输");
        ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
        StatusMessage = $"已取消传输任务: {item.FileName}";
    }

    [RelayCommand]
    public void OpenItemFolder(FileTransferItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.FilePath)) return;
        try
        {
            string? dir = System.IO.File.Exists(item.FilePath)
                ? System.IO.Path.GetDirectoryName(item.FilePath)
                : System.IO.Directory.Exists(item.FilePath) ? item.FilePath : null;

            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.FilePath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开文件夹失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenItemFile(FileTransferItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开文件失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ClearCompletedTransfers()
    {
        var finished = FileTransfers.Where(t => t.Status != TransferStatus.Transferring).ToList();
        foreach (var item in finished)
        {
            FileTransfers.Remove(item);
        }
        StatusMessage = $"已清理 {finished.Count} 项已完成的历史传输任务";
    }

    [RelayCommand]
    public async Task BrowseSaveDirectoryAsync()
    {
        if (RequestPickFolderFunc != null)
        {
            string? dir = await RequestPickFolderFunc.Invoke();
            if (!string.IsNullOrEmpty(dir))
            {
                _service.SetFileSaveDirectory(dir);
                FileSaveDirectory = dir;
                StatusMessage = $"接收保存目录已更新: {dir}";
            }
        }
    }

    [RelayCommand]
    public void OpenSaveDirectory()
    {
        if (!string.IsNullOrEmpty(FileSaveDirectory) && System.IO.Directory.Exists(FileSaveDirectory))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{FileSaveDirectory}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"打开保存目录失败: {ex.Message}";
            }
        }
    }
}

public class PaletteColorItem
{
    public string Hex { get; }
    public string Name { get; }
    public IBrush Brush { get; }

    public PaletteColorItem(string hex, string name)
    {
        Hex = hex;
        Name = name;
        Brush = Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Gray;
    }
}
