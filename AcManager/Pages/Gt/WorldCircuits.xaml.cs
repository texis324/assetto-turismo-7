// [fork] GT7 風「ワールドサーキット」— 地球儀背景に地域ダイヤル3つ（アメリカ／ヨーロッパ／アジア・オセアニア）。
//   各ダイヤル = 「走行済みロケーション数 / 地域のロケーション数」。ホバーで地域のコース名、クリックでコース一覧（country フィルタ）へ。
//   地域判定は ui_track.json の country（表記揺れ吸収。Data (User)\Track Categories の大陸分けと同じ国リスト）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows.Controls;
using FirstFloor.ModernUI.Windows.Media;

namespace AcManager.Pages.Gt {
    public partial class WorldCircuits {
        public static readonly Uri PageUri = new Uri("/Pages/Gt/WorldCircuits.xaml", UriKind.Relative);

        private static readonly Brush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xB4, 0x74)));
        private static readonly Brush Ivory = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xE1)));
        private static readonly Brush DialFill = Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0x10, 0x24, 0x44)));
        private static readonly Brush DialFillHover = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x16, 0x32, 0x5C)));
        private static readonly Brush DialRing = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0xCF, 0xE3, 0xFF)));
        private static readonly FontFamily NumFont = new FontFamily("Bahnschrift SemiCondensed, Yu Gothic UI");
        private static readonly FontFamily HeadFont = new FontFamily("Bahnschrift Light SemiCondensed, Yu Gothic UI Light");

        // [fork] チャンピオンシップボタンの通常色（XAML の Background="#E6D7DBDF" と同じ）。パッドのフォーカス表現で戻す先
        private static readonly Brush ChampionshipIdle = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0xD7, 0xDB, 0xDF)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        private class Region {
            public string Key, Name;
            public string[] Countries;      // 小文字・表記揺れ込み
            public List<TrackObject> Tracks = new List<TrackObject>();
            public int Driven;
            public string Filter => string.Join(" | ", Countries.Select(c => c.Contains(" ") || c.Contains(".") ? "country:\"" + c + "\"" : "country:" + c));
        }

        private static readonly Region[] Regions = {
            new Region { Key = "am", Name = "アメリカ", Countries = new[] {
                "usa", "u.s.a.", "united states", "united states of america", "america", "canada", "mexico",
                "brazil", "argentina", "chile", "uruguay", "peru", "colombia", "venezuela", "bolivia", "ecuador" } },
            new Region { Key = "eu", Name = "ヨーロッパ", Countries = new[] {
                "italy", "germany", "france", "spain", "great britain", "united kingdom", "uk", "england", "scotland", "wales", "ireland", "belgium",
                "netherlands", "austria", "switzerland", "portugal", "azerbaijan", "sweden", "norway", "finland", "denmark", "poland", "czech republic", "czechia",
                "hungary", "russia", "romania", "greece", "monaco", "luxembourg", "croatia", "slovakia", "slovenia", "estonia", "latvia", "lithuania",
                "serbia", "turkey", "south africa", "morocco", "egypt", "kenya", "nigeria", "tunisia", "algeria" } },
            new Region { Key = "ap", Name = "アジア・オセアニア", Countries = new[] {
                "japan", "china", "korea", "south korea", "north korea", "taiwan", "hong kong", "macau", "thailand", "vietnam", "indonesia", "malaysia",
                "singapore", "philippines", "india", "sri lanka", "bahrain", "qatar", "saudi arabia", "united arab emirates", "uae", "abu dhabi", "dubai",
                "australia", "new zealand" } },
        };


        public WorldCircuits() {
            InitializeComponent();
            GtHints.Bind(HintBack, "Esc  ワールドマップへ戻る");   // [fork] パッド中は ○ 表記
        }

        private async void OnLoaded(object sender, RoutedEventArgs e) {
            GtChrome.Enter();
            try {
                GlobeImage.Source = WorldMap.LoadBitmap(System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "globe.jpg"), 0);
            } catch (Exception ex) { Logging.Warning("[WorldCircuits] globe: " + ex.Message); }

            // GT7に無い下部ナビは廃止(要望 2026-08-25)。戻る=Esc、コース一覧はハーバーから
            Root.Opacity = 0;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(450)) { EasingFunction = new SineEase() });
            Focus();

            try {
                await TracksManager.Instance.EnsureLoadedAsync();
                Classify();
                BuildDials();
            } catch (Exception ex) {
                Logging.Warning("[WorldCircuits] tracks: " + ex.Message);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            GtFocus.Clear(this);   // [fork] パッドのフォーカスを手放す
            GtChrome.Leave();
        }

        #region パッド（GtFocus）
        // [fork] 地域ダイヤル3つ + チャンピオンシップ を空間移動（Spatial）で結ぶ。
        //   ダイヤルのホバー表現は SetHot(bool) に切り出して MouseEnter/Leave と GtFocusItem の両方から呼ぶ。
        private readonly List<GtFocusItem> _dialFocus = new List<GtFocusItem>();

        private void RegisterFocus() {
            try {
                if (!IsLoaded) return;   // await の裏でページが変わっていたら触らない
                var items = new List<GtFocusItem>(_dialFocus) {
                    new GtFocusItem {
                        Name = "チャンピオンシップ",
                        Center = () => WorldMap.ElementCenter(ChampionshipButton),
                        SetHot = SetChampionshipHot,
                        Activate = () => WorldMap.Navigate("/Pages/Drive/UserChampionships.xaml")
                    }
                };
                var initial = _dialFocus.FindIndex(f => f.Enabled);   // コースがある一番左の地域から
                GtFocus.Register(this, items, GtFocusLayout.Spatial, 0, initial < 0 ? 0 : initial);
            } catch (Exception ex) {
                Logging.Warning("[WorldCircuits] focus: " + ex.Message);
            }
        }

        /// <summary>[fork] チャンピオンシップのフォーカス表現。マウスにはもともとホバー表現が無いので、
        ///   ここは GtFocus からだけ呼ぶ（既存のマウス経路は変えない）。枠を足すとレイアウトが動くのでグローで出す。</summary>
        private void SetChampionshipHot(bool hot) {
            try {
                ChampionshipButton.Effect = hot
                        ? new DropShadowEffect { BlurRadius = 26, ShadowDepth = 0, Opacity = 0.95, Color = Color.FromRgb(0xBF, 0xE4, 0xFF) }
                        : null;
                ChampionshipButton.Background = hot ? Brushes.White : ChampionshipIdle;
            } catch (Exception ex) {
                Logging.Warning("[WorldCircuits] championship hot: " + ex.Message);
            }
        }
        #endregion


        private int _unknown;

        private void Classify() {
            foreach (var r in Regions) { r.Tracks.Clear(); r.Driven = 0; }
            _unknown = 0;
            foreach (var track in TracksManager.Instance.Enabled.ToList()) {
                var country = (track.Country ?? "").Trim().ToLowerInvariant();
                var region = Regions.FirstOrDefault(r => r.Countries.Contains(country));
                if (region == null) {
                    _unknown++;
                    continue;
                }
                region.Tracks.Add(track);
                var driven = track.TotalDrivenDistance > 0
                        || (track.MultiLayouts != null && track.MultiLayouts.Any(l => l.TotalDrivenDistance > 0));
                if (driven) region.Driven++;
            }
            Logging.Write("[WorldCircuits] " + string.Join(", ", Regions.Select(r => r.Name + "=" + r.Driven + "/" + r.Tracks.Count)) + ", unknown=" + _unknown);
        }

        private void BuildDials() {
            Dials.Children.Clear();
            _dialFocus.Clear();
            var delay = 0;
            foreach (var region in Regions) {
                GtFocusItem focus;
                var dial = CreateDial(region, out focus);
                _dialFocus.Add(focus);
                dial.Opacity = 0;
                var st = new ScaleTransform(0.9, 0.9);
                dial.RenderTransformOrigin = new Point(0.5, 0.5);
                dial.RenderTransform = st;
                var begin = TimeSpan.FromMilliseconds(150 + delay);
                dial.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)) { BeginTime = begin });
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(500)) { BeginTime = begin, EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(500)) { BeginTime = begin, EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
                Dials.Children.Add(dial);
                delay += 120;
            }

            RegisterFocus();
        }

        private const double DialSize = 300;
        private const double RingWidth = 46;

        // GT7実測の配色（gt7-ref ref_1）: 達成=明るい白青 / 未達=濃紺 / 目盛り=暗紺 / 内円盤=半透明紺
        private static readonly Color RingBrightA = Color.FromRgb(0xE2, 0xEA, 0xF0);
        private static readonly Color RingBrightB = Color.FromRgb(0xAF, 0xC6, 0xD6);
        private static readonly Brush RingDark = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x29, 0x3E)));
        private static readonly Brush RingBorder = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0x0C, 0x1A, 0x2A)));
        private static readonly Brush TickBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x24, 0x3A)));
        private static readonly Brush InnerDisc = Freeze(new SolidColorBrush(Color.FromArgb(0xC8, 0x24, 0x44, 0x62)));

        /// <summary>ドーナツの進捗弧（12時から時計回りに p まで）。p>=1 は全周。</summary>
        private static Geometry DonutArc(double size, double ringWidth, double p) {
            var cx = size / 2; var cy = size / 2;
            var R = size / 2; var r = size / 2 - ringWidth;
            if (p >= 0.999) {
                return new CombinedGeometry(GeometryCombineMode.Exclude,
                        new EllipseGeometry(new Point(cx, cy), R, R),
                        new EllipseGeometry(new Point(cx, cy), r, r));
            }
            if (p <= 0.001) return Geometry.Empty;
            var a = p * 360.0 - 90.0;                    // 終端角（度・-90=12時）
            var rad = a * Math.PI / 180.0;
            Point P(double radius, double angRad) => new Point(cx + radius * Math.Cos(angRad), cy + radius * Math.Sin(angRad));
            var startO = new Point(cx, cy - R);
            var endO = P(R, rad);
            var endI = P(r, rad);
            var startI = new Point(cx, cy - r);
            var large = p > 0.5;
            var fig = new PathFigure { StartPoint = startO, IsClosed = true };
            fig.Segments.Add(new ArcSegment(endO, new Size(R, R), 0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(endI, true));
            fig.Segments.Add(new ArcSegment(startI, new Size(r, r), 0, large, SweepDirection.Counterclockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            return g;
        }

        /// <summary>[fork] focus = パッド用のフォーカス候補（GtFocus）。ホバー表現は SetHot(bool) に切り出して
        ///   MouseEnter/Leave と GtFocusItem の両方から呼ぶ。</summary>
        private FrameworkElement CreateDial(Region region, out GtFocusItem focus) {
            var hoverScale = new ScaleTransform(1, 1);
            var outer = new Grid { Width = DialSize + 44, Height = DialSize + 44, Margin = new Thickness(34, 0, 34, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                RenderTransformOrigin = new Point(0.5, 0.5) };
            var inner = new Grid { Width = DialSize, Height = DialSize, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = hoverScale };

            var p = region.Tracks.Count == 0 ? 0 : (double)region.Driven / region.Tracks.Count;

            // 1) 未達側の全周ドーナツ（下地）
            inner.Children.Add(new System.Windows.Shapes.Path { Data = DonutArc(DialSize, RingWidth, 1.0), Fill = RingDark });
            // 2) 達成分の明るい弧（上=明るい縦グラデ）
            var bright = new LinearGradientBrush(RingBrightA, RingBrightB, 90);
            bright.Freeze();
            inner.Children.Add(new System.Windows.Shapes.Path { Data = DonutArc(DialSize, RingWidth, p), Fill = bright });
            // 3) リングの内外の細い縁
            inner.Children.Add(new Ellipse { Stroke = RingBorder, StrokeThickness = 1.5 });
            inner.Children.Add(new Ellipse { Stroke = RingBorder, StrokeThickness = 1.5, Margin = new Thickness(RingWidth) });
            // 4) 時計風の目盛り（16本・リングを横切る短線）
            var ticks = new Canvas { Width = DialSize, Height = DialSize };
            for (var i = 0; i < 16; i++) {
                var line = new Line { X1 = DialSize / 2, Y1 = 3, X2 = DialSize / 2, Y2 = 3 + RingWidth * 0.55, Stroke = TickBrush, StrokeThickness = 2.2 };
                line.RenderTransform = new RotateTransform(i * 22.5, DialSize / 2, DialSize / 2);
                ticks.Children.Add(line);
            }
            inner.Children.Add(ticks);
            // 5) 内側の半透明円盤
            inner.Children.Add(new Ellipse { Fill = InnerDisc, Margin = new Thickness(RingWidth + 4) });

            // 中身: 地域名・n / N・ロケーション（GT7はエンブレム無し）
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock { Text = region.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
                Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = 0.7 } });
            var counter = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextAlignment = TextAlignment.Center };
            counter.Inlines.Add(new System.Windows.Documents.Run(region.Driven.ToString()) { FontFamily = HeadFont, FontSize = 42, Foreground = Brushes.White });
            counter.Inlines.Add(new System.Windows.Documents.Run(" / " + region.Tracks.Count) { FontFamily = NumFont, FontSize = 19, Foreground = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), BaselineAlignment = System.Windows.BaselineAlignment.Baseline });
            content.Children.Add(counter);
            content.Children.Add(new TextBlock { Text = "ロケーション", FontSize = 11, Foreground = Brushes.White, Opacity = 0.7, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            inner.Children.Add(content);
            outer.Children.Add(inner);

            void SetHot(bool hot) {
                if (hot) {
                    // GT7の選択表現=外側のシアン白グロー
                    inner.Effect = new DropShadowEffect { BlurRadius = 46, ShadowDepth = 0, Opacity = 0.85, Color = Color.FromRgb(0xBF, 0xE4, 0xFF) };
                    hoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(180)) { EasingFunction = new SineEase() });
                    hoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(180)) { EasingFunction = new SineEase() });
                    TrackNames.Text = region.Tracks.Count == 0 ? "（この地域のコースはありません）"
                            : string.Join("  ", region.Tracks.OrderBy(t => t.DisplayName)
                                    .Select(t => "［" + System.Text.RegularExpressions.Regex.Replace(t.DisplayName, @"\s*\(\d+\)\s*$", "") + "］"));
                } else {
                    inner.Effect = null;
                    hoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(220)));
                    hoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(220)));
                    TrackNames.Text = "地域を選んでください";
                }
            }

            void Activate() {
                if (region.Tracks.Count == 0) return;
                WorldMap.Navigate("/Pages/Gt/RegionMap.xaml?Region=" + region.Key);
            }

            outer.MouseEnter += (s, e) => SetHot(true);
            outer.MouseLeave += (s, e) => SetHot(false);
            outer.MouseLeftButtonUp += (s, e) => {
                e.Handled = true;
                Activate();
            };

            focus = new GtFocusItem {
                Name = region.Name,
                Center = () => WorldMap.ElementCenter(outer),
                SetHot = SetHot,
                Activate = Activate,
                Enabled = region.Tracks.Count > 0   // コースが無い地域は押しても何も起きないので候補から外す
            };
            return outer;
        }

        private void OnChampionshipClick(object sender, MouseButtonEventArgs e) {
            WorldMap.Navigate("/Pages/Drive/UserChampionships.xaml");
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                WorldMap.Navigate("/Pages/Gt/WorldMap.xaml");
            }
        }
    }
}
