using System.Globalization;
using System.IO.Compression;

namespace SurveyToRevit.Core.Site;

/// <summary>
/// A height grid in British National Grid metres: the Environment Agency LiDAR tiles
/// (GeoTIFF, or the older ESRI ASCII .asc). Only the part inside a requested box is
/// read, so 5 km tiles stay cheap. <see cref="OriginX"/>/<see cref="OriginY"/> are
/// the top-left corner of the top-left cell; rows run southwards.
/// </summary>
public sealed class GeoRaster
{
    public double OriginX { get; }
    public double OriginY { get; }
    public double CellSize { get; }
    public int Width { get; }
    public int Height { get; }
    readonly float[] _v;

    public GeoRaster(double originX, double originY, double cellSize, int width, int height, float[] values)
    {
        OriginX = originX; OriginY = originY; CellSize = cellSize; Width = width; Height = height; _v = values;
    }

    /// <summary>Height at a point (metres), or NaN outside the grid or where there is no data.</summary>
    public double At(double x, double y)
    {
        int c = (int)Math.Floor((x - OriginX) / CellSize), r = (int)Math.Floor((OriginY - y) / CellSize);
        if (c < 0 || r < 0 || c >= Width || r >= Height) return double.NaN;
        return _v[r * Width + c];
    }

    /// <summary>Reads a .tif/.tiff or .asc tile, keeping only cells inside the box (metres); null if the tile misses the box.</summary>
    public static GeoRaster? Read(string path, double minX, double minY, double maxX, double maxY)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".asc" ? ReadAsc(path, minX, minY, maxX, maxY) : GeoTiff.Read(path, minX, minY, maxX, maxY);
    }

    static GeoRaster? ReadAsc(string path, double minX, double minY, double maxX, double maxY)
    {
        using var reader = new StreamReader(path);
        var header = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string? line;
        string? firstData = null;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && char.IsLetter(parts[0][0])) { header[parts[0]] = double.Parse(parts[1], CultureInfo.InvariantCulture); continue; }
            firstData = line;
            break;
        }
        int ncols = (int)header["ncols"], nrows = (int)header["nrows"];
        double cell = header["cellsize"];
        double x0 = header.TryGetValue("xllcorner", out var xc) ? xc : header["xllcenter"] - cell / 2;
        double y0 = header.TryGetValue("yllcorner", out var yc) ? yc : header["yllcenter"] - cell / 2;
        double noData = header.TryGetValue("nodata_value", out var nd) ? nd : -9999;
        double top = y0 + nrows * cell;
        var win = Window(x0, top, cell, ncols, nrows, minX, minY, maxX, maxY);
        if (win == null) return null;
        var (c0, r0, w, h) = win.Value;
        var values = new float[w * h];
        int row = 0;
        var pending = firstData;
        while (row < r0 + h)
        {
            var text = pending ?? reader.ReadLine();
            pending = null;
            if (text == null) break;
            if (row >= r0)
            {
                var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int c = 0; c < w && c0 + c < parts.Length; c++)
                {
                    double v = double.Parse(parts[c0 + c], CultureInfo.InvariantCulture);
                    values[(row - r0) * w + c] = Math.Abs(v - noData) < 1e-6 ? float.NaN : (float)v;
                }
            }
            row++;
        }
        return new GeoRaster(x0 + c0 * cell, top - r0 * cell, cell, w, h, values);
    }

    /// <summary>The cells of a grid that overlap the box: first column, first row, width, height.</summary>
    internal static (int C0, int R0, int W, int H)? Window(double originX, double originY, double cell, int width, int height,
        double minX, double minY, double maxX, double maxY)
    {
        int c0 = Math.Max(0, (int)Math.Floor((minX - originX) / cell)), c1 = Math.Min(width, (int)Math.Ceiling((maxX - originX) / cell));
        int r0 = Math.Max(0, (int)Math.Floor((originY - maxY) / cell)), r1 = Math.Min(height, (int)Math.Ceiling((originY - minY) / cell));
        return c1 <= c0 || r1 <= r0 ? null : (c0, r0, c1 - c0, r1 - r0);
    }
}

