# Agrega o quita "Buscar con EasySearcher" del clic derecho del Explorador para el usuario actual
# (no requiere administrador). En Windows 11 aparece en "Mostrar mas opciones".
#   pwsh -File tools\context-menu.ps1 install -Path D:\...\EasySearcher.exe
#   pwsh -File tools\context-menu.ps1 uninstall
param(
    [Parameter(Mandatory)]
    [ValidateSet('install', 'uninstall')]
    [string]$Action,
    [string]$Path
)

$ErrorActionPreference = 'Stop'

# Sobre una carpeta, en el fondo de una carpeta abierta y sobre una unidad
$targets = 'Directory\shell', 'Directory\Background\shell', 'Drive\shell'
$classes = 'HKCU:\Software\Classes'
$keyName = 'EasySearcher'

switch ($Action) {
    'install' {
        if (-not $Path -or -not (Test-Path $Path)) { throw "No existe el ejecutable: $Path" }
        $exe = (Resolve-Path $Path).Path
        foreach ($target in $targets) {
            $key = "$classes\$target\$keyName"
            New-Item -Path "$key\command" -Force | Out-Null
            Set-ItemProperty -Path $key -Name '(default)' -Value 'Buscar con EasySearcher'
            Set-ItemProperty -Path $key -Name 'Icon' -Value "`"$exe`",0"
            # %V es la carpeta (o la unidad, que llega como D:\ y la app corrige)
            Set-ItemProperty -Path "$key\command" -Name '(default)' -Value "`"$exe`" `"%V`""
        }
        "Menu contextual instalado para: $exe"
    }
    'uninstall' {
        foreach ($target in $targets) {
            Remove-Item -Path "$classes\$target\$keyName" -Recurse -Force -ErrorAction SilentlyContinue
        }
        'Menu contextual eliminado.'
    }
}
