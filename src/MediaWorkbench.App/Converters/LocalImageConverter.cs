using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MediaWorkbench_App.Converters;

/// <summary>Loads a local image; an optional integer parameter decodes it at that width to save memory.</summary>
public sealed class LocalImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        var bitmap = new BitmapImage();
        if (parameter is string width && int.TryParse(width, out int pixels) && pixels > 0)
        {
            bitmap.DecodePixelType = DecodePixelType.Logical;
            bitmap.DecodePixelWidth = pixels;
        }
        bitmap.UriSource = new Uri(Path.GetFullPath(path));
        return bitmap;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
