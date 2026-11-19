using System.Buffers.Binary;
using System.Text;

namespace Reglet;

/// <summary>
/// creates a minimal sRGB ICC profile (ICC v2, RGB display) for the PDF/A OutputIntent
/// (phase R, R02). The profile is generated from code at runtime; no binary file
/// in the repository (zero dependency).
/// </summary>
internal static class PdfIccProfile
{
    /// <summary>Minimal sRGB profile with the required tags (desc, wtpt, rXYZ/gXYZ/bXYZ, rTRC/gTRC/bTRC, cprt).</summary>
    public static byte[] Srgb()
    {
        var tags = new List<(uint Signature, byte[] Data)>
        {
            (Signature("bTRC"), Curve(2.2)),
            (Signature("bXYZ"), Xyz(0.1430612, 0.0606190, 0.7139280)),
            (Signature("cprt"), Text("Public Domain")),
            (Signature("desc"), Description("sRGB IEC61966-2.1")),
            (Signature("gTRC"), Curve(2.2)),
            (Signature("gXYZ"), Xyz(0.3851515, 0.7168870, 0.0970813)),
            (Signature("rTRC"), Curve(2.2)),
            (Signature("rXYZ"), Xyz(0.4360657, 0.2224932, 0.0139239)),
            (Signature("wtpt"), Xyz(0.9642, 1.0000, 0.8249)),
        };
        tags.Sort((left, right) => left.Signature.CompareTo(right.Signature));

        var headerSize = 128;
        var tableSize = 4 + tags.Count * 12;
        var offset = headerSize + tableSize;
        var offsets = new int[tags.Count];
        for (var i = 0; i < tags.Count; i++)
        {
            offsets[i] = offset;
            offset += (tags[i].Data.Length + 3) & ~3;
        }

        var profile = new byte[offset];
        WriteHeader(profile, headerSize, tags.Count);

        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(headerSize, 4), (uint)tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            var entry = headerSize + 4 + i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry, 4), tags[i].Signature);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 4, 4), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 8, 4), (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(profile, offsets[i]);
        }

        // set the profile size (field 0) only after assembly.
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(0, 4), (uint)profile.Length);
        return profile;
    }

    private static void WriteHeader(byte[] profile, int headerSize, int tagCount)
    {
        _ = tagCount;
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8, 4), 0x02100000); // Version 2.1
        Encoding.ASCII.GetBytes("mntr").CopyTo(profile, 12); // Display-Geraet
        Encoding.ASCII.GetBytes("RGB ").CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(profile, 20);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24, 2), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26, 2), 10);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28, 2), 1);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);

        // PCS-Beleuchtung D50 (s15Fixed16).
        WriteFixed16(profile.AsSpan(68, 4), 0.9642);
        WriteFixed16(profile.AsSpan(72, 4), 1.0000);
        WriteFixed16(profile.AsSpan(76, 4), 0.8249);
        _ = headerSize;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(data, 0);
        WriteFixed16(data.AsSpan(8, 4), x);
        WriteFixed16(data.AsSpan(12, 4), y);
        WriteFixed16(data.AsSpan(16, 4), z);
        return data;
    }

    private static byte[] Curve(double gamma)
    {
        var data = new byte[14];
        Encoding.ASCII.GetBytes("curv").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12, 2), (ushort)Math.Round(gamma * 256));
        return data;
    }

    private static byte[] Text(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        var data = new byte[8 + bytes.Length + 1];
        Encoding.ASCII.GetBytes("text").CopyTo(data, 0);
        bytes.CopyTo(data, 8);
        return data;
    }

    private static byte[] Description(string value)
    {
        var ascii = Encoding.ASCII.GetBytes(value);
        var data = new byte[12 + ascii.Length + 1 + 4 + 4 + 2 + 1];
        Encoding.ASCII.GetBytes("desc").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), (uint)(ascii.Length + 1));
        ascii.CopyTo(data, 12);
        // Unicode language code, Unicode count, ScriptCode and ScriptCode count remain 0.
        return data;
    }

    private static void WriteFixed16(Span<byte> destination, double value)
        => BinaryPrimitives.WriteInt32BigEndian(destination, (int)Math.Round(value * 65536));

    private static uint Signature(string value)
        => ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
}
