using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MuClip.Models;
using MuClip.ViewModels;

namespace ShawMuOCR.Views.Clipboard;

public partial class ClipboardView : UserControl
{
    private Window? _attachedWindow;
    private Visual? _parentVisual;

    private readonly DispatcherTimer _longPressTimer;
    private object? _pressedObject;
    private Control? _pressedControl;
    private Point _pressStartPoint;
    private bool _isItemDragActive;

    private Panel? _leftListPanel;
    private ListBox? _historyListBox;
    private ListBox? _phrasesListBox;
    private Border? _dragPreviewCard;
    private TranslateTransform _dragPreviewTransform;
    private TextBlock? _dragPreviewText;
    private TextBlock? _dragPreviewSubText;

    public ClipboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        _longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };
        _longPressTimer.Tick += OnLongPressTimerTick;

        PointerMoved += OnViewPointerMoved;
        PointerReleased += OnViewPointerReleased;
        PointerCaptureLost += OnViewPointerCaptureLost;

        _leftListPanel = this.FindControl<Panel>("LeftListPanel");
        _historyListBox = this.FindControl<ListBox>("HistoryListBox");
        if (_historyListBox != null)
        {
            _historyListBox.AddHandler(ScrollViewer.ScrollChangedEvent, OnHistoryListScrollChanged);
        }
        _phrasesListBox = this.FindControl<ListBox>("PhrasesListBox");

        _dragPreviewCard = this.FindControl<Border>("DragPreviewCard");
        _dragPreviewTransform = (_dragPreviewCard?.RenderTransform as TranslateTransform) ?? new TranslateTransform();
        if (_dragPreviewCard != null)
        {
            _dragPreviewCard.RenderTransform = _dragPreviewTransform;
        }
        _dragPreviewText = this.FindControl<TextBlock>("DragPreviewText");
        _dragPreviewSubText = this.FindControl<TextBlock>("DragPreviewSubText");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is Window win)
        {
            _attachedWindow = win;
            _attachedWindow.PropertyChanged += OnHostWindowPropertyChanged;
        }
        if (Parent is Visual parentVisual)
        {
            _parentVisual = parentVisual;
            _parentVisual.PropertyChanged += OnHostWindowPropertyChanged;
        }
        UpdateHostActiveState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_attachedWindow != null)
        {
            _attachedWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _attachedWindow = null;
        }
        if (_parentVisual != null)
        {
            _parentVisual.PropertyChanged -= OnHostWindowPropertyChanged;
            _parentVisual = null;
        }
        base.OnDetachedFromVisualTree(e);
        if (DataContext is ClipboardViewModel vm)
        {
            vm.SetHostViewActive(false);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateHostActiveState();
        }
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
        {
            UpdateHostActiveState();
        }
    }

    private void UpdateHostActiveState()
    {
        if (_attachedWindow == null) return;
        if (DataContext is ClipboardViewModel vm)
        {
            bool parentVisible = _parentVisual?.IsVisible ?? true;
            bool windowVisible = _attachedWindow.IsVisible && _attachedWindow.WindowState != WindowState.Minimized;
            vm.SetHostViewActive(IsVisible && parentVisible && windowVisible);
        }
    }

    private void OnHistoryListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is ScrollViewer sv && DataContext is ClipboardViewModel vm)
        {
            if (sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 150)
            {
                vm.LoadMoreHistory();
            }
        }
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 如果点击发生在操作按钮上，由按钮处理，不触发拖拽
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() != null)
        {
            return;
        }

        if (sender is Control ctrl && (ctrl.DataContext is MuClipItem || ctrl.DataContext is MuClipPhraseItem))
        {
            _pressedObject = ctrl.DataContext;
            _pressedControl = ctrl;
            _pressStartPoint = e.GetPosition(this);
            _isItemDragActive = false;

            if (DataContext is ClipboardViewModel vm)
            {
                if (_pressedObject is MuClipItem item)
                {
                    vm.SelectedItem = item;
                }
                else if (_pressedObject is MuClipPhraseItem phrase)
                {
                    vm.SelectedPhrase = phrase;
                }
            }

            _longPressTimer.Stop();
            _longPressTimer.Start();

            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (_pressedObject != null && !_isItemDragActive)
        {
            BeginItemDrag(_pressedObject);
        }
    }

    private void BeginItemDrag(object itemObj)
    {
        _isItemDragActive = true;
        string preview = string.Empty;
        string subText = string.Empty;

        if (itemObj is MuClipItem item)
        {
            item.IsDragging = true;
            preview = item.Preview;
            subText = item.TimeAndCharCount;
        }
        else if (itemObj is MuClipPhraseItem phrase)
        {
            phrase.IsDragging = true;
            preview = phrase.Preview;
            subText = phrase.TimeAndCharCount;
        }

        if (_dragPreviewText != null) _dragPreviewText.Text = preview;
        if (_dragPreviewSubText != null) _dragPreviewSubText.Text = subText;

        if (_dragPreviewCard != null)
        {
            _dragPreviewCard.IsVisible = true;
            UpdateDragPreviewPosition();
        }
    }

    private void UpdateDragPreviewPosition(Point? currentViewPt = null)
    {
        if (_dragPreviewCard == null || _dragPreviewTransform == null || _leftListPanel == null)
            return;

        double cardH = _dragPreviewCard.Bounds.Height > 0 ? _dragPreviewCard.Bounds.Height : 48;
        double minY = 48;
        double maxY = _leftListPanel.Bounds.Height > 0 ? Math.Max(minY, _leftListPanel.Bounds.Height - cardH - 6) : 400;

        if (currentViewPt.HasValue)
        {
            var panelPt = currentViewPt.Value;
            var trans = this.TranslatePoint(panelPt, _leftListPanel);
            if (trans.HasValue)
            {
                panelPt = trans.Value;
            }

            double targetY = Math.Clamp(panelPt.Y - (cardH / 2), minY, maxY);
            _dragPreviewTransform.Y = targetY;
        }
        else if (_pressedControl != null)
        {
            var itemPt = _pressedControl.TranslatePoint(new Point(0, 0), _leftListPanel);
            if (itemPt.HasValue)
            {
                _dragPreviewTransform.Y = Math.Clamp(itemPt.Value.Y, minY, maxY);
            }
        }
    }

    private void EndItemDrag()
    {
        _isItemDragActive = false;
        if (_pressedObject is MuClipItem item)
        {
            item.IsDragging = false;
        }
        else if (_pressedObject is MuClipPhraseItem phrase)
        {
            phrase.IsDragging = false;
        }

        if (_dragPreviewCard != null)
        {
            _dragPreviewCard.IsVisible = false;
        }
        if (_dragPreviewTransform != null)
        {
            _dragPreviewTransform.X = 0;
            _dragPreviewTransform.Y = 0;
        }
    }

    private void OnViewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedObject == null) return;

        var currentPoint = e.GetPosition(this);
        double deltaY = Math.Abs(currentPoint.Y - _pressStartPoint.Y);
        double deltaX = Math.Abs(currentPoint.X - _pressStartPoint.X);

        if (!_isItemDragActive)
        {
            if (deltaY > 8 || deltaX > 8)
            {
                _longPressTimer.Stop();
                BeginItemDrag(_pressedObject);
            }
        }

        if (_isItemDragActive)
        {
            UpdateDragPreviewPosition(currentPoint);

            var activeListBox = (DataContext is ClipboardViewModel vm && vm.IsPhrasesTabSelected) ? _phrasesListBox : _historyListBox;
            if (activeListBox != null)
            {
                var scrollViewer = activeListBox.FindDescendantOfType<ScrollViewer>();
                if (scrollViewer != null)
                {
                    var listPt = e.GetPosition(activeListBox);
                    if (listPt.Y < 24)
                    {
                        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Max(0, scrollViewer.Offset.Y - 8));
                    }
                    else if (listPt.Y > activeListBox.Bounds.Height - 24)
                    {
                        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, scrollViewer.Offset.Y + 8);
                    }
                }
            }

            var hit = this.InputHitTest(currentPoint) as Visual;
            var targetListBoxItem = hit?.FindAncestorOfType<ListBoxItem>();
            if (targetListBoxItem != null && targetListBoxItem.DataContext != null && targetListBoxItem.DataContext != _pressedObject)
            {
                if (DataContext is ClipboardViewModel viewModel)
                {
                    if (_pressedObject is MuClipPhraseItem pressedPhrase && targetListBoxItem.DataContext is MuClipPhraseItem targetPhrase)
                    {
                        var items = viewModel.FilteredPhrases;
                        int oldIdx = items.IndexOf(pressedPhrase);
                        int newIdx = items.IndexOf(targetPhrase);
                        if (oldIdx >= 0 && newIdx >= 0 && oldIdx != newIdx)
                        {
                            var ptInItem = e.GetPosition(targetListBoxItem);
                            double itemH = targetListBoxItem.Bounds.Height;
                            if (itemH > 0)
                            {
                                bool canSwap = (newIdx > oldIdx && ptInItem.Y > itemH * 0.35) ||
                                               (newIdx < oldIdx && ptInItem.Y < itemH * 0.65);
                                if (canSwap)
                                {
                                    items.Move(oldIdx, newIdx);
                                }
                            }
                        }
                    }
                    else if (_pressedObject is MuClipItem pressedItem && targetListBoxItem.DataContext is MuClipItem targetItem)
                    {
                        var items = viewModel.FilteredItems;
                        int oldIdx = items.IndexOf(pressedItem);
                        int newIdx = items.IndexOf(targetItem);
                        if (oldIdx >= 0 && newIdx >= 0 && oldIdx != newIdx)
                        {
                            var ptInItem = e.GetPosition(targetListBoxItem);
                            double itemH = targetListBoxItem.Bounds.Height;
                            if (itemH > 0)
                            {
                                bool canSwap = (newIdx > oldIdx && ptInItem.Y > itemH * 0.35) ||
                                               (newIdx < oldIdx && ptInItem.Y < itemH * 0.65);
                                if (canSwap)
                                {
                                    items.Move(oldIdx, newIdx);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private void OnViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedObject == null && !_isItemDragActive) return;

        _longPressTimer.Stop();

        var obj = _pressedObject;
        bool wasDragging = _isItemDragActive;

        EndItemDrag();

        _pressedObject = null;
        _pressedControl = null;

        try
        {
            if (e.Pointer.Captured != null)
            {
                e.Pointer.Capture(null);
            }
        }
        catch { }

        if (obj != null)
        {
            if (wasDragging)
            {
                e.Handled = true;
                if (DataContext is ClipboardViewModel vm && obj is MuClipPhraseItem)
                {
                    vm.CommitPhrasesReordered();
                }
            }
            else
            {
                if (DataContext is ClipboardViewModel vm)
                {
                    if (obj is MuClipItem item)
                    {
                        vm.SelectedItem = item;
                    }
                    else if (obj is MuClipPhraseItem phrase)
                    {
                        vm.SelectedPhrase = phrase;
                    }
                }
            }
        }
    }

    private void OnViewPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pressedObject == null && !_isItemDragActive) return;

        _longPressTimer.Stop();
        if (_isItemDragActive && DataContext is ClipboardViewModel vm && _pressedObject is MuClipPhraseItem)
        {
            vm.CommitPhrasesReordered();
        }
        EndItemDrag();
        _pressedObject = null;
        _pressedControl = null;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ClipboardViewModel vm)
        {
            UpdateHostActiveState();
            vm.RequestPickFilesFunc = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择要发送的文件",
                    AllowMultiple = true
                });
                if (files.Count > 0)
                {
                    var list = new List<string>();
                    foreach (var f in files)
                    {
                        string? p = f.TryGetLocalPath() ?? f.Path.LocalPath;
                        if (!string.IsNullOrEmpty(p)) list.Add(p);
                    }
                    return list;
                }
                return null;
            };

            vm.RequestPickFileFunc = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择要发送的文件",
                    AllowMultiple = false
                });
                if (files.Count > 0)
                {
                    return files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
                }
                return null;
            };

            vm.RequestPickFolderFunc = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择文件接收保存目录"
                });
                if (folders.Count > 0)
                {
                    return folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
                }
                return null;
            };

            vm.RequestPickSaveBackupFileFunc = async (defaultFileName) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "导出加密数据库备份 (.mubc)",
                    DefaultExtension = "mubc",
                    SuggestedFileName = defaultFileName,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("MuClip 加密数据库备份 (*.mubc)")
                        {
                            Patterns = new[] { "*.mubc" }
                        },
                        new FilePickerFileType("所有文件 (*.*)")
                        {
                            Patterns = new[] { "*.*" }
                        }
                    }
                });
                if (file != null)
                {
                    return file.TryGetLocalPath() ?? file.Path.LocalPath;
                }
                return null;
            };

            vm.RequestPickOpenBackupFileFunc = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择要导入的加密数据库备份 (.mubc)",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("MuClip 加密数据库备份 (*.mubc)")
                        {
                            Patterns = new[] { "*.mubc" }
                        },
                        new FilePickerFileType("所有文件 (*.*)")
                        {
                            Patterns = new[] { "*.*" }
                        }
                    }
                });
                if (files.Count > 0)
                {
                    return files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
                }
                return null;
            };
        }
    }

    public void OnPhraseEditorKeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && DataContext is ClipboardViewModel vm)
        {
            vm.PhraseEditorCaretIndex = tb.CaretIndex;
        }
    }

    public void OnPhraseEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is TextBox tb && DataContext is ClipboardViewModel vm)
        {
            vm.PhraseEditorCaretIndex = tb.CaretIndex;
        }
    }
}
