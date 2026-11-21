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
        Loaded += (_, _) =>
        {
            if (Vm is { } vm && vm.ListPaneWidth >= 300) ListColumn.Width = new GridLength(vm.ListPaneWidth);
        };
    }

    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (Vm is { } vm) vm.ListPaneWidth = ListColumn.ActualWidth;
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
        if (row.IsTypeRow) { vm.ShowType(row.TypeKey!); return; }
        if (row.IsRemoved) return;
        if (row.IsDirectory)
        {
            if (vm.Mode != ListMode.Contents || row.HasLocation) { vm.FilterText = ""; vm.Mode = ListMode.Contents; }
            vm.NavigateTo(row.Node);
        }
        else if ((vm.Mode != ListMode.Contents || row.HasLocation) && row.Node.Parent is { } parent)
        {
            // From Largest files, Changes or a search, double-click jumps to the file's folder with it selected.
            vm.FilterText = "";
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
