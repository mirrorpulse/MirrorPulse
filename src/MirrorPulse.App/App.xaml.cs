using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.AppLifecycle;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Host;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MirrorPulse.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;
    private Window? _unsupportedWindow;
    internal MainWindow CurrentWindow => _window ?? throw new InvalidOperationException(
        "The MirrorPulse window has not been created.");
    private DispatcherQueueTimer? _notificationTimer;
    private readonly HashSet<string> _shownNotifications = new(StringComparer.Ordinal);
    private bool _checkingNotifications;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
#if MIRRORPULSE_SHELL_PROBE
        MirrorPulsePackagedShellProbeProgress.RecordFromEnvironment(MirrorPulsePackagedShellProbeStage.AppConstructing);
#endif
        InitializeComponent();
#if MIRRORPULSE_SHELL_PROBE
        MirrorPulsePackagedShellProbeProgress.RecordFromEnvironment(MirrorPulsePackagedShellProbeStage.AppInitialized);
#endif
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
#if MIRRORPULSE_SHELL_PROBE
        MirrorPulsePackagedShellProbeProgress.RecordFromEnvironment(MirrorPulsePackagedShellProbeStage.AppLaunched);
#endif
        if (!MirrorPulsePlatform.IsCurrentSupported())
        {
            var close = new Button { Content = "Close" };
            var unsupported = _unsupportedWindow = new Window
            {
                Title = "MirrorPulse",
                Content = new StackPanel
                {
                    Padding = new Thickness(32),
                    Spacing = 16,
                    Children = { new TextBlock { Text = MirrorPulsePlatform.Requirement,
                        TextWrapping = TextWrapping.Wrap }, close },
                },
            };
            close.Click += (_, _) => _unsupportedWindow?.Close();
            unsupported.Closed += (_, _) => Exit();
            unsupported.Activate();
            return;
        }
#if MIRRORPULSE_SHELL_PROBE
        string? probeResult = Environment.GetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT");
        if (!string.IsNullOrWhiteSpace(probeResult))
        {
            MirrorPulsePackagedShellProbeProgress.Record(probeResult, MirrorPulsePackagedShellProbeStage.ProbeSelected);
            _ = MirrorPulsePackagedShellProbe.RunAsync(probeResult, this);
            return;
        }
#endif
        _window = new MainWindow();
        _window.Activate();
        AppActivationArguments activation = Microsoft.Windows.AppLifecycle.AppInstance
            .GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.File &&
            activation.Data is IFileActivatedEventArgs files)
        {
            string? packagePath = files.Files.Select(file => file.Path).FirstOrDefault(path =>
                string.Equals(System.IO.Path.GetExtension(path), ".mpadapter",
                    StringComparison.OrdinalIgnoreCase));
            if (packagePath is not null)
            {
                _window.NavigateToAdapterInstall(packagePath);
            }
        }
        _notificationTimer = _window.DispatcherQueue.CreateTimer();
        _notificationTimer.Interval = TimeSpan.FromSeconds(2);
        _notificationTimer.Tick += NotificationTimer_Tick;
        _notificationTimer.Start();
    }

    private async void NotificationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_checkingNotifications || _window is null)
        {
            return;
        }

        _checkingNotifications = true;
        try
        {
            MirrorPulseAppStatusResponse status = await MirrorPulseAppStatusPipe.RequestAsync();
            MirrorPulseAppNotification? next = status.Notifications.FirstOrDefault(item =>
                !item.Snoozed && !_shownNotifications.Contains(item.ConflictId));
            if (next is null)
            {
                return;
            }

            bool snooze = await _window.ShowConflictNotificationAsync(next);
            if (snooze)
            {
                await MirrorPulseAppStatusPipe.SnoozeAsync(Guid.Parse(next.ConflictId));
            }

            _shownNotifications.Add(next.ConflictId);
        }
        catch (Exception)
        {
            // The Host may be offline while WinUI remains open; the next tick retries.
        }
        finally
        {
            _checkingNotifications = false;
        }
    }
}

