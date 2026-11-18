using System.Windows.Controls;
using System.Windows.Input;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Views;

public partial class LoggingView : UserControl
{
    public LoggingView() => InitializeComponent();

    private void LogList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.OpenLogFileCommand.CanExecute(null))
            vm.OpenLogFileCommand.Execute(null);
    }
}
