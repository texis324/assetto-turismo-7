// [fork] GT7共通ヘッダーバー — ロゴ / 現在の愛車(プレビュー+車名+「571 PS / 1,620 kg / FR」) / PPピル / 時計。
//   車は __QuickDrive_Main の現在車(WorldMap.FindCurrentCar)。時計は30秒毎更新。
//   GT画面(SettingSheet/CourseEntry等)の最上段に置いて全画面の統一感を出す(gt7-ref layout_notes §0)。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcManager.Tools.Objects;
using AcTools.DataFile;

namespace AcManager.Pages.Gt {
    public partial class GtHeader {
        private DispatcherTimer _timer;
        private Storyboard _runnerStoryboard;
        private Window _inputWindow;
        private IInputElement _returnFocus;
        private static GtHeader _openMenuOwner;
        private static GtHeader _padToggleOwner;
        private static readonly List<GtHeader> LoadedHeaders = new List<GtHeader>();
        internal const string SettingsUri = "/Pages/Gt/GtSettings.xaml";

        /// <summary>[fork] false なら BGM パネルを引き取らない（ショールーム上の透明窓に置く写しのヘッダー用。
        /// 引き取ると窓を閉じた時にパネルが行方不明になる）。</summary>
        public bool AdoptBgmPanel { get; set; } = true;

        /// <summary>別窓へ載せたヘッダーは、ホスト全体の操作へ明示的に転送する。</summary>
        public Action CloseRequested { get; set; }
        public Action MinimizeRequested { get; set; }
        public Action SettingsRequested { get; set; }

