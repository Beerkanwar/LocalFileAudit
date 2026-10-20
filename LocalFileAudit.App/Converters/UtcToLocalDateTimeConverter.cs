using System;
using System.Globalization;
using System.Windows.Data;
using LocalFileAudit.Core.Common;

namespace LocalFileAudit.App.Converters;

/// <summary>
/// Converts a UTC DateTime (or nullable DateTime) to the machine's local DateTime for consistent UI presentation.
/// </summary>
public sealed class UtcToLocalDateTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            return UtcDateTimeHelper.ToLocal(dt);
        }

        return value;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
