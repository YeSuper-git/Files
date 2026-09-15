using System;
using System.Globalization;
using System.Windows.Data;

namespace FilesMax.Installer.Bootstrapper;

public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 ||
            values[0] is not double value ||
            values[1] is not double maximum ||
            values[2] is not double actualWidth ||
            maximum <= 0)
        {
            return 0d;
        }

        return Math.Clamp(actualWidth * value / maximum, 0d, actualWidth);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
