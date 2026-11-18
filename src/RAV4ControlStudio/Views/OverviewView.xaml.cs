using System.Windows.Controls;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Views;

public partial class OverviewView : UserControl
{
    private bool _wired;

    public OverviewView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (_wired || e.NewValue is not MainViewModel vm) return;

            _wired = true;
            Chart.Series = vm.ChartSeriesList;
            Chart.Times = vm.ChartTimes;
            vm.ChartUpdated += Chart.Redraw;
        };
    }
}
