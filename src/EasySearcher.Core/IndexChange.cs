namespace EasySearcher.Core;

/// <summary>Que cambio en el indice al aplicar el USN Journal.</summary>
[Flags]
public enum IndexChange
{
    None = 0,

    /// <summary>Entradas creadas, renombradas, movidas, borradas o con atributos distintos.</summary>
    Structure = 1,

    /// <summary>Solo cambio el contenido de archivos: su tamano o fecha ya no son los guardados.</summary>
    Details = 2,
}
