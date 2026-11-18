using System.Windows;
using System.Windows.Threading;

namespace RAV4ControlStudio;

public partial class App : Application
{
    public App()
    {
        // Show unexpected errors instead of silently closing the app.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Unexpected error:\n\n{e.Exception.Message}",
            "RAV4 Control Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
