using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using MirrorPulse.Core.Host;

namespace MirrorPulse.App;

/// <summary>
/// Provides the user flow for installing a packaged Adapter.
/// </summary>
public sealed partial class AdapterInstallPage : Page
{
    public AdapterInstallPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string packagePath &&
            string.Equals(Path.GetExtension(packagePath), ".mpadapter", StringComparison.OrdinalIgnoreCase))
        {
            PackagePathTextBox.Text = packagePath;
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(((App)Application.Current).CurrentWindow.AppWindow.Id)
            {
                FileTypeFilter = { ".mpadapter" },
                CommitButtonText = "Select Adapter package",
            };
            PickFileResult? result = await picker.PickSingleFileAsync();
            if (result is not null)
            {
                PackagePathTextBox.Text = result.Path;
            }
        }
        catch (Exception exception)
        {
            InstallStatusBar.Severity = InfoBarSeverity.Error;
            InstallStatusBar.Message = $"Could not open the file picker: {exception.Message}";
            InstallStatusBar.IsOpen = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(InstalledAdaptersPage));

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallStatusBar.IsOpen = true;
        if (string.IsNullOrWhiteSpace(PackagePathTextBox.Text))
        {
            InstallStatusBar.Message = "Choose a .mpadapter package first.";
            return;
        }

        InstallButton.IsEnabled = false;
        try
        {
            MirrorPulseAppStatusResponse result = await AppControl.Client.InstallAsync(
                PackagePathTextBox.Text.Trim());
            if (string.IsNullOrWhiteSpace(result.InstalledAdapterId))
            {
                throw new InvalidDataException("The Host did not return the new installation ID.");
            }
            InstallStatusBar.Severity = InfoBarSeverity.Success;
            InstallStatusBar.Message = "The signed Adapter is installed. Configure its first instance.";
            Frame.Navigate(typeof(InstanceConfigurationPage), result.InstalledAdapterId);
        }
        catch (Exception exception)
        {
            InstallStatusBar.Severity = InfoBarSeverity.Error;
            InstallStatusBar.Message = $"Could not install the Adapter: {exception.Message}";
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }
}
