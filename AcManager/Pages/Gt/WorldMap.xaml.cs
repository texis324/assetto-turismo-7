// [fork] GT7 風ワールドマップ — 起動時のホーム画面。
//   地図絵 = Data\Static\gt7\worldmap.jpg（昼）＋ worldmap_dawn/dusk/night.jpg（朝・夕・夜、同構図で Higgsfield 生成）。1920×1072。
//   実時間でキーフレーム間をクロスフェード（GT7 と同じく現実の時刻に連動）。
//   施設ノードはこのファイルの _nodes 表で定義し、MapCanvas（論理 1920×1072）に絶対座標で置く。座標は「建物の少し上」。
//   遷移は MainWindow.NavigateTo(Uri)。鑑賞モード等、CmJaOverlay.dll 側の機能はリフレクションで呼ぶ（DLL が無ければ無効）。
//   デバッグ: キー 1=朝 2=昼 3=夕 4=夜 0=実時間。Data\Static\gt7\time_override.txt に時刻(例 23.5)を書いても同じ。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AcManager.Pages.Windows;
using AcManager.Tools.Helpers;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows.Controls;
using FirstFloor.ModernUI.Windows.Media;
using Newtonsoft.Json.Linq;

namespace AcManager.Pages.Gt {
    public partial class WorldMap {
        public static readonly Uri PageUri = new Uri("/Pages/Gt/WorldMap.xaml", UriKind.Relative);

