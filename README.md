# EasySearcher

Buscador de archivos local para Windows, mucho más rápido que el del Explorador. En lugar de recorrer
carpetas, lee directamente la tabla de archivos de NTFS (MFT), mantiene el índice en memoria y lo
actualiza en vivo con el USN Journal. Encuentra cualquier archivo o carpeta mientras escribes.

## Funciones

- Búsqueda instantánea sobre todas las unidades NTFS: unas 790.000 entradas indexadas en ~2 s y
  cada búsqueda en ~20–30 ms.
- Índice actualizado en vivo: lo que se crea, renombra, mueve o borra aparece en menos de un segundo.
- Carpeta raíz para limitar la búsqueda, también desde el clic derecho del Explorador
  ("Buscar con EasySearcher").
- Filtros por tipo (archivos, carpetas, imágenes, videos, música, documentos), por fecha de
  modificación (hoy, esta semana, rango personalizado…) y por tamaño (mayor o menor que una cifra en
  KB, MB o GB, o vacíos). Con un filtro de fecha o tamaño se puede buscar sin escribir texto.
- Tamaño y fecha de todas las entradas leídos directamente de la `$MFT` (~800.000 en ~4,5 s, en
  segundo plano): ordenar o filtrar por ellos es instantáneo.
- Vistas de detalles (nombre, carpeta, tamaño, fecha, con orden por columna) y de iconos con
  miniaturas, las mismas del Explorador.
- Arrastrar y soltar resultados a otras aplicaciones, abrir, abrir la carpeta contenedora y copiar
  la ruta.
- Oculta por defecto los metarchivos de NTFS y los archivos protegidos del sistema.

## Sintaxis de búsqueda

Sin distinguir mayúsculas:

| Escribes | Encuentra |
|---|---|
| `linkin mp3` | nombres que contienen las dos palabras, en cualquier orden |
| `"linkin park"` | el texto exacto, con el espacio |
| `*.mp3`, `foto_??.jpg` | comodines contra el nombre completo: `*` cualquier texto, `?` un carácter |
| `!temp`, `!*.tmp` | excluye lo que coincide |

Las formas se combinan: `linkin *.mp3 !live`.

## Requisitos

- Windows 10 u 11 de 64 bits. Solo se ha probado en Windows 11 23H2.
- Indexa unidades **NTFS fijas**. No indexa USB ni discos FAT32/exFAT, unidades de red ni Dev Drive (ReFS).
- Para compilar: .NET SDK 10. Para el instalador: Inno Setup 6.

## Arquitectura

```
┌──────────────────────────────┐  named pipe   ┌────────────────────────────┐
│ EasySearcher.Service         │ ◄───────────► │ EasySearcher (app WPF)     │
│ Servicio de Windows (SYSTEM) │ "EasySearcher │ usuario normal, sin UAC    │
│ lee la MFT, índice en memoria│      .v1"     │ pide resultados por páginas│
└──────────────────────────────┘               └────────────────────────────┘
```

Leer la MFT y el USN Journal exige permisos de administrador, así que el índice vive en un servicio.
La app corre como usuario normal: así no hay aviso de UAC, y arrastrar y soltar y abrir archivos
funcionan con normalidad. Si no hay servicio y la app se abre como administrador, construye el índice
ella misma.

| Proyecto | Contenido |
|---|---|
| `src/EasySearcher.Core` | Motor: lectura de la MFT y del journal (`MftReader`), índice por volumen (`VolumeIndex`), búsqueda (`FileIndex`, `SearchQuery`), orden y filtros, y la capa de servicio (`Service/`: contrato, `SearchEngine`, protocolo, servidor y cliente del pipe) |
| `src/EasySearcher.Service` | Servicio de Windows que aloja el `SearchEngine` y atiende el pipe |
| `src/EasySearcher.App` | Interfaz WPF: lista virtual por páginas, miniaturas del shell, instancia única, argumentos |
| `src/EasySearcher.Cli` | `easysearch.exe`: herramienta de línea de comandos para probar y medir el motor |
| `tests/EasySearcher.Tests` | Pruebas (xUnit) del motor, el journal, el orden, el pipe y partes de la app |
| `installer/` | Script de Inno Setup |
| `tools/` | Scripts de desarrollo (ver más abajo) |

