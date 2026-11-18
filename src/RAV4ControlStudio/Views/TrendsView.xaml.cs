using System.Windows.Controls;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Views;

public partial class TrendsView : UserControl
{
    private bool _wired;

    public TrendsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_wired || e.NewValue is not MainViewModel vm) return;
            _wired = true;
            Chart.Provider = () => vm.TrendSeries;
            Chart.Times = vm.ChartTimes;
            vm.ChartUpdated += Chart.Redraw;
        };
    }
}