/// <summary>
/// Minimal GeoTIFF reader for single-band height grids: 32-bit float (or 16/32-bit integer)
/// samples, in strips or tiles, uncompressed, LZW or Deflate, with or without a predictor.
/// That covers the Environment Agency downloads; anything else gets a clear error.
/// </summary>
static class GeoTiff
{
    /// <summary>
    /// A world file beside the image (name.tfw, .tifw or .wld: six lines A D B E C F, C/F the centre of the
    /// top-left cell): cell size and top-left corner; null if there is none.
    /// </summary>
    internal static (double Cell, double OriginX, double OriginY)? WorldFile(string path)
    {
        foreach (var ext in new[] { ".tfw", ".tifw", ".wld" })
        {
            var f = Path.ChangeExtension(path, ext);
            if (!File.Exists(f)) continue;
            var v = File.ReadAllLines(f).Where(l => l.Trim().Length > 0).Take(6)
                .Select(l => double.Parse(l.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            if (v.Length < 6 || v[0] <= 0) continue;
            return (v[0], v[4] - v[0] / 2, v[5] - v[3] / 2);
        }
        return null;
    }

    /// <summary>Pixel size of a TIFF and whether it carries its own map position.</summary>
    internal static (int Width, int Height, bool Placed) Probe(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        bool little = br.ReadUInt16() == 0x4949;
        ushort U16() { var b = br.ReadBytes(2); if (!little) Array.Reverse(b); return BitConverter.ToUInt16(b); }
        uint U32() { var b = br.ReadBytes(4); if (!little) Array.Reverse(b); return BitConverter.ToUInt32(b); }
        U16();
        fs.Position = U32();
        int count = U16(), w = 0, h = 0;
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int tag = U16(), type = U16();
            U32();
            long next = fs.Position + 4;
            if (tag is 256 or 257) { uint v = type == 3 ? U16() : U32(); if (tag == 256) w = (int)v; else h = (int)v; }
            seen.Add(tag);
            fs.Position = next;
        }
        return (w, h, seen.Contains(34264) || (seen.Contains(33550) && seen.Contains(33922)));
    }

    public static GeoRaster? Read(string path, double minX, double minY, double maxX, double maxY)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var order = br.ReadUInt16();
        bool little = order == 0x4949;
        if (!little && order != 0x4D4D) throw new InvalidDataException($"{Path.GetFileName(path)} is not a TIFF file.");
        ushort U16() { var b = br.ReadBytes(2); if (!little) Array.Reverse(b); return BitConverter.ToUInt16(b); }
        uint U32() { var b = br.ReadBytes(4); if (!little) Array.Reverse(b); return BitConverter.ToUInt32(b); }
        var magic = U16();
        if (magic == 43) throw new InvalidDataException($"{Path.GetFileName(path)} is a BigTIFF; save it as a normal GeoTIFF or ASCII grid.");
        if (magic != 42) throw new InvalidDataException($"{Path.GetFileName(path)} is not a TIFF file.");
        fs.Position = U32();

        // tags: number arrays (as doubles) and text
        var tags = new Dictionary<int, double[]>();
        var text = new Dictionary<int, string>();
        int count = U16();
        var entries = new List<(int Tag, int Type, uint N, long ValuePos)>();
        for (int i = 0; i < count; i++)
        {
            int tag = U16(), type = U16();
            uint n = U32();
            entries.Add((tag, type, n, fs.Position));
            fs.Position += 4;
        }
        foreach (var (tag, type, n, valuePos) in entries)
        {
            int size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0 };
            if (size == 0) continue;
            fs.Position = valuePos;
            if (size * n > 4) fs.Position = U32();
            if (type == 2) { text[tag] = System.Text.Encoding.ASCII.GetString(br.ReadBytes((int)n)).TrimEnd('\0'); continue; }
            var values = new double[n];
            for (int k = 0; k < n; k++)
            {
                byte[] b = br.ReadBytes(size);
                if (!little && size > 1 && type != 5 && type != 10) Array.Reverse(b);
                values[k] = type switch
                {
                    1 or 7 => b[0], 6 => (sbyte)b[0],
                    3 => BitConverter.ToUInt16(b), 8 => BitConverter.ToInt16(b),
                    4 => BitConverter.ToUInt32(b), 9 => BitConverter.ToInt32(b),
                    11 => BitConverter.ToSingle(b),
                    12 => BitConverter.ToDouble(b),
                    _ => Rational(b, type == 10, little),
                };
            }
            tags[tag] = values;
        }

