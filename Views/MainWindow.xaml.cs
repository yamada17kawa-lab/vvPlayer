using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyMediaPlayer.Models;
using MyMediaPlayer.ViewModels;

namespace MyMediaPlayer.Views;

/// <summary>
/// MainWindow 视图交互逻辑（遵循 MVVM，仅处理纯 UI 窗口状态如全屏切换、快捷键、进度条拖拽事件桥接）
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isFullScreen;
    private bool _isMiniMode;
    private bool _isTrackDragging;
    private Slider? _activeDraggingSlider;
    private Rect _normalBounds;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private bool _previousPlaylistVisible;
    private readonly System.Windows.Threading.DispatcherTimer _miniOverlayTimer;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // 小窗口模式下的浮层自动隐藏定时器（鼠标静止 2.5 秒后淡出隐藏）
        _miniOverlayTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _miniOverlayTimer.Tick += (s, e) =>
        {
            _miniOverlayTimer.Stop();
            if (_isMiniMode && !MiniOverlayGrid.IsMouseOver)
            {
                MiniOverlayGrid.Visibility = Visibility.Collapsed;
            }
        };
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _viewModel.IsDraggingSlider = true;
    }

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _viewModel.IsDraggingSlider = false;
        if (sender is Slider slider)
        {
            _viewModel.CommitSeek(slider.Value);
        }
        else
        {
            _viewModel.CommitSeek(TimelineSlider.Value);
        }
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_viewModel != null && _viewModel.IsDraggingSlider)
        {
            _viewModel.PreviewSeek(e.NewValue);
        }
    }

    private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        if (slider.Template?.FindName("PART_Track", slider) is Track track &&
            track.Thumb != null &&
            track.Thumb.IsMouseOver)
        {
            // 点击在滑块 Thumb 上时，交由 Thumb 自身的 DragStarted / DragCompleted 处理
            _viewModel.IsDraggingSlider = true;
            return;
        }

        // 点击在进度条轨道任意位置：立即移动滑块到点击处并捕获鼠标，支持直接点击跳转或按住平滑拖拽
        _activeDraggingSlider = slider;
        _isTrackDragging = true;
        _viewModel.IsDraggingSlider = true;
        UpdateSliderFromMousePosition(slider, e);
        slider.CaptureMouse();
        e.Handled = true;
    }

    private void TimelineSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isTrackDragging && e.LeftButton == MouseButtonState.Pressed && _activeDraggingSlider != null)
        {
            UpdateSliderFromMousePosition(_activeDraggingSlider, e);
            e.Handled = true;
        }
    }

    private void TimelineSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isTrackDragging)
        {
            _isTrackDragging = false;
            var slider = _activeDraggingSlider ?? TimelineSlider;
            _activeDraggingSlider = null;
            slider.ReleaseMouseCapture();
            _viewModel.IsDraggingSlider = false;
            _viewModel.CommitSeek(slider.Value);
            e.Handled = true;
        }
    }

    private void TimelineSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isTrackDragging)
        {
            _isTrackDragging = false;
            var slider = _activeDraggingSlider ?? TimelineSlider;
            _activeDraggingSlider = null;
            _viewModel.IsDraggingSlider = false;
            _viewModel.CommitSeek(slider.Value);
        }
    }

    private void UpdateSliderFromMousePosition(Slider slider, MouseEventArgs e)
    {
        double ratio;
        if (slider.Template?.FindName("PART_Track", slider) is Track track && track.ActualWidth > 0)
        {
            double thumbWidth = track.Thumb?.ActualWidth ?? 0;
            double usableWidth = Math.Max(1.0, track.ActualWidth - thumbWidth);
            double x = e.GetPosition(track).X - (thumbWidth * 0.5);
            ratio = Math.Clamp(x / usableWidth, 0.0, 1.0);
        }
        else
        {
            ratio = Math.Clamp(e.GetPosition(slider).X / Math.Max(1.0, slider.ActualWidth), 0.0, 1.0);
        }

        double newValue = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        slider.Value = newValue;
        _viewModel.PreviewSeek(newValue);
    }

    private void VideoSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isMiniMode)
        {
            if (MiniOverlayGrid.Visibility != Visibility.Visible)
            {
                MiniOverlayGrid.Visibility = Visibility.Visible;
            }
            _miniOverlayTimer.Stop();
            _miniOverlayTimer.Start();
        }
    }

    private void VideoSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isMiniMode && !MiniOverlayGrid.IsMouseOver)
        {
            _miniOverlayTimer.Stop();
            MiniOverlayGrid.Visibility = Visibility.Collapsed;
        }
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isMiniMode)
        {
            // 小窗口模式下：双击恢复标准窗口；单指按住可随意拖拽窗口移动
            if (e.ClickCount == 2)
            {
                ExitMiniMode();
                e.Handled = true;
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
                e.Handled = true;
                return;
            }
        }

        if (e.ClickCount == 2)
        {
            ToggleFullScreen();
        }
        else if (e.ClickCount == 1)
        {
            _viewModel.TogglePlayPauseCommand.Execute(null);
        }
    }

    private void MiniTopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ExitMiniMode();
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            e.Handled = true;
        }
    }

    private void MiniModeButton_Click(object sender, RoutedEventArgs e)
    {
        EnterMiniMode();
    }

    private void MiniRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ExitMiniMode();
    }

    private void MiniCloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void EnterMiniMode()
    {
        if (_isMiniMode) return;

        // 若当前处于全屏状态，先恢复为普通状态
        if (_isFullScreen)
        {
            ToggleFullScreen();
        }

        // 保存进入小窗口模式前的窗口状态、尺寸与侧边栏显示状态
        _previousWindowState = WindowState;
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _normalBounds = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        _previousPlaylistVisible = _viewModel.IsPlaylistVisible;

        // 隐藏主窗口顶部栏、底部控制面板及侧边栏
        _viewModel.IsPlaylistVisible = false;
        PlaylistSidebar.Visibility = Visibility.Collapsed;
        TopBar.Visibility = Visibility.Collapsed;
        BottomControlBar.Visibility = Visibility.Collapsed;

        // 切换为无边框置顶浮动小窗口
        _isMiniMode = true;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Topmost = true;

        MinWidth = 320;
        MinHeight = 180;
        Width = 440;
        Height = 255;

        // 默认放置在主屏幕右下角任务栏上方
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(0, workArea.Right - Width - 24);
        Top = Math.Max(0, workArea.Bottom - Height - 24);

        // 缩减字幕字体与边距，在紧凑小窗口上清晰优雅呈现
        SubtitleBorder.Margin = new Thickness(10, 0, 10, 8);
        SubtitleBorder.Padding = new Thickness(10, 4, 10, 4);
        OriginalSubtitleTextBlock.FontSize = 11;
        OriginalSubtitleTextBlock.Margin = new Thickness(0, 0, 0, 2);
        TranslatedSubtitleTextBlock.FontSize = 14;

        // 聚焦窗口，保证键盘快捷键（Ctrl+←/→、J/L 快退快进 10 秒、空格播放/暂停等）无缝可用
        Focus();

        // 默认显示小窗口控制浮层，并启动 2.5 秒无操作自动隐藏
        MiniOverlayGrid.Visibility = Visibility.Visible;
        _miniOverlayTimer.Stop();
        _miniOverlayTimer.Start();
    }

    private void ExitMiniMode()
    {
        if (!_isMiniMode) return;

        _miniOverlayTimer.Stop();
        MiniOverlayGrid.Visibility = Visibility.Collapsed;

        _isMiniMode = false;
        Topmost = false;
        MinWidth = 760;
        MinHeight = 480;

        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        WindowState = _previousWindowState;

        if (_normalBounds.Width > 0 && _normalBounds.Height > 0)
        {
            Left = _normalBounds.Left;
            Top = _normalBounds.Top;
            Width = _normalBounds.Width;
            Height = _normalBounds.Height;
        }

        TopBar.Visibility = Visibility.Visible;
        BottomControlBar.Visibility = Visibility.Visible;
        _viewModel.IsPlaylistVisible = _previousPlaylistVisible;
        PlaylistSidebar.Visibility = _previousPlaylistVisible ? Visibility.Visible : Visibility.Collapsed;

        // 恢复字幕正常尺寸
        SubtitleBorder.Margin = new Thickness(40, 0, 40, 36);
        SubtitleBorder.Padding = new Thickness(22, 12, 22, 12);
        OriginalSubtitleTextBlock.FontSize = 15;
        OriginalSubtitleTextBlock.Margin = new Thickness(0, 0, 0, 4);
        TranslatedSubtitleTextBlock.FontSize = 22;

        Focus();
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _previousWindowState = WindowState;
            _previousWindowStyle = WindowStyle;

            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            TopBar.Visibility = Visibility.Collapsed;
            FullScreenButton.Content = "🗗 退出全屏";
            _isFullScreen = true;
        }
        else
        {
            WindowStyle = _previousWindowStyle;
            WindowState = _previousWindowState;
            TopBar.Visibility = Visibility.Visible;
            FullScreenButton.Content = "⛶ 全屏";
            _isFullScreen = false;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 焦点在下拉框（源语言/目标语言）内时，方向键/空格交给下拉框自身处理，避免误跳转
        if (IsInsideComboBox(e.OriginalSource as DependencyObject))
        {
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when _isMiniMode:
                ExitMiniMode();
                e.Handled = true;
                break;
            case Key.Escape when _isFullScreen:
                ToggleFullScreen();
                e.Handled = true;
                break;
            case Key.F11:
                if (_isMiniMode)
                {
                    ExitMiniMode();
                }
                ToggleFullScreen();
                e.Handled = true;
                break;
            // 快进 / 快退 10 秒：Ctrl+→ / Ctrl+← 或 L / J（小窗口模式同样完美支持）
            case Key.Right when ctrl:
            case Key.L:
                _viewModel.SeekRelative(10);
                e.Handled = true;
                break;
            case Key.Left when ctrl:
            case Key.J:
                _viewModel.SeekRelative(-10);
                e.Handled = true;
                break;
            // 原有：→ / ← 快进 / 快退 5 秒
            case Key.Right:
                _viewModel.CommitSeek(Math.Min(_viewModel.TotalDurationSeconds, _viewModel.CurrentTimeSeconds + 5));
                e.Handled = true;
                break;
            case Key.Left:
                _viewModel.CommitSeek(Math.Max(0, _viewModel.CurrentTimeSeconds - 5));
                e.Handled = true;
                break;
        }
    }

    private static bool IsInsideComboBox(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is System.Windows.Controls.ComboBox || element is System.Windows.Controls.ComboBoxItem)
            {
                return true;
            }

            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files is { Length: > 0 })
            {
                _viewModel.LoadVideoFile(files[0]);
            }
        }
    }

    private bool _isClosing;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }
        _isClosing = true;

        // 1. 立即隐藏窗口，用户感知瞬间秒关，绝无任何“未响应”白屏或卡死现象
        Hide();

        // 2. 终止后台字幕识别管线并让播放器停止
        try
        {
            _viewModel.CancelLookAheadPipeline();
            _viewModel.Playlist.Save();
            _viewModel.StopCommand.Execute(null);
            VideoView.MediaPlayer = null;
        }
        catch
        {
        }

        // 3. 在后台守护线程快速执行资源释放，并在退出时结束整个进程
        var exitThread = new Thread(() =>
        {
            try
            {
                _viewModel.Dispose();
            }
            catch
            {
            }
            finally
            {
                Environment.Exit(0);
            }
        })
        {
            IsBackground = true
        };
        exitThread.Start();

        // 4. 超时安全保险：最多等待 400ms，超时直接退出，彻底杜绝任何后台僵尸残留
        Task.Delay(400).ContinueWith(_ => Environment.Exit(0));
    }

    // ──────────────────────── 播放目录侧边栏交互逻辑 ────────────────────────

    private Point _playlistDragStartPoint;

    private void PlaylistTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.Playlist.SelectedNode = e.NewValue as PlaylistNode;
    }

    private void PlaylistTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Playlist.SelectedNode is { IsFolder: false } node)
        {
            _viewModel.Playlist.Play(node);
            e.Handled = true;
        }
    }

    private void PlaylistTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右键点击项时自动获得焦点并选中，保证右键菜单命令作用在对应节点上
        if (e.OriginalSource is DependencyObject dep)
        {
            var item = FindVisualParent<TreeViewItem>(dep);
            if (item != null)
            {
                item.Focus();
                if (item.DataContext is PlaylistNode node)
                {
                    _viewModel.Playlist.SelectedNode = node;
                }
            }
        }
    }

    private void PlaylistTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _viewModel.Playlist.SelectedNode != null)
        {
            Point current = e.GetPosition(null);
            Vector diff = _playlistDragStartPoint - current;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var data = new DataObject("PlaylistNode", _viewModel.Playlist.SelectedNode);
                DragDrop.DoDragDrop(PlaylistTree, data, DragDropEffects.Move);
            }
        }
        else
        {
            _playlistDragStartPoint = e.GetPosition(null);
        }
    }

    private void PlaylistTree_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("PlaylistNode"))
        {
            e.Effects = DragDropEffects.Copy | DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void PlaylistTree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        PlaylistNode? targetNode = null;
        if (e.OriginalSource is DependencyObject dep)
        {
            var item = FindVisualParent<TreeViewItem>(dep);
            targetNode = item?.DataContext as PlaylistNode;
        }

        // 1. 从外部资源管理器直接拖入视频文件或文件夹
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (paths is { Length: > 0 })
            {
                var targetFolder = targetNode is { IsFolder: true } ? targetNode : targetNode?.Parent;
                _viewModel.Playlist.AddPaths(paths, targetFolder);
            }
            return;
        }

        // 2. 内部节点拖拽重组（归类到某个目录）
        if (e.Data.GetDataPresent("PlaylistNode") &&
            e.Data.GetData("PlaylistNode") is PlaylistNode sourceNode)
        {
            _viewModel.Playlist.MoveNode(sourceNode, targetNode);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent)
            {
                return parent;
            }
            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}
