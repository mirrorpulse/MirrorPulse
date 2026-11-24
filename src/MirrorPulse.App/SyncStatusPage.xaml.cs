using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MirrorPulse.Core;
using MirrorPulse.Core.Host;

namespace MirrorPulse.App;

/// <summary>Shows local product state and CfSharp's authoritative sync journal.</summary>
public sealed partial class SyncStatusPage : Page
{
    public SyncStatusPage()
    {
        InitializeComponent();
        if (ProductInfo.Edition.DirectAdapterId is not null)
            ConfigureStorageButton.Visibility = Visibility.Visible;
        Loaded += SyncStatusPage_Loaded;
    }

    private async void SyncStatusPage_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void ConfigureStorageButton_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(InstanceConfigurationPage));

    private void NotificationsButton_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(NotificationsPage));

    private async Task RefreshAsync()
    {
        try
        {
            MirrorPulseAppStatusResponse status = await AppControl.Client.GetStatusAsync();
            PendingUploadsText.Text = $"Pending uploads: {status.PendingUploads}";
            PendingConflictsText.Text =
                $"Pending conflicts: {status.PendingRemoteConflicts} remote, " +
                $"{status.PendingUploadConflicts} upload";
            var offline = status.Instances.Where(instance => !instance.Enabled).ToArray();
            OfflineNotice.IsOpen = offline.Length > 0;
            OfflineNotice.Message = offline.Length == 0
                ? string.Empty
                : $"{offline.Length} Adapter instance(s) remain visible locally but do not access the remote source. Enable them and restart MirrorPulse to resume synchronization.";
            InstanceStatusList.ItemsSource = status.Instances.Select(instance =>
            {
                string checkpoint = instance.CursorFingerprint is not null
                    ? $"Cursor {instance.CursorFingerprint} ({instance.CursorUpdatedAt:yyyy-MM-dd HH:mm})"
                    : "No remote cursor";
                string error = instance.LastErrorCode is null
                    ? string.Empty : $"  ·  Last error: {instance.LastErrorCode}";
                string progress = instance.TransferProgress is null
                    ? string.Empty
                    : $"  ·  {instance.TransferProgress.Operation}: " +
                      (instance.TransferProgress.TotalBytes is null
                          ? $"{instance.TransferProgress.BytesTransferred} bytes"
                          : $"{instance.TransferProgress.BytesTransferred}/{instance.TransferProgress.TotalBytes} bytes");
                string health = instance.HealthIssues is { Count: > 0 }
                    ? "  ·  " + string.Join(" ", instance.HealthIssues.Select(AdapterHealthText.Describe)) : string.Empty;
                return $"{instance.DisplayName}  ·  {instance.Phase}  ·  {checkpoint}{progress}{error}{health}";
            }).ToArray();
            StatusMessageText.Text = status.Instances.Count == 0
                ? "No Adapter instances are configured."
                : "Cursor values are shown as fingerprints to keep opaque tokens private.";
        }
        catch (Exception exception)
        {
            StatusMessageText.Text = $"Could not read sync status: {exception.Message}";
        }
    }
}
