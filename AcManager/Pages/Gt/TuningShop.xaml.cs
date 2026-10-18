// [fork] GT7風「チューニングショップ」 — GT7実機リファレンス(gt7-ref/car_settings/ref_7_tuning_shop_parts_grid_4k.jpg / from_video t1050,t1060)準拠。
//   上=共通ヘッダー → 店の看板(赤ドット地) → カテゴリタブ(5つ・L1/R1) → 左=パーツカード4列グリッド / 右=車のカード(スペック・変化後は青)。
//   カードを押すと中央に詳細(名前/一言/商品写真/PP 変化/取り付ける)。購入・クレジットは出さない（要望 2026-09-05）。
//   データ層は GtTuning（装着状態 json・PP・コースイン時の反映）。URI: /Pages/Gt/TuningShop.xaml（現在の愛車が対象）。Esc でワールドマップへ。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AcManager.Tools.Objects;
using AcTools.DataFile;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    public partial class TuningShop {
        private CarObject _car;
        private int _cat;
        private List<GtPart> _parts = new List<GtPart>();
        private readonly List<GtFocusItem> _cardFocus = new List<GtFocusItem>();
        private readonly List<GtFocusItem> _detailFocus = new List<GtFocusItem>();
        private readonly List<Border> _tabs = new List<Border>();
        private int _cardIndex;
        private GtPart _detail;
        private bool _gone;

        private static readonly Brush TabIdle = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E)));
        private static readonly Brush TabActive = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)));
        private static readonly Brush GreenBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0xA0, 0x50)));
        private static readonly Brush BlueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xF0)));
        private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x60)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        public TuningShop() {
            InitializeComponent();
            GtHints.Bind(HintBack, "Esc  戻る");   // [fork] パッド中は ○ 表記
            GtHints.Bind(HintTabs, "Q / E  カテゴリ", "L1 / R1  カテゴリ");
            Loaded += OnLoaded;
            Unloaded += (s, e) => {
                _gone = true;
                GtFocus.Clear(this);
                GtChrome.Leave();
            };
            TabPrev.MouseLeftButtonUp += (s, e) => { ShowCat(_cat - 1); GtSfx.PlayMove(); };
            TabNext.MouseLeftButtonUp += (s, e) => { ShowCat(_cat + 1); GtSfx.PlayMove(); };
            DetailPrimary.MouseLeftButtonUp += (s, e) => { e.Handled = true; DetailAction(); };
            DetailSecondary.MouseLeftButtonUp += (s, e) => { e.Handled = true; GtSfx.PlayCancel(); CloseDetail(); };
            DetailPrimary.MouseEnter += (s, e) => DetailPrimary.Background = Brushes.White;
            DetailPrimary.MouseLeave += (s, e) => DetailPrimary.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4));
            DetailSecondary.MouseEnter += (s, e) => DetailSecondary.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            DetailSecondary.MouseLeave += (s, e) => DetailSecondary.Background = Brushes.Transparent;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) {
            _gone = false;
            Focus();
            GtChrome.Enter();
            _car = WorldMap.FindCurrentCar();
            BuildTabs();
            BuildCarPanel(null);
            ShowCat(_cat);
        }

        #region Pad focus
        private static Point PadCenter(FrameworkElement e) {
            try {
                if (e == null || !e.IsLoaded || e.ActualWidth <= 0 || e.ActualHeight <= 0) return new Point(0, 0);
                var main = Application.Current?.MainWindow;
                if (main == null) return new Point(0, 0);
                return e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), main);
            } catch {
                return new Point(0, 0);
            }
        }

        private void EnsureVisible(FrameworkElement row) {
            try {
                if (row == null || !row.IsLoaded) return;
                var sv = GridScroll;
                if (sv.ViewportHeight <= 0) return;
                var top = row.TranslatePoint(new Point(0, 0), sv).Y;
                var bottom = top + row.ActualHeight;
                if (top < 0) sv.ScrollToVerticalOffset(sv.VerticalOffset + top - 12);
                else if (bottom > sv.ViewportHeight) sv.ScrollToVerticalOffset(sv.VerticalOffset + (bottom - sv.ViewportHeight) + 12);
            } catch { }
        }

        private bool PageCommand(GtPadCommand cmd) {
            if (_detail != null) {
                if (cmd == GtPadCommand.Back) { GtSfx.PlayCancel(); CloseDetail(); return true; }
                return false;
            }
            if (cmd == GtPadCommand.L1) { ShowCat(_cat - 1); GtSfx.PlayMove(); return true; }
            if (cmd == GtPadCommand.R1) { ShowCat(_cat + 1); GtSfx.PlayMove(); return true; }
            return false;
        }

        private void RegisterCards(int index) {
            if (_gone) return;
            GtFocus.Register(this, _cardFocus, GtFocusLayout.Grid, 4, index);
            GtFocus.SetPageCommand(this, PageCommand);
        }
        #endregion

        #region Tabs
        private void BuildTabs() {
            TabBar.Children.Clear();
            _tabs.Clear();
            for (var i = 0; i < GtTuning.Cats.Length; i++) {
                var idx = i;
                var tab = new Border {
                    Width = 236, Height = 52, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                    Child = new TextBlock {
                        Text = GtTuning.Cats[i], FontSize = 15.5, FontFamily = (FontFamily)FindResource("GtBody"),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = TabIdle
                    }
                };
                tab.MouseLeftButtonUp += (s, e) => { if (idx != _cat) { ShowCat(idx); GtSfx.PlaySelect(); } };
                tab.MouseEnter += (s, e) => { if (idx != _cat) tab.Background = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE4)); };
                tab.MouseLeave += (s, e) => { if (idx != _cat) tab.Background = Brushes.Transparent; };
                TabBar.Children.Add(tab);
                _tabs.Add(tab);
                if (i < GtTuning.Cats.Length - 1) {
                    TabBar.Children.Add(new System.Windows.Shapes.Rectangle {
                        Width = 1, Height = 22, Fill = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)), VerticalAlignment = VerticalAlignment.Center
                    });
                }
            }
        }

        private void PaintTabs() {
            for (var i = 0; i < _tabs.Count; i++) {
                var on = i == _cat;
                _tabs[i].Background = on ? Brushes.White : Brushes.Transparent;
                var tb = _tabs[i].Child as TextBlock;
                if (tb == null) continue;
                tb.Foreground = on ? TabActive : TabIdle;
                tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        #endregion

        #region Cards
        private void ShowCat(int i) {
            if (_car == null) return;
            var n = GtTuning.Cats.Length;
            _cat = ((i % n) + n) % n;
            PaintTabs();
            _parts = GtTuning.PartsFor(_car, GtTuning.Cats[_cat]);
            PartsGrid.Children.Clear();
            _cardFocus.Clear();
            foreach (var p in _parts) PartsGrid.Children.Add(Card(p));
            if (_parts.Count == 0) {
                PartsGrid.Children.Add(new TextBlock {
                    Text = "このカテゴリに付けられるパーツはありません。", FontSize = 13, Foreground = (Brush)FindResource("GtLabel"), Margin = new Thickness(12)
                });
            }
            GridScroll.ScrollToTop();
            if (_cardIndex >= _cardFocus.Count) _cardIndex = 0;
            RegisterCards(_cardIndex);
        }

        /// <summary>カード（GT7実測: 商品写真の暗い面 → 名前帯 → 価格帯。ここでは価格の代わりに PP の変化）。</summary>
        private FrameworkElement Card(GtPart p) {
            var installed = GtTuning.IsInstalled(_car, p.Id);
            var ok = GtTuning.Applicable(_car, p);
            var idle = installed ? GreenBrush : Brushes.Transparent;

            // CM の UI スケール(1.1)込みで 4 列がちょうど収まる幅（GT7 実機も 4 列）
            var root = new Grid { Width = 280 };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(188) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var img = new Border { Background = ProductBackground(), Child = ProductVisual(p, 64) };
            Grid.SetRow(img, 0);
            root.Children.Add(img);

            var name = new Border {
                Background = (Brush)FindResource("GtCardName"), Padding = new Thickness(14, 9, 14, 10),
                Child = new TextBlock {
                    Text = p.Name, FontSize = 13.5, Foreground = (Brush)FindResource("GtValue"), TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = (FontFamily)FindResource("GtBody")
                }
            };
            Grid.SetRow(name, 1);
            root.Children.Add(name);

            var foot = new Border { Background = (Brush)FindResource("GtCardFoot"), Padding = new Thickness(14, 7, 14, 8), Child = FootContent(p, installed, ok) };
            Grid.SetRow(foot, 2);
            root.Children.Add(foot);

            var bd = new Border {
                Child = root, Margin = new Thickness(9), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5),
                BorderBrush = idle, Cursor = Cursors.Hand, Opacity = ok ? 1 : 0.45, Background = Brushes.Transparent
            };
            bd.MouseEnter += (s, e) => { bd.BorderBrush = Brushes.White; Preview(p); };
            bd.MouseLeave += (s, e) => { bd.BorderBrush = idle; Preview(null); };
            bd.MouseLeftButtonUp += (s, e) => { GtSfx.PlaySelect(); OpenDetail(p); };

            var index = _cardFocus.Count;
            _cardFocus.Add(new GtFocusItem {
                Name = p.Name,
                Center = () => PadCenter(bd),
                SetHot = on => {
                    bd.BorderBrush = on ? Brushes.White : idle;
                    if (on) { _cardIndex = index; Preview(p); EnsureVisible(bd); } else Preview(null);
                },
                Activate = () => { GtSfx.PlaySelect(); OpenDetail(p); }
            });
            return bd;
        }

        private static Brush ProductBackground() {
            var b = new RadialGradientBrush {
                Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.75, RadiusY = 0.85
            };
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x3C, 0x3D, 0x43), 0));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x1E, 0x1F, 0x23), 1));
            b.Freeze();
            return b;
        }

        /// <summary>商品写真（Static\gt7\parts\*.png）。無ければピクトグラム。</summary>
        private FrameworkElement ProductVisual(GtPart p, double glyphSize) {
            var bi = LoadPartImage(p);
            if (bi != null) return new Image { Source = bi, Stretch = Stretch.Uniform, Margin = new Thickness(18, 12, 18, 14) };
            return new TextBlock {
                Text = p.Glyph, FontFamily = (FontFamily)FindResource("GtGlyph"), FontSize = glyphSize, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9
            };
        }

        private static readonly Dictionary<string, BitmapImage> ImageCache = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        private static string PartImageFile(GtPart p) {
            var dir = Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "parts");
            string name;
            if (p.TyreIndex >= 0) {
                name = p.Name.StartsWith("コンフォート") ? "tyre_comfort" : p.Name.StartsWith("スポーツ") ? "tyre_sport" : p.Name.StartsWith("レーシング") ? "tyre_racing"
                        : p.Name.StartsWith("インター") ? "tyre_inter" : p.Name.StartsWith("ヘビー") ? "tyre_wet" : "tyre_sport";
            } else {
                name = p.Id;
            }
            var f = Path.Combine(dir, name + ".png");
            if (File.Exists(f)) return f;
            if (!string.IsNullOrEmpty(p.Group)) {
                f = Path.Combine(dir, "_" + p.Group + ".png");
                if (File.Exists(f)) return f;
            }
            return null;
        }

        private static BitmapImage LoadPartImage(GtPart p) {
            try {
                var f = PartImageFile(p);
                if (f == null) return null;
                BitmapImage bi;
                if (ImageCache.TryGetValue(f, out bi)) return bi;
                bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(f, UriKind.Absolute);
                bi.DecodePixelWidth = 640;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                ImageCache[f] = bi;
                return bi;
            } catch {
                return null;
            }
        }

        private FrameworkElement FootContent(GtPart p, bool installed, bool ok) {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var num = (FontFamily)FindResource("GtNum");
            var label = (Brush)FindResource("GtLabel");
            var value = (Brush)FindResource("GtValue");

            if (!ok) {
                g.Children.Add(new TextBlock { Text = p.TurboOnly ? "過給機付きの車専用" : "4WD 専用", FontSize = 11.5, Foreground = label, VerticalAlignment = VerticalAlignment.Center });
                return g;
            }
            if (installed) {
                var l = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = GreenBrush };
                l.Inlines.Add(new System.Windows.Documents.Run("✓ ") { FontFamily = num });
                l.Inlines.Add(new System.Windows.Documents.Run("装着中"));
                g.Children.Add(l);
                if (p.IsValue) {
                    var st = GtTuning.Get(_car);
                    var v = p.Id == "restrictor" ? st.Restrictor + " %" : st.Ballast + " kg";
                    var r = new TextBlock { Text = v, FontFamily = num, FontSize = 16, Foreground = value, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(r, 1);
                    g.Children.Add(r);
                }
                return g;
            }
            if (p.IsValue) {
                g.Children.Add(new TextBlock { Text = "値はカーセッティングで", FontSize = 11.5, Foreground = label, VerticalAlignment = VerticalAlignment.Center });
                return g;
            }
            var cur = GtTuning.Compute(_car);
            var after = GtTuning.Compute(_car, Candidate(p));
            var delta = after.Pp - cur.Pp;
            g.Children.Add(new TextBlock { Text = "PP", FontFamily = num, FontSize = 12, Foreground = label, VerticalAlignment = VerticalAlignment.Center });
            var t = new TextBlock {
                Text = (delta >= 0 ? "+" : "−") + Math.Abs(delta).ToString("0.00", CultureInfo.InvariantCulture), FontFamily = num, FontSize = 17,
                Foreground = Math.Abs(delta) < 0.005 ? label : delta > 0 ? BlueBrush : WarnBrush, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        /// <summary>このパーツを付けた場合の装着リスト（同じグループは外れる）。</summary>
        private List<string> Candidate(GtPart p) {
            var ids = GtTuning.Get(_car).Installed.ToList();
            if (!string.IsNullOrEmpty(p.Group)) ids.RemoveAll(id => GtTuning.Find(_car, id)?.Group == p.Group);
            if (!ids.Contains(p.Id)) ids.Add(p.Id);
            return ids;
        }

        private List<string> Without(GtPart p) {
            var ids = GtTuning.Get(_car).Installed.ToList();
            ids.Remove(p.Id);
            return ids;
        }
        #endregion

        #region Car panel
        private void Preview(GtPart p) {
            if (p != null && (GtTuning.IsInstalled(_car, p.Id) || !GtTuning.Applicable(_car, p) || p.IsValue)) p = null;
            BuildCarPanel(p);
        }

        private string PowerUnitOf() {
            var raw = _car?.SpecsBhp ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", ""), @"[\d.]+\s*(\S*)");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>出力: 元の単位が bhp/hp なら PS 換算（GtHeader と同じ）。</summary>
        private string Ps(double v) {
            var unit = PowerUnitOf();
            var s = GtHeader.PowerPs(Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + " " + unit);
            return s ?? Math.Round(v).ToString("N0", CultureInfo.InvariantCulture);
        }

        private string Kgfm(double nm) {
            var raw = _car?.SpecsTorque ?? "";
            if (raw.IndexOf("nm", StringComparison.OrdinalIgnoreCase) >= 0) return (nm * 0.10197).ToString("0.0", CultureInfo.InvariantCulture) + " kgfm";
            var m = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", ""), @"[\d.]+\s*(\S*)");
            return Math.Round(nm).ToString("N0", CultureInfo.InvariantCulture) + (m.Success && m.Groups[1].Value.Length > 0 ? " " + m.Groups[1].Value : "");
        }

        private static void SplitUnit(string s, out string num, out string unit) {
            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"^([\d.,+\-]+)\s*(.*)$");
            if (m.Success) { num = m.Groups[1].Value; unit = m.Groups[2].Value; } else { num = s ?? ""; unit = ""; }
        }

        private void BuildCarPanel(GtPart preview) {
            if (_car == null) return;
            try {
                BrandText.Text = _car.Brand ?? "";
                if (!string.IsNullOrEmpty(_car.BrandBadge) && File.Exists(_car.BrandBadge)) {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(_car.BrandBadge, UriKind.Absolute);
                    bi.DecodePixelWidth = 64;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    BrandBadgeImg.Source = bi;
                }
                var prev = _car.SelectedSkin?.PreviewImage;
                if (!string.IsNullOrEmpty(prev) && File.Exists(prev)) {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(prev, UriKind.Absolute);
                    bi.DecodePixelWidth = 900;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    CarImg.Source = bi;
                }
                CarNameText.Text = _car.DisplayName;
            } catch { }

            var cur = GtTuning.Compute(_car);
            var after = preview != null ? GtTuning.Compute(_car, Candidate(preview)) : null;
            var label = (Brush)FindResource("GtLabel");
            var value = (Brush)FindResource("GtValue");
            var num = (FontFamily)FindResource("GtNum");

            PpText.Inlines.Clear();
            if (after != null && Math.Abs(after.Pp - cur.Pp) > 0.005) {
                PpText.Inlines.Add(new System.Windows.Documents.Run(after.Pp.ToString("0.00", CultureInfo.InvariantCulture)) { Foreground = BlueBrush });
                PpText.Inlines.Add(new System.Windows.Documents.Run("  ◀  ") { Foreground = BlueBrush, FontSize = 10 });
            }
            PpText.Inlines.Add(new System.Windows.Documents.Run("PP ") { Foreground = label });
            PpText.Inlines.Add(new System.Windows.Documents.Run(cur.Pp > 0 ? cur.Pp.ToString("0.00", CultureInfo.InvariantCulture) : "--") { Foreground = value });

            SpecRows.Children.Clear();
            var dt = GtTuning.IsAwd(_car) ? "4WD" : Drivetrain();
            SpecRows.Children.Add(SpecRow("駆動形式", dt, null, null));
            SpecRows.Children.Add(SpecRow("車両重量", Math.Round(cur.Kg).ToString("N0", CultureInfo.InvariantCulture), "kg",
                    after != null && Math.Abs(after.Kg - cur.Kg) > 0.5 ? Math.Round(after.Kg).ToString("N0", CultureInfo.InvariantCulture) : null));
            string n1, u1; SplitUnit(Ps(cur.Bhp), out n1, out u1);
            string n2, u2; SplitUnit(after != null ? Ps(after.Bhp) : "", out n2, out u2);
            SpecRows.Children.Add(SpecRow("最高出力", n1, u1, after != null && Math.Abs(after.Bhp - cur.Bhp) > 0.5 ? n2 : null));
            SplitUnit(Kgfm(cur.Nm), out n1, out u1);
            SplitUnit(after != null ? Kgfm(after.Nm) : "", out n2, out u2);
            SpecRows.Children.Add(SpecRow("最大トルク", cur.Nm > 0 ? n1 : "--", cur.Nm > 0 ? u1 : null, after != null && Math.Abs(after.Nm - cur.Nm) > 0.5 && cur.Nm > 0 ? n2 : null));
            var pw = cur.Bhp > 0 ? cur.Kg / PsValue(cur.Bhp) : 0;
            var pwAfter = after != null && after.Bhp > 0 ? after.Kg / PsValue(after.Bhp) : 0;
            SpecRows.Children.Add(SpecRow("パワーウェイトレシオ", pw > 0 ? pw.ToString("0.00", CultureInfo.InvariantCulture) : "--", pw > 0 ? "kg/PS" : null,
                    pwAfter > 0 && Math.Abs(pwAfter - pw) > 0.005 ? pwAfter.ToString("0.00", CultureInfo.InvariantCulture) : null));

            var names = GtTuning.Get(_car).Installed.Select(id => GtTuning.Find(_car, id)?.Name).Where(x => x != null).ToList();
            InstalledText.Text = names.Count == 0 ? "なし（ノーマル）" : string.Join("・", names);
        }

        private double PsValue(double bhp) {
            var unit = PowerUnitOf();
            return unit.IndexOf("hp", StringComparison.OrdinalIgnoreCase) >= 0 ? bhp * 1.0139 : bhp;
        }

        private string Drivetrain() {
            try {
                var t = _car.AcdData?.GetIniFile("drivetrain.ini")["TRACTION"].GetPossiblyEmpty("TYPE")?.ToUpperInvariant();
                if (t == "FWD") return "FF";
                if (t == "RWD") return "FR";
                return t ?? "--";
            } catch { return "--"; }
        }

        /// <summary>GT7実測: ラベル左(薄グレー) / 値右(白・単位は小さく)。変化後があれば「青の新値 ◀ 旧値」。</summary>
        private FrameworkElement SpecRow(string label, string value, string unit, string after) {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("GtLabel"), VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(l);
            var vp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var num = (FontFamily)FindResource("GtNum");
            if (after != null) {
                vp.Children.Add(new TextBlock { Text = after, FontSize = 14.5, FontWeight = FontWeights.SemiBold, FontFamily = num, Foreground = BlueBrush });
                vp.Children.Add(new TextBlock { Text = " ◀ ", FontSize = 9.5, Foreground = BlueBrush, VerticalAlignment = VerticalAlignment.Center });
            }
            vp.Children.Add(new TextBlock { Text = value, FontSize = 14.5, FontWeight = FontWeights.SemiBold, FontFamily = num, Foreground = (Brush)FindResource("GtValue") });
            if (!string.IsNullOrEmpty(unit)) {
                vp.Children.Add(new TextBlock { Text = " " + unit, FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
            }
            Grid.SetColumn(vp, 1);
            g.Children.Add(vp);
            return g;
        }
        #endregion

        #region Detail
        private void OpenDetail(GtPart p) {
            if (p == null || _car == null) return;
            _detail = p;
            var installed = GtTuning.IsInstalled(_car, p.Id);
            var ok = GtTuning.Applicable(_car, p);
            DetailName.Text = p.Name;
            DetailDesc.Text = p.Desc;
            var bi = LoadPartImage(p);
            DetailImg.Source = bi;
            DetailImg.Visibility = bi != null ? Visibility.Visible : Visibility.Collapsed;
            DetailGlyph.Text = p.Glyph;
            DetailGlyph.Visibility = bi != null ? Visibility.Collapsed : Visibility.Visible;

            var cur = GtTuning.Compute(_car);
            var after = installed ? GtTuning.Compute(_car, Without(p)) : GtTuning.Compute(_car, Candidate(p));
            DetailPpBefore.Text = cur.Pp > 0 ? cur.Pp.ToString("0.00", CultureInfo.InvariantCulture) : "--";
            var diff = ok && Math.Abs(after.Pp - cur.Pp) > 0.005;
            DetailPpAfter.Text = after.Pp.ToString("0.00", CultureInfo.InvariantCulture);
            DetailPpAfter.Visibility = diff ? Visibility.Visible : Visibility.Collapsed;
            DetailPpArrow.Visibility = diff ? Visibility.Visible : Visibility.Collapsed;

            DetailPrimaryText.Text = installed ? "取り外す" : "取り付ける";
            DetailPrimary.Opacity = ok ? 1 : 0.4;
            DetailNote.Text = !ok ? (p.TurboOnly ? "過給機付きの車専用のパーツです" : "4WD 専用のパーツです")
                    : p.IsValue ? "値はカーセッティングのシートで決めます" : p.HasPhysics ? "" : "走りには影響しません（GT7 の見た目合わせ）";

            DetailOverlay.Visibility = Visibility.Visible;
            DetailOverlay.Opacity = 0;
            DetailOverlay.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));

            _detailFocus.Clear();
            _detailFocus.Add(new GtFocusItem {
                Name = "secondary", Center = () => PadCenter(DetailSecondary),
                SetHot = on => DetailSecondary.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)),
                Activate = () => { GtSfx.PlayCancel(); CloseDetail(); }
            });
            _detailFocus.Add(new GtFocusItem {
                Name = "primary", Center = () => PadCenter(DetailPrimary),
                SetHot = on => DetailPrimary.BorderBrush = on ? BlueBrush : Brushes.Transparent,
                Activate = DetailAction
            });
            GtFocus.Register(this, _detailFocus, GtFocusLayout.Row, 0, 1);
            GtFocus.SetPageCommand(this, PageCommand);
        }

        private void CloseDetail() {
            if (_detail == null) return;
            _detail = null;
            DetailOverlay.Visibility = Visibility.Collapsed;
            RegisterCards(_cardIndex);
        }

        private void DetailAction() {
            var p = _detail;
            if (p == null || _car == null) return;
            if (GtTuning.IsInstalled(_car, p.Id)) {
                if (!GtTuning.Uninstall(_car, p)) { TuningSaveFailed(); return; }
                GtSfx.PlayCancel();
            } else {
                if (!GtTuning.Applicable(_car, p)) { GtSfx.PlayCancel(); return; }
                if (!GtTuning.Install(_car, p)) { TuningSaveFailed(); return; }
                GtSfx.PlaySelect();
            }
            _detail = null;
            DetailOverlay.Visibility = Visibility.Collapsed;
            ShowCat(_cat);          // カードを作り直す（装着中の緑枠・PP 差分）
            BuildCarPanel(null);
            try { Header.Refresh(); } catch { }
        }

        private static void TuningSaveFailed() {
            GtSfx.PlayCancel();
            NonfatalError.Notify("装着状態を保存できませんでした", "装着状態は変更していません。保存先の空き容量と書き込み権限を確認してください。");
        }

        private void OnOverlayClick(object sender, MouseButtonEventArgs e) {
            GtSfx.PlayCancel();
            CloseDetail();
        }

        private void OnDetailClick(object sender, MouseButtonEventArgs e) {
            e.Handled = true;   // カードの外側クリック（閉じる）に伝播させない
        }
        #endregion

        private void OnKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                if (_detail != null) { GtSfx.PlayCancel(); CloseDetail(); return; }
                GtNav.GoBack();
                return;
            }
            if (_detail != null) {
                if (e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; DetailAction(); }
                return;
            }
            if (e.Key == Key.Q || e.Key == Key.PageUp) { e.Handled = true; ShowCat(_cat - 1); GtSfx.PlayMove(); }
            else if (e.Key == Key.E || e.Key == Key.PageDown) { e.Handled = true; ShowCat(_cat + 1); GtSfx.PlayMove(); }
        }
    }
}
