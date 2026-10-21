using System.Collections.ObjectModel;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed class ProfileConfigurationEditorViewModel : ObservableObject
{
    private IAccessProfileConfigurationService? service;
    private RuntimeProvisioningMode provisioningMode;
    private AccessProfileConfig? configuration;
    private string status = "Profile configuration is not available before target selection.";
    private bool projectionAvailable;

    public event EventHandler? StateChanged;

    public ObservableCollection<ProfileConfigurationFieldViewModel> Fields { get; } = [];

    public AccessProfileConfig? Configuration
    {
        get => configuration;
        private set => SetProperty(ref configuration, value);
    }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public void Load(
        IAccessProfileConfigurationService configurationService,
        AccessProfileConfig currentConfiguration,
        RuntimeProvisioningMode currentProvisioningMode)
    {
        service = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        Configuration = currentConfiguration;
        provisioningMode = currentProvisioningMode;
        var load = service.LoadConfig(currentConfiguration.Values.Json);
        if (!load.IsSuccessful || load.Value is null)
        {
            projectionAvailable = false;
            Fields.Clear();
            Status = FormatFailure(
                load.Diagnostics,
                "The access profile configuration could not be loaded.");
            OnStateChanged();
            return;
        }

        Configuration = load.Value;
        RefreshProjection();
    }

    public void Clear()
    {
        service = null;
        Configuration = null;
        projectionAvailable = false;
        Fields.Clear();
        Status = "Profile configuration is not available before target selection.";
        OnStateChanged();
    }

    public void ReportFailure(string message)
    {
        Status = message;
        OnStateChanged();
    }

    public bool Validate(bool updateStatus)
    {
        if (Configuration is null)
        {
            if (updateStatus)
            {
                Status = "Load an access profile before editing its configuration.";
            }

            return false;
        }

        if (!projectionAvailable)
        {
            return false;
        }

        if (Fields.Any(field => !field.IsValid))
        {
            if (updateStatus)
            {
                Status = "Correct the invalid profile configuration values before continuing.";
            }

            return false;
        }

        var validation = service!.ValidateConfig(provisioningMode);
        if (updateStatus)
        {
            Status = validation.IsValid
                ? "Profile configuration is valid."
                : FormatFailure(validation.Diagnostics, "The access profile configuration is invalid.");
        }

        return validation.IsValid;
    }

    private void RefreshProjection()
    {
        Fields.Clear();
        projectionAvailable = false;
        if (Configuration is null)
        {
            Status = "Load an access profile before editing its configuration.";
            OnStateChanged();
            return;
        }

        var projection = service!.GetFields(provisioningMode);
        if (!projection.IsSuccessful || projection.Value is null)
        {
            Status = FormatFailure(
                projection.Diagnostics,
                "The access profile configuration could not be projected.");
            OnStateChanged();
            return;
        }

        foreach (var field in projection.Value.Fields)
        {
            Fields.Add(new ProfileConfigurationFieldViewModel(
                field,
                service.ContainsFieldValue(field.Id),
                ApplyValue,
                OnFieldValidationChanged));
        }

        projectionAvailable = true;
        Validate(updateStatus: true);
        OnStateChanged();
    }

    private string? ApplyValue(string fieldId, string? value)
    {
        if (Configuration is null || service is null)
        {
            return "The access profile configuration service is not available.";
        }

        AccessProfileConfigurationResult<AccessProfileConfig> result;
        if (value is null)
        {
            if (!service.ContainsFieldValue(fieldId))
            {
                return null;
            }

            result = service.RemoveFieldValue(fieldId);
        }
        else
        {
            var field = service.GetField(fieldId, provisioningMode);
            if (!field.IsSuccessful || field.Value is null)
            {
                Status = FormatFailure(
                    field.Diagnostics,
                    $"The configuration field '{fieldId}' is not available.");
                OnStateChanged();
                return Status;
            }

            result = ApplyTypedValue(field.Value, value);
        }

        if (!result.IsSuccessful || result.Value is null)
        {
            Status = FormatFailure(
                result.Diagnostics,
                $"The value for '{fieldId}' could not be applied.");
            OnStateChanged();
            return Status;
        }

        Configuration = result.Value;
        Validate(updateStatus: true);
        OnStateChanged();
        return null;
    }

    private AccessProfileConfigurationResult<AccessProfileConfig> ApplyTypedValue(
        AccessProfileConfigurationField field,
        string value)
    {
        var add = service is not null && !service.ContainsFieldValue(field.Id);
        return field.ValueType switch
        {
            AccessProfileConfigurationValueType.Boolean => add
                ? service!.AddFieldValue(field.Id, bool.Parse(value))
                : service!.SetFieldValue(field.Id, bool.Parse(value)),
            AccessProfileConfigurationValueType.Integer => add
                ? service!.AddFieldValue(field.Id, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                : service!.SetFieldValue(field.Id, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            AccessProfileConfigurationValueType.Float => add
                ? service!.AddFieldValue(field.Id, float.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                : service!.SetFieldValue(field.Id, float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => add
                ? service!.AddFieldValue(field.Id, value)
                : service!.SetFieldValue(field.Id, value)
        };
    }

    private void OnFieldValidationChanged()
    {
        if (Fields.Any(field => !field.IsValid))
        {
            Status = "Correct the invalid profile configuration values before continuing.";
        }

        OnStateChanged();
    }

    private static string FormatFailure(
        IReadOnlyList<Diagnostic> diagnostics,
        string fallback)
    {
        return diagnostics.FirstOrDefault()?.Message ?? fallback;
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
