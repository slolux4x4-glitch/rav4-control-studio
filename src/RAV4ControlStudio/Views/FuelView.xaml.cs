using System.Windows.Controls;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Views;

public partial class FuelView : UserControl
{
    private bool _wired;

    public FuelView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_wired || e.NewValue is not MainViewModel vm) return;
            _wired = true;
            Chart.Provider = () => vm.FuelSeries;
            Chart.Times = vm.ChartTimes;
            vm.ChartUpdated += Chart.Redraw;
        };
    }
}
