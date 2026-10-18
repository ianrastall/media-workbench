using System.Buffers.Binary;
using System.IO.Compression;

namespace MediaWorkbench.Core;

/// <summary>
/// The painted "change this part" area over a starting picture: one byte per cell, 255 where painted.
/// It has the picture's proportions at no more than <see cref="MaximumSide"/> cells on the longest side;
/// the inference child scales it to the picture. UI-free so painting and PNG encoding are testable.
/// </summary>
public sealed class MaskCanvas
{
    public const int MaximumSide = 512;
    /// <summary>Brush radius as a fraction of the longest side.</summary>
    public static readonly (double Radius, string Label)[] BrushSizes = [(0.025, "Small brush"), (0.05, "Medium brush"), (0.1, "Large brush")];
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly byte[] _cells;

    public MaskCanvas(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaximumSide || height > MaximumSide) throw new ArgumentOutOfRangeException(nameof(width), "Mask size must be 1–512 cells per side.");
        Width = width;
        Height = height;
        _cells = new byte[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Cells => _cells;
    public bool HasPaint => _cells.AsSpan().IndexOfAnyExcept((byte)0) >= 0;

    /// <summary>A canvas with the proportions of a picture of the given pixel size.</summary>
    public static MaskCanvas ForPicture(int pictureWidth, int pictureHeight)
    {
        if (pictureWidth < 1 || pictureHeight < 1) throw new ArgumentOutOfRangeException(nameof(pictureWidth), "The picture has no size.");
        double scale = Math.Min(1, (double)MaximumSide / Math.Max(pictureWidth, pictureHeight));
        return new(Math.Clamp((int)Math.Round(pictureWidth * scale), 1, MaximumSide), Math.Clamp((int)Math.Round(pictureHeight * scale), 1, MaximumSide));
    }

    public void Clear() => Array.Clear(_cells);

    /// <summary>Paints (or erases) a round-ended stroke between two points given as fractions (0–1) of the canvas.</summary>
    public void Stroke(double x0, double y0, double x1, double y1, double radius, bool erase = false)
    {
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(radius) || radius <= 0) return;
        double r = Math.Max(0.5, radius * Math.Max(Width, Height));
        double ax = x0 * Width, ay = y0 * Height, bx = x1 * Width, by = y1 * Height;
        int left = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r)), right = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - r)), bottom = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(ay, by) + r));
        double dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
        byte value = erase ? (byte)0 : (byte)255;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                // Distance from the cell centre to the segment.
                double px = x + 0.5 - ax, py = y + 0.5 - ay;
                double t = length == 0 ? 0 : Math.Clamp((px * dx + py * dy) / length, 0, 1);
                double ex = px - t * dx, ey = py - t * dy;
                if (ex * ex + ey * ey <= r * r) _cells[y * Width + x] = value;
            }
    }

    /// <summary>Paints a rectangle given as fractions (0–1) of the canvas; used by clients that cannot paint.</summary>
    public void FillRectangle(double left, double top, double width, double height)
    {
        if (!(left >= 0 && top >= 0 && width > 0 && height > 0 && left + width <= 1.000001 && top + height <= 1.000001))
            throw new ArgumentOutOfRangeException(nameof(left), "The area must lie inside the picture.");
        int x0 = Math.Clamp((int)Math.Floor(left * Width), 0, Width - 1), y0 = Math.Clamp((int)Math.Floor(top * Height), 0, Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((left + width) * Width), x0 + 1, Width), y1 = Math.Clamp((int)Math.Ceiling((top + height) * Height), y0 + 1, Height);
        for (int y = y0; y < y1; y++) _cells.AsSpan(y * Width + x0, x1 - x0).Fill(255);
    }

    /// <summary>An 8-bit grayscale PNG of the canvas.</summary>
    public byte[] EncodePng()
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; // Bit depth; colour type 0 (grayscale), default compression/filter, no interlace.
        Chunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                for (int y = 0; y < Height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(_cells, y * Width, Width);
                }
            Chunk(output, "IDAT", compressed.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Reads a mask PNG written by <see cref="EncodePng"/> (8-bit grayscale, not interlaced); any nonzero cell counts as painted.</summary>
    public static MaskCanvas DecodePng(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature)) throw new InvalidDataException("The painted area is not a PNG file.");
        int width = 0, height = 0;
        using var data = new MemoryStream();
        int offset = 8;
        while (offset + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12L + length > png.Length) throw new InvalidDataException("The painted area PNG is truncated.");
            string type = System.Text.Encoding.ASCII.GetString(png.Slice(offset + 4, 4));
            var body = png.Slice(offset + 8, length);
            if (Crc32(png.Slice(offset + 4, 4 + length)) != BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + length)..])) throw new InvalidDataException("The painted area PNG is damaged.");
            if (type == "IHDR")
            {
                if (length != 13 || body[8] != 8 || body[9] != 0 || body[12] != 0) throw new InvalidDataException("The painted area must be an 8-bit grayscale PNG.");
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
            }
            else if (type == "IDAT") data.Write(body);
            else if (type == "IEND") break;
            offset += 12 + length;
        }
        if (width is < 1 or > MaximumSide || height is < 1 or > MaximumSide) throw new InvalidDataException("The painted area has an unsupported size.");
        var canvas = new MaskCanvas(width, height);
        data.Position = 0;
        using var zlib = new ZLibStream(data, CompressionMode.Decompress);
        byte[] previous = new byte[width], row = new byte[width];
        for (int y = 0; y < height; y++)
        {
            int filter = zlib.ReadByte();
            zlib.ReadExactly(row);
            for (int x = 0; x < width; x++)
            {
                int a = x > 0 ? row[x - 1] : 0, b = previous[x], c = x > 0 ? previous[x - 1] : 0;
                row[x] = (byte)(row[x] + filter switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("The painted area PNG uses an unknown filter.")
                });
            }
            for (int x = 0; x < width; x++) canvas._cells[y * width + x] = row[x] == 0 ? (byte)0 : (byte)255;
            (previous, row) = (row, previous);
        }
        return canvas;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, body.Length);
        output.Write(number);
        byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. body];
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        output.Write(number);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte value in bytes) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
