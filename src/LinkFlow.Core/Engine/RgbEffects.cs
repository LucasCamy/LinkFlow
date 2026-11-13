namespace LinkFlow.Core;

/// <summary>
/// Efeitos de iluminação calculados no PC. A cor depende só do instante (relógio do sistema, o mesmo
/// no serviço e na interface) e da configuração, então todos os dispositivos — inclusive os periféricos
/// aplicados pela interface — ficam no mesmo compasso sem trocar mensagens.
/// </summary>
public static class RgbEffects
{
    public const int MaxPalette = 8;

    /// <summary>Milissegundos desde que o Windows ligou (igual em todos os processos).</summary>
    public static long Now => Environment.TickCount64;

    /// <summary>Paletas prontas.</summary>
    public static readonly IReadOnlyList<(string Name, string[] Colors)> Presets = new (string, string[])[]
    {
        ("Neon", new[] { "#00F0FF", "#7C3AED", "#FF2D95" }),
        ("Pôr do sol", new[] { "#FF5E00", "#FF2D55", "#9B00FF" }),
        ("Oceano", new[] { "#0047FF", "#00C2FF", "#00FFB3" }),
        ("Fogo", new[] { "#FF1A00", "#FF6A00", "#FFB300" }),
        ("Floresta", new[] { "#00FF3C", "#7DFF00", "#00B37A" }),
        ("Aurora", new[] { "#00FF9D", "#00B3FF", "#B400FF", "#FF00C8" }),
        ("Gelo", new[] { "#FFFFFF", "#9EE7FF", "#2D7CFF" }),
        ("Cyber", new[] { "#FFE600", "#FF00A8", "#00E5FF" }),
    };

    /// <summary>
    /// Cor do efeito no instante dado. <paramref name="phase"/> (0–1) desloca o ciclo — usado para espalhar
    /// o efeito pelas ventoinhas. <c>Level</c> multiplica o brilho (efeito respirar).
    /// </summary>
    public static (byte R, byte G, byte B, double Level) ColorAt(RgbConfig cfg, long timeMs, double phase = 0)
    {
        double speed = (Math.Clamp((int)cfg.EffectSpeed, 1, 10) - 1) / 9.0;
        switch (cfg.Effect)
        {
            case RgbEffect.Breathing:
            {
                double pos = Frac(timeMs / Period(speed, 7000, 1100) + phase);
                double level = 0.06 + 0.94 * Smooth(0.5 - 0.5 * Math.Cos(pos * Math.PI * 2));
                return (cfg.R, cfg.G, cfg.B, level);
            }
            case RgbEffect.Rainbow:
            {
                double hue = Frac(timeMs / Period(speed, 40000, 2500) + phase) * 360;
                var (r, g, b) = FromHsv(hue, 1, 1);
                return (r, g, b, 1);
            }
            case RgbEffect.Palette:
            {
                var colors = Colors(cfg);
                if (colors.Count == 1) return (colors[0].R, colors[0].G, colors[0].B, 1);
                double pos = timeMs / Period(speed, 8000, 700) + phase * colors.Count;
                long slot = (long)Math.Floor(pos);
                double t = Fade(Frac(pos), 0.35);
                var a = colors[(int)Mod(slot, colors.Count)];
                var z = colors[(int)Mod(slot + 1, colors.Count)];
                return (Lerp(a.R, z.R, t), Lerp(a.G, z.G, t), Lerp(a.B, z.B, t), 1);
            }
            case RgbEffect.Random:
            {
                // Cada ventoinha (fase) tem sua sequência própria e troca em momentos diferentes
                uint seed = (uint)Math.Round(phase * 1000);
                double pos = timeMs / Period(speed, 10000, 800) + phase;
                long slot = (long)Math.Floor(pos);
                double t = Fade(Frac(pos), 0.6);
                var a = FromHsv(RandomHue(slot, seed), 1, 1);
                var z = FromHsv(RandomHue(slot + 1, seed), 1, 1);
                return (Lerp(a.R, z.R, t), Lerp(a.G, z.G, t), Lerp(a.B, z.B, t), 1);
            }
            default:
                return (cfg.R, cfg.G, cfg.B, 1);
        }
    }

    /// <summary>Cor da tela (sem gama) para a prévia: já com o nível do respirar aplicado.</summary>
    public static (byte R, byte G, byte B) PreviewAt(RgbConfig cfg, long timeMs, double phase = 0)
    {
        var (r, g, b, level) = ColorAt(cfg, timeMs, phase);
        double k = Math.Clamp(level, 0, 1) * Math.Clamp(cfg.Brightness / 100.0, 0.15, 1);
        return ((byte)(r * k), (byte)(g * k), (byte)(b * k));
    }

    /// <summary>
    /// Fase de cada LED da corrente do hub quando o efeito é espalhado: o arco-íris e a paleta formam
    /// uma onda contínua; no aleatório cada ventoinha inteira recebe a sua cor.
    /// </summary>
    public static double LedPhase(RgbEffect effect, int led, int ledsPerDevice, int devices)
    {
        devices = Math.Max(devices, 1);
        int device = Math.Min(led / Math.Max(ledsPerDevice, 1), devices - 1);
        if (effect == RgbEffect.Random || effect == RgbEffect.Breathing) return (double)device / devices;
        return Math.Min((double)led / (ledsPerDevice * devices), 0.9999);
    }

    public static bool TryParseHex(string? hex, out (byte R, byte G, byte B) color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // ------------------------------------------------------------------ apoio

    private static List<(byte R, byte G, byte B)> Colors(RgbConfig cfg)
    {
        var list = new List<(byte, byte, byte)>(cfg.Palette?.Count ?? 0);
        if (cfg.Palette != null)
            foreach (var h in cfg.Palette)
                if (TryParseHex(h, out var c)) list.Add(c);
        if (list.Count == 0) list.Add((cfg.R, cfg.G, cfg.B));
        return list;
    }

    /// <summary>Duração de um ciclo: interpolação exponencial entre lento e rápido.</summary>
    private static double Period(double speed, double slowMs, double fastMs) => slowMs * Math.Pow(fastMs / slowMs, speed);

    /// <summary>Segura a cor por <paramref name="hold"/> do tempo e depois faz a transição suave.</summary>
    private static double Fade(double f, double hold) => f <= hold ? 0 : Smooth((f - hold) / (1 - hold));

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static double Frac(double x) => x - Math.Floor(x);

    private static long Mod(long a, int n) => ((a % n) + n) % n;

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    /// <summary>Matiz "aleatório" determinístico: sequência de razão áurea com variação, nunca repete a anterior.</summary>
    private static double RandomHue(long slot, uint seed)
    {
        double jitter = Hash((uint)slot * 2654435761u ^ seed * 40503u) / (double)uint.MaxValue;
        double offset = Hash(seed * 2246822519u + 7) / (double)uint.MaxValue;
        return Frac(slot * 0.381966 + jitter * 0.22 + offset) * 360;
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }
}
