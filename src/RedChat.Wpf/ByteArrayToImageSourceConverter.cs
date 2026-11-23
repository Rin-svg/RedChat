#nullable disable
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace RedChat.Wpf;

/// <summary>Convertit un avatar PNG/JPEG déjà validé en image WPF sans conserver le flux ouvert.</summary>
public sealed class ByteArrayToImageSourceConverter : IValueConverter
{
    /// <summary>Convertit les octets PNG/JPEG en <see cref="BitmapImage"/> gelée pour le binding WPF.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0)
        {
            return null;
        }

        using MemoryStream stream = new(bytes, writable: false);
        BitmapImage image = new();
        try
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Une seule dimension de décodage conserve le ratio original de la photo.
            image.DecodePixelWidth = 900;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>La conversion inverse n'est pas prise en charge.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("La conversion inverse n'est pas prise en charge.");
}