        public GtHeader() {
            InitializeComponent();
            Loaded += (s, e) => {
                if (!LoadedHeaders.Contains(this)) LoadedHeaders.Add(this);
                GtPad.PadModeChanged -= OnPadModeChanged;
                GtPad.PadModeChanged += OnPadModeChanged;
                GtDisplayMode.Changed -= OnDisplayModeChanged;
                GtDisplayMode.Changed += OnDisplayModeChanged;
                if (_inputWindow != null) _inputWindow.PreviewKeyDown -= OnHeaderWindowKeyDown;
                _inputWindow = Window.GetWindow(this);
                if (_inputWindow != null) _inputWindow.PreviewKeyDown += OnHeaderWindowKeyDown;
                RefreshDisplayMode();
                Refresh();
                if (AdoptBgmPanel) AdoptBgm();
                if (_timer == null) {
                    _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
                    _timer.Tick += (s2, e2) => UpdateClock();
                }
                _timer.Start();
                UpdateRunnerAnimation();
            };
            IsVisibleChanged += (s, e) => {
                if (!IsVisible) ReleasePadToggle(false);
                UpdateRunnerAnimation();
            };
            Unloaded += (s, e) => {
                ReleasePadToggle(false);
                LoadedHeaders.Remove(this);
                GtPad.PadModeChanged -= OnPadModeChanged;
                GtDisplayMode.Changed -= OnDisplayModeChanged;
                if (_inputWindow != null) _inputWindow.PreviewKeyDown -= OnHeaderWindowKeyDown;
                _inputWindow = null;
                _timer?.Stop();
                StopRunnerAnimation();
                HeaderMenu.IsOpen = false;
                if (ReferenceEquals(_openMenuOwner, this)) _openMenuOwner = null;
            };
            TripleLaunchToggle.IsKeyboardFocusWithinChanged += (s, e) => {
                if (!TripleLaunchToggle.IsKeyboardFocusWithin) ReleasePadToggle(false);
            };
            BtnMin.MouseEnter += (s, e) => BtnMin.Foreground = System.Windows.Media.Brushes.White;
            BtnMin.MouseLeave += (s, e) => BtnMin.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x57, 0x5E));
            BtnClose.MouseEnter += (s, e) => BtnClose.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x50, 0x50));
            BtnClose.MouseLeave += (s, e) => BtnClose.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x57, 0x5E));
        }

        /// <summary>CmJaOverlayのBGMパネル(CmJaBgmPanel)を今のページのヘッダーへ引っ越しさせる。無ければ何もしない。</summary>
        private void AdoptBgm() {
            GtChrome.AdoptBgmInto(BgmHost);
        }

        private void RefreshDisplayMode() {
            TripleLaunchToggle.IsChecked = GtDisplayMode.ToggleValue;
            TripleLaunchToggle.Content = GtDisplayMode.DisplayLabel;
            TripleLaunchToggle.Tag = GtDisplayMode.Spectator ? "spectator" : null;   // [fork] 4画面は Tag トリガーで描く
        }

        private void OnDisplayModeChanged(object sender, EventArgs e) {
            if (!Dispatcher.CheckAccess()) {
                Dispatcher.BeginInvoke(new Action(() => {
                    if (IsLoaded) RefreshDisplayMode();
                }));
                return;
            }
            if (IsLoaded) RefreshDisplayMode();
        }

        private void OnTripleLaunchClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            GtDisplayMode.StepMode(1);   // [fork] クリックも Auto→1→3→4 の順（4画面は 3 状態 CheckBox で表せない）
            RefreshDisplayMode();
            GtSfx.PlaySelect();
        }

        private void OnHeaderWindowKeyDown(object sender, KeyEventArgs e) {
            // Handle at the host window before a page's Enter shortcut can start a race.
            if (e.Handled || !TripleLaunchToggle.IsKeyboardFocusWithin || !TripleLaunchToggle.IsEnabled
                    || (e.Key != Key.Enter && e.Key != Key.Space && e.Key != Key.Up)) return;
            e.Handled = true;
            if (e.IsRepeat) return;
            if (e.Key == Key.Up) { OpenHeaderMenu(); return; }
            GtDisplayMode.StepMode(1);
            RefreshDisplayMode();
            GtSfx.PlaySelect();
        }

        private void OnPadModeChanged() {
            if (!GtPad.PadMode) ReleasePadToggle(true);
        }

        private bool CanHandleTogglePad() {
            return IsLoaded && IsVisible && TripleLaunchToggle.IsVisible && TripleLaunchToggle.IsEnabled
                    && TripleLaunchToggle.IsHitTestVisible && _inputWindow != null && _inputWindow.IsActive
                    && PresentationSource.FromVisual(TripleLaunchToggle) != null;
        }

        private bool FocusTripleLaunchPad() {
            if (ReferenceEquals(_padToggleOwner, this)) return TripleLaunchToggle.IsKeyboardFocusWithin;
            _padToggleOwner?.ReleasePadToggle(false);
            _returnFocus = TripleLaunchToggle.IsKeyboardFocusWithin ? null : Keyboard.FocusedElement;
            if (!TripleLaunchToggle.Focus()) {
                _returnFocus = null;
                return false;
            }
            _padToggleOwner = this;
            GtFocus.OnRealMouseMove(); // Keep the page's position, but hide its second focus ring.
            return true;
        }

        private void ReleasePadToggle(bool restoreFocus) {
            if (!ReferenceEquals(_padToggleOwner, this)) return;
            _padToggleOwner = null;
            var previous = _returnFocus as UIElement;
            _returnFocus = null;
            if (!restoreFocus || !TripleLaunchToggle.IsKeyboardFocusWithin) return;
            if (previous != null && previous.IsVisible && previous.IsEnabled && previous.Focusable
                    && ReferenceEquals(Window.GetWindow(previous), _inputWindow) && previous.Focus()) return;
            for (DependencyObject parent = this; parent != null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent)) {
                var page = parent as Page;
                if (page != null && page.Focus()) return;
            }
            _inputWindow?.Focus();
            if (TripleLaunchToggle.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        }

        private static bool HandleTripleLaunchPad(GtPadCommand command) {
            var focused = Keyboard.FocusedElement;
            // GtPad's text guard only covers its key fallback; this direct shortcut needs its own.
            if (GtSettings.IsCapturingKey || focused is TextBoxBase || focused is PasswordBox || focused is ComboBox) {
                _padToggleOwner?.ReleasePadToggle(false);
                return false;
            }
            if (_padToggleOwner != null && (!_padToggleOwner.CanHandleTogglePad()
                    || !_padToggleOwner.TripleLaunchToggle.IsKeyboardFocusWithin)) {
                _padToggleOwner.ReleasePadToggle(false);
            }
            GtHeader header = null;
            for (var i = LoadedHeaders.Count - 1; i >= 0; i--) {
                var candidate = LoadedHeaders[i];
                if (!candidate.CanHandleTogglePad()) continue;
                if (candidate.TripleLaunchToggle.IsKeyboardFocusWithin) { header = candidate; break; }
                if (command == GtPadCommand.Select && header == null) header = candidate;
            }
            if (header == null) return false;
            if (command == GtPadCommand.Select) {
                if (!header.FocusTripleLaunchPad()) return false;
                GtSfx.PlayMove();
                return true;
            }
            if (!header.TripleLaunchToggle.IsKeyboardFocusWithin || !header.FocusTripleLaunchPad()) return false;
            if (command == GtPadCommand.Up) {
                header.OpenHeaderMenu();
                return true;
            }
            if (command == GtPadCommand.Start) {
                header.ReleasePadToggle(true);
                return false; // Preserve GtPad's global Start route.
            }
            if (command == GtPadCommand.Back || command == GtPadCommand.Down) {
                header.ReleasePadToggle(true);
                GtSfx.PlayCancel();
                return true;
            }
            if (command == GtPadCommand.Confirm || command == GtPadCommand.Left || command == GtPadCommand.Right) {
                GtDisplayMode.StepMode(command == GtPadCommand.Left ? -1 : 1);
                header.RefreshDisplayMode();
                GtSfx.PlaySelect();
            }
            return true; // While the header owns focus, do not operate the page behind it.
        }

        private void UpdateRunnerAnimation() {
            if (!IsLoaded || !IsVisible) {
                StopRunnerAnimation();
                return;
            }
            if (_runnerStoryboard != null) return;
            _runnerStoryboard = ((Storyboard)FindResource("RunnerCycle")).Clone();
            _runnerStoryboard.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
        }

        private void StopRunnerAnimation() {
            if (_runnerStoryboard == null) return;
            _runnerStoryboard.Remove(this);
            _runnerStoryboard = null;
        }

        private void OnLogoClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            if (HeaderMenu.IsOpen) HeaderMenu.IsOpen = false;
            else OpenHeaderMenu();
        }

        private void OpenHeaderMenu() {
            ReleasePadToggle(false);
            HeaderMenu.PlacementTarget = LogoButton;
            HeaderMenu.IsOpen = true;
            GtSfx.PlaySelect();
        }

        private void OnHeaderMenuOpened(object sender, RoutedEventArgs e) {
            if (_openMenuOwner != null && !ReferenceEquals(_openMenuOwner, this)) _openMenuOwner.HeaderMenu.IsOpen = false;
            _openMenuOwner = this;
            SettingsMenuItem.Focus();
        }

        private void OnHeaderMenuClosed(object sender, RoutedEventArgs e) {
            if (ReferenceEquals(_openMenuOwner, this)) _openMenuOwner = null;
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            HeaderMenu.IsOpen = false;
            GtSfx.PlaySelect();
            if (SettingsRequested != null) SettingsRequested();
            else WorldMap.Navigate(SettingsUri);
        }

        // A popup owns input while open; pad commands must not activate the page behind it.
        internal static bool HandleMenuPad(GtPadCommand command) {
            var header = _openMenuOwner;
            if (header == null || !header.HeaderMenu.IsOpen) return HandleTripleLaunchPad(command);
            if (command == GtPadCommand.Back || command == GtPadCommand.Start) {
                header.HeaderMenu.IsOpen = false;
                GtSfx.PlayCancel();
            } else if (command == GtPadCommand.Confirm) {
                header.SettingsMenuItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            } else {
                header.SettingsMenuItem.Focus();
            }
            return true;
        }

        private void OnMinClick(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            e.Handled = true;
            if (MinimizeRequested != null) { MinimizeRequested(); return; }
            var w = Window.GetWindow(this);
            if (w != null) SystemCommands.MinimizeWindow(w);
        }

        private void OnCloseClick(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            e.Handled = true;
            if (CloseRequested != null) { CloseRequested(); return; }
            var w = Window.GetWindow(this);
            if (w != null) SystemCommands.CloseWindow(w);
        }

        public void Refresh() {
            try {
                // [fork] 左上ロゴ=ACエンブレム(要望 2026-08-25「ACのマークに変えよう」)。無ければ従来のfork_logo
                var acLogo = System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "ac_logo.png");
                LogoImg.Source = LoadImage(System.IO.File.Exists(acLogo) ? acLogo : System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "fork_logo.png"), 68);
                var car = WorldMap.FindCurrentCar();
                if (car != null) {
                    CarName.Text = car.DisplayName;
                    CarSpecs.Text = BuildSpecLine(car);
                    CarPreview.Source = LoadImage(car.SelectedSkin?.PreviewImage, 220);
                    var pp = SettingSheet.ComputePp(car);
                    if (pp > 0) {
                        PpValue.Text = pp.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                        PpPill.Visibility = Visibility.Visible;
                    } else {
                        PpPill.Visibility = Visibility.Collapsed;
                    }
                }
            } catch {
                // ヘッダーは飾り。失敗しても画面本体を巻き込まない
            }
            UpdateClock();
        }

        /// <summary>"571 bhp"/"420 hp" → PS換算（×1.0139）した値。それ以外はそのまま。GT7の表記(PS)に合わせる。</summary>
        internal static string PowerPs(string raw) {
            if (string.IsNullOrEmpty(raw)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", ""), @"([\d.]+)\s*(\S*)");
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
            var unit = m.Groups[2].Value;
            if (unit.IndexOf("hp", StringComparison.OrdinalIgnoreCase) >= 0 || unit.IndexOf("bhp", StringComparison.OrdinalIgnoreCase) >= 0) {
                return Math.Round(v * 1.0139).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " PS";
            }
            return v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
        }

        internal static string BuildSpecLine(CarObject car) {
            string Num(string raw) {
                if (string.IsNullOrEmpty(raw)) return null;
                var m = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", ""), @"([\d.]+)\s*(\S*)");
                if (!m.Success) return null;
                if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
                var unit = m.Groups[2].Value;
                return v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
            }
            var parts = new System.Collections.Generic.List<string>();
            var power = PowerPs(GtTuning.SpecsBhp(car));    // [fork] チューニングショップのパーツ込み
            if (power != null) parts.Add(power);
            var weight = Num(GtTuning.SpecsWeight(car));
            if (weight != null) parts.Add(weight);
            try {
                var t = car.AcdData?.GetIniFile("drivetrain.ini")["TRACTION"].GetPossiblyEmpty("TYPE")?.ToUpperInvariant();
                if (t == "FWD") parts.Add("FF");
                else if (t == "RWD") parts.Add("FR");
                else if (t == "AWD" || t == "4WD") parts.Add("4WD");
                else if (!string.IsNullOrEmpty(t)) parts.Add(t);
            } catch { }
            return string.Join(" / ", parts);
        }

        private void UpdateClock() {
            var now = DateTime.Now;
            ClockTime.Text = now.ToString("H:mm");
            ClockDate.Text = now.ToString("M月d日");
            try {
                WorkoutKm.Text = GetDailyKm().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            } catch { }
        }

        /// <summary>本日の走行距離(km)。CMの通算距離のスナップショット差分方式（日付が変わったら基準を取り直す）。
        /// 統計未ロード(総距離0)の間は基準を刻まない＝初回の誤爆を防ぐ。</summary>
        internal static double GetDailyKm() {
            try {
                var total = AcManager.Tools.Profile.PlayerStatsManager.Instance.Overall.Distance;   // meters
                if (total <= 0) return 0;
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var savedDay = FirstFloor.ModernUI.Helpers.ValuesStorage.Get<string>("__GtHeader_DayStamp");
                var dayStart = FirstFloor.ModernUI.Helpers.ValuesStorage.Get("__GtHeader_DayStartDistance", -1d);
                if (savedDay != today || dayStart < 0 || dayStart > total) {
                    FirstFloor.ModernUI.Helpers.ValuesStorage.Set("__GtHeader_DayStamp", today);
                    FirstFloor.ModernUI.Helpers.ValuesStorage.Set("__GtHeader_DayStartDistance", total);
                    dayStart = total;
                }
                return Math.Max(0, (total - dayStart) / 1000.0);
            } catch {
                return 0;
            }
        }

        private static BitmapImage LoadImage(string path, int decodeWidth) {
            try {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.DecodePixelWidth = decodeWidth;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                return bi;
            } catch {
                return null;
            }
        }
    }
}
