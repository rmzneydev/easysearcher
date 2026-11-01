using System.Globalization;

namespace EasySearcher.App;

/// <summary>Opciones del filtro de fecha, en el mismo orden que el ComboBox de la ventana.</summary>
enum DatePreset
{
    Any,
    Today,
    Yesterday,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    ThisYear,
    LastYear,
    Custom,
}

static class DateFilter
{
    /// <summary>
    /// Rango [desde, hasta) en hora local. Se calcula en cada busqueda para que "Hoy" o "Esta semana"
    /// sigan siendo correctos aunque pase la medianoche. La semana empieza segun la configuracion regional.
    /// </summary>
    public static (DateTime? From, DateTime? To) GetRange(DatePreset preset, DateTime? customFrom, DateTime? customTo, DateTime now)
    {
        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        return preset switch
        {
            DatePreset.Today => (today, tomorrow),
            DatePreset.Yesterday => (today.AddDays(-1), today),
            DatePreset.ThisWeek => (weekStart, tomorrow),
            DatePreset.LastWeek => (weekStart.AddDays(-7), weekStart),
            DatePreset.ThisMonth => (monthStart, tomorrow),
            DatePreset.LastMonth => (monthStart.AddMonths(-1), monthStart),
            DatePreset.ThisYear => (yearStart, tomorrow),
            DatePreset.LastYear => (yearStart.AddYears(-1), yearStart),
            // "Hasta" es inclusivo para el usuario: se toma hasta el final de ese dia
            DatePreset.Custom => (customFrom?.Date, customTo?.Date.AddDays(1)),
            _ => (null, null),
        };
    }

    /// <summary>Texto para la barra de estado, p. ej. "modificados este mes".</summary>
    public static string Describe(DatePreset preset, DateTime? customFrom, DateTime? customTo) => preset switch
    {
        DatePreset.Today => "modificados hoy",
        DatePreset.Yesterday => "modificados ayer",
        DatePreset.ThisWeek => "modificados esta semana",
        DatePreset.LastWeek => "modificados la semana pasada",
        DatePreset.ThisMonth => "modificados este mes",
        DatePreset.LastMonth => "modificados el mes pasado",
        DatePreset.ThisYear => "modificados este año",
        DatePreset.LastYear => "modificados el año pasado",
        DatePreset.Custom => (customFrom, customTo) switch
        {
            ({ } f, { } t) => $"modificados del {f:d} al {t:d}",
            ({ } f, null) => $"modificados desde el {f:d}",
            (null, { } t) => $"modificados hasta el {t:d}",
            _ => "",
        },
        _ => "",
    };
}
