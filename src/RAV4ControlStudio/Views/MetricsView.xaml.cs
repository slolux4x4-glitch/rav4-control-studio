using System.Windows;
using System.Windows.Controls;

namespace RAV4ControlStudio.Views;

public partial class MetricsView : UserControl
{
    public MetricsView() => InitializeComponent();

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is null || DataContext is null) return;
        new MetricsFullScreenWindow(owner, DataContext).Show();
    }
}
