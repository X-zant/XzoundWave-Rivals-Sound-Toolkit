using System.IO;

namespace Xzound.Vorbis;

/// <summary>LSB-first bit reader, the order Vorbis and Wwise both use.</summary>
public sealed class BitReader(byte[] data)
{
    private int _pos;
    public int Position => _pos;
    public bool AtEnd => _pos >= data.Length * 8;

    public uint Read(int bits)
    {
        uint v = 0;
        for (var i = 0; i < bits; i++)
        {
            if (_pos >= data.Length * 8) throw new EndOfStreamException("ran off the end of the packet");
            v |= (uint)((data[_pos >> 3] >> (_pos & 7)) & 1) << i;
            _pos++;
        }
        return v;
    }

    public int ReadInt(int bits) => (int)Read(bits);
    public bool Flag() => Read(1) != 0;
}

/// <summary>LSB-first bit writer.</summary>
public sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bit;

    public int BitLength => _bytes.Count * 8 - (_bit == 0 ? 0 : 8 - _bit);

    public void Write(uint value, int bits)
    {
        for (var i = 0; i < bits; i++)
        {
            if (_bit == 0) _bytes.Add(0);
            if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(1 << _bit);
            _bit = (_bit + 1) & 7;
        }
    }

    public void Write(int value, int bits) => Write((uint)value, bits);
    public void Flag(bool b) => Write(b ? 1u : 0u, 1);

    /// <summary>Pads the final byte with zeroes, as a packet boundary requires.</summary>
    public byte[] ToArray() => _bytes.ToArray();
}

public static class BitMath
{
    /// <summary>Vorbis' ilog: number of bits needed to represent n.</summary>
    public static int ILog(int n)
    {
        var r = 0;
        while (n > 0) { r++; n >>= 1; }
        return r;
    }
}
