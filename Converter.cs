using System;
using System.Threading.Tasks;

namespace SE2ColourableTextureMaker
{
    public enum MaskMode { Whole, Bright, Low, Custom, None }

    public sealed class Settings
    {
        public MaskMode Mask = MaskMode.Whole;
        public double Threshold = 0.5;   // 0..1
        public double Softness = 0.03;   // half width of the soft edge, 0..0.5
        public bool Swap;
        public bool MatchBrightness = true;
        public double TargetGrey = 128 / 255.0;   // Keen's PaintedMetalColorable albedo average
        public bool LowerMetal = true;
        public double MetalTarget = 0.55;         // Keen's painted metal is about 0.55
        public bool FlipGreen;                    // DirectX normal map -> OpenGL (what SE2 uses)
        public Plane CustomMask;
        public Settings Clone() => (Settings)MemberwiseClone();
    }

    /// <summary>The converted SE2 texture set, ready to save or preview.</summary>
    public sealed class Converted
    {
        public int W, H;
        public byte[] Albedo, Normal;           // BGRA
        public byte[] Metal, Rough, Ao, Mask;   // one channel
        public double GreyBefore, GreyAfter, PaintFraction, MetalBefore, MetalAfter;
        public string Note;
    }

    public static class Converter
    {
        public static Converted Run(SourceSet s, Settings st)
        {
            int w = s.W, h = s.H, n = w * h;
            var o = new Converted { W = w, H = h };
            var a = s.Albedo.D;

            // The game keeps only the HSV value (the brightest of R, G, B) of the texture under the mask.
            var grey = new float[n];
            Parallel.For(0, h, y =>
            {
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    int p = i * 4;
                    grey[i] = Math.Max(a[p], Math.Max(a[p + 1], a[p + 2])) / 255f;
                }
            });

            // ---- where the paint goes
            var m = new float[n];
            Plane src = null;
            bool invert = false;
            switch (st.Mask)
            {
                case MaskMode.Whole: Fill(m, 1f); break;
                case MaskMode.None: break;
                case MaskMode.Bright: break;
                case MaskMode.Low:
                    src = s.Height;
                    invert = true;
                    if (src == null) o.Note = "No height map in this set: the low parts are worked out from the colour's brightness instead.";
                    break;
                case MaskMode.Custom:
                    src = st.CustomMask != null ? Imaging.Resize(st.CustomMask, w, h) : s.Mask;
                    if (src == null) { Fill(m, 1f); o.Note = "No mask picture loaded yet: the whole texture takes paint."; }
                    break;
            }
            if (st.Mask == MaskMode.Bright || st.Mask == MaskMode.Low || (st.Mask == MaskMode.Custom && src != null))
            {
                double lo = st.Threshold - st.Softness, hi = st.Threshold + st.Softness;
                bool custom = st.Mask == MaskMode.Custom;
                bool inv = invert ^ st.Swap;
                Parallel.For(0, h, y =>
                {
                    for (int x = 0, i = y * w; x < w; x++, i++)
                    {
                        double v = src != null ? src.D[i] / 255.0 : grey[i];
                        double t = custom ? v : (hi - lo < 1e-6 ? (v >= st.Threshold ? 1 : 0) : Smooth(lo, hi, v));
                        m[i] = (float)(inv ? 1 - t : t);
                    }
                });
            }
            else if (st.Swap && st.Mask != MaskMode.None) for (int i = 0; i < n; i++) m[i] = 1 - m[i];

            double msum = 0;
            for (int i = 0; i < n; i++) msum += m[i];
            o.PaintFraction = msum / n;

