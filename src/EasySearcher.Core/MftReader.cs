using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using EasySearcher.Core.Interop;

namespace EasySearcher.Core;

/// <summary>
/// Enumera todas las entradas de la MFT de un volumen NTFS con FSCTL_ENUM_USN_DATA.
/// Requiere permisos de administrador.
/// </summary>
internal static class MftReader
{
    // Los 16 bits altos del FRN son el numero de secuencia; para enlazar padre/hijo basta el indice
    public const ulong FrnMask = 0x0000_FFFF_FFFF_FFFF;

    const int BufferSize = 1 << 20;

    public delegate void RecordHandler(ulong frn, ulong parentFrn, uint attributes, ReadOnlySpan<char> name);

    public static void Enumerate(char drive, RecordHandler onRecord)
    {
        using var volume = NativeMethods.CreateFile(
            $@"\\.\{drive}:", NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

        if (volume.IsInvalid)
            throw VolumeError(drive, Marshal.GetLastPInvokeError());

        // MFT_ENUM_DATA_V0: StartFileReferenceNumber, LowUsn, HighUsn
        var input = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);
        var buffer = new byte[BufferSize];

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

            // Los primeros 8 bytes son el FRN desde donde continuar; luego vienen USN_RECORD_V2
            var data = buffer.AsSpan(0, returned);
            int offset = 8;
            while (offset < data.Length)
            {
                var record = data[offset..];
                int length = BinaryPrimitives.ReadInt32LittleEndian(record);
                if (length == 0) break;

                if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) == 2)
                {
                    ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]) & FrnMask;
                    ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]) & FrnMask;
                    uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
                    int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
                    int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
                    var name = MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength));
                    onRecord(frn, parent, attributes, name);
                }
                offset += length;
            }
            data[..8].CopyTo(input);
        }
    }

    static Exception VolumeError(char drive, int error) => error == NativeMethods.ERROR_ACCESS_DENIED
        ? new UnauthorizedAccessException($"Se requieren permisos de administrador para leer la MFT de {drive}:")
        : new Win32Exception(error, $"No se pudo leer la MFT de {drive}: ({new Win32Exception(error).Message})");
}