        private static readonly Brush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xB4, 0x74)));
        private static readonly Brush Ivory = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xE1)));
        private static readonly Brush Badge = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x0F, 0x12, 0x16)));
        private static readonly Brush BadgeHover = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x1A, 0x1D, 0x22)));
        private static readonly FontFamily NumFont = new FontFamily("Bahnschrift SemiCondensed, Yu Gothic UI");
        private static readonly FontFamily GlyphFont = new FontFamily("Segoe UI Emoji, Segoe UI Symbol");

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        /// <summary>施設ノード。X/Y は地図（1920×1072）上のバッジ中心座標。</summary>
        private class Node {
            public string Title, Subtitle, Glyph;
            public double X, Y;
            public Action Go;
        }

        private readonly List<Node> _nodes;
        private DispatcherTimer _clock;

        // [fork] パッド用フォーカス候補（GtFocus）。要素を作り直すたびに集め直して Register する。
        //   BottomBar は「もう一度走る」ピルとホームバーで共有しているので、片方を作る時にもう片方も捨てる。
        private readonly List<GtFocusItem> _nodeFocus = new List<GtFocusItem>();
        private readonly List<GtFocusItem> _barFocus = new List<GtFocusItem>();
        private GtFocusItem _quickFocus;
        private GtFocusItem _circuitsFocus;

        public WorldMap() {
            InitializeComponent();
            // [fork] 納車の札（ブランドセントラル）
            DeliveryLater.MouseLeftButtonUp += (s, e) => { e.Handled = true; DeliveryDismiss(); };
            DeliverySwitch.MouseLeftButtonUp += (s, e) => { e.Handled = true; DeliverySwitchTo(); };
            DeliveryLater.MouseEnter += (s, e) => DeliveryLater.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            DeliveryLater.MouseLeave += (s, e) => DeliveryLater.Background = Brushes.Transparent;
            DeliverySwitch.MouseEnter += (s, e) => DeliverySwitch.Background = Brushes.White;
            DeliverySwitch.MouseLeave += (s, e) => DeliverySwitch.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4));
            // 座標は建物の「少し上」（建物が隠れないように）
            _nodes = new List<Node> {
                new Node { Title = "ミッション", Subtitle = "チャレンジ・峠", Glyph = "🏔", X = 600, Y = 120, Go = () => Navigate("/Pages/Drive/SpecialEvents.xaml") },
                new Node { Title = "ワールドサーキット", Subtitle = "サーキットを選んで走る", Glyph = "🏁", X = 1450, Y = 112, Go = () => Navigate("/Pages/Gt/WorldCircuits.xaml") },
                new Node { Title = "ガレージ", Subtitle = "所有車", Glyph = "🚗", X = 1018, Y = 388, Go = () => GarageMode.Enter() },
                new Node { Title = "カフェ", Subtitle = "記録・統計", Glyph = "☕", X = 1252, Y = 424, Go = () => Navigate("/Pages/Miscellaneous/PlayerStats.xaml") },
                // [fork] 2026-09-06 ブランドセントラル（全車カタログ→「ガレージに入れる」）。旧「ショーケース」ノード(鑑賞モードの借名)を置き換え。鑑賞は車詳細の「見てみる」へ
                new Node { Title = "ブランドセントラル", Subtitle = "クルマを探す", Glyph = "🏬", X = 640, Y = 432, Go = () => Navigate("/Pages/Gt/BrandCentral.xaml") },
                new Node { Title = "チューニングショップ", Subtitle = "パーツ", Glyph = "🔧", X = 470, Y = 462, Go = () => Navigate("/Pages/Gt/TuningShop.xaml") },   // [fork] 2026-09-05 カスタマイズ
                new Node { Title = "ライセンスセンター", Subtitle = "キャリア", Glyph = "🎓", X = 560, Y = 360, Go = () => Navigate("/Pages/Drive/KunosCareer.xaml") },
                new Node { Title = "スポーツ", Subtitle = "オンライン", Glyph = "📡", X = 1592, Y = 440, Go = () => Navigate("/Pages/Drive/Online.xaml") },
                new Node { Title = "シアター", Subtitle = "リプレイ", Glyph = "🎬", X = 1722, Y = 586, Go = () => Navigate("/Pages/Lists/ReplaysListPage.xaml") },
                new Node { Title = "スケープス", Subtitle = "写真", Glyph = "📷", X = 1196, Y = 636, Go = () => Navigate("/Pages/Gt/Gallery.xaml") },
                new Node { Title = "ハーバー", Subtitle = "コース一覧", Glyph = "⚓", X = 262, Y = 556, Go = () => Navigate("/Pages/Lists/TracksListPage.xaml") },
            };
        }

        #region Lifecycle
        private void OnLoaded(object sender, RoutedEventArgs e) {
            try { LoadMapImages(); } catch (Exception ex) { Logging.Warning("[WorldMap] map image: " + ex.Message); }
            try { BuildNodes(); } catch (Exception ex) { Logging.Warning("[WorldMap] nodes: " + ex.Message); }
            // [fork] 下部タブ(ワールドマップ/ドライブ/コンテンツ/設定)はGT7に無いので出さない(要望 2026-08-25)。従来UIへは左上の»から
            try { UpdateDriverCard(); } catch (Exception ex) { Logging.Warning("[WorldMap] driver card: " + ex.Message); }
            try { BuildQuickDrive(); } catch (Exception ex) { Logging.Warning("[WorldMap] quick drive: " + ex.Message); }
            GtChrome.Enter();   // BGMはGtHeaderが引き取る

            UpdateClock();
            UpdateDayNight(false);
            _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
            _clock.Tick += (s, a) => { UpdateClock(); UpdateDayNight(true); };
            _clock.Start();

            Focus();

            try { BuildTraffic(); } catch (Exception ex) { Logging.Warning("[WorldMap] traffic: " + ex.Message); }
            // [fork] 海のゆらぎ・打ち寄せる波・揺れる船・なびく旗（2026-09-27）
            _ambience?.Dispose();
            _ambience = GtMapAmbience.Attach(MapLayers, MapCanvas,
                    () => Tuple.Create(DawnImage.Opacity, DuskImage.Opacity, NightImage.Opacity));

            // 入場フェード
            Root.Opacity = 0;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new SineEase() });

            CheckDeliveries();   // [fork] ブランドセントラルで入れた車が届く
        }

        #region 納車（ブランドセントラル → ガレージ）
        // [fork] 2026-09-06 GT7 の納車演出。ブランドセントラルで「ガレージに入れる」→ GtGarage.Pending に積まれ、
        //   ワールドマップに戻った時にここで「新しいクルマがガレージに届きました」。乗り換えもここで済ませられる。
        private Queue<CarObject> _deliveries;
        private CarObject _deliveryCar;
        private readonly List<GtFocusItem> _deliveryFocus = new List<GtFocusItem>();
        private static readonly Brush DeliveryEdge = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)));
        private static readonly Brush DeliveryBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xF0)));

        private void CheckDeliveries() {
            try {
                var cars = GtGarage.TakePending();
                if (cars.Count == 0) return;
                _deliveries = new Queue<CarObject>(cars);
                ShowNextDelivery();
            } catch (Exception ex) {
                Logging.Warning("[WorldMap] delivery: " + ex.Message);
            }
        }

        private void ShowNextDelivery() {
            if (_deliveries == null || _deliveries.Count == 0) { HideDelivery(); return; }
            var car = _deliveries.Dequeue();
            _deliveryCar = car;
            DeliveryImg.Source = LoadBitmap(car.SelectedSkin?.PreviewImage, 900);
            DeliveryBrand.Text = ((car.Brand ?? "") + (car.Year.HasValue ? "  ·  " + car.Year.Value : "")).ToUpperInvariant();
            DeliveryName.Text = car.DisplayName;
            DeliveryCount.Text = "ガレージ  " + GtGarage.Count + " 台" + (_deliveries.Count > 0 ? "   （あと " + _deliveries.Count + " 台）" : "");
            DeliveryLater.BorderBrush = DeliveryEdge;
            DeliverySwitch.BorderBrush = Brushes.Transparent;
            if (DeliveryOverlay.Visibility != Visibility.Visible) {
                DeliveryOverlay.Visibility = Visibility.Visible;
                DeliveryOverlay.Opacity = 0;
                DeliveryOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(360)) { BeginTime = TimeSpan.FromMilliseconds(600), EasingFunction = new SineEase() });
            }
            GtSfx.PlaySelect();
            _deliveryFocus.Clear();
            _deliveryFocus.Add(new GtFocusItem {
                Name = "あとで", Center = () => ElementCenter(DeliveryLater),
                SetHot = on => DeliveryLater.BorderBrush = on ? Brushes.White : DeliveryEdge, Activate = DeliveryDismiss
            });
            _deliveryFocus.Add(new GtFocusItem {
                Name = "乗り換える", Center = () => ElementCenter(DeliverySwitch),
                SetHot = on => DeliverySwitch.BorderBrush = on ? DeliveryBlue : Brushes.Transparent, Activate = DeliverySwitchTo
            });
            GtFocus.Register(this, _deliveryFocus, GtFocusLayout.Row, 0, 1);
        }

        private void HideDelivery() {
            DeliveryOverlay.Visibility = Visibility.Collapsed;
            _deliveryCar = null;
            _deliveries = null;
            RegisterFocus();
        }

        private void DeliveryDismiss() {
            if (_deliveryCar == null) return;
            GtSfx.PlayCancel();
            ShowNextDelivery();
        }

        private void DeliverySwitchTo() {
            var car = _deliveryCar;
            if (car == null) return;
            try {
                CourseEntry.SetPresetCar(car);
                try { Header.Refresh(); } catch { }
                try { UpdateDriverCard(); } catch { }
            } catch (Exception ex) {
                Logging.Warning("[WorldMap] delivery switch: " + ex.Message);
            }
            GtSfx.PlaySelect();
            ShowNextDelivery();
        }

        internal bool DeliveryShowing => DeliveryOverlay.Visibility == Visibility.Visible;
        #endregion

        private GtMapAmbience _ambience;

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            _traffic?.Stop();
            _traffic = null;
            _ambience?.Dispose();
            _ambience = null;
            _clock?.Stop();
            _clock = null;
            GtFocus.Clear(this);   // [fork] パッドのフォーカスを手放す
            GtChrome.Leave();
        }

        #endregion

        #region パッド（GtFocus）
        // [fork] ノード全部 + 「もう一度走る」ピル + ホームバー を空間移動（Spatial）で結ぶ。
        //   ホバー表現は SetHot(bool) に切り出して MouseEnter/Leave と GtFocusItem の両方から呼ぶ（RegionMap の SetHot が手本）。
        private void RegisterFocus() {
            try {
                if (!IsLoaded) return;
                var items = new List<GtFocusItem>();
                items.AddRange(_nodeFocus);
                if (_quickFocus != null) items.Add(_quickFocus);
                items.AddRange(_barFocus);
                if (items.Count == 0) return;
                var initial = _circuitsFocus == null ? 0 : items.IndexOf(_circuitsFocus);   // 最初は「ワールドサーキット」
                GtFocus.Register(this, items, GtFocusLayout.Spatial, 0, initial < 0 ? 0 : initial);
            } catch (Exception ex) {
                Logging.Warning("[WorldMap] focus: " + ex.Message);
            }
        }

        /// <summary>[fork] 要素の指定点を MainWindow 基準の論理 px に直す（GtFocusItem.Center 用）。
        ///   まだ配置されていない／別ツリーなら (0,0)＝GtFocus 側で候補から外れる。</summary>
        internal static Point ElementCenter(FrameworkElement el, double x, double y) {
            try {
                var main = Application.Current?.MainWindow;
                if (main == null || el == null || !el.IsLoaded) return new Point(0, 0);
                if (PresentationSource.FromVisual(el) == null) return new Point(0, 0);
                return el.TranslatePoint(new Point(x, y), main);
            } catch {
                return new Point(0, 0);
            }
        }

        internal static Point ElementCenter(FrameworkElement el) {
            return el == null ? new Point(0, 0) : ElementCenter(el, el.ActualWidth / 2, el.ActualHeight / 2);
        }
        #endregion

        #region Day / night
        private static string Gt7Dir => System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7");
        private double? _timeOverride;

        private void LoadMapImages() {
            MapImage.Source = LoadBitmap(System.IO.Path.Combine(Gt7Dir, "worldmap.jpg"), 0);
            DawnImage.Source = LoadBitmap(System.IO.Path.Combine(Gt7Dir, "worldmap_dawn.jpg"), 0);
            DuskImage.Source = LoadBitmap(System.IO.Path.Combine(Gt7Dir, "worldmap_dusk.jpg"), 0);
            NightImage.Source = LoadBitmap(System.IO.Path.Combine(Gt7Dir, "worldmap_night.jpg"), 0);
            if (MapImage.Source == null) Logging.Warning("[WorldMap] worldmap.jpg not found in " + Gt7Dir);
        }

        /// <summary>現在の「時刻」（小数時間）。キー/ファイルで上書き可。</summary>
        private double CurrentHour() {
            if (_timeOverride.HasValue) return _timeOverride.Value;
            try {
                var f = System.IO.Path.Combine(Gt7Dir, "time_override.txt");
                if (File.Exists(f) && double.TryParse(File.ReadAllText(f).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return h;
            } catch { }
            var now = DateTime.Now;
            return now.Hour + now.Minute / 60.0;
        }

        // 時刻→絵のキーフレーム。隣り合う2枚の間をクロスフェード（昼は一番下の層なので不透明度を持たない）
        private static readonly (double hour, string layer)[] Keyframes = {
            (4.5, "night"), (6.5, "dawn"), (9.0, "day"), (17.0, "day"), (18.5, "dusk"), (20.5, "night"), (28.5, "night"),
        };

        /// <summary>4枚（昼=底・朝・夕・夜）の不透明度を時刻から決める。</summary>
        private void UpdateDayNight(bool animate) {
            var h = CurrentHour();
            if (h < 4.5) h += 24; // 0〜4.5時は 24〜28.5 として扱う（夜→夜）
            string from = "night", to = "night"; double t = 0;
            for (var i = 0; i < Keyframes.Length - 1; i++) {
                if (h >= Keyframes[i].hour && h < Keyframes[i + 1].hour) {
                    from = Keyframes[i].layer; to = Keyframes[i + 1].layer;
                    t = Smooth((h - Keyframes[i].hour) / (Keyframes[i + 1].hour - Keyframes[i].hour));
                    break;
                }
            }
            var op = new Dictionary<string, double> { ["dawn"] = 0, ["dusk"] = 0, ["night"] = 0 };
            if (from == to) { if (from != "day") op[from] = 1; }
            else if (to == "day") { op[from] = 1 - t; }
            else if (from == "day") { op[to] = t; }
            else { op[from] = 1; op[to] = t; }

            var dur = TimeSpan.FromMilliseconds(animate ? 2500 : 0);
            DawnImage.BeginAnimation(OpacityProperty, new DoubleAnimation(op["dawn"], dur));
            DuskImage.BeginAnimation(OpacityProperty, new DoubleAnimation(op["dusk"], dur));
            NightImage.BeginAnimation(OpacityProperty, new DoubleAnimation(op["night"], dur));

            var mode = (t < 0.5 ? from : to).ToUpperInvariant();
            TimeModeText.Text = mode + (_timeOverride.HasValue ? "  (preview " + _timeOverride.Value.ToString("0.#", CultureInfo.InvariantCulture) + "h — 0 で戻す)" : "");
        }

        private static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
            // [fork] 納車の札が出ている間: Enter=乗り換える / Esc=あとで
            if (DeliveryOverlay.Visibility == Visibility.Visible) {
                if (e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; DeliverySwitchTo(); }
                else if (e.Key == Key.Escape) { e.Handled = true; DeliveryDismiss(); }
                return;
            }
            double? t = null;
            switch (e.Key) {
                case Key.D1: case Key.NumPad1: t = 6.5; break;
                case Key.D2: case Key.NumPad2: t = 13; break;
                case Key.D3: case Key.NumPad3: t = 18.5; break;
                case Key.D4: case Key.NumPad4: t = 23; break;
                case Key.D0: case Key.NumPad0: _timeOverride = null; UpdateDayNight(true); e.Handled = true; return;
                case Key.S:   // [fork] 検証用の裏口: セッティングシートへ直行（GT画面の自動テストで使う）
                    e.Handled = true;
                    Navigate("/Pages/Gt/SettingSheet.xaml");
                    return;
                case Key.D:   // [fork] 検証用の裏口: コースイン画面(ニュル)へ直行。地球儀/地域マップのクリックナビが不調でも検証できる
                    e.Handled = true;
                    Navigate("/Pages/Gt/CourseEntry.xaml?Track=ks_nurburgring&Region=eu");
                    return;
                case Key.G:   // [fork] 検証用の裏口: ガレージ(ショールーム)へ直行
                    e.Handled = true;
                    GarageMode.Enter();
                    return;
                case Key.B:   // [fork] 検証用の裏口: ブランドセントラルへ直行
                    e.Handled = true;
                    Navigate("/Pages/Gt/BrandCentral.xaml");
                    return;
                case Key.P:   // [fork] 検証用の裏口: 放置スライド(車写真)を今すぐ出す。2分待たずに写真を確認できる(要望 2026-09-12)
                    e.Handled = true;
                    GtIdleSlides.ForceShow();
                    return;
                default: return;
            }
            _timeOverride = t;
            UpdateDayNight(true);
            e.Handled = true;
        }
        #endregion

        #region 一発コースイン（もう一度走る）
        // [fork] 要望 2026-08-26「ワールドマップから一発でコースインできるボタンが欲しい」。
        //   直近のコースイン条件（コース/レイアウト/モード/時刻）をそのまま使い、
        //   ワールドサーキット → 地域マップ → コースイン画面 を全部飛ばして走り出す。
        //   文言と見た目は GT7 のレース後メニュー「もう一度走る」＝枠線ピル（進む系）に合わせた。
        //   一度も走っていない時は出さない（GT7 も走る前に「もう一度」は出ない）。
        private void BuildQuickDrive() {
            BottomBar.Children.Clear();
            _quickFocus = null;
            _barFocus.Clear();   // [fork] BottomBar はピルとホームバーの共有なので、作り直す時に候補も捨てる
            var d = CourseEntry.ReadLastDrive();
            if (d == null) {
                RegisterFocus();
                return;
            }

            var idle = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x12, 0x16)));
            var hover = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x20, 0x26)));
            var edge = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xE8, 0xE6, 0xE1)));

            var pill = new Border {
                CornerRadius = new CornerRadius(26),
                Background = idle,
                BorderThickness = new Thickness(1.5),
                BorderBrush = edge,
                Padding = new Thickness(26, 8, 30, 9),
                Cursor = Cursors.Hand,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 },
                ToolTip = "前回と同じ条件でそのままコースインします"
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock {
                Text = "🏁", FontFamily = GlyphFont, FontSize = 19, Foreground = Ivory,   // CM7の他のスタート系ボタン(準備OK)と同じ旗
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0)
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = "もう一度走る", FontSize = 17, Foreground = Ivory, FontWeight = FontWeights.SemiBold });
            var where = d.Title
                    + (string.IsNullOrEmpty(d.LayoutTitle) ? "" : " " + d.LayoutTitle)
                    + "   " + d.ModeTitle + "   " + FormatClock(d.TimeMinutes);
            text.Children.Add(new TextBlock {
                Text = where, FontFamily = NumFont, FontSize = 11, Foreground = Gold, Opacity = 0.9,
                Margin = new Thickness(0, 1, 0, 0)
            });
            row.Children.Add(text);
            pill.Child = row;

            // [fork] ホバー表現を SetHot に切り出し（マウスとパッドの両方から呼ぶ）
            void SetHot(bool hot) {
                pill.Background = hot ? hover : idle;
                pill.BorderBrush = hot ? Brushes.White : edge;
            }

            void Activate() {
                Navigate(CourseEntry.LastDriveUri(d));
            }

            pill.MouseEnter += (s2, e2) => SetHot(true);
            pill.MouseLeave += (s2, e2) => SetHot(false);
            pill.MouseLeftButtonUp += (s2, e2) => Activate();

            BottomBar.Children.Add(pill);

            _quickFocus = new GtFocusItem {
                Name = "もう一度走る",
                Center = () => ElementCenter(pill),
                SetHot = SetHot,
                Activate = Activate
            };
            RegisterFocus();
        }

        private static string FormatClock(int minutes) {
            return (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":"
                    + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
        }
        #endregion

        #region Nodes
        private void BuildNodes() {
            foreach (var child in MapCanvas.Children.OfType<FrameworkElement>().Where(x => x.Tag is Node).ToList()) MapCanvas.Children.Remove(child);
            _nodeFocus.Clear();
            _circuitsFocus = null;

            var delay = 0;
            foreach (var node in _nodes) {
                GtFocusItem focus;
                var el = CreateNode(node, out focus);
                _nodeFocus.Add(focus);
                if (node.Title == "ワールドサーキット") _circuitsFocus = focus;   // パッドの初期位置
                MapCanvas.Children.Add(el);
                el.Opacity = 0;
                var tt = new TranslateTransform(0, -18);
                el.RenderTransform = new TransformGroup { Children = { (Transform)el.RenderTransform, tt } };
                var begin = TimeSpan.FromMilliseconds(120 + delay);
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { BeginTime = begin, EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
                delay += 55;
            }

            RegisterFocus();
        }

        private const double NodeSize = 56;   // GT7のバッジ比率に合わせて縮小

        #region 動く車（GT7の地図では車が道を走っている）
        private DispatcherTimer _traffic;

        private class TrafficCar {
            public Ellipse Dot;
            public Point[] Path;
            public double[] Seg;        // 各区間の累積長
            public double Total;
            public double T;            // 現在位置(px)
            public double Speed;        // px/s
            public bool Loop;           // true=周回 / false=往復
            public int Dir = 1;
        }

        private readonly List<TrafficCar> _cars = new List<TrafficCar>();

        // 道路のポリライン（Canvas 1920×1072 座標・地図絵からトレース）
        private static readonly Point[] CoastRoad = {
            new Point(180, 640), new Point(320, 612), new Point(420, 560), new Point(520, 548), new Point(640, 560),
            new Point(760, 588), new Point(880, 590), new Point(980, 572), new Point(1090, 596), new Point(1200, 636),
            new Point(1320, 652), new Point(1450, 660), new Point(1580, 676), new Point(1700, 706), new Point(1810, 736)
        };
        private static readonly Point[] CircuitLoop = {
            new Point(1160, 250), new Point(1240, 180), new Point(1380, 148), new Point(1540, 146), new Point(1680, 178),
            new Point(1770, 250), new Point(1720, 330), new Point(1580, 356), new Point(1430, 340), new Point(1290, 320),
            new Point(1180, 300), new Point(1160, 250)
        };

        private void BuildTraffic() {
            _traffic?.Stop();
            foreach (var c in _cars) MapCanvas.Children.Remove(c.Dot);
            _cars.Clear();

            void Add(Point[] path, bool loop, double speed, double startFrac, Color color) {
                var seg = new double[path.Length];
                double total = 0;
                for (var i = 1; i < path.Length; i++) { total += (path[i] - path[i - 1]).Length; seg[i] = total; }
                var dot = new Ellipse {
                    Width = 7, Height = 7, Fill = new SolidColorBrush(color), IsHitTestVisible = false,
                    Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9, Color = color }
                };
                Panel.SetZIndex(dot, 5);   // ノード(10)より下・地図より上
                MapCanvas.Children.Add(dot);
                _cars.Add(new TrafficCar { Dot = dot, Path = path, Seg = seg, Total = total, T = total * startFrac, Speed = speed, Loop = loop });
            }

            var warm = Color.FromRgb(0xFF, 0xF2, 0xC8);   // ヘッドライト色
            Add(CoastRoad, false, 42, 0.10, warm);
            Add(CoastRoad, false, 34, 0.55, warm);
            Add(CoastRoad, false, 50, 0.85, warm);
            Add(CircuitLoop, true, 95, 0.0, Colors.White);
            Add(CircuitLoop, true, 88, 0.45, Colors.White);

            _traffic = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            _traffic.Tick += (s, e) => {
                foreach (var c in _cars) {
                    c.T += c.Speed * 0.05 * c.Dir;
                    if (c.Loop) {
                        if (c.T >= c.Total) c.T -= c.Total;
                    } else {
                        if (c.T >= c.Total) { c.T = c.Total; c.Dir = -1; }
                        else if (c.T <= 0) { c.T = 0; c.Dir = 1; }
                    }
                    var p = PointAt(c);
                    Canvas.SetLeft(c.Dot, p.X - 3.5);
                    Canvas.SetTop(c.Dot, p.Y - 3.5);
                }
            };
            _traffic.Start();
        }

        private static Point PointAt(TrafficCar c) {
            var t = c.T;
            for (var i = 1; i < c.Path.Length; i++) {
                if (t <= c.Seg[i] || i == c.Path.Length - 1) {
                    var segLen = c.Seg[i] - c.Seg[i - 1];
                    var k = segLen <= 0 ? 0 : (t - c.Seg[i - 1]) / segLen;
                    if (k < 0) k = 0; if (k > 1) k = 1;
                    var a = c.Path[i - 1]; var b = c.Path[i];
                    return new Point(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
                }
            }
            return c.Path[c.Path.Length - 1];
        }
        #endregion

        /// <summary>[fork] focus = パッド用のフォーカス候補（GtFocus）。ホバー表現は SetHot(bool) に切り出して
        ///   MouseEnter/Leave と GtFocusItem の両方から呼ぶ。</summary>
        private FrameworkElement CreateNode(Node node, out GtFocusItem focus) {
            var scale = new ScaleTransform(1, 1);
            var root = new Grid { Tag = node, Width = 220, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.25), RenderTransform = scale, Background = Brushes.Transparent };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // GT7流: 角丸正方形の黒バッジ＋白ピクトグラム。ラベルはホバー時だけ下に出す(gt7-ref ref_3)
            var badge = new Border {
                Width = NodeSize, Height = NodeSize, HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.FromArgb(0xE2, 0x14, 0x14, 0x17)),
                BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent,
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.5 }
            };
            var glyph = new TextBlock {
                Text = node.Glyph, FontFamily = GlyphFont, FontSize = 25, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1)
            };
            badge.Child = glyph;
            Grid.SetRow(badge, 0);
            root.Children.Add(badge);

            var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 0) };
            var title = new TextBlock {
                Text = node.Title, FontSize = 14, Foreground = Ivory, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Opacity = 0,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.9 }
            };
            var sub = new TextBlock {
                Text = node.Subtitle, FontFamily = NumFont, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xE8, 0xE6, 0xE1)),
                TextAlignment = TextAlignment.Center, Opacity = 0,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.9 }
            };
            label.Children.Add(title);
            label.Children.Add(sub);
            Grid.SetRow(label, 1);
            root.Children.Add(label);

            Canvas.SetLeft(root, node.X - root.Width / 2);
            Canvas.SetTop(root, node.Y - NodeSize / 2);

            void SetHot(bool hot) {
                if (hot) {
                    badge.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x2A, 0x2A, 0x30));
                    badge.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));   // GT7のフォーカス=白枠
                    Animate(scale, ScaleTransform.ScaleXProperty, 1.12, 160);
                    Animate(scale, ScaleTransform.ScaleYProperty, 1.12, 160);
                    title.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
                    sub.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
                    HintText.Text = node.Title + " — " + node.Subtitle;
                } else {
                    badge.Background = new SolidColorBrush(Color.FromArgb(0xE2, 0x14, 0x14, 0x17));
                    badge.BorderBrush = Brushes.Transparent;
                    Animate(scale, ScaleTransform.ScaleXProperty, 1.0, 200);
                    Animate(scale, ScaleTransform.ScaleYProperty, 1.0, 200);
                    title.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
                    sub.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
                    HintText.Text = "施設を選んでください";
                }
            }

            void Activate() {
                Animate(scale, ScaleTransform.ScaleXProperty, 0.96, 80);
                Animate(scale, ScaleTransform.ScaleYProperty, 0.96, 80);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                    try { node.Go?.Invoke(); } catch (Exception ex) { Logging.Warning("[WorldMap] " + node.Title + ": " + ex.Message); }
                }));
            }

            root.MouseEnter += (s, e) => SetHot(true);
            root.MouseLeave += (s, e) => SetHot(false);
            root.MouseLeftButtonUp += (s, e) => {
                e.Handled = true;
                Activate();
            };

            // Center はバッジの中心（ラベルはホバー時しか出ないので、Grid 全体の中心だと下にずれる）
            focus = new GtFocusItem {
                Name = node.Title,
                Center = () => ElementCenter(root, root.Width / 2, NodeSize / 2),
                SetHot = SetHot,
                Activate = Activate
            };
            return root;
        }

        private static void Animate(DependencyObject target, DependencyProperty prop, double to, int ms) {
            ((IAnimatable)target).BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        }
        #endregion

        #region Bottom bar
        private void BuildBottomBar() {
            BottomBar.Children.Clear();
            _barFocus.Clear();
            _quickFocus = null;   // [fork] BottomBar は「もう一度走る」ピルとの共有
            BottomBar.Children.Add(BarButton("ワールドマップ", true, null));   // 現在地なので候補にしない
            AddBarButton("ドライブ", () => Navigate("/Pages/Drive/QuickDrive.xaml"));
            AddBarButton("コンテンツ", () => Navigate("/Pages/Lists/CarsListPage.xaml"));
            AddBarButton("設定", () => Navigate("/Pages/Settings/SettingsPage.xaml"));
            RegisterFocus();
        }

        /// <summary>[fork] ホームバーのボタンを 1 個足して、パッドのフォーカス候補にも登録する。</summary>
        private void AddBarButton(string text, Action go) {
            Action<bool> setHot;
            var el = BarButton(text, false, go, out setHot);
            BottomBar.Children.Add(el);
            _barFocus.Add(new GtFocusItem {
                Name = text,
                Center = () => ElementCenter(el),
                SetHot = setHot,
                Activate = () => go()
            });
        }

        internal static FrameworkElement BarButton(string text, bool active, Action go) {
            Action<bool> setHot;
            return BarButton(text, active, go, out setHot);
        }

        /// <summary>[fork] setHot = ホバー表現の切り出し（GtFocus からも呼ぶ）。見た目・イベントは従来どおり。</summary>
        internal static FrameworkElement BarButton(string text, bool active, Action go, out Action<bool> setHot) {
            var stack = new StackPanel { Margin = new Thickness(18, 0, 18, 0), Cursor = go == null ? Cursors.Arrow : Cursors.Hand, Background = Brushes.Transparent };
            var tb = new TextBlock {
                Text = text, FontFamily = NumFont, FontSize = 14, Foreground = active ? Gold : Ivory, Opacity = active ? 1 : 0.75,
                HorizontalAlignment = HorizontalAlignment.Center, Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.9 }
            };
            var line = new Rectangle { Height = 2, Fill = Gold, Margin = new Thickness(0, 5, 0, 0), Opacity = active ? 1 : 0 };
            stack.Children.Add(tb);
            stack.Children.Add(line);

            void Hot(bool hot) {
                tb.Opacity = hot ? 1 : (active ? 1 : 0.75);
                line.Opacity = hot ? 0.6 : (active ? 1 : 0);
            }

            setHot = Hot;
            if (go != null) {
                stack.MouseEnter += (s, e) => Hot(true);
                stack.MouseLeave += (s, e) => Hot(false);
                stack.MouseLeftButtonUp += (s, e) => { e.Handled = true; go(); };
            }
            return stack;
        }
        #endregion

        #region Driver card
        private CarObject _currentCar;

        /// <summary>クイックドライブで最後に選んだ車（保存された JSON の CarId）。無ければ既定の車。</summary>
        internal static CarObject FindCurrentCar() {
            try {
                var saved = ValuesStorage.Get<string>("__QuickDrive_Main");
                if (!string.IsNullOrWhiteSpace(saved)) {
                    var id = JObject.Parse(saved)["CarId"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) {
                        var car = CarsManager.Instance.GetById(id);
                        if (car != null) return car;
                    }
                }
            } catch (Exception e) {
                Logging.Warning("[WorldMap] saved car: " + e.Message);
            }
            return CarsManager.Instance.GetDefault();
        }

        private void UpdateDriverCard() {
            _currentCar = FindCurrentCar();
            DriverName.Text = ("DRIVER  ·  " + (SettingsHolder.Drive.PlayerName ?? "")).ToUpperInvariant();
            if (_currentCar == null) {
                CarName.Text = "—";
                return;
            }
            CarName.Text = _currentCar.DisplayName;
            var meta = new List<string>();
            if (!string.IsNullOrEmpty(_currentCar.Brand)) meta.Add(_currentCar.Brand.ToUpperInvariant());
            if (_currentCar.Year.HasValue) meta.Add(_currentCar.Year.Value.ToString());
            if (!string.IsNullOrEmpty(_currentCar.SpecsBhp)) meta.Add(GtHeader.PowerPs(_currentCar.SpecsBhp) ?? _currentCar.SpecsBhp);
            CarMeta.Text = string.Join("  ·  ", meta);
            BrandBadge.Source = LoadBitmap(_currentCar.BrandBadge, 80);
            var skin = _currentCar.SelectedSkin;
            CarPreview.Source = skin != null ? LoadBitmap(skin.PreviewImage, 440) : null;
        }

        // [fork] 小さい画像（ロゴ・コース図など decodeWidth<=800）は「パス＋更新日時＋サイズ」で覚えておく。
        //   地域マップは入るたびにロゴ 20 枚（原寸 1800px の PNG）を UI スレッドで解き直していて、寄る演出がカクついた
        //   （要望 2026-10-14「コース選択画面がちょっと重い」）。ファイルを差し替えれば日時が変わるので、再起動なしの差し替えはそのまま効く。
        private static readonly Dictionary<string, BitmapImage> SmallBitmapCache = new Dictionary<string, BitmapImage>();

        internal static BitmapImage LoadBitmap(string path, int decodeWidth) {
            try {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string key = null;
                if (decodeWidth > 0 && decodeWidth <= 800) {
                    var fi = new FileInfo(path);
                    key = path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length + "|" + decodeWidth;
                    lock (SmallBitmapCache) {
                        if (SmallBitmapCache.TryGetValue(key, out var cached)) return cached;
                    }
                }
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                // [fork] WPF はパス（URI）ごとに画像を覚えていて、同じ名前で差し替えても CM7 を再起動するまで古い絵を出し続けた
                //   （2026-09-27 要望「再起動せずに変更できると嬉しい」）→ 毎回ファイルから読む（背景写真・ロゴ・コース図が画面の入り直しで替わる）
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                if (key != null) {
                    lock (SmallBitmapCache) { SmallBitmapCache[key] = bmp; }
                }
                return bmp;
            } catch {
                return null;
            }
        }

        private void OnDriverCardClick(object sender, MouseButtonEventArgs e) {
            Navigate("/Pages/Drive/QuickDrive.xaml");
        }
        #endregion

        #region Clock
        private void UpdateClock() {
            var now = DateTime.Now;
            ClockText.Text = now.ToString("HH:mm");
            DateText.Text = now.ToString("yyyy.MM.dd  ddd", CultureInfo.InvariantCulture).ToUpperInvariant();
        }
        #endregion

        #region Navigation
        internal static void Navigate(string uri) {
            (Application.Current?.MainWindow as MainWindow)?.NavigateTo(new Uri(uri, UriKind.Relative));
        }

        /// <summary>CmJaOverlay.dll の鑑賞モード（Viewer.Open(Window, car, skin)）。DLL が無ければショールーム一覧へ。</summary>
        private void OpenShowcase() {
            try {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CmJaOverlay.Viewer", false)).FirstOrDefault(t => t != null);
                var open = type?.GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
                var car = _currentCar ?? FindCurrentCar();
                if (open != null && car != null) {
                    open.Invoke(null, new object[] { Application.Current.MainWindow, car, car.SelectedSkin });
                    return;
                }
            } catch (Exception e) {
                Logging.Warning("[WorldMap] showcase: " + e.Message);
            }
            Navigate("/Pages/Lists/ShowroomsListPage.xaml");
        }
        #endregion
    }
}
