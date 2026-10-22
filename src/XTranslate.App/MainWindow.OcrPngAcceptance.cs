using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTranslate.App.Ocr;
using XTranslate.Core;

namespace XTranslate.App;

public partial class MainWindow
{
    private static async Task VerifyOcrPngAcceptanceAsync(List<string> checks, string root)
    {
        var path = Path.Combine(root, "alpha-pixels.png");
        byte[] raw = { 10, 30, 90, 0, 40, 80, 120, 64, 200, 100, 50, 128, 7, 11, 13, 255 };
        SaveOcrAcceptancePng(path, 4, 1, PixelFormats.Bgra32, raw, 16, dpi: 300);
        var original = File.ReadAllBytes(path);
        using (var image = await OcrImageLoader.LoadAsync(path, CancellationToken.None))
        {
            var expected = raw.ToArray();
            for (var i = 0; i < expected.Length; i += 4)
            {
                var alpha = raw[i + 3];
                for (var channel = 0; channel < 3; channel++)
                    expected[i + channel] = (byte)((raw[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                expected[i + 3] = 255;
            }
            var preview = new byte[expected.Length];
            image.Preview.CopyPixels(preview, 16, 0);
            RequireStream(image.Width == 4 && image.Height == 1 && image.Preview.DpiX == 96 && image.Preview.IsFrozen &&
                image.Pixels.AsSpan().SequenceEqual(expected) && preview.AsSpan().SequenceEqual(expected),
                "PNG alpha flattening, physical dimensions or preview/input equality differs");
            image.Dispose();
            RequireStream(image.Pixels.All(value => value == 0), "Disposed PNG retained owned pixels");
        }
        RequireStream(original.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "PNG loader changed its source file");
        checks.Add("Real PNG decoding preserves physical pixels despite 300-DPI metadata; transparent/partial-alpha colors flatten onto white identically in preview and OCR input, and owned pixels clear on dispose");

        var gray = Path.Combine(root, "gray8.png");
        SaveOcrAcceptancePng(gray, 3, 1, PixelFormats.Gray8, new byte[] { 0, 127, 255 }, 3);
        using (var image = await OcrImageLoader.LoadAsync(gray, CancellationToken.None))
            RequireStream(image.Pixels.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 255, 127, 127, 127, 255, 255, 255, 255, 255 }),
                "Grayscale PNG did not convert to exact BGRA pixels");
        var indexed = Path.Combine(root, "indexed8.png");
        SaveOcrAcceptancePng(indexed, 3, 1, PixelFormats.Indexed8, new byte[] { 0, 1, 2 }, 3,
            new BitmapPalette(new[] { Colors.Black, Colors.Red, Colors.White }));
        using (var image = await OcrImageLoader.LoadAsync(indexed, CancellationToken.None))
            RequireStream(image.Pixels.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 }),
                "Indexed PNG did not preserve its declared palette");
        checks.Add("Real grayscale and indexed-palette PNGs decode into the same bounded BGRA format without resizing or changing source files");

        var badCrc = original.ToArray(); badCrc[32] ^= 1;
        var badEndCrc = original.ToArray(); badEndCrc[^1] ^= 1;
        var orphanFrame = InsertOcrPngChunk(original, "fcTL", new byte[26]);
        var unknownCritical = InsertOcrPngChunk(original, "ABCD", Array.Empty<byte>());
        var trailingChunk = original.Concat(OcrPngAcceptanceChunk("tEXt", "fixture\0trailing"u8.ToArray())).ToArray();
        var invalid = new[]
        {
            ("missing-end", original[..^12]), ("header-crc", badCrc), ("end-crc", badEndCrc),
            ("orphan-apng-frame", orphanFrame), ("unknown-critical", unknownCritical), ("after-end", trailingChunk)
        };
        var accepted = new List<string>();
        foreach (var (name, bytes) in invalid)
        {
            var bad = Path.Combine(root, name + ".png"); File.WriteAllBytes(bad, bytes);
            try { using var image = await OcrImageLoader.LoadAsync(bad, CancellationToken.None); accepted.Add(name); }
            catch (LocalOcrException error) when (error.Code == LocalOcrError.InvalidImage) { }
        }
        RequireStream(accepted.Count == 0, "Malformed PNGs were accepted by the production loader: " + string.Join(", ", accepted));
        checks.Add("Production PNG loader rejects missing IEND, bad header/end CRC, orphan APNG controls, unknown critical chunks and post-IEND content before exposing a preview");
    }

    private static void SaveOcrAcceptancePng(string path, int width, int height, PixelFormat format, byte[] pixels, int stride,
        BitmapPalette? palette = null, double dpi = 96)
    {
        var source = BitmapSource.Create(width, height, dpi, dpi, format, palette, pixels, stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new FileStream(path, FileMode.CreateNew);
        encoder.Save(stream);
    }
    private static byte[] InsertOcrPngChunk(byte[] png, string type, byte[] data) =>
        png[..33].Concat(OcrPngAcceptanceChunk(type, data)).Concat(png[33..]).ToArray();
    private static byte[] OcrPngAcceptanceChunk(string type, byte[] data)
    {
        var bytes = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4)); data.CopyTo(bytes, 8);
        // Independent bitwise PNG CRC reference; the decoder's implementation must not produce its own oracle.
        uint crc = uint.MaxValue;
        foreach (var value in bytes.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(data.Length + 8), ~crc); return bytes;
    }
}
