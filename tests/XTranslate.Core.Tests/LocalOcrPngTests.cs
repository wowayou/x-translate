using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using XTranslate.Core;

internal static class LocalOcrPngTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All => new (string, Func<Task>)[]
    {
        ("PNG preflight accepts static RGBA/grayscale/indexed envelopes without changing caller bytes", Valid),
        ("PNG preflight rejects every truncation, critical/data CRC damage and oversized chunk lengths", Corruption),
        ("PNG preflight requires one header, consecutive image data and a final end; animation never degrades to static", Structure),
        ("PNG preflight validates bit depth/color and palette constraints before native decoding", ColorsAndPalettes),
        ("PNG preflight enforces declared pixel, file-byte and chunk-count ceilings with inclusive valid boundaries", Limits),
        ("PNG preflight honors pre-cancellation and checksum validation covers unknown ancillary chunks too", CancellationAndAncillary)
    };
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly byte[] Pixel = CompressedPixel();
    private static readonly byte[] Data = Chunk("IDAT", Pixel);
    private static readonly byte[] End = Chunk("IEND", Array.Empty<byte>());
    private static void Check(bool value) { if (!value) throw new Exception("PNG preflight contract differs"); }
    private static void Reject(byte[] bytes, LocalOcrError code = LocalOcrError.InvalidImage)
    {
        try { LocalOcrPng.Validate(bytes); }
        catch (LocalOcrException error) when (error.Code == code && error.InnerException is null) { return; }
        throw new Exception("PNG preflight accepted a malformed envelope or returned a different safe error");
    }
    private static byte[] Header(uint width = 1, uint height = 1, byte depth = 8, byte color = 6, byte compression = 0, byte filter = 0, byte interlace = 0)
    {
        var data = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(data, width); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), height);
        data[8] = depth; data[9] = color; data[10] = compression; data[11] = filter; data[12] = interlace; return Chunk("IHDR", data);
    }
    private static byte[] Png(params byte[][] chunks) => Signature.Concat(chunks.SelectMany(chunk => chunk)).ToArray();
    private static byte[] Chunk(string type, byte[] data)
    {
        var bytes = new byte[data.Length + 12]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4)); data.CopyTo(bytes, 8);
        // Independent bitwise reference, not the production lookup table.
        uint crc = uint.MaxValue;
        foreach (var value in bytes.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(data.Length + 8), ~crc); return bytes;
    }
    private static byte[] CompressedPixel()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(new byte[] { 0, 0, 0, 0, 255 });
        return stream.ToArray();
    }
    private static Task Valid()
    {
        Check(Convert.ToHexString(End) == "0000000049454E44AE426082"); // PNG's fixed known end checksum.
        foreach (var header in new[] { Header(), Header(8192), Header(4000, 2000), Header(interlace: 1), Header(color: 0), Header(color: 2), Header(color: 4) })
        {
            var png = Png(header, Data, End); var before = png.ToArray();
            var dimensions = LocalOcrPng.Validate(png);
            Check(dimensions.Width == BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)) && dimensions.Height > 0 && png.AsSpan().SequenceEqual(before));
        }
        var palette = Chunk("PLTE", new byte[] { 0, 0, 0, 255, 255, 255 });
        LocalOcrPng.Validate(Png(Header(depth: 1, color: 3), palette, Chunk("tRNS", new byte[] { 0, 255 }), Data, End));
        LocalOcrPng.Validate(Png(Header(), Chunk("tEXt", "fixture\0static"u8.ToArray()), Chunk("IDAT", Pixel[..2]), Chunk("IDAT", Pixel[2..]), Chunk("tEXt", "end\0note"u8.ToArray()), End));
        return Task.CompletedTask;
    }
    private static Task Corruption()
    {
        var valid = Png(Header(), Data, End);
        for (var length = 0; length < valid.Length; length++) Reject(valid[..length]);
        foreach (var offset in new[] { 0, 29, 32, 41, 45, valid.Length - 1 })
        {
            var corrupt = valid.ToArray(); corrupt[offset] ^= 1; Reject(corrupt);
        }
        var oversizedChunk = valid.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(oversizedChunk.AsSpan(33, 4), uint.MaxValue); Reject(oversizedChunk);
        return Task.CompletedTask;
    }
    private static Task Structure()
    {
        foreach (var png in new[]
        {
            Png(Header(), End), Png(Header(), Chunk("IDAT", Array.Empty<byte>()), End), Png(Header(), Data),
            Png(Header(), Data, Chunk("IEND", new byte[] { 0 })), Png(Header(), Data, End, End),
            Png(Header(), Data, End, Chunk("tEXt", "fixture\0extra"u8.ToArray())), Png(Header(), Header(), Data, End),
            Png(Header(), Data, Chunk("tEXt", "fixture\0split"u8.ToArray()), Data, End), Png(Header(), Chunk("ABCD", Array.Empty<byte>()), Data, End),
            Png(Header(), Chunk("text", Array.Empty<byte>()), Data, End), Png(Header(), Chunk("tE1t", Array.Empty<byte>()), Data, End)
        }) Reject(png);
        foreach (var type in new[] { "acTL", "fcTL", "fdAT" }) Reject(Png(Header(), Chunk(type, new byte[26]), Data, End));
        return Task.CompletedTask;
    }
    private static Task ColorsAndPalettes()
    {
        foreach (var (depth, color) in new (byte, byte)[] { (0, 6), (1, 6), (4, 2), (16, 3), (8, 1), (8, 5), (32, 0) }) Reject(Png(Header(depth: depth, color: color), Data, End));
        foreach (var header in new[] { Header(compression: 1), Header(filter: 1), Header(interlace: 2) }) Reject(Png(header, Data, End));
        var palette = Chunk("PLTE", new byte[] { 0, 0, 0 });
        Reject(Png(Header(color: 3), Data, End)); Reject(Png(Header(color: 0), palette, Data, End)); Reject(Png(Header(color: 4), palette, Data, End));
        Reject(Png(Header(), palette, palette, Data, End)); Reject(Png(Header(), Data, palette, End));
        foreach (var count in new[] { 0, 1, 4, 771 }) Reject(Png(Header(color: 3), Chunk("PLTE", new byte[count]), Data, End));
        Reject(Png(Header(depth: 1, color: 3), Chunk("PLTE", new byte[9]), Data, End));
        LocalOcrPng.Validate(Png(Header(), palette, Data, End));
        return Task.CompletedTask;
    }
    private static Task Limits()
    {
        foreach (var header in new[] { Header(8193), Header(1, 8193), Header(4001, 2000), Header(uint.MaxValue, uint.MaxValue) })
            Reject(Png(header, Data, End), LocalOcrError.ImageTooLarge);
        Reject(Png(Header(0), Data, End)); Reject(Png(Header(1, 0), Data, End));
        var annotation = Chunk("tEXt", Array.Empty<byte>());
        var chunks = new[] { Header() }.Concat(Enumerable.Repeat(annotation, LocalOcrPng.MaxChunks - 3)).Concat(new[] { Data, End }).ToArray();
        LocalOcrPng.Validate(Png(chunks)); Reject(Png(chunks[..^1].Concat(new[] { annotation, End }).ToArray()));
        var exact = Png(Header(), Chunk("IDAT", new byte[LocalOcrPng.MaxFileBytes - 57]), End);
        Check(exact.Length == LocalOcrPng.MaxFileBytes); LocalOcrPng.Validate(exact); // Envelope-only, not a valid compressed-pixel claim.
        Reject(new byte[LocalOcrPng.MaxFileBytes + 1], LocalOcrError.ImageTooLarge);
        return Task.CompletedTask;
    }
    private static Task CancellationAndAncillary()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { LocalOcrPng.Validate(Png(Header(), Data, End), cancellation.Token); throw new Exception("PNG preflight ignored cancellation"); }
        catch (OperationCanceledException error) { Check(error.CancellationToken == cancellation.Token); }
        var ancillary = Chunk("vpAg", new byte[] { 1, 2, 3 });
        LocalOcrPng.Validate(Png(Header(), ancillary, Data, End));
        ancillary[^1] ^= 1; Reject(Png(Header(), ancillary, Data, End));
        return Task.CompletedTask;
    }
}
