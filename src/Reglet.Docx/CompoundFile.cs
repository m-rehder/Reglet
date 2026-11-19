namespace Reglet;

/// <summary>
/// a minimal reader and writer for the Compound File Binary format (MS-CFB), as far as DOCX
/// agile encryption needs it (OF-E01): the two streams <c>EncryptionInfo</c> and
/// <c>EncryptedPackage</c>. Version 3 (512-byte sectors), the FAT, the mini stream for small
/// streams and DIFAT sectors for large files.
/// </summary>
internal static class CompoundFile
{
    /// <summary>the 8-byte signature of every compound file.</summary>
    internal static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int MiniStreamCutoff = 4096;
    private const int MaxRegionEntries = 109;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint DifatSector = 0xFFFFFFFC;
    private const int DirectoryEntrySize = 128;

    /// <summary>Whether the data starts with the compound-file signature.</summary>
    public static bool IsCompoundFile(ReadOnlySpan<byte> data)
        => data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    /// <summary>
    /// writes a compound file with the given streams. A stream smaller than 4096 bytes goes into
    /// the mini stream, as the format requires.
    /// </summary>
    public static void Write(IReadOnlyList<(string Name, byte[] Data)> streams, Stream output)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(output);
        if (streams.Count > 64)
        {
            throw new DocumentValidationException("At most 64 streams are supported in a compound file.");
        }