Detalles importantes del diseño:

- Las entradas se guardan en listas paralelas indexadas por el número de registro de la MFT; los
  nombres repetidos comparten un solo string. Los nombres ocupan unos 50 MB; con tamaño y fecha, ~65 MB.
- La enumeración rápida de la MFT (`FSCTL_ENUM_USN_DATA`) no trae tamaño ni fecha. Con el índice ya
  usable, `MftDetailsReader` lee la tabla `$MFT` cruda (siguiendo su runlist, con las correcciones de la
  update sequence) y saca el tamaño de `$DATA` y la fecha de `$STANDARD_INFORMATION`. Lo que cambia
  después (o lo que no se pudo leer) se consulta a Windows por archivo y queda en caché.
- El journal distingue cambios de estructura (altas, bajas, renombres, movimientos) de cambios de
  contenido (tamaño, fecha). La app repite la búsqueda en el primer caso y, en el segundo, solo
  refresca las filas visibles (salvo que esté ordenando o filtrando por tamaño o fecha).
- La app y el servicio comparan `Protocol.Version` (`Core/Service/Contracts.cs`). Si el servicio
  instalado es más viejo, la app deshabilita lo que este no entiende en lugar de mostrar resultados
  incorrectos. **Hay que subir esa versión cada vez que se agregan campos al protocolo.**

## Compilar y probar

```powershell
dotnet build EasySearcher.slnx
dotnet test tests/EasySearcher.Tests
```

Las pruebas no necesitan administrador: construyen índices en memoria (`VolumeIndex.FromRecords`) y
levantan el pipe con un nombre propio, sin tocar el servicio instalado.

## Flujo de desarrollo

En esta máquina el SDK está en `D:\Programs\dotnet` y los paquetes NuGet en
`D:\Programs\nuget\packages` (variable `NUGET_PACKAGES`).

