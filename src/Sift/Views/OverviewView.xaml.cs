using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sift.ViewModels;

namespace Sift.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        Treemap.DrillRequested += node => (DataContext as OverviewViewModel)?.NavigateTo(node);
    }

    private OverviewViewModel? Vm => DataContext as OverviewViewModel;

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    /// <summary>True while the user is typing in the filter box (so global shortcuts stand down).</summary>
    public bool IsTyping => FilterBox.IsKeyboardFocusWithin;

    private void ContentsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || vm.SelectedRow is not { } row) return;
        if (row.IsDirectory) vm.NavigateTo(row.Node);
        else if (vm.IsLargestMode && row.Node.Parent is { } parent)
        {
            // In "Largest files", double-click jumps to the file's folder with it selected.
            vm.Mode = ListMode.Contents;
            vm.NavigateTo(parent, select: row.Node);
        }
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Content: TextBlock { Tag: string key } })
            Vm?.SortCommand.Execute(key);
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.FilterText = "";
        FilterBox.Focus();
    }
}
