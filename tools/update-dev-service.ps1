# Recompila el servicio en artifacts\service y lo reinicia, para probar cambios del motor (Core / Service).
# Requiere administrador: el servicio en ejecucion bloquea sus archivos y hay que detenerlo.
#   pwsh -File tools\update-dev-service.ps1
# La primera vez el servicio se registra con: tools\service.ps1 install -Path artifacts\service\EasySearcher.Service.exe
param(
    [string]$Dotnet = 'D:\Programs\dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\service'

Stop-Service -Name EasySearcher
try {
    & $Dotnet publish (Join-Path $root 'src\EasySearcher.Service') -c Release -r win-x64 --self-contained -o $output -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fallo la publicacion del servicio' }
}
finally {
    # Aunque falle la compilacion, se vuelve a iniciar el servicio que habia
    Start-Service -Name EasySearcher
}
Get-Service -Name EasySearcher | Format-Table Name, Status, StartType -AutoSize
