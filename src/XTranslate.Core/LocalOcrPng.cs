using System.Buffers.Binary;

namespace XTranslate.Core;

/// <summary>
/// Bounded static-PNG preflight before a native decoder can allocate or repair a malformed image.
/// Checks the chunk envelope, CRC, critical ordering and declared image budget, not compressed pixels
/// or every ancillary metadata field; the caller still needs a real single-frame decoder.
/// </summary>
public static class LocalOcrPng
{
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxChunks = 4096;
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static (int Width, int Height) Validate(ReadOnlySpan<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaxFileBytes) throw new LocalOcrException(LocalOcrError.ImageTooLarge);
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(Signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw Invalid();
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        // Dimension limits precede CRC/decompression: even a corrupt oversized declaration cannot allocate pixels.
        if (width > LocalOcrLimits.MaxDimension || height > LocalOcrLimits.MaxDimension || (long)width * height > LocalOcrLimits.MaxPixels)
            throw new LocalOcrException(LocalOcrError.ImageTooLarge);
        LocalOcrLimits.ValidateImage((int)width, (int)height, checked((int)(width * height * 4)));
        var depth = bytes[24]; var color = bytes[25];
        var depthValid = color switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false
        };
        if (!depthValid || bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1) throw Invalid();

        var palette = false; var dataSeen = false; var dataClosed = false; var dataBytes = 0; var chunks = 0;
        for (var offset = 8; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (++chunks > MaxChunks || bytes.Length - offset < 12) throw Invalid();
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12) throw Invalid();
            var count = (int)length;
            var type = bytes.Slice(offset + 4, 4);
            foreach (var letter in type)
                if (letter is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z')) throw Invalid();
            if ((type[2] & 32) != 0 || type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)) throw Invalid();
            if (Crc(bytes.Slice(offset + 4, count + 4), token) != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + count, 4))) throw Invalid();

            if (type.SequenceEqual("IHDR"u8))
            {
                if (offset != 8) throw Invalid();
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (palette || dataSeen || color is 0 or 4 || count == 0 || count > 768 || count % 3 != 0 ||
                    color == 3 && count / 3 > (1 << depth)) throw Invalid();
                palette = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (dataClosed || color == 3 && !palette) throw Invalid();
                dataSeen = true; dataBytes += count;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (count != 0 || !dataSeen || dataBytes == 0 || offset + 12 != bytes.Length) throw Invalid();
                return ((int)width, (int)height);
            }
            else
            {
                if ((type[0] & 32) == 0) throw Invalid(); // Unknown critical chunk: no decoder repair/fallback.
                if (dataSeen) dataClosed = true; // PNG image-data chunks must be consecutive.
            }
            offset += count + 12;
        }
        throw Invalid(); // A complete IEND is mandatory, including when the native decoder would tolerate its absence.
    }

    private static LocalOcrException Invalid() => new(LocalOcrError.InvalidImage);
    private static uint Crc(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        var crc = uint.MaxValue;
        for (var offset = 0; offset < bytes.Length; offset++)
        {
            if ((offset & 0xffff) == 0) token.ThrowIfCancellationRequested();
            crc = CrcTable[(crc ^ bytes[offset]) & 255] ^ (crc >> 8);
        }
        return ~crc;
    }
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var crc = index;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
            table[index] = crc;
        }
        return table;
    }
}
