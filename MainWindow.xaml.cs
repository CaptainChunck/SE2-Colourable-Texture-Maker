using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SE2ColourableTextureMaker
{
    public partial class MainWindow : Window
    {
        const int PreviewSide = 1024;

        // SE2's own starting palette (Vanilla\System\Configurations\PaintToolConfiguration.def, DefaultColors, H S V)
        static readonly double[,] SE2Palette =
        {
            { 0, 0, 0.45 }, { 0, 0, 0.65 }, { 0, 0.80, 0.50 }, { 0, 0.95, 0.70 }, { 0.3333, 0.32, 0.20 }, { 0.3333, 0.47, 0.40 }, { 0.575, 0.80, 0.45 },
            { 0.575, 0.95, 0.65 }, { 0.1222, 0.70, 0.71 }, { 0.1222, 0.85, 0.91 }, { 0, 0, 0.85 }, { 0, 0, 1.0 }, { 0, 0, 0 }, { 0, 0, 0.15 },
        };

        SourceSet full, small;
        Converted conv;
        readonly Settings st = new Settings();
        Paint paint = new Paint(0.575, 0.95, 0.65);
        ViewMode view = ViewMode.Painted;
        bool lit = true, quiet, ready;
        int convertGen, renderGen;
        readonly DispatcherTimer convertTimer, renderTimer;
        string lastFolder;

        public MainWindow()
        {
            InitializeComponent();
            convertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            convertTimer.Tick += (s, e) => { convertTimer.Stop(); StartConvert(); };
            renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            renderTimer.Tick += (s, e) => { renderTimer.Stop(); StartRender(); };
            Stars.Fill = new ImageBrush(MakeStars(1920, 1080)) { Stretch = Stretch.UniformToFill };
            BuildPalette();
            SetHsvSliders(paint);
            UpdateMapList();
            UpdateMaskUi();
            Drop += OnDrop;
            DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            StateChanged += (s, e) => MaxIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized ? "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z" : "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z");
            ready = true;
        }

        /// <summary>Off-screen check: load, convert, render each asked view into a picture of the whole window, save the set.</summary>
        public static async Task SelfTest(string map, string outDir, string[] views)
        {
            Directory.CreateDirectory(outDir);
            var w = new MainWindow();
            await w.LoadPaths(new List<string> { map });
            while (w.conv == null) await Task.Delay(50);
            if (views.Length == 0) views = new[] { "Painted" };
            foreach (var v in views)
            {
                if (v.StartsWith("mask="))
                {
                    string want = v.Substring(5);
                    foreach (var rb in FindAll<RadioButton>(w.Content as DependencyObject)) if (rb.GroupName == "mask") rb.IsChecked = (string)rb.Tag == want;
                    w.st.Mask = (MaskMode)Enum.Parse(typeof(MaskMode), want);
                    int cg = w.convertGen; w.StartConvert();
                    while (w.convertGen == cg) await Task.Delay(20);
                    await Task.Delay(1500);
                    continue;
                }
                var parts = v.Split(':');
                w.view = (ViewMode)Enum.Parse(typeof(ViewMode), parts[0]);
                if (parts.Length > 1) { int k = int.Parse(parts[1]); foreach (RadioButton rb in w.Palette.Children) if (rb.Tag is Paint) { if (--k == 0) rb.IsChecked = true; } }
                if (parts.Length > 2) { foreach (RadioButton rb in w.Palette.Children.OfType<RadioButton>()) rb.IsChecked = false; w.paint = new Paint { On = false }; }
                foreach (RadioButton rb in w.ViewTabs.Children) rb.IsChecked = (string)rb.Tag == parts[0];
                int gen = w.renderGen;
                w.StartRender();
                while (w.renderGen == gen || w.PreviewRect.Fill == null) await Task.Delay(50);
                await Task.Delay(400);
                var root = (FrameworkElement)w.Content;
                root.Measure(new Size(1480, 920)); root.Arrange(new Rect(0, 0, 1480, 920)); root.UpdateLayout();
                await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var rtb = new RenderTargetBitmap(1480, 920, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(outDir, "ui_" + v.Replace(':', '_') + ".png"))) enc.Save(fs);
            }
            var set = w.full;
            var c = Converter.Run(set, w.st.Clone());
            string name = w.NameBox.Text, dir = Path.Combine(outDir, name);
            Directory.CreateDirectory(dir);
            Imaging.SavePngRgb(Path.Combine(dir, name + "_albedo.png"), c.Albedo, c.W, c.H);
            Imaging.SavePngGrey(Path.Combine(dir, name + "_colormask.png"), c.Mask, c.W, c.H);
            Imaging.SavePngRgb(Path.Combine(dir, name + "_normal.png"), c.Normal, c.W, c.H);
            File.WriteAllText(Path.Combine(dir, "How to use in the SE2 SDK.txt"), Readme(name, c, w.st));
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"), string.Join(Environment.NewLine, $"name {name}", $"size {c.W}x{c.H}", $"paint {c.PaintFraction:0.000}", $"grey {c.GreyBefore * 255:0.0} -> {c.GreyAfter * 255:0.0}", $"metal {c.MetalBefore:0.000} -> {c.MetalAfter:0.000}", w.StatsText.Text));
        }

        static IEnumerable<T> FindAll<T>(DependencyObject d) where T : DependencyObject
        {
            if (d == null) yield break;
            if (d is T t) yield return t;
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                foreach (var x in FindAll<T>(c)) yield return x;
        }

        // ------------------------------------------------------------------ backdrop
        static BitmapSource MakeStars(int w, int h)
        {
            var px = new byte[w * h * 4];
            var r = new Random(7);
            for (int i = 0; i < px.Length; i += 4) { px[i] = 14; px[i + 1] = 10; px[i + 2] = 6; px[i + 3] = 255; }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // soft cloud bands, like the menu's nebula backdrop
                    double c = 0.5 + 0.5 * Math.Sin(x * 0.0021 + Math.Sin(y * 0.003) * 2.1) * Math.Cos(y * 0.0027 + Math.Sin(x * 0.0017) * 1.7);
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(14 + c * 22); px[o + 1] = (byte)(10 + c * 16); px[o + 2] = (byte)(6 + c * 9);
                }
            for (int k = 0; k < 2600; k++)
            {
                int x = r.Next(w), y = r.Next(h);
                double b = Math.Pow(r.NextDouble(), 3);
                int v = (int)(90 + 165 * b);
                Put(px, w, h, x, y, v, r.NextDouble() < 0.15);
                if (b > 0.75) { Put(px, w, h, x + 1, y, v / 2, false); Put(px, w, h, x - 1, y, v / 2, false); Put(px, w, h, x, y + 1, v / 2, false); Put(px, w, h, x, y - 1, v / 2, false); }
            }
            return Imaging.ToBitmap(px, w, h);
        }

        static void Put(byte[] px, int w, int h, int x, int y, int v, bool warm)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int o = (y * w + x) * 4;
            px[o] = (byte)Math.Max(px[o], warm ? v * 0.8 : v);
            px[o + 1] = (byte)Math.Max(px[o + 1], v * 0.95);
            px[o + 2] = (byte)Math.Max(px[o + 2], warm ? v : v * 0.9);
        }

        // ------------------------------------------------------------------ palette
        void BuildPalette()
        {
            Palette.Children.Clear();
            var none = new RadioButton { Style = (Style)FindResource("Swatch"), GroupName = "pal", Background = new SolidColorBrush(Color.FromRgb(10, 20, 24)), ToolTip = "Unpainted: a block that was never painted shows the texture as it is" };
            none.Content = new TextBlock { Text = "✕", FontSize = 15, Foreground = (Brush)FindResource("TextDim") };
            none.Checked += (s, e) => { paint.On = false; Repaint(); };
            Palette.Children.Add(none);
            for (int i = 0; i < SE2Palette.GetLength(0); i++)
            {
                var p = new Paint(SE2Palette[i, 0], SE2Palette[i, 1], SE2Palette[i, 2]);
                Renderer.HsvToRgb(p.H, p.S, p.V, out double r, out double g, out double b);
                var sw = new RadioButton { Style = (Style)FindResource("Swatch"), GroupName = "pal", Tag = p, Background = new SolidColorBrush(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255))), ToolTip = "SE2 palette colour " + (i + 1) };
                sw.Checked += (s, e) => { var q = (Paint)((RadioButton)s).Tag; paint = q; SetHsvSliders(q); Repaint(); };
                Palette.Children.Add(sw);
                if (i == 7) sw.IsChecked = true;
            }
        }

        void SetHsvSliders(Paint p)
        {
            quiet = true;
            HSlider.Value = p.H * 360; SSlider.Value = p.S * 100; VSlider.Value = p.V * 100;
            quiet = false;
        }

        void Hsv_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (quiet || !ready) return;
            paint = new Paint(HSlider.Value / 360, SSlider.Value / 100, VSlider.Value / 100);
            foreach (RadioButton rb in Palette.Children) if (rb.IsChecked == true) { quiet = true; rb.IsChecked = false; quiet = false; }
            Repaint();
        }

        void Repaint()
        {
            UpdateSwatches();
            if (view == ViewMode.Painted) renderTimer.Start();
            UpdateCaption();
        }

        void UpdateSwatches()
        {
            SwatchKeen.Background = new SolidColorBrush(PaintedSwatch(128 / 255.0));
            SwatchMine.Background = new SolidColorBrush(PaintedSwatch(conv != null ? conv.GreyAfter : 128 / 255.0));
        }

        Color PaintedSwatch(double grey)
        {
            if (!paint.On) { byte v = (byte)(grey * 255); return Color.FromRgb(v, v, v); }
            Renderer.PaintedColour(grey, paint, out double r, out double g, out double b);
            return Color.FromRgb((byte)(r * 255 + 0.5), (byte)(g * 255 + 0.5), (byte)(b * 255 + 0.5));
        }

        // ------------------------------------------------------------------ loading
        async void Load_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick a PBR texture (any one of its maps - the others are found in the same folder)",
                Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
                Multiselect = true,
                InitialDirectory = lastFolder ?? "",
            };
            if (dlg.ShowDialog(this) == true) await LoadPaths(dlg.FileNames.ToList());
        }

        async void LoadFolder_Click(object sender, RoutedEventArgs e)
        {
            string f = FolderPicker.Pick(new WindowInteropHelper(this).Handle, "Pick the folder that holds the PBR texture", lastFolder, "Load this folder");
            if (f != null) await LoadPaths(new List<string> { f });
        }

        async void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) await LoadPaths(files.ToList());
        }

        public async Task LoadPaths(List<string> paths)
        {
            var found = Imaging.FindSet(paths, out bool dx, out bool gloss);
            if (!found.ContainsKey(MapKind.Albedo))
            {
                MessageBox.Show(this, "No colour map was found.\n\nThe colour (albedo) file's name must contain albedo, basecolor, diffuse or color, for example  steel_albedo.png  or  Metal012_Color.jpg.", "Load PBR texture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetBusy("LOADING…");
            try
            {
                var loaded = await Task.Run(() => { var s = Imaging.LoadSet(found, dx, gloss); return (s, s.Downscaled(PreviewSide)); });
                full = loaded.Item1; small = loaded.Item2;
                lastFolder = Path.GetDirectoryName(found[MapKind.Albedo]);
                st.CustomMask = null;
                quiet = true;
                NameBox.Text = full.SuggestedName;
                if (full.NormalIsDirectX) DxBtn.IsChecked = true; else GlBtn.IsChecked = true;
                quiet = false;
                DropHint.Visibility = Visibility.Collapsed;
                ResText.Text = $"{full.W} × {full.H}";
                UpdateMapList();
                UpdateMaskUi();
                ReadSettings();
                StartConvert();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not load the texture:\n\n" + ex.Message, "Load PBR texture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { SetBusy(null); }
        }

        void UpdateMapList()
        {
            MapList.Children.Clear();
            var rows = new[]
            {
                (MapKind.Albedo, "Colour (albedo)", "needed"),
                (MapKind.Normal, "Normal", "flat"),
                (MapKind.Roughness, "Roughness", "50 %"),
                (MapKind.Metal, "Metalness", "none"),
                (MapKind.AO, "Ambient occlusion", "none"),
                (MapKind.Height, "Height", "not needed"),
                (MapKind.Mask, "Colour mask", "made here"),
            };
            foreach (var (kind, label, missing) in rows)
            {
                bool has = full != null && full.Files.ContainsKey(kind);
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                var top = new DockPanel();
                var mark = new TextBlock { Text = has ? "●" : "○", Foreground = has ? (Brush)FindResource("Accent") : (Brush)FindResource("TextOff"), Margin = new Thickness(0, 0, 7, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(mark, Dock.Left);
                top.Children.Add(mark);
                string extra = kind == MapKind.Roughness && full != null && full.RoughnessIsGloss ? " (from gloss)" : kind == MapKind.Normal && full != null && full.NormalIsDirectX ? " (DirectX)" : "";
                top.Children.Add(new TextBlock { Text = label + extra, FontSize = 13 });
                sp.Children.Add(top);
                sp.Children.Add(new TextBlock
                {
                    Text = has ? Path.GetFileName(full.Files[kind]) : "— " + missing,
                    Style = (Style)FindResource("Small"), FontSize = 11.5, Margin = new Thickness(17, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                    ToolTip = has ? full.Files[kind] : null,
                });
                MapList.Children.Add(sp);
            }
        }

        // ------------------------------------------------------------------ settings
        void ReadSettings()
        {
            st.Threshold = ThresholdSlider.Value / 100;
            st.Softness = SoftSlider.Value / 100;
            st.Swap = SwapBox.IsChecked == true;
            st.MatchBrightness = MatchBox.IsChecked == true;
            st.TargetGrey = TargetSlider.Value / 255;
            st.LowerMetal = MetalBox.IsChecked == true;
            st.MetalTarget = MetalSlider.Value / 100;
            st.FlipGreen = DxBtn.IsChecked == true;
            TargetText.Text = TargetSlider.Value.ToString("0");
            TargetSlider.IsEnabled = st.MatchBrightness;
            MetalSlider.IsEnabled = st.LowerMetal;
        }

        void Setting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => SettingsChanged();
        void Setting_Click(object sender, RoutedEventArgs e) => SettingsChanged();
        void Setting_Checked(object sender, RoutedEventArgs e) => SettingsChanged();

        void SettingsChanged()
        {
            if (!ready || quiet) return;
            ReadSettings();
            if (small != null) convertTimer.Start();
        }

        void Mask_Checked(object sender, RoutedEventArgs e)
        {
            st.Mask = (MaskMode)Enum.Parse(typeof(MaskMode), (string)((RadioButton)sender).Tag);
            if (!ready) return;
            UpdateMaskUi();
            SettingsChanged();
        }

        void UpdateMaskUi()
        {
            if (MaskTuning == null) return;
            bool split = st.Mask == MaskMode.Bright || st.Mask == MaskMode.Low;
            ThresholdSlider.IsEnabled = split; SoftSlider.IsEnabled = split;
            SwapBox.IsEnabled = st.Mask != MaskMode.None;
            MaskTuning.Opacity = split || st.Mask == MaskMode.Custom ? 1 : 0.55;
            MaskBtn.Visibility = st.Mask == MaskMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        }

        void LoadMask_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick a mask picture (white = takes paint, black = stays bare)", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", InitialDirectory = lastFolder ?? "" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var p = Imaging.ToPlane(Imaging.LoadRgba(dlg.FileName));
                st.CustomMask = p;
                if (full != null) full.Files[MapKind.Mask] = dlg.FileName;
                UpdateMapList();
                SettingsChanged();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Mask picture", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        void View_Checked(object sender, RoutedEventArgs e)
        {
            view = (ViewMode)Enum.Parse(typeof(ViewMode), (string)((RadioButton)sender).Tag);
            if (!ready) return;
            UpdateCaption();
            renderTimer.Start();
        }

        void View_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            lit = LitBox.IsChecked == true;
            renderTimer.Start();
        }

        void Tile_Checked(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            ApplyTiling(int.Parse((string)((RadioButton)sender).Tag));
        }

        int tiles = 1;
        void ApplyTiling(int t)
        {
            tiles = t;
            if (PreviewRect.Fill is ImageBrush b) b.Viewport = new Rect(0, 0, 1.0 / t, 1.0 / t);
        }

        void Name_Changed(object sender, TextChangedEventArgs e) { if (ready) UpdateSaveState(); }

        bool NameOk => Regex.IsMatch(NameBox.Text ?? "", "^[A-Za-z][A-Za-z0-9_]*$");

        void UpdateSaveState()
        {
            if (SaveBtn == null) return;
            SaveBtn.IsEnabled = full != null && NameOk;
            if (!NameOk) NoteText.Text = "The name may use letters, digits and _ only, and must start with a letter (it becomes the SDK material name).";
            else if (NoteText.Text.StartsWith("The name may")) NoteText.Text = conv?.Note ?? "";
        }

        // ------------------------------------------------------------------ convert + render
        async void StartConvert()
        {
            if (small == null) return;
            int gen = ++convertGen;
            var s = st.Clone();
            var src = small;
            var c = await Task.Run(() => Converter.Run(src, s));
            if (gen != convertGen) return;
            conv = c;
            NoteText.Text = c.Note ?? "";
            UpdateStats();
            UpdateSwatches();
            UpdateSaveState();
            StartRender();
        }

        async void StartRender()
        {
            if (conv == null) return;
            int gen = ++renderGen;
            var c = conv; var orig = small.Albedo; var v = view; var p = paint; bool l = lit && (v == ViewMode.Painted || v == ViewMode.Original);
            var px = await Task.Run(() => Renderer.Render(c, orig, v, p, l));
            if (gen != renderGen) return;
            var bmp = Imaging.ToBitmap(px, c.W, c.H);
            PreviewRect.Width = 1024; PreviewRect.Height = 1024.0 * c.H / c.W;
            PreviewRect.Fill = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1.0 / tiles, 1.0 / tiles), ViewportUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(PreviewRect, BitmapScalingMode.HighQuality);
            UpdateCaption();
        }

        void UpdateCaption()
        {
            string what;
            switch (view)
            {
                case ViewMode.Painted:
                    if (!paint.On) what = "In game, never painted";
                    else
                    {
                        Renderer.HsvToRgb(paint.H, paint.S, paint.V, out double r, out double g, out double b);
                        what = $"In game, painted  H {paint.H * 360:0}°  S {paint.S * 100:0}%  V {paint.V * 100:0}%";
                    }
                    break;
                case ViewMode.Original: what = "The texture as loaded"; break;
                case ViewMode.Albedo: what = "Colour map that will be saved (_albedo)"; break;
                case ViewMode.Mask: what = "Paint mask (_colormask): white takes paint, black stays bare"; break;
                case ViewMode.Metal: what = "Metalness (_metal)"; break;
                case ViewMode.Roughness: what = "Roughness (_roughness)"; break;
                case ViewMode.Normal: what = "Normal map (_normal, OpenGL)"; break;
                default: what = view.ToString(); break;
            }
            CaptionText.Text = what;
        }

        void UpdateStats()
        {
            if (conv == null || full == null) { StatsText.Text = "Nothing loaded yet."; return; }
            var lines = new List<string>
            {
                $"Takes paint on {conv.PaintFraction * 100:0} % of the texture.",
                $"Average grey under paint: {conv.GreyBefore * 255:0} → {conv.GreyAfter * 255:0}  (Keen 128).",
            };
            if (full.Metal != null) lines.Add($"Metalness under paint: {conv.MetalBefore * 100:0} % → {conv.MetalAfter * 100:0} %  (Keen 55 %).");
            else lines.Add("No metalness map: saved as 0 (not metal).");
            if (full.Normal == null) lines.Add("No normal map: a flat one is saved.");
            if (full.Ao == null) lines.Add("No AO map: a white one is saved.");
            StatsText.Text = string.Join("\n", lines);
        }

        // ------------------------------------------------------------------ save
        async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (full == null || !NameOk) return;
            string folder = FolderPicker.Pick(new WindowInteropHelper(this).Handle, "Pick where to save the colourable texture (a folder named after the material is made inside)", lastFolder, "Save here");
            if (folder == null) return;
            string name = NameBox.Text;
            string dir = Path.Combine(folder, name);
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            {
                if (MessageBox.Show(this, $"The folder\n{dir}\nalready holds files. Replace the {name}_*.png files in it?", "Save colourable texture", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }
            int side = 0;
            foreach (RadioButton rb in SizeTabs.Children) if (rb.IsChecked == true) side = int.Parse((string)rb.Tag);
            var s = st.Clone();
            var src = full;
            SetBusy("SAVING…");
            try
            {
                var written = await Task.Run(() =>
                {
                    var set = side > 0 ? Scaled(src, side) : src;
                    var c = Converter.Run(set, s);
                    Directory.CreateDirectory(dir);
                    var files = new List<string>();
                    string P(string suffix) { var f = Path.Combine(dir, $"{name}_{suffix}.png"); files.Add(f); return f; }
                    Imaging.SavePngRgb(P("albedo"), c.Albedo, c.W, c.H);
                    Imaging.SavePngGrey(P("metal"), c.Metal, c.W, c.H);
                    Imaging.SavePngGrey(P("roughness"), c.Rough, c.W, c.H);
                    Imaging.SavePngRgb(P("normal"), c.Normal, c.W, c.H);
                    Imaging.SavePngGrey(P("ao"), c.Ao, c.W, c.H);
                    if (s.Mask != MaskMode.None) Imaging.SavePngGrey(P("colormask"), c.Mask, c.W, c.H);
                    else { var stale = Path.Combine(dir, $"{name}_colormask.png"); if (File.Exists(stale)) File.Delete(stale); }
                    File.WriteAllText(Path.Combine(dir, "How to use in the SE2 SDK.txt"), Readme(name, c, s));
                    return (files, c.W, c.H);
                });
                lastFolder = folder;
                NoteText.Text = $"Saved {written.files.Count} pictures ({written.W} × {written.H}) to {dir}";
                var r = MessageBox.Show(this, $"Saved to\n{dir}\n\n{string.Join("\n", written.files.Select(Path.GetFileName))}\n\nOpen the folder?", "Colourable texture saved", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (r == MessageBoxResult.Yes) Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save:\n\n" + ex.Message, "Save colourable texture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { SetBusy(null); }
        }

        static SourceSet Scaled(SourceSet s, int side)
        {
            int big = Math.Max(s.W, s.H);
            if (big == side) return s;
            double f = (double)side / big;
            int w = Math.Max(1, (int)Math.Round(s.W * f)), h = Math.Max(1, (int)Math.Round(s.H * f));
            return new SourceSet
            {
                Files = s.Files, NormalIsDirectX = s.NormalIsDirectX, RoughnessIsGloss = s.RoughnessIsGloss, SuggestedName = s.SuggestedName,
                Albedo = Imaging.Resize(s.Albedo, w, h), Normal = Imaging.Resize(s.Normal, w, h), Rough = Imaging.Resize(s.Rough, w, h),
                Metal = Imaging.Resize(s.Metal, w, h), Ao = Imaging.Resize(s.Ao, w, h), Height = Imaging.Resize(s.Height, w, h), Mask = Imaging.Resize(s.Mask, w, h),
            };
        }

        static string Readme(string name, Converted c, Settings s) =>
$@"{name} - a colourable (paintable) Space Engineers 2 material
Made with SE2 Colourable Texture Maker on {DateTime.Now:d MMM yyyy}. Size {c.W} x {c.H}.

FILES
  {name}_albedo.png     colour. Paintable parts are greyscale: under paint the game keeps only their brightness.
  {name}_metal.png      metalness
  {name}_roughness.png  roughness
  {name}_normal.png     normal map, OpenGL style (green up) - what SE2 uses
  {name}_ao.png         ambient occlusion
  {name}_colormask.png  WHERE THE PAINT GOES: white = takes the player's colour, black = keeps its own colour{(s.Mask == MaskMode.None ? "\n  (no colour mask was saved: this material never takes paint)" : "")}

IN THE SDK (VRAGE editor)
  1. In your mod project make a folder, for example Assets\BlockMaterials\{name}, and drag these PNG files into it.
  2. Select all of them, right-click > Create Asset > Material > PBR Material.
     The editor packs them (_cm, _nr, _add .comptex) and makes the material from the file name endings.
  3. The material's name is its file name: keep it {name}.
  4. Check the _add texture: its Color Mask slot must point at {name}_colormask.png, else nothing takes paint.

IN BLENDER
  Name the material on your model exactly {name} (no .001). Map it like Keen's tiling materials: 0.4 UV per metre
  (one 0-1 tile covers 2.5 m). Export and import the model as usual; the SDK finds the material by its name.

HOW THE GAME PAINTS IT
  Under the mask the colour texture's hue is dropped and its brightness is kept; the player's colour is put on top
  with brightness = texture brightness + paint brightness - 0.55. Keen's paintable metal averages 128 grey; this
  texture's paintable parts average {c.GreyAfter * 255:0}.
";

        // ------------------------------------------------------------------ window
        void SetBusy(string text)
        {
            Busy.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            if (text != null) BusyText.Text = text;
        }

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // keep a maximised borderless window off the taskbar
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0024) // WM_GETMINMAXINFO
            {
                var mon = MonitorFromWindow(hwnd, 2);
                var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                {
                    var mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                    mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
                    mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
                    mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
                    mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
            return IntPtr.Zero;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        struct POINT { public int x, y; }
        struct RECT { public int left, top, right, bottom; }
        struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    }
}
