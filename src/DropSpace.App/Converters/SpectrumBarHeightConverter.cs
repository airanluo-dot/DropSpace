using Microsoft.UI.Xaml.Data;

namespace DropSpace.App.Converters;

public sealed class SpectrumBarHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value is float floatValue ? floatValue : System.Convert.ToSingle(value);
        return 4d + Math.Clamp(level, 0, 1) * 24d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
