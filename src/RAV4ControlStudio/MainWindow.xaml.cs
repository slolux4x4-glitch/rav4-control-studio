using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using RAV4ControlStudio.ViewModels;
using RAV4ControlStudio.Views;

namespace RAV4ControlStudio;

public partial class MainWindow : Window
{
    // Page keys match the sidebar order (CAN Monitor is disabled and has no page).
    private static readonly string[] NavKeys =
    {
        "Overview", "Connection", "Live", "Metrics", "Trends", "Fuel", "Diagnostics", "Logging", "CAN", "ECU", "Discovery", "Tests", "Reports",
    };

    // Pages that only show data while connected to an ECU.
    private static readonly HashSet<string> LivePages = new() { "Live", "Metrics", "Trends", "Fuel", "Diagnostics", "ECU", "Discovery", "Tests" };

    private string _currentKey = "Overview";
    private readonly MainViewModel _vm = new();
    private readonly Dictionary<string, UserControl> _pages;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _pages = new Dictionary<string, UserControl>
        {
            ["Overview"] = new OverviewView(),
            ["Connection"] = new ConnectionView(),
            ["Live"] = new LiveDataView(),
            ["Metrics"] = new MetricsView(),
            ["Trends"] = new TrendsView(),
            ["Fuel"] = new FuelView(),
            ["Discovery"] = new DiscoveryView(),
            ["Diagnostics"] = new DiagnosticsView(),
            ["Logging"] = new LoggingView(),
            ["ECU"] = new EcuInfoView(),
            ["Tests"] = new TestsView(),
            ["Reports"] = new ReportsView(),
            ["Settings"] = new SettingsView(),
            ["About"] = new AboutView(),
        };

        _vm.NavigationRequested += key =>
        {
            if (key is "Settings" or "About")
            {
                NavList.SelectedIndex = -1;
                _currentKey = key;
                _vm.CurrentPage = key;
                PageHost.Content = _pages[key];
                UpdateBanner();
                return;
            }

            int index = Array.IndexOf(NavKeys, key);
            if (index >= 0) NavList.SelectedIndex = index;
        };

        Closed += (_, _) => _vm.Dispose();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsConnected) or "") UpdateBanner();
        };

        NavList.SelectedIndex = 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Windows 11: ask the window manager for rounded corners (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2).
        // Older Windows versions simply ignore this.
        try
        {
            int preference = 2;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref preference, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = NavList.SelectedIndex;
        if (index < 0 || index >= NavKeys.Length) return;

        string key = NavKeys[index];
        _currentKey = key;
        _vm.CurrentPage = key;
        if (_pages.TryGetValue(key, out var page))
            PageHost.Content = page;
        UpdateBanner();
    }

    private void UpdateBanner() =>
        NotConnectedBanner.Visibility = !_vm.IsConnected && LivePages.Contains(_currentKey) ? Visibility.Visible : Visibility.Collapsed;

    private void BannerConnect_Click(object sender, RoutedEventArgs e) => NavList.SelectedIndex = Array.IndexOf(NavKeys, "Connection");

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
