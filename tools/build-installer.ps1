# Publica la app y el servicio (autocontenidos, con .NET incluido) y compila el instalador con Inno Setup.
#   pwsh -File tools\build-installer.ps1
# Resultado: artifacts\installer\EasySearcherSetup-<version>.exe
param(
    [string]$Dotnet = 'D:\Programs\dotnet\dotnet.exe',
    [string]$Iscc = 'D:\Programs\InnoSetup\ISCC.exe'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
Write-Host "EasySearcher $version"

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# Los dos en la misma carpeta: comparten una sola copia del runtime de .NET
foreach ($project in 'EasySearcher.App', 'EasySearcher.Service') {
    Write-Host "Publicando $project..."
    & $Dotnet publish (Join-Path $root "src\$project") -c Release -r win-x64 --self-contained -o $publish -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion de $project" }
}
# Archivos de desarrollo que no hacen falta en la instalacion
Get-ChildItem $publish -Filter *.pdb | Remove-Item

$size = (Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Publicado: {0:N0} MB" -f $size)

Write-Host 'Compilando instalador...'
& $Iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\EasySearcher.iss')
if ($LASTEXITCODE -ne 0) { throw 'Fallo la compilacion del instalador' }

$setup = Get-Item (Join-Path $root "artifacts\installer\EasySearcherSetup-$version.exe")
Write-Host ("Instalador: {0} ({1:N0} MB)" -f $setup.FullName, ($setup.Length / 1MB))
