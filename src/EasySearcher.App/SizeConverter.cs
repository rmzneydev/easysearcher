using System.Globalization;
using System.Windows.Data;

namespace EasySearcher.App;

/// <summary>Muestra un tamano en bytes como "1,5 MB". Vacio para carpetas.</summary>
sealed class SizeConverter : IValueConverter
{
    static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long size) return "";
        if (size < 1024) return $"{size:N0} {Units[0]}";

        double s = size;
        int unit = 0;
        while (s >= 1024 && unit < Units.Length - 1)
        {
            s /= 1024;
            unit++;
        }
        return string.Format(culture, "{0:N1} {1}", s, Units[unit]);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
