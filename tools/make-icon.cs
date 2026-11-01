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

static byte[] Render(int size)
{
    double s = size;
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        // Fondo: cuadrado redondeado azul con degradado vertical
        var background = new LinearGradientBrush(Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x1D, 0x4E, 0xD8), 90);
        double margin = s * 0.03;
        double radius = s * 0.22;
        dc.DrawRoundedRectangle(background, null, new Rect(margin, margin, s - 2 * margin, s - 2 * margin), radius, radius);

        // Lupa blanca: aro y mango a 45 grados
        double stroke = Math.Max(1.6, s * 0.105);
        var ring = new Pen(Brushes.White, stroke);
        var center = new Point(s * 0.43, s * 0.43);
        double r = s * 0.205;
        dc.DrawEllipse(null, ring, center, r, r);

        var handle = new Pen(Brushes.White, stroke * 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double offset = (r + stroke * 0.45) * Math.Sqrt(0.5);
        dc.DrawLine(handle, new Point(center.X + offset, center.Y + offset), new Point(s * 0.77, s * 0.77));

        // Brillo en el cristal (solo donde hay pixeles suficientes para que se vea)
        if (size >= 32)
        {
            var glint = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), stroke * 0.45)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            double gr = r * 0.55;
            var arc = new PathGeometry([new PathFigure(
                new Point(center.X - gr, center.Y),
                [new ArcSegment(new Point(center.X, center.Y - gr), new Size(gr, gr), 0, false, SweepDirection.Clockwise, true)],
                false)]);
            dc.DrawGeometry(null, glint, arc);
        }
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
