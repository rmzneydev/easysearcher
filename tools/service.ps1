# Instala, reinicia o desinstala el servicio de Windows de EasySearcher. Requiere administrador.
#   pwsh -File tools\service.ps1 install   -Path D:\...\EasySearcher.Service.exe
#   pwsh -File tools\service.ps1 restart
#   pwsh -File tools\service.ps1 uninstall
param(
    [Parameter(Mandatory)]
    [ValidateSet('install', 'restart', 'uninstall')]
    [string]$Action,
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$name = 'EasySearcher'

function Remove-ExistingService {
    $existing = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service -Name $name -Force }
        sc.exe delete $name | Out-Null
        # SCM borra el servicio de forma asincrona
        for ($i = 0; $i -lt 20 -and (Get-Service -Name $name -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    }
}

switch ($Action) {
    'install' {
        if (-not $Path -or -not (Test-Path $Path)) { throw "No existe el ejecutable del servicio: $Path" }
        Remove-ExistingService
        New-Service -Name $name `
            -BinaryPathName "`"$((Resolve-Path $Path).Path)`"" `
            -DisplayName 'EasySearcher (indice de archivos)' `
            -Description 'Mantiene en memoria el indice de archivos de las unidades NTFS para la app EasySearcher.' `
            -StartupType Automatic | Out-Null
        # Si el servicio se cae, Windows lo reinicia a los 5 s (dos veces); el contador se reinicia cada dia
        sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000// | Out-Null
        Start-Service -Name $name
        Get-Service -Name $name | Format-Table Name, Status, StartType -AutoSize
    }
    'restart' {
        Restart-Service -Name $name -Force
        Get-Service -Name $name | Format-Table Name, Status, StartType -AutoSize
    }
    'uninstall' {
        Remove-ExistingService
        "Servicio $name eliminado."
    }
}
