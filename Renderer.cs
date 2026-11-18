using System;
using System.Threading.Tasks;

namespace SE2ColourableTextureMaker
{
    public enum ViewMode { Painted, Original, Albedo, Mask, Metal, Roughness, Normal, AO }

    /// <summary>A paint colour as the game stores it: hue, saturation, value 0..1. Null-paint = a block never painted.</summary>
    public struct Paint
    {
        public bool On;
        public double H, S, V;
        public Paint(double h, double s, double v) { On = true; H = h; S = s; V = v; }
    }

    /// <summary>
    /// Draws the texture the way SE2 paints it. The game's shader (SDK: Engine\Shaders\Common\Color.hlsli ColorizeGray,
    /// Geometry\Materials\PBRPixel.hlsl) does, per pixel, with mask m from the _add texture's blue channel:
    ///   grey  = max(R, G, B) of the colour texture (sRGB)            - hue and saturation are dropped
    ///   out   = HSV(paint H, paint S, clamp(grey + paint V - 0.55))   - the game shifts V by -0.55 (HSVDeltaColoring)
    ///   pixel = mix(texture, out, m) in linear light
    /// The lighting here is only a simple studio light so the relief and shine can be judged.
    /// </summary>
    public static class Renderer
    {
        static readonly double[] ToLin = new double[256];
        static readonly byte[] ToSrgb = new byte[4097];

        static Renderer()
        {
            for (int i = 0; i < 256; i++) { double c = i / 255.0; ToLin[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            for (int i = 0; i <= 4096; i++) { double c = i / 4096.0; double s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055; ToSrgb[i] = (byte)Math.Round(Imaging.Clamp01(s) * 255); }
        }

        static double LinFromSrgb(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static byte Out(double lin) => ToSrgb[(int)(Imaging.Clamp01(lin) * 4096)];

        public static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            h = h - Math.Floor(h);
            double px = Math.Abs((h + 1.0) % 1.0 * 6 - 3), py = Math.Abs((h + 2.0 / 3) % 1.0 * 6 - 3), pz = Math.Abs((h + 1.0 / 3) % 1.0 * 6 - 3);
            r = v * (1 + (Imaging.Clamp01(px - 1) - 1) * s);
            g = v * (1 + (Imaging.Clamp01(py - 1) - 1) * s);
            b = v * (1 + (Imaging.Clamp01(pz - 1) - 1) * s);
        }

        public static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max; s = max > 0 ? d / max : 0;
            if (d <= 1e-9) h = 0;
            else if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
            else if (max == g) h = ((b - r) / d + 2) / 6;
            else h = ((r - g) / d + 4) / 6;
        }

        /// <summary>The colour a flat texel of brightness `grey` shows under full mask (sRGB 0..1).</summary>
        public static void PaintedColour(double grey, Paint p, out double r, out double g, out double b)
        {
            double v = Imaging.Clamp01(grey + p.V - 0.55);
            HsvToRgb(p.H, Imaging.Clamp01(p.S), v, out r, out g, out b);
        }

        public static byte[] Render(Converted c, Rgba original, ViewMode mode, Paint paint, bool lit)
        {
            int n = c.W * c.H;
            var px = new byte[n * 4];
            switch (mode)
            {
                case ViewMode.Mask: return Grey(c.Mask, n);
                case ViewMode.Metal: return Grey(c.Metal, n);
                case ViewMode.Roughness: return Grey(c.Rough, n);
                case ViewMode.AO: return Grey(c.Ao, n);
                case ViewMode.Normal: Buffer.BlockCopy(c.Normal, 0, px, 0, n * 4); return px;
                case ViewMode.Albedo: Buffer.BlockCopy(c.Albedo, 0, px, 0, n * 4); return px;
            }
            bool useOriginal = mode == ViewMode.Original;
            byte[] alb = useOriginal ? original.D : c.Albedo;

            // light from the upper left, a little towards the viewer; viewer straight in front
            double lx = -0.45, ly = 0.55, lz = 0.70, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;
            double hx = lx, hy = ly, hz = lz + 1, hl = Math.Sqrt(hx * hx + hy * hy + hz * hz); hx /= hl; hy /= hl; hz /= hl;

            Parallel.For(0, c.H, y =>
            {
                for (int x = 0, i = y * c.W; x < c.W; x++, i++)
                {
                    int p = i * 4;
                    double r = ToLin[alb[p + 2]], g = ToLin[alb[p + 1]], b = ToLin[alb[p]];
                    if (!useOriginal && paint.On)
                    {
                        double m = c.Mask[i] / 255.0;
                        if (m > 0)
                        {
                            double grey = Math.Max(alb[p], Math.Max(alb[p + 1], alb[p + 2])) / 255.0;
                            PaintedColour(grey, paint, out double pr, out double pg, out double pb);
                            r += (LinFromSrgb(pr) - r) * m; g += (LinFromSrgb(pg) - g) * m; b += (LinFromSrgb(pb) - b) * m;
                        }
                    }
                    if (lit)
                    {
                        double nx = c.Normal[p + 2] / 127.5 - 1, ny = c.Normal[p + 1] / 127.5 - 1, nz = c.Normal[p] / 127.5 - 1;
                        double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (nl < 1e-6) { nx = 0; ny = 0; nz = 1; nl = 1; }
                        nx /= nl; ny /= nl; nz /= nl;
                        double metal = c.Metal[i] / 255.0, rough = Math.Max(0.04, c.Rough[i] / 255.0), ao = c.Ao[i] / 255.0;
                        double ndl = Math.Max(0, nx * lx + ny * ly + nz * lz);
                        double ndh = Math.Max(0, nx * hx + ny * hy + nz * hz);
                        double a2 = rough * rough * rough * rough;
                        double dd = ndh * ndh * (a2 - 1) + 1;
                        double spec = Math.Min(a2 / (Math.PI * dd * dd) * 0.25, 30) * ndl;
                        double env = 0.30 + 0.25 * (1 - rough);
                        double kd = 1 - metal;
                        double f0r = 0.04 + (r - 0.04) * metal, f0g = 0.04 + (g - 0.04) * metal, f0b = 0.04 + (b - 0.04) * metal;
                        double diff = ndl * 1.45 + 0.40 * ao;
                        r = r * kd * diff + f0r * (spec * 1.45 + env * ao);
                        g = g * kd * diff + f0g * (spec * 1.45 + env * ao);
                        b = b * kd * diff + f0b * (spec * 1.45 + env * ao);
                    }
                    px[p] = Out(b); px[p + 1] = Out(g); px[p + 2] = Out(r); px[p + 3] = 255;
                }
            });
            return px;
        }

        static byte[] Grey(byte[] d, int n)
        {
            var px = new byte[n * 4];
            for (int i = 0; i < n; i++) { byte v = d[i]; int o = i * 4; px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255; }
            return px;
        }
    }
}
