using System.IO;

namespace EasySearcher.App;

/// <summary>
/// Argumentos de la linea de comandos:
///   EasySearcher.exe [--root "carpeta"] [--query "texto"]
///   EasySearcher.exe "carpeta"          (lo que usa el clic derecho del Explorador)
/// </summary>
sealed record StartupArgs(string? Root, string? Query)
{
    public bool IsEmpty => Root is null && Query is null;

    public static StartupArgs Parse(IReadOnlyList<string> args)
    {
        string? root = null, query = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--root" or "-r" when i + 1 < args.Count:
                    root = NormalizePath(args[++i]);
                    break;
                case "--query" or "-q" when i + 1 < args.Count:
                    query = args[++i];
                    break;
                default:
                    root ??= NormalizePath(args[i]);
                    break;
            }
        }
        return new StartupArgs(root, query);
    }

    /// <summary>
    /// Corrige "D:\" entre comillas, que Windows entrega como D:" (la barra escapa la comilla final).
    /// Si la ruta es un archivo, usa la carpeta que lo contiene.
    /// </summary>
    static string? NormalizePath(string path)
    {
        path = path.Trim().TrimEnd('"');
        if (path.Length == 0) return null;
        if (path.Length == 2 && path[1] == ':') path += '\\';
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        return path;
    }
}
