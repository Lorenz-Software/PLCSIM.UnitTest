using System.Windows;
using PLCSIM.UnitTest.App.Wpf.ViewModels;
using PLCSIM.UnitTest.Common.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class ProfileConfigurationFieldViewModelTests
{
    [Fact]
    public void Integer_value_is_validated_and_applied_in_canonical_form()
    {
        string? appliedValue = null;
        var viewModel = CreateViewModel(
            AccessProfileConfigurationValueType.Integer,
            "1",
            isRequired: true,
            (_, value) =>
            {
                appliedValue = value;
                return null;
            });

        viewModel.Value = "01";

        Assert.True(viewModel.IsValid);
        Assert.Equal("1", appliedValue);
        Assert.Equal(Visibility.Visible, viewModel.TextVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.BooleanVisibility);
    }

    [Fact]
    public void Invalid_float_is_kept_as_editor_state_without_updating_payload()
    {
        var applyCount = 0;
        var viewModel = CreateViewModel(
            AccessProfileConfigurationValueType.Float,
            "1.5",
            isRequired: true,
            (_, _) =>
            {
                applyCount++;
                return null;
            });

        viewModel.Value = "not-a-number";

        Assert.False(viewModel.IsValid);
        Assert.Equal("Enter a valid floating-point value.", viewModel.ValidationMessage);
        Assert.Equal(0, applyCount);
    }

    [Fact]
    public void Float_value_is_applied_in_canonical_invariant_form()
    {
        string? appliedValue = null;
        var viewModel = CreateViewModel(
            AccessProfileConfigurationValueType.Float,
            "1",
            isRequired: true,
            (_, value) =>
            {
                appliedValue = value;
                return null;
            });

        viewModel.Value = "1.25E+2";

        Assert.True(viewModel.IsValid);
        Assert.Equal("125", appliedValue);
    }

    [Fact]
    public void Allowed_values_use_selection_and_optional_unset_semantics()
    {
        var appliedValues = new List<string?>();
        var field = new AccessProfileConfigurationField(
            "mode",
            "Mode",
            AccessProfileConfigurationValueType.String,
            "polling",
            "polling",
            IsRequired: false,
            [
                new AccessProfileConfigurationAllowedValue("polling", "Polling"),
                new AccessProfileConfigurationAllowedValue("cycle", "PLC cycle")
            ]);
        var viewModel = new ProfileConfigurationFieldViewModel(
            field,
            hasExplicitValue: true,
            (_, value) =>
            {
                appliedValues.Add(value);
                return null;
            },
            () => { });

        viewModel.SelectedAllowedValue = viewModel.AllowedValues[1];
        viewModel.IsSet = false;

        Assert.Equal(["cycle", null], appliedValues);
        Assert.False(viewModel.IsSet);
        Assert.False(viewModel.IsInputEnabled);
        Assert.Equal(Visibility.Visible, viewModel.AllowedValuesVisibility);
    }

    [Fact]
    public void Boolean_field_uses_boolean_editor_state()
    {
        string? appliedValue = null;
        var viewModel = CreateViewModel(
            AccessProfileConfigurationValueType.Boolean,
            "false",
            isRequired: true,
            (_, value) =>
            {
                appliedValue = value;
                return null;
            });

        viewModel.BooleanValue = true;

        Assert.Equal("true", appliedValue);
        Assert.Equal(Visibility.Visible, viewModel.BooleanVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.TextVisibility);
    }

    [Fact]
    public void Optional_boolean_uses_effective_default_without_second_toggle()
    {
        string? appliedValue = null;
        var viewModel = new ProfileConfigurationFieldViewModel(
            new AccessProfileConfigurationField(
                "enabled",
                "Enabled",
                AccessProfileConfigurationValueType.Boolean,
                Value: null,
                DefaultValue: "true",
                IsRequired: false,
                AllowedValues: []),
            hasExplicitValue: false,
            (_, value) =>
            {
                appliedValue = value;
                return null;
            },
            () => { });

        Assert.True(viewModel.BooleanValue);
        Assert.False(viewModel.IsSet);
        Assert.True(viewModel.IsInputEnabled);
        Assert.Equal(Visibility.Collapsed, viewModel.OptionalToggleVisibility);

        viewModel.BooleanValue = false;

        Assert.True(viewModel.IsSet);
        Assert.Equal("false", appliedValue);
    }

    [Fact]
    public void Apply_failure_is_exposed_as_field_validation_without_losing_editor_value()
    {
        var viewModel = CreateViewModel(
            AccessProfileConfigurationValueType.String,
            "initial",
            isRequired: true,
            (_, _) => "Profile rejected the value.");

        viewModel.Value = "candidate";

        Assert.False(viewModel.IsValid);
        Assert.Equal("candidate", viewModel.Value);
        Assert.Equal("Profile rejected the value.", viewModel.ValidationMessage);
        Assert.Equal(Visibility.Visible, viewModel.ValidationVisibility);
    }

    private static ProfileConfigurationFieldViewModel CreateViewModel(
        AccessProfileConfigurationValueType valueType,
        string? value,
        bool isRequired,
        Func<string, string?, string?> applyValue)
    {
        return new ProfileConfigurationFieldViewModel(
            new AccessProfileConfigurationField(
                "field",
                "Field",
                valueType,
                value,
                value,
                isRequired,
                []),
            hasExplicitValue: true,
            applyValue,
            () => { });
    }
}
