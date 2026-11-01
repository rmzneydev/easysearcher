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

    public static StartupArgs Parse(IReadOnlyList<string> rawArgs)
    {
        var args = RepairQuotedRoots(rawArgs);
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
    /// Windows interpreta \" como una comilla literal, asi que "D:\" --query x llega como un solo
    /// argumento: D:" --query x. Como " no puede aparecer en una ruta, se corta ahi (devolviendo la barra)
    /// y el resto se vuelve a separar en argumentos.
    /// </summary>
    static List<string> RepairQuotedRoots(IReadOnlyList<string> args)
    {
        var repaired = new List<string>();
        foreach (var arg in args)
        {
            int quote = arg.IndexOf('"');
            if (quote < 0)
            {
                repaired.Add(arg);
                continue;
            }
            repaired.Add(arg[..quote] + '\\');
            repaired.AddRange(SplitCommandLine(arg[(quote + 1)..]));
        }
        return repaired;
    }

    /// <summary>Separa por espacios respetando comillas (suficiente para lo que queda tras la reparacion).</summary>
    static IEnumerable<string> SplitCommandLine(string text)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>
    /// Completa "D:" como "D:\". Si la ruta es un archivo, usa la carpeta que lo contiene.
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
