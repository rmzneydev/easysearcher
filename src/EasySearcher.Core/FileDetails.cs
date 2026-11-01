namespace EasySearcher.Core;

/// <param name="Size">Tamano en bytes; null para carpetas o si no se pudo leer.</param>
/// <param name="Modified">Ultima modificacion en hora local; null si no se pudo leer.</param>
public readonly record struct FileDetails(long? Size, DateTime? Modified);
