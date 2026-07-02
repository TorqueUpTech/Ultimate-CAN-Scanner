using System.Windows;
using System.Windows.Threading;

namespace IxxatCanTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        // Keep the app alive so a transient error doesn't lose the session.
        e.Handled = true;
    }
}
