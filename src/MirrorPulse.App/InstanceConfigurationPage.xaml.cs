using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using XamlControl = Microsoft.UI.Xaml.Controls.Control;

namespace MirrorPulse.App;

/// <summary>Collects non-secret Worker settings and stores secrets through the owner Host.</summary>
public sealed partial class InstanceConfigurationPage : Page
{
    private MirrorPulseControlInstallation? _installation;
    private readonly Dictionary<string, TextBox> _rootLabels = new(StringComparer.Ordinal);
    private readonly List<(AdapterConfigurationField Field, XamlControl Input)> _configurationInputs = [];

    public InstanceConfigurationPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            string installId = InstallId.Parse(e.Parameter as string ?? string.Empty).ToString();
            MirrorPulseControlTopology topology = await AppControl.Client.GetAdapterTopologyAsync();
            _installation = topology.Installations.SingleOrDefault(item => item.InstallId == installId)
                ?? throw new FileNotFoundException("The selected Adapter installation was not found.");
            MirrorPulseControlAdapterForm form = _installation.Form
                ?? throw new InvalidDataException("The Host did not return the installed Adapter form.");

            string adapterId = _installation.AdapterId;
            bool isLocal = adapterId == "com.mirrorpulse.adapter.local";
            bool isSmb = adapterId == "com.mirrorpulse.adapter.smb";
            bool isWebDav = adapterId == "com.mirrorpulse.adapter.webdav";
            bool showCredentialInputs = !isLocal && !isSmb;
            bool hasOfficialSource = isLocal || isSmb || isWebDav;
            AdapterNameText.Text = adapterId + " · " + _installation.Version;
            AdapterLabelTextBox.Text = form.Title;
            SourcePathTextBox.Header = isLocal ? "Local source directory" : isSmb
                ? "Network share path" : "Server endpoint";
            SourcePathTextBox.PlaceholderText = isLocal ? @"C:\Users\You\Documents" : isSmb
                ? @"\\server\share" : "https://server.example/path/";
            SourcePathTextBox.Visibility = hasOfficialSource ? Visibility.Visible : Visibility.Collapsed;
            UsernameTextBox.Visibility = showCredentialInputs ? Visibility.Visible : Visibility.Collapsed;
            SecretBox.Visibility = showCredentialInputs ? Visibility.Visible : Visibility.Collapsed;
            SecretHintText.Visibility = showCredentialInputs ? Visibility.Visible : Visibility.Collapsed;
            SecretHintText.Text = "Optional password or token. MirrorPulse stores it in Windows Credential Manager.";

            if (form.ConfigurationFields.Count > 0)
            {
                SourcePathTextBox.Visibility = Visibility.Collapsed;
                UsernameTextBox.Visibility = Visibility.Collapsed;
                SecretBox.Visibility = Visibility.Collapsed;
                SecretHintText.Visibility = Visibility.Collapsed;
                AdditionalConfigurationTextBox.Visibility = Visibility.Collapsed;
                foreach (AdapterConfigurationField field in form.ConfigurationFields)
                {
                    XamlControl input = field.Kind switch
                    {
                        AdapterConfigurationFieldKind.Choice => new ComboBox
                        {
                            Header = field.Label,
                            ItemsSource = field.Options.ToArray(),
                            SelectedItem = field.DefaultValue,
                        },
                        AdapterConfigurationFieldKind.Toggle => new ToggleSwitch
                        {
                            Header = field.Label,
                            IsOn = bool.TryParse(field.DefaultValue, out bool enabled) && enabled,
                        },
                        AdapterConfigurationFieldKind.Secret => new PasswordBox
                        {
                            Header = field.Label,
                        },
                        _ => new TextBox
                        {
                            Header = field.Label,
                            Text = field.DefaultValue ?? string.Empty,
                        },
                    };
                    _configurationInputs.Add((field, input));
                    ConfigurationFieldsPanel.Children.Add(input);
                }
            }

