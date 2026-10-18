// [fork] GT7 風「コースイン」フロー — 地域マップでコースを選んだ後の画面。
//   モード（タイムトライアル／レース）→ レイアウト → 時刻 → コースイン を 1 ページ内でステップ遷移。
//   コースインは普段のクイックドライブ設定（__QuickDrive_Main: 車・アシスト・天気）をそのまま使い、コース／時刻／モードだけ上書きして
//   QuickDrive.RunAsync で直接 AC を起動する（クイックドライブ画面は経由しない）。
//   起動前に Documents\Assetto Corsa\cfg\gt_start.ini を書き、CSP 側の GtStart アプリ（準備OK→3カウント→ローリングスタート）へ合図する。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AcManager.Pages.Drive;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using AcTools.DataFile;
using AcTools.Utils;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows;
using FirstFloor.ModernUI.Windows.Navigation;

namespace AcManager.Pages.Gt {
    public partial class CourseEntry : IParametrizedUriContent, IContent {
        private static readonly Brush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xB4, 0x74)));
        private static readonly Brush Ivory = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xE1)));
        private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xE8, 0xE6, 0xE1)));
        private static readonly Brush CardBg = Freeze(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush CardBgHover = Freeze(new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush CardBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x12, 0x16)));
        // [fork] GT7 のフォーカス表現＝「白反転」。パッドの十字移動でもマウスホバーでも同じ見た目にする（要望 2026-09-04
        //   「フォーカスしても隣と見分けがつかない」）。以前は opacity 0.82 程度しか変わらず、実機では判別できなかった。
        private static readonly Brush HotFill = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush HotInk = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));
        private static readonly Brush HotSubInk = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x11, 0x11, 0x11)));
        private static readonly FontFamily HeadFont = new FontFamily("Bahnschrift Light SemiCondensed, Yu Gothic UI Light");
        private static readonly FontFamily NumFont = new FontFamily("Bahnschrift SemiCondensed, Yu Gothic UI");
        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        private enum Step { Mode, Layout, Time, Go }
        private enum DriveMode { TimeTrial, Race }

        private string _trackId, _regionKey;
        private TrackObject _track;
        private List<TrackObjectBase> _layouts = new List<TrackObjectBase>();
        private TrackObjectBase _layout;
        // [fork] 2026-09-20 「レイアウト＝出発地点」型のコース（首都高 SRP）だけ非 null。見せ方だけを変える（GtStartPoints.cs）
        private GtStartPoints.Plan _startPlan;
        private string LayoutStepLabel => _startPlan?.StepTitle ?? "レイアウト";
        private DriveMode? _mode;
        private int _timeMinutes = 12 * 60;
        private Step _step = Step.Mode;
        private bool _launching;
        // [fork] 一発コースイン（ワールドマップの「もう一度走る」から来た時）
        private bool _autoLaunch;
        private bool _pageReady;
        private string _paramLayoutId, _paramMode;
        private int? _paramTime;
        private string _paramStartMode;
        private string _timeTrialStartMode = "rolling";
        private Action _refreshStartMode;
        private const string StartModeKey = "__CourseEntry_TimeTrialStartMode";

        internal static string NormalizeStartMode(string value) {
            return value == "pit" || value == "grid" ? value : "rolling";
        }
        private readonly List<Step> _visibleSteps = new List<Step>();

        // [fork] パッド（GtPad→GtFocus）用のフォーカス候補。ステップを組み立てるたびに作り直し、ShowStep() の末尾で Register する。
        private List<GtFocusItem> _focus = new List<GtFocusItem>();
        private GtFocusLayout _focusLayout = GtFocusLayout.Row;
        private int _focusColumns;
        private int _focusInitial;
        private bool _gone;   // Unloaded 済み（async の戻りで登録し直さないため）

        public CourseEntry() {
            InitializeComponent();
        }

        public void OnUri(Uri uri) {
            _trackId = uri.GetQueryParam("Track");
            _regionKey = uri.GetQueryParam("Region") ?? "eu";
            // [fork] Auto=1 で来たら、モード/レイアウト/時刻まで決め打ちで受け取り、画面を出さずにそのまま発射する
            _paramLayoutId = uri.GetQueryParam("Layout");
            _paramMode = uri.GetQueryParam("Mode");
            _paramStartMode = uri.GetQueryParam("Start");
            int t;
            _paramTime = null;
            var timeParam = uri.GetQueryParam("Time");
            if (!string.IsNullOrEmpty(timeParam) && int.TryParse(timeParam, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)
                    && t >= 0 && t < 24 * 60) _paramTime = t;
            _autoLaunch = uri.GetQueryParam("Auto") == "1";
        }

        public void OnNavigatedTo(NavigationEventArgs e) {
            // Cached pages do not pass through the content loader (and thus OnUri) again.
            // Only a new explicit navigation may rearm the one-shot course entry command.
            OnUri(e.Source);
            _autoLaunch = _autoLaunch && e.NavigationType == NavigationType.New && !_launching
                    && !GtInGameSettings.IsRestoringPage && !GtInGameSettings.IsSessionActive;
            TryAutoLaunch(); // Same-page navigation can reuse Content without another Loaded event.
        }

        public void OnNavigatedFrom(NavigationEventArgs e) { }
        public void OnNavigatingFrom(NavigatingCancelEventArgs e) { }
        public void OnFragmentNavigation(FragmentNavigationEventArgs e) { }

        private bool TryAutoLaunch() {
            if (!_autoLaunch || !_pageReady || !IsLoaded || _track == null || _launching
                    || GtInGameSettings.IsRestoringPage || GtInGameSettings.IsSessionActive) return false;
            _autoLaunch = false; // Consume before Launch sets its in-progress guard.
            _layout = _layouts.FirstOrDefault() ?? _track;
            if (!string.IsNullOrEmpty(_paramLayoutId)) {
                var layout = _layouts.FirstOrDefault(x => string.Equals(x.LayoutId ?? "", _paramLayoutId, StringComparison.OrdinalIgnoreCase));
                if (layout != null) _layout = layout;
            }
            _mode = _paramMode == "race" ? DriveMode.Race : DriveMode.TimeTrial;
            if (_paramTime.HasValue) _timeMinutes = _paramTime.Value;
            if (_paramStartMode != null) _timeTrialStartMode = NormalizeStartMode(_paramStartMode);
            ShowStep(Step.Go, 0);
            Launch();
            return true;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e) {
            _gone = false;
            GtTestChannel.ActiveCourseEntry = this;
            GtChrome.Enter();
            // Returning from CM7 settings must preserve the in-progress session and its launch task.
            if (_launching) {
                _autoLaunch = false;
                RegisterStepFocus();
                return;
            }
            _pageReady = false;
            SetHideMainMenu(false);   // 前回 CM が途中で落ちていても残骸を掃除
            BottomBar.Children.Clear();
            // GT7に無い下部ナビは廃止(要望 2026-08-25)。戻る=Esc
            BottomBar.Visibility = Visibility.Collapsed;

            Root.Opacity = 0;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SineEase() });
            var zoom = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(1400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            BackdropZoom.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            BackdropZoom.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
            Focus();

            try {
                await TracksManager.Instance.EnsureLoadedAsync();
                if (_launching || _gone || !IsLoaded) return;
                _track = (_trackId == null ? null : TracksManager.Instance.GetById(_trackId)) ?? TracksManager.Instance.GetDefault();
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] track: " + ex.Message);
            }
            if (_track == null) { WorldMap.Navigate("/Pages/Gt/WorldCircuits.xaml"); return; }

            // GT7 はコース名にレイアウト数の「(4)」を付けない
            TrackTitle.Text = GtNames.TrackName(_track);   // [fork] 日本語名（GtNames）。レイアウト数の「(4)」は付けない
            var logoPath = GtLoadingWindow.FindTrackLogo(_track.Id);
            if (logoPath != null) {
                TrackLogo.Source = WorldMap.LoadBitmap(logoPath, 520);
                TrackLogo.Visibility = Visibility.Visible;
                // [fork] ロゴがあるコースはロゴだけ（2026-09-27 要望「ここのところ、ロゴだけにしてほしい」＝ロゴの下のコース名の文字を出さない）
                TrackTitle.Visibility = Visibility.Collapsed;
            }
            Backdrop.Source = LoadTrackPhoto(_track);

            _layouts = _track.MultiLayouts != null && _track.MultiLayouts.Count > 0 ? _track.MultiLayouts.ToList() : new List<TrackObjectBase> { _track };
            // [fork] 出発地点型のコースは、表の順（出発する PA → AI と走る → そのほか）に並べ替える。表に無い物は末尾
            _startPlan = _layouts.Count > 1 ? GtStartPoints.For(_track.Id) : null;
            if (_startPlan != null) _layouts = _layouts.OrderBy(l => _startPlan.OrderOf(l.LayoutId)).ToList();
            _layout = _layouts[0];
            // [fork] 前回このコースで走ったレイアウト（出発地点）から始める。毎回先頭に戻ると、首都高で選び直しになる
            if (_layouts.Count > 1) {
                var lastLayout = ValuesStorage.Get<string>(LastLayoutKeyPrefix + _track.Id);
                var remembered = string.IsNullOrEmpty(lastLayout) ? null
                        : _layouts.FirstOrDefault(l => string.Equals(l.LayoutId ?? "", lastLayout, StringComparison.OrdinalIgnoreCase));
                if (remembered != null) _layout = remembered;
            }
            _visibleSteps.Clear();
            _visibleSteps.Add(Step.Mode);
            if (_layouts.Count > 1) _visibleSteps.Add(Step.Layout);
            _visibleSteps.Add(Step.Time);
            _visibleSteps.Add(Step.Go);   // [fork] GT7の段構え: 時刻→クイックメニュー(最終確認)→スタート

            var savedTime = ReadSavedTimeMinutes();
            if (savedTime.HasValue) _timeMinutes = savedTime.Value;
            _timeTrialStartMode = NormalizeStartMode(ValuesStorage.Get<string>(StartModeKey));
            UpdateTint(false);
            _pageReady = true;

            // [fork] 一発コースイン: 条件が全部渡ってきているなら段階UIを出さずに発射する。
            //   走り終わったら通常どおりこのコースのモード選択に戻るので、行き先としては普通のコースイン画面と同じ。
            if (TryAutoLaunch()) return;

            ShowStep(_visibleSteps[0], 0);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            if (ReferenceEquals(GtTestChannel.ActiveCourseEntry, this)) GtTestChannel.ActiveCourseEntry = null;
            _gone = true;
            GtFocus.Clear(this);   // [fork] パッドのフォーカス候補を手放す
            GtChrome.Leave();
        }

        #region Pad focus (GtFocus)
        /// <summary>[fork] MainWindow 基準の中心座標。まだ配置されていない要素は (0,0)（GtFocus 側で候補から外れる）。</summary>
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

        /// <summary>[fork] コースイン処理中（_launching）はパッドの決定を全部捨てる。既存のクリック処理をそのまま呼ぶ。</summary>
        private Action PadGuard(Action a) {
            return () => {
                if (_launching || a == null) return;
                a();
            };
        }

        /// <summary>[fork] このページの候補を登録する。
        /// ★ページを出た後（async の戻りなど）は登録しない。次のページが入れた候補を奪ってしまうため。</summary>
        private void RegisterFocus(IList<GtFocusItem> items, GtFocusLayout layout, int columns = 0, int initial = 0) {
            if (_gone) return;
            GtFocus.Register(this, items, layout, columns, initial);
            GtFocus.SetPageCommand(this, PadPageCommand);   // [fork] □＝セッティング開閉（全ステップ共通）
        }

        /// <summary>[fork] どの候補に居ても効くページ共通のパッド操作。□＝セッティングシート開閉（キーボードの S と同じ）。
        /// ★コースイン中（_launching）は消費だけして何もしない。</summary>
        private bool PadPageCommand(GtPadCommand cmd) {
            if (cmd != GtPadCommand.Square) return false;
            if (_launching) return true;
            ToggleSettingDialog();
            return true;
        }

        /// <summary>[fork] 今のステップの候補を登録し直す（オーバーレイを閉じた後など）。</summary>
        private void RegisterStepFocus() {
            RegisterFocus(_focus, _focusLayout, _focusColumns, _focusInitial);
        }

        /// <summary>[fork] オーバーレイを開いた瞬間〜中身が組み上がるまでの「フタ」。
        /// ★これが無いと、await 中は裏（Go ステップ）の候補が生きたままなので、
        ///   セッティングを開いた直後の × が「スタート」＝コースインになる。
        ///   空リストで Register すると GtFocus が受けず Enter 合成に落ちるので、必ず 1 件入れる。</summary>
        private void RegisterBlockingFocus(string name) {
            RegisterFocus(new List<GtFocusItem> {
                new GtFocusItem { Name = name, Center = () => new Point(0, 0), SetHot = null, Activate = null, Custom = c => true }
            }, GtFocusLayout.Column);
        }
        #endregion

        #region Steps
        private static string StepTitle(Step s) {
            switch (s) {
                case Step.Mode: return "モード";
                case Step.Layout: return "レイアウト";
                case Step.Time: return "時刻";
                default: return "コースイン";
            }
        }

        private string StepValue(Step s) {
            switch (s) {
                case Step.Mode: return _mode == null ? "" : _mode == DriveMode.TimeTrial ? "タイムトライアル" : "レース";
                case Step.Layout: return _layout == null ? "" : ShortLayoutName(_layout);
                case Step.Time: return FormatTime(_timeMinutes);
                default: return "";
            }
        }

        // [fork] GT7 ref_3化: 左のステップ一覧は持たない。中央3段目=選択済みモード名・最下段中央=現在のステップ名
        private void RebuildStepList() {
            TrackMode.Text = _mode == null ? "" : _mode == DriveMode.TimeTrial ? "タイムトライアル" : "レース";
            switch (_step) {
                case Step.Mode: StepFootLabel.Text = ""; break;   // モードステップはピルの上にラベルを出す(ref_4)ので下は空
                case Step.Layout: StepFootLabel.Text = _startPlan?.FootLabel ?? "レイアウト選択"; break;
                case Step.Time: StepFootLabel.Text = "時刻選択"; break;
                default: StepFootLabel.Text = ""; break;   // Goステップはクイックメニューのボタン列を出すので下ラベルは空
            }
        }

        private void ShowStep(Step step, int direction) {
            _step = step;
            // [fork] 要素を作り直すのでパッドの候補も作り直す。Build*Step() が _focus に足していき、末尾で Register する
            _focus = new List<GtFocusItem>();
            _focusLayout = GtFocusLayout.Row;
            _focusColumns = 0;
            _focusInitial = 0;
            _refreshStartMode = null;
            RebuildStepList();
            StepContent.Children.Clear();
            if (step != Step.Go) { BottomBar.Visibility = Visibility.Collapsed; BottomBar.Children.Clear(); }   // クイックメニューはGoステップ専用
            switch (step) {
                case Step.Mode:
                    StepContent.VerticalAlignment = VerticalAlignment.Center; StepContent.HorizontalAlignment = HorizontalAlignment.Center;
                    BuildModeStep(); GtHints.Bind(HintRight, "クリックで選択");
                    // [fork] 上段 [タイムトライアル][レース]／下段 [乗り換え][もう一度走る] の 2 段なので、見た目の位置で上下左右に動かす
                    //   （Row のままだと上下が効かなかった。2026-09-27 要望「モード選択画面で、ハンコンの Dパッド上下が使えない。左右は使える」）
                    //   ★Spatial のままだと、上段の端で左右を押すと「少し外側にある下段のピル」へ落ちた
                    //     （2026-10-04 要望「横押してるのに、なんか下に行ったりする」＝下段は上段より幅が広く中心が外へはみ出す）
                    //     → 左右は同じ段の中だけ・上下は従来どおり、の Rows にする。
                    _focusLayout = GtFocusLayout.Rows;
                    break;
                case Step.Layout:
                    StepContent.VerticalAlignment = VerticalAlignment.Bottom; StepContent.HorizontalAlignment = HorizontalAlignment.Center;
                    BuildLayoutStep(); GtHints.Bind(HintRight, "クリックで選択"); break;
                case Step.Time:
                    StepContent.VerticalAlignment = VerticalAlignment.Center; StepContent.HorizontalAlignment = HorizontalAlignment.Center;
                    // [fork] パッドでは「クリックで次へ」も「Enter この時刻で」も同じ × なので、機械置換だと × が 2 回出る。
                    //   パッド用の文面だけ別に渡す（padHint もキーボード表記＝confirm=circle の入替に追従する）。
                    BuildTimeStep();
                    GtHints.Bind(HintRight, "時刻をクリックで次へ   ← →  30分   Enter  この時刻で", "← →  30分   Enter  この時刻で");
                    break;
                default:
                    StepContent.VerticalAlignment = VerticalAlignment.Center; StepContent.HorizontalAlignment = HorizontalAlignment.Center;
                    BuildGoStep(); GtHints.Bind(HintRight, "↑ ↓ ← →  選択   Enter  決定", "Enter  決定"); break;
            }
            // [fork] モード選択の段だけ「C 乗り換え」を足す。パッドに C は無い（ピルを選んで ×）ので、パッド用の文面には入れない
            var leftHint = (_visibleSteps.IndexOf(step) == 0 ? "Esc  地域マップへ戻る" : "Esc  前のステップへ") + "   S  セッティング";
            GtHints.Bind(HintLeft, step == Step.Mode ? leftHint + "   C  乗り換え" : leftHint, leftHint);
            // スライドイン
            StepContent.Opacity = 0;
            StepSlide.X = direction >= 0 ? 60 : -60;
            StepContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new SineEase() });
            StepSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            RegisterStepFocus();   // [fork] パッド: このステップの候補に差し替える
        }

        private void Next() {
            var idx = _visibleSteps.IndexOf(_step);
            if (idx < _visibleSteps.Count - 1) ShowStep(_visibleSteps[idx + 1], 1);
        }

        private void Back() {
            var idx = _visibleSteps.IndexOf(_step);
            if (idx <= 0) {
                WorldMap.Navigate("/Pages/Gt/RegionMap.xaml?Region=" + Uri.EscapeDataString(_regionKey ?? "eu"));
            } else {
                ShowStep(_visibleSteps[idx - 1], -1);
            }
        }
        #endregion

        #region Step: mode
        // [fork] GT7 ref_4(敵車AI選択)の様式: ラベル1行＋大きな角丸ピル2つ。選択中=明るいグレー塗り+濃文字、非選択=半透明の暗い塗り+白文字
        private static readonly Brush PillOn = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE4, 0xE6)));
        private static readonly Brush PillOff = Freeze(new SolidColorBrush(Color.FromArgb(0x6E, 0x12, 0x16, 0x1C)));
        private static readonly Brush PillEdge = Freeze(new SolidColorBrush(Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF)));

        private void BuildModeStep() {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock {
                Text = "モード選択", FontFamily = NumFont, FontSize = 14, Foreground = Ivory, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.9 }
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Action<bool> hotTt, hotRace;
            var ttPill = ModePill("⏱", "タイムトライアル", _mode == DriveMode.TimeTrial, () => { _mode = DriveMode.TimeTrial; Next(); }, out hotTt);
            var racePill = ModePill("🏁", "レース", _mode == DriveMode.Race, () => { _mode = DriveMode.Race; Next(); }, out hotRace);
            row.Children.Add(ttPill);
            row.Children.Add(racePill);
            panel.Children.Add(row);
            // [fork] パッド: 「タイムトライアル / レース ( / もう一度走る )」の横 1 列
            _focus.Add(new GtFocusItem {
                Name = "タイムトライアル", Center = () => PadCenter(ttPill), SetHot = hotTt,
                Activate = PadGuard(() => { _mode = DriveMode.TimeTrial; Next(); })
            });
            _focus.Add(new GtFocusItem {
                Name = "レース", Center = () => PadCenter(racePill), SetHot = hotRace,
                Activate = PadGuard(() => { _mode = DriveMode.Race; Next(); })
            });
            _focusInitial = _mode == DriveMode.Race ? 1 : 0;
            // [fork] 2026-09-19 要望「モード選択のところで車を乗り換えられるように」。下段 = [乗り換え] [もう一度走る] の横並び
            var extras = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            BuildModeChangeCar(extras);
            BuildModeQuickDrive(extras);   // [fork] 「もう一度走る」
            panel.Children.Add(extras);
            StepContent.Children.Add(panel);
        }

        /// <summary>[fork] モード選択の下段に「乗り換え」を出す（2026-09-19）。
        /// ガレージと同じ GT7 の乗り換え画面(GarageCarPicker)を開き、選んだ車を普段のクイックドライブ設定へ入れる
        /// ＝ヘッダーの車表示・セッティング・この後のコースインが全部その車になる。C キーでも開く。</summary>
        private void BuildModeChangeCar(Panel host) {
            var car = WorldMap.FindCurrentCar();
            var idle = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x12, 0x16)));
            var hover = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x20, 0x26)));
            var edge = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xE8, 0xE6, 0xE1)));

            var pill = new Border {
                CornerRadius = new CornerRadius(24), Background = idle,
                BorderThickness = new Thickness(1.5), BorderBrush = edge,
                Padding = new Thickness(10, 7, 28, 8), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 34, 16, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 },
                ToolTip = "乗る車を選び直します（C）"
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            // 今の車の写真（ヘッダーと同じスキンのプレビュー）。角丸の窓に収める
            var photo = new Border {
                Width = 64, Height = 36, CornerRadius = new CornerRadius(18), ClipToBounds = true, Margin = new Thickness(0, 0, 13, 0),
                VerticalAlignment = VerticalAlignment.Center, Background = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)))
            };
            try {
                var src = car?.SelectedSkin?.PreviewImage;
                if (!string.IsNullOrEmpty(src) && File.Exists(src)) {
                    photo.Background = new ImageBrush(WorldMap.LoadBitmap(src, 220)) { Stretch = Stretch.UniformToFill };
                }
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] car photo: " + ex.Message);
            }
            row.Children.Add(photo);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = "乗り換え", FontSize = 16, Foreground = Ivory, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock {
                Text = car?.DisplayName ?? "車が選ばれていません", FontFamily = NumFont, FontSize = 11, Foreground = Gold, Opacity = 0.9,
                Margin = new Thickness(0, 1, 0, 0), MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(text);
            pill.Child = row;

            Action<bool> hot = on => {
                pill.Background = on ? hover : idle;
                pill.BorderBrush = on ? Brushes.White : edge;
            };
            pill.MouseEnter += (s2, e2) => hot(true);
            pill.MouseLeave += (s2, e2) => hot(false);
            pill.MouseLeftButtonUp += (s2, e2) => ChangeCarFromModeStep();

            host.Children.Add(pill);
            _focus.Add(new GtFocusItem {
                Name = "乗り換え", Center = () => PadCenter(pill), SetHot = hot,
                Activate = PadGuard(ChangeCarFromModeStep)
            });
            // 乗り換え画面から戻ってきた時は、パッドのフォーカスをこのピルに戻す（タイムトライアルへ飛ばさない）
            if (_focusChangeCar) { _focusInitial = _focus.Count - 1; _focusChangeCar = false; }
        }

        private bool _focusChangeCar;

        /// <summary>[fork] 乗り換え画面を開く。選んでもやめても同じモード選択へ戻る（設定/ブランドセントラルへ出た時だけ離れる）。</summary>
        private void ChangeCarFromModeStep() {
            if (_launching || _step != Step.Mode) return;
            if (RaceOverlay.Visibility == Visibility.Visible || SettingOverlay.Visibility == Visibility.Visible) return;
            var current = WorldMap.FindCurrentCar();
            bool leave;
            var picked = PickCar(current, out leave);
            if (leave || !IsLoaded) return;
            if (picked != null && !string.Equals(picked.Id, current?.Id, StringComparison.OrdinalIgnoreCase)) {
                SetPresetCar(picked);
                try { HeaderBar.Refresh(); } catch (Exception ex) { Logging.Warning("[CourseEntry] header refresh: " + ex.Message); }
            }
            _focusChangeCar = true;
            ShowStep(Step.Mode, 0);   // 車名と写真を作り直す（やめた時もパッドのフォーカスを戻す）
        }

        /// <summary>[fork] モード選択の下に「もう一度走る」を出す。
        /// 要望 2026-08-29「AC を終了するとこの画面に戻るので、ここにもう一度走るボタンが欲しい」。
        /// ワールドマップと同じピル・同じ一発コースイン経路（?...&amp;Auto=1）を使う。
        /// 一度も走っていなければ出さない。</summary>
        private void BuildModeQuickDrive(StackPanel panel) {
            var d = ReadLastDrive();
            if (d == null) return;

            var idle = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x12, 0x16)));
            var hover = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x20, 0x26)));
            var edge = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xE8, 0xE6, 0xE1)));

            var pill = new Border {
                CornerRadius = new CornerRadius(24), Background = idle,
                BorderThickness = new Thickness(1.5), BorderBrush = edge,
                Padding = new Thickness(24, 7, 28, 8), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 34, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 },
                ToolTip = "前回と同じ条件でそのままコースインします"
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock {
                Text = "🏁", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 18, Foreground = Ivory,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 13, 0)
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = "もう一度走る", FontSize = 16, Foreground = Ivory, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock {
                Text = d.Title + (string.IsNullOrEmpty(d.LayoutTitle) ? "" : " " + d.LayoutTitle)
                        + "   " + d.ModeTitle + "   "
                        + (d.TimeMinutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":"
                        + (d.TimeMinutes % 60).ToString("00", CultureInfo.InvariantCulture),
                FontFamily = NumFont, FontSize = 11, Foreground = Gold, Opacity = 0.9, Margin = new Thickness(0, 1, 0, 0)
            });
            row.Children.Add(text);
            pill.Child = row;

            // [fork] ホバー表現を SetHot に切り出して、マウスとパッドの両方から呼ぶ
            Action<bool> hot = on => {
                pill.Background = on ? hover : idle;
                pill.BorderBrush = on ? Brushes.White : edge;
            };
            pill.MouseEnter += (s2, e2) => hot(true);
            pill.MouseLeave += (s2, e2) => hot(false);
            pill.MouseLeftButtonUp += (s2, e2) => WorldMap.Navigate(LastDriveUri(d));

            panel.Children.Add(pill);
            _focus.Add(new GtFocusItem {
                Name = "もう一度走る", Center = () => PadCenter(pill), SetHot = hot,
                Activate = PadGuard(() => WorldMap.Navigate(LastDriveUri(d)))
            });
        }

        /// <summary>[fork] setHot = ホバーと同じ見た目を作る関数（パッドのフォーカス表現に共用）。
        /// ★2026-09-04: 「今フォーカスしている方」を GT7 と同じ白反転（白塗り＋黒文字＋白 2px 枠＋白いにじみ）にする。
        ///   選択中ピルの表現（PillOn＋白 1px 枠＋濃文字）はそのまま残し、Hot はその上から必ず勝つ
        ///   ＝選択中のピルにフォーカスしても見た目が変わるので、隣と見分けがつく。
        ///   Width/Height 固定なので枠を 1→2px にしても外寸は動かない（ボタン列がガタつかない）。</summary>
        private static FrameworkElement ModePill(string glyph, string title, bool selected, Action go, out Action<bool> setHot) {
            var b = new Border {
                Width = 320, Height = 64, Margin = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = selected ? PillOn : PillOff,
                BorderBrush = selected ? Brushes.White : PillEdge, BorderThickness = new Thickness(1)
            };
            var s = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var glyphText = new TextBlock {
                Text = glyph, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"), FontSize = 21,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Foreground = selected ? Ink : Ivory
            };
            var titleText = new TextBlock { Text = title, FontFamily = HeadFont, FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Foreground = selected ? Ink : Ivory };
            s.Children.Add(glyphText);
            s.Children.Add(titleText);
            b.Child = s;
            // 元の見た目（＝Hot を解いた時に必ず戻す先）。selected かどうかで決まる
            var coldBg = selected ? PillOn : PillOff;
            var coldEdge = selected ? Brushes.White : PillEdge;
            var coldInk = selected ? Ink : Ivory;
            Action<bool> hot = on => {
                b.Background = on ? HotFill : coldBg;
                b.BorderBrush = on ? Brushes.White : coldEdge;
                b.BorderThickness = new Thickness(on ? 2 : 1);
                glyphText.Foreground = on ? HotInk : coldInk;
                titleText.Foreground = on ? HotInk : coldInk;
                b.Effect = on ? new System.Windows.Media.Effects.DropShadowEffect {
                    BlurRadius = 20, ShadowDepth = 0, Color = Colors.White, Opacity = 0.6
                } : null;
            };
            setHot = hot;
            b.MouseEnter += (o, e) => hot(true);
            b.MouseLeave += (o, e) => hot(false);
            b.MouseLeftButtonUp += (o, e) => { e.Handled = true; go(); };
            return b;
        }
        #endregion

        #region Step: layout
        // [fork] GT7 ref_3(レイアウト選択)の様式: 画面下1/3に半透明の暗いカード横並び・白い線画マップ・
        //   レイアウト名＋「コース長 N,NNN m」・選択中/ホバーだけ明るく＋白1px枠
        private static readonly Brush LayoutCardOff = Freeze(new SolidColorBrush(Color.FromArgb(0x5C, 0x07, 0x0B, 0x12)));
        private static readonly Brush LayoutCardOn = Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0x3A, 0x42, 0x4E)));
        private static readonly Brush LayoutCardEdge = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
        // [fork] 2026-09-04: フォーカス中カード＝白 2.5px 枠＋1.03 倍＋さらに明るい塗り。
        //   「前回選択したカード」は白枠→薄いグレー枠(1.5px)に落として、フォーカス枠と紛れないようにする。
        private static readonly Brush LayoutCardHot = Freeze(new SolidColorBrush(Color.FromArgb(0xB4, 0x5A, 0x64, 0x72)));
        private static readonly Brush LayoutCardSelEdge = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));

        // 出発地点のカードは文字だけ＝背景の写真（看板の文字など）が透けると読めない → コース図つきのカードより背板を濃くする
        //   （0xC8=78% でも白い看板は透けて見えた → 0xE8=91%）
        private static readonly Brush StartCardOff = Freeze(new SolidColorBrush(Color.FromArgb(0xE8, 0x0B, 0x0F, 0x16)));
        private static readonly Brush StartCardOn = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x3A, 0x42, 0x4E)));
        private static readonly Brush StartCardHot = Freeze(new SolidColorBrush(Color.FromArgb(0xF4, 0x5A, 0x64, 0x72)));

        /// <summary>[fork] 2026-09-20 出発地点型のコース（首都高 SRP）: 見出しごとに、文字だけの小さいカードを並べる。
        /// コース図は全部同じ首都高の全線で見分けにならないので載せない。コース長(全部 165km)も出さない。
        /// 見出しごとに 1 行の枚数が違うので、パッドの移動は位置で決める（Spatial）。</summary>
        private static readonly Brush StartPinPa = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0x45, 0x3C)));
        private static readonly Brush StartPinAi = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xB4, 0x74)));
        private static readonly Brush StartLine = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0xE8, 0xE6, 0xE1)));
        private static readonly Brush StartPanel = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x08, 0x0B, 0x11)));
        private static readonly Brush StartMinorBar = Freeze(new SolidColorBrush(Color.FromRgb(0x6A, 0x70, 0x7A)));

        private sealed class StartCard {
            public TrackObjectBase Layout;
            public GtStartPoints.Entry Entry;
            public bool Left, HasPin;
            public double Px, Py, Y, H;
        }

        /// <summary>[fork] 2026-09-21 出発地点を「地図＋ピン＋左右のカード」で見せる（要望「出発場所の名前だと分かりにくい。
        /// 首都高のでかい地図をバンと載っけて、それぞれの出発地点を添える感じがいい」）。
        /// 地図の絵（Data\Static\gt7\maps\）が無ければ false ＝呼び出し側が見出しつきカードの並びで出す。
        /// 論理サイズ 1660x640 の Canvas に描いて Viewbox で縮める。カードはピンの高さに合わせて左右の列に並べ、重なったら下へ送る。</summary>
        private bool BuildStartPointMap() {
            var mapPath = _startPlan.FindMap();
            var mapImage = mapPath == null ? null : WorldMap.LoadBitmap(mapPath, 1400);
            if (mapImage == null) return false;

            const double W = 1660, H = 640, M = 616, Pad = 12, CardW = 310, Gap = 8, Reach = 36;
            var mapX = (W - M) / 2;
            var canvas = new Canvas { Width = W, Height = H };
            canvas.Children.Add(new Border { Width = W, Height = H, CornerRadius = new CornerRadius(10), Background = StartPanel });
            var img = new Image { Width = M, Height = M, Source = mapImage, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(img, mapX);
            Canvas.SetTop(img, Pad);
            canvas.Children.Add(img);

            var lastGroup = _startPlan.GroupTitles.Length - 1;
            var cards = _layouts.Select(l => {
                var e = _startPlan.Find(l.LayoutId);
                var c = new StartCard { Layout = l, Entry = e, HasPin = e != null, H = e == null || e.Group == lastGroup ? 46 : 58 };
                if (e != null) {
                    double nx, ny;
                    _startPlan.ToMap(e, out nx, out ny);
                    c.Px = mapX + nx * M;
                    c.Py = Pad + ny * M;
                    c.Left = nx < 0.55;
                } else {
                    c.Py = H;   // 表に無いレイアウトは右の列の一番下（ピンなし）
                }
                return c;
            }).ToList();

            foreach (var side in cards.GroupBy(c => c.Left)) {
                var column = side.OrderBy(c => c.Py).ThenBy(c => _layouts.IndexOf(c.Layout)).ToList();
                var y = Pad;
                foreach (var c in column) {
                    c.Y = Math.Max(y, c.Py - c.H / 2);
                    y = c.Y + c.H + Gap;
                }
                var limit = H - Pad;   // 下にはみ出したぶんは、下から順に上へ詰める
                for (var i = column.Count - 1; i >= 0; i--) {
                    column[i].Y = Math.Min(column[i].Y, limit - column[i].H);
                    limit = column[i].Y - Gap;
                }
            }

            // 重ね順: 地図 → 引き出し線 → ピン → カード
            var lines = new Dictionary<StartCard, System.Windows.Shapes.Polyline>();
            foreach (var c in cards.Where(x => x.HasPin)) {
                var edge = c.Left ? mapX - Reach : mapX + M + Reach;
                var line = new System.Windows.Shapes.Polyline { Stroke = StartLine, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
                line.Points.Add(new Point(edge, c.Y + c.H / 2));
                line.Points.Add(new Point(edge + (c.Left ? 20 : -20), c.Y + c.H / 2));
                line.Points.Add(new Point(c.Px, c.Py));
                canvas.Children.Add(line);
                lines[c] = line;
            }

            // 近いピン（8px 以内）は 1 個にまとめる。全体図だと平和島PA の上り/下りは 2px しか離れない
            var pinKey = new Dictionary<StartCard, string>();
            foreach (var c in cards.Where(x => x.HasPin)) {
                var near = pinKey.Keys.FirstOrDefault(o => Math.Abs(o.Px - c.Px) < 8 && Math.Abs(o.Py - c.Py) < 8);
                pinKey[c] = near != null ? pinKey[near] : Math.Round(c.Px) + "," + Math.Round(c.Py);
            }
            var pins = new Dictionary<string, Ellipse>();
            var pinOwner = new Dictionary<string, StartCard>();
            foreach (var c in cards.Where(x => x.HasPin)) {
                var key = pinKey[c];
                if (pins.ContainsKey(key)) continue;
                var here = cards.Where(x => x.HasPin && pinKey[x] == key).ToList();
                var pin = new Ellipse {
                    Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, Cursor = Cursors.Hand,
                    Fill = here.Any(x => x.Entry.Group != 1) ? StartPinPa : StartPinAi,
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
                };
                Canvas.SetLeft(pin, c.Px - 7);
                Canvas.SetTop(pin, c.Py - 7);
                canvas.Children.Add(pin);
                pins[key] = pin;
                pinOwner[key] = here[0];   // ピンをクリックしたら、その場所の先頭（出発する PA）を選ぶ
            }

            var hots = new Dictionary<StartCard, Action<bool>>();
            foreach (var c in cards) {
                var card = c;
                var layout = c.Layout;
                var selected = layout == _layout;
                var minor = c.H < 50;
                var b = new Border {
                    Width = CardW, Height = c.H, CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand,
                    Background = selected ? StartCardOn : StartCardOff,
                    BorderBrush = selected ? LayoutCardSelEdge : LayoutCardEdge, BorderThickness = new Thickness(selected ? 1.5 : 1),
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
                };
                var g = new Grid();
                g.Children.Add(new Border {   // 左端の色の帯: 赤=出発する PA / 金=AI と走る / 灰=そのほか
                    Width = 3, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(5, 0, 0, 5),
                    Background = c.Entry == null || c.Entry.Group == lastGroup ? StartMinorBar : c.Entry.Group == 1 ? StartPinAi : StartPinPa
                });
                var t = new StackPanel { Margin = new Thickness(16, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                t.Children.Add(new TextBlock { Text = ShortLayoutName(layout), FontFamily = HeadFont, FontSize = minor ? 14.5 : 18, Foreground = Ivory, TextTrimming = TextTrimming.CharacterEllipsis });
                var sub = c.Entry?.Sub ?? (layout.LayoutName ?? layout.DisplayName);
                if (!string.IsNullOrEmpty(sub)) {
                    t.Children.Add(new TextBlock { Text = sub, FontFamily = HeadFont, FontSize = 11.5, Foreground = Dim, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                }
                t.Opacity = selected ? 1 : 0.85;
                g.Children.Add(t);
                b.Child = g;
                Canvas.SetLeft(b, c.Left ? mapX - Reach - CardW : mapX + M + Reach);
                Canvas.SetTop(b, c.Y);

                var cardScale = (ScaleTransform)b.RenderTransform;
                System.Windows.Shapes.Polyline myLine;
                lines.TryGetValue(card, out myLine);
                Ellipse myPin = null;
                if (card.HasPin) pins.TryGetValue(pinKey[card], out myPin);
                Action<bool> hot = on => {
                    var sel = layout == _layout;
                    b.Background = on ? StartCardHot : sel ? StartCardOn : StartCardOff;
                    b.BorderBrush = on ? Brushes.White : sel ? LayoutCardSelEdge : LayoutCardEdge;
                    b.BorderThickness = new Thickness(on ? 2.5 : sel ? 1.5 : 1);
                    t.Opacity = on || sel ? 1 : 0.85;
                    Panel.SetZIndex(b, on ? 5 : 0);
                    var a = new DoubleAnimation(on ? 1.03 : 1, TimeSpan.FromMilliseconds(on ? 140 : 180)) { EasingFunction = new SineEase() };
                    cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                    cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                    // 地図の上でも「どこか」が分かるように、線とピンを光らせる
                    if (myLine != null) { myLine.Stroke = on || sel ? Brushes.White : StartLine; myLine.StrokeThickness = on ? 2 : sel ? 1.4 : 1; }
                    if (myPin != null) {
                        var p = new DoubleAnimation(on ? 1.7 : 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new SineEase() };
                        ((ScaleTransform)myPin.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, p);
                        ((ScaleTransform)myPin.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, p);
                    }
                };
                if (selected && myLine != null) { myLine.Stroke = Brushes.White; myLine.StrokeThickness = 1.4; }
                hots[card] = hot;
                b.MouseEnter += (o, e) => hot(true);
                b.MouseLeave += (o, e) => hot(false);
                b.MouseLeftButtonUp += (o, e) => { e.Handled = true; _layout = layout; Next(); };
                canvas.Children.Add(b);
                if (selected) _focusInitial = _focus.Count;
                _focus.Add(new GtFocusItem {
                    Name = ShortLayoutName(layout), Center = () => PadCenter(b), SetHot = hot,
                    Activate = PadGuard(() => { _layout = layout; Next(); })
                });
            }

            foreach (var kv in pins) {
                var owner = pinOwner[kv.Key];
                kv.Value.MouseEnter += (o, e) => hots[owner](true);
                kv.Value.MouseLeave += (o, e) => hots[owner](false);
                kv.Value.MouseLeftButtonUp += (o, e) => { e.Handled = true; _layout = owner.Layout; Next(); };
            }

            // 凡例（右の列の下の空き。地図の中に置くと下端の路線に被る）
            var legend = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
            foreach (var item in new[] { Tuple.Create(StartPinPa, _startPlan.GroupTitles[0]), Tuple.Create(StartPinAi, _startPlan.GroupTitles[1]) }) {
                legend.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = item.Item1, Stroke = Brushes.White, StrokeThickness = 1.2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
                legend.Children.Add(new TextBlock { Text = item.Item2, FontFamily = HeadFont, FontSize = 11.5, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center });
            }
            Canvas.SetLeft(legend, mapX + M + Reach - 14);
            Canvas.SetTop(legend, H - Pad - 22);
            canvas.Children.Add(legend);

            _focusLayout = GtFocusLayout.Spatial;
            StepContent.Children.Add(new Viewbox {
                Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = W, Margin = new Thickness(0, 0, 0, 16), Child = canvas
            });
            return true;
        }

        private void BuildStartPointStep() {
            if (BuildStartPointMap()) return;
            var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 18) };
            var groups = _layouts.GroupBy(l => _startPlan.Find(l.LayoutId)?.Group ?? _startPlan.GroupTitles.Length - 1).OrderBy(x => x.Key);
            foreach (var group in groups) {
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 14, 0, 8) };
                head.Children.Add(new TextBlock { Text = _startPlan.GroupTitles[group.Key], FontFamily = HeadFont, FontSize = 17, Foreground = Ivory });
                var note = _startPlan.GroupNotes[group.Key];
                if (!string.IsNullOrEmpty(note)) {
                    head.Children.Add(new TextBlock { Text = note, FontFamily = HeadFont, FontSize = 12.5, Foreground = Dim, Margin = new Thickness(16, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom });
                }
                root.Children.Add(head);

                var wrap = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 1660 };
                foreach (var l in group) {
                    var layout = l;
                    var entry = _startPlan.Find(layout.LayoutId);
                    var selected = layout == _layout;
                    var minor = group.Key == _startPlan.GroupTitles.Length - 1;   // 「そのほか」は一段小さく
                    var b = new Border {
                        Width = minor ? 300 : 312, Height = minor ? 70 : 88, Margin = new Thickness(10, 0, 10, 10), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
                        Background = selected ? StartCardOn : StartCardOff,
                        BorderBrush = selected ? LayoutCardSelEdge : LayoutCardEdge, BorderThickness = new Thickness(selected ? 1.5 : 1),
                        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
                    };
                    var t = new StackPanel { Margin = new Thickness(18, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                    t.Children.Add(new TextBlock {
                        Text = ShortLayoutName(layout), FontFamily = HeadFont, FontSize = minor ? 16 : 20, Foreground = Ivory, TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    var sub = entry?.Sub ?? (layout.LayoutName ?? layout.DisplayName);
                    if (!string.IsNullOrEmpty(sub)) {
                        t.Children.Add(new TextBlock { Text = sub, FontFamily = HeadFont, FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                    }
                    t.Opacity = selected ? 1 : 0.85;
                    b.Child = t;
                    var cardScale = (ScaleTransform)b.RenderTransform;
                    Action<bool> hot = on => {
                        var sel = layout == _layout;
                        b.Background = on ? StartCardHot : sel ? StartCardOn : StartCardOff;
                        b.BorderBrush = on ? Brushes.White : sel ? LayoutCardSelEdge : LayoutCardEdge;
                        b.BorderThickness = new Thickness(on ? 2.5 : sel ? 1.5 : 1);
                        t.Opacity = on || sel ? 1 : 0.85;
                        Panel.SetZIndex(b, on ? 5 : 0);
                        var a = new DoubleAnimation(on ? 1.03 : 1, TimeSpan.FromMilliseconds(on ? 140 : 180)) { EasingFunction = new SineEase() };
                        cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                        cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                    };
                    b.MouseEnter += (o, e) => hot(true);
                    b.MouseLeave += (o, e) => hot(false);
                    b.MouseLeftButtonUp += (o, e) => { e.Handled = true; _layout = layout; Next(); };
                    wrap.Children.Add(b);
                    if (selected) _focusInitial = _focus.Count;
                    _focus.Add(new GtFocusItem {
                        Name = ShortLayoutName(layout), Center = () => PadCenter(b), SetHot = hot,
                        Activate = PadGuard(() => { _layout = layout; Next(); })
                    });
                }
                root.Children.Add(wrap);
            }
            _focusLayout = GtFocusLayout.Spatial;
            StepContent.Children.Add(root);
        }

        private void BuildLayoutStep() {
            if (_startPlan != null) { BuildStartPointStep(); return; }
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 1660 };
            var cards = new List<FrameworkElement>();   // [fork] 1 行あたり枚数の実測用
            foreach (var l in _layouts) {
                var layout = l;
                var selected = layout == _layout;
                var b = new Border {
                    Width = 300, Height = 286, Margin = new Thickness(10, 0, 10, 12), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
                    Background = selected ? LayoutCardOn : LayoutCardOff,
                    BorderBrush = selected ? LayoutCardSelEdge : LayoutCardEdge, BorderThickness = new Thickness(selected ? 1.5 : 1),
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
                };
                var g = new Grid();
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var img = new Image { Margin = new Thickness(28, 24, 28, 8), Stretch = Stretch.Uniform, Source = WorldMap.LoadBitmap(layout.OutlineImage, 440) ?? WorldMap.LoadBitmap(layout.PreviewImage, 440) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                g.Children.Add(img);
                var t = new StackPanel { Margin = new Thickness(14, 0, 14, 16), HorizontalAlignment = HorizontalAlignment.Center };
                t.Children.Add(new TextBlock {
                    Text = ShortLayoutName(layout), FontFamily = HeadFont, FontSize = 19, Foreground = Ivory,
                    TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center
                });
                var len = FormatLengthMeters(layout.SpecsLength);
                if (len != null) {
                    var lt = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), FontFamily = NumFont, FontSize = 12 };
                    lt.Inlines.Add(new System.Windows.Documents.Run("コース長  ") { Foreground = Dim });
                    lt.Inlines.Add(new System.Windows.Documents.Run(len) { Foreground = Brushes.White });
                    t.Children.Add(lt);
                }
                Grid.SetRow(t, 1);
                g.Children.Add(t);
                g.Opacity = selected ? 1 : 0.85;   // 非選択は中身も少し沈める(ref_3)
                b.Child = g;
                // [fork] ホバー表現を SetHot に切り出して、マウスとパッドの両方から呼ぶ
                // ★Hot を解いた時は「前回選択」かどうかで必ず元へ戻す（以前は選択中カードだけ戻さず、
                //   フォーカス表現と選択表現が同じ白枠のまま残って区別がつかなかった）。
                var cardScale = (ScaleTransform)b.RenderTransform;
                Action<bool> hot = on => {
                    var sel = layout == _layout;
                    b.Background = on ? LayoutCardHot : sel ? LayoutCardOn : LayoutCardOff;
                    b.BorderBrush = on ? Brushes.White : sel ? LayoutCardSelEdge : LayoutCardEdge;
                    b.BorderThickness = new Thickness(on ? 2.5 : sel ? 1.5 : 1);
                    g.Opacity = on || sel ? 1 : 0.85;
                    Panel.SetZIndex(b, on ? 5 : 0);   // 1.03 倍が隣のカードに隠れないように前へ
                    var a = new DoubleAnimation(on ? 1.03 : 1, TimeSpan.FromMilliseconds(on ? 140 : 180)) { EasingFunction = new SineEase() };
                    cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                    cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                };
                b.MouseEnter += (o, e) => hot(true);
                b.MouseLeave += (o, e) => hot(false);
                b.MouseLeftButtonUp += (o, e) => { e.Handled = true; _layout = layout; Next(); };
                wrap.Children.Add(b);
                cards.Add(b);
                if (selected) _focusInitial = _focus.Count;
                _focus.Add(new GtFocusItem {
                    Name = ShortLayoutName(layout), Center = () => PadCenter(b), SetHot = hot,
                    Activate = PadGuard(() => { _layout = layout; Next(); })
                });
            }
            StepContent.Children.Add(wrap);
            // [fork] WrapPanel の 1 行あたり枚数は配置後でないと分からない。Loaded 優先度で測って Grid レイアウトに切り替える
            Dispatcher.BeginInvoke(new Action(() => ApplyLayoutStepColumns(cards)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>[fork] レイアウトカードの「1 行あたり枚数」を実測してパッドの移動を Grid に切り替える。
        /// 測れない・折り返していない時は Row のまま（上下は無視される）。</summary>
        private void ApplyLayoutStepColumns(List<FrameworkElement> cards) {
            try {
                if (_step != Step.Layout || !IsLoaded || cards == null || cards.Count < 3) return;
                var y0 = PadCenter(cards[0]).Y;
                if (y0 <= 0) return;
                var cols = 0;
                for (var i = 0; i < cards.Count; i++) {
                    if (Math.Abs(PadCenter(cards[i]).Y - y0) > 4) break;
                    cols = i + 1;
                }
                if (cols < 2 || cols >= cards.Count) return;   // 折り返していない＝Row のままでよい
                _focusLayout = GtFocusLayout.Grid;
                _focusColumns = cols;
                RegisterStepFocus();
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] pad columns: " + ex.Message);
            }
        }

        /// <summary>GT7 表記「4,730 m」。数値化できなければ元の文字列。</summary>
        private static string FormatLengthMeters(string s) {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim().Replace(",", "");
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return m.ToString("N0", CultureInfo.InvariantCulture) + " m";
            return s;
        }
        #endregion

        #region Step: time
        private TextBlock _timeBig, _timeLabel;
        private Slider _timeSlider;
        private Canvas _sunCanvas;
        private Ellipse _sun;

        private static readonly (string label, int minutes)[] TimePresets = {
            ("早朝", 5 * 60 + 30), ("朝", 8 * 60), ("昼", 12 * 60), ("夕方", 17 * 60 + 30), ("夜", 20 * 60 + 30), ("深夜", 0),
        };

        private void BuildTimeStep() {
            // [fork] StepContent が中央寄せ(サイズ=内容依存)になったので幅は固定で与える
            var panel = new StackPanel { Width = 1000 };

            var clock = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            _timeBig = new TextBlock { Text = FormatTime(_timeMinutes), FontFamily = NumFont, FontSize = 72, Foreground = Ivory, VerticalAlignment = VerticalAlignment.Bottom };
            _timeLabel = new TextBlock { Text = TimeWord(_timeMinutes), FontFamily = HeadFont, FontSize = 22, Foreground = Gold, Margin = new Thickness(18, 0, 0, 14), VerticalAlignment = VerticalAlignment.Bottom };
            clock.Children.Add(_timeBig);
            clock.Children.Add(_timeLabel);
            panel.Children.Add(clock);

            // 空の帯（0〜24h のグラデーション）＋太陽
            var band = new Grid { Height = 64, Margin = new Thickness(0, 10, 0, 0) };
            var sky = new Border { CornerRadius = new CornerRadius(3), Background = SkyBand(), BorderBrush = CardBorder, BorderThickness = new Thickness(1) };
            band.Children.Add(sky);
            _sunCanvas = new Canvas { IsHitTestVisible = false };
            _sun = new Ellipse { Width = 14, Height = 14, Fill = Gold, Stroke = Brushes.White, StrokeThickness = 1.5 };
            _sun.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Color = Color.FromRgb(0xFF, 0xD8, 0x80), Opacity = 0.9 };
            _sunCanvas.Children.Add(_sun);
            band.Children.Add(_sunCanvas);
            _timeSlider = new Slider {
                Minimum = 0, Maximum = 24 * 60 - 1, Value = _timeMinutes, SmallChange = 30, LargeChange = 60, IsSnapToTickEnabled = true, TickFrequency = 5, IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), Opacity = 0.01, Cursor = Cursors.Hand
            };
            _timeSlider.ValueChanged += (o, e) => { SetTime((int)e.NewValue, false); };
            band.Children.Add(_timeSlider);
            band.SizeChanged += (o, e) => PositionSun();
            panel.Children.Add(band);

            var ticks = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (var h = 0; h <= 24; h += 6) {
                var tb = new TextBlock { Text = h.ToString("00") + ":00", FontFamily = NumFont, FontSize = 11, Foreground = Dim, HorizontalAlignment = h == 0 ? HorizontalAlignment.Left : h == 24 ? HorizontalAlignment.Right : HorizontalAlignment.Center };
                if (h > 0 && h < 24) tb.Margin = new Thickness(0, 0, 0, 0);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(h, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24 - h, GridUnitType.Star) });
                Grid.SetColumn(tb, 1);
                g.Children.Add(tb);
                ticks.Children.Add(g);
            }
            panel.Children.Add(ticks);

            // 大きなタイル: クリックで即コースイン
            var tiles = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 24, 0, 0) };
            // [fork] パッド: index 0 = スライダ行（左右で ±30 分・× で次へ・下でタイル列へ）、1〜 = 時刻タイル（上でスライダ行へ）
            _focus.Add(new GtFocusItem {
                Name = "時刻スライダ",
                Center = () => PadCenter(band),
                SetHot = on => sky.BorderBrush = on ? Brushes.White : CardBorder,
                Custom = TimeSliderCommand
            });
            foreach (var p in TimePresets) {
                var minutes = p.minutes;
                Action<bool> hot;
                var tile = TimeTile(p.label, minutes, () => { SetTime(minutes, true); Next(); }, out hot);
                tiles.Children.Add(tile);
                _focus.Add(new GtFocusItem {
                    Name = p.label, Center = () => PadCenter(tile), SetHot = hot,
                    Activate = PadGuard(() => { SetTime(minutes, true); Next(); }),
                    Custom = TimeTileCommand
                });
            }
            panel.Children.Add(tiles);

            StepContent.Children.Add(panel);
            Dispatcher.BeginInvoke(new Action(PositionSun), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>[fork] スライダ行のパッド操作: 左右で ±30 分・× で次へ・下でタイル列へ。</summary>
        private bool TimeSliderCommand(GtPadCommand cmd) {
            if (_launching) return true;
            switch (cmd) {
                case GtPadCommand.Left: SetTime(_timeMinutes - 30, true); GtSfx.PlayMove(); return true;
                case GtPadCommand.Right: SetTime(_timeMinutes + 30, true); GtSfx.PlayMove(); return true;
                case GtPadCommand.Confirm: GtSfx.PlaySelect(); Next(); return true;
                case GtPadCommand.Down:
                    if (_focus.Count > 1) { GtFocus.SetIndex(1); GtSfx.PlayMove(); }
                    return true;
                case GtPadCommand.Up: return true;   // 一番上。消費だけして端で止める
                default: return false;
            }
        }

        /// <summary>[fork] 時刻タイルのパッド操作: 上でスライダ行へ戻る（左右のタイル移動と × は GtFocus に任せる）。</summary>
        private bool TimeTileCommand(GtPadCommand cmd) {
            if (cmd == GtPadCommand.Up) {
                GtFocus.SetIndex(0);
                GtSfx.PlayMove();
                return true;
            }
            return cmd == GtPadCommand.Down;   // 一番下。消費だけして端で止める
        }

        /// <summary>時刻タイル（空の色＋大きな時刻）。クリックで即コースイン。
        /// [fork] setHot = ホバーと同じ見た目（パッドのフォーカス表現に共用）。</summary>
        private FrameworkElement TimeTile(string label, int minutes, Action go, out Action<bool> setHot) {
            var c = TintColor(minutes);
            var b = new Border {
                Width = 150, Height = 126, Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand,
                BorderBrush = Math.Abs(minutes - _timeMinutes) < 1 ? Gold : CardBorder, BorderThickness = new Thickness(Math.Abs(minutes - _timeMinutes) < 1 ? 2 : 1),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
            };
            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            bg.GradientStops.Add(new GradientStop(Color.FromArgb(0xD0, c.R, c.G, c.B), 0));
            bg.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, (byte)(c.R * 0.35), (byte)(c.G * 0.35), (byte)(c.B * 0.35)), 1));
            b.Background = bg;
            var st = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 14, 12) };
            st.Children.Add(new TextBlock { Text = label, FontFamily = HeadFont, FontSize = 16, Foreground = Ivory, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.8 } });
            st.Children.Add(new TextBlock { Text = FormatTime(minutes), FontFamily = NumFont, FontSize = 36, Foreground = Ivory, Margin = new Thickness(0, -2, 0, 0), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.8 } });
            b.Child = st;
            var sc = (ScaleTransform)b.RenderTransform;
            Action<bool> hot = on => {
                b.BorderBrush = on || Math.Abs(minutes - _timeMinutes) < 1 ? Gold : CardBorder;
                var a = new DoubleAnimation(on ? 1.05 : 1, TimeSpan.FromMilliseconds(on ? 140 : 180)) { EasingFunction = new SineEase() };
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, a); sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            };
            setHot = hot;
            b.MouseEnter += (o, e) => hot(true);
            b.MouseLeave += (o, e) => hot(false);
            b.MouseLeftButtonUp += (o, e) => { e.Handled = true; go(); };
            return b;
        }

        /// <summary>その時刻の空の色（タイル用）。</summary>
        private static Color TintColor(int minutes) {
            var t = minutes / 60.0;
            if (t < 5 || t >= 20.5) return Color.FromRgb(0x10, 0x18, 0x3A);
            if (t < 7.5) return Lerp(Color.FromRgb(0x10, 0x18, 0x3A), Color.FromRgb(0xE0, 0x90, 0x50), (t - 5) / 2.5);
            if (t < 9.5) return Lerp(Color.FromRgb(0xE0, 0x90, 0x50), Color.FromRgb(0x5A, 0xA8, 0xE8), (t - 7.5) / 2);
            if (t < 16.5) return Color.FromRgb(0x5A, 0xA8, 0xE8);
            if (t < 18.5) return Lerp(Color.FromRgb(0x5A, 0xA8, 0xE8), Color.FromRgb(0xE8, 0x78, 0x30), (t - 16.5) / 2);
            return Lerp(Color.FromRgb(0xE8, 0x78, 0x30), Color.FromRgb(0x10, 0x18, 0x3A), (t - 18.5) / 2);
        }

        private void SetTime(int minutes, bool updateSlider) {
            minutes = ((minutes % 1440) + 1440) % 1440;
            _timeMinutes = minutes;
            if (_timeBig != null) _timeBig.Text = FormatTime(minutes);
            if (_timeLabel != null) _timeLabel.Text = TimeWord(minutes);
            if (updateSlider && _timeSlider != null && Math.Abs(_timeSlider.Value - minutes) > 0.5) _timeSlider.Value = minutes;
            PositionSun();
            UpdateTint(true);
            RebuildStepList();
        }

        private void PositionSun() {
            if (_sunCanvas == null || _sun == null) return;
            var w = _sunCanvas.ActualWidth; var h = _sunCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var x = 8 + (w - 16) * _timeMinutes / 1439.0;
            var t = _timeMinutes / 60.0;
            // 日の出 6h / 日没 18.5h を山にした高さ
            var up = t > 5.5 && t < 19 ? Math.Sin((t - 5.5) / 13.5 * Math.PI) : 0;
            var y = h - 10 - up * (h - 20);
            Canvas.SetLeft(_sun, x - 7);
            Canvas.SetTop(_sun, y - 7);
            _sun.Fill = up > 0 ? Gold : new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xE8));
        }

        private static Brush SkyBand() {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x07, 0x0C, 0x22), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x12, 0x30), 0.20));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xC8, 0x6A, 0x3C), 0.255));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xF0, 0xB0, 0x60), 0.29));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x6F, 0xB4, 0xE8), 0.38));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x4C, 0x9C, 0xE6), 0.50));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x6F, 0xB4, 0xE8), 0.64));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0x9A, 0x4A), 0.73));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xB0, 0x4A, 0x3A), 0.78));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x14, 0x3A), 0.84));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x12, 0x30), 0.90));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x07, 0x0C, 0x22), 1.0));
            b.Freeze();
            return b;
        }

        /// <summary>背景の色被り。夜は青黒、朝夕は橙。</summary>
        private void UpdateTint(bool animate) {
            var t = _timeMinutes / 60.0;
            Color c; double a;
            if (t < 5 || t >= 20.5) { c = Color.FromRgb(0x06, 0x0C, 0x26); a = 0.72; }
            else if (t < 7.5) { var k = (t - 5) / 2.5; c = Lerp(Color.FromRgb(0x06, 0x0C, 0x26), Color.FromRgb(0xE0, 0x90, 0x50), k); a = 0.72 - 0.30 * k; }
            else if (t < 9.5) { var k = (t - 7.5) / 2; c = Lerp(Color.FromRgb(0xE0, 0x90, 0x50), Color.FromRgb(0x80, 0xB0, 0xE0), k); a = 0.42 - 0.36 * k; }
            else if (t < 16.5) { c = Color.FromRgb(0x80, 0xB0, 0xE0); a = 0.06; }
            else if (t < 18.5) { var k = (t - 16.5) / 2; c = Lerp(Color.FromRgb(0x80, 0xB0, 0xE0), Color.FromRgb(0xE8, 0x78, 0x30), k); a = 0.06 + 0.38 * k; }
            else { var k = (t - 18.5) / 2; c = Lerp(Color.FromRgb(0xE8, 0x78, 0x30), Color.FromRgb(0x06, 0x0C, 0x26), k); a = 0.44 + 0.28 * k; }
            TimeTint.Fill = new SolidColorBrush(c);
            if (animate) TimeTint.BeginAnimation(OpacityProperty, new DoubleAnimation(a, TimeSpan.FromMilliseconds(300)));
            else TimeTint.Opacity = a;
        }

        private static Color Lerp(Color a, Color b, double k) {
            k = Math.Max(0, Math.Min(1, k));
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
        }

        private static string FormatTime(int minutes) => (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");

        private static string TimeWord(int minutes) {
            var t = minutes / 60.0;
            if (t < 4) return "深夜";
            if (t < 6.5) return "夜明け";
            if (t < 10) return "朝";
            if (t < 15) return "昼";
            if (t < 17) return "午後";
            if (t < 19) return "夕方";
            if (t < 21) return "宵";
            return "夜";
        }
        #endregion

        #region Step: go
        private void BuildGoStep() {
            var panel = new StackPanel { Width = 760 };

            var car = WorldMap.FindCurrentCar();
            var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var carBox = new Border { Width = 300, Height = 170, CornerRadius = new CornerRadius(3), Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1), ClipToBounds = true };
            var carImg = new Image { Stretch = Stretch.UniformToFill, Source = car == null ? null : WorldMap.LoadBitmap(car.SelectedSkin?.PreviewImage, 600) };
            RenderOptions.SetBitmapScalingMode(carImg, BitmapScalingMode.HighQuality);
            carBox.Child = carImg;
            grid.Children.Add(carBox);

            var rows = new StackPanel { Margin = new Thickness(26, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            rows.Children.Add(Row("車", car?.DisplayName ?? "（未設定）"));
            rows.Children.Add(Row("コース", System.Text.RegularExpressions.Regex.Replace(_track.DisplayName ?? "", @"\s*\(\d+\)\s*$", "")));
            if (_layouts.Count > 1) rows.Children.Add(Row(LayoutStepLabel, ShortLayoutName(_layout)));
            rows.Children.Add(Row("モード", _mode == DriveMode.Race ? "レース" : "タイムトライアル"));
            rows.Children.Add(Row("時刻", FormatTime(_timeMinutes) + "  " + TimeWord(_timeMinutes)));
            Grid.SetColumn(rows, 1);
            grid.Children.Add(rows);
            panel.Children.Add(grid);

            // [fork] GT7 pre_race ref_1: 「周回数: 5   スタート方式: ローリングスタート」のインライン情報行
            var way = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), FontFamily = NumFont, FontSize = 14 };
            if (_mode == DriveMode.Race) {
                LoadRaceData();
                way.Inlines.Add(new System.Windows.Documents.Run("周回数:  ") { Foreground = Dim });
                way.Inlines.Add(new System.Windows.Documents.Run(_raceLaps.ToString()) { Foreground = Ivory, FontSize = 17 });
                way.Inlines.Add(new System.Windows.Documents.Run("        "));
            }
            way.Inlines.Add(new System.Windows.Documents.Run(_mode == DriveMode.Race ? "スタート方式:  グリッドスタート" : "スタート方式を選択") { Foreground = Ivory });
            panel.Children.Add(way);
            if (_mode == DriveMode.TimeTrial) {
                panel.Children.Add(BuildStartModeSelector());
                _focusLayout = GtFocusLayout.Spatial;
                _focusInitial = _focus.Count; // Start remains the initial pad action; up selects the start method.
            }

            // 下部ボタン列(ref_1): スタート=枠線ピル / セッティング☰ / 終了=赤塗りピル
            BottomBar.Children.Clear();
            BottomBar.Margin = new Thickness(0, 0, 0, 14);
            AddQuickMenuBtn("🏁", "スタート", null, false, Launch);
            AddQuickMenuBtn("⚙", "セッティング", "☰", false, ToggleSettingDialog);
            if (_mode == DriveMode.Race) AddQuickMenuBtn("🏎", "レース設定", "☰", false, ToggleRaceDialog);
            // GT7の「終了」=コース画面から出る。前のステップへはEsc
            AddQuickMenuBtn("🏃", "終了", null, true,
                    () => WorldMap.Navigate("/Pages/Gt/RegionMap.xaml?Region=" + Uri.EscapeDataString(_regionKey ?? "eu")));
            BottomBar.Visibility = Visibility.Visible;

            StepContent.Children.Add(panel);
        }

        private FrameworkElement BuildStartModeSelector() {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0) };
            var updates = new List<Action>();
            foreach (var entry in new[] {
                new[] { "rolling", "ローリングスタート", "走行状態から操作を引き継ぐ" },
                new[] { "pit", "ピットスタート", "ピット／PAで停止して出発" },
                new[] { "grid", "グリッドスタート", "開始位置から発進／未設置時はピット" }
            }) {
                var id = entry[0];
                var label = entry[1];
                var box = new Border { Width = 240, Height = 82, Margin = new Thickness(5, 0, 5, 0),
                    CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(2), Padding = new Thickness(10, 8, 10, 8),
                    Cursor = Cursors.Hand };
                var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var title = new TextBlock { FontFamily = NumFont, FontSize = 17, TextAlignment = TextAlignment.Center };
                var detail = new TextBlock { Text = entry[2], FontFamily = NumFont, FontSize = 11,
                    TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
                body.Children.Add(title); body.Children.Add(detail); box.Child = body;
                var hot = false;
                Action refresh = () => {
                    var selected = _timeTrialStartMode == id;
                    box.Background = hot ? HotFill : QuickBg;
                    box.BorderBrush = hot ? Brushes.White : selected ? Gold : CardBorder;
                    title.Text = (selected ? "✓  " : "") + label;
                    title.Foreground = hot ? HotInk : selected ? Gold : Ivory;
                    detail.Foreground = hot ? HotSubInk : Dim;
                };
                Action choose = () => TestSetStartMode(id);
                box.MouseEnter += (o, e) => { hot = true; refresh(); };
                box.MouseLeave += (o, e) => { hot = false; refresh(); };
                box.MouseLeftButtonUp += (o, e) => { e.Handled = true; if (!_launching) choose(); };
                _focus.Add(new GtFocusItem { Name = label, Center = () => PadCenter(box),
                    SetHot = on => { hot = on; refresh(); }, Activate = PadGuard(choose) });
                updates.Add(refresh); row.Children.Add(box);
            }
            _refreshStartMode = () => { foreach (var update in updates) update(); };
            _refreshStartMode();
            return row;
        }

        internal bool TestSetStartMode(string value) {
            if (_launching || _mode != DriveMode.TimeTrial || (value != "rolling" && value != "pit" && value != "grid")) return false;
            _timeTrialStartMode = value;
            ValuesStorage.Set(StartModeKey, value);
            _refreshStartMode?.Invoke();
            return true;
        }

        private bool CycleStartMode(int direction) {
            if (_launching || _step != Step.Go || _mode != DriveMode.TimeTrial
                    || SettingOverlay.Visibility == Visibility.Visible || RaceOverlay.Visibility == Visibility.Visible) return false;
            var choices = new[] { "rolling", "pit", "grid" };
            TestSetStartMode(choices[(Array.IndexOf(choices, _timeTrialStartMode) + direction + choices.Length) % choices.Length]);
            GtSfx.PlayMove();
            return true;
        }

        /// <summary>[fork] クイックメニューのボタンを 1 個作って、BottomBar とパッドの候補の両方へ足す。</summary>
        private void AddQuickMenuBtn(string glyph, string label, string suffix, bool danger, Action onClick) {
            Action<bool> hot;
            var b = QuickMenuBtn(glyph, label, suffix, danger, onClick, out hot);
            BottomBar.Children.Add(b);
            _focus.Add(new GtFocusItem {
                Name = label, Center = () => PadCenter(b), SetHot = hot, Activate = PadGuard(onClick)
            });
        }

        // [fork] クイックメニューのピル配色。以前は呼ばれるたびに new していたので、Hot を解く時に戻す先が無かった
        private static readonly Brush QuickBg = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0x10, 0x14, 0x18)));
        private static readonly Brush QuickEdge = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush QuickDanger = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0x30, 0x24)));
        private static readonly Brush QuickDangerHot = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x4C, 0x3C)));

        /// <summary>GT7クイックメニューのピルボタン。通常=半透明暗+白枠線、danger=赤塗り(終了)。
        /// [fork] setHot = ホバーと同じ見た目（パッドのフォーカス表現に共用）。
        /// ★2026-09-04: Hot は GT7 と同じ白反転（白塗り＋黒文字/黒アイコン＋白枠）。終了(danger)だけは赤のまま
        ///   明度を上げて白 2px 枠。danger は最初から 2px（透明でなく赤）なので Hot で外寸が動かない。
        ///   通常ボタンは 1→2px にする代わりに Padding を 24→23 に削って相殺＝ボタン列が左右にガタつかない。</summary>
        private static Border QuickMenuBtn(string glyph, string label, string suffix, bool danger, Action onClick, out Action<bool> setHot) {
            var b = new Border {
                Height = 44, CornerRadius = new CornerRadius(22), Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(24, 0, 24, 0),
                Cursor = Cursors.Hand,
                Background = danger ? QuickDanger : QuickBg,
                BorderBrush = danger ? QuickDanger : QuickEdge,
                BorderThickness = new Thickness(danger ? 2 : 1)
            };
            var s = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var glyphText = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Foreground = Ivory };
            var labelText = new TextBlock { Text = label, FontFamily = NumFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = Ivory };
            s.Children.Add(glyphText);
            s.Children.Add(labelText);
            TextBlock suffixText = null;
            if (suffix != null) {
                suffixText = new TextBlock { Text = suffix, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = Dim };
                s.Children.Add(suffixText);
            }
            b.Child = s;
            Action<bool> hot = on => {
                b.Opacity = 1;   // 旧実装の opacity 0.82 の名残が残らないように必ず戻す
                if (danger) {
                    b.Background = on ? QuickDangerHot : QuickDanger;
                    b.BorderBrush = on ? Brushes.White : QuickDanger;
                    // 文字は赤地のまま白
                } else {
                    b.Background = on ? HotFill : QuickBg;
                    b.BorderBrush = on ? Brushes.White : QuickEdge;
                    // 枠 1→2px。Width 非固定なのでそのままだと外寸が 2px 太ってボタン列が左右にガタつく。
                    // Padding を 24→23 に削って相殺する（Height は 44 固定なので縦は動かない）。
                    b.BorderThickness = new Thickness(on ? 2 : 1);
                    b.Padding = new Thickness(on ? 23 : 24, 0, on ? 23 : 24, 0);
                    glyphText.Foreground = on ? HotInk : Ivory;
                    labelText.Foreground = on ? HotInk : Ivory;
                    if (suffixText != null) suffixText.Foreground = on ? HotSubInk : Dim;
                }
                b.Effect = on ? new System.Windows.Media.Effects.DropShadowEffect {
                    BlurRadius = 16, ShadowDepth = 0, Color = danger ? Color.FromRgb(0xFF, 0x60, 0x50) : Colors.White, Opacity = 0.55
                } : null;
            };
            setHot = hot;
            b.MouseEnter += (o, e) => hot(true);
            b.MouseLeave += (o, e) => hot(false);
            b.MouseLeftButtonUp += (o, e) => { e.Handled = true; onClick(); };
            return b;
        }

        private static FrameworkElement Row(string key, string value) {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = key, FontFamily = NumFont, FontSize = 12, Foreground = Gold, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
            var v = new TextBlock { Text = value, FontFamily = HeadFont, FontSize = 20, Foreground = Ivory, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            return g;
        }

        private const double MinLoadingSeconds = 5.0;

        private GtLoadingWindow _loadingWindow;

        /// <summary>全画面・最前面の「幕」（GtLoadingWindow）。AC の起動〜ピット画面を全部この裏に隠す。</summary>
        private void ShowLoading(CarObject car, TrackObjectBase layout) {
            HideLoading();
            var w = new GtLoadingWindow();
            w.SetContent(_track, layout, _layouts.Count > 1 ? ShortLayoutName(layout) : null, car, null); // [fork] 時間帯表記は出さない（要望 2026-08-24）
            w.Closed += (o, e) => { if (ReferenceEquals(_loadingWindow, w)) _loadingWindow = null; };
            _loadingWindow = w;
            w.Show();
        }

        private void HideLoading() {
            var w = _loadingWindow;
            _loadingWindow = null;
            if (w != null) { try { w.Drop("done"); } catch { } }
        }

        private async void Launch() {
            if (_launching || _track == null) return;
            _launching = true;
            SaveLastDrive();   // [fork] ワールドマップの「もう一度走る」がこれを読む
            try {
                var layout = _layout ?? _track;
                var isRace = _mode == DriveMode.Race;
                var isTimeTrial = _mode == DriveMode.TimeTrial;
                var startMode = isTimeTrial ? _timeTrialStartMode : "grid";
                var mode = isRace ? QuickDrive.ModeRace : QuickDrive.ModeHotlap;
                var time = _timeMinutes * 60;
                var car = WorldMap.FindCurrentCar();
                string saved = null;
                try { saved = ValuesStorage.Get<string>("__QuickDrive_Main"); } catch (Exception ex) { Logging.Warning("[CourseEntry] preset: " + ex.Message); }
                if (isRace) { LoadRaceData(); saved = InjectRaceData(saved); }
                var conditions = isTimeTrial ? new GtDriveConditions(saved) : null;
                var changingCar = false;
                while (true) {
                    GtGameUi gtUi = null;
                    CarObject tunedCar = null;
                    string request = null;
                    var restored = true;
                    try {
                        WriteGtStartFlag(layout, car, isRace ? "race" : "timetrial", startMode);
                        GtRecords.PrepareForLaunch(layout, car);   // [fork] 準備画面の「ベストタイム」面（自己ベスト・世界記録）
                        ShowLoading(car, layout);
                        // Only the initial course entry has the presentation pause. A car swap loads immediately.
                        if (!changingCar) await Task.Delay(TimeSpan.FromSeconds(MinLoadingSeconds));
                        SetHideMainMenu(!isRace);
                        var launchPreset = GtTuning.InjectPreset(conditions?.Preset ?? saved, car, isRace, mode);
                        tunedCar = car;
                        if (!GtTuning.Prepare(car)) {
                            tunedCar = null;   // Prepare keeps its own restoration ledger on failure.
                            ReadGtStartRequest();
                            NonfatalError.Notify("コースインを中止しました", "チューニングの準備または元データの復元に失敗しました。車両データを確認してから再試行してください。");
                            return;
                        }
                        gtUi = new GtGameUi();
                        // [fork] 2026-09-20: 走行条件に続けて、GT7 HUD のポーズ画面で保存したアシスト（MT/AT 等）を起動設定へ上書きする
                        gtUi.OnStart = properties => { conditions?.Apply(properties); GtGameUi.ApplySavedAssists(properties); };
                        var loadingWindow = _loadingWindow;
                        gtUi.StatusChanged += msg => Dispatcher.BeginInvoke(new Action(() => loadingWindow?.SetStatus(msg)));
                        GtGameUi.Pending = gtUi;
                        if (GtTestChannel.DiagnosticsEnabled) Logging.Write("[GtCarSwitch] run car=" + car?.Id + " swap=" + changingCar);
                        var ok = await QuickDrive.RunAsync(car: car, track: layout, time: time,
                                serializedPreset: string.IsNullOrEmpty(launchPreset) ? null : launchPreset, mode: mode);
                        request = ReadGtStartRequest();
                        if (!ok) {
                            Logging.Warning("[CourseEntry] RunAsync returned false, falling back to QuickDrive page");
                            QuickDrive.Show(car: car, track: layout, time: time, mode: mode);
                            return;
                        }
                        GtRecords.SaveAfterRun();   // [fork] ベストと走った条件を恒久保存（Data\Static\gt7\records）
                    } finally {
                        // Finish the old session before opening the picker or creating any new loading window.
                        if (ReferenceEquals(GtGameUi.Pending, gtUi)) GtGameUi.Pending = null;
                        SetHideMainMenu(false);
                        if (tunedCar != null && !GtTuning.Restore(tunedCar)) {
                            restored = false;
                            NonfatalError.Notify("車両データの復元が完了していません", "元データは退避先に保持されています。この車で再び走る前に、復元状態を確認してください。");
                        }
                        HideLoading();
                    }
                    if (!restored || !IsLoaded) return;
                    if (!isTimeTrial || request != "change_car") break;
                    bool leavePreparation;
                    var picked = PickCar(car, out leavePreparation);
                    if (leavePreparation || !IsLoaded) break;
                    if (picked != null) {
                        car = picked;
                        SetPresetCar(car);
                    }
                    // Cancelling keeps the original car and returns to the same preparation screen.
                    changingCar = true;
                }
                if (IsLoaded) ShowStep(Step.Mode, -1);
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] launch: " + ex);
                ReadGtStartRequest();
                HideLoading();
                try { QuickDrive.Show(track: _layout ?? _track); } catch { }
            } finally {
                _launching = false;
                HideLoading();
            }
        }

        /// <summary>CSP の GtStart アプリへの合図（Documents\Assetto Corsa\cfg\gt_start.ini）。</summary>
        private const string GuiOverrideMarker = "; written by CM fork (GtStart) — removed automatically after the session";

        /// <summary>Documents\Assetto Corsa\cfg\extension\gui.ini（CSP のユーザー上書き）に HIDE_MAIN_MENU=1 を書く／消す。
        /// 既存の gui.ini があれば退避して、終了後に戻す。自分が書いたファイルだけ消す（マーカー行で判別）。</summary>
        internal static void SetHideMainMenu(bool on) {
            try {
                var dir = System.IO.Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "extension");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "gui.ini");
                var backup = path + ".gtstart-bak";
                if (on) {
                    if (File.Exists(path)) {
                        var cur = File.ReadAllText(path);
                        if (!cur.Contains(GuiOverrideMarker)) File.Copy(path, backup, true);
                    }
                    File.WriteAllText(path, GuiOverrideMarker + Environment.NewLine + "[BASIC]" + Environment.NewLine + "HIDE_MAIN_MENU=1" + Environment.NewLine);
                } else {
                    if (File.Exists(path) && File.ReadAllText(path).Contains(GuiOverrideMarker)) {
                        if (File.Exists(backup)) {
                            File.Copy(backup, path, true);
                            File.Delete(backup);
                        } else {
                            File.Delete(path);
                        }
                    }
                }
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] gui.ini: " + ex.Message);
            }
        }

        private static void WriteGtStartFlag(TrackObjectBase layout, CarObject car, string mode, string startMode) {
            try {
                var dir = AcPaths.GetDocumentsCfgDirectory();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var stamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                var text = "[GT]\r\nENABLED=1\r\nMODE=" + mode + "\r\nTRACK=" + (layout.MainTrackObject?.Id ?? layout.Id) + "\r\nLAYOUT=" + (layout.LayoutId ?? "")
                        + "\r\nCAR=" + (car?.Id ?? "") + "\r\nSTART_MODE=" + NormalizeStartMode(startMode) + "\r\nSTAMP=" + stamp.ToString(CultureInfo.InvariantCulture)
                        + "\r\nWORKOUT_DAY=" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        + "\r\nWORKOUT_KM=" + GtHeader.GetDailyKm().ToString("R", CultureInfo.InvariantCulture)
                        + "\r\nUI_VOLUME=" + GtSfx.Volume.ToString("R", CultureInfo.InvariantCulture) + "\r\n";
                File.WriteAllText(System.IO.Path.Combine(dir, "gt_start.ini"), text);
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] gt_start.ini: " + ex.Message);
            }
        }

        /// <summary>gt_start.ini の REQUEST= を読んで消す（ENABLED も 0 にして、次の普通の起動に残さない）。</summary>
        private static string ReadGtStartRequest() {
            try {
                var path = System.IO.Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt_start.ini");
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                string request = null, mode = null, state = null;
                foreach (var line in lines) {
                    var t = line.Trim();
                    if (t.StartsWith("REQUEST=", StringComparison.OrdinalIgnoreCase)) request = t.Substring(8).Trim();
                    if (t.StartsWith("MODE=", StringComparison.OrdinalIgnoreCase)) mode = t.Substring(5).Trim();
                    if (t.StartsWith("STATE=", StringComparison.OrdinalIgnoreCase)) state = t.Substring(6).Trim();
                }
                File.WriteAllText(path, "[GT]" + Environment.NewLine + "ENABLED=0" + Environment.NewLine);
                if (request == "change_car" && (mode != "timetrial" || state != "preview")) return null;
                return string.IsNullOrEmpty(request) ? null : request;
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] request: " + ex.Message);
                return null;
            }
        }

        private static CarObject PickCar(CarObject current, out bool leavePreparation) {
            leavePreparation = false;
            try {
                // [fork] GT7の「乗り換え」画面(GarageCarPicker)で統一
                var picker = new GarageCarPicker(current);
                // [fork] 2026-10-17 持ち主を付けないと、画面構成が変わった後などに本体の裏に開き、本体はモーダル待ちでクリック不能になる
                var owner = Application.Current?.MainWindow;
                if (owner != null && owner.IsVisible && !ReferenceEquals(owner, picker)) picker.Owner = owner;
                picker.ShowDialog();
                if (picker.GoSettings || picker.GoBrandCentral) {
                    leavePreparation = true;
                    var main = Application.Current?.MainWindow as AcManager.Pages.Windows.MainWindow;
                    var source = main?.CurrentSource;
                    // Launch の finally（車両データ復元など）が完了してから設定へ移る。
                    main?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => {
                        if (ReferenceEquals(Application.Current?.MainWindow, main) && Equals(main.CurrentSource, source)) {
                            WorldMap.Navigate(picker.GoSettings ? GtHeader.SettingsUri : "/Pages/Gt/BrandCentral.xaml");
                        }
                    }));
                    return null;
                }
                return picker.Picked;
            } catch (Exception ex) {
                leavePreparation = true;
                Logging.Warning("[CourseEntry] pick car: " + ex.Message);
                return null;
            }
        }

        /// <summary>普段のクイックドライブ設定（__QuickDrive_Main）の CarId を差し替える＝ドライバーカードと次回の発射に効く。</summary>
        internal static void SetPresetCar(CarObject car) {
            try {
                var saved = ValuesStorage.Get<string>("__QuickDrive_Main");
                var o = string.IsNullOrWhiteSpace(saved) ? new Newtonsoft.Json.Linq.JObject() : Newtonsoft.Json.Linq.JObject.Parse(saved);
                o["CarId"] = car.Id;
                ValuesStorage.Set("__QuickDrive_Main", o.ToString(Newtonsoft.Json.Formatting.None));
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] set car: " + ex.Message);
            }
        }

        /// <summary>背景写真。Data\Static\gt7	racks\&lt;トラックID&gt;.jpg|png があればそれ（高解像度差し替え用）、無ければ AC の preview.png（355×200 なので荒い）。</summary>
        internal static System.Windows.Media.Imaging.BitmapImage LoadTrackPhoto(TrackObject track) {
            try {
                var dir = System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "tracks");
                foreach (var ext in new[] { ".jpg", ".png", ".jpeg" }) {
                    var f = System.IO.Path.Combine(dir, track.Id + ext);
                    // [fork] 画面幅で解く: モナコは 4256×2832（約 4800 万バイト）を原寸で UI スレッドに解いていて、ピンを押してから固まった（2026-10-14）
                    if (File.Exists(f)) return WorldMap.LoadBitmap(f, 1920);
                }
            } catch { }
            return WorldMap.LoadBitmap(track.PreviewImage, 0);
        }

        private static int? ReadSavedTimeMinutes() {
            try {
                var saved = ValuesStorage.Get<string>("__QuickDrive_Main");
                if (string.IsNullOrWhiteSpace(saved)) return null;
                var v = Newtonsoft.Json.Linq.JObject.Parse(saved)["Time"];
                if (v == null) return null;
                var seconds = v.ToObject<int>();
                if (seconds < 0 || seconds >= 24 * 3600) return null;
                return seconds / 60;
            } catch {
                return null;
            }
        }
        #endregion

        #region Quick setting (GT7 ref_6)
        // レース直前の簡易セッティング: GT7のクイックメニュー「カーセッティング」縦ダイアログの写し。
        // タイヤ行は汎用セットアップファイル([TYRES] VALUE)を実際に書き替える(ACはフロント/リア同一コンパウンドなので2行連動)。
        private static readonly Brush RowBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20)));
        private static readonly Brush RowEdge = Freeze(new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)));
        private CarSetupObject _quickSetup;
        private List<(string name, string shortName)> _tyreCompounds = new List<(string, string)>();
        private TextBlock _tyreBadgeF, _tyreNameF, _tyreBadgeR, _tyreNameR;
        private bool _settingBusy;

        private async void ToggleSettingDialog() {
            if (SettingOverlay.Visibility == Visibility.Visible) { CloseSettingDialog(); return; }
            var car = WorldMap.FindCurrentCar();
            if (car == null) return;
            SettingOverlay.Visibility = Visibility.Visible;
            SettingOverlay.Opacity = 0;
            SettingOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new SineEase() });
            RegisterBlockingFocus("（セッティング準備中）");   // [fork] 中身が出来るまで裏の候補を殺す
            try {
                await BuildSettingDialogAsync(car);
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] setting dialog: " + ex);
            }
        }

        private void CloseSettingDialog() {
            SettingOverlay.Visibility = Visibility.Collapsed;
            Focus();
            RegisterStepFocus();   // [fork] パッドのフォーカスをステップの候補へ戻す
        }

        private void OnSettingOverlayClick(object sender, MouseButtonEventArgs e) { CloseSettingDialog(); }
        private void OnSettingDialogClick(object sender, MouseButtonEventArgs e) { e.Handled = true; }

        private async Task BuildSettingDialogAsync(CarObject car) {
            SettingBody.Children.Clear();
            SettingBody.Children.Add(new TextBlock {
                Text = "カーセッティング", FontFamily = NumFont, FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center
            });

            // 国旗+メーカー(左) / PPピル(右)
            var head = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            var brandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            try {
                brandRow.Children.Add(new AcManager.Controls.CountryIcon {
                    Country = car.Country, Width = 24, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
                });
            } catch { }
            brandRow.Children.Add(new TextBlock { Text = car.Brand ?? "", FontFamily = HeadFont, FontSize = 15, Foreground = Ivory, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(brandRow);
            var pp = SettingSheet.ComputePp(car);
            if (pp > 0) {
                var pill = new Border {
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xB0)), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 3, 14, 4)
                };
                var pt = new TextBlock { FontFamily = NumFont, FontSize = 13 };
                pt.Inlines.Add(new System.Windows.Documents.Run("PP ") { Foreground = Dim });
                pt.Inlines.Add(new System.Windows.Documents.Run(pp.ToString("0.00", CultureInfo.InvariantCulture)) { Foreground = Brushes.White });
                pill.Child = pt;
                head.Children.Add(pill);
            }
            SettingBody.Children.Add(head);

            // 車レンダー + 車名
            var img = new Image { Height = 170, Stretch = Stretch.Uniform, Margin = new Thickness(0, 6, 0, 0), Source = WorldMap.LoadBitmap(car.SelectedSkin?.PreviewImage, 700) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            SettingBody.Children.Add(img);
            SettingBody.Children.Add(new TextBlock { Text = car.DisplayName ?? "", FontFamily = HeadFont, FontSize = 18, Foreground = Ivory, Margin = new Thickness(2, 4, 0, 8) });

            // コンパウンド一覧(tyres.iniのFRONT系セクション)
            _tyreCompounds = new List<(string, string)>();
            try {
                var ini = car.AcdData?.GetIniFile("tyres.ini");
                if (ini != null) {
                    foreach (var s in ini.GetSections("FRONT", -1)) {
                        var name = s.GetPossiblyEmpty("NAME") ?? "";
                        var sn = s.GetPossiblyEmpty("SHORT_NAME");
                        if (string.IsNullOrEmpty(sn)) sn = name.Length >= 2 ? name.Substring(0, 2).ToUpperInvariant() : name.ToUpperInvariant();
                        _tyreCompounds.Add((name, sn));
                    }
                }
            } catch (Exception ex) { Logging.Warning("[CourseEntry] tyres: " + ex.Message); }

            // 保存先 = 汎用(コース指定なし)のセットアップを優先
            _quickSetup = null;
            try {
                var mgr = CarSetupsManager.Create(car);
                await mgr.EnsureLoadedAsync();
                _quickSetup = mgr.Loaded.OfType<CarSetupObject>().OrderBy(x => x.Track == null ? 0 : 1).FirstOrDefault();
            } catch (Exception ex) { Logging.Warning("[CourseEntry] setups: " + ex.Message); }

            Action openSheet = () => { CloseSettingDialog(); WorldMap.Navigate("/Pages/Gt/SettingSheet.xaml"); };
            var sheetRow = SettingRow("セッティングシート", _quickSetup?.DisplayName ?? "（セットアップ未作成）", "⣿", true, openSheet);
            SettingBody.Children.Add(sheetRow);
            var tyreRowF = TyreRow("フロントタイヤ", out _tyreBadgeF, out _tyreNameF);
            SettingBody.Children.Add(tyreRowF);
            var tyreRowR = TyreRow("リアタイヤ", out _tyreBadgeR, out _tyreNameR);
            SettingBody.Children.Add(tyreRowR);
            UpdateTyreRows();
            var detailRow = SettingRow(null, "詳細設定", "⣿", false, openSheet);
            SettingBody.Children.Add(detailRow);

            // [fork] パッド: ダイアログの行を縦 1 列で。SetHot は行のホバー表現（白 1px 枠）と同じ
            var items = new List<GtFocusItem> {
                new GtFocusItem {
                    Name = "セッティングシート", Center = () => PadCenter(sheetRow),
                    SetHot = on => sheetRow.BorderBrush = on ? Brushes.White : RowEdge, Activate = PadGuard(openSheet)
                },
                new GtFocusItem {
                    Name = "フロントタイヤ", Center = () => PadCenter(tyreRowF),
                    SetHot = on => tyreRowF.BorderBrush = on ? Brushes.White : RowEdge, Activate = PadGuard(CycleTyre)
                },
                new GtFocusItem {
                    Name = "リアタイヤ", Center = () => PadCenter(tyreRowR),
                    SetHot = on => tyreRowR.BorderBrush = on ? Brushes.White : RowEdge, Activate = PadGuard(CycleTyre)
                },
                new GtFocusItem {
                    Name = "詳細設定", Center = () => PadCenter(detailRow),
                    SetHot = on => detailRow.BorderBrush = on ? Brushes.White : RowEdge, Activate = PadGuard(openSheet)
                },
            };
            // ★await の間にダイアログが閉じられている（○/Esc）ことがある。
            //   その時に登録すると、見えない 4 行がパッドの候補を奪い返す（× で SettingSheet へ飛ぶ）。
            if (_gone || SettingOverlay.Visibility != Visibility.Visible) return;
            RegisterFocus(items, GtFocusLayout.Column);
        }

        /// <summary>ref_6の行ブロック。centerLabel有り=見出し中央+値行、無し=1行。ホバー行だけ白1px枠。</summary>
        private static Border SettingRow(string centerLabel, string value, string glyph, bool dimValue, Action click) {
            var b = new Border {
                Background = RowBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(22, 12, 18, 14),
                Margin = new Thickness(0, 10, 0, 0), BorderBrush = RowEdge, BorderThickness = new Thickness(1), Cursor = Cursors.Hand
            };
            var sp = new StackPanel();
            if (centerLabel != null) {
                sp.Children.Add(new TextBlock { Text = centerLabel, FontFamily = NumFont, FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
            }
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = value, FontFamily = NumFont, FontSize = 15, Foreground = dimValue ? Dim : Ivory, VerticalAlignment = VerticalAlignment.Center });
            var gl = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 15, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(gl, 1);
            g.Children.Add(gl);
            sp.Children.Add(g);
            b.Child = sp;
            b.MouseEnter += (o, e) => b.BorderBrush = Brushes.White;
            b.MouseLeave += (o, e) => b.BorderBrush = RowEdge;
            b.MouseLeftButtonUp += (o, e) => { e.Handled = true; click(); };
            return b;
        }

        /// <summary>タイヤ行: 丸バッジ(略号)+コンパウンド名+☰。クリックでコンパウンドを順送り(前後連動)。</summary>
        private Border TyreRow(string title, out TextBlock badge, out TextBlock name) {
            var b = new Border {
                Background = RowBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(22, 12, 18, 14),
                Margin = new Thickness(0, 10, 0, 0), BorderBrush = RowEdge, BorderThickness = new Thickness(1), Cursor = Cursors.Hand
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontFamily = NumFont, FontSize = 12, Foreground = Dim, Margin = new Thickness(0, 0, 0, 8) });
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            badge = new TextBlock { FontFamily = NumFont, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), Child = badge });
            name = new TextBlock { FontFamily = NumFont, FontSize = 15, Foreground = Ivory, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            row.Children.Add(name);
            g.Children.Add(row);
            var gl = new TextBlock { Text = "☰", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 15, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(gl, 1);
            g.Children.Add(gl);
            sp.Children.Add(g);
            b.Child = sp;
            b.MouseEnter += (o, e) => b.BorderBrush = Brushes.White;
            b.MouseLeave += (o, e) => b.BorderBrush = RowEdge;
            b.MouseLeftButtonUp += (o, e) => { e.Handled = true; CycleTyre(); };
            return b;
        }

        private void UpdateTyreRows() {
            string badge = "—", name = "—";
            if (_tyreCompounds.Count > 0) {
                var idx = _quickSetup?.Tyres ?? 0;
                idx = ((idx % _tyreCompounds.Count) + _tyreCompounds.Count) % _tyreCompounds.Count;
                badge = _tyreCompounds[idx].shortName;
                name = _tyreCompounds[idx].name;
                if (_quickSetup == null) name += "（セットアップ未作成・変更不可）";
            }
            if (_tyreBadgeF != null) { _tyreBadgeF.Text = badge; _tyreNameF.Text = name; }
            if (_tyreBadgeR != null) { _tyreBadgeR.Text = badge; _tyreNameR.Text = name; }
        }

        private async void CycleTyre() {
            if (_settingBusy || _quickSetup == null || _tyreCompounds.Count == 0) return;
            _settingBusy = true;
            try {
                _quickSetup.Tyres = ((_quickSetup.Tyres ?? 0) + 1) % _tyreCompounds.Count;
                UpdateTyreRows();
                await _quickSetup.SaveAsync();
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] tyre save: " + ex.Message);
            } finally {
                _settingBusy = false;
            }
        }
        #endregion

        #region 一発コースイン（ワールドマップの「もう一度走る」）
        // 直近のコースイン条件をここに残す。ワールドマップのピルがこれを読んで
        // /Pages/Gt/CourseEntry.xaml?...&Auto=1 へ飛ばすと、段階UIを飛ばしてそのまま走り出す。
        private const string LastDriveKey = "__Gt_LastDrive";
        private const string LastLayoutKeyPrefix = "__CourseEntry_LastLayout_";   // + コース ID。前回走ったレイアウト（出発地点）

        /// <summary>直近のコースイン条件。無ければ null。</summary>
        public class LastDrive {
            public string TrackId, LayoutId, Region, Mode, Title, LayoutTitle, StartMode;
            public int TimeMinutes;
            public bool IsRace => Mode == "race";
            public string ModeTitle => IsRace ? "レース" : "タイムトライアル";
        }

        private void SaveLastDrive() {
            try {
                if (_track == null) return;
                var o = new Newtonsoft.Json.Linq.JObject {
                    ["Track"] = _track.Id,
                    ["Layout"] = (_layout ?? _track).LayoutId ?? "",
                    ["Region"] = _regionKey ?? "eu",
                    ["Mode"] = _mode == DriveMode.Race ? "race" : "timetrial",
                    ["Time"] = _timeMinutes,
                    ["Start"] = NormalizeStartMode(_timeTrialStartMode),
                    ["Title"] = System.Text.RegularExpressions.Regex.Replace(_track.DisplayName ?? "", @"\s*\(\d+\)\s*$", ""),
                    ["LayoutTitle"] = _layouts.Count > 1 && _layout != null ? ShortLayoutName(_layout) : "",
                };
                ValuesStorage.Set(LastDriveKey, o.ToString(Newtonsoft.Json.Formatting.None));
                if (_layouts.Count > 1) ValuesStorage.Set(LastLayoutKeyPrefix + _track.Id, (_layout ?? _track).LayoutId ?? "");
            } catch (Exception ex) { Logging.Warning("[CourseEntry] last drive save: " + ex.Message); }
        }

        /// <summary>直近のコースイン条件を読む。一度も走っていなければ null。</summary>
        public static LastDrive ReadLastDrive() {
            try {
                var s = ValuesStorage.Get<string>(LastDriveKey);
                if (string.IsNullOrWhiteSpace(s)) return null;
                var o = Newtonsoft.Json.Linq.JObject.Parse(s);
                var trackId = (string)o["Track"];
                if (string.IsNullOrEmpty(trackId)) return null;
                return new LastDrive {
                    TrackId = trackId,
                    LayoutId = (string)o["Layout"] ?? "",
                    Region = (string)o["Region"] ?? "eu",
                    Mode = (string)o["Mode"] ?? "timetrial",
                    TimeMinutes = (int?)o["Time"] ?? 12 * 60,
                    StartMode = NormalizeStartMode((string)o["Start"]),
                    Title = (string)o["Title"] ?? trackId,
                    LayoutTitle = (string)o["LayoutTitle"] ?? "",
                };
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] last drive read: " + ex.Message);
                return null;
            }
        }

        /// <summary>直近の条件で「そのままコースイン」する遷移先 URI。</summary>
        public static string LastDriveUri(LastDrive d) {
            return "/Pages/Gt/CourseEntry.xaml?Track=" + Uri.EscapeDataString(d.TrackId)
                    + "&Region=" + Uri.EscapeDataString(d.Region)
                    + (string.IsNullOrEmpty(d.LayoutId) ? "" : "&Layout=" + Uri.EscapeDataString(d.LayoutId))
                    + "&Mode=" + d.Mode
                    + "&Time=" + d.TimeMinutes.ToString(CultureInfo.InvariantCulture)
                    + "&Start=" + NormalizeStartMode(d.StartMode)
                    + "&Auto=1";
        }
        #endregion

        #region Race settings (GT7 ref_8 簡易版)
        // レース設定(周回数/台数/AIの強さ)はCM7専用キーに保存し、発射時にプリセットのコピーへ注入する。
        // ユーザーの__QuickDrive_Main本体は書き替えない(普段のクイックドライブに副作用を出さない)。
        private const string RaceDataKey = "__CourseEntry_RaceData";
        private int _raceLaps = 2, _raceOpponents = 3, _raceAi = 92;

        private void LoadRaceData() {
            try {
                var s = ValuesStorage.Get<string>(RaceDataKey);
                if (string.IsNullOrEmpty(s)) return;
                var o = Newtonsoft.Json.Linq.JObject.Parse(s);
                _raceLaps = (int?)o["LapsNumber"] ?? _raceLaps;
                _raceOpponents = (int?)o["OpponentsNumber"] ?? _raceOpponents;
                _raceAi = (int?)o["AiLevel"] ?? _raceAi;
            } catch (Exception ex) { Logging.Warning("[CourseEntry] race data: " + ex.Message); }
        }

        private void SaveRaceData() {
            try {
                var o = new Newtonsoft.Json.Linq.JObject { ["LapsNumber"] = _raceLaps, ["OpponentsNumber"] = _raceOpponents, ["AiLevel"] = _raceAi };
                ValuesStorage.Set(RaceDataKey, o.ToString(Newtonsoft.Json.Formatting.None));
            } catch (Exception ex) { Logging.Warning("[CourseEntry] race data save: " + ex.Message); }
        }

        /// <summary>発射用プリセットにレース設定を注入。既存ModeDataがレースのものならマージして他の設定(グリッド構成等)を残す。</summary>
        private string InjectRaceData(string saved) {
            try {
                return InjectRaceSettings(saved, QuickDrive.ModeRace.OriginalString, _raceLaps, _raceOpponents, _raceAi);
            } catch (Exception ex) {
                Logging.Warning("[CourseEntry] race inject: " + ex.Message);
                return saved;
            }
        }

        // [fork] 現行CMはグリッド設定を RaceGridSerialized から読む。旧形式も変換せず維持する。
        // UIや保存先に触れない変換に分離し、既存車種リストとAI設定の往復を単独で検証できるようにする。
        internal static string InjectRaceSettings(string saved, string raceUri, int laps, int opponents, int ai) {
            var o = string.IsNullOrWhiteSpace(saved) ? new Newtonsoft.Json.Linq.JObject() : Newtonsoft.Json.Linq.JObject.Parse(saved);
            Newtonsoft.Json.Linq.JObject rd = null;
            if (string.Equals((string)o["Mode"], raceUri, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace((string)o["ModeData"])) {
                rd = Newtonsoft.Json.Linq.JObject.Parse((string)o["ModeData"]);
            }
            if (rd == null) rd = new Newtonsoft.Json.Linq.JObject();
            rd["LapsNumber"] = laps;
            var grid = rd;
            if (rd["Version"] != null) {
                var serializedGrid = (string)rd["RaceGridSerialized"];
                grid = string.IsNullOrWhiteSpace(serializedGrid)
                        ? new Newtonsoft.Json.Linq.JObject { ["ModeId"] = "same_car" }
                        : Newtonsoft.Json.Linq.JObject.Parse(serializedGrid);
            }
            grid["OpponentsNumber"] = opponents;
            grid["AiLevel"] = ai;
            grid["AiLevelMin"] = ai;   // AiLevelFixed は算出プロパティ: 上限と下限を同じにする。
            if (ReferenceEquals(grid, rd)) grid["AiLevelFixed"] = true;
            else rd["RaceGridSerialized"] = grid.ToString(Newtonsoft.Json.Formatting.None);
            o["Mode"] = raceUri;
            o["ModeData"] = rd.ToString(Newtonsoft.Json.Formatting.None);
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        private void ToggleRaceDialog() {
            if (RaceOverlay.Visibility == Visibility.Visible) { CloseRaceDialog(); return; }
            LoadRaceData();
            BuildRaceDialog();
            RaceOverlay.Visibility = Visibility.Visible;
            RaceOverlay.Opacity = 0;
            RaceOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new SineEase() });
        }

        private void CloseRaceDialog() {
            RaceOverlay.Visibility = Visibility.Collapsed;
            Focus();
            if (_step == Step.Go && IsLoaded) ShowStep(Step.Go, 0);   // 周回数のインライン表示を最新化（ここで Register し直される）
            else RegisterStepFocus();   // [fork] パッドのフォーカスをステップの候補へ戻す
        }

        private void OnRaceOverlayClick(object sender, MouseButtonEventArgs e) { CloseRaceDialog(); }

        private void BuildRaceDialog() {
            RaceBody.Children.Clear();
            RaceBody.Children.Add(new TextBlock {
                Text = "レースの設定", FontFamily = NumFont, FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14)
            });
            // [fork] パッド: 行を縦 1 列で。左右は各行の −/＋（RaceRow の Custom）
            var items = new List<GtFocusItem>();
            RaceBody.Children.Add(RaceRow("周回数", () => _raceLaps, v => _raceLaps = Math.Max(1, Math.Min(99, v)), null, items));
            RaceBody.Children.Add(RaceRow("対戦相手の数", () => _raceOpponents, v => _raceOpponents = Math.Max(1, Math.Min(23, v)), "台", items));
            RaceBody.Children.Add(RaceRow("AIの強さ", () => _raceAi, v => _raceAi = Math.Max(70, Math.Min(100, v)), "%", items));
            RegisterFocus(items, GtFocusLayout.Column);
        }

        /// <summary>[fork] レース設定の行にパッドのフォーカスが乗っている時の見た目（この行だけ薄く明るく）。</summary>
        private static readonly Brush RaceRowHot = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));

        /// <summary>ref_8のセル様式: ラベル(薄グレー小)＋値(白・大)を上下に、右に−/＋。行間は細い区切り線のみ。
        /// [fork] focus に候補を足す（左右で −/＋、× は消費だけ）。</summary>
        private FrameworkElement RaceRow(string label, Func<int> get, Action<int> set, string unit, IList<GtFocusItem> focus) {
            var outer = new StackPanel { Background = Brushes.Transparent };
            var g = new Grid { Margin = new Thickness(4, 10, 4, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = label, FontFamily = NumFont, FontSize = 12, Foreground = Dim });
            g.Children.Add(left);

            var valueText = new TextBlock {
                Text = get() + (unit ?? ""), FontFamily = NumFont, FontSize = 20, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            var field = new Border { Background = RowBg, CornerRadius = new CornerRadius(3), Padding = new Thickness(14, 5, 14, 6), VerticalAlignment = VerticalAlignment.Center, Child = valueText };
            Grid.SetColumn(field, 2);
            g.Children.Add(field);

            // [fork] ラムダから呼べるようローカル関数ではなくデリゲートで持つ（パッドの Custom が捕まえる）
            Action<int> step = dir => {
                set(get() + dir);
                valueText.Text = get() + (unit ?? "");
                SaveRaceData();
            };

            var minus = StepPill("−", () => step(-1));
            Grid.SetColumn(minus, 1); g.Children.Add(minus);
            var plus = StepPill("＋", () => step(1));
            Grid.SetColumn(plus, 3); g.Children.Add(plus);

            outer.Children.Add(g);
            outer.Children.Add(new System.Windows.Shapes.Rectangle { Height = 1, Fill = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)) });

            if (focus != null) {
                focus.Add(new GtFocusItem {
                    Name = label,
                    Center = () => PadCenter(outer),
                    SetHot = on => outer.Background = on ? RaceRowHot : Brushes.Transparent,
                    Custom = cmd => {
                        if (_launching) return true;
                        if (cmd == GtPadCommand.Left) { step(-1); GtSfx.PlayMove(); return true; }
                        if (cmd == GtPadCommand.Right) { step(1); GtSfx.PlayMove(); return true; }
                        // × はこのダイアログでは何もしない。ただし消費しないと Enter 合成が Go ステップの Launch に落ちる
                        return cmd == GtPadCommand.Confirm;
                    }
                });
            }
            return outer;
        }

        private static FrameworkElement StepPill(string glyph, Action onClick) {
            var b = new Border {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(3, 0, 3, 0),
                Background = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x36))), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Child = new TextBlock {
                    Text = glyph, FontSize = 13, Foreground = Ivory,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0)
                }
            };
            b.MouseEnter += (o, e) => b.Opacity = 0.8;
            b.MouseLeave += (o, e) => b.Opacity = 1;
            b.MouseLeftButtonUp += (o, e) => { e.Handled = true; try { onClick(); } catch (Exception ex) { Logging.Warning("[CourseEntry] race step: " + ex.Message); } };
            return b;
        }
        #endregion

        #region Test channel hooks (GtTestChannel用・UIには出ない)
        internal bool TestSelectMode(string v) {
            _mode = v == "race" ? DriveMode.Race : DriveMode.TimeTrial;
            if (_step == Step.Mode) Next(); else RebuildStepList();
            return true;
        }

        internal bool TestSelectLayout(string v) {
            var pick = int.TryParse(v, out var idx)
                    ? (idx >= 0 && idx < _layouts.Count ? _layouts[idx] : null)
                    : _layouts.FirstOrDefault(l => ShortLayoutName(l).Equals(v, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(l.LayoutId ?? "", v, StringComparison.OrdinalIgnoreCase));   // 表示名は日本語になり得るのでフォルダ名でも選べる
            if (pick == null) return false;
            _layout = pick;
            if (_step == Step.Layout) Next(); else RebuildStepList();
            return true;
        }

        internal bool TestSetTime(int minutes) { SetTime(minutes, true); return true; }
        internal bool TestNext() { Next(); return true; }
        internal bool TestBack() { Back(); return true; }

        internal bool TestOpen(string v) {
            if (v == "setting") { if (SettingOverlay.Visibility != Visibility.Visible) ToggleSettingDialog(); return true; }
            if (v == "race") { if (RaceOverlay.Visibility != Visibility.Visible) ToggleRaceDialog(); return true; }
            return false;
        }

        internal bool TestCloseDialogs() {
            if (RaceOverlay.Visibility == Visibility.Visible) CloseRaceDialog();
            if (SettingOverlay.Visibility == Visibility.Visible) CloseSettingDialog();
            return true;
        }

        internal bool TestSetRace(string v) {
            var p = v.Split(':');
            if (p.Length != 2 || !int.TryParse(p[1], out var n)) return false;
            LoadRaceData();
            switch (p[0]) {
                case "laps": _raceLaps = Math.Max(1, Math.Min(99, n)); break;
                case "opponents": _raceOpponents = Math.Max(1, Math.Min(23, n)); break;
                case "ai": _raceAi = Math.Max(70, Math.Min(100, n)); break;
                default: return false;
            }
            SaveRaceData();
            if (RaceOverlay.Visibility == Visibility.Visible) BuildRaceDialog();
            if (_step == Step.Go) ShowStep(Step.Go, 0);
            return true;
        }

        internal bool TestCycleTyre() { CycleTyre(); return true; }

        /// <summary>テスト用: そのままコースイン（AC を起動）。HUD の検証に使う。</summary>
        internal bool TestLaunch() {
            if (_launching) return false;
            Launch();
            return true;
        }

        internal string TestDump() {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("page=CourseEntry");
            sb.AppendLine("step=" + _step);
            sb.AppendLine("track=" + (_track?.Id ?? "(null)"));
            sb.AppendLine("layout=" + (_layout == null ? "(null)" : ShortLayoutName(_layout)) + " (of " + _layouts.Count + ")");
            sb.AppendLine("mode=" + (_mode == null ? "(null)" : _mode == DriveMode.Race ? "race" : "timetrial"));
            sb.AppendLine("time=" + _timeMinutes);
            sb.AppendLine("start=" + (_mode == DriveMode.Race ? "grid" : _timeTrialStartMode));
            LoadRaceData();
            sb.AppendLine("race=laps:" + _raceLaps + " opponents:" + _raceOpponents + " ai:" + _raceAi);
            sb.AppendLine("overlay=" + (RaceOverlay.Visibility == Visibility.Visible ? "race" : SettingOverlay.Visibility == Visibility.Visible ? "setting" : "none"));
            return sb.ToString();
        }
        #endregion

        #region UI helpers
        private static TextBlock Heading(string text) {
            return new TextBlock { Text = text, FontFamily = HeadFont, FontSize = 26, Foreground = Ivory, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.9 } };
        }

        private static void Hover(Border b, double scale) {
            var st = (ScaleTransform)b.RenderTransform;
            b.MouseEnter += (o, e) => {
                b.Background = b.Background == Gold ? Gold : CardBgHover;
                if (b.Background != Gold) b.BorderBrush = Gold;
                var a = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(140)) { EasingFunction = new SineEase() };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            };
            b.MouseLeave += (o, e) => {
                if (b.Background != Gold) { b.Background = CardBg; b.BorderBrush = CardBorder; }
                var a = new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new SineEase() };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            };
        }

        private static Border Chip(string text, bool primary, Action go) {
            var b = new Border {
                Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(16, 6, 16, 7), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand,
                Background = primary ? Gold : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderBrush = primary ? Gold : new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
            };
            b.Child = new TextBlock { Text = text, FontSize = 13, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, Foreground = primary ? Ink : Ivory };
            b.MouseEnter += (s, e) => b.Opacity = 0.85;
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; go(); };
            return b;
        }

        /// <summary>「Nurburgring - GP」→「GP」。全レイアウト名に共通する「… - 」接頭辞を落とす。</summary>
        private string ShortLayoutName(TrackObjectBase layout) {
            // [fork] 出発地点型のコースは日本語の出発地点名（ロード画面・準備画面・一発コースインの保存名も同じ名前になる）
            var start = _startPlan?.Find(layout.LayoutId);
            if (start != null) return start.Title;
            var name = layout.LayoutName ?? layout.DisplayName ?? "";
            if (_layouts.Count < 2) return name;
            var names = _layouts.Select(l => l.LayoutName ?? l.DisplayName ?? "").ToList();
            var common = names[0];
            foreach (var n in names.Skip(1)) {
                var k = 0;
                while (k < common.Length && k < n.Length && common[k] == n[k]) k++;
                common = common.Substring(0, k);
            }
            var sep = Math.Max(common.LastIndexOf(" - ", StringComparison.Ordinal), common.LastIndexOf(" – ", StringComparison.Ordinal));
            if (sep < 0) return name;
            var prefix = common.Substring(0, sep + 3);
            return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name.Substring(prefix.Length) : name;
        }

        private static string FormatLength(string s) {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim().Replace(",", "");
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return m >= 1000 ? (m / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km" : m.ToString("0", CultureInfo.InvariantCulture) + " m";
            return s;
        }
        #endregion

        private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
            switch (e.Key) {
                case Key.Escape:
                    e.Handled = true;
                    if (_launching) break;
                    if (RaceOverlay.Visibility == Visibility.Visible) { CloseRaceDialog(); break; }
                    if (SettingOverlay.Visibility == Visibility.Visible) { CloseSettingDialog(); break; }
                    Back(); break;
                case Key.S:
                    e.Handled = true;
                    if (_launching) break;
                    ToggleSettingDialog(); break;
                case Key.C:
                    // [fork] モード選択の段だけ: 乗り換え（ChangeCarFromModeStep が段とオーバーレイを見て弾く）
                    if (_step != Step.Mode) break;
                    e.Handled = true;
                    ChangeCarFromModeStep(); break;
                case Key.Enter:
                    e.Handled = true;
                    if (_launching) break;
                    // [fork] オーバーレイが開いている間の Enter（＝パッドの × の合成）でコースインしない
                    if (RaceOverlay.Visibility == Visibility.Visible || SettingOverlay.Visibility == Visibility.Visible) break;
                    if (_step == Step.Time) Next();
                    else if (_step == Step.Go) Launch();
                    else if (_step == Step.Mode && _mode != null) Next();
                    else if (_step == Step.Layout) Next();
                    break;
                case Key.Left:
                    if (_step == Step.Time) { e.Handled = true; SetTime(_timeMinutes - 30, true); }
                    else if (CycleStartMode(-1)) e.Handled = true;
                    break;
                case Key.Right:
                    if (_step == Step.Time) { e.Handled = true; SetTime(_timeMinutes + 30, true); }
                    else if (CycleStartMode(1)) e.Handled = true;
                    break;
            }
        }
    }
}