            // ---- brightness of the paintable parts: a curve (no clipping) that brings their average to the target
            double before = WeightedMean(grey, m, 1.0);
            o.GreyBefore = before;
            double k = 1.0;
            if (st.MatchBrightness && msum > n * 0.001 && before > 0.002 && before < 0.998)
            {
                double lo = 0.05, hi = 20;
                for (int it = 0; it < 40; it++)
                {
                    double mid = Math.Sqrt(lo * hi);
                    if (WeightedMean(grey, m, mid, 3) > st.TargetGrey) lo = mid; else hi = mid;
                }
                k = Math.Sqrt(lo * hi);
            }
            o.GreyAfter = msum > 0 ? WeightedMean(grey, m, k) : before;

            o.Albedo = new byte[n * 4];
            var outA = o.Albedo;
            Parallel.For(0, h, y =>
            {
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    int p = i * 4;
                    float mm = m[i];
                    double g = Math.Pow(grey[i], k) * 255.0;
                    outA[p] = (byte)(a[p] * (1 - mm) + g * mm + 0.5);
                    outA[p + 1] = (byte)(a[p + 1] * (1 - mm) + g * mm + 0.5);
                    outA[p + 2] = (byte)(a[p + 2] * (1 - mm) + g * mm + 0.5);
                    outA[p + 3] = 255;
                }
            });

            // ---- metalness: only ever lowered, and only where paint goes
            o.Metal = new byte[n];
            if (s.Metal != null)
            {
                double mb = 0;
                for (int i = 0; i < n; i++) mb += s.Metal.D[i] / 255.0 * m[i];
                mb = msum > 0 ? mb / msum : 0;
                o.MetalBefore = mb;
                double scale = (st.LowerMetal && mb > st.MetalTarget && mb > 0.001) ? st.MetalTarget / mb : 1.0;
                double ma = 0;
                for (int i = 0; i < n; i++)
                {
                    double v0 = s.Metal.D[i];
                    double v = v0 * (1 - m[i]) + v0 * scale * m[i];
                    o.Metal[i] = (byte)(v + 0.5);
                    ma += v / 255.0 * m[i];
                }
                o.MetalAfter = msum > 0 ? ma / msum : 0;
            }

            // ---- roughness (gloss is turned round), AO, normal
            o.Rough = new byte[n];
            if (s.Rough != null) for (int i = 0; i < n; i++) o.Rough[i] = s.RoughnessIsGloss ? (byte)(255 - s.Rough.D[i]) : s.Rough.D[i];
            else Fill(o.Rough, (byte)128);
            o.Ao = new byte[n];
            if (s.Ao != null) Buffer.BlockCopy(s.Ao.D, 0, o.Ao, 0, n); else Fill(o.Ao, (byte)255);
            o.Normal = new byte[n * 4];
            if (s.Normal != null)
            {
                var nd = s.Normal.D;
                for (int i = 0; i < n * 4; i += 4)
                {
                    o.Normal[i] = nd[i];
                    o.Normal[i + 1] = st.FlipGreen ? (byte)(255 - nd[i + 1]) : nd[i + 1];
                    o.Normal[i + 2] = nd[i + 2];
                    o.Normal[i + 3] = 255;
                }
            }
            else for (int i = 0; i < n * 4; i += 4) { o.Normal[i] = 255; o.Normal[i + 1] = 128; o.Normal[i + 2] = 128; o.Normal[i + 3] = 255; }

            o.Mask = new byte[n];
            for (int i = 0; i < n; i++) o.Mask[i] = (byte)(m[i] * 255 + 0.5);
            return o;
        }

        static double WeightedMean(float[] g, float[] m, double k, int step = 1)
        {
            double s = 0, ws = 0;
            for (int i = 0; i < g.Length; i += step)
            {
                if (m[i] <= 0) continue;
                s += Math.Pow(g[i], k) * m[i];
                ws += m[i];
            }
            return ws > 0 ? s / ws : 0;
        }

        static double Smooth(double e0, double e1, double x)
        {
            double t = Imaging.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }

        static void Fill(float[] a, float v) { for (int i = 0; i < a.Length; i++) a[i] = v; }
        static void Fill(byte[] a, byte v) { for (int i = 0; i < a.Length; i++) a[i] = v; }
    }
}