            HashSet<string> usedLabels = topology.Roots.Select(root => root.Label)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (MirrorPulseControlAdapterRootDefinition definition in form.RootDefinitions)
            {
                string suggested = definition.DirectoryName;
                for (int suffix = 2; usedLabels.Contains(suggested); suffix++)
                {
                    suggested = $"{definition.DirectoryName} {suffix}";
                }

                usedLabels.Add(suggested);
                var box = new TextBox { Header = $"MirrorPulse folder · {definition.Label}", Text = suggested };
                _rootLabels.Add(definition.Key, box);
                RootLabelsPanel.Children.Add(box);
            }
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            CreateButton.IsEnabled = false;
        }
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installation is null)
        {
            return;
        }

        CreateButton.IsEnabled = false;
        try
        {
            string adapterId = _installation.AdapterId;
            bool usesManifestForm = _configurationInputs.Count > 0;
            string? sourceKey = usesManifestForm ? null : adapterId switch
            {
                "com.mirrorpulse.adapter.local" => "sourceDirectory",
                "com.mirrorpulse.adapter.smb" => "networkPath",
                "com.mirrorpulse.adapter.webdav" => "endpoint",
                _ => null,
            };
            string source = SourcePathTextBox.Text.Trim();
            if (sourceKey is not null && source.Length == 0)
            {
                throw new InvalidDataException("Enter a source path or endpoint.");
            }

            if (!usesManifestForm && adapterId == "com.mirrorpulse.adapter.local" &&
                !Directory.Exists(source))
            {
                throw new DirectoryNotFoundException("The local source directory does not exist.");
            }

            var configuration = AdditionalConfigurationTextBox.Visibility != Visibility.Visible ||
                string.IsNullOrWhiteSpace(AdditionalConfigurationTextBox.Text)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(
                    AdditionalConfigurationTextBox.Text) ?? throw new InvalidDataException(
                        "Additional settings must be a JSON object of string values.");
            if (configuration.ContainsKey("credentialReference"))
            {
                throw new InvalidDataException("Credential references are managed by MirrorPulse.");
            }

            if (sourceKey is not null)
            {
                configuration[sourceKey] = source;
            }
            if (UsernameTextBox.Visibility == Visibility.Visible)
            {
                string username = UsernameTextBox.Text.Trim();
                if (username.Length > 0)
                {
                    configuration["username"] = username;
                }
            }

            string? secret = SecretBox.Visibility == Visibility.Visible ? SecretBox.Password : null;
            foreach ((AdapterConfigurationField field, XamlControl input) in _configurationInputs)
            {
                if (input is PasswordBox password)
                {
                    secret = password.Password;
                }
                else
                {
                    string? value = input switch
                    {
                        TextBox box => box.Text.Trim(),
                        ComboBox combo => combo.SelectedItem as string,
                        ToggleSwitch toggle => toggle.IsOn ? "true" : "false",
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        configuration[field.Key] = value;
                    }
                }
            }

            var rootLabels = _rootLabels.ToDictionary(item => item.Key,
                item => item.Value.Text.Trim(), StringComparer.Ordinal);
            var request = new InstanceCreateArguments(
                _installation.InstallId, AdapterLabelTextBox.Text.Trim(),
                configuration, rootLabels, secret, StartWithMirrorPulseToggle.IsOn);
            MirrorPulseAppStatusResponse result = await AppControl.Client.CreateInstanceAsync(request);
            if (string.IsNullOrWhiteSpace(result.CreatedInstanceId))
            {
                throw new InvalidDataException("The Host did not return the new instance ID.");
            }

            SecretBox.Password = string.Empty;
            foreach ((_, XamlControl input) in _configurationInputs)
            {
                if (input is PasswordBox password)
                {
                    password.Password = string.Empty;
                }
            }
            Frame.Navigate(typeof(InstalledAdaptersPage));
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            CreateButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(InstalledAdaptersPage));

    private void ShowError(string message)
    {
        ConfigurationStatusBar.Severity = InfoBarSeverity.Error;
        ConfigurationStatusBar.Message = message;
        ConfigurationStatusBar.IsOpen = true;
    }
}
