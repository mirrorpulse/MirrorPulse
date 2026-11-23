using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MirrorPulse.Core;
using MirrorPulse.Core.Host;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MirrorPulse.App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = ProductInfo.Name;
        AppTitleBar.Title = ProductInfo.Name;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    public void NavigateToAdapterInstall(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        RootFrame.Navigate(typeof(AdapterInstallPage), packagePath);
    }

    public async Task<bool> ShowConflictNotificationAsync(MirrorPulseAppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var dialog = new ContentDialog
        {
            XamlRoot = RootFrame.XamlRoot,
            Title = "File conflict needs attention",
            Content = $"{notification.RelativePath} has a pending remote conflict.",
            PrimaryButtonText = "Open notifications",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            RootFrame.Navigate(typeof(NotificationsPage));
            return false;
        }

        return true;
    }
}

