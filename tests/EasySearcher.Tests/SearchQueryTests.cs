using EasySearcher.Core;

namespace EasySearcher.Tests;

public class SearchQueryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_query_matches_everything(string text)
    {
        var query = SearchQuery.Parse(text);
        Assert.True(query.IsEmpty);
        Assert.True(query.Matches("cualquier cosa.txt"));
    }

    [Theory]
    [InlineData("linkin mp3", "Numb - Linkin Park.mp3", true)]
    [InlineData("mp3 linkin", "Numb - Linkin Park.mp3", true)]   // el orden no importa
    [InlineData("linkin flac", "Numb - Linkin Park.mp3", false)]  // tienen que estar todas
    [InlineData("LINKIN", "linkin park.mp3", true)]                // sin distinguir mayusculas
    [InlineData("árbol", "ÁRBOL de navidad.jpg", true)]            // tambien fuera de ASCII
    public void Words_must_all_appear(string text, string name, bool expected) =>
        Assert.Equal(expected, SearchQuery.Parse(text).Matches(name));

    [Theory]
    [InlineData("\"linkin park\"", "Best of Linkin Park.mp3", true)]
    [InlineData("\"linkin park\"", "Park - Linkin.mp3", false)]
    [InlineData("\"linkin park", "Best of Linkin Park.mp3", true)] // comilla sin cerrar: hasta el final
    [InlineData("\"*.mp3\"", "song.mp3", false)]                  // entre comillas * es literal
    [InlineData("\"*.mp3\"", "raro*.mp3", true)]
    public void Quotes_match_the_exact_text(string text, string name, bool expected) =>
        Assert.Equal(expected, SearchQuery.Parse(text).Matches(name));

    [Theory]
    [InlineData("*.mp3", "song.mp3", true)]
    [InlineData("*.mp3", "song.MP3", true)]
    [InlineData("*.mp3", "song.mp3.bak", false)]   // contra el nombre completo, no como subcadena
    [InlineData("foto_??.jpg", "foto_01.jpg", true)]
    [InlineData("foto_??.jpg", "foto_1.jpg", false)]
    [InlineData("foto_??.jpg", "foto_001.jpg", false)]
    [InlineData("a*b*c", "a-xx-b-yy-c", true)]
    [InlineData("a*b*c", "a-xx-c-yy-b", false)]
    [InlineData("*", "lo que sea", true)]
    [InlineData("linkin *.mp3", "Linkin Park - Numb.mp3", true)]  // se combinan palabras y comodines
    public void Wildcards_match_the_whole_name(string text, string name, bool expected) =>
        Assert.Equal(expected, SearchQuery.Parse(text).Matches(name));

    [Theory]
    [InlineData("!temp", "notas.txt", true)]
    [InlineData("!temp", "archivo.temp", false)]
    [InlineData("*.log !error", "app.log", true)]
    [InlineData("*.log !error", "error.log", false)]
    [InlineData("!*.tmp", "datos.tmp", false)]
    [InlineData("!\"linkin park\"", "Linkin Park.mp3", false)]
    [InlineData("!", "cualquiera", true)]  // una exclusion vacia se ignora
    public void Exclamation_excludes(string text, string name, bool expected) =>
        Assert.Equal(expected, SearchQuery.Parse(text).Matches(name));

    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "*", true)]
    [InlineData("", "?", false)]
    [InlineData("abc", "a?c", true)]
    [InlineData("abc", "***", true)]
    [InlineData("abc", "abcd", false)]
    [InlineData("abc", "ab", false)]
    public void WildcardMatch_edge_cases(string text, string pattern, bool expected) =>
        Assert.Equal(expected, SearchQuery.WildcardMatch(text, pattern));
}
