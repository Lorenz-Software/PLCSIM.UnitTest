using System.Globalization;
using System.Windows;
using PLCSIM.UnitTest.Common.AccessProfiles;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed class ProfileConfigurationFieldViewModel : ObservableObject
{
    private readonly Func<string, string?, string?> applyValue;
    private readonly Action validationChanged;
    private string? value;
    private bool? booleanValue;
    private ProfileConfigurationAllowedValueViewModel? selectedAllowedValue;
    private bool isSet;
    private string? validationMessage;

    public ProfileConfigurationFieldViewModel(
        AccessProfileConfigurationField field,
        bool hasExplicitValue,
        Func<string, string?, string?> applyValue,
        Action validationChanged)
    {
        Id = field.Id;
        DisplayName = field.DisplayName;
        ValueType = field.ValueType;
        IsRequired = field.IsRequired;
        DefaultValue = field.DefaultValue;
        this.applyValue = applyValue;
        this.validationChanged = validationChanged;

        AllowedValues = field.AllowedValues
            .Select(item => new ProfileConfigurationAllowedValueViewModel(item.Value, item.DisplayName))
            .ToArray();
        var effectiveValue = field.Value ?? field.DefaultValue;
        value = effectiveValue;
        isSet = hasExplicitValue || field.IsRequired;
        booleanValue = ParseBoolean(effectiveValue);
        selectedAllowedValue = AllowedValues.FirstOrDefault(item =>
            string.Equals(item.Value, effectiveValue, StringComparison.Ordinal));
        ValidateCurrentValue();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public AccessProfileConfigurationValueType ValueType { get; }

    public bool IsRequired { get; }

    public string? DefaultValue { get; }

    public IReadOnlyList<ProfileConfigurationAllowedValueViewModel> AllowedValues { get; }

    public Visibility OptionalToggleVisibility => IsRequired || ValueType == AccessProfileConfigurationValueType.Boolean
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility AllowedValuesVisibility => AllowedValues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BooleanVisibility => AllowedValues.Count == 0 && ValueType == AccessProfileConfigurationValueType.Boolean
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility TextVisibility => AllowedValues.Count == 0 && ValueType != AccessProfileConfigurationValueType.Boolean
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsInputEnabled => IsRequired
        || IsSet
        || ValueType == AccessProfileConfigurationValueType.Boolean;

    public TextAlignment TextAlignment => ValueType is AccessProfileConfigurationValueType.Integer
        or AccessProfileConfigurationValueType.Float
        ? TextAlignment.Right
        : TextAlignment.Left;

    public bool IsSet
    {
        get => isSet;
        set
        {
            if (IsRequired || !SetProperty(ref isSet, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsInputEnabled));
            if (!value)
            {
                Apply(null);
                return;
            }

            if (AllowedValues.Count > 0 && selectedAllowedValue is null)
            {
                SelectedAllowedValue = AllowedValues[0];
            }
            else if (ValueType == AccessProfileConfigurationValueType.Boolean && booleanValue is null)
            {
                BooleanValue = false;
            }
            else
            {
                ValidateCurrentValue();
            }
        }
    }

    public string? Value
    {
        get => value;
        set
        {
            if (!SetProperty(ref this.value, value))
            {
                return;
            }

            Apply(value);
        }
    }

    public bool? BooleanValue
    {
        get => booleanValue;
        set
        {
            if (!SetProperty(ref booleanValue, value))
            {
                return;
            }

            if (!IsRequired && !isSet)
            {
                isSet = true;
                OnPropertyChanged(nameof(IsSet));
            }

            Apply(value.HasValue ? (value.Value ? "true" : "false") : null);
        }
    }

    public ProfileConfigurationAllowedValueViewModel? SelectedAllowedValue
    {
        get => selectedAllowedValue;
        set
        {
            if (!SetProperty(ref selectedAllowedValue, value))
            {
                return;
            }

            Apply(value?.Value);
        }
    }

    public string? ValidationMessage
    {
        get => validationMessage;
        private set
        {
            if (SetProperty(ref validationMessage, value))
            {
                OnPropertyChanged(nameof(ValidationVisibility));
                OnPropertyChanged(nameof(IsValid));
                validationChanged();
            }
        }
    }

    public Visibility ValidationVisibility => string.IsNullOrWhiteSpace(ValidationMessage)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsValid => string.IsNullOrWhiteSpace(ValidationMessage);

    private void Apply(string? candidate)
    {
        var localValidation = Validate(candidate);
        if (localValidation is not null)
        {
            ValidationMessage = localValidation;
            return;
        }

        ValidationMessage = applyValue(Id, Normalize(candidate));
    }

    private void ValidateCurrentValue()
    {
        ValidationMessage = Validate(IsSet ? CurrentValue() : null);
    }

    private string? CurrentValue()
    {
        if (AllowedValues.Count > 0)
        {
            return selectedAllowedValue?.Value;
        }

        if (ValueType == AccessProfileConfigurationValueType.Boolean)
        {
            return booleanValue.HasValue ? (booleanValue.Value ? "true" : "false") : null;
        }

        return value;
    }

    private string? Validate(string? candidate)
    {
        if (!IsSet)
        {
            return IsRequired ? "A value is required." : null;
        }

        if (candidate is null)
        {
            return "A value is required.";
        }

        if (AllowedValues.Count > 0
            && !AllowedValues.Any(item => string.Equals(item.Value, candidate, StringComparison.Ordinal)))
        {
            return "Select one of the available values.";
        }

        return ValueType switch
        {
            AccessProfileConfigurationValueType.Integer when !int.TryParse(
                candidate,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _) => "Enter a valid integer.",
            AccessProfileConfigurationValueType.Float when !float.TryParse(
                candidate,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out _) => "Enter a valid floating-point value.",
            AccessProfileConfigurationValueType.Boolean when candidate is not ("true" or "false") => "Select true or false.",
            _ => null
        };
    }

    private string? Normalize(string? candidate)
    {
        if (candidate is null)
        {
            return null;
        }

        return ValueType switch
        {
            AccessProfileConfigurationValueType.Integer => int.Parse(
                candidate,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            AccessProfileConfigurationValueType.Float => float.Parse(
                candidate,
                NumberStyles.Float,
                CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            _ => candidate
        };
    }

    private static bool? ParseBoolean(string? candidate)
    {
        return candidate switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };
    }
}

public sealed record ProfileConfigurationAllowedValueViewModel(
    string Value,
    string DisplayName);
