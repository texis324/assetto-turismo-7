// [fork] CM7 オープニング。GT7 と同じく起動時に「ロゴ演出（黒→ロゴがフェードイン→フェードアウト）」→「ムービー全画面」。
//   何かキー／クリック／パッドでスキップ（演出中は演出だけ飛ばしてムービーへ・Esc は全部飛ばす）。
//   設定（CM7 設定 → 表示）: ムービー オン/オフ・ロゴ演出 オン/オフ・音量。どちらもオフなら起動時に何も出ない。
//   素材 = Data\Static\gt7\opening.mp4（ムービー）・opening_intro.png（ロゴ）・opening_intro.mp3（演出の音）。無ければその部分は省く。
//   窓の実体は GtOpeningWindow。テストチャンネル: opening=play|intro|skip|on|off|intro:on|off|volume:NN
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal static class GtOpening {
        private const string KeyEnabled = "__GtOpening";
        private const string KeyIntro = "__GtOpeningIntro";
        private const string KeyVolume = "__GtOpeningVolume";
        private const string KeyVolumeCurve = "__GtOpeningVolumeCurve";   // 2 = GtVolumeCurve の目盛りで保存済み
        /// <summary>既定 43%（= 旧・直線 2%・約 −34 dB。要望 2026-09-12「ちょうどよかったのが 2%」）。</summary>
        public const int DefaultVolumePercent = 43;
        private static GtOpeningWindow _win;
        private static bool _startupDone;
        private static MediaPlayer _preview;

        /// <summary>起動時にムービーを再生するか（既定 ON・GT7 と同じ）。</summary>
        public static bool Enabled {
            get { return ValuesStorage.Get(KeyEnabled, true); }
            set { ValuesStorage.Set(KeyEnabled, value); }
        }

        /// <summary>起動時のロゴ演出（既定 ON）。オフなら起動が少し速い。</summary>
        public static bool IntroEnabled {
            get { return ValuesStorage.Get(KeyIntro, true); }
            set { ValuesStorage.Set(KeyIntro, value); }
        }

        /// <summary>ムービーと演出の音量の目盛り 0〜100（GtVolumeCurve・既定 43）。
        /// 旧版（直線 p/100・既定 60）の保存値は初回に目盛りへ変換する。</summary>
        public static int VolumePercent {
            get {
                if (ValuesStorage.Get(KeyVolumeCurve, 0) != 2) {
                    if (ValuesStorage.Contains(KeyVolume)) {
                        var old = Math.Max(0, Math.Min(100, ValuesStorage.Get(KeyVolume, 60)));
                        var migrated = GtVolumeCurve.FromGain(old / 100.0);
                        ValuesStorage.Set(KeyVolume, migrated);
                        Logging.Write("[GtOpening] volume migrated: linear " + old + "% -> " + migrated + "%");
                    }
                    ValuesStorage.Set(KeyVolumeCurve, 2);
                }
                return Math.Max(0, Math.Min(100, ValuesStorage.Get(KeyVolume, DefaultVolumePercent)));
            }
            set {
                ValuesStorage.Set(KeyVolumeCurve, 2);
                ValuesStorage.Set(KeyVolume, Math.Max(0, Math.Min(100, value)));
            }
        }

        /// <summary>MediaElement/MediaPlayer に渡す 0〜1（対数カーブ）。[fork] 全体音量（GtMasterVolume）を掛けた値。
        /// ロゴ演出・ムービー・試聴のどれもここを通る。</summary>
        public static double Volume {
            get { return GtVolumeCurve.ToGain(VolumePercent) * GtMasterVolume.Gain; }
        }

        private static string StaticPath(string name) {
            return Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", name);
        }

        public static string MoviePath { get { return StaticPath("opening.mp4"); } }
        public static string IntroImagePath { get { return StaticPath("opening_intro.png"); } }
        public static string IntroSoundPath { get { return StaticPath("opening_intro.mp3"); } }

        public static bool Available { get { try { return File.Exists(MoviePath); } catch { return false; } } }
        public static bool IntroAvailable { get { try { return File.Exists(IntroImagePath); } catch { return false; } } }

        public static bool IsShowing {
            get { return _win != null && _win.IsVisible; }
        }

        /// <summary>直近の結果（テスト/ログ用）: disabled / no file / ended / skip (...) / failed ...</summary>
        public static string LastResult = "-";

        /// <summary>MainWindow の OnLoadedOverride から一度だけ。両方 OFF・素材無し・2 回目以降は何もしない。</summary>
        public static void ShowAtStartup(Window owner) {
            if (_startupDone) return;
            _startupDone = true;
            var intro = IntroEnabled && IntroAvailable;
            var movie = Enabled && Available;
            if (!intro && !movie) {
                LastResult = !Enabled && !IntroEnabled ? "disabled" : !Enabled ? "movie disabled, no intro file" : "no file";
                Logging.Write("[GtOpening] startup: " + LastResult + " (movie=" + Enabled + "/" + Available + " intro=" + IntroEnabled + "/" + IntroAvailable + ")");
                return;
            }
            Play(owner, "startup", intro, movie);
        }

        /// <summary>再生開始（既に出ていれば false）。intro/movie を省略すると設定どおり。</summary>
        public static bool Play(Window owner, string why, bool? intro = null, bool? movie = null) {
            if (IsShowing) return false;
            var doIntro = (intro ?? IntroEnabled) && IntroAvailable;
            var doMovie = (movie ?? true) && Available;
            if (!doIntro && !doMovie) { LastResult = "no file"; return false; }
            try {
                StopPreview();
                var w = new GtOpeningWindow(owner, why, doIntro, doMovie);
                w.Closed += (s, e) => { if (ReferenceEquals(_win, w)) _win = null; };
                _win = w;
                LastResult = "playing (" + why + (doIntro ? " intro" : "") + (doMovie ? " movie" : "") + ")";
                w.Show();
                return true;
            } catch (Exception ex) {
                LastResult = "error " + ex.Message;
                Logging.Warning("[GtOpening] play: " + ex);
                _win = null;
                return false;
            }
        }

        /// <summary>スキップ（出ていなければ false）。演出中なら演出だけ、ムービー中なら閉じる。</summary>
        public static bool Skip() {
            var w = _win;
            if (w == null || !w.IsVisible) return false;
            w.Skip("request");
            return true;
        }

        /// <summary>設定画面の「音を確認」: 演出の音だけを今の音量で鳴らす。</summary>
        public static bool PreviewSound() {
            try {
                StopPreview();
                if (!File.Exists(IntroSoundPath)) return false;
                _preview = new MediaPlayer { Volume = Volume };
                _preview.Open(new Uri(IntroSoundPath, UriKind.Absolute));
                _preview.MediaEnded += (s, e) => StopPreview();
                _preview.Play();
                return true;
            } catch (Exception ex) {
                Logging.Warning("[GtOpening] preview sound: " + ex.Message);
                return false;
            }
        }

        public static void StopPreview() {
            var p = _preview;
            _preview = null;
            if (p == null) return;
            try { p.Stop(); p.Close(); } catch { }
        }
    }
}
