// [fork] GT7の「放置していると車の歴史のスライドが流れる」を再現（要望 2026-08-25）。
//   GTページで一定時間無操作になると、所有車の写真＋日本語の解説（ui_car.json の description ＝ 要望 が219台翻訳済み）を
//   全画面スライドで流す。何か操作すると即座に消える。走行・ロード・ブランドセントラルでは出さない。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal static class GtIdleSlides {
        private const int IdleSeconds = 120;     // 無操作でスライド開始（40秒だと読み物中に出てくる・要望 2026-08-26）
        private const int SlideSeconds = 15;     // 1枚の表示秒（9 秒は「切り替わるのが早い」→ 15・要望 2026-09-12）
        private const int FadeMs = 900;
        private const int CrossfadeMs = 1100;    // 写真同士のクロスフェード

        private static DispatcherTimer _poll, _advance;
        private static Window _win;
        // [fork] 写真は 2 層でクロスフェード。以前は画面全体(不透明な黒背景ごと)を 0.15 まで薄くしていたので、
        //   切り替えの瞬間に後ろのワールドマップが透けて見えた（要望 2026-09-12）。下層は常に不透明、上層だけ 0↔1
        private static Image _layerBottom, _layerTop;
        private static bool _topShowing;
        private static StackPanel _text;
        private static Grid _photoBox;
        private static System.Windows.Shapes.Rectangle _grad;
        private static TextBlock _brand, _name, _desc;
        private static List<CarObject> _cars;
        private static int _index;

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo p);
        [DllImport("kernel32.dll")] private static extern uint GetTickCount();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);

        // [fork] 自 pid はプロセス生存中ずっと変わらない。GtPad のゲートから毎フレーム呼ばれるので、
        //   呼ぶたびに Process オブジェクトを作って捨てる（＝GC 圧）のをやめて 1 回だけ取る。
        private static readonly uint OurPid = GetOurPid();

        private static uint GetOurPid() {
            try {
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) {
                    return (uint)p.Id;
                }
            } catch {
                return 0u;
            }
        }

        /// <summary>前面のウィンドウが CM7 自身（またはその子窓）か。他アプリ作業中はスライドを出さない。
        /// [fork] GtPad のゲート（走行中/他アプリ中はパッドを効かせない）でも同じ判定を使うので internal。
        /// main は使わない（呼び出し側の都合で null が来てもよい）。</summary>
        internal static bool IsOurForeground(Window main) {
            try {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                if (OurPid == 0u) return true;
                GetWindowThreadProcessId(fg, out var pid);
                return pid == OurPid;
            } catch {
                return true;
            }
        }

        // [fork] パッド入力は OS の GetLastInputInfo に載らない（XInput は「入力」として数えられない）。
        //   それだとスライドが出っぱなし＆消えなくなるので、GtPad から時刻をもらって OS 側と小さい方を採る。
        private static int _lastPadInput;
        private static bool _padInputSeen;

        /// <summary>[fork] GtPad がコマンドを出したら呼ぶ。</summary>
        internal static void NotePadInput() {
            _lastPadInput = Environment.TickCount;
            _padInputSeen = true;
        }

        private static uint IdleMs() {
            var i = new LastInputInfo { cbSize = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            var os = GetLastInputInfo(ref i) ? GetTickCount() - i.dwTime : 0u;
            if (!_padInputSeen) return os;
            // Environment.TickCount は 約24.9 日で負にラップする。差は unchecked で取る
            var pad = unchecked(Environment.TickCount - _lastPadInput);
            if (pad < 0) pad = 0;
            return Math.Min(os, (uint)pad);
        }

        /// <summary>テストチャンネル用: 今スライドが出ているか。</summary>
        internal static bool IsShowing => _win != null;

        /// <summary>テストチャンネル用・ワールドマップの隠しキー(P)用: 放置を待たずに出す/消す。
        /// キーで呼ばれた場合はそのキー操作自体が OS の「入力」として数えられ、次のポーリングで即消えてしまうので、
        /// 直後 <see cref="ForceGraceMs"/> ミリ秒だけは入力による Hide を無視する（押す/離すの両方を吸収）。</summary>
        internal static void ForceShow() {
            _suppressed = false;
            _forceGraceUntil = unchecked(Environment.TickCount + ForceGraceMs);
            if (_win == null) Show();
        }

        private const int ForceGraceMs = 1500;
        // 初期値=今。差を unchecked で取るので TickCount のラップ(約24.9日)でも壊れない
        private static int _forceGraceUntil = Environment.TickCount;
        private static bool InForceGrace => unchecked(_forceGraceUntil - Environment.TickCount) > 0;

        /// <summary>消したあと、本物の入力があるまでは出し直さない（テストで消してもすぐ復活しないように）。</summary>
        internal static void ForceHide() { _suppressed = true; Hide(); }

        private static bool _suppressed;

        public static void Start() {
            if (_poll != null) return;
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _poll.Tick += OnPoll;
            _poll.Start();
            // [fork] CarsManager.Loaded は「読み込み済みの車」しか返さない（起動直後は 4 台程度）。
            //   起動 20 秒後に裏で全車を読ませておく（BrandCentral/車選択と同じ EnsureLoadedAsync・数秒）
            var kick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
            kick.Tick += (s, e) => { kick.Stop(); KickLoadAll(); };
            kick.Start();
        }

        private static bool _loadKicked;

        /// <summary>全車の読み込みを裏で始め、終わったら車リストを作り直す（表示中なら差し替える）。</summary>
        private static void KickLoadAll() {
            if (_loadKicked) return;
            _loadKicked = true;
            try {
                if (CarsManager.Instance.IsLoaded) return;
                Logging.Write("[GtIdleSlides] loading all cars in background");
                CarsManager.Instance.EnsureLoadedAsync().ContinueWith(t => {
                    try {
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
                            _cars = null;
                            if (_win != null) { LoadCars(); Apply(); }
                        }));
                    } catch { }
                });
            } catch (Exception ex) {
                Logging.Warning("[GtIdleSlides] load all: " + ex.Message);
            }
        }

        /// <summary>テスト用: なぜ出せない/出せるのかを一言で。</summary>
        internal static string Reason = "-";

        private static bool IsBrandCentralPage() {
            var source = (Application.Current?.MainWindow as AcManager.Pages.Windows.MainWindow)?.CurrentSource?.OriginalString;
            // Covers the maker list, 2D fallback and separate 3D showroom window.
            // Active can outlive navigation, so use the actual page URI.
            return source != null && source.IndexOf("/Pages/Gt/BrandCentral.xaml", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CanShow() {
            try {
                var main = Application.Current?.MainWindow as AcManager.Pages.Windows.MainWindow;
                if (main == null || !main.IsVisible || main.WindowState == WindowState.Minimized) { Reason = "main window"; return false; }
                var src = main.CurrentSource?.OriginalString;
                if (src == null || src.IndexOf("/Pages/Gt/", StringComparison.OrdinalIgnoreCase) < 0) { Reason = "not a GT page"; return false; }
                if (src.IndexOf(TitleScreen.PagePath, StringComparison.OrdinalIgnoreCase) >= 0) { Reason = "title screen"; return false; }
                if (IsBrandCentralPage()) { Reason = "brand central"; return false; }
                if (System.Diagnostics.Process.GetProcessesByName("acs").Length > 0) { Reason = "acs running"; return false; }
                foreach (Window w in Application.Current.Windows) {
                    if (w == _win || !w.IsVisible) continue;
                    var n = w.GetType().Name;
                    if (n == "GtLoadingWindow" || n == "GarageCarPicker" || n == "LiteShowroomTools" || n == "GtOpeningWindow") { Reason = "window " + n; return false; }
                    // ※CmJaOverlay の窓(幕/Studio/GarageGt)は退出後も IsVisible のまま居残るので窓では判定しない。
                    //   ガレージ中かどうかは DLL 側の GarageGt.IsVisible を見る(下)
                }
                if (IsGarageOpen()) { Reason = "garage open"; return false; }   // ガレージ(ショールーム)の最中は出さない
                if (!IsOurForeground(main)) { Reason = "other app"; return false; }   // 他のアプリを使っている間は出さない
                Reason = "ok";
                return true;
            } catch (Exception ex) {
                Reason = "error " + ex.Message;
                return false;
            }
        }

        private static System.Reflection.PropertyInfo _garageVisible;
        private static bool _garageLookedUp;

        /// <summary>CmJaOverlay.GarageGt.IsVisible（DLL 無しなら常に false）。
        /// [fork] GtPad のゲート（ガレージ中はパッドを止める）でも使うので internal。</summary>
        internal static bool IsGarageOpen() {
            try {
                if (!_garageLookedUp) {
                    _garageLookedUp = true;
                    var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CmJaOverlay.GarageGt", false)).FirstOrDefault(x => x != null);
                    _garageVisible = t?.GetProperty("IsVisible", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                }
                return _garageVisible != null && (bool)_garageVisible.GetValue(null, null);
            } catch {
                return false;
            }
        }

        private static void OnPoll(object sender, EventArgs e) {
            try {
                var idle = IdleMs();
                if (idle < 1200) _suppressed = false;   // 本物の操作があったら抑制解除
                if (_suppressed) return;
                if (_win != null) {
                    // 何か触ったら即終了。表示中でも状況が変わったら(ガレージに入った/走行開始/画面遷移)引っ込める
                    //   ただし隠しキーで出した直後は、そのキー入力(押す/離す)で消えないよう少しだけ猶予
                    if ((idle < 1200 && !InForceGrace) || !CanShow()) Hide();
                    return;
                }
                if (idle >= IdleSeconds * 1000u && CanShow()) Show();
            } catch (Exception ex) {
                Logging.Warning("[GtIdleSlides] poll: " + ex.Message);
            }
        }

        // [fork] 生成した「映える」車写真（要望 2026-09-12「ガレージ写真はダサい」）。あればそれを全画面で敷き、
        //   写真がある車が 5 台以上ならその車だけを回す（無ければ従来どおり全車＋ガレージ写真）。
        //   置き場 = Data\Static\gt7\carphotos\<carId>.jpg（作り方は別リポジトリ）
        internal static string PhotoPath(CarObject car) {
            try {
                if (car == null) return null;
                var p = System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "carphotos", car.Id + ".jpg");
                return System.IO.File.Exists(p) ? p : null;
            } catch { return null; }
        }

        private static void LoadCars() {
            try {
                var all = CarsManager.Instance.Loaded
                        .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Description))
                        .ToList();
                var withPhoto = all.Where(x => PhotoPath(x) != null).ToList();
                _cars = withPhoto.Count >= 5 ? withPhoto : all;
                Logging.Write("[GtIdleSlides] cars: " + _cars.Count + " (with photo " + withPhoto.Count + " / all " + all.Count
                        + " / manager loaded " + CarsManager.Instance.IsLoaded + ")");
                // 毎回同じ順にならないよう、日替わりのオフセットで回す
                if (_cars.Count > 1) {
                    var off = DateTime.Now.DayOfYear * 7 + DateTime.Now.Hour;
                    _index = off % _cars.Count;
                }
            } catch (Exception ex) {
                Logging.Warning("[GtIdleSlides] cars: " + ex.Message);
                _cars = new List<CarObject>();
            }
        }

        private static void Show() {
            // Keep every entry point, including the opt-in force-show diagnostic, out of this page.
            if (IsBrandCentralPage()) { Reason = "brand central"; return; }
            if (!CarsManager.Instance.IsLoaded) KickLoadAll();   // まだなら今から（終わり次第リストを差し替える）
            if (_cars == null) LoadCars();
            if (_cars == null || _cars.Count == 0) return;

            var main = Application.Current?.MainWindow;
            if (main == null) return;

            // [fork] Topmost にすると他アプリ(Claude/Chrome)の上にも居座る＝GT7と違うし邪魔(要望 2026-08-26)。
            //   CM7 のウィンドウに従属させ、CM7 が前面の時だけ出す(CanShow で判定)
            _win = new Window {
                Owner = main,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, ShowInTaskbar = false, ShowActivated = false,
                ResizeMode = ResizeMode.NoResize, Topmost = false, Background = Brushes.Transparent, Opacity = 0,
                Left = main.Left, Top = main.Top, Width = main.ActualWidth, Height = main.ActualHeight,
                FontFamily = new FontFamily("Yu Gothic UI, Segoe UI")
            };
            if (main.WindowState == WindowState.Maximized) {
                var scr = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(main).Handle).Bounds;
                var src = PresentationSource.FromVisual(main);
                var sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                var sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
                _win.Left = scr.Left * sx; _win.Top = scr.Top * sy; _win.Width = scr.Width * sx; _win.Height = scr.Height * sy;
            }

            var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x06, 0x07, 0x0A)) };

            _layerBottom = NewLayer();
            _layerTop = NewLayer();
            _layerTop.Opacity = 0;
            _topShowing = false;
            _photoBox = new Grid { Margin = new Thickness(80, 90, 80, 300), ClipToBounds = true };
            _photoBox.Children.Add(_layerBottom);
            _photoBox.Children.Add(_layerTop);
            root.Children.Add(_photoBox);
            // 全画面写真のとき、文字の下に敷く黒グラデーション（GT7 の車紹介スライドの流儀）
            _grad = new System.Windows.Shapes.Rectangle {
                VerticalAlignment = VerticalAlignment.Bottom, Height = 520, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                Fill = new LinearGradientBrush(new GradientStopCollection {
                    new GradientStop(Color.FromArgb(0x00, 0x06, 0x07, 0x0A), 0.0),
                    new GradientStop(Color.FromArgb(0x90, 0x06, 0x07, 0x0A), 0.45),
                    new GradientStop(Color.FromArgb(0xE6, 0x06, 0x07, 0x0A), 1.0)
                }, new Point(0.5, 0), new Point(0.5, 1))
            };
            root.Children.Add(_grad);

            _text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 92), MaxWidth = 1180 };
            _brand = new TextBlock {
                FontFamily = new FontFamily("Bahnschrift SemiCondensed, Yu Gothic UI"), FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xB4, 0x74)), HorizontalAlignment = HorizontalAlignment.Center
            };
            _name = new TextBlock {
                FontFamily = new FontFamily("Bahnschrift Light SemiCondensed, Yu Gothic UI Light"), FontSize = 42,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF2)), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap
            };
            _desc = new TextBlock {
                FontSize = 15, LineHeight = 27, Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xE8, 0xE6, 0xE1)),
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 22, 0, 0), MaxHeight = 116
            };
            _text.Children.Add(_brand); _text.Children.Add(_name); _text.Children.Add(_desc);
            root.Children.Add(_text);

            root.Children.Add(new TextBlock {
                Text = "操作すると戻ります", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0x70, 0xE8, 0xE6, 0xE1)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 34)
            });

            _win.Content = root;
            _win.Show();
            _win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(FadeMs)) { EasingFunction = new SineEase() });

            Apply();
            _advance = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SlideSeconds) };
            _advance.Tick += delegate { Next(); };
            _advance.Start();
        }

        private static Image NewLayer() {
            var img = new Image { Stretch = Stretch.Uniform, MaxHeight = 560, Margin = new Thickness(0, 0, 0, 40), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        private static void Next() {
            if (_cars == null || _cars.Count == 0 || _win == null) return;
            _index = (_index + 1) % _cars.Count;
            var car = _cars[_index];
            try {
                // 写真: 見えていない方の層に次の写真を仕込み、上層の不透明度だけを動かす（黒も地図も挟まない）
                if (_topShowing) {
                    ApplyPhoto(_layerBottom, car);   // 上層の下に用意して、上層を消す
                    _layerTop.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(CrossfadeMs)) { EasingFunction = new SineEase() });
                } else {
                    ApplyPhoto(_layerTop, car);      // 上層に用意して、上層を出す
                    _layerTop.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(CrossfadeMs)) { EasingFunction = new SineEase() });
                }
                _topShowing = !_topShowing;
            } catch (Exception ex) {
                Logging.Warning("[GtIdleSlides] next: " + ex.Message);
            }
            // 文字: 消してから差し替えて出す（クロスフェードの真ん中あたりで入れ替わる）
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new SineEase() };
            fade.Completed += delegate {
                if (_win == null) return;
                ApplyText(car);
                _text?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = new SineEase() });
            };
            _text?.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>今の車を「今見えている層」にそのまま反映（初回表示と、裏読み込み完了で車リストが差し替わった時）。</summary>
        private static void Apply() {
            try {
                var car = _cars[_index];
                ApplyPhoto(_topShowing ? _layerTop : _layerBottom, car);
                ApplyText(car);
            } catch (Exception ex) {
                Logging.Warning("[GtIdleSlides] apply: " + ex.Message);
            }
        }

        private static void ApplyText(CarObject car) {
            _brand.Text = ((car.Brand ?? "").ToUpperInvariant() + (car.Year != null ? "   ·   " + car.Year : "")).Trim();
            _name.Text = car.DisplayName ?? "";
            var d = (car.Description ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (d.Length > 190) d = d.Substring(0, 190) + "…";
            _desc.Text = d;
        }

        private static void ApplyPhoto(Image img, CarObject car) {
            var photo = PhotoPath(car);
            if (photo != null) {
                // 全画面に敷いて、1 枚の間にわずかに寄る（ケン・バーンズ）
                _photoBox.Margin = new Thickness(0);
                img.Stretch = Stretch.UniformToFill;
                img.MaxHeight = double.PositiveInfinity;
                img.Margin = new Thickness(0);
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                var st = new ScaleTransform(1.0, 1.0);
                img.RenderTransform = st;
                img.Source = WorldMap.LoadBitmap(photo, 2000);
                var zoom = new DoubleAnimation(1.0, 1.08, TimeSpan.FromSeconds(SlideSeconds + 1.5)) { EasingFunction = new SineEase() };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
                _grad.Visibility = Visibility.Visible;
            } else {
                _photoBox.Margin = new Thickness(80, 90, 80, 300);
                img.Stretch = Stretch.Uniform;
                img.MaxHeight = 560;
                img.Margin = new Thickness(0, 0, 0, 40);
                img.RenderTransform = null;
                img.Source = WorldMap.LoadBitmap(car.SelectedSkin?.PreviewImage, 1200);
                _grad.Visibility = Visibility.Collapsed;
            }
        }

        private static void Hide() {
            if (_advance != null) { _advance.Stop(); _advance = null; }
            var w = _win;
            _win = null;
            if (w == null) return;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new SineEase() };
            a.Completed += delegate { try { w.Close(); } catch { } };
            w.BeginAnimation(UIElement.OpacityProperty, a);
        }
    }
}
