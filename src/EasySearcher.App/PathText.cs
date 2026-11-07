using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EasySearcher.App;

static class PathTrimmer
{
    const char Separator = '\\';
    const string Ellipsis = "…";
    const int HeadSegments = 2;   // "D:\Neyder": la unidad y la primera carpeta ubican la ruta

    /// <summary>
    /// Acorta una ruta por el medio, a limites de carpeta: lo que distingue dos resultados suele estar al
    /// final ("D:\Neyder\…\clients\presentation\dtos"). Si ni la ultima carpeta cabe, corta dentro de ella.
    /// </summary>
    /// <param name="measure">Ancho que ocupa un texto, en las mismas unidades que <paramref name="width"/>.</param>
    public static string Trim(string path, double width, Func<string, double> measure)
    {
        if (measure(path) <= width) return path;

        var segments = path.Split(Separator);
        string head = string.Join(Separator, segments.Take(HeadSegments));
        for (int start = HeadSegments + 1; start < segments.Length; start++)
        {
            string candidate = $"{head}{Separator}{Ellipsis}{Separator}{string.Join(Separator, segments.Skip(start))}";
            if (measure(candidate) <= width) return candidate;
        }

        // Mayor cantidad de caracteres finales que cabe tras los puntos suspensivos
        int low = 0, high = path.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (measure(Ellipsis + path[^middle..]) <= width) low = middle;
            else high = middle - 1;
        }
        return Ellipsis + path[^low..];
    }
}

/// <summary>Ruta de una sola linea que se acorta por el medio al ancho disponible (TextBlock solo corta al final).</summary>
sealed class PathText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PathText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    // La fila seleccionada cambia el color del texto
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(PathText),
        new FrameworkPropertyMetadata(SystemColors.ControlTextBrush,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    FormattedText Format(string text) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection,
        new Typeface(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this), TextElement.GetFontStretch(this)),
        TextElement.GetFontSize(this),
        (Brush)GetValue(ForegroundProperty),
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // Sin ancho propio: ocupa el de la celda, y un texto largo no debe ensanchar nada
    protected override Size MeasureOverride(Size availableSize) => new(0, Format("Ag").Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        string shown = PathTrimmer.Trim(Text ?? "", ActualWidth, t => Format(t).WidthIncludingTrailingWhitespace);
        drawingContext.DrawText(Format(shown), new Point(0, 0));
    }
}
