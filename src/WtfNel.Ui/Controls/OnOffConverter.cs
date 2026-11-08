using System.Globalization;
using System.Windows.Data;

namespace WtfNel.Ui.Controls;

/// <summary>Turns a toggle state into the WinUI-style on/off label.</summary>
public sealed class OnOffConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "开" : "关";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
