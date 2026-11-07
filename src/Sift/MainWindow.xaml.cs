using System.Windows;
using System.Windows.Input;
using Sift.Themes;
using Sift.ViewModels;

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
        Closing += (_, _) => SaveWindow();
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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Back && _vm.CurrentPage == Page.Overview && Keyboard.FocusedElement is not System.Windows.Controls.TextBox
                && _vm.Overview.UpCommand.CanExecute(null))
            {
                _vm.Overview.UpCommand.Execute(null);
                e.Handled = true;
            }
        };
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
