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

public class SizeFilterTests
{
    const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB;

    [Fact]
    public void Larger_is_strict_and_smaller_excludes_the_limit()
    {
        var larger = SizeFilter.Create(SizeMode.Larger, "100", SizeUnit.MB);
        Assert.Equal((100 * MB + 1, (long?)null), SizeFilter.GetRange(larger));

        var smaller = SizeFilter.Create(SizeMode.Smaller, "500", SizeUnit.KB);
        Assert.Equal(((long?)null, 500 * KB), SizeFilter.GetRange(smaller));
    }

    [Fact]
    public void Empty_means_exactly_zero_bytes()
    {
        Assert.Equal((0L, 1L), SizeFilter.GetRange(SizeFilter.Create(SizeMode.Empty, "", SizeUnit.MB)));
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("1.5")]
    [InlineData(" 1,5 ")]
    public void Decimals_work_with_comma_or_point(string text)
    {
        var size = SizeFilter.Create(SizeMode.Smaller, text, SizeUnit.GB);
        Assert.Equal(((long?)null, GB + GB / 2), SizeFilter.GetRange(size));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("-5")]
    [InlineData("99999999999")]   // mas que cualquier disco: no debe desbordar
    public void Without_a_valid_value_it_does_not_filter(string text)
    {
        Assert.Equal(default, SizeFilter.Create(SizeMode.Larger, text, SizeUnit.GB));
    }

    [Fact]
    public void Any_ignores_what_is_left_in_the_value_box()
    {
        // Dos busquedas sin filtro deben ser iguales aunque quede una cifra escrita (historial)
        Assert.Equal(default, SizeFilter.Create(SizeMode.Any, "100", SizeUnit.GB));
        Assert.Equal(((long?)null, (long?)null), SizeFilter.GetRange(default));
        Assert.Equal("", SizeFilter.Describe(default));
    }

    [Fact]
    public void Describe_reads_like_the_filter()
    {
        Assert.Equal("de más de 100 MB", SizeFilter.Describe(new SizeCriterion(SizeMode.Larger, 100, SizeUnit.MB)));
        Assert.Equal("de menos de 2 GB", SizeFilter.Describe(new SizeCriterion(SizeMode.Smaller, 2, SizeUnit.GB)));
        Assert.Equal("vacíos", SizeFilter.Describe(new SizeCriterion(SizeMode.Empty)));
    }
}

public class PathTrimmerTests
{
    // Un caracter = una unidad de ancho
    static string Trim(string path, double width) => PathTrimmer.Trim(path, width, t => t.Length);

    const string Sample = @"D:\Neyder\CMP\backend\src\modules\clients\presentation\dtos";

    [Fact]
    public void A_path_that_fits_stays_whole()
    {
        Assert.Equal(Sample, Trim(Sample, Sample.Length));
    }

    [Fact]
    public void Trims_the_middle_at_folder_boundaries_keeping_as_much_of_the_end_as_fits()
    {
        Assert.Equal(@"D:\Neyder\…\clients\presentation\dtos", Trim(Sample, 40));
        Assert.Equal(@"D:\Neyder\…\presentation\dtos", Trim(Sample, 32));
    }

    [Fact]
    public void A_very_narrow_cell_keeps_the_end_of_the_path()
    {
        Assert.Equal(@"…on\dtos", Trim(Sample, 8));
    }

    [Fact]
    public void Short_paths_and_empty_text_do_not_fail()
    {
        Assert.Equal("", Trim("", 10));
        Assert.Equal("…", Trim(@"D:\", 1));
    }
}
