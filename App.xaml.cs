using System.Windows;
using System.Windows.Threading;
using UnrealManager.Services;

namespace UnrealManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    private bool _errorDialogOpen;
    private string? _lastErrorMessage;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Instance.Log("UNHANDLED ERROR: " + e.Exception.Message);
        e.Handled = true;

        // A recurring exception (e.g. thrown during layout/binding) would otherwise
        // open a new modal dialog on every render pass — show each distinct message once.
        if (_errorDialogOpen || e.Exception.Message == _lastErrorMessage) return;
        _lastErrorMessage = e.Exception.Message;
        _errorDialogOpen = true;
        try
        {
            MessageBox.Show(e.Exception.Message + "\n\n(Details in the Output Log.)",
                "Source Manager — Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _errorDialogOpen = false;
        }
    }
}