1. **Registrar el servicio de desarrollo** (una vez, como administrador). Se publica en
   `artifacts\service` y no se registra desde `bin\`, porque el servicio bloquearía esos archivos al
   compilar:
   ```powershell
   dotnet publish src/EasySearcher.Service -c Release -r win-x64 --self-contained -o artifacts/service
   pwsh -File tools\service.ps1 install -Path artifacts\service\EasySearcher.Service.exe
   ```
2. **Después de cambiar el motor o el servicio** (como administrador): detiene el servicio, lo
   recompila y lo vuelve a iniciar.
   ```powershell
   pwsh -File tools\update-dev-service.ps1
   ```
3. **Cambios en la interfaz:** compilar y abrir `src\EasySearcher.App\bin\Debug\net10.0-windows\EasySearcher.exe`.
4. **Clic derecho apuntando a la app de desarrollo** (solo el usuario actual, sin administrador):
   ```powershell
   pwsh -File tools\context-menu.ps1 install -Path src\EasySearcher.App\bin\Debug\net10.0-windows\EasySearcher.exe
   ```

`tools\service.ps1` también admite `restart` y `uninstall`, y `tools\context-menu.ps1` admite `uninstall`.

> El servicio de desarrollo corre como SYSTEM desde una carpeta en la que el usuario puede escribir.
> Sirve para desarrollar en un equipo propio; el instalador lo coloca en `Program Files`.

### Desarrollar con la versión instalada

El servicio instalado y el de desarrollo se llaman igual (`EasySearcher`), así que **solo uno puede estar
registrado a la vez**. Con EasySearcher instalado:

- **Cambios en la interfaz (app):** se prueban con la app de desarrollo
  (`src\EasySearcher.App\bin\Debug\net10.0-windows\EasySearcher.exe`), que habla con el servicio instalado.
  **Antes hay que cerrar la app instalada**: las dos comparten la instancia única, y si la instalada está
  abierta, la de desarrollo le pasa sus argumentos y se cierra.
- **Cambios en el motor o el servicio (`EasySearcher.Core`, `EasySearcher.Service`):** hay que volver a
  apuntar el servicio a `artifacts\service` (paso 1 de arriba), y eso **reemplaza el servicio instalado**.
  La app instalada sigue funcionando contra el servicio de desarrollo. Después, `tools\update-dev-service.ps1`
  como siempre.
- **Volver a la versión instalada:** reinstalar con el instalador (`artifacts\installer\EasySearcherSetup-<versión>.exe`),
  que vuelve a registrar el servicio desde `Program Files`.
- **Clic derecho:** el de desarrollo (HKCU) tiene prioridad sobre el del instalador (HKLM). Si se registra
  el de desarrollo, se quita con `tools\context-menu.ps1 uninstall` para volver al instalado.
- Si un cambio agrega campos al protocolo, una app nueva con un servicio viejo deshabilita lo que este no
  entiende (ver `Protocol.Version`); no es un error, falta actualizar el servicio.

### CLI

```powershell
easysearch linkin                                  # lee la MFT directamente (requiere administrador)
easysearch --service -r D:\Musica -k audio linkin  # consulta al servicio (sin administrador)
easysearch --service -k images --from 2026-01-01 -o date- -l ""
easysearch --service --min 4G -o size- -l ""       # archivos de mas de 4 GB
easysearch --verify-mft                            # compara tamano/fecha de la $MFT con Windows (administrador)
easysearch -h                                      # todas las opciones
```

### Ícono

`tools/make-icon.cs` dibuja el ícono transcribiendo `src/EasySearcher.App/Assets/EasySearcher.svg`
y genera el `.ico` con todos los tamaños:

```powershell
dotnet run tools/make-icon.cs -- src/EasySearcher.App/Assets/EasySearcher.ico
```

## Instalador

```powershell
pwsh -File tools\build-installer.ps1
```

Publica la app y el servicio autocontenidos (con .NET incluido) en `artifacts\publish` y genera
`artifacts\installer\EasySearcherSetup-<versión>.exe`. La versión sale de `Directory.Build.props`.

El instalador copia todo a `Program Files\EasySearcher`, registra e inicia el servicio, y agrega el
clic derecho (opcional) y el acceso en Inicio. Al actualizar, detiene el servicio anterior. Al
desinstalar, quita todo.

## Limitaciones conocidas

- Durante los primeros segundos tras arrancar el servicio (mientras se lee la `$MFT`), ordenar o filtrar
  por tamaño o fecha una búsqueda enorme puede tardar, porque esos datos se piden archivo por archivo.
- El orden ignora tildes ("Árbol" va junto a "Arbol"), pero no es un orden "natural" de números
  ("archivo10" va antes que "archivo2").
- Los archivos que el sistema tiene abiertos escribiendo (p. ej. logs de eventos) pueden mostrar en la
  `$MFT` una fecha unos minutos más vieja que la que ve Windows en memoria.
- Un generador de miniaturas que se cuelgue más de 10 s se abandona y se reemplaza su hilo (hasta 6 veces).
- Cualquier usuario del equipo puede buscar nombres de archivos de todo el disco (solo los nombres).
- En Windows 11 el clic derecho aparece en "Mostrar más opciones".

## Licencias de terceros

- [VirtualizingWrapPanel](https://github.com/sbaeumlisberger/VirtualizingWrapPanel), de S. Bäumlisberger: licencia MIT.
- .NET (runtime, WPF, `Microsoft.Extensions.*`), incluido en el instalador: licencia MIT, .NET Foundation.

Los textos completos están en `installer/THIRD-PARTY-NOTICES.txt`, que el instalador copia a la carpeta
de instalación. Si se agrega otra dependencia, hay que sumar su aviso ahí.
