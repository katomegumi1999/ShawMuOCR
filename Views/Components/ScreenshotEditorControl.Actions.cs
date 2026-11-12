using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Editor;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;

namespace ShawMuOCR.Views.Components;

/// <summary>
/// 截图标注窗口 - 动作导出、快捷键与撤销重做 (Actions & Shortcuts) 模块
/// </summary>
public partial class ScreenshotEditorControl
{
    private bool _isProcessingAction;
    private bool _isFullSnapshotRestored;

    // ==========================================
    // 撤销 / 重做
    // ==========================================
    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();
        int prevW = _doc.OriginalImage.Width;
        int prevH = _doc.OriginalImage.Height;

        _doc.UndoRedo.Undo();
        _selectedElement = null;
        DimensionText.Text = $"{_doc.OriginalImage.Width} × {_doc.OriginalImage.Height}";
        SyncRestoreFullSnapshotBtnState();
        RefreshDisplay();

        if (prevW != _doc.OriginalImage.Width || prevH != _doc.OriginalImage.Height)
        {
            ApplyDimensions(recenter: false);
        }
        ReactivateWindowAndIme();
    }

    private void OnRedoClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();
        int prevW = _doc.OriginalImage.Width;
        int prevH = _doc.OriginalImage.Height;

        _doc.UndoRedo.Redo();
        _selectedElement = null;
        DimensionText.Text = $"{_doc.OriginalImage.Width} × {_doc.OriginalImage.Height}";
        SyncRestoreFullSnapshotBtnState();
        RefreshDisplay();

        if (prevW != _doc.OriginalImage.Width || prevH != _doc.OriginalImage.Height)
        {
            ApplyDimensions(recenter: false);
        }
        ReactivateWindowAndIme();
    }

    private void SyncRestoreFullSnapshotBtnState()
    {
        if (_fullScreenshot == null || RestoreFullSnapshotBtn == null) return;
        bool isFull = _doc.OriginalImage.Width == _fullScreenshot.Width && _doc.OriginalImage.Height == _fullScreenshot.Height;
        _isFullSnapshotRestored = isFull;
        RestoreFullSnapshotBtn.IsEnabled = !isFull;
        RestoreFullSnapshotBtn.Opacity = isFull ? 0.5 : 1.0;

        if (!isFull && GetHostWindowState() == WindowState.Maximized)
        {
            SetHostWindowState(WindowState.Normal);
        }
    }

    // ==========================================
    // 动作按钮
    // ==========================================
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            CommitInlineText();

            using var rendered = _doc.RenderToBitmap();
            bool copied = await _clipboardService.SetImageAsync(rendered);

            if (copied)
            {
                ToastNotificationWindow.Show(
                    "带标注图片已复制到剪贴板，可直接在微信/QQ/Word中粘贴",
                    TimeSpan.Zero,
                    "✓ 标注已复制到剪贴板");
            }

            TryClose();
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注复制图片异常", ex);
        }
        finally
        {
            _isProcessingAction = false;
        }
    }

    private async void OnQuickSaveClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();

        try
        {
            var hostTopLevel = TopLevel.GetTopLevel(this);
            string? filePath = await QuickSaveHelper.QuickSaveAsync(
                _doc,
                _settingsService,
                hostTopLevel,
                successTitle: "✓ 已自动保存到上次路径");

            if (!string.IsNullOrEmpty(filePath))
            {
                TryClose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("快速保存截图失败", ex);
            ToastNotificationWindow.Show($"快速保存图片失败: {ex.Message}");
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();

        var dialog = new SaveImageDialog(_doc, _settingsService);
        var hostWin = VisualRoot as Window ?? TopLevel.GetTopLevel(this) as Window;
        bool result = false;
        if (hostWin != null)
        {
            result = await dialog.ShowDialog<bool>(hostWin);
        }
        else
        {
            dialog.Show();
        }

        if (result && dialog.ShouldCloseEditor)
        {
            TryClose();
        }
    }

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            CommitInlineText();

            var rendered = _doc.RenderToBitmap();

            PixelPoint pinPos = _selectionBounds.HasValue
                ? new PixelPoint(_selectionBounds.Value.X, _selectionBounds.Value.Y)
                : GetControlScreenPosition();

            var pinWin = new PinnedImageWindow(rendered, pinPos);
            pinWin.Show();

            TryClose();
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注贴图异常", ex);
        }
        finally
        {
            _isProcessingAction = false;
        }
    }

    private async void OnOcrClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            CommitInlineText();
            var rendered = _doc.RenderToBitmap();
            TryClose();

            if (_ocrCallback != null)
            {
                try
                {
                    await _ocrCallback(rendered);
                }
                catch (Exception ex)
                {
                    rendered.Dispose();
                    _logger?.Error("截图标注 OCR 识别任务执行异常", ex);
                }
            }
            else
            {
                rendered.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注点击 OCR 失败", ex);
        }
        finally
        {
            _isProcessingAction = false;
        }
    }

    private async void OnTranslateClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            CommitInlineText();
            var rendered = _doc.RenderToBitmap();

            var screenPos = GetControlScreenPosition();
            var bounds = _selectionBounds ?? new ScreenRect(
                screenPos.X,
                screenPos.Y,
                (int)Bounds.Width,
                (int)Bounds.Height);

            TryClose();

            if (_translateCallback != null)
            {
                try
                {
                    await _translateCallback(rendered, bounds);
                }
                catch (Exception ex)
                {
                    rendered.Dispose();
                    _logger?.Error("截图标注翻译回调执行异常", ex);
                }
            }
            else
            {
                rendered.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注点击翻译失败", ex);
        }
        finally
        {
            _isProcessingAction = false;
        }
    }

    private async void OnLongScreenshotClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            var bounds = _selectionBounds ?? ScreenRect.Empty;
            TryClose();

            if (_longScreenshotCallback != null)
            {
                await _longScreenshotCallback(bounds);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注启动长截图失败", ex);
        }
        finally
        {
            _isProcessingAction = false;
        }
    }

    private async void OnRegionRecordClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            var bounds = _selectionBounds ?? ScreenRect.Empty;
            TryClose();

            if (_regionRecordCallback != null)
            {
                await _regionRecordCallback(bounds);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注启动区域录屏失败", ex);
        }
        finally
        {
            _isProcessingAction = false;
        }
    }

    private ScreenRect GetEffectiveSelectionBounds()
    {
        if (_selectionBounds.HasValue)
        {
            return new ScreenRect(
                _selectionBounds.Value.X,
                _selectionBounds.Value.Y,
                _doc.OriginalImage.Width,
                _doc.OriginalImage.Height);
        }

        var screenPos = GetControlScreenPosition();
        return new ScreenRect(
            screenPos.X,
            screenPos.Y,
            _doc.OriginalImage.Width,
            _doc.OriginalImage.Height);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        TryClose();
    }

    private void OnControlKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (InlineTextBox.IsVisible || focused is TextBox || e.Source is TextBox) return;

        if (_currentTool == EditorTool.Crop)
        {
            if (e.Key == Key.Enter)
            {
                CommitCrop();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                CancelCrop();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OnUndoClick(sender, e);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && (e.Key is Key.D0 or Key.NumPad0))
        {
            if (_zoomFactor > 1.001 || Math.Abs(_panOffsetX) > 0.1 || Math.Abs(_panOffsetY) > 0.1)
            {
                ResetZoomPan();
                e.Handled = true;
                return;
            }
        }
        else if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OnRedoClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                OnQuickSaveClick(sender, e);
            }
            else
            {
                OnSaveClick(sender, e);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OnCopyClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_selectedElement != null)
            {
                _selectedElement = null;
                _transformMode = TransformMode.None;
                UpdateSelectionHighlight();
                RefreshDisplay();
                e.Handled = true;
                return;
            }

            if (_currentTool != EditorTool.Select)
            {
                ToolTextBtn.IsChecked = false;
                ToolLineBtn.IsChecked = false;
                ToolArrowBtn.IsChecked = false;
                ToolRectBtn.IsChecked = false;
                ToolPenBtn.IsChecked = false;
                ToolMosaicBtn.IsChecked = false;
                ToolEraserBtn.IsChecked = false;
                ToolCropBtn.IsChecked = false;
                _currentTool = EditorTool.Select;
                UpdateCursor();
                e.Handled = true;
                return;
            }

            if (_isEmbedded)
            {
                e.Handled = true;
                return;
            }

            TryClose();
            e.Handled = true;
        }
        else if ((e.Key is Key.Delete or Key.Back) && _selectedElement != null)
        {
            _doc.RemoveElement(_selectedElement);
            _selectedElement = null;
            RefreshDisplay();
            e.Handled = true;
        }
        else if (e.Key == Key.T)
        {
            ToolTextBtn.IsChecked = true;
            OnToolToggleClick(ToolTextBtn, e);
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            ToolLineBtn.IsChecked = true;
            OnToolToggleClick(ToolLineBtn, e);
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            ToolArrowBtn.IsChecked = true;
            OnToolToggleClick(ToolArrowBtn, e);
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            ToolRectBtn.IsChecked = true;
            OnToolToggleClick(ToolRectBtn, e);
            e.Handled = true;
        }
        else if (e.Key == Key.P)
        {
            ToolPenBtn.IsChecked = true;
            OnToolToggleClick(ToolPenBtn, e);
            e.Handled = true;
        }
        else if (e.Key == Key.M)
        {
            ToolMosaicBtn.IsChecked = true;
            OnToolToggleClick(ToolMosaicBtn, e);
            e.Handled = true;
        }
        else if (e.Key == Key.E)
        {
            ToolEraserBtn.IsChecked = true;
            OnToolToggleClick(ToolEraserBtn, e);
            e.Handled = true;
        }
        else if (e.Key is Key.C or Key.X)
        {
            ToolCropBtn.IsChecked = true;
            OnToolToggleClick(ToolCropBtn, e);
            e.Handled = true;
        }
    }

    private void OnRestoreFullSnapshotClick(object? sender, RoutedEventArgs e)
    {
        if (_fullScreenshot == null || _fullScreenshot.Bitmap == null || _fullScreenshot.Bitmap.IsNull)
        {
            ToastNotificationWindow.Show("当前截图无可用全屏快照", TimeSpan.FromSeconds(2), "✕ 无法恢复");
            return;
        }

        if (_isFullSnapshotRestored)
        {
            ToastNotificationWindow.Show("当前画布已经是全屏快照状态", TimeSpan.FromSeconds(2), "ℹ 已是全屏");
            return;
        }

        CommitInlineText();

        float dx = _cropOffset?.X ?? (_selectionBounds?.X ?? 0);
        float dy = _cropOffset?.Y ?? (_selectionBounds?.Y ?? 0);

        var newFullImage = _fullScreenshot.Clone();
        var restoredElements = new List<IImageElement>();

        foreach (var elem in _doc.Elements)
        {
            var cloned = elem.Clone();
            cloned.MoveBy(dx, dy);
            restoredElements.Add(cloned);
        }

        _doc.UndoRedo.Execute(new CropDocumentCommand(_doc, newFullImage, restoredElements));
        _isFullSnapshotRestored = true;
        if (RestoreFullSnapshotBtn != null)
        {
            RestoreFullSnapshotBtn.IsEnabled = false;
            RestoreFullSnapshotBtn.Opacity = 0.5;
        }

        DimensionText.Text = $"{newFullImage.Width} × {newFullImage.Height}";
        SetHostWindowState(WindowState.Maximized);
        RefreshDisplay();
        ApplyDimensions(recenter: false);
        ReactivateWindowAndIme();

        ToastNotificationWindow.Show("已恢复至全屏快照并自动最大化，已有标注已自动定位 (可按 Ctrl+Z 撤销)", TimeSpan.FromSeconds(2.5), "⛶ 全屏已恢复");
    }
}
