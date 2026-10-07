using System;
using System.Globalization;
using BulkPimRoleSettings.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace BulkPimRoleSettings.Converters;

/// <summary>
/// Converts between TriState enum and CheckBox IsChecked (bool?).
/// Unchanged = null (indeterminate), SetTrue = true, SetFalse = false.
/// </summary>
public class TriStateConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is TriState state)
        {
            return state switch
            {
                TriState.SetTrue => (bool?)true,
                TriState.SetFalse => (bool?)false,
                _ => null
            };
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
            return boolValue ? TriState.SetTrue : TriState.SetFalse;

        return TriState.Unchanged;
    }
}

/// <summary>
/// Converts a step number to visibility. Pass the step index as ConverterParameter.
/// </summary>
public class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int currentStep && parameter is string stepStr && int.TryParse(stepStr, out var targetStep))
        {
            return currentStep == targetStep
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        return Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Inverts a boolean value.
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : value;
}

/// <summary>
/// Converts boolean to Visibility.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Microsoft.UI.Xaml.Visibility v && v == Microsoft.UI.Xaml.Visibility.Visible;
}

/// <summary>
/// Converts between TriState enum and ComboBox SelectedIndex (int).
/// Index 0 = Unchanged, 1 = SetTrue (Enable), 2 = SetFalse (Disable).
/// </summary>
public class TriStateToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is TriState state)
            return (int)state;
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index && Enum.IsDefined(typeof(TriState), index))
            return (TriState)index;
        return TriState.Unchanged;
    }
}

/// <summary>
/// Converts RoleApplyStatus to a glyph string (checkmark or cross).
/// </summary>
public class RoleStatusToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is RoleApplyStatus status)
        {
            return status switch
            {
                RoleApplyStatus.Success => "\uE73E",  // Checkmark
                RoleApplyStatus.Failed => "\uE711",   // Cross
                _ => "\uE946"                          // Clock/Pending
            };
        }
        return "\uE946";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts RoleApplyStatus to a foreground color brush.
/// </summary>
public class RoleStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is RoleApplyStatus status)
        {
            return status switch
            {
                RoleApplyStatus.Success => new SolidColorBrush(Microsoft.UI.Colors.LimeGreen),
                RoleApplyStatus.Failed => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
                _ => new SolidColorBrush(Microsoft.UI.Colors.Gray)
            };
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts RoleApplyStatus to Visibility (visible only for non-Pending).
/// </summary>
public class RoleStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is RoleApplyStatus status && status != RoleApplyStatus.Pending)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts RoleApplyStatus to Visibility (visible only for Pending).
/// </summary>
public class RoleStatusToPendingVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is RoleApplyStatus status && status == RoleApplyStatus.Pending)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Scales a measured size (typically ActualHeight) by a fraction supplied as ConverterParameter,
/// so element heights adapt to the window instead of using hardcoded values.
/// Falls back to a sensible minimum while the host is still being measured.
/// </summary>
public class SizeFractionConverter : IValueConverter
{
    private const double MinimumSize = 120d;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not double available || double.IsNaN(available) || available <= 0)
            return MinimumSize;

        var fraction = 0.4d;
        if (parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            fraction = parsed;

        return Math.Max(MinimumSize, available * fraction);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