        string name = Path.GetFileName(path);
        int width = (int)tags[256][0], height = (int)tags[257][0];
        int bits = tags.TryGetValue(258, out var bps) ? (int)bps[0] : 1;
        int compression = tags.TryGetValue(259, out var cmp) ? (int)cmp[0] : 1;
        int spp = tags.TryGetValue(277, out var sp) ? (int)sp[0] : 1;
        int predictor = tags.TryGetValue(317, out var pr) ? (int)pr[0] : 1;
        int format = tags.TryGetValue(339, out var sf) ? (int)sf[0] : 1;
        if (spp != 1) throw new InvalidDataException($"{name} has {spp} bands; a height grid has one.");
        if (!(bits == 32 || bits == 16) || (format == 3 && bits != 32)) throw new InvalidDataException($"{name}: {bits}-bit samples are not supported.");
        if (compression is not (1 or 5 or 8 or 32946)) throw new InvalidDataException($"{name}: TIFF compression {compression} is not supported (use LZW, Deflate or none).");
        double cell, originX, originY;
        if (tags.TryGetValue(33550, out var scale) && tags.TryGetValue(33922, out var tie))
        {
            cell = scale[0];
            originX = tie[3] - tie[0] * cell; originY = tie[4] + tie[1] * scale[1];
        }
        else if (tags.TryGetValue(34264, out var m) && m.Length >= 8)
        {
            // ModelTransformation matrix (row by row): x = m0·col + m1·row + m3, y = m4·col + m5·row + m7
            cell = m[0]; originX = m[3]; originY = m[7];
        }
        else if (WorldFile(path) is (double wc, double wx, double wy))
        {
            cell = wc; originX = wx; originY = wy;
        }
        else throw new InvalidDataException($"{name} has no map position (not a GeoTIFF, and no .tfw world file beside it).");
        double noData = text.TryGetValue(42113, out var nd) && double.TryParse(nd, NumberStyles.Float, CultureInfo.InvariantCulture, out var ndv) ? ndv : double.NaN;

        var win = GeoRaster.Window(originX, originY, cell, width, height, minX, minY, maxX, maxY);
        if (win == null) return null;
        var (c0, r0, w, h) = win.Value;
        var result = new float[w * h];
        Array.Fill(result, float.NaN);

        bool tiled = tags.ContainsKey(322);
        int blockW = tiled ? (int)tags[322][0] : width;
        int blockH = tiled ? (int)tags[323][0] : (tags.TryGetValue(278, out var rps) ? (int)Math.Min(rps[0], height) : height);
        var offsets = tags[tiled ? 324 : 273];
        var counts = tags[tiled ? 325 : 279];
        int across = (width + blockW - 1) / blockW;
        int bytesPer = bits / 8;

