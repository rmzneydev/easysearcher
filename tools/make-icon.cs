#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

// Genera el icono de la app dibujandolo con WPF en los tamanos que usa Windows y empaquetandolo en un .ico
// (entradas PNG, validas desde Windows Vista). Tambien deja una vista previa .png de 256 px.
//   dotnet run tools/make-icon.cs -- src/EasySearcher.App/Assets/EasySearcher.ico

using System.IO;
using System.Windows;
using System.Windows.Media;
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

// Diseno "Variante C - Tile con rayo" (Claude Design): cuadrado azul, lupa blanca con sombra y un rayo
// ambar dentro del cristal. A 24 px o menos queda solo la lupa sobre el tile, para que se lea.
static byte[] Render(int size)
{
    double s = size;
    double m = s * 0.03;
    double t = s - 2 * m;
    Point P(double u, double v) => new(m + u * t, m + v * t);  // coordenadas relativas al tile (0..1)
    double L(double u) => u * t;
    bool detailed = size >= 32;

    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        // Tile: degradado diagonal de azul claro a azul profundo, con un borde claro sutil
        var tile = new Rect(m, m, t, t);
        double radius = L(0.24);
        var tileBrush = new LinearGradientBrush(Color.FromRgb(0x5E, 0x9E, 0xFB), Color.FromRgb(0x1D, 0x4E, 0xD8), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(tileBrush, null, tile, radius, radius);
        if (detailed)
        {
            double inset = L(0.008);
            var rim = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x93, 0xC5, 0xFD)), L(0.016));
            dc.DrawRoundedRectangle(null, rim, Rect.Inflate(tile, -inset, -inset), radius - inset, radius - inset);
        }

        // Geometria de la lupa: aro (radio medio y grosor) y mango a 45 grados
        var center = P(0.4375, 0.425);
        double ringRadius = L(0.23);
        double ringWidth = Math.Max(1.6, L(0.10));
        double handleWidth = Math.Max(2.0, L(0.115));
        double startOffset = ringRadius * Math.Sqrt(0.5);
        var handleStart = new Point(center.X + startOffset, center.Y + startOffset);
        var handleEnd = P(0.785, 0.785);

        Pen RingPen(Brush b) => new(b, ringWidth);
        // Extremo plano del lado del aro: uno redondeado invadiria el cristal
        Pen HandlePen(Brush b) => new(b, handleWidth) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Round };

        if (detailed)
        {
            // Sombra azul oscura desplazada hacia abajo a la derecha
            var shadow = new SolidColorBrush(Color.FromArgb(0x90, 0x1E, 0x3A, 0x8A));
            dc.PushTransform(new TranslateTransform(L(0.012), L(0.022)));
            dc.DrawEllipse(null, RingPen(shadow), center, ringRadius, ringRadius);
            dc.DrawLine(HandlePen(shadow), handleStart, handleEnd);
            dc.Pop();

            // Cristal: azul con la parte de arriba mas oscura (sombra del aro)
            double lens = ringRadius - ringWidth / 2;
            var lensBrush = new LinearGradientBrush(Color.FromRgb(0x2B, 0x5F, 0xD6), Color.FromRgb(0x4F, 0x8F, 0xF6), 90);
            dc.DrawEllipse(lensBrush, null, center, lens, lens);

            // Rayo ambar
            var bolt = new StreamGeometry();
            using (var g = bolt.Open())
            {
                g.BeginFigure(P(0.475, 0.275), true, true);
                g.PolyLineTo([P(0.335, 0.465), P(0.425, 0.465), P(0.395, 0.585), P(0.535, 0.385), P(0.445, 0.385)], true, true);
            }
            var boltBrush = new LinearGradientBrush(Color.FromRgb(0xFC, 0xD3, 0x4D), Color.FromRgb(0xF5, 0x9E, 0x0B), 90);
            dc.DrawGeometry(boltBrush, null, bolt);
        }

        dc.DrawEllipse(null, RingPen(Brushes.White), center, ringRadius, ringRadius);
        dc.DrawLine(HandlePen(Brushes.White), handleStart, handleEnd);
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
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