        // 1. The mini stream: all small streams one after the other, in 64-byte mini sectors.
        var miniStream = new List<byte>();
        var miniFat = new List<uint>();
        var miniStart = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, data) in streams)
        {
            if (data.Length >= MiniStreamCutoff)
            {
                continue;
            }

            miniStart[name] = miniFat.Count;
            var sectors = (data.Length + MiniSectorSize - 1) / MiniSectorSize;
            for (var index = 0; index < Math.Max(1, sectors); index++)
            {
                var offset = index * MiniSectorSize;
                var chunk = new byte[MiniSectorSize];
                var count = data.Length == 0 ? 0 : Math.Min(MiniSectorSize, data.Length - offset);
                if (count > 0)
                {
                    Array.Copy(data, offset, chunk, 0, count);
                }

                miniStream.AddRange(chunk);
                miniFat.Add(index == Math.Max(1, sectors) - 1 ? EndOfChain : (uint)miniFat.Count + 1);
            }
        }

        var miniFatSectors = (miniFat.Count * 4 + SectorSize - 1) / SectorSize;

        // 2. Regular streams: the mini stream container and every large stream, sector by sector.
        var regular = new List<byte[]>();
        var chains = new Dictionary<string, (int Start, long Length)>(StringComparer.Ordinal);
        var nextSector = 0;
        if (miniStream.Count > 0)
        {
            var sectors = Split(miniStream.ToArray());
            chains["\u0001mini"] = (nextSector, miniStream.Count);
            nextSector += sectors.Count;
            regular.AddRange(sectors);
        }

        foreach (var (name, data) in streams)
        {
            if (data.Length < MiniStreamCutoff)
            {
                continue;
            }

            var sectors = Split(data);
            chains[name] = (nextSector, data.Length);
            nextSector += sectors.Count;
            regular.AddRange(sectors);
        }

        var miniFatStart = nextSector;
        nextSector += miniFatSectors;

        // 3. The directory: the root entry and one entry per stream.
        var directoryEntries = streams.Count + 1;
        var directorySectors = (directoryEntries * DirectoryEntrySize + SectorSize - 1) / SectorSize;
        var directoryStart = nextSector;
        nextSector += directorySectors;

        var dataSectors = nextSector;

        // 4. The FAT covers the data sectors, itself and the DIFAT sectors.
        var (fatSectors, difatSectors) = FitFat(dataSectors);
        var fat = Enumerable.Repeat(FreeSector, fatSectors * (SectorSize / 4)).ToArray();

        void Chain(int start, int sectors)
        {
            for (var index = 0; index < sectors; index++)
            {
                fat[start + index] = index == sectors - 1 ? EndOfChain : (uint)(start + index + 1);
            }
        }

        if (miniFatSectors > 0)
        {
            Chain(miniFatStart, miniFatSectors);
        }

        Chain(directoryStart, directorySectors);
        if (miniStream.Count > 0)
        {
            Chain(chains["\u0001mini"].Start, (miniStream.Count + SectorSize - 1) / SectorSize);
        }

        foreach (var (name, data) in streams.Where(stream => stream.Data.Length >= MiniStreamCutoff))
        {
            Chain(chains[name].Start, (data.Length + SectorSize - 1) / SectorSize);
        }

        for (var index = 0; index < fatSectors; index++)
        {
            fat[dataSectors + index] = FatSector;
        }

        for (var index = 0; index < difatSectors; index++)
        {
            fat[dataSectors + fatSectors + index] = DifatSector;
        }

        // 5. Write the header.
        var header = new byte[SectorSize];
        Signature.CopyTo(header, 0);
        WriteUInt16(header, 24, 0x003E);           // minor version
        WriteUInt16(header, 26, 0x0003);           // major version 3
        WriteUInt16(header, 28, 0xFFFE);           // byte order
        WriteUInt16(header, 30, 9);                // sector shift (512 bytes)
        WriteUInt16(header, 32, 6);                // mini sector shift (64 bytes)
        WriteUInt32(header, 40, 0);                // directory sectors (0 for version 3)
        WriteUInt32(header, 44, (uint)fatSectors);
        WriteUInt32(header, 48, (uint)directoryStart);
        WriteUInt32(header, 52, 0);                // transaction signature
        WriteUInt32(header, 56, MiniStreamCutoff);
        WriteUInt32(header, 60, miniFatSectors > 0 ? (uint)miniFatStart : EndOfChain);
        WriteUInt32(header, 64, (uint)miniFatSectors);
        WriteUInt32(header, 68, difatSectors > 0 ? (uint)(dataSectors + fatSectors) : EndOfChain);
        WriteUInt32(header, 72, (uint)difatSectors);

        // The DIFAT array of the header: the first FAT sector ids (at most 109).
        var headerDifat = HeaderDifatCapacity(fatSectors, difatSectors);
        for (var index = 0; index < MaxRegionEntries; index++)
        {
            WriteUInt32(header, 76 + (index * 4), index < headerDifat ? (uint)(dataSectors + index) : FreeSector);
        }

        output.Write(header);

        // 6. The data sectors.
        foreach (var sector in regular)
        {
            output.Write(sector);
        }

        // 7. The mini FAT.
        var miniFatBytes = new byte[miniFatSectors * SectorSize];
        for (var index = 0; index < miniFat.Count; index++)
        {
            WriteUInt32(miniFatBytes, index * 4, miniFat[index]);
        }

        output.Write(miniFatBytes);

        // 8. The directory.
        var directory = new byte[directorySectors * SectorSize];
        WriteDirectoryEntry(directory, 0, "Root Entry", 5, child: streams.Count > 0 ? 1 : -1,
            start: miniStream.Count > 0 ? (uint)chains["\u0001mini"].Start : EndOfChain, size: miniStream.Count);
        for (var index = 0; index < streams.Count; index++)
        {
            var (name, data) = streams[index];
            var entry = index + 1;
            var start = data.Length < MiniStreamCutoff ? (uint)miniStart[name] : (uint)chains[name].Start;
            WriteDirectoryEntry(
                directory,
                entry,
                name,
                2,
                right: index + 1 < streams.Count ? entry + 1 : -1,
                start: data.Length == 0 ? EndOfChain : start,
                size: data.Length);
        }

        output.Write(directory);

        // 9. The FAT and the DIFAT sectors that do not fit into the header.
        var fatBytes = new byte[fatSectors * SectorSize];
        for (var index = 0; index < fat.Length; index++)
        {
            WriteUInt32(fatBytes, index * 4, fat[index]);
        }

        output.Write(fatBytes);

        var extra = fatSectors - HeaderDifatCapacity(fatSectors, difatSectors);
        if (difatSectors > 0)
        {
            var difatBytes = new byte[difatSectors * SectorSize];
            var perSector = SectorSize / 4 - 1;
            for (var index = 0; index < extra; index++)
            {
                var sector = index / perSector;
                var slot = index % perSector;
                WriteUInt32(difatBytes, (sector * SectorSize) + (slot * 4), (uint)(dataSectors + HeaderDifatCapacity(fatSectors, difatSectors) + index));
            }

            for (var sector = 0; sector < difatSectors; sector++)
            {
                var next = sector + 1 < difatSectors ? (uint)(dataSectors + fatSectors + sector + 1) : EndOfChain;
                WriteUInt32(difatBytes, (sector * SectorSize) + (SectorSize - 4), next);
            }

            output.Write(difatBytes);
        }

        output.Flush();
    }

    /// <summary>Reads a stream of a compound file by name; null when the stream does not exist.</summary>
    public static byte[]? Read(ReadOnlySpan<byte> data, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!IsCompoundFile(data) || data.Length < SectorSize)
        {
            throw new DocumentValidationException("The compound file is too short.");
        }

        var fatSectors = (int)ReadUInt32(data, 44);
        var directoryStart = ReadUInt32(data, 48);
        var miniFatStart = ReadUInt32(data, 60);
        var miniFatSectors = (int)ReadUInt32(data, 64);
        var difatStart = ReadUInt32(data, 68);
        var difatSectors = (int)ReadUInt32(data, 72);

        // The FAT sector ids: the header's DIFAT array and the DIFAT sectors.
        var fatIds = new List<uint>(fatSectors);
        for (var index = 0; index < MaxRegionEntries && fatIds.Count < fatSectors; index++)
        {
            var id = ReadUInt32(data, 76 + (index * 4));
            if (id != FreeSector)
            {
                fatIds.Add(id);
            }
        }

        var difat = difatStart;
        for (var index = 0; index < difatSectors && difat != EndOfChain && difat != FreeSector; index++)
        {
            var sector = Sector(data, difat);
            for (var slot = 0; slot < (SectorSize / 4) - 1 && fatIds.Count < fatSectors; slot++)
            {
                var id = ReadUInt32(sector, slot * 4);
                if (id != FreeSector)
                {
                    fatIds.Add(id);
                }
            }

            difat = ReadUInt32(sector, SectorSize - 4);
        }

        var fat = new List<uint>(fatIds.Count * (SectorSize / 4));
        foreach (var id in fatIds)
        {
            var sector = Sector(data, id);
            for (var slot = 0; slot < SectorSize / 4; slot++)
            {
                fat.Add(ReadUInt32(sector, slot * 4));
            }
        }

        var directory = Chain(data, fat, directoryStart, -1);
        var entryCount = directory.Length / DirectoryEntrySize;
        var entry = -1;
        for (var index = 1; index < entryCount; index++)
        {
            if (string.Equals(ReadName(directory, index), name, StringComparison.OrdinalIgnoreCase))
            {
                entry = index;
                break;
            }
        }

        if (entry < 0)
        {
            return null;
        }

        var size = ReadUInt32(directory, (entry * DirectoryEntrySize) + 120);
        var start = ReadUInt32(directory, (entry * DirectoryEntrySize) + 116);
        if (size == 0)
        {
            return [];
        }

        if (size < MiniStreamCutoff)
        {
            var rootStart = ReadUInt32(directory, 116);
            var rootSize = ReadUInt32(directory, 120);
            var miniStream = Chain(data, fat, rootStart, (int)rootSize);
            var miniFatBytes = miniFatSectors > 0 ? Chain(data, fat, miniFatStart, miniFatSectors * SectorSize) : [];
            var miniFat = new List<uint>(miniFatBytes.Length / 4);
            for (var index = 0; index + 4 <= miniFatBytes.Length; index += 4)
            {
                miniFat.Add(ReadUInt32(miniFatBytes, index));
            }

            var result = new byte[size];
            var written = 0;
            var miniSector = start;
            var guard = 0;
            var fits = true;
            while (miniSector != EndOfChain && miniSector != FreeSector && written < result.Length && guard++ < 1_000_000)
            {
                var offset = (int)miniSector * MiniSectorSize;
                var count = Math.Min(MiniSectorSize, result.Length - written);
                if (offset + count > miniStream.Length)
                {
                    fits = false;
                    break;
                }

                Array.Copy(miniStream, offset, result, written, count);
                written += count;
                miniSector = miniSector < miniFat.Count ? miniFat[(int)miniSector] : EndOfChain;
            }

            if (fits && written == result.Length)
            {
                return result;
            }

            // Some producers (olefile-based writers) place a small stream in the regular sectors
            // although the format says it belongs to the mini stream. The mini chain cannot
            // describe a stream here, so the FAT chain is the only possible reading.
            var fallback = Chain(data, fat, start, (int)size);
            return fallback.Length == size
                ? fallback
                : throw new DocumentValidationException("The mini stream of the compound file is truncated.");
        }

        var stream = Chain(data, fat, start, (int)size);
        return stream;
    }

    /// <summary>
    /// the number of FAT sectors and of DIFAT sectors needed so that the FAT and its DIFAT cover
    /// the given data sectors plus themselves.
    /// </summary>
    private static (int Fat, int Difat) FitFat(int sectors)
    {
        var perSector = SectorSize / 4;
        for (var fat = 1; fat < 1_000_000; fat++)
        {
            var difat = fat <= MaxRegionEntries ? 0 : (fat - MaxRegionEntries + perSector - 2) / (perSector - 1);
            if (fat * perSector >= sectors + fat + difat)
            {
                return (fat, difat);
            }
        }

        throw new DocumentValidationException("The compound file would be too large.");
    }

    /// <summary>How many FAT sector ids fit into the header's DIFAT array.</summary>
    private static int HeaderDifatCapacity(int fatSectors, int difatSectors)
        => difatSectors == 0 ? fatSectors : MaxRegionEntries;

    private static List<byte[]> Split(byte[] data)
    {
        var count = (data.Length + SectorSize - 1) / SectorSize;
        var sectors = new List<byte[]>(count);
        for (var index = 0; index < count; index++)
        {
            var sector = new byte[SectorSize];
            var offset = index * SectorSize;
            Array.Copy(data, offset, sector, 0, Math.Min(SectorSize, data.Length - offset));
            sectors.Add(sector);
        }

        return sectors;
    }

    /// <summary>Follows a FAT chain and returns the bytes of the chain.</summary>
    private static byte[] Chain(ReadOnlySpan<byte> data, List<uint> fat, uint start, int size)
    {
        var result = new List<byte>(size > 0 ? size : 0);
        var sector = start;
        var guard = 0;
        while (sector != EndOfChain && sector != FreeSector && guard++ < 10_000_000)
        {
            if (sector >= fat.Count)
            {
                throw new DocumentValidationException("The compound file references a sector outside its FAT.");
            }

            result.AddRange(Sector(data, sector));
            if (size >= 0 && result.Count >= size)
            {
                break;
            }

            sector = fat[(int)sector];
        }

        var bytes = result.ToArray();
        return size >= 0 && bytes.Length > size ? bytes[..size] : bytes;
    }

    private static byte[] Sector(ReadOnlySpan<byte> data, long index)
    {
        var offset = SectorSize + (index * SectorSize);
        if (offset < 0 || offset + SectorSize > data.Length)
        {
            throw new DocumentValidationException("The compound file is truncated.");
        }

        return data.Slice((int)offset, SectorSize).ToArray();
    }

    private static void WriteDirectoryEntry(
        byte[] directory,
        int index,
        string name,
        byte type,
        int child = -1,
        int right = -1,
        int left = -1,
        uint start = EndOfChain,
        long size = 0)
    {
        var offset = index * DirectoryEntrySize;
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        Array.Copy(nameBytes, 0, directory, offset, Math.Min(nameBytes.Length, 62));
        WriteUInt16(directory, offset + 64, (ushort)(nameBytes.Length + 2));
        directory[offset + 66] = type;
        directory[offset + 67] = 1;                 // black
        WriteInt32(directory, offset + 68, left);
        WriteInt32(directory, offset + 72, right);
        WriteInt32(directory, offset + 76, child);
        WriteUInt32(directory, offset + 116, start);
        WriteUInt32(directory, offset + 120, (uint)Math.Min(size, uint.MaxValue));
        WriteUInt32(directory, offset + 124, (uint)(size >> 32));
    }

    private static string ReadName(byte[] directory, int index)
    {
        var offset = index * DirectoryEntrySize;
        var length = ReadUInt16(directory, offset + 64);
        if (length is < 2 or > 64)
        {
            return string.Empty;
        }

        return System.Text.Encoding.Unicode.GetString(directory, offset, length - 2);
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteInt32(byte[] target, int offset, int value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static void WriteUInt32(byte[] target, int offset, uint value)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
}
