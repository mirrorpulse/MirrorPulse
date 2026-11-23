using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.App;

/// <summary>Reads durable pending conflict projections through the current-user Host channel.</summary>
public sealed partial class NotificationsPage : Page
{
    private IReadOnlyList<MirrorPulseAppNotification> _notifications = [];

    public NotificationsPage()
    {
        InitializeComponent();
        Loaded += NotificationsPage_Loaded;
    }

    private async void NotificationsPage_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void NotificationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = NotificationsList.SelectedIndex;
        bool selected = index >= 0 && index < _notifications.Count;
        bool conflict = selected;
        SnoozeButton.IsEnabled = selected && !_notifications[index].Snoozed;
        KeepLocalButton.IsEnabled = conflict;
        KeepRemoteButton.IsEnabled = conflict;
        KeepBothButton.IsEnabled = conflict;
        RetryButton.IsEnabled = conflict;
        DeleteLocalButton.IsEnabled = conflict;
    }

    private async void SnoozeButton_Click(object sender, RoutedEventArgs e)
    {
        int index = NotificationsList.SelectedIndex;
        if (index < 0 || index >= _notifications.Count || _notifications[index].Snoozed)
        {
            return;
        }

        try
        {
            MirrorPulseAppStatusResponse status = await AppControl.Client.SnoozeConflictAsync(
                Guid.Parse(_notifications[index].ConflictId));
            Show(status);
        }
        catch (Exception exception)
        {
            NotificationsMessageText.Text = $"Could not snooze notification: {exception.Message}";
        }
    }

    private Task ResolveSelectedAsync(MirrorPulseConflictAction action) => ResolveAsync(action);

    private async Task ResolveAsync(MirrorPulseConflictAction action)
    {
        int index = NotificationsList.SelectedIndex;
        if (index < 0 || index >= _notifications.Count)
        {
            return;
        }

        try
        {
            MirrorPulseAppStatusResponse status = await AppControl.Client.ResolveConflictAsync(
                Guid.Parse(_notifications[index].ConflictId), action);
            Show(status);
        }
        catch (Exception exception)
        {
            NotificationsMessageText.Text = $"Could not resolve conflict: {exception.Message}";
        }
    }

    private async void KeepLocalButton_Click(object sender, RoutedEventArgs e) =>
        await ResolveSelectedAsync(MirrorPulseConflictAction.KeepLocal);

    private async void KeepRemoteButton_Click(object sender, RoutedEventArgs e) =>
        await ResolveSelectedAsync(MirrorPulseConflictAction.KeepRemote);

    private async void KeepBothButton_Click(object sender, RoutedEventArgs e) =>
        await ResolveSelectedAsync(MirrorPulseConflictAction.KeepBoth);

    private async void RetryButton_Click(object sender, RoutedEventArgs e) =>
        await ResolveSelectedAsync(MirrorPulseConflictAction.Retry);

    private async void DeleteLocalButton_Click(object sender, RoutedEventArgs e) =>
        await ResolveSelectedAsync(MirrorPulseConflictAction.DeleteLocal);

    private async Task RefreshAsync()
    {
        try
        {
            Show(await AppControl.Client.GetStatusAsync());
        }
        catch (Exception exception)
        {
            NotificationsMessageText.Text = $"Could not read notifications: {exception.Message}";
        }
    }

    private void Show(MirrorPulseAppStatusResponse status)
    {
        _notifications = status.Notifications;
        NotificationsList.ItemsSource = _notifications.Select(item =>
            $"{(item.Source == MirrorPulseConflictSource.Upload ? "Upload" : "Remote")}  ·  " +
            $"{item.RelativePath}  ·  {item.DetectedAt:yyyy-MM-dd HH:mm}  ·  " +
            (item.Snoozed ? "Snoozed" : "Needs attention")).ToArray();
        SnoozeButton.IsEnabled = false;
        KeepLocalButton.IsEnabled = false;
        KeepRemoteButton.IsEnabled = false;
        KeepBothButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        DeleteLocalButton.IsEnabled = false;
        NotificationsMessageText.Text = _notifications.Count == 0
            ? "No pending conflict notifications."
            : $"{_notifications.Count(item => !item.Snoozed)} conflict notifications need attention.";
    }
}
