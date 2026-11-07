using EasySearcher.Core.Service;

namespace EasySearcher.App;

/// <summary>
/// Lo que se muestra en lugar de la lista cuando no hay filas que ver, con lo que se puede hacer al respecto.
/// </summary>
sealed record ListMessage(
    string Title,
    string Detail = "",
    bool OfferClearFilters = false,
    bool OfferAllDrives = false,
    bool ShowRecent = false)
{
    const string Tips =
        "linkin mp3  contiene ambas palabras      \"linkin park\"  texto exacto\n" +
        "*.mp3  comodines      !temp  excluye\n" +
        "↓ entra en los resultados      Enter abre      Ctrl+Enter abre la carpeta      Ctrl+L vuelve aquí";

    /// <param name="status">Estado del motor; null si no hay conexion con el.</param>
    /// <param name="search">La busqueda de la ventana.</param>
    /// <param name="total">Resultados de la ultima busqueda; null si no se ha buscado.</param>
    /// <param name="error">Por que no se pudo buscar.</param>
    /// <param name="rootFailed">El error es de la carpeta escrita, que aun no es la raiz de la busqueda.</param>
    /// <returns>null si hay filas que mostrar, o si la busqueda aun no ha respondido.</returns>
    public static ListMessage? For(EngineStatus? status, SearchState search, int? total, string? error, bool rootFailed = false)
    {
        if (status is null)
        {
            return new ListMessage("Conectando con el servicio de EasySearcher...",
                "Si no conecta, comprueba que el servicio «EasySearcher» esté iniciado en Servicios de Windows.");
        }

        if (!status.Ready)
        {
            return status.Failures.Length > 0
                ? new ListMessage("No se pudieron indexar las unidades", string.Join("\n", status.Failures))
                : new ListMessage("Indexando unidades NTFS...", $"{status.BuildSeconds:N0} s");
        }

        if (error is not null)
            return new ListMessage("No se pudo buscar", error, OfferAllDrives: rootFailed || search.Root is not null);

        if (total is null)
            return search.Text.Length == 0 ? new ListMessage("Escribe para buscar", Tips, ShowRecent: true) : null;

        if (total > 0) return null;

        string title = search.Text.Length > 0 ? $"Sin resultados para «{search.Text}»" : "Sin resultados";
        string detail = search.Description;
        if (search.UsesDetails && !status.DetailsReady)
            detail += "\nTodavía se están leyendo tamaños y fechas: pueden aparecer más.";
        return new ListMessage(title, detail, search.HasFilters, search.Root is not null);
    }
}
