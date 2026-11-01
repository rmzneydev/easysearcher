using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EasySearcher.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace EasySearcher.Core;

/// <summary>
/// Lee la tabla $MFT cruda del volumen para obtener el tamano ($DATA) y la fecha de modificacion
/// ($STANDARD_INFORMATION) de todos los registros de una vez. La enumeracion con FSCTL_ENUM_USN_DATA
/// no los trae, y pedirlos archivo por archivo es lento para cientos de miles de entradas.
/// Requiere administrador.
/// </summary>
internal static class MftDetailsReader
{
    public const long Unknown = long.MinValue;

    const uint FileSignature = 0x454C4946; // "FILE"
    const uint AttrStandardInformation = 0x10;
    const uint AttrData = 0x80;
    const uint AttrEnd = 0xFFFFFFFF;
    const ushort RecordInUse = 0x01;
    const ushort RecordIsDirectory = 0x02;
    // NTFS protege cada tramo de 512 bytes de un registro con la "update sequence", sin importar el sector fisico
    const int FixupStride = 512;
    const int ChunkSize = 4 << 20;

    public delegate void DetailsHandler(int frn, long size, long modifiedTicksUtc);

    internal readonly record struct VolumeData(int BytesPerCluster, int BytesPerRecord, long MftValidDataLength, long MftStartLcn);

    /// <param name="Lcn">Primer cluster en el disco; -1 si el tramo es disperso (sin datos).</param>
    internal readonly record struct Run(long Lcn, long Clusters);

    /// <param name="BaseRecord">0 si es un registro base; si no, el registro base al que pertenece.</param>
    /// <param name="Size">Bytes del flujo de datos sin nombre, -1 en carpetas, <see cref="Unknown"/> si no esta en este registro.</param>
    /// <param name="Modified">Ticks UTC de la ultima modificacion, o <see cref="Unknown"/>.</param>
    internal readonly record struct RecordDetails(long BaseRecord, long Size, long Modified);

    /// <summary>Recorre la $MFT y llama a <paramref name="onRecord"/> por cada registro en uso.</summary>
    public static void Read(SafeFileHandle volume, char drive, DetailsHandler onRecord, CancellationToken cancellationToken)
    {
        var data = QueryVolumeData(volume, drive);
        // Con clusters mas chicos que un registro, un registro podria quedar repartido entre tramos
        // no contiguos. Es muy raro en NTFS moderno; en ese caso se sigue consultando archivo por archivo.
        if (data.BytesPerCluster < data.BytesPerRecord) return;

        // El registro 0 es el propio $MFT: su $DATA indica en que clusters esta la tabla
        var first = new byte[data.BytesPerCluster];
        ReadAt(volume, data.MftStartLcn * data.BytesPerCluster, first);
        var mftRecord = first.AsSpan(0, data.BytesPerRecord);
        if (!ApplyFixups(mftRecord)) throw new InvalidDataException($"El registro de $MFT de {drive}: no es valido.");
        var runs = GetUnnamedDataRuns(mftRecord);

        long totalRecords = data.MftValidDataLength / data.BytesPerRecord;
        long runFirstRecord = 0;
        var buffer = new byte[ChunkSize];
        foreach (var run in runs)
        {
            long runBytes = run.Clusters * data.BytesPerCluster;
            if (run.Lcn >= 0)
            {
                for (long done = 0; done < runBytes; done += ChunkSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long chunkFirstRecord = runFirstRecord + done / data.BytesPerRecord;
                    if (chunkFirstRecord >= totalRecords) return;

                    var chunk = buffer.AsSpan(0, (int)Math.Min(ChunkSize, runBytes - done));
                    ReadAt(volume, run.Lcn * data.BytesPerCluster + done, chunk);

                    int count = chunk.Length / data.BytesPerRecord;
                    for (int k = 0; k < count && chunkFirstRecord + k < totalRecords; k++)
                    {
                        if (!TryParseRecord(chunk.Slice(k * data.BytesPerRecord, data.BytesPerRecord), out var details)) continue;
                        long frn = details.BaseRecord != 0 ? details.BaseRecord : chunkFirstRecord + k;
                        if (frn <= int.MaxValue) onRecord((int)frn, details.Size, details.Modified);
                    }
                }
            }
            runFirstRecord += runBytes / data.BytesPerRecord;
        }
    }

