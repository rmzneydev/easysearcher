using System.Text;

namespace EasySearcher.Core;

/// <summary>
/// Texto de busqueda ya interpretado (sin distinguir mayusculas):
///   linkin park        el nombre contiene "linkin" y "park", en cualquier orden
///   "linkin park"      contiene el texto exacto, con el espacio
///   *.mp3  foto_??.jpg comodines (* cualquier texto, ? un caracter) contra el nombre completo
///   !temp  !*.tmp      excluye los nombres que coinciden
/// Una consulta vacia coincide con todo.
/// </summary>
public sealed class SearchQuery
{
    readonly Term[] _terms;

    SearchQuery(Term[] terms) => _terms = terms;

    public bool IsEmpty => _terms.Length == 0;

    public static SearchQuery Parse(string text)
    {
        var terms = new List<Term>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            bool negated = text[i] == '!';
            if (negated) i++;

            var pattern = new StringBuilder();
            bool quoted = i < text.Length && text[i] == '"';
            if (quoted)
            {
                // Si falta la comilla de cierre, se toma hasta el final
                i++;
                while (i < text.Length && text[i] != '"') pattern.Append(text[i++]);
                i++;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i])) pattern.Append(text[i++]);
            }

            if (pattern.Length == 0) continue;
            var value = pattern.ToString();
            // Entre comillas el texto es literal: * y ? no son comodines
            bool wildcard = !quoted && value.AsSpan().IndexOfAny('*', '?') >= 0;
            terms.Add(new Term(value, wildcard, negated));
        }
        return new SearchQuery([.. terms]);
    }

    public bool Matches(string name)
    {
        foreach (var term in _terms)
        {
            bool hit = term.Wildcard
                ? WildcardMatch(name, term.Pattern)
                : name.Contains(term.Pattern, StringComparison.OrdinalIgnoreCase);
            if (hit == term.Negated) return false;
        }
        return true;
    }

    /// <summary>Compara el texto completo con un patron con * y ?, sin distinguir mayusculas. Sin expresiones regulares.</summary>
    internal static bool WildcardMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int t = 0, p = 0;
        int star = -1, starText = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || SameChar(pattern[p], text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                // Se recuerda el ultimo * para reintentar desde ahi si algo no coincide mas adelante
                star = p++;
                starText = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    // Misma regla que StringComparison.OrdinalIgnoreCase
    static bool SameChar(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    readonly record struct Term(string Pattern, bool Wildcard, bool Negated);
}
