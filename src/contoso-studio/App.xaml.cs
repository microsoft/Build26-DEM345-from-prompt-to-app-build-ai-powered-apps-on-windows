using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VideoStudio;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Services.Log.Error($"Unhandled: {e.Exception.Message}", e.Exception);
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ContosoStudio_crash.log"),
            $"\n[{DateTime.Now:HH:mm:ss}] UNHANDLED: {e.Exception}\n");
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Services.Log.Error($"Unobserved task: {e.Exception.InnerException?.Message ?? e.Exception.Message}", e.Exception);
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ContosoStudio_crash.log"),
            $"\n[{DateTime.Now:HH:mm:ss}] UNOBSERVED: {e.Exception}\n");
        e.SetObserved();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Services.LanguageModelRegistry.EnsureFoundryCatalogLoadedAsync(ui);
    }
}
