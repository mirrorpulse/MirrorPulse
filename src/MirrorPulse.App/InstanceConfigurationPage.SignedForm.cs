using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using Windows.System;

namespace MirrorPulse.App;

public sealed partial class InstanceConfigurationPage
{
    private readonly Dictionary<string, Func<string>> _signedValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PasswordBox Input, CheckBox? Clear)> _signedPasswords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AdapterAuthorizationStatus> _authorizations = new(StringComparer.Ordinal);
    private bool _authorizationRequestRunning;
    private bool _configurationPageActive;

    private void LoadSignedForm(MirrorPulseControlAdapterForm form, string installationDirectory)
    {
        AdapterPackageLocale presentation = form.Presentation!;
        AdapterNameText.Visibility = Visibility.Collapsed;
        AdapterPresentationPanel.Visibility = Visibility.Visible;
        if (form.LogoPath is { } logo)
            AdapterPresentationPanel.Children.Add(CreatePackageImage(installationDirectory, logo, presentation.Title, 64));
        AdapterPresentationPanel.Children.Add(new TextBlock { Text = presentation.Title, FontSize = 24, TextWrapping = TextWrapping.Wrap });
        AdapterPresentationPanel.Children.Add(new TextBlock { Text = presentation.Description, TextWrapping = TextWrapping.Wrap });

        foreach (AdapterPackageFormControl control in presentation.Controls)
        {
            string label = control.Required ? $"{control.Label} *" : control.Label;
            FrameworkElement element;
            switch (control.Kind)
            {
                case AdapterPackageFormKind.Heading:
                    element = new TextBlock { Text = label, FontSize = 20, TextWrapping = TextWrapping.Wrap };
                    break;
                case AdapterPackageFormKind.Paragraph:
                    element = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
                    break;
                case AdapterPackageFormKind.Image:
                    element = CreatePackageImage(installationDirectory, control.AssetPath!, label, 192);
                    break;
                case AdapterPackageFormKind.Password:
                    element = CreatePasswordInput(control, label);
                    break;
                case AdapterPackageFormKind.Callback:
                    element = CreateAuthorizationControl(control);
                    break;
                default:
                    element = CreateValueInput(control, label);
                    break;
            }

            ConfigurationFieldsPanel.Children.Add(element);
            if (control.Key is { } field && control.Kind != AdapterPackageFormKind.Callback &&
                _instance?.HealthIssues?.Where(issue => issue.FieldKey == field).ToArray() is { Length: > 0 } issues)
            {
                string details = string.Join(Environment.NewLine, issues.Select(AdapterHealthText.Describe));
                AutomationProperties.SetHelpText(element, details);
                ConfigurationFieldsPanel.Children.Add(new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Warning,
                    Message = details,
                });
            }
        }
    }

    private StackPanel CreateAuthorizationControl(AdapterPackageFormControl control)
    {
        string key = control.Key!;
        var panel = new StackPanel { Spacing = 6 };
        var start = new Button { Content = control.Label };
        var open = new Button { Content = "Open authorization page", Visibility = Visibility.Collapsed };
        var check = new Button { Content = "Check authorization", Visibility = Visibility.Collapsed };
        var cancel = new Button { Content = "Cancel authorization", Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var buttons = new StackPanel { Spacing = 8 };
        buttons.Children.Add(start);
        buttons.Children.Add(open);
        buttons.Children.Add(check);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        panel.Children.Add(status);

        start.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            string? draftId = _instance is null ? _authorizations.Values.FirstOrDefault(IsActiveAuthorization)?.DeploymentId : null;
            var result = await AppControl.Client.StartInstanceAuthorizationAsync(new(_installation!.InstallId, key,
                ReadSignedValues(), _instance?.InstanceId, _instance?.ConfigurationRevision, draftId));
            if (!_configurationPageActive)
            {
                await CancelAbandonedAuthorizationAsync(result);
                return;
            }
            SetStatus(result);
            if (result.InteractionUri is { } page) await LaunchAsync(page);
        });
        open.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            if (_authorizations[key].InteractionUri is { } page) await LaunchAsync(page);
        });
        check.Click += async (_, _) => await ExecuteAsync(async () => SetStatus(await AppControl.Client
            .PollInstanceAuthorizationAsync(_authorizations[key].AuthorizationId)));
        cancel.Click += async (_, _) => await ExecuteAsync(async () => SetStatus(await AppControl.Client
            .CancelInstanceAuthorizationAsync(_authorizations[key].AuthorizationId)));
        return panel;

        void SetStatus(AdapterAuthorizationStatus result)
        {
            if (!_configurationPageActive) return;
            _authorizations[key] = result;
            bool pending = result.Phase == AdapterAuthorizationPhase.Pending;
            bool ready = result.Phase == AdapterAuthorizationPhase.ReadyToSave;
            start.Visibility = pending || ready ? Visibility.Collapsed : Visibility.Visible;
            open.Visibility = pending && result.InteractionUri is not null ? Visibility.Visible : Visibility.Collapsed;
            check.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            cancel.Visibility = pending || ready ? Visibility.Visible : Visibility.Collapsed;
            status.Text = result.Phase switch
            {
                AdapterAuthorizationPhase.Pending => "Finish authorizing, then check authorization here.",
                AdapterAuthorizationPhase.ReadyToSave => "Authorization is ready. Save to apply it.",
                AdapterAuthorizationPhase.Cancelled => "Authorization cancelled.",
                AdapterAuthorizationPhase.Expired => "Authorization expired. Start again to continue.",
                _ => "Authorization could not be completed.",
            };
        }

        async Task ExecuteAsync(Func<Task> operation)
        {
            if (_authorizationRequestRunning || !CreateButton.IsEnabled) return;
            _authorizationRequestRunning = true;
            ConfigurationStatusBar.IsOpen = false;
            try { await operation(); }
            catch (Exception exception) { ShowError(exception.Message); }
            finally { _authorizationRequestRunning = false; }
        }

        async Task LaunchAsync(Uri page)
        {
            if (!await Launcher.LaunchUriAsync(page)) ShowError("The authorization page could not be opened.");
        }
    }

    private static bool IsActiveAuthorization(AdapterAuthorizationStatus status) =>
        status.Phase is AdapterAuthorizationPhase.Pending or AdapterAuthorizationPhase.ReadyToSave;

    private static async Task CancelAbandonedAuthorizationAsync(AdapterAuthorizationStatus status)
    {
        if (!IsActiveAuthorization(status)) return;
        try { await AppControl.Client.CancelInstanceAuthorizationAsync(status.AuthorizationId); }
        catch (Exception) { /* The Host expires drafts that cannot be cancelled while navigating away. */ }
    }

    private StackPanel CreatePasswordInput(AdapterPackageFormControl control, string label)
    {
        bool configured = _instance?.ConfiguredPasswordFields?.Contains(control.Key!, StringComparer.Ordinal) is true;
        var input = new PasswordBox { Header = label, MaxLength = 16384 };
        if (configured) input.PlaceholderText = "Saved credential";
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(input);
        CheckBox? clear = null;
        if (configured && !control.Required)
        {
            clear = new CheckBox { Content = $"Remove saved value for {control.Label}" };
            panel.Children.Add(clear);
        }
        _signedPasswords.Add(control.Key!, (input, clear));
        return panel;
    }

    private FrameworkElement CreateValueInput(AdapterPackageFormControl control, string label)
    {
        string key = control.Key!;
        string? value = _instance?.Configuration.GetValueOrDefault(key) ?? control.DefaultValue;
        switch (control.Kind)
        {
            case AdapterPackageFormKind.Text:
                var text = new TextBox { Header = label, Text = value ?? string.Empty, MaxLength = 16384 };
                _signedValues.Add(key, () => text.Text);
                return text;
            case AdapterPackageFormKind.CheckBox:
                var check = new CheckBox
                {
                    Content = label,
                    IsThreeState = !control.Required,
                    IsChecked = value is null && !control.Required ? null : value == "true",
                };
                _signedValues.Add(key, () => check.IsChecked switch { true => "true", false => "false", _ => string.Empty });
                return check;
            case AdapterPackageFormKind.SingleChoice:
                var radios = new RadioButtons { Header = label };
                if (!control.Required) radios.Items.Add(new RadioButton { Content = "None" });
                foreach (AdapterPackageFormOption option in control.Options!)
                {
                    var radio = new RadioButton { Tag = option.Id, Content = option.Label };
                    radios.Items.Add(radio);
                    if (option.Id == value) radios.SelectedItem = radio;
                }
                _signedValues.Add(key, () => (radios.SelectedItem as RadioButton)?.Tag as string ?? string.Empty);
                return radios;
            default:
                var choices = new StackPanel();
                choices.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
                var selected = (value ?? string.Empty).Split(',').ToHashSet(StringComparer.Ordinal);
                var boxes = control.Options!.Select(option => new CheckBox
                {
                    Content = option.Label,
                    Tag = option.Id,
                    IsChecked = selected.Contains(option.Id),
                }).ToArray();
                foreach (CheckBox choice in boxes) choices.Children.Add(choice);
                _signedValues.Add(key, () => string.Join(',', boxes.Where(box => box.IsChecked == true).Select(box => (string)box.Tag)));
                return choices;
        }
    }

    private static FrameworkElement CreatePackageImage(string installationDirectory, string relativePath, string label, int maxHeight)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));
        string path = Path.GetFullPath(relativePath, root);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(path, UriKind.Absolute, out Uri? location) || !location.IsFile || location.IsUnc ||
            !File.Exists(path))
            return new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };

        for (string? item = path; item is not null; item = Path.GetDirectoryName(item))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                return new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            if (string.Equals(item, root, StringComparison.OrdinalIgnoreCase)) break;
        }

        var image = new Image { Source = new BitmapImage(location), MaxHeight = maxHeight, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(image, label);
        return image;
    }

    private async Task SaveSignedFormAsync()
    {
        if (_authorizations.Values.Any(item => item.Phase == AdapterAuthorizationPhase.Pending))
            throw new InvalidDataException("Finish or cancel the pending authorization before saving.");
        var values = ReadSignedValues();
        Guid[]? authorizationIds = _authorizations.Values.Where(item => item.Phase == AdapterAuthorizationPhase.ReadyToSave)
            .Select(item => item.AuthorizationId).ToArray();
        if (authorizationIds.Length == 0) authorizationIds = null;
        var passwords = new Dictionary<string, string?>(StringComparer.Ordinal);
        var roots = _rootLabels.ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim(), StringComparer.Ordinal);
        foreach ((string key, (PasswordBox input, CheckBox? clear)) in _signedPasswords)
        {
            if (clear?.IsChecked is true && input.Password.Length != 0)
                throw new InvalidDataException("Choose either to remove or replace a saved password.");
            if (clear?.IsChecked is true) passwords.Add(key, null);
            if (input.Password.Length > 0) passwords.Add(key, input.Password);
        }

        string name = AdapterLabelTextBox.Text.Trim();
        if (_instance is null)
        {
            var created = await AppControl.Client.CreateInstanceAsync(new InstanceCreateArguments(
                _installation!.InstallId, name, values, roots, null, StartWithMirrorPulseToggle.IsOn, passwords, authorizationIds));
            if (string.IsNullOrWhiteSpace(created.CreatedInstanceId))
                throw new InvalidDataException("The Host did not return the new instance ID.");
        }
        else
        {
            await AppControl.Client.ConfigureInstanceAsync(new InstanceConfigureArguments(_instance.InstanceId,
                name, values, roots, SecretFields: passwords, ExpectedConfigurationRevision: _instance.ConfigurationRevision,
                AuthorizationIds: authorizationIds));
        }

        _authorizations.Clear();
        foreach (var input in _signedPasswords.Values) input.Input.Password = string.Empty;
    }

    private Dictionary<string, string> ReadSignedValues() =>
        _signedValues.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal);
}
