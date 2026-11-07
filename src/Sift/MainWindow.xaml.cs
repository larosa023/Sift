using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Sift.Core;
using Sift.Themes;
using Sift.ViewModels;
using Page = Sift.ViewModels.Page;

namespace Sift;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        var s = vm.Settings;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight &&
            s.WindowWidth <= SystemParameters.VirtualScreenWidth && s.WindowHeight <= SystemParameters.VirtualScreenHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        SourceInitialized += (_, _) => ThemeManager.ApplyFrame(this);
        StateChanged += (_, _) => UpdateMaximizedState();
        Closing += OnClosing;
        UpdateMaximizedState();

        // Mouse "back" button navigates up a folder in the treemap.
        PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1 && _vm.CurrentPage == Page.Overview && _vm.Overview.UpCommand.CanExecute(null))
            {
                _vm.Overview.UpCommand.Execute(null);
                e.Handled = true;
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;

        vm.Overview.PropertyChanged += OnOverviewChanged;
        vm.Overview.ScanCompleted += _ =>
        {
            if (!IsActive) Native.FlashUntilFocused(new WindowInteropHelper(this).Handle);
        };
    }

    // ------------------------------------------------------------------ keyboard

    private static bool IsTextInput(object? focused) => focused is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool typing = IsTextInput(Keyboard.FocusedElement);
        var ov = _vm.Overview;
        bool onOverview = _vm.CurrentPage == Page.Overview;

        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.D1 or Key.NumPad1: _vm.CurrentPage = Page.Overview; e.Handled = true; return;
                case Key.D2 or Key.NumPad2: _vm.CurrentPage = Page.Cleanup; e.Handled = true; return;
                case Key.D3 or Key.NumPad3: _vm.CurrentPage = Page.Review; e.Handled = true; return;
                case Key.D4 or Key.NumPad4: _vm.CurrentPage = Page.Settings; e.Handled = true; return;
                case Key.F when ov.ShowResults:
                    _vm.CurrentPage = Page.Overview;
                    Dispatcher.InvokeAsync(() => OverviewPage.FocusFilter(), System.Windows.Threading.DispatcherPriority.Input);
                    e.Handled = true; return;
                case Key.E when onOverview && !typing && ov.OpenInExplorerCommand.CanExecute(null):
                    ov.OpenInExplorerCommand.Execute(null); e.Handled = true; return;
            }
        }

        if (mods != ModifierKeys.None) return;

        switch (key)
        {
            case Key.F5 when onOverview && ov.RescanCommand.CanExecute(null):
                ov.RescanCommand.Execute(null); e.Handled = true; break;

            case Key.Escape when onOverview:
                if (typing && ov.FilterText.Length == 0) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, this); e.Handled = true; break; }
                if (ov.ClearOne()) e.Handled = true;
                break;

            case Key.Escape when _vm.CurrentPage == Page.Review && _vm.Review.IsConfirming:
                _vm.Review.IsConfirming = false; e.Handled = true; break;

            case Key.Back when onOverview && !typing && ov.UpCommand.CanExecute(null):
                ov.UpCommand.Execute(null); e.Handled = true; break;

            case Key.Delete when onOverview && !typing && ov.AddToReviewCommand.CanExecute(null):
                ov.AddToReviewCommand.Execute(null); e.Handled = true; break;

            case Key.Enter when onOverview && !typing && ov.Selected is { IsDirectory: true } dir:
                ov.NavigateTo(dir); e.Handled = true; break;
        }
    }

    // ------------------------------------------------------------------ drag & drop

    private static string? DroppedFolder(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return null;
        var p = paths[0];
        if (Directory.Exists(p)) return p;
        var parent = Path.GetDirectoryName(p);
        return parent is not null && Directory.Exists(parent) ? parent : null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = !_vm.Overview.IsScanning && DroppedFolder(e) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var folder = DroppedFolder(e);
        if (folder is null || _vm.Overview.IsScanning) return;
        _vm.CurrentPage = Page.Overview;
        _ = _vm.Overview.ScanAsync(folder);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ taskbar progress

    private void OnOverviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        var tb = TaskbarItemInfo;
        if (tb is null) return;
        var ov = _vm.Overview;
        switch (e.PropertyName)
        {
            case nameof(OverviewViewModel.IsScanning):
                tb.ProgressState = !ov.IsScanning ? TaskbarItemProgressState.None
                    : ov.HasProgressEstimate ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Indeterminate;
                break;
            case nameof(OverviewViewModel.ProgressPercent) when ov.IsScanning && ov.HasProgressEstimate:
                tb.ProgressValue = ov.ProgressPercent / 100.0;
                break;
        }
    }

    // ------------------------------------------------------------------ window

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_vm.Review.IsWorking)
        {
            var r = MessageBox.Show(this, "Sift is still removing items. Quit anyway? Items already processed stay removed.",
                "Sift", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        SaveWindow();
    }

    private void UpdateMaximizedState()
    {
        // A maximized WindowChrome window overhangs the screen by the resize border; pad it back in.
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void SaveWindow()
    {
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        else
        {
            s.WindowWidth = RestoreBounds.Width;
            s.WindowHeight = RestoreBounds.Height;
        }
        s.Save();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