    /// <summary>Aplica las correcciones de la update sequence y extrae tamano y fecha. False si no es un registro en uso.</summary>
    internal static bool TryParseRecord(Span<byte> record, out RecordDetails details)
    {
        details = default;
        if (record.Length < 0x30 || BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature) return false;
        if (!ApplyFixups(record)) return false;

        ushort flags = U16(record, 0x16);
        if ((flags & RecordInUse) == 0) return false;

        long baseRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) & MftReader.FrnMask);
        bool isDirectory = (flags & RecordIsDirectory) != 0;
        long size = isDirectory ? -1 : Unknown;
        long modified = Unknown;

        foreach (var attribute in Attributes(record))
        {
            uint type = U32(attribute, 0);
            bool nonResident = attribute[8] != 0;
            byte nameLength = attribute[9];

            if (type == AttrStandardInformation && !nonResident)
            {
                int contentOffset = U16(attribute, 0x14);
                if (U32(attribute, 0x10) >= 16 && contentOffset + 16 <= attribute.Length)
                {
                    long fileTime = BinaryPrimitives.ReadInt64LittleEndian(attribute[(contentOffset + 8)..]);
                    if (fileTime > 0 && fileTime < MaxFileTime) modified = DateTime.FromFileTimeUtc(fileTime).Ticks;
                }
            }
            else if (type == AttrData && nameLength == 0 && !isDirectory)
            {
                if (!nonResident)
                    size = U32(attribute, 0x10);
                else if (attribute.Length >= 0x40 && BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]) == 0)
                    // Solo el primer tramo del atributo (LowestVcn = 0) trae el tamano real
                    size = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
            }
        }

        details = new RecordDetails(baseRecord, size, modified);
        return true;
    }

    static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>
    /// Restaura los ultimos 2 bytes de cada tramo de 512, que NTFS reemplaza por el numero de secuencia
    /// para detectar escrituras incompletas. False si no coinciden (registro danado).
    /// </summary>
    internal static bool ApplyFixups(Span<byte> record)
    {
        int usaOffset = U16(record, 4);
        int usaCount = U16(record, 6);
        if (usaCount == 0 || usaOffset + usaCount * 2 > record.Length) return false;

        byte usn0 = record[usaOffset], usn1 = record[usaOffset + 1];
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * FixupStride - 2;
            if (end + 2 > record.Length) return false;
            if (record[end] != usn0 || record[end + 1] != usn1) return false;
            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>Tramos en disco del flujo de datos sin nombre de un registro (el del $MFT).</summary>
    static List<Run> GetUnnamedDataRuns(Span<byte> record)
    {
        foreach (var attribute in Attributes(record))
        {
            if (U32(attribute, 0) == AttrData && attribute[8] != 0 && attribute[9] == 0 &&
                BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]) == 0)
            {
                int runlistOffset = U16(attribute, 0x20);
                return DecodeRuns(attribute[runlistOffset..]);
            }
        }
        throw new InvalidDataException("El $MFT no tiene flujo de datos.");
    }

    /// <summary>
    /// Decodifica una runlist de NTFS: cada tramo empieza con un byte cuyos 4 bits bajos dicen cuantos bytes
    /// ocupa la longitud y los 4 altos cuantos ocupa el desplazamiento (con signo, relativo al tramo anterior).
    /// </summary>
    internal static List<Run> DecodeRuns(ReadOnlySpan<byte> runlist)
    {
        var runs = new List<Run>();
        long lcn = 0;
        int p = 0;
        while (p < runlist.Length && runlist[p] != 0)
        {
            int header = runlist[p++];
            int lengthBytes = header & 0x0F, offsetBytes = header >> 4;
            if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || p + lengthBytes + offsetBytes > runlist.Length) break;

            long length = 0;
            for (int b = 0; b < lengthBytes; b++) length |= (long)runlist[p + b] << (8 * b);
            p += lengthBytes;

            if (offsetBytes == 0)
            {
                runs.Add(new Run(-1, length));
                continue;
            }

            long delta = 0;
            for (int b = 0; b < offsetBytes; b++) delta |= (long)runlist[p + b] << (8 * b);
            if (offsetBytes < 8 && (runlist[p + offsetBytes - 1] & 0x80) != 0)
                delta |= -1L << (8 * offsetBytes);
            p += offsetBytes;

            lcn += delta;
            runs.Add(new Run(lcn, length));
        }
        return runs;
    }

    /// <summary>Atributos de un registro (ya corregido), cada uno como un tramo del registro.</summary>
    static AttributeEnumerator Attributes(Span<byte> record) => new(record);

    ref struct AttributeEnumerator(Span<byte> record)
    {
        readonly Span<byte> _record = record;
        readonly int _used = (int)Math.Min(U32(record, 0x18), (uint)record.Length);
        int _next = U16(record, 0x14);

        public Span<byte> Current { get; private set; }

        public readonly AttributeEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_next + 16 > _used || U32(_record, _next) == AttrEnd) return false;
            int length = (int)U32(_record, _next + 4);
            if (length < 16 || _next + length > _used) return false;
            Current = _record.Slice(_next, length);
            _next += length;
            return true;
        }
    }

    static VolumeData QueryVolumeData(SafeFileHandle volume, char drive)
    {
        // NTFS_VOLUME_DATA_BUFFER
        var output = new byte[128];
        if (!NativeMethods.DeviceIoControl(volume, NativeMethods.FSCTL_GET_NTFS_VOLUME_DATA,
                null, 0, output, output.Length, out _, IntPtr.Zero))
            throw new IOException($"No se pudieron leer los datos NTFS de {drive}: (error {Marshal.GetLastPInvokeError()})");

        return new VolumeData(
            BytesPerCluster: BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(44)),
            BytesPerRecord: BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(48)),
            MftValidDataLength: BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(56)),
            MftStartLcn: BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(64)));
    }

    static void ReadAt(SafeFileHandle volume, long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(volume, buffer[total..], offset + total);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    static ushort U16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(s[offset..]);

    static uint U32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s[offset..]);
}
