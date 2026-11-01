using System.Globalization;
using EasySearcher.App;

namespace EasySearcher.Tests;

public class StartupArgsTests
{
    [Fact]
    public void Root_and_query_options()
    {
        var args = StartupArgs.Parse(["--root", @"D:\Musica", "--query", "linkin"]);
        Assert.Equal(@"D:\Musica", args.Root);
        Assert.Equal("linkin", args.Query);
    }

    [Fact]
    public void A_bare_path_is_the_root()
    {
        Assert.Equal(@"D:\Musica", StartupArgs.Parse([@"D:\Musica"]).Root);
        Assert.True(StartupArgs.Parse([]).IsEmpty);
    }

    [Fact]
    public void Drive_root_in_quotes_is_repaired()
    {
        // "D:\" llega como D:" porque \" es una comilla escapada (asi lo manda el clic derecho)
        Assert.Equal(@"D:\", StartupArgs.Parse(["D:\""]).Root);
        Assert.Equal(@"D:\", StartupArgs.Parse(["D:"]).Root);
    }

    [Fact]
    public void Drive_root_in_quotes_does_not_swallow_the_next_arguments()
    {
        // --root "D:\" --query linkin  llega como un solo argumento tras --root
        var args = StartupArgs.Parse(["--root", "D:\" --query linkin"]);
        Assert.Equal(@"D:\", args.Root);
        Assert.Equal("linkin", args.Query);
    }

    [Fact]
    public void A_file_path_uses_its_folder()
    {
        var file = Path.GetTempFileName();
        try
        {
            Assert.Equal(Path.GetDirectoryName(file), StartupArgs.Parse([file]).Root);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class DateFilterTests
{
    // Miercoles 4 de noviembre de 2026, 15:30
    static readonly DateTime Now = new(2026, 11, 4, 15, 30, 0);

    // DatePreset es interno de la app: una prueba publica no puede recibirlo como parametro
    [Theory]
    [InlineData((int)DatePreset.Today, "2026-11-04", "2026-11-05")]
    [InlineData((int)DatePreset.Yesterday, "2026-11-03", "2026-11-04")]
    [InlineData((int)DatePreset.ThisWeek, "2026-11-02", "2026-11-05")]   // semana desde el lunes (es-ES)
    [InlineData((int)DatePreset.LastWeek, "2026-10-26", "2026-11-02")]
    [InlineData((int)DatePreset.ThisMonth, "2026-11-01", "2026-11-05")]
    [InlineData((int)DatePreset.LastMonth, "2026-10-01", "2026-11-01")]
    [InlineData((int)DatePreset.ThisYear, "2026-01-01", "2026-11-05")]
    [InlineData((int)DatePreset.LastYear, "2025-01-01", "2026-01-01")]
    public void Presets_give_half_open_ranges(int preset, string from, string to)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            var (f, t) = DateFilter.GetRange((DatePreset)preset, null, null, Now);
            Assert.Equal(DateTime.Parse(from, CultureInfo.InvariantCulture), f);
            Assert.Equal(DateTime.Parse(to, CultureInfo.InvariantCulture), t);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Custom_range_includes_the_whole_last_day()
    {
        var (from, to) = DateFilter.GetRange(DatePreset.Custom, new DateTime(2020, 1, 1, 9, 0, 0), new DateTime(2020, 12, 31), Now);
        Assert.Equal(new DateTime(2020, 1, 1), from);
        Assert.Equal(new DateTime(2021, 1, 1), to);
    }

    [Fact]
    public void Any_date_does_not_filter()
    {
        Assert.Equal((null, null), DateFilter.GetRange(DatePreset.Any, null, null, Now));
    }
}
