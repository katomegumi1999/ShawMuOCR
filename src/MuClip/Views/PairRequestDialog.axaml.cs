using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MuClip.Services;

namespace MuClip.Views;

public partial class PairRequestDialog : Window
{
    private static PairRequestDialog? s_activeDialog;

    private readonly IMuClipService? _service;
    private readonly string _requestId = string.Empty;
    private readonly string _deviceId = string.Empty;
    private readonly string _deviceName = string.Empty;
    private readonly string _peerIp = string.Empty;
    private bool _isResponded;

    private TextBlock? _deviceInfoTextBlock;
    private TextBox? _pinTextBox;
    private TextBlock? _errorTextBlock;
    private TextBlock? _successTextBlock;
    private Button? _confirmButton;
    private Button? _rejectButton;

    public PairRequestDialog()
    {
        InitializeComponent();
    }

    public PairRequestDialog(IMuClipService service, string requestId, string deviceId, string deviceName, string peerIp)
    {
        InitializeComponent();

        _service = service;
        _requestId = requestId;
        _deviceId = deviceId;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? "未命名局域网设备" : deviceName;
        _peerIp = peerIp;

        if (_deviceInfoTextBlock != null)
        {
            _deviceInfoTextBlock.Text = $"设备: {_deviceName} ({_peerIp})";
        }

        if (_service != null)
        {
            _service.PairEstablished += OnPairEstablished;
        }

        Opened += OnWindowOpened;
        Closing += OnWindowClosing;

        TryLoadIcon();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _deviceInfoTextBlock = this.FindControl<TextBlock>("DeviceInfoTextBlock");
        _pinTextBox = this.FindControl<TextBox>("PinTextBox");
        _errorTextBlock = this.FindControl<TextBlock>("ErrorTextBlock");
        _successTextBlock = this.FindControl<TextBlock>("SuccessTextBlock");
        _confirmButton = this.FindControl<Button>("ConfirmButton");
        _rejectButton = this.FindControl<Button>("RejectButton");

        if (_confirmButton != null)
        {
            _confirmButton.Click += OnConfirmClick;
        }

        if (_rejectButton != null)
        {
            _rejectButton.Click += OnRejectClick;
        }

        if (_pinTextBox != null)
        {
            _pinTextBox.KeyDown += OnPinTextBoxKeyDown;
        }

        KeyDown += OnWindowKeyDown;
    }

    private void TryLoadIcon()
    {
        try
        {
            var uri = new Uri("avares://ShawMuOCR/Assets/MuClip.ico");
            if (AssetLoader.Exists(uri))
            {
                Icon = new WindowIcon(AssetLoader.Open(uri));
            }
        }
        catch { }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        _pinTextBox?.Focus();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_service != null)
        {
            _service.PairEstablished -= OnPairEstablished;
        }

        // 若窗口被关闭（如点击右上角关闭按钮），且尚未响应过，向底层发送拒绝响应以释放对端等待
        if (!_isResponded && _service != null && !string.IsNullOrEmpty(_requestId))
        {
            _isResponded = true;
            try
            {
                _service.RespondPair(_requestId, string.Empty, false);
            }
            catch { }
        }
    }

    private void OnPairEstablished(string deviceId, string deviceName)
    {
        if (string.Equals(deviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() =>
            {
                _isResponded = true;
                Close();
            });
        }
    }

    private void OnPinTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DoConfirm();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DoReject();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DoReject();
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        DoConfirm();
    }

    private void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        DoReject();
    }

    private void DoConfirm()
    {
        if (_isResponded || _service == null) return;

        string pin = _pinTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 6)
        {
            ShowError("请输入 6 位数字配对码");
            return;
        }

        int code = _service.RespondPair(_requestId, pin, true);
        if (code == 0)
        {
            // 配对成功
            _isResponded = true;
            ShowSuccess("✓ 配对成功！已建立加密连接");
            DisableInputs();

            Task.Delay(600).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(Close);
            });
        }
        else if (code == 2)
        {
            // PIN 码错误，允许重试
            ShowError("配对码错误，请核对对方屏幕显示的 6 位数字！");
            _pinTextBox?.SelectAll();
            _pinTextBox?.Focus();
        }
        else
        {
            // 超时或已失效
            _isResponded = true;
            ShowError("配对请求已超时或失效，请让对方重新发起配对");
            DisableInputs();

            Task.Delay(1500).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(Close);
            });
        }
    }

    private void DoReject()
    {
        if (_isResponded) return;
        _isResponded = true;

        try
        {
            _service?.RespondPair(_requestId, string.Empty, false);
        }
        catch { }

        Close();
    }

    private void ShowError(string message)
    {
        if (_errorTextBlock != null)
        {
            _errorTextBlock.Text = message;
            _errorTextBlock.IsVisible = true;
        }
        if (_successTextBlock != null)
        {
            _successTextBlock.IsVisible = false;
        }
    }

    private void ShowSuccess(string message)
    {
        if (_successTextBlock != null)
        {
            _successTextBlock.Text = message;
            _successTextBlock.IsVisible = true;
        }
        if (_errorTextBlock != null)
        {
            _errorTextBlock.IsVisible = false;
        }
    }

    private void DisableInputs()
    {
        if (_pinTextBox != null) _pinTextBox.IsEnabled = false;
        if (_confirmButton != null) _confirmButton.IsEnabled = false;
        if (_rejectButton != null) _rejectButton.IsEnabled = false;
    }

    /// <summary>
    /// 全局唤起配对确认弹窗（自动去重并前置展示）
    /// </summary>
    public static void ShowRequest(Window? owner, IMuClipService service, string requestId, string deviceId, string deviceName, string peerIp)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (s_activeDialog != null)
            {
                s_activeDialog.Activate();
                return;
            }

            var dlg = new PairRequestDialog(service, requestId, deviceId, deviceName, peerIp);
            s_activeDialog = dlg;
            dlg.Closed += (s, e) =>
            {
                if (s_activeDialog == dlg)
                {
                    s_activeDialog = null;
                }
            };

            if (owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized)
            {
                dlg.Show(owner);
            }
            else
            {
                dlg.Show();
            }
            dlg.Activate();
        });
    }
}
