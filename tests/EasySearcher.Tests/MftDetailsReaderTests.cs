using System.Buffers.Binary;
using EasySearcher.Core;

namespace EasySearcher.Tests;

/// <summary>
/// Registros NTFS sinteticos con el mismo formato que en disco: cabecera FILE, update sequence (fixups)
/// y atributos $STANDARD_INFORMATION y $DATA residentes o no residentes.
/// </summary>
public class MftDetailsReaderTests
{
    const int RecordSize = 1024;
    static readonly DateTime Modified = new(2024, 5, 17, 10, 30, 15, DateTimeKind.Utc);

    [Fact]
    public void Reads_size_and_date_of_a_file_with_non_resident_data()
    {
        var record = new RecordBuilder(inUse: true).StandardInformation(Modified).NonResidentData(1_234_567_890).Build();
        Assert.True(MftDetailsReader.TryParseRecord(record, out var details));
        Assert.Equal(0, details.BaseRecord);
        Assert.Equal(1_234_567_890, details.Size);
        Assert.Equal(Modified.Ticks, details.Modified);
    }

    [Fact]
    public void Small_files_keep_their_data_inside_the_record()
    {
        var record = new RecordBuilder(inUse: true).StandardInformation(Modified).ResidentData(300).Build();
        Assert.True(MftDetailsReader.TryParseRecord(record, out var details));
        Assert.Equal(300, details.Size);
    }

    [Fact]
    public void Directories_have_no_size()
    {
        var record = new RecordBuilder(inUse: true, directory: true).StandardInformation(Modified).Build();
        Assert.True(MftDetailsReader.TryParseRecord(record, out var details));
        Assert.Equal(-1, details.Size);
        Assert.Equal(Modified.Ticks, details.Modified);
    }

    [Fact]
    public void Named_streams_are_not_the_file_size()
    {
        // Un flujo alternativo (p. ej. Zone.Identifier) no es el tamano del archivo
        var record = new RecordBuilder(inUse: true).StandardInformation(Modified).ResidentData(26, name: "Zone.Identifier").Build();
        Assert.True(MftDetailsReader.TryParseRecord(record, out var details));
        Assert.Equal(MftDetailsReader.Unknown, details.Size);
    }

    [Fact]
    public void Extension_records_report_their_base_record()
    {
        var record = new RecordBuilder(inUse: true, baseRecord: 4242).NonResidentData(99).Build();
        Assert.True(MftDetailsReader.TryParseRecord(record, out var details));
        Assert.Equal(4242, details.BaseRecord);
        Assert.Equal(99, details.Size);
        Assert.Equal(MftDetailsReader.Unknown, details.Modified);
    }

    [Fact]
    public void Free_records_are_skipped()
    {
        var record = new RecordBuilder(inUse: false).StandardInformation(Modified).Build();
        Assert.False(MftDetailsReader.TryParseRecord(record, out _));
    }

    [Fact]
    public void Torn_records_fail_the_fixup_check()
    {
        var record = new RecordBuilder(inUse: true).StandardInformation(Modified).Build();
        // Simula una escritura incompleta: el final del segundo tramo de 512 no tiene el numero de secuencia
        record[1022] ^= 0xFF;
        Assert.False(MftDetailsReader.TryParseRecord(record, out _));
    }

    [Fact]
    public void Fixups_restore_the_original_sector_end_bytes()
    {
        // $DATA en la posicion 456: su tamano (bytes 504-511) cae sobre el final del primer tramo de 512,
        // que NTFS reemplaza por el numero de secuencia
        var record = new RecordBuilder(inUse: true).StandardInformation(Modified)
            .FillUntil(456).NonResidentData(0x0102030405060708).Build();
        Assert.True(MftDetailsReader.TryParseRecord(record, out var details));
        Assert.Equal(0x0102030405060708, details.Size);
    }

