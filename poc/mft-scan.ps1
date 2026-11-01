# Prueba de concepto: enumerar la MFT de NTFS con FSCTL_ENUM_USN_DATA y medir tiempos.
# Requiere PowerShell 7 y ejecutarse como administrador.
#   pwsh -File mft-scan.ps1 -Drives C,D -Query json [-Max 20]
param(
    [string[]]$Drives = @('C'),
    [string]$Query = 'json',
    [int]$Max = 0,  # rutas a mostrar; 0 = todas
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
# Con -File, "C,D,E" llega como un solo string
$Drives = $Drives -split ',' | ForEach-Object { $_.Trim().TrimEnd(':') } | Where-Object { $_ }

$src = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public class MftIndex
{
    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint FSCTL_ENUM_USN_DATA = 0x000900b3;
    const int ERROR_HANDLE_EOF = 38;
    const ulong FRN_MASK = 0x0000FFFFFFFFFFFF; // descarta el numero de secuencia
    const ulong ROOT_FRN = 5;
    const uint ATTR_DIRECTORY = 0x10;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize,
        byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    public char Drive;
    public int Count;
    public int Directories;
    public double EnumMs, IndexMs;
    public long ManagedBytes;

    List<ulong> frn = new List<ulong>(1 << 20);
    List<ulong> parent = new List<ulong>(1 << 20);
    List<uint> attrs = new List<uint>(1 << 20);
    List<string> names = new List<string>(1 << 20);
    Dictionary<ulong, int> byFrn;

    public static MftIndex Scan(char drive)
    {
        var idx = new MftIndex { Drive = char.ToUpperInvariant(drive) };
        long before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();

        using (var h = CreateFileW(@"\\.\" + idx.Drive + ":", GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

            // MFT_ENUM_DATA_V0: StartFileReferenceNumber, LowUsn, HighUsn
            var input = new byte[24];
            BitConverter.GetBytes(long.MaxValue).CopyTo(input, 16);
            var buf = new byte[1 << 20];
            int ret;

            while (true)
            {
                if (!DeviceIoControl(h, FSCTL_ENUM_USN_DATA, input, input.Length, buf, buf.Length, out ret, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_HANDLE_EOF) break;
                    throw new Win32Exception(err);
                }
                if (ret <= 8) break;

                // Los primeros 8 bytes son el FRN desde donde continuar; luego vienen USN_RECORD_V2
                int off = 8;
                while (off < ret)
                {
                    int len = BitConverter.ToInt32(buf, off);
                    if (len == 0) break;
                    if (BitConverter.ToUInt16(buf, off + 4) == 2)
                    {
                        idx.frn.Add(BitConverter.ToUInt64(buf, off + 8) & FRN_MASK);
                        idx.parent.Add(BitConverter.ToUInt64(buf, off + 16) & FRN_MASK);
                        uint a = BitConverter.ToUInt32(buf, off + 52);
                        idx.attrs.Add(a);
                        if ((a & ATTR_DIRECTORY) != 0) idx.Directories++;
                        int nameLen = BitConverter.ToUInt16(buf, off + 56);
                        int nameOff = BitConverter.ToUInt16(buf, off + 58);
                        idx.names.Add(Encoding.Unicode.GetString(buf, off + nameOff, nameLen));
                    }
                    off += len;
                }
                Buffer.BlockCopy(buf, 0, input, 0, 8);
            }
        }
        idx.EnumMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        idx.byFrn = new Dictionary<ulong, int>(idx.frn.Count);
        for (int i = 0; i < idx.frn.Count; i++) idx.byFrn[idx.frn[i]] = i;
        idx.IndexMs = sw.Elapsed.TotalMilliseconds;

        idx.Count = idx.frn.Count;
        idx.ManagedBytes = GC.GetTotalMemory(true) - before;
        return idx;
    }

    public string GetPath(int i)
    {
        var parts = new List<string> { names[i] };
        ulong p = parent[i];
        for (int guard = 0; p != ROOT_FRN && guard < 512; guard++)
        {
            int j;
            if (!byFrn.TryGetValue(p, out j)) { parts.Add("?"); break; }
            parts.Add(names[j]);
            p = parent[j];
        }
        parts.Reverse();
        return Drive + @":\" + string.Join(@"\", parts);
    }

    // Busqueda por subcadena sin distinguir mayusculas; devuelve el total y los primeros 'max' indices
    public int Search(string q, int max, List<int> firsts)
    {
        int c = 0;
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (c < max) firsts.Add(i);
                c++;
            }
        }
        return c;
    }
}
'@

$log = Join-Path $PSScriptRoot 'mft-scan-result.txt'
Start-Transcript -Path $log -Force | Out-Null
try {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Warning 'Se necesita ejecutar como administrador para abrir el volumen.'
    }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    Add-Type -TypeDefinition $src
    "Compilacion C#: {0:N0} ms" -f $sw.Elapsed.TotalMilliseconds

    foreach ($d in $Drives) {
        ''
        "=== Unidad ${d}: ==="
        try {
            $idx = [MftIndex]::Scan($d[0])
        } catch {
            "  ERROR: $($_.Exception.InnerException.Message ?? $_.Exception.Message)"
            continue
        }
        "  Entradas leidas:    {0:N0} ({1:N0} carpetas, {2:N0} archivos)" -f $idx.Count, $idx.Directories, ($idx.Count - $idx.Directories)
        "  Lectura MFT:        {0:N0} ms" -f $idx.EnumMs
        "  Indice FRN->entrada:{0,6:N0} ms" -f $idx.IndexMs
        "  Memoria (managed):  {0:N0} MB" -f ($idx.ManagedBytes / 1MB)

        $firsts = [Collections.Generic.List[int]]::new()
        $sw.Restart()
        $total = $idx.Search($Query, ($Max -gt 0 ? $Max : [int]::MaxValue), $firsts)
        $searchMs = $sw.Elapsed.TotalMilliseconds
        "  Busqueda '{0}':     {1:N0} coincidencias en {2:N1} ms" -f $Query, $total, $searchMs
        foreach ($i in $firsts) { "    " + $idx.GetPath($i) }
    }
} finally {
    Stop-Transcript | Out-Null
    if (-not $NoPause) { Read-Host "`nEnter para cerrar" | Out-Null }
}
