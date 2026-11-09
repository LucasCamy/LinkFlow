using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>
/// Prepara imagens para o LCD: copia para a biblioteca compartilhada (o serviço precisa ler),
/// converte formatos que o renderizador não abre (WebP, HEIC, TIFF), corrige a rotação EXIF de fotos
/// e reduz fotos grandes (o display tem 480x480).
/// </summary>
public static class LcdMediaImporter
{
    public const string DialogFilter =
        "Imagens e GIFs|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.heic|GIF animado|*.gif|Todos os arquivos|*.*";

    private static readonly HashSet<string> Accepted = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic"
    };

    private const int MaxSide = 960;

    public static bool IsSupported(string path) => Accepted.Contains(Path.GetExtension(path));

    public static Task<(List<(string Path, string Name)> Imported, List<string> Failed)> ImportAsync(IEnumerable<string> files) =>
        Task.Run(() =>
        {
            var ok = new List<(string, string)>();
            var failed = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    if (!IsSupported(file)) throw new NotSupportedException("formato não suportado");
                    ok.Add((Prepare(file), Path.GetFileName(file)));
                }
                catch (Exception ex)
                {
                    Log.Warn($"LCD: não foi possível importar {file}: {ex.Message}");
                    failed.Add(Path.GetFileName(file));
                }
            }
            return (ok, failed);
        });

    private static string Prepare(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();

        if (ext == ".gif")
        {
            // GIF mantém a animação: só valida e copia
            using var img = System.Drawing.Image.FromFile(file);
            return MediaLibrary.Import(file);
        }

        BitmapSource frame;
        ushort orientation = 1;
        using (var stream = File.OpenRead(file))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
            try
            {
                if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("/app1/ifd/{ushort=274}") is ushort o) orientation = o;
            }
            catch { /* sem EXIF */ }
        }

        bool nativeFormat = ext is ".png" or ".jpg" or ".jpeg" or ".bmp";
        bool large = Math.Max(frame.PixelWidth, frame.PixelHeight) > MaxSide * 1.25;
        if (nativeFormat && !large && orientation == 1) return MediaLibrary.Import(file);

        BitmapSource result = frame;
        double angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (angle != 0) result = new TransformedBitmap(result, new RotateTransform(angle));
        if (large)
        {
            double scale = (double)MaxSide / Math.Max(result.PixelWidth, result.PixelHeight);
            result = new TransformedBitmap(result, new ScaleTransform(scale, scale));
        }

        bool photo = ext is ".jpg" or ".jpeg" or ".heic";
        BitmapEncoder encoder = photo ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return MediaLibrary.Import(file, ms.ToArray(), photo ? ".jpg" : ".png");
    }
}
