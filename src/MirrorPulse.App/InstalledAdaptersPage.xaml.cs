using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.App;

/// <summary>
/// Lists installed Adapter packages and their configured instances.
/// </summary>
public sealed partial class InstalledAdaptersPage : Page
{
    private MirrorPulseControlTopology _topology = new([], [], [], []);
    private MirrorPulseControlInstance[] _instances = [];
    private MirrorPulseControlInstallation[] _versions = [];
    private bool _loading;

    public InstalledAdaptersPage()
    {
        InitializeComponent();
        Loaded += InstalledAdaptersPage_Loaded;
    }

    private async void InstalledAdaptersPage_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(string? selectedInstanceId = null)
    {
        try
        {
            MirrorPulseControlTopology topology = await AppControl.Client.GetAdapterTopologyAsync();
            _loading = true;
            _topology = topology;
            _instances = topology.Instances.ToArray();
            InstalledAdaptersList.ItemsSource = topology.Installations.Select(installation =>
            {
                string instances = string.Join(", ", topology.Instances
                    .Where(instance => instance.InstallId == installation.InstallId)
                    .Select(instance => instance.DisplayName));
                return $"{installation.AdapterId}  ·  {installation.Version}  ·  {installation.InstallId}" +
                    (instances.Length == 0 ? string.Empty : $"  ·  {instances}");
            }).ToArray();
            EmptyStateText.Visibility = topology.Installations.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            InstanceSelector.ItemsSource = _instances.Select(instance =>
                $"{instance.DisplayName}  ·  {instance.InstanceId}" +
                (instance.HealthIssues?.Any(issue => issue.RequiresConfiguration) == true ? "  ·  Needs configuration" : string.Empty)).ToArray();
            int index = selectedInstanceId is null ? 0 :
                Array.FindIndex(_instances, instance => instance.InstanceId == selectedInstanceId);
            InstanceSelector.SelectedIndex = _instances.Length == 0 ? -1 : Math.Max(0, index);
            UpdateInstanceSelection();
            _loading = false;
        }
        catch (Exception exception)
        {
            _loading = false;
            EmptyStateText.Text = $"Could not read installed Adapters: {exception.Message}";
            EmptyStateText.Visibility = Visibility.Visible;
        }
    }

    private void InstanceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateInstanceSelection();
        }
    }

    private void UpdateInstanceSelection()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            ApplyInstanceSelection();
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void ApplyInstanceSelection()
    {
        int index = InstanceSelector.SelectedIndex;
        if (index < 0 || index >= _instances.Length)
        {
            _versions = [];
            VersionSelector.ItemsSource = Array.Empty<string>();
            UseVersionButton.IsEnabled = false;
            AdapterEnableToggle.IsEnabled = false;
            ConfigureInstanceButton.IsEnabled = false;
            AdapterHealthNotice.IsOpen = false;
            return;
        }

        MirrorPulseControlInstance instance = _instances[index];
        ConfigureInstanceButton.IsEnabled = instance.ConfigurationRevision is not null;
        AdapterHealthNotice.Title = instance.HealthIssues?.Any(issue => issue.RequiresConfiguration) == true
            ? "Configuration needs attention" : "Adapter health";
        AdapterHealthNotice.Message = string.Join(Environment.NewLine, (instance.HealthIssues ?? []).Select(AdapterHealthText.Describe));
        AdapterHealthNotice.IsOpen = instance.HealthIssues is { Count: > 0 };
        _versions = _topology.Installations.Where(installation => installation.AdapterId == instance.AdapterId)
            .OrderBy(installation => installation.Version, StringComparer.Ordinal)
            .ToArray();
        VersionSelector.ItemsSource = _versions.Select(installation =>
            $"{installation.Version}  ·  {installation.InstallId}").ToArray();
        VersionSelector.SelectedIndex = Array.FindIndex(_versions, installation =>
            installation.InstallId == instance.InstallId);
        AdapterEnableToggle.IsEnabled = true;
        AdapterEnableToggle.IsOn = instance.Enabled;
        UpdateVersionButton();
    }

    private void VersionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateVersionButton();

    private void UpdateVersionButton()
    {
        int instanceIndex = InstanceSelector.SelectedIndex;
        int versionIndex = VersionSelector.SelectedIndex;
        UseVersionButton.IsEnabled = instanceIndex >= 0 && instanceIndex < _instances.Length &&
            versionIndex >= 0 && versionIndex < _versions.Length &&
            _versions[versionIndex].InstallId != _instances[instanceIndex].InstallId;
    }

    private async void UseVersionButton_Click(object sender, RoutedEventArgs e)
    {
        int instanceIndex = InstanceSelector.SelectedIndex;
        int versionIndex = VersionSelector.SelectedIndex;
        if (instanceIndex < 0 || instanceIndex >= _instances.Length ||
            versionIndex < 0 || versionIndex >= _versions.Length)
        {
            return;
        }

        string instanceId = _instances[instanceIndex].InstanceId;
        try
        {
            await AppControl.Client.SelectInstallationAsync(instanceId,
                _versions[versionIndex].InstallId);
            ActionStatusText.Text = "The instance version selection was saved. Restart MirrorPulse to run this version.";
            await RefreshAsync(instanceId);
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = $"Could not select the Adapter version: {exception.Message}";
        }
    }

    private async void AdapterEnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        int index = InstanceSelector.SelectedIndex;
        if (_loading || index < 0 || index >= _instances.Length)
        {
            return;
        }

        string instanceId = _instances[index].InstanceId;
        try
        {
            await AppControl.Client.SetInstanceEnabledAsync(instanceId, AdapterEnableToggle.IsOn);
            ActionStatusText.Text = "The startup selection was saved. Restart MirrorPulse to apply it.";
            await RefreshAsync(instanceId);
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = $"Could not change the startup selection: {exception.Message}";
            _loading = true;
            AdapterEnableToggle.IsOn = _instances[index].Enabled;
            _loading = false;
        }
    }

    private void SyncStatusButton_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SyncStatusPage));

    private void InstallAdapterButton_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(AdapterInstallPage));

    private void ConfigureInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        int index = InstanceSelector.SelectedIndex;
        if (index >= 0 && index < _instances.Length && _instances[index].ConfigurationRevision is not null)
            Frame.Navigate(typeof(InstanceConfigurationPage), InstanceId.Parse(_instances[index].InstanceId));
    }

    private void CreateInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        int index = InstalledAdaptersList.SelectedIndex;
        if (index < 0 || index >= _topology.Installations.Count)
        {
            ActionStatusText.Text = "Select an installed Adapter first.";
            return;
        }

        Frame.Navigate(typeof(InstanceConfigurationPage), _topology.Installations[index].InstallId);
    }
}
