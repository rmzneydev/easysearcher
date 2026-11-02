#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

// Genera el icono de la app dibujandolo con WPF en los tamanos que usa Windows y empaquetandolo en un .ico
// (entradas PNG, validas desde Windows Vista). Tambien deja una vista previa .png de 256 px.
//   dotnet run tools/make-icon.cs -- src/EasySearcher.App/Assets/EasySearcher.ico
//
// El dibujo transcribe src/EasySearcher.App/Assets/EasySearcher.svg (diseno "Variante C - Tile con rayo"
// de Claude Design) en sus mismas coordenadas (viewBox 0 0 256 256). Si cambia el SVG, hay que reflejarlo aqui.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

var output = args.Length > 0 ? args[0] : "EasySearcher.ico";
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];

// WPF exige un hilo STA para crear elementos visuales
List<(int Size, byte[] Png)> images = [];
var thread = new Thread(() => images = sizes.Select(s => (s, Render(s))).ToList());
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
WriteIco(output, images);
File.WriteAllBytes(Path.ChangeExtension(output, ".png"), images[^1].Png);
Console.WriteLine($"{output}: {string.Join(", ", sizes)} px");

static byte[] Render(int size)
{
    // El diseno original dejaba solo la lupa a 24 px o menos; se prefirio el rayo tambien en tamanos chicos.
    // Para volver a eso: detailed = size > 24 y strokeBoost = 1.25 cuando no es detailed.
    bool detailed = true;
    double strokeBoost = 1.0;
    var scale = new ScaleTransform(size / 256.0, size / 256.0);

    var center = new Point(114, 114);
    var handleStart = new Point(158, 158);
    var handleEnd = new Point(192, 192);
    Pen RingPen(Brush b) => new(b, 24 * strokeBoost);
    Pen HandlePen(Brush b) => new(b, 32 * strokeBoost) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    var root = new ContainerVisual();

    // Tile: degradado diagonal, brillo arriba y borde (claro arriba, oscuro abajo)
    var tileVisual = new DrawingVisual();
    using (var dc = tileVisual.RenderOpen())
    {
        dc.PushTransform(scale);
        var tile = new Rect(16, 16, 224, 224);
        var tileBrush = new LinearGradientBrush(Hex("#3B82F6"), Hex("#1D4ED8"), new Point(16, 16), new Point(240, 240))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        dc.DrawRoundedRectangle(tileBrush, null, tile, 52, 52);

        if (detailed)
        {
            var sheen = new LinearGradientBrush(
                [new GradientStop(Hex("#FFFFFF", 0.20), 0), new GradientStop(Hex("#FFFFFF", 0), 0.55)], 90);
            dc.DrawRoundedRectangle(sheen, null, tile, 52, 52);

            var rim = new LinearGradientBrush(
                [
                    new GradientStop(Hex("#FFFFFF", 0.35), 0),
                    new GradientStop(Hex("#FFFFFF", 0.06), 0.5),
                    new GradientStop(Hex("#0B1F66", 0.25), 1),
                ], 90);
            dc.DrawRoundedRectangle(null, new Pen(rim, 3), new Rect(17.5, 17.5, 221, 221), 50.5, 50.5);
        }
    }
    root.Children.Add(tileVisual);

    // Sombra de la lupa: desplazada 5 hacia abajo, desenfocada (feGaussianBlur stdDeviation=4) y al 38 %
    if (detailed)
    {
        var shadowVisual = new DrawingVisual
        {
            Opacity = 0.38,
            // El radio del BlurEffect de WPF equivale a unas 2,5 desviaciones estandar
            Effect = new BlurEffect { Radius = 4 * 2.5 * size / 256.0, KernelType = KernelType.Gaussian },
        };
        using (var dc = shadowVisual.RenderOpen())
        {
            dc.PushTransform(scale);
            dc.PushTransform(new TranslateTransform(0, 5));
            var shadow = new SolidColorBrush(Hex("#0B1F66"));
            dc.DrawEllipse(null, RingPen(shadow), center, 54, 54);
            dc.DrawLine(HandlePen(shadow), handleStart, handleEnd);
        }
        root.Children.Add(shadowVisual);
    }

    // Lupa: cristal blanco al 12 %, aro y mango blancos; rayo ambar con trazo
    var glyphVisual = new DrawingVisual();
    using (var dc = glyphVisual.RenderOpen())
    {
        dc.PushTransform(scale);
        dc.DrawEllipse(new SolidColorBrush(Hex("#FFFFFF", 0.12)), RingPen(Brushes.White), center, 54, 54);
        dc.DrawLine(HandlePen(Brushes.White), handleStart, handleEnd);

        if (detailed)
        {
            var bolt = Geometry.Parse("M124 78L92 120H111L103 150L136 106H117Z");
            var boltBrush = new LinearGradientBrush(Hex("#FDE68A"), Hex("#F59E0B"), new Point(114, 78), new Point(114, 150))
            {
                MappingMode = BrushMappingMode.Absolute,
            };
            dc.DrawGeometry(boltBrush, new Pen(new SolidColorBrush(Hex("#F59E0B")), 2) { LineJoin = PenLineJoin.Round }, bolt);
        }
    }
    root.Children.Add(glyphVisual);

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(root);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

static Color Hex(string hex, double opacity = 1)
{
    var c = (Color)ColorConverter.ConvertFromString(hex);
    c.A = (byte)Math.Round(255 * opacity);
    return c;
}

static void WriteIco(string path, List<(int Size, byte[] Png)> images)
{
    using var writer = new BinaryWriter(File.Create(path));
    // ICONDIR
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Count);

    // ICONDIRENTRY por imagen; los datos van despues de todas las entradas
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png.Length);
        writer.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images)
        writer.Write(png);
}
