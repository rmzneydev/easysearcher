using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using EasySearcher.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace EasySearcher.Core;

/// <summary>Estado del USN Journal de un volumen.</summary>
internal readonly record struct JournalInfo(ulong Id, long NextUsn);

/// <summary>Un cambio leido del USN Journal.</summary>
internal readonly record struct JournalRecord(ulong Frn, ulong Parent, uint Attributes, uint Reason, string Name);

/// <summary>
/// Acceso de bajo nivel a NTFS: enumera la MFT con FSCTL_ENUM_USN_DATA y lee cambios del USN Journal.
/// Requiere permisos de administrador.
/// </summary>
internal static class MftReader
{
    // Los 16 bits altos del FRN son el numero de secuencia; para enlazar padre/hijo basta el indice
    public const ulong FrnMask = 0x0000_FFFF_FFFF_FFFF;

    public const uint ReasonFileCreate = 0x0000_0100;
    public const uint ReasonFileDelete = 0x0000_0200;
    public const uint ReasonRenameOldName = 0x0000_1000;
    public const uint ReasonRenameNewName = 0x0000_2000;
    public const uint ReasonBasicInfoChange = 0x0000_8000;

    const uint JournalReasonMask = ReasonFileCreate | ReasonFileDelete | ReasonRenameOldName | ReasonRenameNewName | ReasonBasicInfoChange;

    const int EnumBufferSize = 1 << 20;
    const int JournalBufferSize = 1 << 16;

    public delegate void RecordHandler(ulong frn, ulong parentFrn, uint attributes, uint reason, ReadOnlySpan<char> name);

    public static SafeFileHandle OpenVolume(char drive)
    {
        var volume = NativeMethods.CreateFile(
            $@"\\.\{drive}:", NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (volume.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            volume.Dispose();
            throw VolumeError(drive, error);
        }
        return volume;
    }

    public static void Enumerate(SafeFileHandle volume, char drive, RecordHandler onRecord)
    {
        // MFT_ENUM_DATA_V0: StartFileReferenceNumber, LowUsn, HighUsn
        var input = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);
        var buffer = new byte[EnumBufferSize];

        while (true)
        {
            if (!NativeMethods.DeviceIoControl(volume, NativeMethods.FSCTL_ENUM_USN_DATA,
                    input, input.Length, buffer, buffer.Length, out int returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == NativeMethods.ERROR_HANDLE_EOF) return;
                throw VolumeError(drive, error);
            }
            if (returned <= 8) return;

            // Los primeros 8 bytes son el FRN desde donde continuar
            ParseRecords(buffer.AsSpan(8, returned - 8), onRecord);
            buffer.AsSpan(0, 8).CopyTo(input);
        }
    }

    /// <summary>Devuelve null si el volumen no tiene el journal activo.</summary>
    public static JournalInfo? QueryJournal(SafeFileHandle volume, char drive)
    {
        var output = new byte[80]; // USN_JOURNAL_DATA_V0/V1
        if (!NativeMethods.DeviceIoControl(volume, NativeMethods.FSCTL_QUERY_USN_JOURNAL,
                null, 0, output, output.Length, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is NativeMethods.ERROR_JOURNAL_NOT_ACTIVE or NativeMethods.ERROR_JOURNAL_DELETE_IN_PROGRESS) return null;
            throw VolumeError(drive, error);
        }
        return new JournalInfo(
            BinaryPrimitives.ReadUInt64LittleEndian(output),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16)));
    }

    /// <summary>
    /// Lee los cambios desde <paramref name="nextUsn"/> y lo avanza. Devuelve false si el journal se
    /// reinicio o ya no tiene esas entradas, en cuyo caso hay que volver a enumerar la MFT.
    /// </summary>
    public static bool ReadJournal(SafeFileHandle volume, char drive, ulong journalId, ref long nextUsn, List<JournalRecord> records)
    {
        // READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID
        var input = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), JournalReasonMask);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        var buffer = new byte[JournalBufferSize];

        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, nextUsn);
            if (!NativeMethods.DeviceIoControl(volume, NativeMethods.FSCTL_READ_USN_JOURNAL,
                    input, input.Length, buffer, buffer.Length, out int returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is NativeMethods.ERROR_JOURNAL_ENTRY_DELETED or NativeMethods.ERROR_JOURNAL_DELETE_IN_PROGRESS
                    or NativeMethods.ERROR_JOURNAL_NOT_ACTIVE or NativeMethods.ERROR_INVALID_PARAMETER)
                    return false;
                throw VolumeError(drive, error);
            }

            // Los primeros 8 bytes son el USN desde donde seguir leyendo
            nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            if (returned <= 8) return true;

            ParseRecords(buffer.AsSpan(8, returned - 8), (frn, parent, attributes, reason, name) =>
                records.Add(new JournalRecord(frn, parent, attributes, reason, new string(name))));
        }
    }

    /// <summary>Recorre una secuencia de USN_RECORD_V2.</summary>
    static void ParseRecords(ReadOnlySpan<byte> data, RecordHandler onRecord)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            var record = data[offset..];
            int length = BinaryPrimitives.ReadInt32LittleEndian(record);
            if (length == 0) break;

            if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) == 2)
            {
                ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]) & FrnMask;
                ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]) & FrnMask;
                uint reason = BinaryPrimitives.ReadUInt32LittleEndian(record[40..]);
                uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
                int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
                var name = MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength));
                onRecord(frn, parent, attributes, reason, name);
            }
            offset += length;
        }
    }

    static Exception VolumeError(char drive, int error) => error == NativeMethods.ERROR_ACCESS_DENIED
        ? new UnauthorizedAccessException($"Se requieren permisos de administrador para leer la MFT de {drive}:")
        : new Win32Exception(error, $"No se pudo leer la MFT de {drive}: ({new Win32Exception(error).Message})");
}