    [Fact]
    public void Decodes_runlists_with_relative_and_negative_offsets()
    {
        byte[] runlist =
        [
            0x21, 0x10, 0x00, 0x01,       // 16 clusters en el LCN 256
            0x31, 0x08, 0x00, 0x10, 0x00, // 8 clusters, +4096 -> 4352
            0x11, 0x04, 0xF0,             // 4 clusters, -16 -> 4336
            0x01, 0x02,                   // 2 clusters dispersos
            0x00,
        ];
        var runs = MftDetailsReader.DecodeRuns(runlist);
        Assert.Equal(
            [new(256, 16), new(4352, 8), new(4336, 4), new(-1, 2)],
            runs);
    }

    /// <summary>Arma un registro FILE de 1 KB con fixups validos.</summary>
    sealed class RecordBuilder
    {
        const int UsaOffset = 0x30;
        const int UsaCount = RecordSize / 512 + 1;
        const int FirstAttribute = 0x38;

        readonly byte[] _record = new byte[RecordSize];
        int _next = FirstAttribute;

        public RecordBuilder(bool inUse, bool directory = false, long baseRecord = 0)
        {
            "FILE"u8.CopyTo(_record);
            BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(4), UsaOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(6), UsaCount);
            BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(0x14), FirstAttribute);
            BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(0x16), (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
            BinaryPrimitives.WriteUInt64LittleEndian(_record.AsSpan(0x20), (ulong)baseRecord);
        }

        public RecordBuilder StandardInformation(DateTime modifiedUtc)
        {
            var content = new byte[72];
            BinaryPrimitives.WriteInt64LittleEndian(content.AsSpan(8), modifiedUtc.ToFileTimeUtc());
            return Resident(0x10, content, "");
        }

        public RecordBuilder ResidentData(int size, string name = "") => Resident(0x80, new byte[size], name);

        /// <summary>Atributo de relleno ($OBJECT_ID) para que el siguiente empiece en <paramref name="offset"/>.</summary>
        public RecordBuilder FillUntil(int offset) => Resident(0x40, new byte[offset - _next - 0x18], "");

        public RecordBuilder NonResidentData(long size)
        {
            var attribute = _record.AsSpan(_next, 0x48);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], 0x48);
            attribute[8] = 1; // no residente
            BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x20..], 0x40);                // runlist
            BinaryPrimitives.WriteInt64LittleEndian(attribute[0x28..], (size + 4095) / 4096 * 4096);
            BinaryPrimitives.WriteInt64LittleEndian(attribute[0x30..], size);                  // tamano real
            BinaryPrimitives.WriteInt64LittleEndian(attribute[0x38..], size);
            _next += 0x48;
            return this;
        }

        RecordBuilder Resident(uint type, byte[] content, string name)
        {
            int nameOffset = 0x18;
            int contentOffset = nameOffset + (name.Length * 2 + 7) / 8 * 8;
            int length = (contentOffset + content.Length + 7) / 8 * 8;
            var attribute = _record.AsSpan(_next, length);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], (uint)length);
            attribute[9] = (byte)name.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x0A..], (ushort)nameOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x10..], (uint)content.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x14..], (ushort)contentOffset);
            for (int i = 0; i < name.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(attribute[(nameOffset + i * 2)..], name[i]);
            content.CopyTo(attribute[contentOffset..]);
            _next += length;
            return this;
        }

        public byte[] Build()
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_record.AsSpan(_next), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(_record.AsSpan(0x18), (uint)(_next + 8));

            // Fixups: los ultimos 2 bytes de cada tramo de 512 se guardan en el arreglo y se reemplazan por el USN
            const ushort usn = 0x0007;
            BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(UsaOffset), usn);
            for (int i = 1; i < UsaCount; i++)
            {
                int end = i * 512 - 2;
                _record.AsSpan(end, 2).CopyTo(_record.AsSpan(UsaOffset + i * 2));
                BinaryPrimitives.WriteUInt16LittleEndian(_record.AsSpan(end), usn);
            }
            return _record;
        }
    }
}
