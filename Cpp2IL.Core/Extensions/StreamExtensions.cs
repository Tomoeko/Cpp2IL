using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.IO.Compression;

namespace Cpp2IL.Core.Extensions;

public static class StreamExtensions
{
    public static uint ReadUnityCompressedUint(this Stream stream)
    {
        var b = ReadRequiredByte(stream);
        if (b < 128)
            return (uint)b;
        if (b == 240)
        {
            //Full Uint
            return (uint)(ReadRequiredByte(stream) | ReadRequiredByte(stream) << 8 |
                ReadRequiredByte(stream) << 16 | ReadRequiredByte(stream) << 24);
        }

        //Special constant values
        if (b == byte.MaxValue)
            return uint.MaxValue;
        if (b == 254)
            return uint.MaxValue - 1;

        if ((b & 192) == 192)
        {
            //3 more to read
            return (uint)((b & ~192U) << 24 | (uint)(ReadRequiredByte(stream) << 16) | (uint)(ReadRequiredByte(stream) << 8) | (uint)ReadRequiredByte(stream));
        }

        if ((b & 128) == 128)
        {
            //1 more to read
            return (uint)((b & ~128U) << 8 | (uint)ReadRequiredByte(stream));
        }


        throw new Exception($"How did we even get here? Invalid compressed int first byte {b}");
    }

    private static int ReadRequiredByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new EndOfStreamException("Truncated compressed integer") : value;
    }

    public static int ReadUnityCompressedInt(this Stream stream)
    {
        //Ref libil2cpp, il2cpp\utils\ReadCompressedInt32
        var unsigned = stream.ReadUnityCompressedUint();

        if (unsigned == uint.MaxValue)
            return int.MinValue;

        var isNegative = (unsigned & 1) == 1;
        unsigned >>= 1;
        if (isNegative)
            return -(int)(unsigned + 1);

        return (int)unsigned;
    }

    public static string ReadUnicodeString(this BinaryReader reader)
    {
        List<byte> bytes = [];
        var continueReading = true;
        var lastWasNull = false;
        while (continueReading)
        {
            var b = reader.ReadByte();

            if (b == 0 && lastWasNull)
                //Double null is a terminator for unicode strings
                continueReading = false;

            lastWasNull = b == 0;
            bytes.Add(b);
        }

        bytes.Add(reader.ReadByte()); //Last byte of null terminator will always be skipped - unskip it

        return Encoding.Unicode.GetString(bytes.ToArray()).TrimEnd('\0');
    }

    public static byte[] ReadBytes(this Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[] ReadBytes(this ZipArchiveEntry entry) => entry.Open().ReadBytes();
}
