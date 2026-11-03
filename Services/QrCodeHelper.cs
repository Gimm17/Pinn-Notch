using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace VNotch.Services;

public static class QrCodeHelper
{
    public static BitmapSource GenerateQrCode(string text, int pixelsPerModule = 10)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(qrCodeData);
        byte[] bytes = qrCode.GetGraphic(pixelsPerModule);

        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
