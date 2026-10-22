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
    private int _phraseEditorCaretIndex = 0;

    [ObservableProperty]
    private string _cursorPositionText = "光标位置：第 0 字符处 (开头)";

    [ObservableProperty]
    private bool _isPhraseRuleEnabled;

    partial void OnIsPhraseRuleEnabledChanged(bool value)
    {
        if (!value)
        {
            foreach (var r in CurrentPhraseRules)
            {
                r.IsEnabled = false;
                r.RefreshState();
            }
        }
        else
        {
            if (CurrentPhraseRules.Count > 0 && CurrentPhraseRules.All(r => !r.IsEnabled))
            {
                foreach (var r in CurrentPhraseRules)
                {
                    r.IsEnabled = true;
                    r.RefreshState();
                }
            }
        }
        if (SelectedPhrase != null)
        {
            _service.SetPhraseRules(SelectedPhrase.Id, CurrentPhraseRules);
        }
        NotifyRuleProperties();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulePreviewOutputText))]
    private int _ruleInsertPosition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulePreviewOutputText))]
    private int _rulePositionMode = 1; // 0: 字符前插入, 1: 字符后追加

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleTimeStatusText))]
    [NotifyPropertyChangedFor(nameof(RulePreviewOutputText))]
    private string _ruleStartTime = "08:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleTimeStatusText))]
    [NotifyPropertyChangedFor(nameof(RulePreviewOutputText))]
    private string _ruleEndTime = "18:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleTimeStatusText))]
    [NotifyPropertyChangedFor(nameof(RulePreviewOutputText))]
    private string _ruleAppendText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingRule))]
    [NotifyPropertyChangedFor(nameof(RuleSubmitButtonText))]
    [NotifyPropertyChangedFor(nameof(RuleSubmitButtonIcon))]
    private string? _editingRuleId;

    public bool IsEditingRule => !string.IsNullOrEmpty(EditingRuleId);
    public string RuleSubmitButtonText => IsEditingRule ? "保存规则修改" : "添加为时段规则";
    public string RuleSubmitButtonIcon => IsEditingRule ? "💾" : "➕";
    public string CurrentRulesCountText => CurrentPhraseRules.Count > 0 ? $"({CurrentPhraseRules.Count} 条)" : "(0 条)";
    public bool HasRules => CurrentPhraseRules.Count > 0;

    public ObservableCollection<PhraseConditionalRule> CurrentPhraseRules { get; } = new();

    public ObservableCollection<PhraseRuleTemplate> SavedRuleTemplates { get; } = new();

    [ObservableProperty]
    private PhraseRuleTemplate? _selectedRuleTemplate;

    [ObservableProperty]
    private string _newTemplateCustomName = string.Empty;

    public string RuleTimeStatusText
    {
        get
        {
            // 1. 编辑状态：处于时段规则编辑或正在表单中输入追加文本
            if (IsEditingRule || !string.IsNullOrWhiteSpace(RuleAppendText))
            {
                string start = string.IsNullOrWhiteSpace(RuleStartTime) ? "08:00" : RuleStartTime.Trim();
                string end = string.IsNullOrWhiteSpace(RuleEndTime) ? "18:00" : RuleEndTime.Trim();
                return $"✏ 正在预览编辑时段 ({start} ~ {end})";
            }

            // 2. 平时：未开启或无规则
            if (!IsPhraseRuleEnabled || CurrentPhraseRules.Count == 0)
            {
                return "⚪ 动态条件追加已关闭";
            }

            // 3. 平时：按当前实时时段预览，检查当前时钟是否命中规则
            var matchedRule = CurrentPhraseRules.FirstOrDefault(r => r.IsEnabled && r.IsTimeMatched(DateTime.Now));
            if (matchedRule != null)
            {
                return $"🎯 实时时段预览 (当前命中: {matchedRule.TimeRangeText})";
            }

            return "🎯 实时时段预览 (当前时段未命中任何规则)";
        }
    }

    public string RulePreviewOutputText
    {
        get
        {
            string raw = PhraseEditorText ?? string.Empty;

            // 1. 编辑状态：只预览当前正在编辑/填写的那个时段规则，不与其它时段混杂
            if (IsEditingRule || !string.IsNullOrWhiteSpace(RuleAppendText))
            {
                var editingRule = BuildCurrentRule();
                return editingRule.ApplyUnconditional(raw);
            }

            // 2. 平时：按当前实时时段真实预览生效结果
            if (!IsPhraseRuleEnabled || CurrentPhraseRules.Count == 0)
            {
                return string.IsNullOrEmpty(raw) ? "(无内容)" : raw;
            }

            return PhraseConditionalRule.ApplyRules(raw, CurrentPhraseRules, DateTime.Now);
        }
    }

    public void NotifyRuleProperties()
    {
        OnPropertyChanged(nameof(IsEditingRule));
        OnPropertyChanged(nameof(RuleSubmitButtonText));
        OnPropertyChanged(nameof(RuleSubmitButtonIcon));
        OnPropertyChanged(nameof(CurrentRulesCountText));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(RuleTimeStatusText));
        OnPropertyChanged(nameof(RulePreviewOutputText));
    }

    private PhraseConditionalRule BuildCurrentRule() => new()
    {
        Id = EditingRuleId ?? Guid.NewGuid().ToString("N"),
        IsEnabled = true,
        InsertPosition = RuleInsertPosition,
        PositionMode = RulePositionMode,
        StartTime = string.IsNullOrWhiteSpace(RuleStartTime) ? "08:00" : RuleStartTime.Trim(),
        EndTime = string.IsNullOrWhiteSpace(RuleEndTime) ? "18:00" : RuleEndTime.Trim(),
        AppendText = RuleAppendText ?? string.Empty
    };

    partial void OnPhraseEditorTextChanged(string value)
    {
        UpdateCursorPositionText();
        OnPropertyChanged(nameof(RulePreviewOutputText));
    }

    partial void OnPhraseEditorCaretIndexChanged(int value)
    {
        UpdateCursorPositionText();
    }

    private void UpdateCursorPositionText()
    {
        int len = PhraseEditorText?.Length ?? 0;
        int clamped = Math.Clamp(PhraseEditorCaretIndex, 0, len);
        if (len == 0)
        {
            CursorPositionText = "光标位置：文本为空 (第 0 字符)";
        }
        else if (clamped == 0)
        {
            CursorPositionText = $"光标位置：第 0 字符处 (文本开头，共 {len} 字符)";
        }
        else if (clamped >= len)
        {
            CursorPositionText = $"光标位置：第 {len} 字符处 (文本最末尾，共 {len} 字符)";
        }
        else
        {
            char prev = PhraseEditorText![clamped - 1];
            char next = PhraseEditorText[clamped];
            string prevDisplay = prev == '\n' ? "\\n" : (prev == '\r' ? "\\r" : prev.ToString());
            string nextDisplay = next == '\n' ? "\\n" : (next == '\r' ? "\\r" : next.ToString());
            CursorPositionText = $"光标位置：第 {clamped} 字符处 ('{prevDisplay}' 与 '{nextDisplay}' 之间，共 {len} 字符)";
        }
    }

    private void ResetRuleToDefaults()
    {
        EditingRuleId = null;
        IsPhraseRuleEnabled = false;
        RuleInsertPosition = 0;
        RulePositionMode = 1;
        RuleStartTime = "08:00";
        RuleEndTime = "18:00";
        RuleAppendText = string.Empty;
        NotifyRuleProperties();
    }

    private void ResetRuleForm()
    {
        EditingRuleId = null;
        RuleInsertPosition = 0;
        RulePositionMode = 1;
        RuleStartTime = "08:00";
        RuleEndTime = "18:00";
        RuleAppendText = string.Empty;
        NotifyRuleProperties();
    }

    [ObservableProperty]
    private int _phrasesCount;

    public bool HasSelectedPhrase => SelectedPhrase != null && !IsAddingNewPhrase;
    public string PhraseDetailTitle => IsAddingNewPhrase ? "➕ 新增常用语" : "💬 常用语详情与编辑";

    partial void OnSelectedPhraseChanged(MuClipPhraseItem? value)
    {
        CurrentPhraseRules.Clear();
        EditingRuleId = null;

        if (value != null)
        {
            IsAddingNewPhrase = false;
            PhraseEditorText = value.Content;
            PhraseEditorCaretIndex = value.Content.Length;

            var rules = _service.GetPhraseRules(value.Id);
            foreach (var r in rules)
            {
                CurrentPhraseRules.Add(r);
            }
            IsPhraseRuleEnabled = CurrentPhraseRules.Count > 0 && CurrentPhraseRules.Any(r => r.IsEnabled);
            if (rules.Count > 0)
            {
                var first = rules[0];
                RuleInsertPosition = first.InsertPosition;
                RulePositionMode = first.PositionMode;
                RuleStartTime = first.StartTime;
                RuleEndTime = first.EndTime;
                RuleAppendText = first.AppendText;
            }
            else
            {
                ResetRuleForm();
            }
        }
        else if (!IsAddingNewPhrase)
        {
            PhraseEditorText = string.Empty;
            PhraseEditorCaretIndex = 0;
            ResetRuleToDefaults();
        }
        UpdateCursorPositionText();
        NotifyRuleProperties();
    }

    [ObservableProperty]
    private bool _isSyncPanelVisible;

    [ObservableProperty]
    private string _pairingPin = string.Empty;

    [ObservableProperty]
    private string _manualPeerInput = string.Empty;

    [ObservableProperty]
    private string _customSyncPortText = "42424";

    [ObservableProperty]
    private string _portStatusMessage = string.Empty;

    [ObservableProperty]
    private string _syncStatusText = "局域网同步未连接";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOnlinePeers))]
    private int _onlinePeerCount;

    public bool HasOnlinePeers => OnlinePeerCount > 0;

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

    // 页面与子视图索引 (0: 剪贴板主页, 1: 文件传输二级页面, 2: 局域网同步二级页面)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainViewActive))]
    [NotifyPropertyChangedFor(nameof(IsFileTransferViewActive))]
    [NotifyPropertyChangedFor(nameof(IsLanSyncViewActive))]
    private int _currentViewIndex = 0;

    public bool IsMainViewActive => CurrentViewIndex == 0;
    public bool IsFileTransferViewActive
    {
        get => CurrentViewIndex == 1;
        set => CurrentViewIndex = value ? 1 : 0;
    }
    public bool IsLanSyncViewActive
    {
        get => CurrentViewIndex == 2;
        set => CurrentViewIndex = value ? 2 : 0;
    }

    // 文件传输日志区
    [ObservableProperty]
    private string _transferLogText = string.Empty;

    private bool _hasInitializedTransferLog = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveTransfers))]
    private int _activeTransferCount;

    public bool HasActiveTransfers => ActiveTransferCount > 0;

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
    public Func<Task<IReadOnlyList<string>?>>? RequestPickFilesFunc { get; set; }
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

    public string DataDirectory => _service.DataDirectory;
    public string DatabasePath => _service.DatabasePath;

    [RelayCommand]
    public void OpenDataDirectory()
    {
        try
        {
            if (!Directory.Exists(DataDirectory))
            {
                Directory.CreateDirectory(DataDirectory);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DataDirectory,
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    public void CopyDataDirectoryPath()
    {
        try
        {
            _service.CopyToSystemClipboard(DataDirectory);
        }
        catch { }
    }

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

    partial void OnCurrentViewIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMainViewActive));
        OnPropertyChanged(nameof(IsFileTransferViewActive));
        OnPropertyChanged(nameof(IsLanSyncViewActive));
        UpdateForegroundDiscoveryLoop();
        if (value == 1)
        {
            EnsureTransferLogInitialized();
            UpdateAvailableTransferTargets();
        }
        else if (value == 2)
        {
            UpdateAvailableTransferTargets();
        }
    }

    public void AppendTransferLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{timestamp}] {message}";
        if (string.IsNullOrEmpty(TransferLogText))
        {
            TransferLogText = line;
        }
        else
        {
            TransferLogText = TransferLogText + Environment.NewLine + line;
        }
    }

    public void EnsureTransferLogInitialized()
    {
        if (!_hasInitializedTransferLog)
        {
            _hasInitializedTransferLog = true;
            AppendTransferLog("128KB切片 · XChaCha20 · 100MB/s+");
            AppendTransferLog($"接收目录: {FileSaveDirectory}");
        }
        else
        {
            AppendTransferLog($"接收目录: {FileSaveDirectory}");
        }
    }

    [RelayCommand]
    public void ClearTransferLog()
    {
        TransferLogText = string.Empty;
    }

    partial void OnIsSyncPanelVisibleChanged(bool value)
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
        bool shouldScan = _isHostViewActive && AutoDiscoveryEnabled && (IsLanSyncViewActive || IsFileTransferViewActive || IsSyncPanelVisible);

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
        CustomSyncPortText = _service.SyncPort.ToString();
        RefreshDevicesCollections();
        UpdateSyncStatus();
        LoadPhrases();
        LoadRuleTemplates();
        LoadItems();
    }

    private void OnHistoryChanged()
    {
        Dispatcher.UIThread.Post(LoadItems);
    }

    private bool _isReorderingInternally;

    private void OnPhrasesChanged()
    {
        if (_isReorderingInternally) return;
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
        bool needRebuildPaired = pairedList.Count != PairedDevices.Count ||
            !pairedList.Select(p => p.DeviceId).SequenceEqual(PairedDevices.Select(p => p.DeviceId));
        if (needRebuildPaired)
        {
            PairedDevices.Clear();
            foreach (var p in pairedList)
            {
                PairedDevices.Add(p);
            }
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
        PhraseEditorCaretIndex = 0;
        CurrentPhraseRules.Clear();
        ResetRuleForm();
        IsPhraseRuleEnabled = false;
        UpdateCursorPositionText();
        NotifyRuleProperties();
        StatusMessage = "请在右侧输入新常用语内容并点击「保存」";
    }

    [RelayCommand]
    public void UseCurrentCaretPosition()
    {
        RuleInsertPosition = PhraseEditorCaretIndex;
        StatusMessage = $"✓ 已填入当前光标位置：第 {RuleInsertPosition} 个字符";
    }

    [RelayCommand]
    public void SetPositionToStart()
    {
        RuleInsertPosition = 0;
        RulePositionMode = 0;
        StatusMessage = "✓ 已设为在文本开头插入";
    }

    [RelayCommand]
    public void SetPositionToEnd()
    {
        RuleInsertPosition = 9999;
        RulePositionMode = 1;
        StatusMessage = "✓ 已设为在文本最末尾追加";
    }

    [RelayCommand]
    public void SetPresetTimeRange(string? preset)
    {
        switch (preset?.ToLowerInvariant())
        {
            case "early_morning":
            case "zaoshang":
                RuleStartTime = "06:00";
                RuleEndTime = "10:00";
                break;
            case "morning":
            case "shangwu":
                RuleStartTime = "10:00";
                RuleEndTime = "12:00";
                break;
            case "noon":
            case "zhongwu":
                RuleStartTime = "12:00";
                RuleEndTime = "14:00";
                break;
            case "afternoon":
            case "xiawu":
                RuleStartTime = "14:00";
                RuleEndTime = "18:00";
                break;
            case "evening":
            case "wanshang":
                RuleStartTime = "18:00";
                RuleEndTime = "22:00";
                break;
            case "night":
            case "yejian":
                RuleStartTime = "22:00";
                RuleEndTime = "06:00";
                break;
            case "all_day":
            case "allday":
                RuleStartTime = "00:00";
                RuleEndTime = "23:59";
                break;
        }
        StatusMessage = $"✓ 已设为生效时段: {RuleStartTime} ~ {RuleEndTime}";
    }

    [RelayCommand]
    public void AddOrUpdateRule()
    {
        if (string.IsNullOrWhiteSpace(RuleAppendText))
        {
            StatusMessage = "⚠️ 请输入满足条件时追加的文字内容";
            return;
        }

        string startTime = string.IsNullOrWhiteSpace(RuleStartTime) ? "08:00" : RuleStartTime.Trim();
        string endTime = string.IsNullOrWhiteSpace(RuleEndTime) ? "18:00" : RuleEndTime.Trim();
        string appendText = RuleAppendText.Trim();

        if (IsEditingRule)
        {
            var existing = CurrentPhraseRules.FirstOrDefault(r => r.Id == EditingRuleId);
            if (existing != null)
            {
                existing.InsertPosition = RuleInsertPosition;
                existing.PositionMode = RulePositionMode;
                existing.StartTime = startTime;
                existing.EndTime = endTime;
                existing.AppendText = appendText;
                existing.IsEnabled = true;
                existing.RefreshState();
                StatusMessage = $"✓ 已保存时段 ({startTime} ~ {endTime}) 规则修改";
            }
            EditingRuleId = null;
        }
        else
        {
            var newRule = new PhraseConditionalRule
            {
                IsEnabled = true,
                InsertPosition = RuleInsertPosition,
                PositionMode = RulePositionMode,
                StartTime = startTime,
                EndTime = endTime,
                AppendText = appendText
            };
            CurrentPhraseRules.Add(newRule);
            StatusMessage = $"✓ 已添加新时段规则: {startTime} ~ {endTime}";
        }

        IsPhraseRuleEnabled = true;

        if (SelectedPhrase != null)
        {
            _service.SetPhraseRules(SelectedPhrase.Id, CurrentPhraseRules);
        }

        ResetRuleForm();
        NotifyRuleProperties();
    }

    [RelayCommand]
    public void EditRule(PhraseConditionalRule? rule)
    {
        if (rule == null) return;
        EditingRuleId = rule.Id;
        RuleInsertPosition = rule.InsertPosition;
        RulePositionMode = rule.PositionMode;
        RuleStartTime = rule.StartTime;
        RuleEndTime = rule.EndTime;
        RuleAppendText = rule.AppendText;
        StatusMessage = $"正在编辑时段 {rule.TimeRangeText} 规则，修改后请点击「保存」";
        NotifyRuleProperties();
    }

    [RelayCommand]
    public void CancelEditRule()
    {
        EditingRuleId = null;
        ResetRuleForm();
        StatusMessage = "已取消编辑规则";
        NotifyRuleProperties();
    }

    [RelayCommand]
    public void DeleteRule(PhraseConditionalRule? rule)
    {
        if (rule == null) return;
        if (EditingRuleId == rule.Id)
        {
            EditingRuleId = null;
            ResetRuleForm();
        }
        CurrentPhraseRules.Remove(rule);
        if (SelectedPhrase != null)
        {
            _service.SetPhraseRules(SelectedPhrase.Id, CurrentPhraseRules);
        }
        if (CurrentPhraseRules.Count == 0)
        {
            IsPhraseRuleEnabled = false;
        }
        StatusMessage = $"已删除时段规则 ({rule.TimeRangeText})";
        NotifyRuleProperties();
    }

    [RelayCommand]
    public void ToggleRule(PhraseConditionalRule? rule)
    {
        if (rule == null) return;
        rule.IsEnabled = !rule.IsEnabled;
        rule.RefreshState();
        if (SelectedPhrase != null)
        {
            _service.SetPhraseRules(SelectedPhrase.Id, CurrentPhraseRules);
        }
        IsPhraseRuleEnabled = CurrentPhraseRules.Any(r => r.IsEnabled);
        StatusMessage = rule.IsEnabled ? $"已启用规则 ({rule.TimeRangeText})" : $"已停用规则 ({rule.TimeRangeText})";
        NotifyRuleProperties();
    }

    public bool HasRuleTemplates => SavedRuleTemplates.Count > 0;

    public void LoadRuleTemplates()
    {
        var templates = _service.GetRuleTemplates();
        SavedRuleTemplates.Clear();
        foreach (var tpl in templates)
        {
            SavedRuleTemplates.Add(tpl);
        }
        if (SavedRuleTemplates.Count > 0 && SelectedRuleTemplate == null)
        {
            SelectedRuleTemplate = SavedRuleTemplates[0];
        }
        OnPropertyChanged(nameof(HasRuleTemplates));
    }

    [RelayCommand]
    public void ApplySelectedTemplate()
    {
        if (SelectedRuleTemplate != null)
        {
            ApplyRuleTemplate(SelectedRuleTemplate);
        }
        else if (SavedRuleTemplates.Count > 0)
        {
            ApplyRuleTemplate(SavedRuleTemplates[0]);
        }
    }

    [RelayCommand]
    public void ApplyRuleTemplate(PhraseRuleTemplate? template)
    {
        if (template == null) return;
        RuleInsertPosition = template.InsertPosition;
        RulePositionMode = template.PositionMode;
        RuleStartTime = template.StartTime;
        RuleEndTime = template.EndTime;
        RuleAppendText = template.AppendText;
        StatusMessage = $"✓ 已套用模板「{template.Name}」到规则输入栏";
        NotifyRuleProperties();
    }

    [RelayCommand]
    public void SaveCurrentAsTemplate(string? nameInput = null)
    {
        string name = !string.IsNullOrWhiteSpace(nameInput)
            ? nameInput.Trim()
            : (!string.IsNullOrWhiteSpace(NewTemplateCustomName) ? NewTemplateCustomName.Trim() : string.Empty);

        if (string.IsNullOrWhiteSpace(name))
        {
            name = string.IsNullOrWhiteSpace(RuleAppendText)
                ? $"时段规则 ({RuleStartTime}~{RuleEndTime})"
                : $"{RuleAppendText.Trim()} ({RuleStartTime}~{RuleEndTime})";
        }

        var tpl = new PhraseRuleTemplate
        {
            Name = name,
            InsertPosition = RuleInsertPosition,
            PositionMode = RulePositionMode,
            StartTime = string.IsNullOrWhiteSpace(RuleStartTime) ? "08:00" : RuleStartTime.Trim(),
            EndTime = string.IsNullOrWhiteSpace(RuleEndTime) ? "18:00" : RuleEndTime.Trim(),
            AppendText = RuleAppendText ?? string.Empty
        };

        _service.SaveRuleTemplate(tpl);
        LoadRuleTemplates();
        SelectedRuleTemplate = SavedRuleTemplates.FirstOrDefault(t => t.Id == tpl.Id) ?? SavedRuleTemplates.FirstOrDefault();
        NewTemplateCustomName = string.Empty;
        StatusMessage = $"✓ 规则已保存为常用模板「{name}」，下次新增可直接套用！";
    }

    [RelayCommand]
    public void SaveRuleAsTemplate(PhraseConditionalRule? rule)
    {
        if (rule == null) return;
        string name = string.IsNullOrWhiteSpace(rule.AppendText)
            ? $"规则 ({rule.TimeRangeText})"
            : $"{rule.AppendText} ({rule.TimeRangeText})";

        var tpl = PhraseRuleTemplate.FromRule(rule, name);
        _service.SaveRuleTemplate(tpl);
        LoadRuleTemplates();
        SelectedRuleTemplate = SavedRuleTemplates.FirstOrDefault(t => t.Id == tpl.Id) ?? SavedRuleTemplates.FirstOrDefault();
        StatusMessage = $"✓ 已将规则保存为常用模板「{name}」";
    }

    [RelayCommand]
    public void DeleteRuleTemplate(PhraseRuleTemplate? template)
    {
        var target = template ?? SelectedRuleTemplate;
        if (target == null) return;
        string name = target.Name;
        _service.DeleteRuleTemplate(target.Id);
        LoadRuleTemplates();
        SelectedRuleTemplate = SavedRuleTemplates.FirstOrDefault();
        StatusMessage = $"已删除规则模板「{name}」";
    }

    [ObservableProperty]
    private string _managerTemplateName = string.Empty;

    [ObservableProperty]
    private string _managerTemplateStartTime = "08:00";

    [ObservableProperty]
    private string _managerTemplateEndTime = "18:00";

    [ObservableProperty]
    private int _managerTemplatePositionMode = 1; // 0: 开头, 1: 末尾

    [ObservableProperty]
    private string _managerTemplateAppendText = string.Empty;

    [RelayCommand]
    public void SetManagerPresetPosition(string mode)
    {
        ManagerTemplatePositionMode = mode == "start" ? 0 : 1;
    }

    [RelayCommand]
    public void SetManagerPresetTime(string preset)
    {
        (ManagerTemplateStartTime, ManagerTemplateEndTime) = preset switch
        {
            "early_morning" => ("06:00", "10:00"),
            "morning" => ("10:00", "12:00"),
            "noon" => ("12:00", "14:00"),
            "afternoon" => ("14:00", "18:00"),
            "evening" => ("18:00", "22:00"),
            "night" => ("22:00", "06:00"),
            "work" => ("09:00", "18:00"),
            "all_day" => ("00:00", "23:59"),
            _ => ("08:00", "18:00")
        };
    }

    [RelayCommand]
    public void AddTemplateFromManager()
    {
        string text = ManagerTemplateAppendText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = "⚠️ 请输入追加文字内容后再保存模板";
            return;
        }

        string name = string.IsNullOrWhiteSpace(ManagerTemplateName)
            ? $"{text} ({ManagerTemplateStartTime}~{ManagerTemplateEndTime})"
            : ManagerTemplateName.Trim();

        var tpl = new PhraseRuleTemplate
        {
            Name = name,
            InsertPosition = ManagerTemplatePositionMode == 1 ? 9999 : 0,
            PositionMode = ManagerTemplatePositionMode,
            StartTime = string.IsNullOrWhiteSpace(ManagerTemplateStartTime) ? "08:00" : ManagerTemplateStartTime.Trim(),
            EndTime = string.IsNullOrWhiteSpace(ManagerTemplateEndTime) ? "18:00" : ManagerTemplateEndTime.Trim(),
            AppendText = text
        };

        _service.SaveRuleTemplate(tpl);
        LoadRuleTemplates();
        SelectedRuleTemplate = SavedRuleTemplates.FirstOrDefault(t => t.Id == tpl.Id) ?? SavedRuleTemplates.FirstOrDefault();
        ManagerTemplateName = string.Empty;
        ManagerTemplateAppendText = string.Empty;
        StatusMessage = $"✓ 常用语规则模板「{name}」已添加并保存！";
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

        long targetId;
        if (IsAddingNewPhrase || SelectedPhrase == null)
        {
            targetId = _service.AddPhrase(text);
            IsAddingNewPhrase = false;
            LoadPhrases();
            SyncHistoryFavoriteStates();
            SelectedPhrase = FilteredPhrases.FirstOrDefault(p => p.Id == targetId) ?? FilteredPhrases.FirstOrDefault();
            StatusMessage = "✓ 已新增常用语";
        }
        else
        {
            targetId = SelectedPhrase.Id;
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

        // 若用户在表单填写了追加文本但未点击添加规则，且当前没有任何规则，则自动加入
        if (!string.IsNullOrWhiteSpace(RuleAppendText) && CurrentPhraseRules.Count == 0)
        {
            var autoRule = BuildCurrentRule();
            CurrentPhraseRules.Add(autoRule);
            IsPhraseRuleEnabled = true;
            ResetRuleForm();
        }

        // 保存多条条件追加规则列表
        _service.SetPhraseRules(targetId, CurrentPhraseRules);
        NotifyRuleProperties();
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
    public void StartEditPhrase(MuClipPhraseItem? phrase)
    {
        if (phrase == null) return;
        SelectedPhrase = phrase;
        StatusMessage = "已选中常用语，可在右侧编辑内容与规则";
    }

    [RelayCommand]
    public void StartEditItem(MuClipItem? item)
    {
        if (item == null) return;
        SelectedItem = item;
    }

    public void CommitPhrasesReordered()
    {
        _isReorderingInternally = true;
        try
        {
            _service.ReorderPhrases(FilteredPhrases.Select(x => x.Id).ToList());
        }
        finally
        {
            _isReorderingInternally = false;
        }
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

    [ObservableProperty]
    private string _cleanupStartTimeText = string.Empty;

    [ObservableProperty]
    private string _cleanupEndTimeText = string.Empty;

    [ObservableProperty]
    private string _cleanupStatusPrompt = "可选择上方快捷预设或输入时间后点击确认清理";

    [RelayCommand]
    public void SetCleanupPreset(string? preset)
    {
        switch (preset?.ToLowerInvariant())
        {
            case "1h":
                CleanupStartTimeText = string.Empty;
                CleanupEndTimeText = DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");
                CleanupStatusPrompt = $"将清理 1 小时前 (早于 {CleanupEndTimeText}) 的历史记录";
                break;
            case "24h":
                CleanupStartTimeText = string.Empty;
                CleanupEndTimeText = DateTime.Now.AddHours(-24).ToString("yyyy-MM-dd HH:mm:ss");
                CleanupStatusPrompt = $"将清理 24 小时前 (早于 {CleanupEndTimeText}) 的历史记录";
                break;
            case "3d":
                CleanupStartTimeText = string.Empty;
                CleanupEndTimeText = DateTime.Now.AddDays(-3).ToString("yyyy-MM-dd HH:mm:ss");
                CleanupStatusPrompt = $"将清理 3 天前 (早于 {CleanupEndTimeText}) 的历史记录";
                break;
            case "7d":
                CleanupStartTimeText = string.Empty;
                CleanupEndTimeText = DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd HH:mm:ss");
                CleanupStatusPrompt = $"将清理 7 天前 (早于 {CleanupEndTimeText}) 的历史记录";
                break;
            case "30d":
                CleanupStartTimeText = string.Empty;
                CleanupEndTimeText = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd HH:mm:ss");
                CleanupStatusPrompt = $"将清理 30 天前 (早于 {CleanupEndTimeText}) 的历史记录";
                break;
            case "today":
                CleanupStartTimeText = string.Empty;
                CleanupEndTimeText = DateTime.Today.ToString("yyyy-MM-dd 00:00:00");
                CleanupStatusPrompt = "将清理今天 00:00 之前产生的历史记录";
                break;
        }
    }

    [RelayCommand]
    public void SetCleanupRangeShortcut(string? shortcut)
    {
        switch (shortcut?.ToLowerInvariant())
        {
            case "today_only":
                CleanupStartTimeText = DateTime.Today.ToString("yyyy-MM-dd 00:00:00");
                CleanupEndTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                CleanupStatusPrompt = "将清理今天内产生的所有历史记录";
                break;
            case "yesterday_only":
                CleanupStartTimeText = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd 00:00:00");
                CleanupEndTimeText = DateTime.Today.ToString("yyyy-MM-dd 00:00:00");
                CleanupStatusPrompt = "将清理昨天整天产生的所有历史记录";
                break;
            case "this_week":
                int diff = (7 + (DateTime.Today.DayOfWeek - DayOfWeek.Monday)) % 7;
                var monday = DateTime.Today.AddDays(-1 * diff);
                CleanupStartTimeText = monday.ToString("yyyy-MM-dd 00:00:00");
                CleanupEndTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                CleanupStatusPrompt = "将清理本周内产生的所有历史记录";
                break;
        }
    }

    [RelayCommand]
    public void ClearCleanupInputs()
    {
        CleanupStartTimeText = string.Empty;
        CleanupEndTimeText = string.Empty;
        CleanupStatusPrompt = "已清空时间条件 (全部不限时将清空所有历史)";
    }

    [RelayCommand]
    public void ExecuteCleanupByRange()
    {
        DateTime? startTime = null;
        DateTime? endTime = null;

        if (!string.IsNullOrWhiteSpace(CleanupStartTimeText))
        {
            if (DateTime.TryParse(CleanupStartTimeText.Trim(), out var st))
            {
                startTime = st;
            }
            else
            {
                CleanupStatusPrompt = "⚠️ 起始时间格式无效，请参考 2026-10-01 00:00:00";
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(CleanupEndTimeText))
        {
            if (DateTime.TryParse(CleanupEndTimeText.Trim(), out var et))
            {
                endTime = et;
            }
            else
            {
                CleanupStatusPrompt = "⚠️ 结束时间格式无效，请参考 2026-10-20 12:00:00";
                return;
            }
        }

        if (startTime.HasValue && endTime.HasValue && startTime.Value > endTime.Value)
        {
            CleanupStatusPrompt = "⚠️ 起始时间不能晚于结束时间";
            return;
        }

        int deleted = _service.DeleteHistoryByTimeRange(startTime, endTime);
        StatusMessage = $"✓ 已按时间区间批量清理 {deleted} 条历史记录";
        CleanupStatusPrompt = $"✓ 上次清理成功：共删除 {deleted} 条历史记录";
        LoadItems();
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
        CurrentViewIndex = (CurrentViewIndex == 2) ? 0 : 2;
    }

    [RelayCommand]
    public void OpenLanSyncView()
    {
        CurrentViewIndex = 2;
    }

    [RelayCommand]
    public void CloseLanSyncView()
    {
        CurrentViewIndex = 0;
    }

    [RelayCommand]
    public void BackToMainView()
    {
        CurrentViewIndex = 0;
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
    public void ApplyCustomSyncPort()
    {
        if (ushort.TryParse(CustomSyncPortText.Trim(), out var port) && port >= 1024)
        {
            bool success = _service.ChangeSyncPort(port);
            if (success)
            {
                PortStatusMessage = $"✓ 监听端口已更新为 {port}";
                StatusMessage = $"局域网同步监听端口已更新为 {port}";
                SyncStatusText = $"局域网加密同步已运行 (端口: {port})，已配对: {PairedDeviceCount} 台 (在线 {OnlinePairedCount} 台)";
            }
            else
            {
                PortStatusMessage = $"✕ 绑定端口 {port} 失败，可能端口被占用";
            }
        }
        else
        {
            PortStatusMessage = "⚠️ 请输入有效端口 (1024 - 65535)";
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

        device.IsTestingLatency = true;
        device.LatencySuccess = null;
        device.LatencyFeedback = "⏳ 测速中...";
        StatusMessage = $"正在测试与 [{device.DeviceName}] ({device.Endpoint}) 的连接延迟...";

        int latency = await Task.Run(() =>
        {
            try
            {
                return _service.TestPeerConnection(device.Ip, device.SyncPort);
            }
            catch
            {
                return -1;
            }
        });

        device.IsTestingLatency = false;

        if (latency >= 0)
        {
            device.IsOnline = true;
            device.LatencySuccess = true;
            device.LatencyFeedback = $"⚡ {latency} ms (正常)";
            StatusMessage = $"✓ 连接正常！与 [{device.DeviceName}] 的延迟: {latency} ms";
        }
        else
        {
            device.IsOnline = false;
            device.LatencySuccess = false;
            device.LatencyFeedback = "✕ 连接超时 (离线)";
            StatusMessage = $"✕ 与 [{device.DeviceName}] 连接测试失败（设备已离线或不可达）";
        }

        RefreshDevicesCollections();
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
        CurrentViewIndex = (CurrentViewIndex == 1) ? 0 : 1;
    }

    [RelayCommand]
    public void OpenFileTransferView()
    {
        if (CurrentViewIndex == 1)
        {
            EnsureTransferLogInitialized();
        }
        else
        {
            CurrentViewIndex = 1;
        }
    }

    [RelayCommand]
    public void CloseFileTransferView()
    {
        CurrentViewIndex = 0;
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

        // 记住先前勾选的设备端点
        var selectedEndpoints = AvailableTransferTargets
            .Where(t => t.IsSelected)
            .Select(t => t.Endpoint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AvailableTransferTargets.Clear();
        foreach (var t in targets)
        {
            if (selectedEndpoints.Contains(t.Endpoint))
            {
                t.IsSelected = true;
            }
            AvailableTransferTargets.Add(t);
        }

        // 若没有任何设备被勾选，优先自动勾选第一个在线设备
        if (!AvailableTransferTargets.Any(t => t.IsSelected))
        {
            var defaultTarget = AvailableTransferTargets.FirstOrDefault(t => t.IsOnline && t.IsPaired)
                             ?? AvailableTransferTargets.FirstOrDefault(t => t.IsOnline);
            if (defaultTarget != null)
            {
                defaultTarget.IsSelected = true;
            }
        }

        SelectedTransferTarget = AvailableTransferTargets.FirstOrDefault(t => t.IsSelected)
                              ?? AvailableTransferTargets.FirstOrDefault();
    }

    private void OnFileProgress(FileProgressEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = FileTransfers.FirstOrDefault(t => t.TransferId == e.TransferId)
                       ?? (e.IsSend ? FileTransfers.FirstOrDefault(t => t.IsSend && string.IsNullOrEmpty(t.TransferId) && t.Status == TransferStatus.Transferring) : null);
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
                       ?? (e.IsSend ? FileTransfers.FirstOrDefault(t => t.IsSend && string.IsNullOrEmpty(t.TransferId) && t.Status == TransferStatus.Transferring) : null);
            if (item != null)
            {
                if (string.IsNullOrEmpty(item.TransferId)) item.TransferId = e.TransferId;
                item.MarkCompleted(e.FilePath, e.Success, e.ErrorMessage);
                StatusMessage = e.Success
                    ? $"✓ 文件传输完成: {item.FileName}"
                    : $"✕ 文件传输失败: {e.ErrorMessage}";
                if (e.Success)
                {
                    AppendTransferLog($"✓ 传输完成: {item.FileName}");
                }
                else
                {
                    AppendTransferLog($"✕ 传输失败: {item.FileName} - {e.ErrorMessage}");
                }
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
                if (e.Success)
                {
                    AppendTransferLog($"✓ 传输完成: {newItem.FileName}");
                }
                else
                {
                    AppendTransferLog($"✕ 传输失败: {newItem.FileName} - {e.ErrorMessage}");
                }
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
            AppendTransferLog($"📥 收到传输请求: {e.FileName} ({FileTransferItem.FormatFileSize(e.FileSize)}) 来自 {e.FromDeviceName}");
            ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
        });
    }

    [RelayCommand]
    public async Task PickSendFileAsync()
    {
        if (RequestPickFilesFunc != null)
        {
            var files = await RequestPickFilesFunc.Invoke();
            if (files != null && files.Count > 0)
            {
                AddFilesToTransferQueue(files);
                return;
            }
        }
        else if (RequestPickFileFunc != null)
        {
            string? file = await RequestPickFileFunc.Invoke();
            if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
            {
                AddFilesToTransferQueue(new[] { file });
            }
        }
    }

    public void AddFilesToTransferQueue(IEnumerable<string> filePaths)
    {
        int added = 0;
        foreach (var path in filePaths.Distinct())
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) continue;
            // 避免重复将相同的待发送任务加入队列
            if (FileTransfers.Any(t => t.Status == TransferStatus.Pending && string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var fi = new System.IO.FileInfo(path);
            var item = new FileTransferItem
            {
                FileName = fi.Name,
                FilePath = path,
                IsSend = true,
                TotalBytes = (ulong)fi.Length,
                Status = TransferStatus.Pending,
                PeerName = "等待发送"
            };
            FileTransfers.Insert(0, item);
            AppendTransferLog($"添加待发送文件: {fi.Name} ({FileTransferItem.FormatFileSize((ulong)fi.Length)})");
            added++;
        }
        if (added > 0)
        {
            StatusMessage = $"已添加 {added} 个待发送文件到传输任务列表";
        }
    }

    [RelayCommand]
    public void RemoveTransferItem(FileTransferItem? item)
    {
        if (item == null) return;
        if (item.Status == TransferStatus.Transferring)
        {
            CancelTransfer(item);
        }
        FileTransfers.Remove(item);
        AppendTransferLog($"移除文件: {item.FileName}");
        ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
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

        // 收集所有勾选的目标设备
        var targets = AvailableTransferTargets.Where(t => t.IsSelected).ToList();
        if (targets.Count == 0 && !string.IsNullOrWhiteSpace(ManualTargetEndpoint))
        {
            string ep = ManualTargetEndpoint.Trim();
            targets.Add(new TransferTargetDeviceItem
            {
                DeviceName = ep,
                Endpoint = ep,
                IsOnline = true
            });
        }

        if (targets.Count == 0)
        {
            StatusMessage = "⚠️ 请先在上方勾选至少一个接收端设备";
            AppendTransferLog("⚠️ 发送失败: 未勾选接收端设备");
            return;
        }

        // 收集待发送的队列项
        var pendingItems = FileTransfers.Where(t => t.Status == TransferStatus.Pending).ToList();
        if (pendingItems.Count == 0)
        {
            StatusMessage = "⚠️ 传输任务列表中没有待发送的文件，请先选择或拖拽文件添加";
            AppendTransferLog("⚠️ 发送失败: 列表中无待发送文件");
            return;
        }

        IsSendingFile = true;
        StatusMessage = $"🚀 正在准备向 {targets.Count} 台设备发送 {pendingItems.Count} 个文件...";
        AppendTransferLog($"🚀 开始批量发送: {pendingItems.Count} 个文件 -> {targets.Count} 台设备");

        try
        {
            foreach (var target in targets)
            {
                foreach (var item in pendingItems)
                {
                    item.Status = TransferStatus.Transferring;
                    item.PeerName = target.DeviceName;
                    item.PeerEndpoint = target.Endpoint;
                    ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);

                    AppendTransferLog($"正在发送: {item.FileName} -> {target.DeviceName} ({target.Endpoint})");

                    string? tid = await Task.Run(() => _service.SendFile(target.Endpoint, item.FilePath));
                    if (string.IsNullOrEmpty(tid))
                    {
                        item.MarkCompleted(item.FilePath, false, "连接目标设备失败或对方未开启监听");
                        AppendTransferLog($"✕ 发送失败: {item.FileName} -> {target.DeviceName}");
                    }
                    else
                    {
                        item.TransferId = tid;
                        AppendTransferLog($"✓ 已发起发送: {item.FileName} (传输ID: {tid[..Math.Min(8, tid.Length)]})");
                    }
                }
            }
        }
        finally
        {
            IsSendingFile = false;
            ActiveTransferCount = FileTransfers.Count(t => t.Status == TransferStatus.Transferring);
        }
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
                AppendTransferLog($"接收目录已更新为: {dir}");
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
