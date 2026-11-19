using System.Windows;
using System.Windows.Input;

namespace RAV4ControlStudio.Views;

/// <summary>
/// The four Metrics dials filling the screen the app is on. Any key, mouse button or touch closes it,
/// as does switching to another window.
/// </summary>
public partial class MetricsFullScreenWindow : Window
{
    private bool _closing;

    public MetricsFullScreenWindow(Window owner, object dataContext)
    {
        InitializeComponent();
        Owner = owner;
        DataContext = dataContext;

        // Open on the same monitor as the app: start inside its bounds, then maximise there.
        WindowStartupLocation = WindowStartupLocation.Manual;
        var bounds = owner.WindowState == WindowState.Maximized ? owner.RestoreBounds : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        Left = bounds.Left + 20;
        Top = bounds.Top + 20;
        Width = 800;
        Height = 600;
        WindowState = WindowState.Maximized;

        Loaded += (_, _) =>
        {
            // The page is laid out for about 1,500 pixels across; enlarge everything evenly on wider screens.
            double scale = Math.Max(1, ActualWidth / 1500);
            Gauges.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
            Activate();
            Focus();
        };
        PreviewKeyDown += (_, e) => { e.Handled = true; Exit(); };
        PreviewMouseDown += (_, e) => { e.Handled = true; Exit(); };
        PreviewTouchDown += (_, e) => { e.Handled = true; Exit(); };
        Deactivated += (_, _) => Exit();
    }

    private void Exit()
    {
        if (_closing) return;
        _closing = true;
        Owner?.Activate();
        Close();
    }
}
