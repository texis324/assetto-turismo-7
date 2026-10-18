// [fork] GT7 風「ブランドセントラル」— 全車カタログ。地域 → メーカー → 車を眺めて読む → 「ガレージに入れる」。
//   GT7 実機リファレンス:
//     gt7-ref/garage/ref_5_brand_central_maker_grid.jpg … 左に地域の縦タブ（日本/…）・暗いショールームの上に白いメーカーロゴ 5 列
//     gt7-ref/garage/ref_4_brand_central_car_detail.jpg … 左=大きなロゴ・中=車（ガレージと同じ空間で回る）・右=国旗/メーカー/車名/PP ピル/スペック表/クルマ図鑑・下=同メーカーの車の帯
//   購入・クレジットは出さない（要望 2026-09-05）。価格の行の代わりに「ガレージ」の行（所有状態＋ガレージに入れる/手放す）。
//   所有の正体 = カタログ（全車）と手元（ガレージ）を分けること（GtGarage）。レースで使えるのはガレージの車だけ。
//   ★車の詳細は 3D（2026-09-06 要望「車が回っていない・場所はガレージと同じ空間」）:
//     CmJaOverlay の Viewer.OpenBrandCentral でガレージと同じショールーム（黒スタジオ・自動カメラ）を開き、
//     この画面の CarsRoot（右パネル＋帯＋確認/図鑑）をショールームの上の透明窓へ引っ越させる（MoveCarsRoot）。
//     帯で車を選ぶと Viewer.SwitchCar（MainSlot.SetCarAsync）で 3D の車が入れ替わる。キーは DLL が RenderForm から回してくる。
//     DLL が無い／Data\Static\gt7\brandcentral_2d.flag がある時は 2D（skins/preview の静止画）にフォールバック。
//   「クルマ図鑑」= ui_car.json の説明文（219 台日本語化済み）。「見てみる」（鑑賞モード）は 2D の時だけ。
//   BGM: ブランドセントラルにいる間は Bgm.SetScene("brandcentral")（bgm\brandcentral\ に曲があればそれだけ流れる）。
//   URI: /Pages/Gt/BrandCentral.xaml。Esc: 車の詳細 → メーカー一覧 → ワールドマップ。
//   パッド: 十字=移動 / ×=決定 / L1 R1=地域(一覧) or メーカー(詳細) / □=見てみる(2D)・ACで鑑賞(3D) / △=図鑑。
//   テストチャンネル: goto=brandcentral / brand=<名> / pick=<idx|carId> / bc=confirm|cancel|back|zukan|showcase|watch|region:<key> / screen=<png>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using AcTools.DataFile;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    public partial class BrandCentral {
        /// <summary>テストチャンネル用（表示中のページ）。</summary>
        internal static BrandCentral Active;

        private class Brand {
            public string Name, Region, Badge;
            public string Country;   // 段の国（英語名・CountryIcon と GtNames.CountryJa に渡す）。null = 「その他」の段

            public List<CarObject> Cars = new List<CarObject>();
            public int Owned { get { return Cars.Count(GtGarage.IsOwned); } }
        }

        private class RegionDef {
            public string Key, Name, Mark;
            public Color Color;
        }

        // GT7 の地域タブ（日本 / ヨーロッパ / アメリカ）。それ以外は「その他」。存在する地域だけ出す
        private static readonly RegionDef[] Regions = {
            new RegionDef { Key = "jp", Name = "日本", Color = Color.FromRgb(0xE0, 0x24, 0x2C), Mark = "" },
            new RegionDef { Key = "eu", Name = "ヨーロッパ", Color = Color.FromRgb(0x2F, 0x5E, 0xC6), Mark = "✦" },
            new RegionDef { Key = "am", Name = "アメリカ", Color = Color.FromRgb(0x24, 0x3A, 0x7A), Mark = "★" },
            new RegionDef { Key = "other", Name = "その他", Color = Color.FromRgb(0x60, 0x60, 0x66), Mark = "◆" },
        };

        // ブランド → 地域（ui_car.json の country は空が多いので名前で決める。無い名前は CM が補う Country → 国リストで判定）
        private static readonly Dictionary<string, string> BrandRegion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "Nissan", "jp" }, { "Nismo", "jp" }, { "Toyota", "jp" }, { "Lexus", "jp" }, { "Mazda", "jp" }, { "Honda", "jp" }, { "Acura", "jp" },
            { "Mitsubishi", "jp" }, { "Subaru", "jp" }, { "Suzuki", "jp" }, { "Daihatsu", "jp" }, { "Infiniti", "jp" }, { "Riseart", "jp" }, { "Autobacs", "jp" },
            { "Ford", "am" }, { "Chevrolet", "am" }, { "Corvette", "am" }, { "Shelby", "am" }, { "Dodge", "am" }, { "Chrysler", "am" }, { "Cadillac", "am" },
            { "Pontiac", "am" }, { "Tesla", "am" }, { "Saleen", "am" }, { "Hennessey", "am" }, { "Scuderia Glickenhaus", "am" }, { "SCG", "am" },
            { "Glickenhaus", "am" }, { "Plymouth", "am" }, { "Buick", "am" }, { "GMC", "am" }, { "Jeep", "am" }, { "Lincoln", "am" }, { "Panoz", "am" },
            { "Porsche", "eu" }, { "RUF", "eu" }, { "BMW", "eu" }, { "Audi", "eu" }, { "Mercedes-Benz", "eu" }, { "Mercedes", "eu" }, { "Mercedes-AMG", "eu" },
            { "Volkswagen", "eu" }, { "Opel", "eu" }, { "Ferrari", "eu" }, { "Lamborghini", "eu" }, { "Maserati", "eu" }, { "Alfa Romeo", "eu" }, { "Abarth", "eu" },
            { "Fiat", "eu" }, { "Pagani", "eu" }, { "Lancia", "eu" }, { "Dallara", "eu" }, { "Tatuus", "eu" }, { "Lotus", "eu" }, { "McLaren", "eu" },
            { "Aston Martin", "eu" }, { "Jaguar", "eu" }, { "Bentley", "eu" }, { "Rolls-Royce", "eu" }, { "Caterham", "eu" }, { "Mini", "eu" },
            { "Land Rover", "eu" }, { "Radical", "eu" }, { "Ginetta", "eu" }, { "TVR", "eu" }, { "Race Sim Studio", "eu" }, { "RSS", "eu" },
            { "Renault", "eu" }, { "Alpine", "eu" }, { "Peugeot", "eu" }, { "Citroën", "eu" }, { "Citroen", "eu" }, { "Bugatti", "eu" }, { "DS", "eu" },
            { "Praga", "eu" }, { "Škoda", "eu" }, { "Skoda", "eu" }, { "KTM", "eu" }, { "Koenigsegg", "eu" }, { "Volvo", "eu" }, { "Saab", "eu" },
            { "Polestar", "eu" }, { "Rimac", "eu" }, { "Spyker", "eu" }, { "Donkervoort", "eu" }, { "SEAT", "eu" }, { "Cupra", "eu" }, { "Hispano Suiza", "eu" },
            { "VRC", "eu" }, { "Formula Hybrid", "eu" }, { "Wolf", "eu" }, { "Zenvo", "eu" }, { "Brabham", "eu" },
        };

        private static readonly HashSet<string> JpCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "japan" };
        private static readonly HashSet<string> AmCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "usa", "u.s.a.", "united states", "united states of america", "america", "canada", "mexico",
            "brazil", "argentina", "chile", "uruguay", "peru", "colombia", "venezuela", "bolivia", "ecuador"
        };
        private static readonly HashSet<string> EuCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "italy", "germany", "france", "spain", "great britain", "united kingdom", "uk", "england", "scotland", "wales", "ireland", "belgium",
            "netherlands", "austria", "switzerland", "portugal", "sweden", "norway", "finland", "denmark", "poland", "czech republic", "czechia",
            "hungary", "russia", "romania", "greece", "monaco", "luxembourg", "croatia", "slovakia", "slovenia", "estonia", "latvia", "lithuania",
            "serbia", "turkey", "south africa", "morocco", "egypt", "kenya", "nigeria", "tunisia", "algeria"
        };

        // [fork] 地域の中を国ごとの段に分ける（2026-09-27 要望「国別に分けてほしい。段でね。ページとかじゃなくて」→ 地域の切替は残して中を段に）。
        //   ブランド → 国（英語名）。"" = MOD の製作チーム等（一番下の「その他」段）。無い名前は車の country（ui_car.json）の多数決
        private static readonly Dictionary<string, string> BrandCountry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "Nissan", "Japan" }, { "Nismo", "Japan" }, { "Toyota", "Japan" }, { "Lexus", "Japan" }, { "Mazda", "Japan" }, { "Honda", "Japan" },
            { "Acura", "Japan" }, { "Mitsubishi", "Japan" }, { "Subaru", "Japan" }, { "Suzuki", "Japan" }, { "Daihatsu", "Japan" }, { "Infiniti", "Japan" },
            { "Riseart", "Japan" }, { "Autobacs", "Japan" },
            { "Ford", "USA" }, { "Chevrolet", "USA" }, { "Corvette", "USA" }, { "Shelby", "USA" }, { "Dodge", "USA" }, { "Chrysler", "USA" },
            { "Cadillac", "USA" }, { "Pontiac", "USA" }, { "Tesla", "USA" }, { "Saleen", "USA" }, { "Hennessey", "USA" }, { "Scuderia Glickenhaus", "USA" },
            { "SCG", "USA" }, { "Glickenhaus", "USA" }, { "Plymouth", "USA" }, { "Buick", "USA" }, { "GMC", "USA" }, { "Jeep", "USA" },
            { "Lincoln", "USA" }, { "Panoz", "USA" },
            { "Porsche", "Germany" }, { "RUF", "Germany" }, { "BMW", "Germany" }, { "Audi", "Germany" }, { "Mercedes-Benz", "Germany" },
            { "Mercedes", "Germany" }, { "Mercedes-AMG", "Germany" }, { "Volkswagen", "Germany" }, { "Opel", "Germany" },
            { "Ferrari", "Italy" }, { "Lamborghini", "Italy" }, { "Maserati", "Italy" }, { "Alfa Romeo", "Italy" }, { "Abarth", "Italy" },
            { "Fiat", "Italy" }, { "Pagani", "Italy" }, { "Lancia", "Italy" }, { "Dallara", "Italy" }, { "Tatuus", "Italy" }, { "Wolf", "Italy" },
            { "Lotus", "Great Britain" }, { "McLaren", "Great Britain" }, { "Aston Martin", "Great Britain" }, { "Jaguar", "Great Britain" },
            { "Bentley", "Great Britain" }, { "Rolls-Royce", "Great Britain" }, { "Caterham", "Great Britain" }, { "Mini", "Great Britain" },
            { "Land Rover", "Great Britain" }, { "Radical", "Great Britain" }, { "Ginetta", "Great Britain" }, { "TVR", "Great Britain" },
            { "Brabham", "Great Britain" },
            { "Renault", "France" }, { "Alpine", "France" }, { "Peugeot", "France" }, { "Citroën", "France" }, { "Citroen", "France" },
            { "Bugatti", "France" }, { "DS", "France" },
            { "Praga", "Czech Republic" }, { "Škoda", "Czech Republic" }, { "Skoda", "Czech Republic" }, { "KTM", "Austria" },
            { "Koenigsegg", "Sweden" }, { "Volvo", "Sweden" }, { "Saab", "Sweden" }, { "Polestar", "Sweden" }, { "Rimac", "Croatia" },
            { "Spyker", "Netherlands" }, { "Donkervoort", "Netherlands" }, { "SEAT", "Spain" }, { "Cupra", "Spain" }, { "Hispano Suiza", "Spain" },
            { "Zenvo", "Denmark" },
            { "Race Sim Studio", "" }, { "RSS", "" }, { "VRC", "" }, { "Formula Hybrid", "" }, { "Grand Prix 2025", "" },
        };

        private class CountryRow {
            public string Country;   // null = その他
            public string Name;
            public List<Brand> Brands = new List<Brand>();
            public int Cars;
        }

        private static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));
        private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
        private static readonly Brush GoldBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xB4, 0x74)));
        private static readonly Brush BlueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xF0)));
        private static readonly Brush EdgeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)));
        private static readonly Brush WhitePill = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)));
        private static readonly Brush DarkText = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)));
        private static readonly Brush CardIdle = Freeze(new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush CardSel = Freeze(new SolidColorBrush(Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush TileHot = Freeze(new SolidColorBrush(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
        private static readonly FontFamily NumFont = new FontFamily("Bahnschrift SemiCondensed, Yu Gothic UI");
        private static readonly FontFamily GlyphFont = new FontFamily("Segoe UI Emoji, Segoe UI Symbol");

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        private List<Brand> _brands = new List<Brand>();
        private readonly List<RegionDef> _regionsShown = new List<RegionDef>();
        private readonly List<Action<bool>> _regionHot = new List<Action<bool>>();
        private int _region;
        private int _brandIndex;
        private Brand _brand;
        private CarObject _car;
        private int _carIndex;
        private readonly List<GtFocusItem> _brandFocus = new List<GtFocusItem>();
        private readonly List<GtFocusItem> _railFocus = new List<GtFocusItem>();   // 左の地域の列（十字キー用）
        private bool _inRail;                 // 十字キーの枠が地域の列に居る
        private int _railEnteredRegion;
        private readonly List<GtFocusItem> _stripFocus = new List<GtFocusItem>();
        private readonly List<GtFocusItem> _overlayFocus = new List<GtFocusItem>();
        private readonly List<Border> _stripCards = new List<Border>();
        private string _overlay;        // ダイアログ: null | confirm | done | zukan
        private bool _confirmRemove;
        private bool _gone;

        // 3D（ショールーム）モード
        private bool _showroom;             // ショールームを開いている（開こうとしている）
        private Window _win3d;              // ショールームの上に載せる透明窓（CarsRoot の引っ越し先）
        private Grid _win3dHost;
        private Panel _carsHome;            // CarsRoot の本来の親
        private int _carsHomeIndex;
        private Func<string, bool> _showroomHandler;   // DLL に渡すコールバック（参照を保持）
        private bool _settingsAfterShowroom;
        private bool _watching;             // 「ACで鑑賞」で AC を開いている（ショールームを閉じても一覧へ戻らない）
        private Tuple<Brand, int> _watchReopen;   // AC から戻った時にショールームがまだ閉じ切っていなければ、closed を受けた所で開き直す

        public BrandCentral() {
            InitializeComponent();
            // Actions below own their sound for mouse, keyboard and pad alike.
            // CarsRoot keeps this setting when moved into the 3D overlay window.
            GtSfx.SetHandlesClickSound(BrandsView, true);
            GtSfx.SetHandlesClickSound(CarsRoot, true);
            // [fork] パッド操作中はヒントを記号表記へ（GtHints）
            GtHints.Bind(HintBrandsBack, "Esc  戻る");
            GtHints.Bind(HintBrands, "Q / E  地域    クリックでメーカーを選ぶ", "←  地域の列    L1 / R1  地域    Enter  メーカーを選ぶ");
            GtHints.Bind(HintCarsBack, "Esc  メーカー一覧へ");
            BindCarsHint(false);
            Loaded += OnLoaded;
            Unloaded += (s, e) => {
                _gone = true;
                _settingsAfterShowroom = false;   // 遅れて届いた closed で別ページから設定へ飛ばない。
                if (ReferenceEquals(Active, this)) Active = null;
                if (_showroom) CloseShowroom();
                try { if (_win3d != null) { _win3d.Close(); _win3d = null; } } catch { }
                GtFocus.Clear(this);
                SetBgmScene(null);
                GtChrome.Leave();
            };
            PrimaryBtn.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenConfirm(); };
            SecondaryBtn.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (_showroom) WatchInAc(); else OpenShowcase(); };
            ZukanRow.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenZukan(); };
            SkinPrev.MouseLeftButtonUp += (s, e) => { e.Handled = true; StepSkin(-1); };
            SkinNext.MouseLeftButtonUp += (s, e) => { e.Handled = true; StepSkin(1); };
            ConfirmSkinPrev.MouseLeftButtonUp += (s, e) => { e.Handled = true; StepSkin(-1); };
            ConfirmSkinNext.MouseLeftButtonUp += (s, e) => { e.Handled = true; StepSkin(1); };
            ConfirmYes.MouseLeftButtonUp += (s, e) => { e.Handled = true; ConfirmAction(); };
            ConfirmNo.MouseLeftButtonUp += (s, e) => { e.Handled = true; GtSfx.PlayCancel(); CloseOverlay(); };
            ZukanClose.MouseLeftButtonUp += (s, e) => { e.Handled = true; GtSfx.PlayCancel(); CloseOverlay(); };
            HoverPill(PrimaryBtn, true);
            HoverPill(SecondaryBtn, false);
            HoverPill(ConfirmYes, true);
            HoverPill(ConfirmNo, false);
            HoverPill(ZukanClose, false);
            ZukanRow.MouseEnter += (s, e) => ZukanState.Foreground = BlueBrush;
            ZukanRow.MouseLeave += (s, e) => ZukanState.Foreground = ValueBrush;
            StripSurface.SizeChanged += (s, e) => UpdateStripBlur();
        }

        private void BindCarsHint(bool threeD) {
            if (threeD) {
                GtHints.Bind(HintCars, "← →  クルマを選ぶ    ↑ ↓  カラー    Q / E  メーカー    Enter  購入    U  ACで鑑賞    T  図鑑",
                        "← →  クルマを選ぶ    ↑ ↓  カラー    L1 / R1  メーカー    Enter  購入    □  ACで鑑賞    △  図鑑");
            } else {
                GtHints.Bind(HintCars, "← →  クルマを選ぶ    ↑ ↓  カラー    Q / E  メーカー    Enter  購入    U  見てみる    T  図鑑",
                        "← →  クルマを選ぶ    ↑ ↓  カラー    L1 / R1  メーカー    Enter  購入    □  見てみる    △  図鑑");
            }
        }

        /// <summary>白ピル（filled）は少し明るく、枠線ピルは薄く塗る。PrimaryBtn は所有状態で塗りが変わるので Tag で今の姿を見る。</summary>
        private static void HoverPill(Border b, bool filled) {
            b.MouseEnter += (s, e) => {
                var f = b.Tag is bool ? (bool)b.Tag : filled;
                b.Background = f ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            };
            b.MouseLeave += (s, e) => {
                var f = b.Tag is bool ? (bool)b.Tag : filled;
                b.Background = f ? WhitePill : Brushes.Transparent;
            };
        }

        private async void OnLoaded(object sender, RoutedEventArgs e) {
            _gone = false;
            _inRail = false;
            Active = this;
            Focus();
            GtChrome.Enter();
            LoadBackground();
            SetBgmScene("brandcentral");
            try {
                await CarsManager.Instance.EnsureLoadedAsync();
                if (_gone) return;
                BuildBrands();
                BuildRegionRail();
                if (_brand != null && !_showroom) {
                    // 戻ってきた時（フレームの使い回し）は同じメーカーを開き直す
                    var b = _brands.FirstOrDefault(x => x.Name == _brand.Name);
                    if (b != null) { ShowBrand(b, _carIndex); return; }
                }
                ShowRegion(_region);
            } catch (Exception ex) {
                Logging.Warning("[BrandCentral] load: " + ex);
            }
        }

        private static string Gt7Dir => System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7");

        private void LoadBackground() {
            var bmp = WorldMap.LoadBitmap(System.IO.Path.Combine(Gt7Dir, "brandcentral.jpg"), 1920);
            BgImage.Source = bmp;
            BgShade.Visibility = bmp != null ? Visibility.Visible : Visibility.Collapsed;
        }

        #region CmJaOverlay（BGM シーン・ショールーム）
        private static Type DllType(string name) {
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
        }

        private static MethodInfo ViewerMethod(string name) {
            return DllType("CmJaOverlay.Viewer")?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        }

        /// <summary>ブランドセントラルにいる間だけ施設 BGM（DLL 側 Bgm.SetScene・曲が無ければ何もしない）。</summary>
        private static void SetBgmScene(string scene) {
            try {
                var m = DllType("CmJaOverlay.Bgm")?.GetMethod("SetScene", BindingFlags.Public | BindingFlags.Static);
                if (m != null) m.Invoke(null, new object[] { scene });
            } catch (Exception e) {
                Logging.Warning("[BrandCentral] bgm scene: " + e.Message);
            }
        }

        /// <summary>3D（ショールーム）が使えるか。DLL に OpenBrandCentral があり、2D 固定フラグが無い時。</summary>
        private static bool ShowroomAvailable {
            get {
                try {
                    if (File.Exists(System.IO.Path.Combine(Gt7Dir, "brandcentral_2d.flag"))) return false;
                    return ViewerMethod("OpenBrandCentral") != null && ViewerMethod("SwitchCar") != null;
                } catch {
                    return false;
                }
            }
        }
        #endregion

        #region データ
        private static string RegionOf(Brand b) {
            string r;
            if (BrandRegion.TryGetValue(b.Name, out r)) return r;
            var country = b.Cars.Select(c => c.Country).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            if (country == null) return "other";
            country = country.Trim();
            if (JpCountries.Contains(country)) return "jp";
            if (AmCountries.Contains(country)) return "am";
            if (EuCountries.Contains(country)) return "eu";
            return "other";
        }

        private static string CountryOf(Brand b) {
            string c;
            if (BrandCountry.TryGetValue(b.Name, out c)) return string.IsNullOrEmpty(c) ? null : c;
            var votes = b.Cars.Select(x => NormalizeCountry(x.Country)).Where(x => x != null)
                    .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            return votes?.Key;
        }

        private static string NormalizeCountry(string country) {
            if (string.IsNullOrWhiteSpace(country)) return null;
            country = country.Trim();
            switch (country.ToLowerInvariant()) {
                case "united kingdom": case "uk": case "england": case "scotland": case "wales": return "Great Britain";
                case "u.s.a.": case "united states": case "united states of america": case "america": return "USA";
                case "czechia": return "Czech Republic";
                default: return country;
            }
        }

        /// <summary>地域の中の段: 国ごとにまとめ、車の多い国から。「その他」は一番下。段の中はメーカーの車の多い順（_brands の順）。</summary>
        private List<CountryRow> RowsOf(string regionKey) {
            var rows = new List<CountryRow>();
            foreach (var b in _brands.Where(x => x.Region == regionKey)) {
                var row = rows.FirstOrDefault(r => string.Equals(r.Country, b.Country, StringComparison.OrdinalIgnoreCase));
                if (row == null) {
                    row = new CountryRow { Country = b.Country, Name = b.Country == null ? "その他" : GtNames.CountryJa(b.Country) };
                    rows.Add(row);
                }
                row.Brands.Add(b);
                row.Cars += b.Cars.Count;
            }
            return rows.OrderBy(r => r.Country == null ? 1 : 0).ThenByDescending(r => r.Cars).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>画面に並んでいる順（段の順）のメーカー。Q/E のメーカー送りもこの順。</summary>
        private List<Brand> BrandsInRegion(string regionKey) {
            return RowsOf(regionKey).SelectMany(r => r.Brands).ToList();
        }

        private static int CompareCars(CarObject x, CarObject y) {
            // 年式が古い順 → 名前（GT7 の帯は年式順に近い）
            var yx = x.Year ?? 9999;
            var yy = y.Year ?? 9999;
            if (yx != yy) return yx.CompareTo(yy);
            return string.Compare(x.DisplayName ?? "", y.DisplayName ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private void BuildBrands() {
            var cars = CarsManager.Instance.Loaded.Where(x => x.Enabled).ToList();
            var map = new Dictionary<string, Brand>(StringComparer.OrdinalIgnoreCase);
            foreach (var car in cars) {
                var name = string.IsNullOrWhiteSpace(car.Brand) ? "その他" : car.Brand.Trim();
                Brand b;
                if (!map.TryGetValue(name, out b)) {
                    b = new Brand { Name = name };
                    map[name] = b;
                }
                b.Cars.Add(car);
            }
            foreach (var b in map.Values) {
                b.Cars.Sort(CompareCars);
                b.Badge = b.Cars.OrderBy(c => c.Id.StartsWith("ks_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .Select(c => c.BrandBadge).FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
                b.Region = RegionOf(b);
                b.Country = CountryOf(b);
            }
            _brands = map.Values.OrderByDescending(b => b.Cars.Count).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _regionsShown.Clear();
            foreach (var r in Regions) {
                if (_brands.Any(b => b.Region == r.Key)) _regionsShown.Add(r);
            }
            if (_regionsShown.Count == 0) _regionsShown.Add(Regions[Regions.Length - 1]);
            if (_region >= _regionsShown.Count) _region = 0;
            Logging.Write("[BrandCentral] brands=" + _brands.Count + " cars=" + cars.Count + " regions=" + string.Join(",", _regionsShown.Select(r => r.Key))
                    + " 3d=" + (ShowroomAvailable ? "yes" : "no"));
        }

        private int TotalCars() {
            return _brands.Sum(b => b.Cars.Count);
        }
        #endregion

        #region パッド（GtFocus）
        private static Point PadCenter(FrameworkElement e) {
            try {
                if (e == null || !e.IsLoaded || e.ActualWidth <= 0 || e.ActualHeight <= 0) return new Point(0, 0);
                var main = Application.Current?.MainWindow;
                if (main == null) return new Point(0, 0);
                if (PresentationSource.FromVisual(e) == null) return new Point(0, 0);
                return e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), main);
            } catch {
                return new Point(0, 0);
            }
        }

        private bool PageCommand(GtPadCommand cmd) {
            if (_overlay != null) {
                if (cmd == GtPadCommand.Back) { GtSfx.PlayCancel(); CloseOverlay(); return true; }
                return false;
            }
            if (InCars) {
                if (cmd == GtPadCommand.L1) { StepBrand(-1); return true; }
                if (cmd == GtPadCommand.R1) { StepBrand(1); return true; }
                if (cmd == GtPadCommand.Square) { if (_showroom) WatchInAc(); else OpenShowcase(); return true; }
                if (cmd == GtPadCommand.Triangle) { OpenZukan(); return true; }
                return false;
            }
            if (cmd == GtPadCommand.L1 || cmd == GtPadCommand.R1) {
                ShowRegion(_region + (cmd == GtPadCommand.L1 ? -1 : 1));
                if (_inRail) GtFocus.SetIndex(_region);   // 地域の列に居る時は枠も付いていく
                GtSfx.PlayMove();
                return true;
            }
            return false;
        }

        /// <summary>車の詳細（帯）を出しているか（2D ならページ内の CarsView、3D ならショールーム上の窓）。</summary>
        private bool InCars {
            get { return _showroom || CarsView.Visibility == Visibility.Visible; }
        }

        private void RegisterBrands(int index) {
            if (_gone) return;
            // 左右=並び順（段の端から次の段の頭へ続く）。上下は各タイルの Custom(MoveLine) が 1 段ずつ動かす
            //   （画面上の距離だけで選ぶと、1 社だけの段(オーストリア・チェコ)を飛ばしてしまう・2026-09-27 実測）
            GtFocus.Register(this, _brandFocus, GtFocusLayout.Row, 0, index);
            GtFocus.SetPageCommand(this, PageCommand);
        }

        private void RegisterStrip(int index) {
            if (_gone) return;
            GtFocus.Register(this, _stripFocus, GtFocusLayout.Row, 0, index);
            GtFocus.SetPageCommand(this, PageCommand);
        }

        /// <summary>購入確認の中の ↑↓ = ボディカラー。</summary>
        private bool OverlaySkinCommand(GtPadCommand cmd) {
            if (!BuyingColor) return false;
            if (cmd == GtPadCommand.Up) { StepSkin(-1); return true; }
            if (cmd == GtPadCommand.Down) { StepSkin(1); return true; }
            return false;
        }

        private void RegisterOverlay(Border no, Border yes, int initial) {
            _overlayFocus.Clear();
            if (no != null) {
                _overlayFocus.Add(new GtFocusItem {
                    HandlesActivationSound = true,
                    Name = "no", Center = () => PadCenter(no),
                    SetHot = on => no.BorderBrush = on ? Brushes.White : EdgeBrush,
                    Custom = OverlaySkinCommand,
                    Activate = () => { GtSfx.PlayCancel(); CloseOverlay(); }
                });
            }
            if (yes != null) {
                _overlayFocus.Add(new GtFocusItem {
                    HandlesActivationSound = true,
                    Name = "yes", Center = () => PadCenter(yes),
                    SetHot = on => yes.BorderBrush = on ? BlueBrush : Brushes.Transparent,
                    Custom = OverlaySkinCommand,
                    Activate = ConfirmAction
                });
            }
            if (_gone) return;
            GtFocus.Register(this, _overlayFocus, GtFocusLayout.Row, 0, Math.Min(initial, Math.Max(0, _overlayFocus.Count - 1)));
            GtFocus.SetPageCommand(this, PageCommand);
        }

        private void EnsureVisibleV(FrameworkElement el) {
            try {
                if (el == null || !el.IsLoaded) return;
                var sv = BrandScroll;
                if (sv.ViewportHeight <= 0) return;
                var top = el.TranslatePoint(new Point(0, 0), sv).Y;
                var bottom = top + el.ActualHeight;
                if (top < 0) sv.ScrollToVerticalOffset(sv.VerticalOffset + top - 12);
                else if (bottom > sv.ViewportHeight) sv.ScrollToVerticalOffset(sv.VerticalOffset + (bottom - sv.ViewportHeight) + 12);
            } catch { }
        }

        private void EnsureVisibleH(FrameworkElement el) {
            // 帯を作った直後はまだ配置されていないので、レイアウト後にもう一度
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                try {
                    if (el == null || !el.IsLoaded) return;
                    var sv = StripScroll;
                    if (sv.ViewportWidth <= 0) return;
                    var left = el.TranslatePoint(new Point(0, 0), sv).X;
                    var right = left + el.ActualWidth;
                    if (left < 0) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + left - 24);
                    else if (right > sv.ViewportWidth) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + (right - sv.ViewportWidth) + 24);
                } catch { }
            }));
        }
        #endregion

        #region ① 地域とメーカー
        private void BuildRegionRail() {
            RegionRail.Children.Clear();
            _regionHot.Clear();
            _railFocus.Clear();
            for (var i = 0; i < _regionsShown.Count; i++) {
                var idx = i;
                var r = _regionsShown[i];
                var row = new StackPanel {
                    Margin = new Thickness(0, 0, 0, 30), Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left
                };
                var icon = RegionIcon(r);
                // 十字キーで地域の列に居る時の枠（アイコンを囲む白い輪）
                var ring = new Ellipse { Stroke = Brushes.White, StrokeThickness = 2, Margin = new Thickness(-6), Opacity = 0, IsHitTestVisible = false };
                var iconHost = new Grid { Width = 42, Height = 42, HorizontalAlignment = HorizontalAlignment.Left };
                iconHost.Children.Add(icon);
                iconHost.Children.Add(ring);
                var name = new TextBlock { Text = r.Name, FontSize = 14, Foreground = ValueBrush, Margin = new Thickness(1, 8, 0, 0) };
                row.Children.Add(iconHost);
                row.Children.Add(name);
                _railFocus.Add(new GtFocusItem {
                    HandlesActivationSound = true,
                    Name = "region:" + r.Key,
                    Center = () => PadCenter(iconHost),
                    SetHot = on => {
                        ring.Opacity = on ? 1 : 0;
                        // ↑↓ で動かしたらすぐその地域を表示（GT7 と同じ）
                        if (on && _inRail && idx != _region) ShowRegion(idx);
                    },
                    Custom = RailCommand,
                    Activate = LeaveRail
                });
                Action<bool> hot = on => {
                    var o = on || idx == _region ? 1.0 : 0.5;
                    name.Opacity = o;
                    icon.Opacity = o;
                    name.FontWeight = idx == _region ? FontWeights.SemiBold : FontWeights.Normal;
                };
                _regionHot.Add(hot);
                row.MouseEnter += (s, e) => hot(true);
                row.MouseLeave += (s, e) => hot(false);
                row.MouseLeftButtonUp += (s, e) => {
                    e.Handled = true;
                    if (idx == _region) return;
                    GtSfx.PlaySelect();
                    ShowRegion(idx);
                };
                RegionRail.Children.Add(row);
            }
        }

        /// <summary>GT7 の地域アイコン: 白い丸に地域の印（日本=赤丸・ヨーロッパ=青に星・アメリカ=紺に星）。</summary>
        private static FrameworkElement RegionIcon(RegionDef r) {
            var g = new Grid { Width = 42, Height = 42, HorizontalAlignment = HorizontalAlignment.Left };
            g.Children.Add(new Ellipse { Fill = Brushes.White, Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.45 } });
            var inner = r.Key == "jp" ? 18.0 : 30.0;
            g.Children.Add(new Ellipse { Width = inner, Height = inner, Fill = new SolidColorBrush(r.Color) });
            if (!string.IsNullOrEmpty(r.Mark)) {
                g.Children.Add(new TextBlock {
                    Text = r.Mark, FontFamily = GlyphFont, FontSize = 14, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1)
                });
            }
            return g;
        }

        private void ShowRegion(int i) {
            var n = _regionsShown.Count;
            if (n == 0) return;
            _region = ((i % n) + n) % n;
            for (var k = 0; k < _regionHot.Count; k++) _regionHot[k](false);
            var key = _regionsShown[_region].Key;
            BrandGrid.Children.Clear();
            _brandFocus.Clear();
            var rows = RowsOf(key);
            for (var r = 0; r < rows.Count; r++) BrandGrid.Children.Add(CountryRowView(rows[r], r == 0));
            BrandScroll.ScrollToTop();
            OwnedCountText.Text = GtGarage.Count.ToString(CultureInfo.InvariantCulture) + " / " + TotalCars().ToString(CultureInfo.InvariantCulture);
            if (_brandIndex >= _brandFocus.Count) _brandIndex = 0;
            if (!_showroom && !_inRail) RegisterBrands(_brandIndex);
        }

        // ---- 十字キーで地域の列へ（2026-09-27 要望「ハンコンの D パッドで日本かヨーロッパかアメリカを選べない」）----
        //   段の左端のメーカーで ← → 地域の列。↑↓ で地域が切り替わる。→ か決定でメーカーへ戻る。
        //   L1/R1（ハンコンは左右パドル）はどちらに居ても地域を切り替える。

        /// <summary>段（見た目の行）の左端か。</summary>
        private bool IsLineStart(int index) {
            var cur = PadCenterOf(index);
            for (var k = 0; k < _brandFocus.Count; k++) {
                if (k == index) continue;
                var p = PadCenterOf(k);
                if (Math.Abs(p.Y - cur.Y) <= 24 && p.X < cur.X - 1) return false;
            }
            return true;
        }

        private bool EnterRail() {
            if (_gone || _showroom || _railFocus.Count == 0) return false;
            _inRail = true;
            _railEnteredRegion = _region;
            GtFocus.Register(this, _railFocus, GtFocusLayout.Column, 0, _region);
            GtFocus.SetPageCommand(this, PageCommand);
            GtSfx.PlayMove();
            return true;
        }

        private bool RailCommand(GtPadCommand cmd) {
            if (cmd == GtPadCommand.Right) { LeaveRail(); return true; }
            return cmd == GtPadCommand.Left;   // 左は行き止まり（消費）
        }

        /// <summary>地域の列からメーカーへ戻る。同じ地域なら出てきたメーカー、地域を変えたら先頭へ。</summary>
        private void LeaveRail() {
            if (!_inRail) return;
            _inRail = false;
            GtSfx.PlayMove();
            var idx = _region == _railEnteredRegion ? _brandIndex : 0;
            if (_brandFocus.Count > 0) RegisterBrands(Math.Max(0, Math.Min(idx, _brandFocus.Count - 1)));
        }

        /// <summary>国の段: 左に国旗・国名・メーカー数/台数、右にその国のメーカーのタイル（入り切らなければ段の中で折り返す）。
        /// 段どうしは細い線で区切る。</summary>
        private FrameworkElement CountryRowView(CountryRow row, bool first) {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 38, 10, 0) };
            if (row.Country != null) {
                label.Children.Add(new AcManager.Controls.CountryIcon {
                    Country = row.Country, Width = 33, Height = 22, HorizontalAlignment = HorizontalAlignment.Left,
                    Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 }
                });
            } else {
                label.Children.Add(new Border {
                    Width = 33, Height = 22, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                    BorderBrush = EdgeBrush, BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = "◆", FontFamily = GlyphFont, FontSize = 11, Foreground = LabelBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                });
            }
            label.Children.Add(new TextBlock { Text = row.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = ValueBrush, Margin = new Thickness(0, 9, 0, 0), TextWrapping = TextWrapping.Wrap });
            label.Children.Add(new TextBlock {
                Text = row.Brands.Count.ToString(CultureInfo.InvariantCulture) + " メーカー · " + row.Cars.ToString(CultureInfo.InvariantCulture) + " 台",
                FontFamily = NumFont, FontSize = 11.5, Foreground = LabelBrush, Margin = new Thickness(0, 3, 0, 0)
            });
            g.Children.Add(label);
            var tiles = new WrapPanel();
            foreach (var b in row.Brands) tiles.Children.Add(BrandTile(b));
            Grid.SetColumn(tiles, 1);
            g.Children.Add(tiles);
            return new Border {
                Child = g, Padding = new Thickness(0, 8, 0, 4),
                BorderBrush = first ? Brushes.Transparent : SeparatorBrush, BorderThickness = new Thickness(0, 1, 0, 0)
            };
        }

        /// <summary>メーカーのタイル（ロゴ・名前・所有印）。238px（段に入り切らなければ段の中で折り返す。
        /// 7 社を 1 段に詰めるため縮めた版は本人「1 段に納めなくていいので、もとのロゴの大きさで」で戻した・2026-09-27）。</summary>
        private FrameworkElement BrandTile(Brand b) {
            var scale = new ScaleTransform(1, 1);
            var root = new StackPanel {
                Width = 222, Margin = new Thickness(8, 6, 8, 16), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale
            };
            var logoBox = new Border {
                Width = 206, Height = 126, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
                BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center
            };
            var bmp = WorldMap.LoadBitmap(b.Badge, 256);
            if (bmp != null) {
                var logo = new Image {
                    Source = bmp, Width = 104, Height = 104, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
                logoBox.Child = logo;
            } else {
                logoBox.Child = new TextBlock {
                    Text = b.Name.ToUpperInvariant(), FontFamily = NumFont, FontSize = 22, Foreground = ValueBrush, TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0)
                };
            }
            root.Children.Add(logoBox);
            root.Children.Add(new TextBlock {
                Text = b.Name, FontSize = 13, Foreground = ValueBrush, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            });
            var owned = b.Owned;
            root.Children.Add(new TextBlock {
                Text = owned > 0 ? "✓ " + owned + " / " + b.Cars.Count : b.Cars.Count + " 台",
                FontFamily = NumFont, FontSize = 11, Foreground = owned > 0 ? GoldBrush : LabelBrush,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0)
            });

            void SetHot(bool on) {
                logoBox.BorderBrush = on ? Brushes.White : Brushes.Transparent;
                logoBox.Background = on ? TileHot : Brushes.Transparent;
                Animate(scale, ScaleTransform.ScaleXProperty, on ? 1.06 : 1.0, 140);
                Animate(scale, ScaleTransform.ScaleYProperty, on ? 1.06 : 1.0, 140);
            }

            void Activate() {
                GtSfx.PlaySelect();
                ShowBrand(b, 0);
            }

            root.MouseEnter += (s, e) => SetHot(true);
            root.MouseLeave += (s, e) => SetHot(false);
            root.MouseLeftButtonUp += (s, e) => { e.Handled = true; Activate(); };

            var index = _brandFocus.Count;
            _brandFocus.Add(new GtFocusItem {
                HandlesActivationSound = true,
                Name = b.Name,
                Center = () => PadCenter(logoBox),
                SetHot = on => {
                    SetHot(on);
                    if (on) { _brandIndex = index; EnsureVisibleV(root); }
                },
                Custom = cmd => MoveLine(index, cmd) || (cmd == GtPadCommand.Left && IsLineStart(index) && EnterRail()),
                Activate = Activate
            });
            return root;
        }

        /// <summary>上下: 見た目の 1 行下(上)へ、横位置のいちばん近いメーカーに移る。段の中で折り返した 2 行目も 1 行として扱う。</summary>
        private bool MoveLine(int index, GtPadCommand cmd) {
            if (cmd != GtPadCommand.Up && cmd != GtPadCommand.Down) return false;
            if (index < 0 || index >= _brandFocus.Count) return false;
            const double sameLine = 24;
            var down = cmd == GtPadCommand.Down;
            var cur = PadCenterOf(index);
            double? lineY = null;
            for (var k = 0; k < _brandFocus.Count; k++) {
                var y = PadCenterOf(k).Y;
                var dy = y - cur.Y;
                if (down ? dy <= sameLine : dy >= -sameLine) continue;
                if (lineY == null || (down ? y < lineY.Value : y > lineY.Value)) lineY = y;
            }
            if (lineY == null) return true;   // 一番上/下の段: 動かない（入力は消費）
            var best = -1;
            var bestD = double.MaxValue;
            for (var k = 0; k < _brandFocus.Count; k++) {
                var p = PadCenterOf(k);
                if (Math.Abs(p.Y - lineY.Value) > sameLine) continue;
                var d = Math.Abs(p.X - cur.X);
                if (d < bestD) { bestD = d; best = k; }
            }
            if (best < 0) return true;
            GtFocus.SetIndex(best);
            GtSfx.PlayMove();
            return true;
        }

        private Point PadCenterOf(int index) {
            try { return _brandFocus[index].Center(); } catch { return new Point(0, 0); }
        }

        private static void Animate(DependencyObject target, DependencyProperty prop, double to, int ms) {
            ((IAnimatable)target).BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        }
        #endregion

        #region ② 車の詳細（2D/3D 共通のパネルと帯）
        private void ShowBrand(Brand b, int carIndex) {
            if (b == null || b.Cars.Count == 0) return;
            _inRail = false;
            var n = b.Cars.Count;
            carIndex = ((carIndex % n) + n) % n;
            if (!_showroom && ShowroomAvailable) {
                // 3D: ショールームを開く。パネル(CarsRoot)は "ready" が来たらショールームの上に載せる（ShowPanel3D）
                _brand = b;
                _carIndex = carIndex;
                _car = b.Cars[carIndex];
                if (OpenShowroom(_car)) return;
                // 開けなければ 2D にフォールバック
            }
            _brand = b;
            BigBrandLogo.Source = WorldMap.LoadBitmap(b.Badge, 256);
            BuildStrip();
            if (!_showroom) {
                BrandsView.Visibility = Visibility.Collapsed;
                CarsView.Visibility = Visibility.Visible;
                CarsView.Opacity = 0;
                CarsView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new SineEase() });
            }
            _carIndex = carIndex;
            RegisterStrip(_carIndex);
            SelectCar(_carIndex);
        }

        private void BuildStrip() {
            CarStrip.Children.Clear();
            _stripFocus.Clear();
            _stripCards.Clear();
            for (var i = 0; i < _brand.Cars.Count; i++) CarStrip.Children.Add(StripCard(_brand.Cars[i], i));
        }

        /// <summary>帯のカード（GT7 実測: 車のレンダー＋名前。選択中は明るい面＋白枠。所有車には金の印）。</summary>
        private Border StripCard(CarObject car, int idx) {
            var root = new Grid { Width = 250 };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(128) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var img = new Image { Source = GtCarThumbnail.Load(car.SelectedSkin?.PreviewImage, 320), Stretch = Stretch.Uniform, Margin = new Thickness(10, 8, 10, 2) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            root.Children.Add(img);
            var name = new TextBlock {
                Text = car.DisplayName, FontSize = 12, Foreground = ValueBrush, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 2, 8, 8)
            };
            Grid.SetRow(name, 1);
            root.Children.Add(name);
            if (GtGarage.IsOwned(car)) {
                root.Children.Add(new Border {
                    Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 8, 0),
                    Child = new TextBlock { Text = "✓ ガレージ", FontSize = 10, Foreground = GoldBrush }
                });
            }
            var bd = new Border {
                Child = root, Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5),
                BorderBrush = Brushes.Transparent, Background = CardIdle, Cursor = Cursors.Hand
            };
            bd.MouseEnter += (s, e) => { if (idx != _carIndex) bd.BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)); };
            bd.MouseLeave += (s, e) => { if (idx != _carIndex) bd.BorderBrush = Brushes.Transparent; };
            bd.MouseLeftButtonUp += (s, e) => {
                e.Handled = true;
                if (idx == _carIndex) { OpenConfirm(); return; }
                GtSfx.PlayMove();
                SelectCar(idx);
                GtFocus.SetIndex(idx);
            };
            _stripCards.Add(bd);
            _stripFocus.Add(new GtFocusItem {
                HandlesActivationSound = true,
                Name = car.Id,
                Center = () => PadCenter(bd),
                SetHot = on => { if (on && idx != _carIndex) SelectCar(idx); },
                // ↑↓ = ボディカラー（帯は左右しか使わないので空いている・2026-09-27）
                Custom = cmd => {
                    if (cmd == GtPadCommand.Up) { StepSkin(-1); return true; }
                    if (cmd == GtPadCommand.Down) { StepSkin(1); return true; }
                    return false;
                },
                Activate = OpenConfirm
            });
            return bd;
        }

        // ---- ボディカラー（2026-09-27 要望「車選択画面で車のカラーを変えられるようにしてほしい」）----
        //   ↑↓ / 右パネルの ‹ › で選ぶ。見ている色は車ごとに覚えておき（帯で行き来しても戻る）、
        //   「ガレージに入れる」とその色が CM の選択スキン(SelectedSkin)になる＝ガレージとレースもその色。
        private readonly Dictionary<string, CarSkinObject> _pickedSkin = new Dictionary<string, CarSkinObject>(StringComparer.OrdinalIgnoreCase);

        private List<CarSkinObject> SkinsOf(CarObject car) {
            try { return car?.EnabledOnlySkins?.ToList() ?? new List<CarSkinObject>(); } catch { return new List<CarSkinObject>(); }
        }

        /// <summary>この車で今見せる色（選び直していなければ CM の選択スキン）。</summary>
        private CarSkinObject PickedSkin(CarObject car) {
            if (car == null) return null;
            CarSkinObject s;
            if (_pickedSkin.TryGetValue(car.Id, out s) && s != null) return s;
            return car.SelectedSkin ?? SkinsOf(car).FirstOrDefault();
        }

        private void StepSkin(int dir) {
            var car = _car;
            // 購入確認の中でも色を選べる（2026-10-17 本人「色指定してしか車買えないように」）
            if (car == null || _overlay != null && !BuyingColor) return;
            var skins = SkinsOf(car);
            if (skins.Count <= 1) return;
            // 3D で車を差し替えている最中は受けない（読み終わると選んだ色が上書きされるため）
            if (_showroom && (_switchTimer != null && _switchTimer.IsEnabled || SwitchShade.Visibility == Visibility.Visible)) return;
            var cur = skins.IndexOf(PickedSkin(car));
            var next = skins[((Math.Max(cur, 0) + dir) % skins.Count + skins.Count) % skins.Count];
            _pickedSkin[car.Id] = next;
            GtSfx.PlayMove();
            ShowPickedSkin();
            if (_showroom) {
                try { ViewerMethod("SetBrandSkin")?.Invoke(null, new object[] { next.Id }); }
                catch (Exception e) { Logging.Warning("[BrandCentral] skin: " + e.Message); }
            }
        }

        /// <summary>右パネルの「ボディカラー」行と、2D の時は大きい写真も今の色に。</summary>
        private void ShowPickedSkin() {
            var car = _car;
            if (car == null) return;
            var skins = SkinsOf(car);
            var skin = PickedSkin(car);
            SkinNameText.Text = SkinName(skin);
            var i = skins.IndexOf(skin);
            SkinCountText.Text = skins.Count > 0 ? (Math.Max(i, 0) + 1).ToString(CultureInfo.InvariantCulture) + " / " + skins.Count.ToString(CultureInfo.InvariantCulture) : "";
            var many = skins.Count > 1;
            SkinPrev.Opacity = many ? 1 : 0.25;
            SkinNext.Opacity = many ? 1 : 0.25;
            if (!_showroom || CarImg.Visibility == Visibility.Visible) CarImg.Source = WorldMap.LoadBitmap(skin?.PreviewImage, 1400);
            if (BuyingColor) {
                ConfirmImg.Source = WorldMap.LoadBitmap(skin?.PreviewImage, 1400);
                ConfirmSkinName.Text = SkinName(skin);
                ConfirmSkinCount.Text = SkinCountText.Text;
                ConfirmSkinPrev.Opacity = many ? 1 : 0.25;
                ConfirmSkinNext.Opacity = many ? 1 : 0.25;
            }
        }

        /// <summary>購入の確認を開いていて、色を選べる状態。</summary>
        private bool BuyingColor => _overlay == "confirm" && !_confirmRemove;

        // ---- 価格（2026-10-17 本人「車の値段表示しておいてほしい」）----
        //   新車時の値段（円）。Data\Static\gt7\car_prices.json = { "<carId>": { "yen": 4990000, ... } }。無い車は「—」。
        private static Dictionary<string, long> _prices;

        private static long? PriceOf(CarObject car) {
            if (car == null) return null;
            if (_prices == null) {
                _prices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                try {
                    var file = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Static", "gt7", "car_prices.json");
                    if (File.Exists(file)) {
                        var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                        foreach (var p in root.Properties()) {
                            var v = p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Object ? p.Value["yen"] : p.Value;
                            if (v != null && (v.Type == Newtonsoft.Json.Linq.JTokenType.Integer || v.Type == Newtonsoft.Json.Linq.JTokenType.Float)) {
                                _prices[p.Name] = (long)v;
                            }
                        }
                    }
                    Logging.Write("[BrandCentral] prices: " + _prices.Count);
                } catch (Exception e) { Logging.Warning("[BrandCentral] prices: " + e.Message); }
            }
            long y;
            return _prices.TryGetValue(car.Id, out y) ? y : (long?)null;
        }

        private static string PriceLabel(CarObject car) {
            var y = PriceOf(car);
            return y.HasValue ? "¥" + y.Value.ToString("#,0", CultureInfo.InvariantCulture) : "—";
        }

        private static string SkinName(CarSkinObject skin) {
            if (skin == null) return "—";
            var n = skin.DisplayName;
            return string.IsNullOrWhiteSpace(n) ? skin.Id : n;
        }

        private void PaintCards() {
            for (var i = 0; i < _stripCards.Count; i++) {
                var sel = i == _carIndex;
                _stripCards[i].BorderBrush = sel ? Brushes.White : Brushes.Transparent;
                _stripCards[i].Background = sel ? CardSel : CardIdle;
            }
        }

        /// <summary>帯の車を選ぶ。sync3d=true なら 3D の車も差し替える（ショールームを開いた直後は既にその車なので false）。</summary>
        private void SelectCar(int idx, bool sync3d = true) {
            if (_brand == null || _brand.Cars.Count == 0) return;
            var n = _brand.Cars.Count;
            idx = ((idx % n) + n) % n;
            var changed = !ReferenceEquals(_car, _brand.Cars[idx]);
            _carIndex = idx;
            _car = _brand.Cars[idx];
            PaintCards();
            if (idx < _stripCards.Count) EnsureVisibleH(_stripCards[idx]);
            try { BuildDetail(); } catch (Exception e) { Logging.Warning("[BrandCentral] detail: " + e.Message); }
            if (_showroom && sync3d && changed) SwitchCar3D(_car);
        }

        private void StepBrand(int dir) {
            if (_brand == null) return;
            var list = BrandsInRegion(_brand.Region);
            var i = list.IndexOf(_brand);
            if (list.Count == 0 || i < 0) return;
            var next = list[((i + dir) % list.Count + list.Count) % list.Count];
            if (ReferenceEquals(next, _brand)) return;
            GtSfx.PlayMove();
            ShowBrand(next, 0);
        }

        /// <summary>車の詳細からメーカー一覧へ。3D 中はショールームを閉じる（"closing"→"closed" で ExitShowroom → ここへ戻る）。</summary>
        private void BackToBrands() {
            if (_showroom) { CloseShowroom(); return; }
            var from = _brand;
            _brand = null;
            _car = null;
            _overlay = null;
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            ZukanOverlay.Visibility = Visibility.Collapsed;
            CarsView.Visibility = Visibility.Collapsed;
            BrandsView.Visibility = Visibility.Visible;
            if (from != null) {
                var ri = _regionsShown.FindIndex(r => r.Key == from.Region);
                if (ri >= 0) _region = ri;
                var list = BrandsInRegion(from.Region);
                var bi = list.IndexOf(from);
                if (bi >= 0) _brandIndex = bi;
            }
            _inRail = false;
            ShowRegion(_region);   // 所有印の更新込み
        }

        private static string Drivetrain(CarObject car) {
            try {
                if (GtTuning.IsAwd(car)) return "4WD";
                var t = car.AcdData?.GetIniFile("drivetrain.ini")["TRACTION"].GetPossiblyEmpty("TYPE")?.ToUpperInvariant();
                if (t == "FWD") return "FF";
                if (t == "RWD") return "FR";
                return string.IsNullOrEmpty(t) ? "--" : t;
            } catch {
                return "--";
            }
        }

        private static bool HasDigit(string s) {
            return !string.IsNullOrEmpty(s) && s.Any(char.IsDigit);
        }

        /// <summary>「Mazda  Mazda RX-7 Tuned」にならないように、車名がブランド名で始まる時はブランドを重ねない。</summary>
        private static string CarLabel(CarObject car) {
            var n = car?.DisplayName ?? "";
            var b = car?.Brand ?? "";
            if (b.Length == 0) return n;
            var first = b.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? b;   // "Mercedes-Benz" → "Mercedes"
            return n.StartsWith(b, StringComparison.OrdinalIgnoreCase) || n.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? n : b + "  " + n;
        }

        private static void SplitUnit(string s, out string num, out string unit) {
            var m = System.Text.RegularExpressions.Regex.Match((s ?? "").Trim(), @"^([\d.,+\-]+)\s*(.*)$");
            if (m.Success) { num = m.Groups[1].Value; unit = m.Groups[2].Value; } else { num = string.IsNullOrEmpty(s) ? "--" : s; unit = ""; }
        }

        /// <summary>"540 Nm" → "55.1 kgfm"（GT7 表記）。それ以外はそのまま。</summary>
        private static string Torque(CarObject car) {
            var raw = (car.SpecsTorque ?? "").Trim();
            if (raw.Length == 0) return "--";
            var m = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", ""), @"([\d.]+)\s*(\S*)");
            if (!m.Success) return raw;
            double v;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return raw;
            if (m.Groups[2].Value.IndexOf("nm", StringComparison.OrdinalIgnoreCase) >= 0) return (v * 0.10197).ToString("0.0", CultureInfo.InvariantCulture) + " kgfm";
            return raw;
        }

        private static string Weight(CarObject car) {
            var raw = (car.SpecsWeight ?? "").Trim();
            if (raw.Length == 0) return "--";
            var m = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", ""), @"([\d.]+)\s*(\S*)");
            if (!m.Success) return raw;
            double v;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return raw;
            return v.ToString("N0", CultureInfo.InvariantCulture) + (m.Groups[2].Value.Length > 0 ? " " + m.Groups[2].Value : " kg");
        }

        /// <summary>GT7 の「カータイプ: #ロードカー」。AC のタグから大まかに。</summary>
        private static string CarType(CarObject car) {
            var tags = new List<string>();
            try {
                if (car.Tags != null) tags.AddRange(car.Tags.Select(t => (t ?? "").Trim().TrimStart('#').ToLowerInvariant()));
            } catch { }
            Func<string[], bool> has = keys => keys.Any(k => tags.Any(t => t.Contains(k)));
            if (has(new[] { "f1", "formula", "openwheel", "open wheel", "single seater", "monoposto" })) return "#フォーミュラカー";
            if (has(new[] { "prototype", "lmp", "le mans", "group c", "endurance" })) return "#プロトタイプ";
            if (has(new[] { "gt3", "gt2", "gte", "gt4", "gt1", "dtm", "race", "racing", "touring", "rally", "drift", "trackday", "track day" })) return "#レーシングカー";
            if (has(new[] { "vintage", "classic", "historic", "60s", "70s", "80s" })) return "#クラシックカー";
            if (has(new[] { "hypercar", "supercar" })) return "#スーパーカー";
            return "#ロードカー";
        }

        private FrameworkElement SpecRow(string label, string value, string unit) {
            // Reference layout: compact, right-aligned labels beside larger left-aligned values.
            var g = new Grid { MinHeight = 37 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock {
                Text = label, FontSize = 14, Foreground = LabelBrush,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            });
            var vp = new TextBlock {
                FontSize = 21, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI, Yu Gothic UI"), Foreground = ValueBrush,
                Margin = new Thickness(16, 3, 0, 3), VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            vp.Inlines.Add(new Run(value));
            if (!string.IsNullOrEmpty(unit)) {
                vp.Inlines.Add(new Run(" " + unit) { FontSize = 20 });
            }
            Grid.SetColumn(vp, 1);
            g.Children.Add(vp);
            return g;
        }

        private void StylePrimary(string text, bool filled, double opacity) {
            PrimaryText.Text = text;
            PrimaryBtn.Tag = filled;
            PrimaryBtn.Background = filled ? WhitePill : Brushes.Transparent;
            PrimaryBtn.BorderBrush = filled ? Brushes.Transparent : EdgeBrush;
            PrimaryText.Foreground = filled ? DarkText : ValueBrush;
            PrimaryText.FontWeight = filled ? FontWeights.SemiBold : FontWeights.Normal;
            PrimaryBtn.Opacity = opacity;
        }

        private void BuildDetail() {
            var car = _car;
            if (car == null) return;
            CarImg.Source = WorldMap.LoadBitmap(PickedSkin(car)?.PreviewImage, 1400);
            ShowPickedSkin();
            MakerText.Text = string.IsNullOrEmpty(car.Brand) ? _brand.Name : car.Brand;
            try { MakerFlag.Country = car.Country; } catch { }
            CarNameText.Text = car.DisplayName;

            var pp = SettingSheet.ComputePp(car);
            PpText.Inlines.Clear();
            PpText.Inlines.Add(new Run("PP ") { Foreground = LabelBrush });
            PpText.Inlines.Add(new Run(pp > 0 ? pp.ToString("0.00", CultureInfo.InvariantCulture) : "--") { Foreground = ValueBrush });

            SpecRows.Children.Clear();
            string n, u;
            SpecRows.Children.Add(SpecRow("駆動形式", Drivetrain(car), null));
            SplitUnit(GtHeader.PowerPs(car.SpecsBhp) ?? car.SpecsBhp, out n, out u);
            SpecRows.Children.Add(SpecRow("最高出力", n, u));
            SplitUnit(Torque(car), out n, out u);
            SpecRows.Children.Add(SpecRow("最大トルク", n, u));
            SplitUnit(Weight(car), out n, out u);
            SpecRows.Children.Add(SpecRow("車両重量", n, u));
            SpecRows.Children.Add(SpecRow("吸気形式", GtTuning.HasTurbo(car) ? "ターボ" : "NA（自然吸気）", null));
            if (HasDigit(car.SpecsTopSpeed)) {
                SplitUnit(car.SpecsTopSpeed, out n, out u);
                if (HasDigit(n)) SpecRows.Children.Add(SpecRow("最高速度", n, u));
            }
            if (HasDigit(car.SpecsAcceleration)) {
                SplitUnit(car.SpecsAcceleration, out n, out u);
                u = u.Replace("0-100", "").Trim();   // "6s 0-100" → 単位は "s" だけ
                if (HasDigit(n)) SpecRows.Children.Add(SpecRow("0-100 km/h", n, u));
            }
            if (car.Year.HasValue) SpecRows.Children.Add(SpecRow("年式", car.Year.Value.ToString(CultureInfo.InvariantCulture), null));
            if (!string.IsNullOrWhiteSpace(car.CarClass)) SpecRows.Children.Add(SpecRow("クラス", car.CarClass.ToUpperInvariant(), null));

            CarTypeText.Text = CarType(car);
            ZukanState.Text = string.IsNullOrWhiteSpace(car.Description) ? "説明なし" : "説明を読む";

            var owned = GtGarage.IsOwned(car);
            var current = owned && GtGarage.IsCurrent(car);
            OwnStateText.Text = current ? "✓ 乗っているクルマ" : owned ? "✓ ガレージにあります" : "まだ持っていません";
            OwnStateText.Foreground = owned ? GoldBrush : LabelBrush;
            PriceText.Text = PriceLabel(car);
            if (!owned) StylePrimary("購入する", true, 1);
            else StylePrimary("手放す", false, current ? 0.4 : 1);
        }
        #endregion

        #region 3D ショールーム（CmJaOverlay Viewer・ガレージと同じ空間）
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        private bool OpenShowroom(CarObject car) {
            try {
                var open = ViewerMethod("OpenBrandCentral");
                if (open == null || car == null) return false;
                _showroomHandler = OnShowroomAction;
                _showroom = true;
                var ok = open.Invoke(null, new object[] { Application.Current.MainWindow, car, PickedSkin(car), _showroomHandler });
                if (ok is bool && !(bool)ok) {
                    Logging.Warning("[BrandCentral] showroom busy → 2D");
                    _showroom = false;
                    return false;
                }
                Logging.Write("[BrandCentral] showroom open: " + car.Id);
                return true;
            } catch (Exception e) {
                Logging.Warning("[BrandCentral] showroom open: " + e.Message);
                _showroom = false;
                return false;
            }
        }

        private void CloseShowroom() {
            _switchTimer?.Stop();
            _switchWanted = null;
            try {
                var m = ViewerMethod("CloseShowroom");
                var ok = m == null ? null : m.Invoke(null, null);
                if (!(ok is bool) || !(bool)ok) {
                    // 新しい DLL は起動中の取消も受け付け、後始末後に closed を返す。
                    // DLL が無い／既に閉じている時だけ自分で後始末する。
                    Logging.Warning("[BrandCentral] close showroom: not open → exit");
                    ExitShowroom();
                }
            } catch (Exception e) {
                Logging.Warning("[BrandCentral] close showroom: " + e.Message);
                ExitShowroom();
            }
        }

        private void RequestSettingsFromShowroom() {
            if (_gone || !ReferenceEquals(Active, this)) return;
            _settingsAfterShowroom = true;
            CloseShowroom();   // 設定への遷移は closed を受けた ExitShowroom の後始末後に行う。
        }

        private void MinimizeShowroom() {
            try {
                var m = ViewerMethod("MinimizeShowroom");
                if (m == null || !Equals(m.Invoke(null, null), true)) {
                    Logging.Warning("[BrandCentral] minimize showroom: unavailable");
                }
            } catch (Exception e) {
                Logging.Warning("[BrandCentral] minimize showroom: " + e.Message);
            }
        }

        private DispatcherTimer _switchTimer;
        private CarObject _switchWanted;
        private bool _switchCoverRequested;
        private int _switchShadeVersion;

        private void SetSwitchShade(bool covered) {
            if (_switchCoverRequested == covered && SwitchShade.Visibility == Visibility.Visible) return;
            _switchCoverRequested = covered;
            var version = ++_switchShadeVersion;
            SwitchShade.Visibility = Visibility.Visible;
            var animation = new DoubleAnimation(SwitchShade.Opacity, covered ? 1 : 0,
                    TimeSpan.FromMilliseconds(covered ? 200 : 320)) { EasingFunction = new SineEase() };
            animation.Completed += (s, e) => {
                if (version != _switchShadeVersion) return;
                if (!covered) ResetSwitchShade();
            };
            SwitchShade.BeginAnimation(OpacityProperty, animation);
        }

        private void ResetSwitchShade() {
            ++_switchShadeVersion;
            _switchCoverRequested = false;
            SwitchShade.BeginAnimation(OpacityProperty, null);
            SwitchShade.Opacity = 0;
            SwitchShade.Visibility = Visibility.Collapsed;
        }

        private void SetPreviewOnly(bool preview) {
            CarsView.Background = preview ? (Brush)FindResource("GtBg") : null;
            CarImg.Visibility = preview || !_showroom ? Visibility.Visible : Visibility.Collapsed;
            PreviewNotice.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>3D の車を差し替える。帯を連打しても 320ms 止まった時の車だけ読む（kn5 の同時読込は x86 で OOM）。</summary>
        private void SwitchCar3D(CarObject car) {
            _switchWanted = car;
            SetSwitchShade(true);
            if (_switchTimer == null) {
                _switchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
                _switchTimer.Tick += (s, e) => {
                    _switchTimer.Stop();
                    var c = _switchWanted;
                    _switchWanted = null;
                    if (c == null || !_showroom || _gone) return;
                    try {
                        var m = ViewerMethod("SwitchCar");
                        if (m == null) { ResetSwitchShade(); return; }
                        var ok = m.Invoke(null, new object[] { c, PickedSkin(c) });
                        if (ok is bool && !(bool)ok) {
                            ResetSwitchShade();
                            Logging.Warning("[BrandCentral] switch car failed: " + c.Id);
                        }
                    } catch (Exception ex) {
                        ResetSwitchShade();
                        Logging.Warning("[BrandCentral] switch car: " + ex.Message);
                    }
                };
            }
            _switchTimer.Stop();
            _switchTimer.Start();
        }

        /// <summary>DLL からの合図（UI スレッド）。"ready:l,t,w,h" / "key:<Keys>" / "closing" / "closed"。戻り値=消費したか。</summary>
        private bool OnShowroomAction(string cmd) {
            try {
                if (cmd == null) return false;
                if (cmd.StartsWith("preview:")) {
                    if (_gone || !_showroom) return false;
                    SetPreviewOnly(cmd == "preview:on");
                    return true;
                }
                if (cmd.StartsWith("switch:")) {
                    if (_gone || !_showroom) return false;
                    switch (cmd.Substring(7)) {
                        case "cover": SetSwitchShade(true); return true;
                        case "covered": return SwitchShade.Visibility == Visibility.Visible && SwitchShade.Opacity >= 0.999;
                        case "pending": return _switchTimer != null && _switchTimer.IsEnabled;
                        case "reveal": SetSwitchShade(false); return true;
                        case "clear": return SwitchShade.Visibility == Visibility.Collapsed || SwitchShade.Opacity <= 0.001;
                        case "reset": ResetSwitchShade(); return true;
                    }
                }
                if (cmd.StartsWith("ready:")) { ShowPanel3D(cmd.Substring(6)); return true; }
                if (cmd == "hidden") { HidePanel3D(); return true; }
                if (cmd == "shown") {
                    if (!_gone && _showroom && _win3d != null) {
                        _win3d.Show();
                        AttachToShowroom();   // 最小化からの復元: ビネットの上へ出し直す
                    }
                    return true;
                }
                if (cmd == "closing") { HidePanel3D(); return true; }
                if (cmd == "closed") { ExitShowroom(); return true; }
                if (cmd.StartsWith("key:")) return HandleShowroomKey(cmd.Substring(4));
            } catch (Exception e) {
                Logging.Warning("[BrandCentral] showroom action " + cmd + ": " + e.Message);
            }
            return false;
        }

        /// <summary>CarsRoot（右パネル＋帯＋確認/図鑑）をショールームの上の透明窓へ載せる。</summary>
        private void ShowPanel3D(string rect) {
            if (_gone || !_showroom) return;
            ResetSwitchShade();
            SetPreviewOnly(false);
            double l = 0, t = 0, w = 0, h = 0;
            try {
                var p = rect.Split(',');
                l = double.Parse(p[0], CultureInfo.InvariantCulture); t = double.Parse(p[1], CultureInfo.InvariantCulture);
                w = double.Parse(p[2], CultureInfo.InvariantCulture); h = double.Parse(p[3], CultureInfo.InvariantCulture);
            } catch { }
            if (w < 100 || h < 100) {
                var main = Application.Current.MainWindow;
                l = main.Left; t = main.Top; w = main.ActualWidth; h = main.ActualHeight;
            }
            if (_win3d == null) {
                _win3d = new Window {
                    Title = "CM7 BrandCentral", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                    Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                    FontFamily = new FontFamily("Yu Gothic UI, Segoe UI")
                };
                _win3d.SizeChanged += (s, e) => UpdateStripBlur();
                _win3d.SourceInitialized += (s, e) => {
                    try {
                        var hw = new WindowInteropHelper(_win3d).Handle;
                        SetWindowLong(hw, GWL_EXSTYLE, GetWindowLong(hw, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                    } catch { }
                };
                var dock = new DockPanel();
                var header = new GtHeader {
                    AdoptBgmPanel = false,
                    CloseRequested = CloseShowroom,
                    MinimizeRequested = MinimizeShowroom,
                    SettingsRequested = RequestSettingsFromShowroom
                };   // 透明窓だけを閉じず、ショールーム本体と連動させる
                DockPanel.SetDock(header, Dock.Top);
                dock.Children.Add(header);
                _win3dHost = new Grid();
                dock.Children.Add(_win3dHost);
                _win3d.Content = dock;
            }
            MoveCarsRoot(_win3dHost);
            CarsView.Visibility = Visibility.Visible;
            CarsView.Opacity = 1;
            CarImg.Visibility = Visibility.Collapsed;        // 車は後ろの 3D
            LeftScrim.Visibility = Visibility.Visible;
            RightScrim.Visibility = Visibility.Visible;
            // 「見てみる」は要らない（もう 3D）。代わりに「ACで鑑賞」（CSP で守られた車は CM では 3D が出ないため・2026-09-28）
            SecondaryText.Text = "ACで鑑賞";
            SecondaryBtn.Visibility = Visibility.Visible;
            BindCarsHint(true);
            BigBrandLogo.Source = WorldMap.LoadBitmap(_brand.Badge, 256);
            BuildStrip();
            RegisterStrip(_carIndex);
            SelectCar(_carIndex, false);   // ショールームは既にこの車
            _win3d.Left = l; _win3d.Top = t; _win3d.Width = w; _win3d.Height = h;
            _win3d.Opacity = 0;
            _win3d.Show();
            AttachToShowroom();
            _win3d.UpdateLayout();
            UpdateStripBlur();
            _win3d.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)));
            Logging.Write("[BrandCentral] panel 3d shown " + rect);
        }

        /// <summary>[fork] パッド/ハンコンの「戻る」(B): 3D 中はキー合成(Esc)を使わず、ここで確認/図鑑を閉じるかショールームを閉じる。
        /// キー合成は CM 本体の窓にフォーカスを移すので、ブランド一覧がショールームの手前に一瞬映り、
        /// ショールームへフォーカスが戻る時に設定パネルまで出ていた（2026-09-27 要望「B で戻った時に何か一瞬映る」・連写で確認）。</summary>
        internal static bool HandlePadBack() {
            var p = Active;
            if (p == null || p._gone || !p._showroom) return false;
            GtSfx.PlayCancel();
            if (p._overlay != null) p.CloseOverlay();
            else p.CloseShowroom();
            return true;
        }

        /// <summary>透明窓をショールーム窓の持ち物にする（DLL の Viewer.AttachToShowroom）。他アプリへ切り替えたら一緒に裏へ下がる
        /// （2026-09-27 要望「裏に回ったら一緒に下がるようにして」）。古い DLL では Topmost のまま。</summary>
        private void AttachToShowroom() {
            try {
                if (_win3d != null) ViewerMethod("AttachToShowroom")?.Invoke(null, new object[] { _win3d });
            } catch (Exception e) {
                Logging.Warning("[BrandCentral] attach to showroom: " + e.Message);
            }
        }

        private void HidePanel3D() {
            try { if (_win3d != null && _win3d.IsVisible) _win3d.Hide(); } catch { }
        }

        private void UpdateStripBlur() {
            if (!_showroom || _win3d == null || !_win3d.IsVisible || _win3d.ActualHeight <= 0) return;
            try {
                var top = StripSurface.TranslatePoint(new Point(), _win3d).Y / _win3d.ActualHeight;
                ViewerMethod("SetBrandStripBlur")?.Invoke(null, new object[] { top });
            } catch (Exception e) { Logging.Warning("[BrandCentral] strip blur: " + e.Message); }
        }

        /// <summary>ショールームが閉じた: CarsRoot をページへ戻して 2D 表示に整え、メーカー一覧へ。</summary>
        private void ExitShowroom() {
            var goSettings = _settingsAfterShowroom;
            _settingsAfterShowroom = false;
            _switchTimer?.Stop();
            _switchWanted = null;
            ResetSwitchShade();
            SetPreviewOnly(false);
            _showroom = false;
            HidePanel3D();
            RestoreCarsRoot();
            try { if (_win3d != null) { _win3d.Close(); } } catch { }
            _win3d = null;
            _win3dHost = null;
            CarImg.Visibility = Visibility.Visible;
            LeftScrim.Visibility = Visibility.Collapsed;
            RightScrim.Visibility = Visibility.Collapsed;
            SecondaryText.Text = "見てみる";
            SecondaryBtn.Visibility = Visibility.Visible;
            BindCarsHint(false);
            CarsView.Visibility = Visibility.Collapsed;
            if (_gone || !ReferenceEquals(Active, this)) return;
            if (_watching) return;   // 「ACで鑑賞」の間は一覧へ戻らない（AC から戻ったら WatchInAc が同じ車で開き直す）
            if (_watchReopen != null) {   // AC の方が先に終わっていた（起動にすぐ失敗した等）→ ここで同じ車を開き直す
                var r = _watchReopen;
                _watchReopen = null;
                ShowBrand(r.Item1, r.Item2);
                return;
            }
            if (goSettings) {
                WorldMap.Navigate(GtHeader.SettingsUri);
                return;
            }
            BackToBrands();
        }

        private void MoveCarsRoot(Panel target) {
            var parent = CarsRoot.Parent as Panel;
            if (ReferenceEquals(parent, target)) return;
            if (parent != null) {
                if (_carsHome == null) { _carsHome = parent; _carsHomeIndex = parent.Children.IndexOf(CarsRoot); }
                parent.Children.Remove(CarsRoot);
            }
            if (target != null) target.Children.Add(CarsRoot);
        }

        private void RestoreCarsRoot() {
            if (_carsHome == null) return;
            var parent = CarsRoot.Parent as Panel;
            if (ReferenceEquals(parent, _carsHome)) return;
            if (parent != null) parent.Children.Remove(CarsRoot);
            var idx = Math.Min(Math.Max(0, _carsHomeIndex), _carsHome.Children.Count);
            _carsHome.Children.Insert(idx, CarsRoot);
        }

        /// <summary>RenderForm のキー（DLL 経由・WinForms Keys の名前）。true=消費（Esc は消費しなければショールームが閉じる）。</summary>
        private bool HandleShowroomKey(string name) {
            if (_gone) return false;
            if (_overlay != null) {
                switch (name) {
                    case "Escape": GtSfx.PlayCancel(); CloseOverlay(); return true;
                    case "Return": case "Enter": case "Space": ConfirmAction(); return true;
                    case "Up": if (BuyingColor) StepSkin(-1); return true;
                    case "Down": if (BuyingColor) StepSkin(1); return true;
                }
                return false;
            }
            switch (name) {
                case "Escape": GtSfx.PlayCancel(); return false;   // ショールームが閉じる → closing/closed で一覧へ
                case "Up": StepSkin(-1); return true;     // ボディカラー
                case "Down": StepSkin(1); return true;
                case "Left": GtSfx.PlayMove(); SelectCar(_carIndex - 1); GtFocus.SetIndex(_carIndex); return true;
                case "Right": GtSfx.PlayMove(); SelectCar(_carIndex + 1); GtFocus.SetIndex(_carIndex); return true;
                case "Return": case "Enter": case "Space": OpenConfirm(); return true;
                case "T": OpenZukan(); return true;
                case "U": WatchInAc(); return true;
                case "Q": case "PageUp": case "Prior": StepBrand(-1); return true;
                case "E": case "PageDown": case "Next": StepBrand(1); return true;
            }
            return false;
        }
        #endregion

        #region 確認・図鑑・鑑賞
        private void ShowOverlay(Grid overlay) {
            overlay.Visibility = Visibility.Visible;
            overlay.Opacity = 0;
            overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }

        /// <summary>「ガレージに入れる」/「手放す」の確認。乗っている車は手放せない（音だけ）。</summary>
        private void OpenConfirm() {
            if (_car == null || _overlay != null) return;
            var owned = GtGarage.IsOwned(_car);
            if (owned && GtGarage.IsCurrent(_car)) { GtSfx.PlayCancel(); return; }
            GtSfx.PlaySelect();
            _confirmRemove = owned;
            _overlay = "confirm";
            ConfirmTitle.Text = owned ? "このクルマを手放しますか？" : "ボディカラーを選んで購入";
            ConfirmSub.Text = owned
                    ? "ガレージから外れます。ブランドセントラルでいつでも取り戻せます。"
                    : "↑ ↓ か ‹ › で色を選んでください。購入後、ワールドマップに戻るとガレージへ届きます。";
            ConfirmImg.Source = CarImg.Source;
            ConfirmCar.Text = CarLabel(_car);
            ConfirmYesText.Text = owned ? "手放す" : "この色で購入";
            ConfirmSkinRow.Visibility = owned ? Visibility.Collapsed : Visibility.Visible;
            ConfirmPrice.Text = owned ? "" : PriceLabel(_car);
            if (!owned) ShowPickedSkin();
            ConfirmNo.Visibility = Visibility.Visible;
            ConfirmNo.BorderBrush = EdgeBrush;
            ConfirmYes.BorderBrush = Brushes.Transparent;
            ShowOverlay(ConfirmOverlay);
            RegisterOverlay(ConfirmNo, ConfirmYes, 1);
        }

        private void ConfirmAction() {
            if (_overlay == "done") { GtSfx.PlaySelect(); CloseOverlay(); return; }
            if (_overlay == "zukan") { GtSfx.PlayCancel(); CloseOverlay(); return; }
            if (_overlay != "confirm" || _car == null) return;
            if (_confirmRemove) {
                string reason;
                if (GtGarage.Remove(_car, out reason)) {
                    GtSfx.PlayCancel();
                    CloseOverlay();
                    RefreshAfterChange();
                } else {
                    GtSfx.PlayCancel();
                    ConfirmSub.Text = reason;
                }
                return;
            }
            if (!GtGarage.Add(_car)) { CloseOverlay(); return; }
            // 選んだ色で納車（CM の選択スキン＝ガレージ・レースの色）
            try {
                var picked = PickedSkin(_car);
                if (picked != null && !ReferenceEquals(picked, _car.SelectedSkin)) _car.SelectedSkin = picked;
            } catch (Exception e) { Logging.Warning("[BrandCentral] picked skin: " + e.Message); }
            GtSfx.PlaySelect();
            _overlay = "done";
            ConfirmSkinRow.Visibility = Visibility.Collapsed;
            ConfirmTitle.Text = "納車の手続きが完了しました";
            ConfirmSub.Text = "ワールドマップに戻ると、ガレージに届きます。";
            ConfirmYesText.Text = "OK";
            ConfirmNo.Visibility = Visibility.Collapsed;
            RefreshAfterChange();
            RegisterOverlay(null, ConfirmYes, 0);
        }

        /// <summary>所有が変わった後: 帯の印・右パネル・ヘッダーを描き直す（フォーカス登録は CloseOverlay 側）。</summary>
        private void RefreshAfterChange() {
            if (_brand == null) return;
            BuildStrip();
            PaintCards();
            if (_carIndex < _stripCards.Count) EnsureVisibleH(_stripCards[_carIndex]);
            try { BuildDetail(); } catch { }
            try { Header.Refresh(); } catch { }
        }

        private void CloseOverlay() {
            if (_overlay == null) return;
            _overlay = null;
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            ZukanOverlay.Visibility = Visibility.Collapsed;
            if (InCars) RegisterStrip(_carIndex);
            else RegisterBrands(_brandIndex);
        }

        private void OpenZukan() {
            if (_car == null || _overlay != null) return;
            GtSfx.PlaySelect();
            _overlay = "zukan";
            ZukanImg.Source = CarImg.Source;
            var meta = new List<string>();
            if (!string.IsNullOrEmpty(_car.Brand)) meta.Add(_car.Brand.ToUpperInvariant());
            if (_car.Year.HasValue) meta.Add(_car.Year.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(_car.Country)) meta.Add(_car.Country.ToUpperInvariant());
            ZukanBrand.Text = string.Join("  ·  ", meta);
            ZukanName.Text = _car.DisplayName;
            var sub = new List<string>();
            try { var s = GtHeader.BuildSpecLine(_car); if (!string.IsNullOrEmpty(s)) sub.Add(s); } catch { }
            if (!string.IsNullOrWhiteSpace(_car.Author)) sub.Add(_car.Author + (string.IsNullOrWhiteSpace(_car.Version) ? "" : " · v" + _car.Version));
            ZukanMeta.Text = string.Join("\n", sub);
            var d = (_car.Description ?? "").Trim();
            ZukanText.Text = d.Length == 0 ? "このクルマの説明はありません。" : d;
            ZukanClose.BorderBrush = EdgeBrush;
            ShowOverlay(ZukanOverlay);
            _overlayFocus.Clear();
            _overlayFocus.Add(new GtFocusItem {
                HandlesActivationSound = true,
                Name = "close", Center = () => PadCenter(ZukanClose),
                SetHot = on => ZukanClose.BorderBrush = on ? Brushes.White : EdgeBrush,
                Activate = () => { GtSfx.PlayCancel(); CloseOverlay(); }
            });
            if (_gone) return;
            GtFocus.Register(this, _overlayFocus, GtFocusLayout.Row, 0, 0);
            GtFocus.SetPageCommand(this, PageCommand);
        }

        /// <summary>CmJaOverlay の鑑賞モード（Viewer.Open(Window, car, skin)）をこの車で。2D の時だけ（3D では既にショールーム）。</summary>
        private void OpenShowcase() {
            if (_car == null || _overlay != null || _showroom) return;
            GtSfx.PlaySelect();
            try {
                var open = ViewerMethod("Open");
                if (open != null) {
                    open.Invoke(null, new object[] { Application.Current.MainWindow, _car, _car.SelectedSkin });
                    return;
                }
                Logging.Warning("[BrandCentral] showcase: CmJaOverlay.Viewer.Open not available");
            } catch (Exception e) {
                Logging.Warning("[BrandCentral] showcase: " + e.Message);
            }
        }

        /// <summary>[fork] 「ACで鑑賞」: この車・今の色で AC 本体の鑑賞モード（apps\lua\GtView）を開く。
        /// CSP で守られた車（暗号化 kn5）は CM のショールームでは 3D が出ずプレビュー画像になるため
        /// （2026-09-28 要望「ワイはただ鑑賞したいだけ…これができなきゃ車を眺められないじゃないか」）。
        /// 幕を出してからショールームを閉じ、AC から戻ったら同じ車で開き直す。AC で選んだ色はこちらのボディカラーにも反映する。</summary>
        private async void WatchInAc() {
            if (_watching || _gone || _car == null || _brand == null || _overlay != null || GtViewLaunch.IsBusy) return;
            if (_showroom && (_switchTimer != null && _switchTimer.IsEnabled || SwitchShade.Visibility == Visibility.Visible)) return;
            var brand = _brand;
            var carIndex = _carIndex;
            var car = _car;
            var skin = PickedSkin(car);
            _watching = true;
            _watchReopen = null;
            GtSfx.PlaySelect();
            string picked = null;
            var closedShowroom = false;   // 幕を出して閉じに行ったか（幕の前に失敗した時はショールームは開いたまま＝何もしない）
            try {
                picked = await GtViewLaunch.RunAsync(car, skin?.Id, () => {
                    if (!_showroom) return;
                    closedShowroom = true;
                    CloseShowroom();
                });
            } catch (Exception e) {
                Logging.Warning("[BrandCentral] watch in AC: " + e.Message);
            } finally {
                _watching = false;
            }
            if (!string.IsNullOrEmpty(picked)) {
                var s = SkinsOf(car).FirstOrDefault(x => string.Equals(x.Id, picked, StringComparison.OrdinalIgnoreCase));
                if (s != null) _pickedSkin[car.Id] = s;
            }
            if (!closedShowroom || _gone || !ReferenceEquals(Active, this)) return;
            if (_showroom) {
                _watchReopen = Tuple.Create(brand, carIndex);   // まだ closed が来ていない → ExitShowroom で開き直す
                return;
            }
            ShowBrand(brand, carIndex);
        }

        private void OnOverlayClick(object sender, MouseButtonEventArgs e) {
            GtSfx.PlayCancel();
            CloseOverlay();
        }

        private void OnCardClick(object sender, MouseButtonEventArgs e) {
            e.Handled = true;   // カードの外側クリック（閉じる）に伝播させない
        }

        private void OnStripWheel(object sender, MouseWheelEventArgs e) {
            StripScroll.ScrollToHorizontalOffset(StripScroll.HorizontalOffset - e.Delta * 1.2);
            e.Handled = true;
        }
        #endregion

        private void OnKeyDown(object sender, KeyEventArgs e) {
            if (_overlay != null) {
                // Main-window Escape already sounds in GtNav's preview handler.
                // The separate 3D key route keeps its own sound in HandleShowroomKey.
                if (e.Key == Key.Escape) { e.Handled = true; CloseOverlay(); }
                else if (e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; ConfirmAction(); }
                else if (BuyingColor && (e.Key == Key.Up || e.Key == Key.Down)) { e.Handled = true; StepSkin(e.Key == Key.Up ? -1 : 1); }
                return;
            }
            if (InCars) {
                switch (e.Key) {
                    case Key.Escape: e.Handled = true; BackToBrands(); return;   // 戻る音は GtNav の Preview 側が鳴らす
                    case Key.Left: e.Handled = true; GtSfx.PlayMove(); SelectCar(_carIndex - 1); GtFocus.SetIndex(_carIndex); return;
                    case Key.Right: e.Handled = true; GtSfx.PlayMove(); SelectCar(_carIndex + 1); GtFocus.SetIndex(_carIndex); return;
                    case Key.Enter: case Key.Space: e.Handled = true; OpenConfirm(); return;
                    case Key.U: e.Handled = true; OpenShowcase(); return;
                    case Key.T: e.Handled = true; OpenZukan(); return;
                    case Key.Q: case Key.PageUp: e.Handled = true; StepBrand(-1); return;
                    case Key.E: case Key.PageDown: e.Handled = true; StepBrand(1); return;
                }
                return;
            }
            if (e.Key == Key.Escape) { e.Handled = true; GtNav.GoBack(); return; }
            if (e.Key == Key.Q || e.Key == Key.PageUp) { e.Handled = true; ShowRegion(_region - 1); GtSfx.PlayMove(); }
            else if (e.Key == Key.E || e.Key == Key.PageDown) { e.Handled = true; ShowRegion(_region + 1); GtSfx.PlayMove(); }
        }

        #region テストチャンネル
        private sealed class TestRecording {
            public Window Window;
            public DispatcherTimer Timer;
            public System.Diagnostics.Stopwatch Clock;
            public System.Threading.Tasks.Task Writing;
            public string Folder, EndReason;
            public int Frames;
            public bool Stopping;
        }

        private TestRecording _testRecording;
        private string _testRecordingStatus = "idle";

        private bool StartTestRecording(string folder) {
            if (!Dispatcher.CheckAccess() || _testRecording != null || _gone || !_showroom
                    || _win3d == null || !_win3d.IsVisible
                    || !File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "cm7_test_enabled.flag"))) return false;
            try {
                // Reject relative/drive-relative destinations and never overwrite an earlier recording.
                if (string.IsNullOrWhiteSpace(folder)
                        || !(folder.StartsWith(@"\\", StringComparison.Ordinal)
                            || folder.Length >= 3 && char.IsLetter(folder[0]) && folder[1] == ':' && (folder[2] == '\\' || folder[2] == '/'))) return false;
                folder = System.IO.Path.GetFullPath(folder);
                Directory.CreateDirectory(folder);
                using (var stream = new FileStream(System.IO.Path.Combine(folder, "frames.csv"), FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false))) {
                    writer.WriteLine("elapsed_ms,shade_opacity,car_id,frame");
                }
                var recording = new TestRecording {
                    Window = _win3d,
                    Folder = folder,
                    Clock = System.Diagnostics.Stopwatch.StartNew(),
                    Timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(75) }
                };
                recording.Timer.Tick += (s, e) => CaptureTestRecording(recording);
                _testRecording = recording;
                _testRecordingStatus = "recording";
                recording.Timer.Start();
                CaptureTestRecording(recording);
                return ReferenceEquals(_testRecording, recording);
            } catch (Exception ex) {
                Logging.Warning("[BrandCentral] record start: " + ex.Message);
                return false;
            }
        }

        private void CaptureTestRecording(TestRecording recording) {
            if (!ReferenceEquals(recording, _testRecording) || recording.Stopping) return;
            if (_gone || !_showroom || !ReferenceEquals(_win3d, recording.Window) || !recording.Window.IsVisible
                    || recording.Window.WindowState == WindowState.Minimized) {
                StopTestRecording(recording, "hidden");
                return;
            }
            if (recording.Clock.ElapsedMilliseconds >= 5000) {
                StopTestRecording(recording, "done");
                return;
            }
            // At most one PNG write in flight: a slow disk drops samples instead of blocking the animation.
            if (recording.Writing != null) return;
            try {
                var window = recording.Window;
                var source = PresentationSource.FromVisual(window);
                var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                var width = (int)Math.Ceiling(window.ActualWidth * transform.M11);
                var height = (int)Math.Ceiling(window.ActualHeight * transform.M22);
                if (width <= 0 || height <= 0) { StopTestRecording(recording, "empty"); return; }
                var elapsed = recording.Clock.Elapsed.TotalMilliseconds;
                var shade = SwitchShade.Opacity;
                var carId = _car?.Id ?? "";
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) {
                    drawing.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap(width, height, 96 * transform.M11, 96 * transform.M22, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                var filename = "frame_" + recording.Frames.ToString("D4", CultureInfo.InvariantCulture) + ".png";
                var row = elapsed.ToString("F3", CultureInfo.InvariantCulture) + ","
                        + shade.ToString("F6", CultureInfo.InvariantCulture) + ",\"" + carId.Replace("\"", "\"\"") + "\","
                        + filename + Environment.NewLine;
                recording.Writing = System.Threading.Tasks.Task.Run(() => {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = new FileStream(System.IO.Path.Combine(recording.Folder, filename), FileMode.CreateNew, FileAccess.Write)) encoder.Save(output);
                    File.AppendAllText(System.IO.Path.Combine(recording.Folder, "frames.csv"), row, new System.Text.UTF8Encoding(false));
                });
                recording.Writing.ContinueWith(task => {
                    try {
                        var error = task.Exception;
                        Dispatcher.BeginInvoke(new Action(() => {
                            recording.Writing = null;
                            if (!ReferenceEquals(recording, _testRecording)) return;
                            if (error != null) {
                                Logging.Warning("[BrandCentral] record write: " + error.GetBaseException().Message);
                                StopTestRecording(recording, "error");
                            } else {
                                recording.Frames++;
                                if (recording.Stopping) StopTestRecording(recording, recording.EndReason);
                            }
                        }));
                    } catch { /* The dispatcher can already be shutting down; each file stream is independently disposed. */ }
                });
            } catch (Exception ex) {
                Logging.Warning("[BrandCentral] record capture: " + ex.Message);
                StopTestRecording(recording, "error");
            }
        }

        private void StopTestRecording(TestRecording recording, string reason) {
            recording.Stopping = true;
            recording.EndReason = reason;
            recording.Timer.Stop();
            recording.Clock.Stop();
            _testRecordingStatus = "finishing";
            if (recording.Writing != null) return;
            _testRecordingStatus = reason + ":" + recording.Frames;
            if (ReferenceEquals(recording, _testRecording)) _testRecording = null;
            Logging.Write("[BrandCentral] record " + reason + " frames=" + recording.Frames + " folder=" + recording.Folder);
        }

        internal string TestDump() {
            try {
                return "bc=view:" + (InCars ? "cars" : "brands")
                        + " mode:" + (_showroom ? "3d" : "2d") + (_win3d != null && _win3d.IsVisible ? "+panel" : "")
                        + " region:" + (_regionsShown.Count > 0 ? _regionsShown[_region].Key : "-")
                        + " brands:" + _brandFocus.Count + " rail:" + (_inRail ? "1" : "0")
                        + " brand:" + (_brand?.Name ?? "-")
                        + " car:" + (_car?.Id ?? "-") + " index:" + _carIndex + "/" + (_brand?.Cars.Count ?? 0)
                        + " skin:" + (PickedSkin(_car)?.Id ?? "-") + " selected:" + (_car?.SelectedSkin?.Id ?? "-")
                        + " owned:" + (_car != null && GtGarage.IsOwned(_car) ? "1" : "0")
                        + " shade:" + SwitchShade.Opacity.ToString("0.000", CultureInfo.InvariantCulture)
                        + " preview:" + (PreviewNotice.Visibility == Visibility.Visible ? "yes" : "no")
                        + " watch:" + (_watching ? "yes" : "no")
                        + " record:" + _testRecordingStatus
                        + " overlay:" + (_overlay ?? "-") + "\n"
                        + "bc-rows=" + (_regionsShown.Count > 0
                                ? string.Join(" | ", RowsOf(_regionsShown[_region].Key).Select(r => r.Name + ":" + string.Join(",", r.Brands.Select(b => b.Name))))
                                : "-") + "\n";
            } catch (Exception e) {
                return "bc=(error " + e.Message + ")\n";
            }
        }

        internal bool TestOpenBrand(string name) {
            // Optional initial car allows the opt-in channel to exercise showroom startup,
            // without issuing a pick before the asynchronous opening has completed.
            var parts = name.Split(new[] { '|' }, 2);
            var b = _brands.FirstOrDefault(x => string.Equals(x.Name, parts[0], StringComparison.OrdinalIgnoreCase));
            if (b == null) return false;
            var initial = parts.Length == 1 ? 0 : b.Cars.FindIndex(c => string.Equals(c.Id, parts[1], StringComparison.OrdinalIgnoreCase));
            if (initial < 0) return false;
            var ri = _regionsShown.FindIndex(r => r.Key == b.Region);
            if (ri >= 0 && ri != _region) ShowRegion(ri);
            ShowBrand(b, initial);
            return true;
        }

        internal bool TestPick(string v) {
            if (_brand == null) return false;
            int i;
            if (int.TryParse(v, out i)) { SelectCar(i); GtFocus.SetIndex(_carIndex); return true; }
            var idx = _brand.Cars.FindIndex(c => string.Equals(c.Id, v, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            SelectCar(idx);
            GtFocus.SetIndex(_carIndex);
            return true;
        }

        internal bool TestCommand(string v) {
            if (v.StartsWith("record:", StringComparison.Ordinal)) return StartTestRecording(v.Substring(7));
            if (v.StartsWith("blur:")) {
                double top;
                if (!double.TryParse(v.Substring(5), NumberStyles.Float, CultureInfo.InvariantCulture, out top)
                        || double.IsNaN(top) || double.IsInfinity(top)) return false;
                var setBlur = ViewerMethod("SetBrandStripBlur");
                if (setBlur == null || !_showroom) return false;
                setBlur.Invoke(null, new object[] { top });
                return true;
            }
            if (v.StartsWith("camera:")) {
                var parts = v.Split(new[] { ':' }, 4);
                float distance, alpha;
                if (parts.Length != 4 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out distance)
                        || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out alpha)) return false;
                var method = ViewerMethod("TestBrandCamera");
                if (method == null || !(bool)method.Invoke(null, new object[] { parts[3], distance, alpha })) return false;
                // Include the actual transparent WPF controls in a second capture;
                // neither capture requires the showroom to be in the foreground.
                if (_win3d != null) {
                    var scene = new BitmapImage();
                    scene.BeginInit(); scene.CacheOption = BitmapCacheOption.OnLoad;
                    scene.UriSource = new Uri(System.IO.Path.GetFullPath(parts[3])); scene.EndInit(); scene.Freeze();
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen()) {
                        var rect = new Rect(0, 0, scene.PixelWidth, scene.PixelHeight);
                        dc.DrawImage(scene, rect);
                        dc.DrawRectangle(new VisualBrush(_win3d), null, rect);
                    }
                    var image = new RenderTargetBitmap(scene.PixelWidth, scene.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var output = File.Create(System.IO.Path.ChangeExtension(parts[3], "ui.png"))) encoder.Save(output);
                }
                return true;
            }
            switch (v) {
                case "confirm": if (_overlay != null) ConfirmAction(); else OpenConfirm(); return true;
                case "cancel": CloseOverlay(); return true;
                case "back": if (InCars) BackToBrands(); else GtNav.GoBack(); return true;
                case "zukan": OpenZukan(); return true;
                case "showcase": OpenShowcase(); return true;
                case "watch": WatchInAc(); return true;   // [fork] 「ACで鑑賞」（AC を起動する＝本人の OK を取ってから）
                case "skin:next": StepSkin(1); return true;
                case "skin:prev": StepSkin(-1); return true;
            }
            if (v.StartsWith("region:")) {
                var key = v.Substring(7);
                var ri = _regionsShown.FindIndex(r => r.Key == key);
                if (ri < 0) return false;
                ShowRegion(ri);
                return true;
            }
            return false;
        }
        #endregion
    }
}
