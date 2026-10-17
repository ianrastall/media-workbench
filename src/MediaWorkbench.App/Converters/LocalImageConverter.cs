using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MediaWorkbench_App.Converters;

public sealed class LocalImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        return new BitmapImage(new Uri(Path.GetFullPath(path)));
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
