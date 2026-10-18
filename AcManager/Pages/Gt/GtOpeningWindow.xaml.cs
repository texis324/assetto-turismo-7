// [fork] オープニングの窓。MainWindow の乗っているモニタ全面・最前面の黒窓。
//   フェーズ: Intro（ロゴ演出 約 4.8 秒: 0.3s 黒 → ロゴ 1.4s フェードイン＋3s でわずかに縮む → 3.8s から 0.8s フェードアウト）
//           → Movie（MediaElement・黒から 0.7s フェードイン・終わりは 0.5s で黒＆無音へ）→ 閉じる。
//   スキップ: 何かキー（修飾キー単独は除く）／マウス／パッド（GtPad の OpeningSkip → GtOpening.Skip）。
//            演出中は演出だけ飛ばしてムービーへ。Esc は全部飛ばす。
//   再生中は CmJaOverlay の BGM を止め、閉じたら元に戻す（DLL 側の private static Play/Pause をリフレクションで。DLL 無しなら何もしない）。
//   音量 = GtOpening.Volume（設定 0〜100）。
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    public partial class GtOpeningWindow {
        private enum Phase { Idle, Intro, Movie, Closing }

        private const double IntroFadeOutAt = 3.8;   // 秒
        private const double IntroEndAt = 4.8;

        private readonly string _why;
        private readonly bool _intro;
        private readonly bool _movie;
        private readonly DispatcherTimer _guard;
        private Phase _phase = Phase.Idle;
        private bool _started;
        private bool _bgmWasPlaying;
        private DateTime _shownAt;
        private DateTime _introAt;
        private bool _introFadingOut;
        private MediaPlayer _introSound;
        private double _introSoundVolume;
        private readonly Window _owner;

        public GtOpeningWindow(Window owner, string why, bool intro, bool movie) {
            InitializeComponent();
            _why = why;
            _intro = intro;
            _movie = movie;
            _owner = owner;
            Rect r;
            try {
                // 起動時は MainWindow の SourceInitialized（HWND はあるが未ロード）から来るので、ハンドルがあればそのモニタに合わせる
                var hasHandle = owner != null && new System.Windows.Interop.WindowInteropHelper(owner).Handle != IntPtr.Zero;
                r = hasHandle ? GtBorderless.ScreenBounds(owner) : PrimaryBounds();
            } catch {
                r = PrimaryBounds();
            }
            Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            _guard = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnGuard, Dispatcher);
            _guard.Stop();
            Loaded += OnLoaded;
            Closed += OnClosed;
            // 後から出てくる MainWindow が前面を取ってもキー入力はこちらで受けたい → 親が活性化したら取り返す
            if (owner != null) owner.Activated += OnOwnerActivated;
            // 親がロード後にボーダーレスで別モニタへ移ることがある → ロードされたらモニタを合わせ直す
            if (owner != null && !owner.IsLoaded) owner.Loaded += OnOwnerLoaded;
            PreviewKeyDown += (s, e) => {
                if (IsModifier(e.Key)) return;
                e.Handled = true;
                if (e.Key == Key.Escape) Finish("skip all (Esc)", true);
                else Skip("key " + e.Key);
            };
            PreviewMouseDown += (s, e) => { e.Handled = true; Skip("mouse"); };
        }

        private void OnOwnerLoaded(object sender, RoutedEventArgs e) {
            if (_owner != null) _owner.Loaded -= OnOwnerLoaded;
            if (_phase == Phase.Closing) return;
            Dispatcher.BeginInvoke(new Action(() => {
                try {
                    var r = GtBorderless.ScreenBounds(_owner);
                    if (Math.Abs(r.Left - Left) > 1 || Math.Abs(r.Top - Top) > 1 || Math.Abs(r.Width - Width) > 1 || Math.Abs(r.Height - Height) > 1) {
                        Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
                        Logging.Write("[GtOpening] moved to owner's monitor: " + r);
                    }
                } catch { }
            }), DispatcherPriority.Loaded);
        }

        private void OnOwnerActivated(object sender, EventArgs e) {
            if (_phase == Phase.Closing || !IsVisible) return;
            Dispatcher.BeginInvoke(new Action(() => {
                if (_phase == Phase.Closing || !IsVisible) return;
                try { Topmost = true; Activate(); Focus(); } catch { }
            }), DispatcherPriority.Background);
        }

        private static Rect PrimaryBounds() {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return new Rect(b.Left, b.Top, b.Width, b.Height);
        }

        private static bool IsModifier(Key k) {
            return k == Key.LeftShift || k == Key.RightShift || k == Key.LeftCtrl || k == Key.RightCtrl
                    || k == Key.LeftAlt || k == Key.RightAlt || k == Key.LWin || k == Key.RWin || k == Key.System
                    || k == Key.CapsLock || k == Key.NumLock || k == Key.Scroll || k == Key.ImeProcessed || k == Key.None;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) {
            _shownAt = DateTime.Now;
            try { Activate(); Focus(); } catch { }
            BgmPause();
            _guard.Start();
            if (_intro) StartIntro();
            else if (_movie) StartMovie();
            else Finish("nothing to play", false);
        }

        // ---- ロゴ演出 -------------------------------------------------------------------------
        private void StartIntro() {
            _phase = Phase.Intro;
            _introAt = DateTime.Now;
            _introFadingOut = false;
            try {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(GtOpening.IntroImagePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                IntroImage.Source = bmp;
            } catch (Exception ex) {
                Logging.Warning("[GtOpening] intro image: " + ex.Message);
                IntroDone("intro image failed");
                return;
            }
            Fade.Opacity = 0;   // 地は黒なので幕は要らない
            IntroImage.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1400)) { BeginTime = TimeSpan.FromMilliseconds(300) };
            IntroImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            var shrink = new DoubleAnimation(1.04, 1.0, TimeSpan.FromMilliseconds(3200)) {
                BeginTime = TimeSpan.FromMilliseconds(300), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            IntroScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            IntroScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            try {
                if (System.IO.File.Exists(GtOpening.IntroSoundPath)) {
                    _introSoundVolume = GtOpening.Volume;
                    _introSound = new MediaPlayer { Volume = _introSoundVolume };
                    _introSound.Open(new Uri(GtOpening.IntroSoundPath, UriKind.Absolute));
                    var t = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (s, e) => {
                        ((DispatcherTimer)s).Stop();
                        try { if (_phase == Phase.Intro && _introSound != null) _introSound.Play(); } catch { }
                    }, Dispatcher);
                    t.Start();
                }
            } catch (Exception ex) {
                Logging.Warning("[GtOpening] intro sound: " + ex.Message);
            }
            Logging.Write("[GtOpening] intro (" + _why + ")");
        }

        private void IntroTick(double elapsed) {
            if (!_introFadingOut && elapsed >= IntroFadeOutAt) {
                _introFadingOut = true;
                IntroImage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(800)));
            }
            // 音は最後の 1 秒でなだらかに下げる（クリップ自体もフェードアウト済みだが、演出の終わりに合わせる）
            if (_introSound != null && elapsed >= IntroEndAt - 1.0) {
                var k = Math.Max(0, (IntroEndAt - elapsed) / 1.0);
                try { _introSound.Volume = _introSoundVolume * k; } catch { }
            }
            if (elapsed >= IntroEndAt) IntroDone("intro ended");
        }

        private void IntroDone(string why) {
            if (_phase != Phase.Intro) return;
            StopIntroSound();
            IntroImage.BeginAnimation(UIElement.OpacityProperty, null);
            IntroImage.Opacity = 0;
            IntroImage.Visibility = Visibility.Collapsed;
            Logging.Write("[GtOpening] " + why + " after " + (DateTime.Now - _introAt).TotalSeconds.ToString("0.0") + "s");
            if (_movie) StartMovie();
            else { _phase = Phase.Idle; Finish(why + " (no movie)", false); }
        }

        private void StopIntroSound() {
            var p = _introSound;
            _introSound = null;
            if (p == null) return;
            try { p.Stop(); p.Close(); } catch { }
        }

        // ---- ムービー -------------------------------------------------------------------------
        private void StartMovie() {
            _phase = Phase.Movie;
            Fade.Opacity = 1;
            try {
                Player.Volume = GtOpening.Volume;
                Player.Source = new Uri(GtOpening.MoviePath, UriKind.Absolute);
                Player.Play();
                _started = true;
                Logging.Write("[GtOpening] play (" + _why + "): " + GtOpening.MoviePath + " vol=" + GtOpening.VolumePercent + "%");
            } catch (Exception ex) {
                Finish("failed: " + ex.Message, false);
                return;
            }
            // 黒からフェードイン。ヒントは 2 秒後に薄く出す
            Fade.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700)));
            var hint = new DoubleAnimation(0, 0.4, TimeSpan.FromMilliseconds(800)) { BeginTime = TimeSpan.FromSeconds(2) };
            Hint.BeginAnimation(UIElement.OpacityProperty, hint);
        }

        private void OnMediaOpened(object sender, RoutedEventArgs e) {
            try {
                Logging.Write("[GtOpening] media opened: " + (Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.ToString() : "?")
                        + " " + Player.NaturalVideoWidth + "x" + Player.NaturalVideoHeight);
            } catch { }
        }

        private void OnMediaEnded(object sender, RoutedEventArgs e) { if (_phase == Phase.Movie) Finish("ended", true); }

        private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e) {
            Finish("failed: " + (e.ErrorException?.Message ?? "?"), false);
        }

        /// <summary>スキップ。演出中なら演出だけ飛ばしてムービーへ、ムービー中なら黒にフェードして閉じる。</summary>
        public void Skip(string why) {
            if (_phase == Phase.Intro) {
                // 演出は短くフェードアウトしてからムービーへ
                if (_introFadingOut && (DateTime.Now - _introAt).TotalSeconds >= IntroFadeOutAt + 0.3) { IntroDone("intro skip (" + why + ")"); return; }
                _introFadingOut = true;
                IntroImage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
                var t = new DispatcherTimer(TimeSpan.FromMilliseconds(260), DispatcherPriority.Normal, (s, e) => {
                    ((DispatcherTimer)s).Stop();
                    IntroDone("intro skip (" + why + ")");
                }, Dispatcher);
                t.Start();
                return;
            }
            Finish("skip (" + why + ")", true);
        }

        private void Finish(string why, bool fade) {
            if (_phase == Phase.Closing) return;
            _phase = Phase.Closing;
            _guard.Stop();
            StopIntroSound();
            GtOpening.LastResult = why;
            Logging.Write("[GtOpening] " + why + " after " + (DateTime.Now - _shownAt).TotalSeconds.ToString("0.0") + "s");
            if (!fade) { try { Close(); } catch { } return; }
            var d = TimeSpan.FromMilliseconds(500);
            try { Player.BeginAnimation(System.Windows.Controls.MediaElement.VolumeProperty, new DoubleAnimation(0, d)); } catch { }
            var a = new DoubleAnimation(1, d);
            a.Completed += (s, e) => { try { Close(); } catch { } };
            Fade.BeginAnimation(UIElement.OpacityProperty, a);
        }

        private void OnClosed(object sender, EventArgs e) {
            _guard.Stop();
            if (_owner != null) { _owner.Activated -= OnOwnerActivated; _owner.Loaded -= OnOwnerLoaded; }
            StopIntroSound();
            try { Player.Stop(); Player.Close(); } catch { }
            BgmResume();
            try { _owner?.Activate(); } catch { }
        }

        private void OnGuard(object sender, EventArgs e) {
            if (_phase == Phase.Closing) return;
            try {
                if (_phase == Phase.Intro) IntroTick((DateTime.Now - _introAt).TotalSeconds);
                // BGM が後から鳴り出した（起動時は DLL の初期化がこの窓より後）ら止める（型はキャッシュ済みなので毎 tick でも軽い）
                if (BgmIsPlaying()) BgmPause();
                // 万一 MediaEnded が来なくても、尺を 3 秒過ぎたら閉じる
                if (_phase == Phase.Movie && _started && Player.NaturalDuration.HasTimeSpan
                        && Player.Position >= Player.NaturalDuration.TimeSpan + TimeSpan.FromSeconds(3)) Finish("guard", true);
            } catch { }
        }

        // ---- CmJaOverlay.Bgm（private static Play/Pause/_playing）------------------------------
        private static Type _bgmType;

        private static Type BgmType() {
            if (_bgmType != null) return _bgmType;
            try {
                _bgmType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => { try { return a.GetType("CmJaOverlay.Bgm", false); } catch { return null; } })
                        .FirstOrDefault(t => t != null);
            } catch { }
            return _bgmType;
        }

        private static bool BgmIsPlaying() {
            try {
                var f = BgmType()?.GetField("_playing", BindingFlags.NonPublic | BindingFlags.Static);
                return f != null && (bool)f.GetValue(null);
            } catch { return false; }
        }

        private void BgmPause() {
            try {
                if (!BgmIsPlaying()) return;
                BgmType()?.GetMethod("Pause", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
                _bgmWasPlaying = true;
                Logging.Write("[GtOpening] bgm paused");
            } catch (Exception ex) {
                Logging.Warning("[GtOpening] bgm pause: " + ex.Message);
            }
        }

        private void BgmResume() {
            if (!_bgmWasPlaying) return;
            _bgmWasPlaying = false;
            try {
                BgmType()?.GetMethod("Play", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
                Logging.Write("[GtOpening] bgm resumed");
            } catch (Exception ex) {
                Logging.Warning("[GtOpening] bgm resume: " + ex.Message);
            }
        }
    }
}
