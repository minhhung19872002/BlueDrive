using System.Globalization;
using Avalonia.Data.Converters;

namespace BlueDrive.Desktop.ViewModels;

/// <summary>Wizard pages: visible when the current step equals the parameter.</summary>
public sealed class StepIs : IValueConverter
{
    public static readonly StepIs Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int step && int.TryParse(parameter?.ToString(), out var p) && step == p;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
