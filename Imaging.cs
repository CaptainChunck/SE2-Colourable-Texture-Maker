using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SE2ColourableTextureMaker
{
    public enum MapKind { Albedo, Normal, Roughness, Metal, AO, Height, Mask }

    /// <summary>A 4-channel picture, bytes in B, G, R, A order.</summary>
    public sealed class Rgba
    {
        public int W, H;
        public byte[] D;
        public Rgba(int w, int h) { W = w; H = h; D = new byte[w * h * 4]; }
    }

    /// <summary>A one-channel picture.</summary>
    public sealed class Plane
    {
        public int W, H;
        public byte[] D;
        public Plane(int w, int h) { W = w; H = h; D = new byte[w * h]; }
    }

    /// <summary>The loaded PBR set, every map resampled to the albedo's size.</summary>
    public sealed class SourceSet
    {
        public Dictionary<MapKind, string> Files = new Dictionary<MapKind, string>();
        public bool NormalIsDirectX, RoughnessIsGloss;
        public string SuggestedName;
        public Rgba Albedo, Normal;
        public Plane Rough, Metal, Ao, Height, Mask;
        public int W => Albedo.W;
        public int H => Albedo.H;

        public SourceSet Downscaled(int maxSide)
        {
            int side = Math.Max(W, H);
            if (side <= maxSide) return this;
            double f = (double)maxSide / side;
            int w = Math.Max(1, (int)Math.Round(W * f)), h = Math.Max(1, (int)Math.Round(H * f));
            return new SourceSet
            {
                Files = Files, NormalIsDirectX = NormalIsDirectX, RoughnessIsGloss = RoughnessIsGloss, SuggestedName = SuggestedName,
                Albedo = Imaging.Resize(Albedo, w, h), Normal = Normal == null ? null : Imaging.Resize(Normal, w, h),
                Rough = Imaging.Resize(Rough, w, h), Metal = Imaging.Resize(Metal, w, h), Ao = Imaging.Resize(Ao, w, h),
                Height = Imaging.Resize(Height, w, h), Mask = Imaging.Resize(Mask, w, h),
            };
        }
    }

    public static class Imaging
    {
        public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

        static readonly string[] Skip = { "preview", "thumb", "thumbnail", "sphere", "cube", "render", "emissive", "emission", "opacity", "alpha", "transmission", "sss" };

        /// <summary>Which map a file is, from its name (case and separators do not matter).</summary>
        public static MapKind? Detect(string path, out bool directX, out bool gloss)
        {
            directX = false; gloss = false;
            string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var t = new HashSet<string>(Regex.Split(n, "[^a-z0-9]+").Where(s => s.Length > 0));
            bool Has(params string[] w) => w.Any(t.Contains);
            if (Has(Skip)) return null;
            if (Has("colormask", "colourmask", "paintmask")) return MapKind.Mask;
            if (Has("normal", "normals", "nrm", "nor", "norm", "nrml", "normalgl", "normaldx", "normalogl"))
            {
                directX = Has("dx", "directx", "normaldx", "d3d");
                return MapKind.Normal;
            }
            if (Has("height", "displacement", "disp", "bump", "heightmap", "displace")) return MapKind.Height;
            if (Has("ao", "ambientocclusion", "occlusion", "occ", "ambient")) return MapKind.AO;
            if (Has("gloss", "glossiness", "smoothness")) { gloss = true; return MapKind.Roughness; }
            if (Has("roughness", "rough", "rgh")) return MapKind.Roughness;
            if (Has("metallic", "metalness", "metal", "met", "mtl")) return MapKind.Metal;
            if (Has("albedo", "basecolor", "basecolour", "diffuse", "diff", "color", "colour", "col", "base", "alb")) return MapKind.Albedo;
            return null;
        }

        /// <summary>Finds the whole set: from several chosen files, or from one file / a folder (its siblings).</summary>
        public static Dictionary<MapKind, string> FindSet(IList<string> chosen, out bool directX, out bool gloss)
        {
            directX = false; gloss = false;
            var files = new List<string>();
            string anchor = null;
            if (chosen.Count == 1 && Directory.Exists(chosen[0]))
                files.AddRange(Directory.GetFiles(chosen[0]).Where(IsImage));
            else if (chosen.Count == 1 && File.Exists(chosen[0]))
            {
                anchor = chosen[0];
                files.AddRange(Directory.GetFiles(Path.GetDirectoryName(chosen[0])).Where(IsImage));
            }
            else files.AddRange(chosen.Where(f => File.Exists(f) && IsImage(f)));

            var found = new Dictionary<MapKind, string>();
            var score = new Dictionary<MapKind, int>();
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var k = Detect(f, out bool dx, out bool gl);
                if (k == null) continue;
                int s = anchor == null ? 0 : CommonPrefix(Path.GetFileName(f), Path.GetFileName(anchor));
                // prefer PNG over JPG of the same map, and the file sharing most of its name with the one picked
                s = s * 4 + (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 2 : 0) + (k == MapKind.Roughness && !gl ? 1 : 0);
                if (!found.ContainsKey(k.Value) || s > score[k.Value])
                {
                    found[k.Value] = f; score[k.Value] = s;
                    if (k == MapKind.Normal) directX = dx;
                    if (k == MapKind.Roughness) gloss = gl;
                }
            }
            return found;
        }

        static int CommonPrefix(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }

        public static bool IsImage(string f) => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant());

        public static BitmapSource LoadBitmap(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                BitmapSource f = dec.Frames[0];
                var conv = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                conv.Freeze();
                return conv;
            }
        }

        public static Rgba ToRgba(BitmapSource b)
        {
            var r = new Rgba(b.PixelWidth, b.PixelHeight);
            b.CopyPixels(r.D, r.W * 4, 0);
            return r;
        }

        /// <summary>One channel: the average of R, G, B (grey maps are equal in all three).</summary>
        public static Plane ToPlane(Rgba c)
        {
            var p = new Plane(c.W, c.H);
            Parallel.For(0, c.H, y =>
            {
                int o = y * c.W;
                for (int x = 0; x < c.W; x++)
                {
                    int i = (o + x) * 4;
                    p.D[o + x] = (byte)((c.D[i] + c.D[i + 1] + c.D[i + 2] + 1) / 3);
                }
            });
            return p;
        }

        public static Rgba LoadRgba(string path, int w = 0, int h = 0)
        {
            var c = ToRgba(LoadBitmap(path));
            return (w > 0 && (c.W != w || c.H != h)) ? Resize(c, w, h) : c;
        }

        public static Plane LoadPlane(string path, int w, int h) => ToPlane(LoadRgba(path, w, h));

        /// <summary>Area-average when shrinking, bilinear when growing.</summary>
        public static Rgba Resize(Rgba s, int w, int h)
        {
            if (s == null) return null;
            if (s.W == w && s.H == h) return s;
            var d = new Rgba(w, h);
            Resample(s.D, s.W, s.H, 4, d.D, w, h);
            return d;
        }

        public static Plane Resize(Plane s, int w, int h)
        {
            if (s == null) return null;
            if (s.W == w && s.H == h) return s;
            var d = new Plane(w, h);
            Resample(s.D, s.W, s.H, 1, d.D, w, h);
            return d;
        }

        static void Resample(byte[] src, int sw, int sh, int ch, byte[] dst, int dw, int dh)
        {
            double fx = (double)sw / dw, fy = (double)sh / dh;
            if (fx >= 1 && fy >= 1)
            {
                Parallel.For(0, dh, y =>
                {
                    int y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, (int)((y + 1) * fy));
                    var acc = new double[ch];
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, (int)((x + 1) * fx));
                        Array.Clear(acc, 0, ch);
                        int n = 0;
                        for (int yy = y0; yy < y1 && yy < sh; yy++)
                            for (int xx = x0; xx < x1 && xx < sw; xx++)
                            {
                                int i = (yy * sw + xx) * ch;
                                for (int c = 0; c < ch; c++) acc[c] += src[i + c];
                                n++;
                            }
                        int o = (y * dw + x) * ch;
                        for (int c = 0; c < ch; c++) dst[o + c] = (byte)(acc[c] / Math.Max(1, n) + 0.5);
                    }
                });
                return;
            }
            Parallel.For(0, dh, y =>
            {
                double sy = (y + 0.5) * fy - 0.5;
                int y0 = (int)Math.Floor(sy); double ty = sy - y0;
                int ya = Clamp(y0, 0, sh - 1), yb = Clamp(y0 + 1, 0, sh - 1);
                for (int x = 0; x < dw; x++)
                {
                    double sx = (x + 0.5) * fx - 0.5;
                    int x0 = (int)Math.Floor(sx); double tx = sx - x0;
                    int xa = Clamp(x0, 0, sw - 1), xb = Clamp(x0 + 1, 0, sw - 1);
                    int o = (y * dw + x) * ch;
                    for (int c = 0; c < ch; c++)
                    {
                        double a = src[(ya * sw + xa) * ch + c] * (1 - tx) + src[(ya * sw + xb) * ch + c] * tx;
                        double b = src[(yb * sw + xa) * ch + c] * (1 - tx) + src[(yb * sw + xb) * ch + c] * tx;
                        dst[o + c] = (byte)(a * (1 - ty) + b * ty + 0.5);
                    }
                }
            });
        }

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>Loads a whole set. The albedo decides the size; a missing map is left null (defaults are used).</summary>
        public static SourceSet LoadSet(Dictionary<MapKind, string> files, bool directX, bool gloss)
        {
            if (!files.ContainsKey(MapKind.Albedo)) throw new InvalidOperationException("No colour (albedo / base colour) map was found. Its file name must contain albedo, basecolor, diffuse or color.");
            var s = new SourceSet { Files = files, NormalIsDirectX = directX, RoughnessIsGloss = gloss };
            s.Albedo = LoadRgba(files[MapKind.Albedo]);
            int w = s.Albedo.W, h = s.Albedo.H;
            if (files.TryGetValue(MapKind.Normal, out var f)) s.Normal = LoadRgba(f, w, h);
            if (files.TryGetValue(MapKind.Roughness, out f)) s.Rough = LoadPlane(f, w, h);
            if (files.TryGetValue(MapKind.Metal, out f)) s.Metal = LoadPlane(f, w, h);
            if (files.TryGetValue(MapKind.AO, out f)) s.Ao = LoadPlane(f, w, h);
            if (files.TryGetValue(MapKind.Height, out f)) s.Height = LoadPlane(f, w, h);
            if (files.TryGetValue(MapKind.Mask, out f)) s.Mask = LoadPlane(f, w, h);
            s.SuggestedName = SuggestName(files[MapKind.Albedo]);
            return s;
        }

        /// <summary>"armor-plating1_albedo.png" in "armor-plating1-bl" -> "ArmorPlating1Colorable".</summary>
        public static string SuggestName(string albedoPath)
        {
            string n = Path.GetFileNameWithoutExtension(albedoPath);
            n = Regex.Replace(n, "(?i)[-_ .]*(albedo|basecolou?r|base_colou?r|diffuse|diff|colou?r|col|alb)([-_ .].*)?$", "");
            if (n.Length == 0) n = new DirectoryInfo(Path.GetDirectoryName(albedoPath)).Name;
            n = Regex.Replace(n, "(?i)[-_ ](bl|\\d+k|jpg|png|4k|2k|1k|8k)$", "");
            var parts = Regex.Split(n, "[^A-Za-z0-9]+").Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1));
            string name = string.Concat(parts);
            if (name.Length == 0) name = "MyMaterial";
            if (char.IsDigit(name[0])) name = "M" + name;
            return name + "Colorable";
        }

        public static BitmapSource ToBitmap(byte[] bgra, int w, int h)
        {
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            b.Freeze();
            return b;
        }

        /// <summary>Writes an 8-bit RGB PNG with no colour profile (a profile blocks publishing in the SDK).</summary>
        public static void SavePngRgb(string path, byte[] bgra, int w, int h)
        {
            var rgb = new byte[w * h * 3];
            for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3) { rgb[j] = bgra[i]; rgb[j + 1] = bgra[i + 1]; rgb[j + 2] = bgra[i + 2]; }
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
            var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
            enc.Frames.Add(BitmapFrame.Create(b));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                File.WriteAllBytes(path, PlainPng(ms.ToArray()));
            }
        }

        /// <summary>Keeps only the picture chunks (IHDR, IDAT, IEND): no colour profile, gamma or sRGB tags.</summary>
        static byte[] PlainPng(byte[] png)
        {
            using (var o = new MemoryStream())
            {
                o.Write(png, 0, 8);
                int i = 8;
                while (i + 8 <= png.Length)
                {
                    int len = (png[i] << 24) | (png[i + 1] << 16) | (png[i + 2] << 8) | png[i + 3];
                    string type = System.Text.Encoding.ASCII.GetString(png, i + 4, 4);
                    if (type == "IHDR" || type == "IDAT" || type == "IEND") o.Write(png, i, len + 12);
                    i += len + 12;
                }
                return o.ToArray();
            }
        }

        public static void SavePngGrey(string path, byte[] grey, int w, int h)
        {
            var bgra = new byte[w * h * 4];
            for (int i = 0; i < grey.Length; i++) { byte v = grey[i]; int o = i * 4; bgra[o] = v; bgra[o + 1] = v; bgra[o + 2] = v; bgra[o + 3] = 255; }
            SavePngRgb(path, bgra, w, h);
        }
    }
}
