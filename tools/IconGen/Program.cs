// Usage: IconGen <small.svg> <full.svg> <out dir>
// Writes app.ico (16–256 px; the small design up to 32 px, the full one above) plus PNGs for each size.
using SkiaSharp;
using Svg.Skia;

var (small, full, outDir) = (args[0], args[1], Directory.CreateDirectory(args[2]).FullName);
int[] sizes = [16, 24, 32, 48, 64, 128, 256];

var pngs = new List<(int Size, byte[] Png)>();
foreach (var size in sizes)
{
    var png = Render(size <= 32 ? small : full, size);
    File.WriteAllBytes(Path.Combine(outDir, $"app-{size}.png"), png);
    pngs.Add((size, png));
}
File.WriteAllBytes(Path.Combine(outDir, "app-512.png"), Render(full, 512));

// ICO = header + one 16-byte entry per image + the PNGs themselves (PNG-in-ICO, supported since Vista)
using var ico = new MemoryStream();
using (var w = new BinaryWriter(ico, System.Text.Encoding.ASCII, leaveOpen: true))
{
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)pngs.Count);
    int offset = 6 + 16 * pngs.Count;
    foreach (var (size, png) in pngs)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in pngs) w.Write(png);
}
File.WriteAllBytes(Path.Combine(outDir, "app.ico"), ico.ToArray());
Console.WriteLine($"app.ico ({ico.Length / 1024} KB) + PNGs in {outDir}");

static byte[] Render(string svgPath, int size)
{
    using var svg = new SKSvg();
    var pic = svg.Load(svgPath) ?? throw new InvalidDataException("can't read " + svgPath);
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Scale(size / pic.CullRect.Width, size / pic.CullRect.Height);
    c.DrawPicture(pic);
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}
