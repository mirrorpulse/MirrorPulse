using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MirrorPulse.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MirrorPulse.App;

/// <summary>
/// The main content page displayed inside the application window.
/// Add your UI logic, event handlers, and data binding here.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        if (ProductInfo.Edition.DirectAdapterId is not null)
        {
            GetStartedButton.Content = "Open storage";
            Loaded += DirectEdition_Loaded;
        }
    }

    private async void DirectEdition_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DirectEdition_Loaded;
        await OpenDirectEditionAsync();
    }

    private async void GetStartedButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProductInfo.Edition.DirectAdapterId is null)
            Frame.Navigate(typeof(InstalledAdaptersPage));
        else
            await OpenDirectEditionAsync();
    }

    private async Task OpenDirectEditionAsync()
    {
        GetStartedButton.IsEnabled = false;
        try
        {
            var topology = await AppControl.Client.GetAdapterTopologyAsync();
            if (ReferenceEquals(Frame?.Content, this))
                Frame.Navigate(topology.Instances.Count == 0 ? typeof(InstanceConfigurationPage) : typeof(SyncStatusPage));
        }
        catch (Exception exception)
        {
            StartupStatusText.Text = exception.Message;
        }
        finally
        {
            GetStartedButton.IsEnabled = true;
        }
    }
}

