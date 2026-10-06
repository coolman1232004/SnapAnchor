using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Services;

/// <summary>Writes animated GIF frames immediately so recording memory does not grow with duration.</summary>
internal sealed class StreamingGifWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly TimeSpan _frameDuration;
    private double _delayRemainder;
    private readonly byte[] _indices;
    private readonly byte[] _row;
    private bool _completed;

    public StreamingGifWriter(string path, int width, int height, TimeSpan frameDuration)
    {
        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "GIF dimensions must be between 1 and 65,535 pixels.");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        _width = width;
        _height = height;
        _frameDuration = frameDuration;
        _indices = new byte[checked(width * height)];
        _row = new byte[checked(width * 4)];
        WriteHeader();
    }

    public void AppendFrame(BitmapSource source)
        => AppendFrame(source, _frameDuration);

    public void AppendFrame(BitmapSource source, TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (source.PixelWidth != _width || source.PixelHeight != _height)
            throw new ArgumentException("Every GIF frame must have the same pixel dimensions.", nameof(source));

        _stream.Write([0x21, 0xF9, 0x04, 0x04]);
        var exactDelay = Math.Max(2, duration.TotalMilliseconds / 10d) + _delayRemainder;
        var delay = (ushort)Math.Clamp((int)Math.Round(exactDelay), 2, ushort.MaxValue);
        _delayRemainder = exactDelay - delay;
        WriteUInt16(delay);
        _stream.WriteByte(0);
        _stream.WriteByte(0);

        _stream.WriteByte(0x2C);
        WriteUInt16(0);
        WriteUInt16(0);
        WriteUInt16((ushort)_width);
        WriteUInt16((ushort)_height);
        _stream.WriteByte(0);
        _stream.WriteByte(8);
        WriteLzw(Quantize(source));
    }

    public void Complete()
    {
        if (_completed) return;
        _stream.WriteByte(0x3B);
        _stream.Flush(flushToDisk: true);
        _completed = true;
        _stream.Dispose();
    }

    private void WriteHeader()
    {
        _stream.Write(Encoding.ASCII.GetBytes("GIF89a"));
        WriteUInt16((ushort)_width);
        WriteUInt16((ushort)_height);
        _stream.WriteByte(0xF7); // 256-entry global palette.
        _stream.WriteByte(0);
        _stream.WriteByte(0);
        // A 6x6x6 color cube plus a 40-step gray ramp preserves UI colors and
        // antialiased text better than a simple 3-3-2 bit palette.
        for (var red = 0; red < 6; red++)
        for (var green = 0; green < 6; green++)
        for (var blue = 0; blue < 6; blue++)
        {
            _stream.WriteByte((byte)(red * 51));
            _stream.WriteByte((byte)(green * 51));
            _stream.WriteByte((byte)(blue * 51));
        }
        for (var gray = 0; gray < 40; gray++)
        {
            var value = (byte)(gray * 255 / 39);
            _stream.WriteByte(value);
            _stream.WriteByte(value);
            _stream.WriteByte(value);
        }
        _stream.Write([0x21, 0xFF, 0x0B]);
        _stream.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        _stream.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    private byte[] Quantize(BitmapSource source)
    {
        BitmapSource bgra = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            bgra = converted;
        }

        var indices = _indices;
        var row = _row;
        for (var y = 0; y < _height; y++)
        {
            bgra.CopyPixels(new Int32Rect(0, y, _width, 1), row, row.Length, 0);
            var destination = y * _width;
            for (var x = 0; x < _width; x++)
            {
                var offset = x * 4;
                var blue = row[offset];
                var green = row[offset + 1];
                var red = row[offset + 2];
                var maximum = Math.Max(red, Math.Max(green, blue));
                var minimum = Math.Min(red, Math.Min(green, blue));
                indices[destination + x] = maximum - minimum <= 12
                    ? (byte)(216 + ((red + green + blue) / 3 * 39 + 127) / 255)
                    : (byte)(((red * 5 + 127) / 255) * 36 + ((green * 5 + 127) / 255) * 6 + (blue * 5 + 127) / 255);
            }
        }
        return indices;
    }

    private void WriteLzw(ReadOnlySpan<byte> indices)
    {
        const int clearCode = 256;
        const int endCode = 257;
        var dictionary = new Dictionary<int, int>(4096);
        var codeSize = 9;
        var nextCode = 258;
        using var blocks = new GifSubBlockWriter(_stream);
        blocks.WriteCode(clearCode, codeSize);
        var prefix = (int)indices[0];

        for (var index = 1; index < indices.Length; index++)
        {
            var suffix = indices[index];
            var key = (prefix << 8) | suffix;
            if (dictionary.TryGetValue(key, out var combined))
            {
                prefix = combined;
                continue;
            }

            blocks.WriteCode(prefix, codeSize);
            if (nextCode < 4096)
            {
                dictionary[key] = nextCode++;
                if (nextCode == (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                blocks.WriteCode(clearCode, codeSize);
                dictionary.Clear();
                codeSize = 9;
                nextCode = 258;
            }
            prefix = suffix;
        }

        blocks.WriteCode(prefix, codeSize);
        blocks.WriteCode(endCode, codeSize);
        blocks.Complete();
    }

    private void WriteUInt16(ushort value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
    }

    public void Dispose()
    {
        if (!_completed) _stream.Dispose();
        _completed = true;
    }

    private sealed class GifSubBlockWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _block = new byte[255];
        private int _blockLength;
        private uint _bits;
        private int _bitCount;
        private bool _completed;

        public GifSubBlockWriter(Stream stream) => _stream = stream;

        public void WriteCode(int code, int size)
        {
            _bits |= (uint)code << _bitCount;
            _bitCount += size;
            while (_bitCount >= 8)
            {
                WriteByte((byte)_bits);
                _bits >>= 8;
                _bitCount -= 8;
            }
        }

        public void Complete()
        {
            if (_completed) return;
            if (_bitCount > 0) WriteByte((byte)_bits);
            FlushBlock();
            _stream.WriteByte(0);
            _completed = true;
        }

        private void WriteByte(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == _block.Length) FlushBlock();
        }

        private void FlushBlock()
        {
            if (_blockLength == 0) return;
            _stream.WriteByte((byte)_blockLength);
            _stream.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }

        public void Dispose() => Complete();
    }
}
