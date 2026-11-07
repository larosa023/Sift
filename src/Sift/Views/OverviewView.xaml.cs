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

    private void ContentsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is OverviewViewModel vm && vm.SelectedRow is { IsDirectory: true } row)
            vm.NavigateTo(row.Node);
    }
}