        for (int b = 0; b < offsets.Length; b++)
        {
            int bx = (b % across) * blockW, by = (b / across) * blockH;
            if (bx >= c0 + w || bx + blockW <= c0 || by >= r0 + h || by + blockH <= r0) continue;
            fs.Position = (long)offsets[b];
            var raw = br.ReadBytes((int)counts[b]);
            var data = compression switch
            {
                5 => Lzw(raw),
                8 or 32946 => Inflate(raw),
                _ => raw,
            };
            int rows = Math.Min(blockH, data.Length / (blockW * bytesPer));
            if (predictor == 2) UndoHorizontal(data, blockW, rows, bytesPer, little);
            if (predictor == 3) data = UndoFloatingPoint(data, blockW, rows, bytesPer);
            for (int y = 0; y < rows; y++)
            {
                int r = by + y - r0;
                if (r < 0 || r >= h) continue;
                for (int x = 0; x < blockW; x++)
                {
                    int c = bx + x - c0;
                    if (c < 0 || c >= w) continue;
                    int i = (y * blockW + x) * bytesPer;
                    double v = Sample(data, i, bits, format, little, predictor == 3);
                    result[r * w + c] = double.IsNaN(v) || v < -1000 || (!double.IsNaN(noData) && Math.Abs(v - noData) < 1e-3) ? float.NaN : (float)v;
                }
            }
        }
        return new GeoRaster(originX + c0 * cell, originY - r0 * cell, cell, w, h, result);
    }

    static double Rational(byte[] b, bool signed, bool little)
    {
        byte[] n = b[..4], d = b[4..];
        if (!little) { Array.Reverse(n); Array.Reverse(d); }
        return signed ? (double)BitConverter.ToInt32(n) / BitConverter.ToInt32(d) : (double)BitConverter.ToUInt32(n) / BitConverter.ToUInt32(d);
    }

    // After the floating-point predictor the bytes are in big-endian order whatever the file's order.
    static double Sample(byte[] data, int i, int bits, int format, bool little, bool bigEndianFloat)
    {
        if (i + bits / 8 > data.Length) return double.NaN;
        var b = new byte[bits / 8];
        Array.Copy(data, i, b, 0, b.Length);
        if (bigEndianFloat ? BitConverter.IsLittleEndian : little != BitConverter.IsLittleEndian) Array.Reverse(b);
        return (bits, format) switch
        {
            (32, 3) => BitConverter.ToSingle(b),
            (32, 2) => BitConverter.ToInt32(b),
            (32, _) => BitConverter.ToUInt32(b),
            (16, 2) => BitConverter.ToInt16(b),
            _ => BitConverter.ToUInt16(b),
        };
    }

    static byte[] Inflate(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>TIFF LZW (MSB-first codes, 9–12 bits, early change).</summary>
    static byte[] Lzw(byte[] raw)
    {
        var output = new List<byte>(raw.Length * 3);
        var table = new List<byte[]>(4096);
        void Reset() { table.Clear(); for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i }); table.Add(Array.Empty<byte>()); table.Add(Array.Empty<byte>()); }
        Reset();
        int bitPos = 0, codeLen = 9;
        byte[]? prev = null;
        int Next()
        {
            if (bitPos + codeLen > raw.Length * 8) return 257;
            int v = 0;
            for (int k = 0; k < codeLen; k++, bitPos++) v = (v << 1) | ((raw[bitPos >> 3] >> (7 - (bitPos & 7))) & 1);
            return v;
        }
        while (true)
        {
            int code = Next();
            if (code == 257) break;
            if (code == 256) { Reset(); codeLen = 9; prev = null; continue; }
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else if (prev != null) { entry = new byte[prev.Length + 1]; prev.CopyTo(entry, 0); entry[^1] = prev[0]; }
            else break;
            output.AddRange(entry);
            if (prev != null)
            {
                var add = new byte[prev.Length + 1];
                prev.CopyTo(add, 0); add[^1] = entry[0];
                table.Add(add);
            }
            prev = entry;
            if (table.Count + 1 >= (1 << codeLen) && codeLen < 12) codeLen++;
        }
        return output.ToArray();
    }

    static void UndoHorizontal(byte[] data, int width, int rows, int bytesPer, bool little)
    {
        for (int y = 0; y < rows; y++)
            for (int x = 1; x < width; x++)
            {
                int i = (y * width + x) * bytesPer, p = i - bytesPer;
                if (bytesPer == 2)
                {
                    ushort a = little ? (ushort)(data[i] | data[i + 1] << 8) : (ushort)(data[i] << 8 | data[i + 1]);
                    ushort b = little ? (ushort)(data[p] | data[p + 1] << 8) : (ushort)(data[p] << 8 | data[p + 1]);
                    ushort s = (ushort)(a + b);
                    if (little) { data[i] = (byte)s; data[i + 1] = (byte)(s >> 8); } else { data[i] = (byte)(s >> 8); data[i + 1] = (byte)s; }
                }
                else
                {
                    uint a = little ? BitConverter.ToUInt32(data, i) : (uint)(data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3]);
                    uint b = little ? BitConverter.ToUInt32(data, p) : (uint)(data[p] << 24 | data[p + 1] << 16 | data[p + 2] << 8 | data[p + 3]);
                    uint s = a + b;
                    var bytes = BitConverter.GetBytes(s);
                    if (!little) Array.Reverse(bytes);
                    Array.Copy(bytes, 0, data, i, 4);
                }
            }
    }

    /// <summary>Floating-point predictor: per row, byte differences, then bytes regrouped by significance.</summary>
    static byte[] UndoFloatingPoint(byte[] data, int width, int rows, int bytesPer)
    {
        var output = new byte[data.Length];
        int rowBytes = width * bytesPer;
        for (int y = 0; y < rows; y++)
        {
            int o = y * rowBytes;
            for (int k = 1; k < rowBytes; k++) data[o + k] = (byte)(data[o + k] + data[o + k - 1]);
            for (int x = 0; x < width; x++)
                for (int b = 0; b < bytesPer; b++)
                    output[o + x * bytesPer + b] = data[o + b * width + x];
        }
        return output;
    }
}
