using System.Buffers;
using System.Buffers.Binary;

namespace DataPitcher.Core.Identity;

/// <summary>
/// Fixed, big-endian layouts for stable-key components. Variable-length values carry a 4-byte length; DateTime carries
/// its Kind so a value round-trips to the exact instant and kind the driver produced.
/// </summary>
public static class StableKeyBytes
{
    public static void WriteByte(ArrayBufferWriter<byte> buffer, byte value)
    {
        buffer.GetSpan(1)[0] = value;
        buffer.Advance(1);
    }

    public static void WriteInt16(ArrayBufferWriter<byte> buffer, short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(buffer.GetSpan(2), value);
        buffer.Advance(2);
    }

    public static void WriteInt32(ArrayBufferWriter<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.GetSpan(4), value);
        buffer.Advance(4);
    }

    public static void WriteInt64(ArrayBufferWriter<byte> buffer, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(buffer.GetSpan(8), value);
        buffer.Advance(8);
    }

    public static void WriteBytes(ArrayBufferWriter<byte> buffer, byte[] value)
    {
        WriteInt32(buffer, value.Length);
        buffer.Write(value);
    }

    public static void WriteGuid(ArrayBufferWriter<byte> buffer, Guid value)
    {
        value.TryWriteBytes(buffer.GetSpan(16));
        buffer.Advance(16);
    }

    public static void WriteDecimal(ArrayBufferWriter<byte> buffer, decimal value)
    {
        foreach (var part in decimal.GetBits(value))
            WriteInt32(buffer, part);
    }

    public static void WriteDateTime(ArrayBufferWriter<byte> buffer, DateTime value)
    {
        WriteInt64(buffer, value.Ticks);
        WriteByte(buffer, (byte)value.Kind);
    }

    public static void WriteDateTimeOffset(ArrayBufferWriter<byte> buffer, DateTimeOffset value)
    {
        WriteInt64(buffer, value.Ticks);
        WriteInt16(buffer, (short)value.Offset.TotalMinutes);
    }

    public static byte ReadByte(byte[] bytes, ref int offset) => bytes[offset++];

    public static short ReadInt16(byte[] bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    public static int ReadInt32(byte[] bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    public static long ReadInt64(byte[] bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset, 8));
        offset += 8;
        return value;
    }

    public static byte[] ReadBytes(byte[] bytes, ref int offset)
    {
        var length = ReadInt32(bytes, ref offset);
        var value = bytes.AsSpan(offset, length).ToArray();
        offset += length;
        return value;
    }

    public static Guid ReadGuid(byte[] bytes, ref int offset)
    {
        var value = new Guid(bytes.AsSpan(offset, 16));
        offset += 16;
        return value;
    }

    public static decimal ReadDecimal(byte[] bytes, ref int offset)
    {
        var parts = new int[4];
        for (var index = 0; index < 4; index++)
            parts[index] = ReadInt32(bytes, ref offset);
        return new decimal(parts);
    }

    public static DateTime ReadDateTime(byte[] bytes, ref int offset)
    {
        var ticks = ReadInt64(bytes, ref offset);
        return new DateTime(ticks, (DateTimeKind)ReadByte(bytes, ref offset));
    }

    public static DateTimeOffset ReadDateTimeOffset(byte[] bytes, ref int offset)
    {
        var ticks = ReadInt64(bytes, ref offset);
        return new DateTimeOffset(ticks, TimeSpan.FromMinutes(ReadInt16(bytes, ref offset)));
    }
}
