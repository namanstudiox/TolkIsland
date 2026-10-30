using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TolkIsland.Services;

public enum OcrStatus
{
    Success,
    NoText,
    NoLanguage,
    Failed
}

public sealed record OcrScanResult(
    OcrStatus Status,
    string Text,
    IReadOnlyList<string> Lines,
    string Language);

public static class OcrService
{
    public static async Task<OcrScanResult> ScanAsync(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return Failed();
        }

        var engine = CreateEngine();
        if (engine is null)
        {
            return new OcrScanResult(OcrStatus.NoLanguage, string.Empty, Array.Empty<string>(), string.Empty);
        }

        try
        {
            using var captured = Capture(bounds);
            using var image = Fit(captured, (int)OcrEngine.MaxImageDimension);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Bmp);
            stream.Position = 0;

            using var randomAccess = new InMemoryRandomAccessStream();
            await randomAccess.WriteAsync(stream.ToArray().AsBuffer());
            randomAccess.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(randomAccess);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore);

            var result = await engine.RecognizeAsync(bitmap);
            var lines = result.Lines
                .Select(line => line.Text.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            var text = string.Join(Environment.NewLine, lines);

            return new OcrScanResult(
                lines.Count == 0 ? OcrStatus.NoText : OcrStatus.Success,
                text,
                lines,
                engine.RecognizerLanguage.DisplayName);
        }
        catch
        {
            return Failed();
        }
    }

    private static OcrEngine? CreateEngine()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null)
            {
                return engine;
            }

            foreach (var language in OcrEngine.AvailableRecognizerLanguages)
            {
                engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine is not null)
                {
                    return engine;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static Bitmap Capture(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            bounds.Left,
            bounds.Top,
            0,
            0,
            bounds.Size,
            CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static Bitmap Fit(Bitmap source, int maxDimension)
    {
        if (source.Width <= maxDimension && source.Height <= maxDimension)
        {
            return source;
        }

        var scale = Math.Min(
            (double)maxDimension / source.Width,
            (double)maxDimension / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }

    private static OcrScanResult Failed()
        => new(OcrStatus.Failed, string.Empty, Array.Empty<string>(), string.Empty);
}
