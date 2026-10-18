// [fork] ワールドマップの「生きている」演出（2026-09-27 要望「画像の波部分が動いてほしい。砂浜の打ち寄せる波とか。
//   揺れるヨットとか船とか。可能なら風に靡く旗も欲しい」）
//   ① 海: WorldMapWaterEffect（Pages\Gt\Fx\worldmap_water.hlsl → Data\Static\gt7\fx\worldmap_water.ps）を 4 枚重ねの地図絵
//      (MapLayers) にだけ掛ける。水面のゆらぎ・砂浜に打ち寄せて引く波・船の揺れ。マスク fx\worldmap_mask.png は Fx\make_mask.py で絵から作る
//   ② 旗: ベクターで描き足し、布を毎フレーム波打たせる。朝・夕・夜は絵の重なり具合から暗さと泡の色味を合わせる
//   ③ 止める: ページが見えない・CM7 最小化中・AC 走行中（acs.exe）は更新しない（GPU/CPU を食わない）
//   設定 fx\worldmap_ambience.txt（船と旗の位置・強さ）。ページを開き直すと読み直す
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal sealed class WorldMapWaterEffect : ShaderEffect {
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(WorldMapWaterEffect), 0);
        public static readonly DependencyProperty MaskProperty = RegisterPixelShaderSamplerProperty("Mask", typeof(WorldMapWaterEffect), 1, SamplingMode.Bilinear);
        public static readonly DependencyProperty NoiseProperty = RegisterPixelShaderSamplerProperty("Noise", typeof(WorldMapWaterEffect), 2, SamplingMode.Bilinear);

        private static DependencyProperty Reg(string name, int register, Point4D value) {
            return DependencyProperty.Register(name, typeof(Point4D), typeof(WorldMapWaterEffect),
                    new UIPropertyMetadata(value, PixelShaderConstantCallback(register)));
        }

        public static readonly DependencyProperty P0Property = Reg("P0", 0, new Point4D(0, 0, 1 / 1920.0, 1 / 1072.0));
        public static readonly DependencyProperty FoamProperty = Reg("Foam", 1, new Point4D(0.96, 0.97, 1, 0.85));
        public static readonly DependencyProperty[] BoatAProperties = Enumerable.Range(0, 5).Select(i => Reg("BoatA" + i, 2 + i * 2, new Point4D(-1, -1, 1e-4, 1e-4))).ToArray();
        public static readonly DependencyProperty[] BoatBProperties = Enumerable.Range(0, 5).Select(i => Reg("BoatB" + i, 3 + i * 2, new Point4D(0, 0, 0, 0))).ToArray();

        public WorldMapWaterEffect(PixelShader shader) {
            PixelShader = shader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(MaskProperty);
            UpdateShaderValue(NoiseProperty);
            UpdateShaderValue(P0Property);
            UpdateShaderValue(FoamProperty);
            foreach (var p in BoatAProperties) UpdateShaderValue(p);
            foreach (var p in BoatBProperties) UpdateShaderValue(p);
        }

        public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
        public Brush Mask { get => (Brush)GetValue(MaskProperty); set => SetValue(MaskProperty, value); }
        public Brush Noise { get => (Brush)GetValue(NoiseProperty); set => SetValue(NoiseProperty, value); }
        public Point4D P0 { get => (Point4D)GetValue(P0Property); set => SetValue(P0Property, value); }
        public Point4D Foam { get => (Point4D)GetValue(FoamProperty); set => SetValue(FoamProperty, value); }
    }

    internal sealed class GtMapAmbience : IDisposable {
        private const double MapW = 1920, MapH = 1072;

        private readonly FrameworkElement _layers;
        private readonly Canvas _canvas;
        private readonly Func<Tuple<double, double, double>> _timeOfDay;   // dawn / dusk / night の不透明度（絵の重なり）
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Flag> _flags = new List<Flag>();
        private readonly List<UIElement> _added = new List<UIElement>();
        private WorldMapWaterEffect _fx;
        private DispatcherTimer _timer;
        private double _wind = 1.0;
        private double _flagSpeed = 1.0;   // 設定 flagspeed=: 旗のはためきの速さの倍率
        private double _debug;   // 設定 debug=1: マスクの波打ち際(赤)と水面の縁(青)を描く（位置合わせの確認用）
        private double _lastLight = -1;
        private bool _disposed;

        private static string FxDir => System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "fx");

        private GtMapAmbience(FrameworkElement layers, Canvas canvas, Func<Tuple<double, double, double>> timeOfDay) {
            _layers = layers;
            _canvas = canvas;
            _timeOfDay = timeOfDay;
        }

        public static GtMapAmbience Attach(FrameworkElement layers, Canvas canvas, Func<Tuple<double, double, double>> timeOfDay) {
            var a = new GtMapAmbience(layers, canvas, timeOfDay);
            try { a.Start(); } catch (Exception e) { Logging.Warning("[GtMapAmbience] " + e); }
            return a;
        }

        #region 設定
        private sealed class Boat { public double Px, Py, Cx, Cy, Rx, Ry, AmpDeg, Speed, Phase; }

        private sealed class Flag {
            public double X, Y, PoleH, W, H, Blur, Phase;
            public string Style;
            public System.Windows.Shapes.Path Cloth, Shade;
            public Rectangle Pole;
            public Ellipse Ball;
            public LinearGradientBrush ShadeBrush;
            public SolidColorBrush PoleBrush;
        }

        private static double[] Nums(string v) {
            return v.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
                    .Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        }

        private void LoadConfig(out bool water, out bool flags, out int fps, List<Boat> boats) {
            water = true; flags = true; fps = 30;
            var path = System.IO.Path.Combine(FxDir, "worldmap_ambience.txt");
            if (!File.Exists(path)) return;
            var idx = 0;
            foreach (var raw in File.ReadAllLines(path)) {
                var line = raw.Split('#')[0].Trim();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var val = line.Substring(eq + 1).Trim();
                try {
                    switch (key) {
                        case "water": water = val != "0"; break;
                        case "flags": flags = val != "0"; break;
                        case "fps": fps = Math.Max(10, Math.Min(60, int.Parse(val, CultureInfo.InvariantCulture))); break;
                        case "debug": _debug = val == "1" ? 1 : 0; break;
                        case "wind": _wind = Math.Max(0, Math.Min(2, double.Parse(val, NumberStyles.Float, CultureInfo.InvariantCulture))); break;
                        case "flagspeed": _flagSpeed = Math.Max(0.1, Math.Min(3, double.Parse(val, NumberStyles.Float, CultureInfo.InvariantCulture))); break;
                        case "boat": {
                            var n = Nums(val);
                            if (n.Length >= 9) boats.Add(new Boat { Px = n[0], Py = n[1], Cx = n[2], Cy = n[3], Rx = n[4], Ry = n[5], AmpDeg = n[6], Speed = n[7], Phase = n[8] });
                            break;
                        }
                        case "flag": {
                            var parts = val.Split(',').Select(s => s.Trim()).ToArray();
                            if (parts.Length < 6) break;
                            Func<int, double> d = i => double.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
                            _flags.Add(new Flag {
                                X = d(0), Y = d(1), PoleH = d(2), W = d(3), H = d(4), Style = parts[5].ToLowerInvariant(),
                                Blur = parts.Length > 6 ? d(6) : 0, Phase = idx++ * 1.37
                            });
                            break;
                        }
                    }
                } catch (Exception e) {
                    Logging.Warning("[GtMapAmbience] config line '" + raw + "': " + e.Message);
                }
            }
        }
        #endregion

        private void Start() {
            var boats = new List<Boat>();
            bool water, flags; int fps;
            LoadConfig(out water, out flags, out fps, boats);
            if (!flags) _flags.Clear();

            if (water) StartWater(boats);
            foreach (var f in _flags) BuildFlag(f);

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / fps) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            Tick();
            Logging.Write("[GtMapAmbience] water=" + (_fx != null) + " boats=" + boats.Count + " flags=" + _flags.Count + " fps=" + fps);
        }

        private void StartWater(List<Boat> boats) {
            if (!RenderCapability.IsPixelShaderVersionSupported(3, 0)) {
                Logging.Warning("[GtMapAmbience] ps_3_0 not supported → water off");
                return;
            }
            var psPath = System.IO.Path.Combine(FxDir, "worldmap_water.ps");
            var maskPath = System.IO.Path.Combine(FxDir, "worldmap_mask.png");
            var noisePath = System.IO.Path.Combine(FxDir, "worldmap_noise.png");
            if (!File.Exists(psPath) || !File.Exists(maskPath) || !File.Exists(noisePath)) {
                Logging.Warning("[GtMapAmbience] shader/mask missing in " + FxDir);
                return;
            }
            var ps = new PixelShader { ShaderRenderMode = ShaderRenderMode.HardwareOnly };
            using (var fs = File.OpenRead(psPath)) ps.SetStreamSource(fs);
            var mask = WorldMap.LoadBitmap(maskPath, 0);
            var noise = WorldMap.LoadBitmap(noisePath, 0);
            if (mask == null || noise == null) return;
            var fx = new WorldMapWaterEffect(ps) {
                Mask = new ImageBrush(mask) { Stretch = Stretch.Fill },
                Noise = new ImageBrush(noise) { Stretch = Stretch.Fill }
            };
            for (var i = 0; i < Math.Min(5, boats.Count); i++) {
                var b = boats[i];
                fx.SetValue(WorldMapWaterEffect.BoatAProperties[i], new Point4D(b.Px / MapW, b.Py / MapH, b.Rx / MapW, b.Ry / MapH));
                fx.SetValue(WorldMapWaterEffect.BoatBProperties[i], new Point4D((b.Cy - b.Py) / MapH, b.AmpDeg * Math.PI / 180, b.Speed, b.Phase));
                // 範囲の中心は x もずれうるが、支点と中心の x はほぼ同じなので y だけ渡す
            }
            _fx = fx;
            _layers.Effect = fx;
        }

        #region 更新
        private bool _acsRunning;
        private long _acsCheckedAt = -10000;

        private bool Paused() {
            if (!_layers.IsVisible) return true;
            var w = Window.GetWindow(_layers);
            if (w == null || w.WindowState == WindowState.Minimized) return true;
            var now = _clock.ElapsedMilliseconds;
            if (now - _acsCheckedAt > 2000) {
                _acsCheckedAt = now;
                try {
                    var ps = Process.GetProcessesByName("acs");
                    _acsRunning = ps.Length > 0;
                    foreach (var p in ps) { try { p.Dispose(); } catch { } }
                } catch { _acsRunning = false; }
            }
            return _acsRunning;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        private static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;
        private double _lastTick = -1;

        private static bool OurAppInFront() {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return pid == OwnPid;
        }

        private void Tick() {
            if (_disposed || Paused()) return;
            var t = _clock.Elapsed.TotalSeconds;
            // 他のアプリを使っている間（CM7 が前面でない）は 1 秒 12 回まで落とす。止めはしない（別の画面で眺めている時に止まって見えないように）
            if (_lastTick >= 0 && t - _lastTick < 1.0 / 12 && !OurAppInFront()) return;
            _lastTick = t;

            // 時間帯（昼を底に 朝・夕・夜 の順で重なっている絵と同じ順で混ぜる）
            double dawn = 0, dusk = 0, night = 0;
            try { var tod = _timeOfDay(); dawn = tod.Item1; dusk = tod.Item2; night = tod.Item3; } catch { }
            var light = Mix(Mix(Mix(1.0, 0.82, dawn), 0.78, dusk), 0.34, night);
            var foam = Mix(Mix(Mix(new Point4D(0.96, 0.97, 1.0, 0.85), new Point4D(1.0, 0.92, 0.86, 0.78), dawn),
                    new Point4D(1.0, 0.84, 0.70, 0.78), dusk), new Point4D(0.50, 0.58, 0.72, 0.5), night);

            if (_fx != null) {
                _fx.P0 = new Point4D(t, _debug, 1 / MapW, 1 / MapH);
                if (Math.Abs(light - _lastLight) > 0.005) _fx.Foam = foam;
            }
            var gust = _wind * (0.72 + 0.28 * Math.Sin(t * 0.37) * Math.Sin(t * 0.23 + 1.0));
            foreach (var f in _flags) UpdateFlag(f, t * _flagSpeed, gust, light, Math.Abs(light - _lastLight) > 0.005);
            if (Math.Abs(light - _lastLight) > 0.005) _lastLight = light;
        }

        private static double Mix(double a, double b, double k) => a + (b - a) * Math.Max(0, Math.Min(1, k));

        private static Point4D Mix(Point4D a, Point4D b, double k) {
            k = Math.Max(0, Math.Min(1, k));
            return new Point4D(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k, a.Z + (b.Z - a.Z) * k, a.W + (b.W - a.W) * k);
        }
        #endregion

        #region 旗
        private static Brush Solid(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }

        private static Brush Bands(bool vertical, params Tuple<double, Color>[] stops) {
            // 硬い境目の帯（同じ位置に 2 つの stop）
            var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 0) };
            var prev = 0.0;
            foreach (var s in stops) {
                br.GradientStops.Add(new GradientStop(s.Item2, prev));
                br.GradientStops.Add(new GradientStop(s.Item2, s.Item1));
                prev = s.Item1;
            }
            br.Freeze();
            return br;
        }

        private static Brush Pattern(string style) {
            var blue = Color.FromRgb(0x1F, 0x4E, 0x9C); var white = Color.FromRgb(0xF4, 0xF4, 0xF0);
            var red = Color.FromRgb(0xD2, 0x2A, 0x2A); var green = Color.FromRgb(0x2E, 0x8B, 0x57);
            switch (style) {
                case "harbor": return Bands(true, Tuple.Create(0.36, blue), Tuple.Create(0.64, white), Tuple.Create(1.0, blue));
                case "redwhite": return Bands(true, Tuple.Create(0.5, red), Tuple.Create(1.0, white));
                case "cafe": return Bands(false, Tuple.Create(0.34, green), Tuple.Create(0.66, white), Tuple.Create(1.0, green));
                case "pennant": return Solid(0xD2, 0x2A, 0x2A);
                case "yellow": return Solid(0xF2, 0xC4, 0x1E);
                case "blue": return Solid(0x2B, 0x5F, 0xC0);
                case "checker": {
                    var dg = new DrawingGroup();
                    dg.Children.Add(new GeometryDrawing(Solid(0xF4, 0xF4, 0xF0), null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
                    dg.Children.Add(new GeometryDrawing(Solid(0x18, 0x18, 0x18), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
                    dg.Children.Add(new GeometryDrawing(Solid(0x18, 0x18, 0x18), null, new RectangleGeometry(new Rect(1, 1, 1, 1))));
                    var db = new DrawingBrush(dg) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 0.34, 0.5), ViewportUnits = BrushMappingMode.RelativeToBoundingBox };
                    db.Freeze();
                    return db;
                }
                default: return Solid(0xE8, 0xE8, 0xE8);
            }
        }

        private const int Segs = 10;

        private void BuildFlag(Flag f) {
            var host = new Canvas { IsHitTestVisible = false };
            if (f.Blur > 0) host.Effect = new BlurEffect { Radius = f.Blur, RenderingBias = RenderingBias.Performance };
            Panel.SetZIndex(host, 4);   // 地図絵(0)より上・走る車(5)とノード(10)より下
            f.PoleBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD4));
            f.Pole = new Rectangle { Width = f.PoleH > 30 ? 1.7 : 1.3, Height = f.PoleH, Fill = f.PoleBrush };
            Canvas.SetLeft(f.Pole, f.X - f.Pole.Width / 2);
            Canvas.SetTop(f.Pole, f.Y - f.PoleH);
            f.Ball = new Ellipse { Width = 2.8, Height = 2.8, Fill = Solid(0xE0, 0xC0, 0x70) };
            Canvas.SetLeft(f.Ball, f.X - 1.4);
            Canvas.SetTop(f.Ball, f.Y - f.PoleH - 2.2);
            f.Cloth = new System.Windows.Shapes.Path { Fill = Pattern(f.Style) };
            f.ShadeBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (var i = 0; i <= Segs; i++) f.ShadeBrush.GradientStops.Add(new GradientStop(Colors.Transparent, i / (double)Segs));
            f.Shade = new System.Windows.Shapes.Path { Fill = f.ShadeBrush };
            // 小さな影（ポールの足もと）
            var foot = new Ellipse { Width = 5, Height = 1.6, Fill = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)) };
            Canvas.SetLeft(foot, f.X - 2.5);
            Canvas.SetTop(foot, f.Y - 0.8);
            host.Children.Add(foot);
            host.Children.Add(f.Pole);
            host.Children.Add(f.Cloth);
            host.Children.Add(f.Shade);
            host.Children.Add(f.Ball);
            _canvas.Children.Add(host);
            _added.Add(host);
        }

        private static void UpdateFlag(Flag f, double t, double gust, double light, bool relight) {
            var ax = f.X + 0.6;                     // 旗の付け根（ポールの右側）
            var ay = f.Y - f.PoleH + 1.0;
            var top = new Point[Segs + 1];
            var bot = new Point[Segs + 1];
            var pennant = f.Style == "pennant";
            var droop = (1.2 - Math.Min(1.2, gust)) * f.H * 0.55;          // 風が弱い時は垂れる
            var len = f.W * (0.86 + 0.14 * Math.Min(1, gust));
            for (var i = 0; i <= Segs; i++) {
                var u = i / (double)Segs;
                var amp = f.H * (0.10 + 0.22 * u) * Math.Min(1.3, gust);
                var ph = u * Math.PI * 2.4 - t * (2.2 + 0.8 * gust) + f.Phase;   // 1 秒に約 0.4 回（元の 5.2+2.0 は速すぎた・本人「旗が靡くのも早すぎる」2026-09-28）
                var y1 = amp * Math.Sin(ph) + droop * u * u;
                var y2 = amp * Math.Sin(ph - 0.45) + droop * u * u;
                var x = u * len - Math.Abs(Math.Sin(ph)) * 0.6 * u;           // 波打つと少し縮む
                var h = pennant ? f.H * (1 - u * 0.92) : f.H;
                top[i] = new Point(ax + x, ay + y1 + (pennant ? f.H * 0.46 * u : 0));
                bot[i] = new Point(ax + x, ay + y2 + h + (pennant ? f.H * 0.46 * u - f.H * 0.46 * u * 0.08 : 0));
            }
            var geo = new StreamGeometry();
            using (var ctx = geo.Open()) {
                ctx.BeginFigure(top[0], true, true);
                ctx.PolyLineTo(top.Skip(1).Concat(bot.Reverse()).ToList(), true, false);
            }
            geo.Freeze();
            f.Cloth.Data = geo;
            f.Shade.Data = geo;

            // 陰影: 布の傾き（上がり＝明るい / 下がり＝暗い）＋時間帯の暗さ
            var dark = (1 - light) * 0.72;
            for (var i = 0; i <= Segs; i++) {
                var a = top[Math.Max(0, i - 1)];
                var b = top[Math.Min(Segs, i + 1)];
                var slope = (b.Y - a.Y) / Math.Max(0.5, b.X - a.X);
                var s = Math.Max(-1, Math.Min(1, slope * 1.6));
                var stop = f.ShadeBrush.GradientStops[i];
                if (s >= 0) stop.Color = Color.FromArgb((byte)(255 * Math.Min(0.9, dark + 0.38 * s)), 0, 0, 0);
                else {
                    var hi = -s * 0.28 * (1 - dark);
                    stop.Color = dark > hi
                            ? Color.FromArgb((byte)(255 * Math.Min(0.9, dark - hi * 0.5)), 0, 0, 0)
                            : Color.FromArgb((byte)(255 * hi), 255, 255, 255);
                }
            }
            if (relight) {
                var g = (byte)(Math.Max(0.25, light) * 0xD8);
                f.PoleBrush.Color = Color.FromRgb(g, g, (byte)(g * 0.98));
                f.Ball.Opacity = Math.Max(0.3, light);
            }
        }
        #endregion

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _timer?.Stop();
            _timer = null;
            try { if (_fx != null && ReferenceEquals(_layers.Effect, _fx)) _layers.Effect = null; } catch { }
            _fx = null;
            foreach (var e in _added) { try { _canvas.Children.Remove(e); } catch { } }
            _added.Clear();
        }
    }
}
