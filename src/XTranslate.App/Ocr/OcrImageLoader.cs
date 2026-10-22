using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTranslate.Core;
using XTranslate.App.Lexicons;

namespace XTranslate.App.Ocr;

/// <summary>Single PNG snapshot and owned pixels. The caller releases the preview and clears pixels on close.</summary>
internal sealed record OcrImage(int Width, int Height, BitmapSource Preview, byte[] Pixels) : IDisposable
{
    public void Dispose() => Array.Clear(Pixels);
}
internal static class OcrImageLoader
{
    internal const int MaxFileBytes=LocalOcrPng.MaxFileBytes;
    internal static Task<OcrImage> LoadAsync(string path,CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var local=Path.GetFullPath(path); var root=Path.GetPathRoot(local)!;
        // Do not turn "local OCR" into an implicit network share read. Reject UNC/device paths, mapped
        // network drives and reparse links before opening bytes; the file picker itself is user-controlled.
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            new DriveInfo(root).DriveType == DriveType.Network) throw new LocalOcrException(LocalOcrError.InvalidImage);
        try { LexiconFiles.NoLinks(local); }
        catch(LexiconStorageException) { throw new LocalOcrException(LocalOcrError.InvalidImage); }
        using var file=new FileStream(local,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (file.Length > MaxFileBytes) throw new LocalOcrException(LocalOcrError.ImageTooLarge);
        if (file.Length < 33) throw new LocalOcrException(LocalOcrError.InvalidImage);
        var bytes=new byte[(int)file.Length];
        try
        {
            for (var offset=0;offset<bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count=file.Read(bytes,offset,Math.Min(65536,bytes.Length-offset));
                if (count==0) throw new LocalOcrException(LocalOcrError.InvalidImage);
                offset+=count;
            }
            return Decode(bytes,token);
        }
        finally { Array.Clear(bytes); }
    },token);
    private static OcrImage Decode(byte[] bytes,CancellationToken token)
    {
        var (width,height)=LocalOcrPng.Validate(bytes,token);
        var pixelBytes=checked(width*height*4);
        using var stream=new MemoryStream(bytes,false);
        BitmapDecoder decoder;
        try { decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException or FileFormatException)
        { throw new LocalOcrException(LocalOcrError.InvalidImage); }
        token.ThrowIfCancellationRequested();
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != width || decoder.Frames[0].PixelHeight != height)
            throw new LocalOcrException(LocalOcrError.InvalidImage);
        var converted=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);
        var pixels=new byte[pixelBytes];
        try
        {
            converted.CopyPixels(pixels,width*4,0);
            // OCR ignores alpha; flatten transparent PNG pixels onto white in both preview and engine input.
            for (var i=0;i<pixels.Length;i+=4)
            {
                if ((i & 0xffff)==0) token.ThrowIfCancellationRequested();
                var alpha=pixels[i+3];
                for (var c=0;c<3;c++) pixels[i+c]=(byte)((pixels[i+c]*alpha+255*(255-alpha)+127)/255);
                pixels[i+3]=255;
            }
            token.ThrowIfCancellationRequested();
            // Avoid retaining decoder, metadata or compressed input. Preview represents the exact submitted pixels.
            var preview=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
            preview.Freeze(); return new(width,height,preview,pixels);
        }
        catch { Array.Clear(pixels); throw; }
    }
}
