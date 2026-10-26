using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenIPTV.App;

/// <summary>Visible when the value is a non-empty string (or non-null object).</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? (string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible)
        : value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapsed when null; pass "invert" to show only when null.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is not null;
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Channel logo for an Image: <c>local:LogoImage.Url="{Binding LogoUrl}" local:LogoImage.DecodeHeight="28"</c>.
/// Shows a neutral TV placeholder until the logo has downloaded, and keeps it when the channel has no logo,
/// the logo is SVG (WPF cannot draw it) or the link is broken. Downloaded logos are cached.
/// </summary>
public static class LogoImage
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(LogoImage), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty DecodeHeightProperty = DependencyProperty.RegisterAttached(
        "DecodeHeight", typeof(int), typeof(LogoImage), new PropertyMetadata(32, OnChanged));

    public static string? GetUrl(DependencyObject o) => (string?)o.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject o, string? value) => o.SetValue(UrlProperty, value);
    public static int GetDecodeHeight(DependencyObject o) => (int)o.GetValue(DecodeHeightProperty);
    public static void SetDecodeHeight(DependencyObject o, int value) => o.SetValue(DecodeHeightProperty, value);

    private const int MaxCached = 3000;
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private static readonly HashSet<string> Failed = new(StringComparer.Ordinal);

    private static readonly Lazy<ImageSource> PlaceholderImage = new(() =>
    {
        var b = new BitmapImage(new Uri("pack://application:,,,/Assets/channel-placeholder.png"));
        b.Freeze();
        return b;
    });

    /// <summary>The default picture for channels without a usable logo.</summary>
    public static ImageSource Placeholder => PlaceholderImage.Value;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var url = GetUrl(image);
        int height = GetDecodeHeight(image);
        image.Source = Placeholder;   // rows are recycled: never leave the previous channel's logo behind

        if (string.IsNullOrWhiteSpace(url) || url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return;
        var key = height + "|" + url;
        if (Failed.Contains(key)) return;

        if (!Cache.TryGetValue(key, out var bmp))
        {
            bmp = Create(url, height);
            if (bmp is null) { Failed.Add(key); return; }
            Cache[key] = bmp;
            Order.Enqueue(key);
            while (Order.Count > MaxCached) Cache.Remove(Order.Dequeue());
        }

        if (!bmp.IsDownloading)
        {
            image.Source = bmp;
            return;
        }

        void Unhook()
        {
            bmp.DownloadCompleted -= Done;
            bmp.DownloadFailed -= Fail;
            bmp.DecodeFailed -= Fail;
        }
        void Done(object? sender, EventArgs args)
        {
            Unhook();
            // Only if this Image still shows the same channel (it may have been recycled meanwhile).
            if (GetUrl(image) == url && GetDecodeHeight(image) == height) image.Source = bmp;
        }
        void Fail(object? sender, ExceptionEventArgs args)
        {
            Unhook();
            Cache.Remove(key);
            Failed.Add(key);   // keep the placeholder; don't retry this session
        }
        bmp.DownloadCompleted += Done;
        bmp.DownloadFailed += Fail;
        bmp.DecodeFailed += Fail;
    }

    private static BitmapImage? Create(string url, int height)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.DecodePixelHeight = height * 2;   // sharp on high-DPI screens
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.EndInit();
            return img;
        }
        catch
        {
            return null;
        }
    }
}
