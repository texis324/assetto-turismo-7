// [fork] GT7 風「地域マップ」— ワールドサーキットで地域を選ぶと、その地域の地形図に寄ってコースのピンが立つ。
//   地形図 = Data\Static\gt7\region_{eu,am,ap}.jpg（2026-09-27〜 Natural Earth の海岸線＋陰影起伏をメルカトルで。Pictures\cm-theme\tools\build_region_maps.py）。
//   ピン位置 = 緯度経度→px（切り出し範囲は Bounds 表）。座標は ①自前の座標表（実在サーキット） ②ui_track.json の geotags（有効なもの）
//   ③国の中心（フォールバック）の順。同じ場所に複数ある時は少し散らす。
//   ホバーでカード（コース図・国旗・国名・コース名）、クリックで下部パネル（レイアウト選択）→「ドライブ」でクイックドライブへ。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AcManager.Controls;
using AcManager.Pages.Drive;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows;

namespace AcManager.Pages.Gt {
    public partial class RegionMap : IParametrizedUriContent {
        private static readonly Brush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xB4, 0x74)));
        private static readonly Brush Ivory = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xE1)));
        private static readonly Brush PinRed = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x3A, 0x2F)));
        private static readonly Brush PinCore = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0xD0)));
        private const double GlowIdle = 0.55, GlowHot = 0.95;   // 光の輪の濃さ（ふだん／選択中）。旧・鼓動は 0.35〜0.8
        private const double PinGlowSize = 74;   // 44px の丸に半径 18 のぼかしを掛けた広がりと同じくらい（ピンの Grid と同じ大きさ）
        private static readonly Brush PinGlow = Freeze(new RadialGradientBrush(new GradientStopCollection {
            new GradientStop(Color.FromArgb(0xFF, 0xE8, 0x3A, 0x2F), 0.0),
            new GradientStop(Color.FromArgb(0xE0, 0xE8, 0x3A, 0x2F), 0.3),
            new GradientStop(Color.FromArgb(0x70, 0xE8, 0x3A, 0x2F), 0.62),
            new GradientStop(Color.FromArgb(0x00, 0xE8, 0x3A, 0x2F), 1.0)
        }));
        private static readonly FontFamily NumFont = new FontFamily("Bahnschrift SemiCondensed, Yu Gothic UI");
        private static readonly FontFamily HeadFont = new FontFamily("Bahnschrift Light SemiCondensed, Yu Gothic UI Light");
        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        private class RegionDef {
            public string Key, Title, Image;
            public double Lon0, Lon1, Lat0, Lat1;   // 切り出し範囲（メルカトル。絵は Pictures/cm-theme/tools/build_region_maps.py が同じ範囲で作る）
            public Func<string, bool> Contains;       // 国名(小文字)が属するか
        }

        private static readonly string[] AmericaCountries = { "usa", "u.s.a.", "united states", "united states of america", "america", "canada", "mexico",
            "brazil", "argentina", "chile", "uruguay", "peru", "colombia", "venezuela", "bolivia", "ecuador" };
        private static readonly string[] EuropeCountries = { "italy", "germany", "france", "spain", "great britain", "united kingdom", "uk", "england", "scotland",
            "wales", "ireland", "belgium", "netherlands", "austria", "switzerland", "portugal", "azerbaijan", "sweden", "norway", "finland", "denmark", "poland", "czech republic",
            "czechia", "hungary", "russia", "romania", "greece", "monaco", "luxembourg", "croatia", "slovakia", "slovenia", "estonia", "latvia", "lithuania", "serbia",
            "turkey", "south africa", "morocco", "egypt", "kenya", "nigeria", "tunisia", "algeria" };
        private static readonly string[] AsiaCountries = { "japan", "china", "korea", "south korea", "north korea", "taiwan", "hong kong", "macau", "thailand",
            "vietnam", "indonesia", "malaysia", "singapore", "philippines", "india", "sri lanka", "bahrain", "qatar", "saudi arabia", "united arab emirates", "uae",
            "abu dhabi", "dubai", "australia", "new zealand" };

        private static readonly RegionDef[] Regions = {
            // [fork] 2026-09-27 作り直し（本人「アメリカのマップが非リアル」）: 陸と海の境を Natural Earth の海岸線に、図法をメルカトルに。
            //   範囲は画面の縦横比 1.95 に合わせてある（旧: am -150..10/-36..56・eu -12..38/34..62・ap 118..154/25..45.2 の等距円筒）。
            //   範囲外の国（南米・豪州・中東等）は縁に寄せて表示
            new RegionDef { Key = "am", Title = "アメリカ", Image = "region_am.jpg", Lon0 = -142.736, Lon1 = -57.264, Lat0 = 17, Lat1 = 52, Contains = c => AmericaCountries.Contains(c) },
            new RegionDef { Key = "eu", Title = "ヨーロッパ", Image = "region_eu.jpg", Lon0 = -26.523, Lon1 = 42.523, Lat0 = 36.5, Lat1 = 59.6, Contains = c => EuropeCountries.Contains(c) },
            // [fork] 日本中心（要望「ほぼ日本しか走ってない」2026-08-24）
            new RegionDef { Key = "ap", Title = "アジア・オセアニア", Image = "region_ap.jpg", Lon0 = 118.640, Lon1 = 156.360, Lat0 = 28.5, Lat1 = 44, Contains = c => AsiaCountries.Contains(c) },
        };

        /// <summary>実在サーキットの座標（コース ID の前方一致）。架空コースは設定上の土地。</summary>
        private static readonly (string prefix, double lat, double lon)[] KnownCoords = {
            ("imola", 44.3439, 11.7167), ("ks_drag", 44.60, 11.30), ("ks_barcelona", 41.5700, 2.2611), ("ks_black_cat_county", 35.30, -118.90),
            ("ks_brands_hatch", 51.3569, 0.2631), ("ks_highlands", 57.10, -4.70), ("ks_laguna_seca", 36.5844, -121.7534), ("ks_monza66", 45.6450, 9.2500),
            ("ks_nordschleife", 50.3356, 6.9475), ("ks_nurburgring", 50.3100, 7.0000), ("ks_red_bull_ring", 47.2197, 14.7647), ("ks_silverstone1967", 52.0950, -0.9900),
            ("ks_silverstone", 52.0786, -1.0169), ("ks_vallelunga", 42.1594, 12.3694), ("ks_zandvoort", 52.3888, 4.5409), ("magione", 43.0614, 12.2458),
            ("monza", 45.6156, 9.2811), ("mugello", 43.9975, 11.3714), ("spa", 50.4372, 5.9714), ("trento-bondone", 46.0600, 11.0500),
            ("bristol_motor_speedway", 36.5156, -82.2592), ("la_canyons", 34.2619, -118.1125), ("drift", 35.70, 139.90), ("am_tsukuba", 36.1505, 139.9241),
            ("hakone_nanamagari", 35.2300, 139.0300), ("meihan", 34.6334, 135.9963), ("90sgdsp_happogahara", 36.8300, 139.7300),
            // 蝙蝠峠＝伊賀市（旧大山田）と亀山市（加太）の境。geotags が空で日本の真ん中（長野）に出ていた（要望 2026-10-15「蝙蝠峠は伊賀だよ」）。名阪も geotags の実座標へ
            ("90sgdsp_koumori_touge", 34.7850, 136.2600), ("rt_fuji_speedway", 35.3717, 138.9267),
            ("abu_dhabi", 24.4672, 54.6031), ("baku", 40.3725, 49.8533),   // ヤス・マリーナ（2026-09-27 追加。f1_2024 レイアウトの geotags はジェッダの座標になっている）
            ("rt_suzuka", 34.8431, 136.5411),("shutoko_revival_project", 35.6600, 139.7800), ("shuto", 35.6600, 139.7800), ("pk_akina", 36.4936, 138.9040),
            ("akina", 36.4936, 138.9040), ("daytona", 29.1852, -81.0705), ("sebring", 27.4547, -81.3483), ("road_america", 43.7980, -87.9898),
            ("watkins", 42.3369, -76.9272), ("indianapolis", 39.7951, -86.2347), ("interlagos", -23.7036, -46.6997), ("bathurst", -33.4496, 149.5547),
            ("le_mans", 47.9500, 0.2244), ("lemans", 47.9500, 0.2244), ("paul_ricard", 43.2506, 5.7917), ("hockenheim", 49.3278, 8.5656), ("donington", 52.8306, -1.3753),
            ("okayama", 34.9150, 134.2200), ("ebisu", 37.6400, 140.3200), ("sportsland_sugo", 38.1400, 140.7800), ("autopolis", 33.0400, 131.1400),
        };

        private static readonly Dictionary<string, (double lat, double lon)> CountryCenters = new Dictionary<string, (double, double)> {
            ["japan"] = (36.2, 138.3), ["italy"] = (42.8, 12.5), ["usa"] = (39.8, -98.6), ["u.s.a."] = (39.8, -98.6), ["united states"] = (39.8, -98.6),
            ["america"] = (39.8, -98.6), ["germany"] = (51.1, 10.4), ["great britain"] = (52.8, -1.5), ["united kingdom"] = (52.8, -1.5), ["uk"] = (52.8, -1.5),
            ["england"] = (52.5, -1.5), ["scotland"] = (56.5, -4.2), ["wales"] = (52.3, -3.7), ["ireland"] = (53.3, -8.0), ["spain"] = (40.4, -3.7),
            ["belgium"] = (50.6, 4.6), ["netherlands"] = (52.2, 5.3), ["austria"] = (47.5, 14.5), ["france"] = (46.6, 2.4), ["switzerland"] = (46.8, 8.2),
            ["portugal"] = (39.6, -8.2), ["sweden"] = (60.1, 15.0), ["norway"] = (61.0, 9.0), ["finland"] = (62.0, 26.0), ["denmark"] = (56.0, 10.0),
            ["poland"] = (52.0, 19.0), ["czech republic"] = (49.8, 15.5), ["czechia"] = (49.8, 15.5), ["hungary"] = (47.2, 19.5), ["russia"] = (55.7, 37.6),
            ["monaco"] = (43.73, 7.42), ["turkey"] = (39.0, 35.0), ["greece"] = (39.0, 22.0), ["south africa"] = (-29.0, 25.0), ["morocco"] = (32.0, -6.0),
            ["australia"] = (-25.3, 133.8), ["new zealand"] = (-41.0, 174.0), ["china"] = (35.9, 104.2), ["south korea"] = (36.5, 127.9), ["korea"] = (36.5, 127.9),
            ["taiwan"] = (23.7, 121.0), ["thailand"] = (15.8, 100.9), ["malaysia"] = (4.2, 101.9), ["singapore"] = (1.35, 103.8), ["indonesia"] = (-2.5, 118.0),
            ["india"] = (20.6, 79.0), ["bahrain"] = (26.0, 50.5), ["qatar"] = (25.3, 51.2), ["united arab emirates"] = (24.0, 54.0), ["uae"] = (24.0, 54.0),
            ["abu dhabi"] = (24.4, 54.4), ["dubai"] = (25.2, 55.3), ["canada"] = (56.1, -106.3), ["mexico"] = (23.6, -102.5), ["brazil"] = (-14.2, -51.9),
            ["argentina"] = (-34.6, -64.0), ["chile"] = (-33.5, -70.7),
        };

        private class Pin {
            public TrackObject Track;
            public double Lat, Lon;
            public FrameworkElement Element;
            public FrameworkElement Label;
            public Canvas Leader;   // ピンとカードを結ぶ線（要望 2026-09-27「線で引いてカードと点を繋げて」）
            public Point Pt;        // ピンの位置（キャンバス座標）
            public Size CardSize;
            public Rect CardRect;   // LayoutCards が決める
            public Action UpdateLeader;
            public bool Placed;     // 置き場所が決まるまでカードと線は出さない
            public bool Outside;    // 本当の場所は地図の外（端に置いて外向きの矢印）
            public double Angle;    // その矢印の向き（度・右 = 0・時計回り）

            /// <summary>[fork] ホバーと同じ見た目の切替（パッドのフォーカス表現）。マウス経路と同じ実体を入れる。</summary>
            public Action<bool> SetHot;
        }

        /// <summary>[fork] 下パネルのチップ 1 個ぶん（パッドのフォーカス候補にする）。マウス経路は変えない。</summary>
        private sealed class PanelItem {
            public string Name;
            public FrameworkElement Element;
            public Action<bool> SetHot;
            public Action Activate;
        }

        private RegionDef _region;
        private readonly List<Pin> _pins = new List<Pin>();
        private Border _card;
        private TrackObjectBase _selected;

        // [fork] パッド操作: 下パネルが開いている間は候補をチップ行に差し替える
        private readonly List<PanelItem> _panelItems = new List<PanelItem>();
        private bool _panelOpen;

        public RegionMap() {
            InitializeComponent();
            // [fork] パッド中は ○ / × 表記に切り替わる（HintText はカード選択中にコース名で上書きされるので SetHint 経由）
            GtHints.Bind(HintBack, "Esc  ワールドサーキットへ戻る");
            SetHint(null);
        }

        /// <summary>[fork] 右下ヒント。null/空 = 既定文（パッド中は記号表記）。
        /// コース名はヒントではないので置換に通さず、そのまま入れる。</summary>
        private void SetHint(string cardText) {
            if (string.IsNullOrEmpty(cardText)) GtHints.Bind(HintText, "コースをクリック");
            else HintText.Text = cardText;
        }

        public void OnUri(Uri uri) {
            var key = uri.GetQueryParam("Region") ?? "eu";
            _region = Regions.FirstOrDefault(r => r.Key == key) ?? Regions[1];
        }

        private async void OnLoaded(object sender, RoutedEventArgs e) {
            GtChrome.Enter();
            GtTestChannel.ActiveRegionMap = this;
            if (_region == null) _region = Regions[1];
            RegionTitle.Text = _region.Title;

            // GT7に無い下部ナビは廃止(要望 2026-08-25)。戻る=Esc
            BottomBar.Visibility = Visibility.Collapsed;

            try {
                var bmp = LoadRegionImage(System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", _region.Image));
                if (bmp != null) {
                    MapCanvas.Width = MapImage.Width = bmp.PixelWidth;
                    MapCanvas.Height = MapImage.Height = bmp.PixelHeight;
                    MapImage.Source = bmp;
                }
            } catch (Exception ex) { Logging.Warning("[RegionMap] image: " + ex.Message); }

            // 「寄る」演出: 少し大きい状態からズームアウト＋フェード
            Root.Opacity = 0;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(500)) { EasingFunction = new SineEase() });
            var zoom = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            MapZoom.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            MapZoom.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
            Focus();

            try {
                await TracksManager.Instance.EnsureLoadedAsync();
                // [fork] ピンのカードに載せるロゴ（原寸 1800px の PNG）を裏のスレッドで先に解いておく。BuildPins は覚えた物を使うだけになり、
                //   寄る演出の途中で UI が数百 ms 止まらない（2026-10-14）
                var logoPaths = TracksManager.Instance.Enabled.ToList()
                        .Where(t => _region.Contains((t.Country ?? "").Trim().ToLowerInvariant()))
                        .Select(t => { try { return GtLoadingWindow.FindTrackLogo(t.Id); } catch { return null; } })
                        .Where(p => p != null).ToList();
                await System.Threading.Tasks.Task.Run(() => {
                    foreach (var p in logoPaths) WorldMap.LoadBitmap(p, 300);
                });
                if (!IsLoaded || !IsCurrentPage()) return;
                BuildPins();
            } catch (Exception ex) { Logging.Warning("[RegionMap] pins: " + ex.Message); }
        }

        // [fork] 地形図（2400px の jpg）も入るたびに解き直さない。差し替えは更新日時で気づく（2026-10-14）
        private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> RegionImageCache = new Dictionary<string, System.Windows.Media.Imaging.BitmapImage>();

        private static System.Windows.Media.Imaging.BitmapImage LoadRegionImage(string path) {
            if (!System.IO.File.Exists(path)) return null;
            var fi = new System.IO.FileInfo(path);
            var key = path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
            if (RegionImageCache.TryGetValue(key, out var cached)) return cached;
            var bmp = WorldMap.LoadBitmap(path, 0);
            if (bmp != null) RegionImageCache[key] = bmp;
            return bmp;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            GtChrome.Leave();
            GtFocus.Clear(this);   // [fork] パッドのフォーカス候補を落とす（owner が一致する時だけ消える）
            if (ReferenceEquals(GtTestChannel.ActiveRegionMap, this)) GtTestChannel.ActiveRegionMap = null;
        }

        /// <summary>[検証用] 全カードの矩形を書き出す。cm7_test の dump に載る（カード被り検証）。</summary>
        internal string TestDumpCards() {
            var sb = new System.Text.StringBuilder();
            // [fork] パッド検証用: 登録数とパネル状態、各ピンの MainWindow 基準の中心（＝GtFocus の Center）
            sb.AppendLine("-- region focus: pins=" + _pins.Count + " panel=" + (_panelOpen ? 1 : 0)
                    + " chips=" + _panelItems.Count + " --");
            sb.AppendLine("-- region cards (" + (_region?.Key ?? "?") + ") " + _layoutStats + " --");
            foreach (var p in _pins) {
                var c = p.Label;
                if (c == null) continue;
                var pc = PadCenter(p.Element);
                sb.AppendLine(string.Format("card {0}: x={1:0} y={2:0} w={3:0} h={4:0} pin=({5:0},{6:0})",
                        p.Track.Id, Canvas.GetLeft(c), Canvas.GetTop(c), c.DesiredSize.Width, c.DesiredSize.Height, pc.X, pc.Y));
            }
            return sb.ToString();
        }

        #region Coordinates
        private static bool TryKnown(string id, out double lat, out double lon) {
            var lower = id.ToLowerInvariant();
            foreach (var k in KnownCoords.OrderByDescending(x => x.prefix.Length)) {
                if (lower.StartsWith(k.prefix)) { lat = k.lat; lon = k.lon; return true; }
            }
            lat = lon = 0;
            return false;
        }

        private static bool TryResolve(TrackObject track, out double lat, out double lon) {
            if (TryKnown(track.Id, out lat, out lon)) return true;
            var g = track.GeoTags;
            if (g != null && !g.IsEmptyOrInvalid && g.LatitudeValue.HasValue && g.LongitudeValue.HasValue
                    && Math.Abs(g.LatitudeValue.Value) <= 90 && Math.Abs(g.LongitudeValue.Value) <= 180
                    && !(Math.Abs(g.LatitudeValue.Value) < 0.01 && Math.Abs(g.LongitudeValue.Value) < 0.01)) {
                lat = g.LatitudeValue.Value; lon = g.LongitudeValue.Value; return true;
            }
            var country = (track.Country ?? "").Trim().ToLowerInvariant();
            if (CountryCenters.TryGetValue(country, out var c)) { lat = c.lat; lon = c.lon; return true; }
            return false;
        }

        /// <summary>緯度経度 → キャンバス座標（枠の外もそのまま返す）。経度は地図の中心に一番近い周回（±360）を選ぶ
        /// （以前の「Lon0-20 より西なら +360」は、アジアの地図でアブダビ(54°E)を 414°＝右の端の外へ飛ばしていた）。</summary>
        private Point ToCanvasRaw(double lat, double lon) {
            var w = MapCanvas.Width; var h = MapCanvas.Height;
            var mid = (_region.Lon0 + _region.Lon1) / 2;
            while (lon - mid > 180) lon -= 360;
            while (lon - mid < -180) lon += 360;
            var x = (lon - _region.Lon0) / (_region.Lon1 - _region.Lon0) * w;
            var y = (Merc(_region.Lat1) - Merc(lat)) / (Merc(_region.Lat1) - Merc(_region.Lat0)) * h;   // メルカトル（地図の絵と同じ）
            return new Point(x, y);
        }

        /// <summary>地図のうち画面に見えている範囲（キャンバス座標）と、上の見出し・下の案内文字の分のあき。</summary>
        private void VisibleCanvas(out double visW, out double visH, out double mTop, out double mBot) {
            // ★境界はキャンバス実寸から（region_*.jpg は 2400x1344 ＝ 1920x1080 ではない）。
            //   96/94 は 1080 基準のヘッダー/下端マージンなので、キャンバス高さに比例させる
            //   （要望 2026-08-30「magione と vallelunga のカードが被ってる」の正体は決め打ちの境界だった）
            var cvW = MapCanvas.Width; var cvH = MapCanvas.Height;
            if (double.IsNaN(cvW) || cvW <= 0) cvW = 1920;
            if (double.IsNaN(cvH) || cvH <= 0) cvH = 1080;
            mTop = 96.0 * cvH / 1080; mBot = 94.0 * cvH / 1080;
            // ★見えている範囲で抑える。UniformToFill で地図が枠より大きい時、WPF は左上に寄せてはみ出た右/下を切る
            //   （1918x950 の枠で地図の下 155px が画面外＝一番下のカード(ks_vallelunga)が半分切れていた。
            //   それまでは下の暗幕で沈んでいて気づかなかった。要望 2026-09-27）。下は案内文字の札の分(44px)をあける
            visW = cvW; visH = cvH;
            var rw = Root.ActualWidth; var rh = Root.ActualHeight;
            if (rw > 0 && rh > 0) {
                var sc = Math.Max(rw / cvW, rh / cvH);
                visW = Math.Min(cvW, rw / sc); visH = Math.Min(cvH, rh / sc);
                mBot = 44 / sc;
            }
        }

        /// <summary>
        /// ★地図の外のコース（2026-09-27 ヤス・マリーナを入れたら、日本に寄せたアジアの地図ではアブダビが枠外になった。
        /// 要望「端に矢印付きで」）: 見えている範囲の外なら、地図の中心からその方向へ伸ばした線が「少し内側の枠」と交わる所に置き、
        /// 外向きの矢印の向き（度・画面の右 = 0・時計回り）を返す。見えている範囲の中なら、今まで通り縁から 30px の内側に収めるだけ。
        /// </summary>
        private Point PlacePin(Point raw, out bool outside, out double angleDeg) {
            VisibleCanvas(out var visW, out var visH, out var mTop, out var mBot);
            outside = raw.X < 0 || raw.X > visW || raw.Y < 0 || raw.Y > visH;
            angleDeg = 0;
            if (!outside) return new Point(Math.Max(30, Math.Min(visW - 30, raw.X)), Math.Max(30, Math.Min(visH - 30, raw.Y)));
            double left = 70, right = visW - 70, top = mTop + 60, bottom = visH - mBot - 60;
            var c = new Point((left + right) / 2, (top + bottom) / 2);
            var d = raw - c;
            var t = double.MaxValue;
            if (d.X > 0) t = Math.Min(t, (right - c.X) / d.X); else if (d.X < 0) t = Math.Min(t, (left - c.X) / d.X);
            if (d.Y > 0) t = Math.Min(t, (bottom - c.Y) / d.Y); else if (d.Y < 0) t = Math.Min(t, (top - c.Y) / d.Y);
            if (t == double.MaxValue) t = 0;
            angleDeg = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
            return c + d * t;
        }

        private static double Merc(double lat) {
            lat = Math.Max(-85, Math.Min(85, lat));
            return Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360));
        }
        #endregion

        #region Pins
        private void BuildPins() {
            foreach (var p in _pins) {
                MapCanvas.Children.Remove(p.Element);
                if (p.Label != null) MapCanvas.Children.Remove(p.Label);
                if (p.Leader != null) MapCanvas.Children.Remove(p.Leader);
            }
            _pins.Clear();

            var tracks = TracksManager.Instance.Enabled.ToList()
                    .Where(t => _region.Contains((t.Country ?? "").Trim().ToLowerInvariant()))
                    .OrderBy(t => t.DisplayName).ToList();
            RegionSub.Text = tracks.Count + " ロケーション";

            // 同じ場所は少し散らす（1パス目: 位置決め → 2パス目: 生成 → カードの置き場所は LayoutCards でまとめて決める）
            var used = new List<Point>();
            var placedPts = new List<Point>();
            var placedTracks = new List<TrackObject>();
            var outsideAngle = new Dictionary<TrackObject, double>();   // 地図の外のコース → 矢印の向き
            foreach (var t in tracks) {
                if (!TryResolve(t, out var lat, out var lon)) continue;
                var pt = PlacePin(ToCanvasRaw(lat, lon), out var outside, out var angle);
                if (outside) outsideAngle[t] = angle;
                // [fork] 散らしは「ほぼ同じ地点」だけ・ずらしも小さく。以前は 70px 以内を 52px ずつ散らしていて（ピンを押し分けるため）、
                //   密集した日本では鈴鹿や箱根が本当の場所から何十 km も離れて見えた（要望 2026-10-15「鈴鹿・箱根七曲の位置もおかしい」）。
                //   今はコース名カードが常に出ていてクリックも十字キーもカードで選べるので、ピンは本当の場所に置く
                var n = used.Count(u => (u - pt).Length < 12);
                if (n > 0) { var a = n * 1.1; pt = new Point(pt.X + Math.Cos(a) * 16 * Math.Ceiling(n / 5.0), pt.Y + Math.Sin(a) * 16 * Math.Ceiling(n / 5.0)); }
                used.Add(pt);
                placedPts.Add(pt);
                placedTracks.Add(t);
            }
            var delay = 0;
            for (var i = 0; i < placedTracks.Count; i++) {
                var t = placedTracks[i];
                var pt = placedPts[i];
                TryResolve(t, out var lat, out var lon);
                var pin = new Pin { Track = t, Lat = lat, Lon = lon };
                if (outsideAngle.TryGetValue(t, out var oa)) { pin.Outside = true; pin.Angle = oa; }
                pin.Element = CreatePin(pin, pt);
                MapCanvas.Children.Add(pin.Element);
                _pins.Add(pin);

                pin.Element.Opacity = 0;
                pin.Element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(400 + delay) });
                delay += 40;
            }
            LayoutCards();   // 置き場所は裏で計算して、決まったらカードと線をふわっと出す
            Logging.Write("[RegionMap] " + _region.Key + ": " + _pins.Count + "/" + tracks.Count + " pins");
            RegisterFocus();   // [fork] ピンを作り直したらパッドの候補も登録し直す
        }

        private const double PinSize = 44;
        private const double MinLeader = 14;   // これより短い線は引かない（カードがピンに触れている）
        private string _layoutStats = "";

        /// <summary>カード上の、ピンに一番近い点（線の終点）。上下は角の丸みぶん 6px 内側。</summary>
        private static Point AnchorOf(Point pt, Rect c) {
            return new Point(Math.Max(c.Left, Math.Min(c.Right, pt.X)), Math.Max(c.Top + 6, Math.Min(c.Bottom - 6, pt.Y)));
        }

        private static double Area(Rect a, Rect b) {
            var x = Rect.Intersect(a, b);
            return x.IsEmpty ? 0 : x.Width * x.Height;
        }

        private static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        /// <summary>線分 p1-p2 と q1-q2 が交わるか（端がちょうど触れるだけは数えない）。</summary>
        private static bool SegmentsCross(Point p1, Point p2, Point q1, Point q2) {
            var d1 = Cross(q1, q2, p1); var d2 = Cross(q1, q2, p2);
            var d3 = Cross(p1, p2, q1); var d4 = Cross(p1, p2, q2);
            return (d1 > 0 && d2 < 0 || d1 < 0 && d2 > 0) && (d3 > 0 && d4 < 0 || d3 < 0 && d4 > 0);
        }

        /// <summary>線分が長方形の中を通るか。</summary>
        private static bool SegmentHitsRect(Point a, Point b, Rect r) {
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return false;
            if (r.Contains(a) || r.Contains(b)) return true;
            var tl = r.TopLeft; var tr = r.TopRight; var bl = r.BottomLeft; var br = r.BottomRight;
            return SegmentsCross(a, b, tl, tr) || SegmentsCross(a, b, tr, br) || SegmentsCross(a, b, br, bl) || SegmentsCross(a, b, bl, tl);
        }

        private static double DistToSegment(Point p, Point a, Point b) {
            var ab = b - a; var l2 = ab.X * ab.X + ab.Y * ab.Y;
            if (l2 <= 0) return (p - a).Length;
            var t = Math.Max(0, Math.Min(1, ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / l2));
            return (p - (a + ab * t)).Length;
        }

        private int _layoutGen;   // 地図を作り直したら古い計算結果は捨てる

        /// <summary>画面の値を集めて、置き場所の計算は裏のスレッドで（21 枚で約 0.2 秒。寄る演出の途中で画面を止めない）。</summary>
        private async void LayoutCards() {
            var gen = ++_layoutGen;
            var pins = _pins.ToList();
            var n = pins.Count;
            if (n == 0) return;

            VisibleCanvas(out var visW, out var visH, out var mTop, out var mBot);

            var pts = pins.Select(p => p.Pt).ToArray();
            var sizes = pins.Select(p => p.CardSize).ToArray();
            Rect[] cur;
            string stats;
            try {
                var r = await System.Threading.Tasks.Task.Run(() => {
                    var c = SolveLayout(pts, sizes, visW, visH, mTop, mBot, out var st);
                    return Tuple.Create(c, st);
                });
                cur = r.Item1; stats = r.Item2;
            } catch (Exception ex) {
                Logging.Warning("[RegionMap] layout: " + ex.Message);
                return;
            }
            if (gen != _layoutGen) return;   // 計算中に作り直された

            for (var i = 0; i < n; i++) {
                var p = pins[i];
                p.CardRect = cur[i];
                p.Placed = true;
                Canvas.SetLeft(p.Label, cur[i].X);
                Canvas.SetTop(p.Label, cur[i].Y);
                p.UpdateLeader?.Invoke();
                p.Label.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(i * 18) });
                p.Leader.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(i * 18) });
            }
            _layoutStats = stats;
            Logging.Write("[RegionMap] " + _region?.Key + ": " + stats);
        }

        /// <summary>
        /// カードの置き場所をまとめて決める（要望 2026-09-27「交差減らして」）。画面には触らない（裏のスレッドで呼ぶ）。
        /// 以前は 1 枚ずつ「空いている最初の場所」に置いていたので、イタリアの密集地帯で線が長くなり交差していた。
        /// 候補 = ピンの右/左（少しずつ離す）と真上/真下寄り × 上下のずらし（13px 刻み）。
        /// 費用 = 線の長さ＋カード同士・ピンとの重なり（重い）＋線の交差＋線が他のカードの下をくぐる/他のピンをかすめる。
        /// 混んでいるピンから順に置いてから、1 枚ずつ外して一番安い場所へ置き直す（動かなくなるまで・最大 12 周）。
        /// </summary>
        private static Rect[] SolveLayout(Point[] pts, Size[] sizes, double visW, double visH, double mTop, double mBot, out string stats) {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var n = pts.Length;
            var pinRects = pts.Select(p => new Rect(p.X - 18, p.Y - 18, 36, 36)).ToArray();

            // 候補
            var cands = new List<Rect>[n];
            for (var i = 0; i < n; i++) {
                var pt = pts[i]; var s = sizes[i];
                var lefts = new List<double>();
                foreach (var dx in new[] { 0, 30, 70, 120, 180, 250 }) {
                    lefts.Add(pt.X + 26 + dx);
                    lefts.Add(pt.X - 26 - s.Width - dx);
                }
                foreach (var f in new[] { 0.1, 0.5, 0.9 }) lefts.Add(pt.X - s.Width * f);   // 真上/真下寄り
                var seen = new HashSet<long>();
                var list = new List<Rect>();
                foreach (var l0 in lefts) {
                    var l = Math.Max(8, Math.Min(visW - 20 - s.Width, l0));
                    for (var dy = -455; dy <= 455; dy += 13) {
                        var t = Math.Max(mTop, Math.Min(visH - mBot - s.Height, pt.Y - s.Height / 2 + dy));
                        var key = ((long)Math.Round(l) << 20) ^ (long)Math.Round(t);
                        if (seen.Add(key)) list.Add(new Rect(l, t, s.Width, s.Height));
                    }
                }
                cands[i] = list;
            }

            var cur = new Rect[n];
            var placed = new bool[n];

            double Cost(int i, Rect r, bool full) {
                var pt = pts[i];
                var a = AnchorOf(pt, r);
                var len = (a - pt).Length;
                var hasLine = len > MinLeader;
                var cost = len;
                if (r.Right <= pt.X) cost += 12;   // GT7 と同じく右に出す方を少しだけ好む
                var ri = new Rect(r.X - 7, r.Y - 5, r.Width + 14, r.Height + 10);   // 影ぶんの隙間
                var rIn = new Rect(r.X + 3, r.Y + 3, Math.Max(0, r.Width - 6), Math.Max(0, r.Height - 6));
                for (var j = 0; j < n; j++) {
                    cost += Area(r, pinRects[j]) * 40;                               // ピン（自分のも）を隠さない
                    if (j == i) continue;
                    if (hasLine && DistToSegment(pts[j], pt, a) < 24) cost += 300;   // 自分の線が他のピン（光の輪まで）をかすめる
                    if (!placed[j] && !full) continue;
                    var rj = cur[j];
                    cost += Area(ri, new Rect(rj.X - 7, rj.Y - 5, rj.Width + 14, rj.Height + 10)) * 40;   // カード同士が重ならない
                    var aj = AnchorOf(pts[j], rj);
                    var jLine = (aj - pts[j]).Length > MinLeader;
                    var rjIn = new Rect(rj.X + 3, rj.Y + 3, Math.Max(0, rj.Width - 6), Math.Max(0, rj.Height - 6));
                    if (hasLine && jLine && SegmentsCross(pt, a, pts[j], aj)) cost += 600;   // 線どうしの交差
                    if (hasLine && SegmentHitsRect(pt, a, rjIn)) cost += 350;                // 自分の線が他のカードの下をくぐる
                    if (jLine && SegmentHitsRect(pts[j], aj, rIn)) cost += 350;              // 他の線が自分のカードの下をくぐる
                }
                return cost;
            }

            Rect Best(int i, bool full) {
                var best = cands[i][0]; var bestCost = double.MaxValue;
                foreach (var c in cands[i]) {
                    var k = Cost(i, c, full);
                    if (k < bestCost) { bestCost = k; best = c; }
                }
                return best;
            }

            // 混んでいるピンから順に置く → 1 枚ずつ置き直す
            var order = Enumerable.Range(0, n).OrderByDescending(i => pts.Count(q => (q - pts[i]).Length < 220)).ThenBy(i => pts[i].Y).ToList();
            foreach (var i in order) { cur[i] = Best(i, false); placed[i] = true; }
            var passes = 0;
            for (; passes < 12; passes++) {
                var moved = false;
                foreach (var i in order) {
                    var now = Cost(i, cur[i], true);
                    var b = Best(i, true);
                    if (Cost(i, b, true) < now - 0.5) { cur[i] = b; moved = true; }
                }
                if (!moved) break;
            }

            // 集計（dump とログに出す）
            int crossings = 0, underCards = 0, grazing = 0; double total = 0, overlap = 0;
            for (var i = 0; i < n; i++) {
                var a = AnchorOf(pts[i], cur[i]);
                var li = (a - pts[i]).Length;
                total += li;
                for (var j = 0; j < n; j++) {
                    if (j == i) continue;
                    var aj = AnchorOf(pts[j], cur[j]);
                    if (j > i && li > MinLeader && (aj - pts[j]).Length > MinLeader && SegmentsCross(pts[i], a, pts[j], aj)) crossings++;
                    if (j > i) overlap += Area(cur[i], cur[j]);
                    if (li > MinLeader && SegmentHitsRect(pts[i], a, new Rect(cur[j].X + 3, cur[j].Y + 3, Math.Max(0, cur[j].Width - 6), Math.Max(0, cur[j].Height - 6)))) underCards++;
                    if (li > MinLeader && DistToSegment(pts[j], pts[i], a) < 24) grazing++;
                }
            }
            stats = string.Format("layout: crossings={0} lines-under-cards={1} lines-grazing-pins={2} card-overlap={3:0}px2 total-line={4:0} passes={5} {6}ms",
                    crossings, underCards, grazing, overlap, total, passes, sw.ElapsedMilliseconds);
            return cur;
        }

        private FrameworkElement CreatePin(Pin pin, Point pt) {
            var scale = new ScaleTransform(1, 1);
            var g = new Grid { Width = PinSize + 30, Height = PinSize + 30, Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = pin,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
            // [fork] 光の輪は BlurEffect をやめて放射グラデーションで（見た目はほぼ同じ）。ぼかしの効果を 20 個以上ずっと点滅させると
            //   毎フレーム地図ごと描き直しになって画面が重かった（要望 2026-10-14「コース選択画面がちょっと重い」）
            var glow = new Ellipse { Width = PinGlowSize, Height = PinGlowSize, Fill = PinGlow, Opacity = 0.55, IsHitTestVisible = false };
            var dot = new Ellipse { Width = 22, Height = 22, Fill = PinRed, Stroke = Brushes.White, StrokeThickness = 2 };
            var core = new Ellipse { Width = 7, Height = 7, Fill = PinCore };
            g.Children.Add(glow); g.Children.Add(dot); g.Children.Add(core);
            if (pin.Outside) {
                // 地図の外のコース: 赤丸の外側に、本当の場所の方向を指す白い三角（ホバーで一緒に拡大）
                var cxy = g.Width / 2;
                var arrow = new Polygon {
                    Points = new PointCollection { new Point(0, -8), new Point(15, 0), new Point(0, 8) },
                    Fill = Brushes.White, Stroke = PinRed, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                    RenderTransform = new TransformGroup { Children = { new TranslateTransform(cxy + 15, cxy), new RotateTransform(pin.Angle, cxy, cxy) } }
                };
                g.Children.Add(arrow);
            }
            Canvas.SetLeft(g, pt.X - g.Width / 2);
            Canvas.SetTop(g, pt.Y - g.Height / 2);
            Panel.SetZIndex(g, 10);

            // [fork] 鼓動（永久点滅）はやめた。どんな小さいアニメでもこの画面を回し続けると WPF が毎フレーム描き直し、
            //   眺めているだけで CPU 1 コアの 80% 前後を食っていた（止めると 13% 前後。回数を減らしてもぼかしを外しても効かなかった。
            //   2026-10-14 実測・要望「コース選択画面がちょっと重い」）。光は灯したまま、選んでいるピンだけ明るくする（SetHot）
            glow.Opacity = GlowIdle;

            // [fork→GT7 ref_2] ピンから伸びる「ロゴ入り吹き出しカード」（常時表示）。ホバー/フォーカスで白反転
            var cardText = GtNames.TrackJa(pin.Track.Id) ?? System.Text.RegularExpressions.Regex.Replace(pin.Track.DisplayName, @"\s*\(\d+\)\s*$", "");   // [fork] 日本語名（GtNames）
            // MODトラック名のバージョン表記(「0.9.4 PTB 1」等)はGT7では出さない。年号(Monza 1966)はドット無しなので残る
            cardText = System.Text.RegularExpressions.Regex.Replace(cardText, @"\s+v?\d+\.\d+\S*(\s+\S+)*$", "").Trim();
            var darkBg = new SolidColorBrush(Color.FromArgb(0xD9, 0x0E, 0x10, 0x14));
            var liteBg = new SolidColorBrush(Color.FromArgb(0xF2, 0xF2, 0xF5, 0xF8));
            var nameDark = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1E));
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            try {
                var logoPath = GtLoadingWindow.FindTrackLogo(pin.Track.Id);
                if (logoPath != null) {
                    row.Children.Add(new Image {
                        Source = WorldMap.LoadBitmap(logoPath, 300), Height = 34, MaxWidth = 150, Stretch = Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0)
                    });
                    row.Children.Add(new Rectangle { Width = 1, Height = 30, Fill = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 0, 12, 0), Tag = "sep" });
                }
            } catch { }
            var flagCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            try { flagCol.Children.Add(new CountryIcon { Country = pin.Track.Country, Width = 26, Height = 17, HorizontalAlignment = HorizontalAlignment.Center }); } catch { }
            flagCol.Children.Add(new TextBlock { Text = GtNames.CountryJa(pin.Track.Country), FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xC8, 0xC8, 0xC8)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), Tag = "country" });
            row.Children.Add(flagCol);
            var nameText = new TextBlock { Text = cardText, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Tag = "name" };
            row.Children.Add(nameText);
            var card = new Border {
                Background = darkBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(14, 8, 16, 8), Cursor = Cursors.Hand,
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.55 }, Child = row
            };
            // 置き場所は全部のカードが揃ってから LayoutCards がまとめて決める（ここでは大きさを測るだけ）
            card.Measure(new Size(700, 120));
            pin.Pt = pt;
            pin.CardSize = card.DesiredSize;
            card.Opacity = 0;
            Panel.SetZIndex(card, 9);
            MapCanvas.Children.Add(card);
            pin.Label = card;

            // ピン→カードの引き出し線。カードの一番近い点へ、赤丸の縁から引く。暗い下敷き＋白の2本で、明るい陸でも海でも見える。
            // カード(9)とピン(10)より奥(8)＝他のカードの下をくぐる（くぐらない置き方を LayoutCards が選ぶ）
            var leaderLight = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
            var leaderHot = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            var under = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0x06, 0x0E, 0x1C)), StrokeThickness = 4.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var over = new Line { Stroke = leaderLight, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var leader = new Canvas { IsHitTestVisible = false };
            leader.Children.Add(under); leader.Children.Add(over);
            Panel.SetZIndex(leader, 8);
            leader.Opacity = 0;
            MapCanvas.Children.Add(leader);
            pin.Leader = leader;
            var hotNow = false;
            pin.UpdateLeader = () => {
                if (!pin.Placed) { leader.Visibility = Visibility.Collapsed; return; }
                var a = AnchorOf(pt, pin.CardRect);
                var lx = a.X - pt.X; var ly = a.Y - pt.Y; var len = Math.Sqrt(lx * lx + ly * ly);
                leader.Visibility = len > MinLeader ? Visibility.Visible : Visibility.Collapsed;   // カードがピンに触れているなら線は要らない
                if (len <= 0) return;
                var r = hotNow ? 16 : 12;   // 赤丸(22)の半径＋白縁。ホバーでピンが 1.35 倍になる時は 16
                under.X1 = over.X1 = pt.X + lx / len * r; under.Y1 = over.Y1 = pt.Y + ly / len * r;
                under.X2 = over.X2 = a.X; under.Y2 = over.Y2 = a.Y;
            };

            void SetHot(bool hot) {
                hotNow = hot;
                glow.Opacity = hot ? GlowHot : GlowIdle;
                over.Stroke = hot ? leaderHot : leaderLight;
                over.StrokeThickness = hot ? 3 : 2;
                pin.UpdateLeader();
                Panel.SetZIndex(leader, hot ? 29 : 8);   // 選択中は他のカードの上に線を出す（カード 30 の下）
                card.Background = hot ? liteBg : darkBg;
                nameText.Foreground = hot ? (Brush)nameDark : Brushes.White;
                foreach (var tb in flagCol.Children.OfType<TextBlock>()) tb.Foreground = hot ? nameDark : new SolidColorBrush(Color.FromArgb(0xB0, 0xC8, 0xC8, 0xC8));
                foreach (var sp in row.Children.OfType<Rectangle>()) sp.Fill = hot ? new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)) : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
                SetHint(hot ? cardText : null);   // [fork] 既定文はパッド中だけ記号表記になる
            }
            void Hot(object s, MouseEventArgs e) {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.35, TimeSpan.FromMilliseconds(150)));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.35, TimeSpan.FromMilliseconds(150)));
                Panel.SetZIndex(card, 30);   // 密集地帯でも選択中カードを最前面に
                SetHot(true);
            }
            void Cold(object s, MouseEventArgs e) {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)));
                Panel.SetZIndex(card, 9);
                SetHot(false);
            }
            g.MouseEnter += Hot; g.MouseLeave += Cold;
            card.MouseEnter += Hot; card.MouseLeave += Cold;
            g.MouseLeftButtonUp += (s, e) => { e.Handled = true; EnterCourse(pin.Track); };
            card.MouseLeftButtonUp += (s, e) => { e.Handled = true; EnterCourse(pin.Track); };
            // [fork] パッドのフォーカス表現はホバーと完全に同じ（ピンの拡大・カード最前面・白反転・ヒント文言）。
            //   マウス経路の Hot/Cold をそのまま呼ぶだけなので、既存の挙動は一切変わらない。
            pin.SetHot = hot => { if (hot) Hot(null, null); else Cold(null, null); };
            return g;
        }

        /// <summary>[fork] 要素の中心を MainWindow 基準の論理 px で返す（GtFocus の空間探索用）。
        /// まだ配置されていない／別ツリーなら (0,0)（GtFocus 側が候補から外す）。</summary>
        private static Point PadCenter(FrameworkElement el) {
            try {
                if (el == null || !el.IsLoaded) return new Point(0, 0);
                var main = Application.Current?.MainWindow;
                if (main == null || PresentationSource.FromVisual(el) == null) return new Point(0, 0);
                var w = double.IsNaN(el.Width) ? el.ActualWidth : el.Width;
                var h = double.IsNaN(el.Height) ? el.ActualHeight : el.Height;
                return el.TranslatePoint(new Point(w / 2, h / 2), main);
            } catch {
                return new Point(0, 0);
            }
        }

        private static bool IsCurrentPage() {
            var src = (Application.Current?.MainWindow as AcManager.Pages.Windows.MainWindow)?.CurrentSource?.OriginalString;
            return src == null || src.IndexOf("/Pages/Gt/RegionMap.xaml", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>[fork] 要素の枠を MainWindow 基準の論理 px で返す（GtFocus の「一直線上の枠」判定用）。取れなければ空。</summary>
        private static Rect PadBounds(FrameworkElement el) {
            try {
                if (el == null || !el.IsLoaded) return Rect.Empty;
                var main = Application.Current?.MainWindow;
                if (main == null || PresentationSource.FromVisual(el) == null) return Rect.Empty;
                return el.TransformToVisual(main).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            } catch {
                return Rect.Empty;
            }
        }

        /// <summary>[fork] パッドのフォーカス候補を登録する。
        ///   通常 = ピン全部（Spatial・Center は「カードの画面位置」。カードを置き終わる前だけピンの位置）
        ///   下パネルが開いている間 = チップ行（Row・initial = 先頭＝「コースへ ▶」）
        /// 要素を作り直すたび／パネルの開閉のたびに呼ぶ。</summary>
        private void RegisterFocus() {
            try {
                // ピンの生成は await の後ろ。その間に別ページへ移っていたら、そちらの登録を奪わない
                if (!IsLoaded) return;
                // [fork] 画面の切り替え中は古いページもまだ Loaded のまま。ここで登録すると次のページ（ワールドサーキット）の登録を奪い、
                //   直後の Unloaded で Clear されて候補ゼロ＝十字キーが効かなくなった（要望 2026-10-15）。今の画面が地域マップの時だけ登録する
                if (!IsCurrentPage()) return;

                var items = new List<GtFocusItem>();
                if (_panelOpen && _panelItems.Count > 0) {
                    foreach (var p in _panelItems) {
                        var it = p;   // クロージャ用
                        items.Add(new GtFocusItem {
                            Name = it.Name,
                            Center = () => PadCenter(it.Element),
                            SetHot = it.SetHot,
                            Activate = it.Activate
                        });
                    }
                    GtFocus.Register(this, items, GtFocusLayout.Row);
                    return;
                }

                foreach (var p in _pins) {
                    var pin = p;   // クロージャ用
                    items.Add(new GtFocusItem {
                        Name = pin.Track.Id,
                        // [fork] 目が追うのはコース名のカード。ピンは密集地（イタリア等）で団子になり、ピン基準だと
                        //   見た目の並びと違う所へ飛んだ（要望 2026-10-15「上行ったり下行ったり選びにくい」）。置き終わる前はピン
                        Center = () => pin.Placed && pin.Label != null ? PadCenter(pin.Label) : PadCenter(pin.Element),
                        Bounds = () => pin.Placed && pin.Label != null ? PadBounds(pin.Label) : Rect.Empty,
                        SetHot = pin.SetHot,
                        Activate = () => EnterCourse(pin.Track)
                    });
                }
                GtFocus.Register(this, items, GtFocusLayout.Spatial);
            } catch (Exception ex) {
                Logging.Warning("[RegionMap] focus: " + ex.Message);
            }
        }

        /// <summary>ピンの画面位置（Canvas 座標→ページ座標）。</summary>
        private Point PinScreenPoint(Pin pin) {
            try {
                var el = pin.Element;
                return el.TransformToAncestor(Root).Transform(new Point(el.Width / 2, el.Height / 2));
            } catch { return new Point(0, 0); }
        }

        private void ShowCard(Pin pin) {
            HideCard();
            var t = pin.Track;
            var card = new Border {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xF2, 0xF5, 0xF8)), CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 8, 14, 8),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.6 }
            };
            var dp = new DockPanel();
            var outline = new Border { Width = 64, Height = 44, Background = new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x00, 0x00)), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
            var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(4) };
            var src = WorldMap.LoadBitmap(t.OutlineImage, 256) ?? WorldMap.LoadBitmap(t.PreviewImage, 256);
            img.Source = src;
            outline.Child = img;
            DockPanel.SetDock(outline, Dock.Left);
            dp.Children.Add(outline);

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var countryRow = new StackPanel { Orientation = Orientation.Horizontal };
            try { countryRow.Children.Add(new CountryIcon { Country = t.Country, Width = 22, Height = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }); } catch { }
            countryRow.Children.Add(new TextBlock { Text = t.Country ?? "", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x48, 0x55)), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(countryRow);
            stack.Children.Add(new TextBlock { Text = t.DisplayName, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1C)), Margin = new Thickness(0, 2, 0, 0) });
            if (t.MultiLayouts != null && t.MultiLayouts.Count > 1) {
                stack.Children.Add(new TextBlock { Text = t.MultiLayouts.Count + " レイアウト", FontFamily = NumFont, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x68, 0x75)), Margin = new Thickness(0, 2, 0, 0) });
            }
            dp.Children.Add(stack);
            card.Child = dp;

            Overlay.Children.Add(card);
            card.Measure(new Size(500, 200));
            var p = PinScreenPoint(pin);
            var cw = card.DesiredSize.Width; var chh = card.DesiredSize.Height;
            var left = p.X - cw - 26;
            if (left < 12) left = p.X + 26;
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, Math.Max(8, p.Y - chh / 2));
            card.Opacity = 0;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            _card = card;
        }

        private void HideCard() {
            if (_card == null) return;
            Overlay.Children.Remove(_card);
            _card = null;
        }
        #endregion

        #region Selection
        private void Select(TrackObject track) {
            _selected = track;
            SelectedName.Text = GtNames.TrackName(track);
            SelectedMeta.Text = string.Join("  ·  ", new[] { GtNames.CountryJa(track.Country), track.City, FormatLength(track.SpecsLength) }.Where(s => !string.IsNullOrEmpty(s)));
            OutlineImage.Source = WorldMap.LoadBitmap(track.OutlineImage, 400) ?? WorldMap.LoadBitmap(track.PreviewImage, 400);

            LayoutChips.Children.Clear();
            _panelItems.Clear();
            var layouts = track.MultiLayouts != null && track.MultiLayouts.Count > 0 ? track.MultiLayouts.ToList() : new List<TrackObjectBase> { track };
            // [fork] レイアウト/モード/時刻は CourseEntry 側のステップで選ぶ
            LayoutChips.Children.Add(Chip("コースへ  ▶", true, () => EnterCourse(track), _panelItems));
            if (layouts.Count > 1) {
                LayoutChips.Children.Add(new TextBlock { Text = layouts.Count + " レイアウト", FontFamily = NumFont, FontSize = 12, Foreground = Ivory, Opacity = 0.7,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 6) });
            }

            BottomPanel.IsHitTestVisible = true;
            BottomPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            BottomShade.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));

            // [fork] パッド: パネルが開いている間は候補をチップ行に差し替える（initial = 先頭＝「コースへ ▶」）
            _panelOpen = true;
            RegisterFocus();
        }

        /// <summary>[fork] 下パネルを閉じてピンの操作に戻る（Esc / 将来のマウス閉じ経路の共通処理）。</summary>
        private void CloseBottomPanel() {
            _panelOpen = false;
            _panelItems.Clear();
            _selected = null;
            try {
                BottomPanel.IsHitTestVisible = false;
                BottomPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
                BottomShade.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
            } catch { }
            RegisterFocus();   // 候補をピンに戻す
        }

        /// <summary>ui_track.json の length は「10000」(m) だったり「5.8km」だったりする。数字だけなら km に。</summary>
        private static string FormatLength(string s) {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim().Replace(",", "");
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return m >= 1000 ? (m / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km" : m.ToString("0", CultureInfo.InvariantCulture) + " m";
            return s;
        }

        /// <summary>チップ。<paramref name="sink"/> を渡すと、パッドのフォーカス候補としても記録する（[fork]）。</summary>
        private static FrameworkElement Chip(string text, bool primary, Action go, List<PanelItem> sink = null) {
            var b = new Border {
                Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(14, 5, 14, 6), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand,
                Background = primary ? Gold : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderBrush = primary ? Gold : new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
            };
            b.Child = new TextBlock { Text = text, FontSize = 13, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = primary ? new SolidColorBrush(Color.FromRgb(0x0F, 0x12, 0x16)) : Ivory };
            // [fork] ホバーの見た目は SetHot に切り出して、マウスとパッドの両方から同じものを呼ぶ
            void SetHot(bool hot) { b.Opacity = hot ? 0.85 : 1.0; }
            b.MouseEnter += (s, e) => SetHot(true);
            b.MouseLeave += (s, e) => SetHot(false);
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; go(); };
            if (sink != null) sink.Add(new PanelItem { Name = text.Trim(), Element = b, SetHot = SetHot, Activate = go });
            return b;
        }

        private void EnterCourse(TrackObject track) {
            WorldMap.Navigate("/Pages/Gt/CourseEntry.xaml?Track=" + Uri.EscapeDataString(track.Id) + "&Region=" + Uri.EscapeDataString(_region?.Key ?? "eu"));
        }

        private static void Drive(TrackObjectBase layout) {
            try {
                QuickDrive.Show(track: layout);
            } catch (Exception ex) {
                Logging.Warning("[RegionMap] drive: " + ex.Message);
                WorldMap.Navigate("/Pages/Drive/QuickDrive.xaml");
            }
        }
        #endregion

        private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                // [fork] 下パネルが開いている時の Esc は「パネルを閉じる」（GT7 と同じで、1 段ずつ戻る）。
                //   キャンセル音は GtNav の Tunnel クラスハンドラが先に鳴らすので、ここでは鳴らさない。
                if (_panelOpen) {
                    CloseBottomPanel();
                    return;
                }
                WorldMap.Navigate("/Pages/Gt/WorldCircuits.xaml");
            }
        }
    }
}
