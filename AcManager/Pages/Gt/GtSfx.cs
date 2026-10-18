// [fork] GT7 のメニュー操作音。CM7 の画面で「押せる要素」をホバー/クリックした時に鳴らす。
//   音源 = Data\Static\gt7\sfx\{ui_move,ui_select,ui_cancel}.mp3
//   （AC 側 CSP アプリ GtStart と同じファイル。GT Sport の選択音から作った3種）
//
//   仕掛け: EventManager.RegisterClassHandler で UIElement 全体の
//     PreviewMouseLeftButtonDown（クリック）と MouseEnter（ホバー）をアプリ全体で一度だけ拾う。
//     ページごとに配線しないので、ワールドマップ / 地域マップ / コースイン / ガレージ / 車選択…
//     どの画面でも自動的に鳴る。
//   ⚠ 音は Polyphony Digital の著作物。個人利用のみ。配布物には含めないこと。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal static class GtSfx {
        private const string KeyEnabled = "__GtSfx_Enabled";
        private const string KeyVolume = "__GtSfx_Volume";
        private const string KeySettingsVolume = "__GtSfx_SettingsVolume";
        public const double DefaultVolume = 0.35d;
        private const string KeyHover = "__GtSfx_Hover";
        private const string KeyDebug = "__GtSfx_Debug";

        private static bool _init;
        private static readonly Dictionary<string, List<MediaPlayer>> Pools = new Dictionary<string, List<MediaPlayer>>();
        private static readonly Dictionary<string, int> Cursor = new Dictionary<string, int>();
        private static readonly Dictionary<object, bool> Ready = new Dictionary<object, bool>();
        private static DateTime _lastHoverAt = DateTime.MinValue;
        private static object _lastHoverTarget;
        private static int _lastClickStamp = -1;

        // Pages whose actions choose select/move/cancel themselves opt out of the
        // automatic mouse-down sound. Inherit to text/image children of each control.
        public static readonly DependencyProperty HandlesClickSoundProperty = DependencyProperty.RegisterAttached(
                "HandlesClickSound", typeof(bool), typeof(GtSfx),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetHandlesClickSound(DependencyObject element) {
            return (bool)element.GetValue(HandlesClickSoundProperty);
        }

        public static void SetHandlesClickSound(DependencyObject element, bool value) {
            element.SetValue(HandlesClickSoundProperty, value);
        }

        public static bool Enabled {
            get { return ValuesStorage.Get(KeyEnabled, true); }
            set { ValuesStorage.Set(KeyEnabled, value); }
        }

        /// <summary>メニュー音のマスター音量。既定 0.35（要望 2026-08-29 の聴き比べで確定）。
        /// 0.55/0.45/0.40/0.35 は本人が差を感じず、0.28 以下で「消えた」と感じる＝下げ幅の限界が 0.35。
        /// ⚠「下げて」の正体は音量ではなく尺だった。同日 ui_select.mp3 を余韻 1.03 秒版に差し替え済み
        ///   （元は 0.44 秒。ピークは元と同一なので大きさは変わらず尾だけ伸びる。退避 = sfx\_backup\）。
        /// 独自設定で変更するまでは volume.txt / 旧保存値を引き継ぐ。
        /// 変更後は独自設定の保存値を優先し、旧ファイルは書き換えない。</summary>
        public static double Volume {
            get {
                if (ValuesStorage.Contains(KeySettingsVolume)) {
                    return ReadStoredVolume(KeySettingsVolume);
                }
                RefreshSwitches();
                return _volumeFromFile ?? ReadStoredVolume(KeyVolume);
            }
            set {
                var volume = NormalizeVolume(value);
                ValuesStorage.Set(KeySettingsVolume, volume);
                ApplyMaster();
            }
        }

        /// <summary>[fork] 鳴っている最中の音にも今の音量（個別 × 全体）を反映する。消音へ動かした時のため。
        /// 全体音量（GtMasterVolume）を動かした時もここが呼ばれる。</summary>
        internal static void ApplyMaster() {
            var volume = Volume * GtMasterVolume.Gain;
            foreach (var pool in Pools) {
                var gain = pool.Key == "ui_select" ? 1.0 : 0.9;
                foreach (var player in pool.Value) player.Volume = Math.Max(0, Math.Min(1, gain * volume));
            }
        }

        private const string KeySettingsPercent = "__GtSfx_SettingsPercent";

        /// <summary>設定画面の目盛り 0〜100（GtVolumeCurve の対数カーブ）。
        /// 目盛りで保存した値があればそれ、無ければ従来の直線保存値（0.35 など）を目盛りに換算して見せる。</summary>
        public static int VolumePercent {
            get {
                if (ValuesStorage.Contains(KeySettingsPercent)) return Math.Max(0, Math.Min(100, ValuesStorage.Get(KeySettingsPercent, DefaultPercent)));
                return GtVolumeCurve.FromGain(Volume);
            }
            set {
                var p = Math.Max(0, Math.Min(100, value));
                ValuesStorage.Set(KeySettingsPercent, p);
                Volume = GtVolumeCurve.ToGain(p);
            }
        }

        /// <summary>既定 0.35 を目盛りにしたもの（約 85%）。</summary>
        public static int DefaultPercent {
            get { return GtVolumeCurve.FromGain(DefaultVolume); }
        }

        private static double NormalizeVolume(double value) {
            return double.IsNaN(value) || double.IsInfinity(value) ? DefaultVolume : Math.Max(0, Math.Min(1, value));
        }

        private static double ReadStoredVolume(string key) {
            double value;
            return double.TryParse(ValuesStorage.Get<string>(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    ? NormalizeVolume(value) : DefaultVolume;
        }

        /// <summary>マウスを乗せただけで鳴らすか。既定 false
        /// （要望 2026-08-28「カーソルを当てただけで鳴る」＝選択が動いていないのに鳴るのは違う）。</summary>
        public static bool HoverEnabled {
            get { return ValuesStorage.Get(KeyHover, false); }
            set { ValuesStorage.Set(KeyHover, value); }
        }

        /// <summary>クリックのたびに %TEMP%\gtsfx.log へ判定経路を書く（切り分け用）。既定 false。
        /// 2026-08-28 の「鳴らない/どこでも鳴る」はこのログ1回で原因が確定した。</summary>
        /// ★`Data\Static\gt7\sfx\debug.flag` を置くだけでも on になる（ビルド不要）。
        /// ⚠切り分けが済んだら消すこと（消し忘れで 8 時間溶かした前科がある）。
        public static bool Debug {
            get {
                RefreshSwitches();
                return _debugFromFile || ValuesStorage.Get(KeyDebug, false);
            }
            set { ValuesStorage.Set(KeyDebug, value); }
        }

        // ---- [fork] 再ビルド不要の外部スイッチ（volume.txt / debug.flag）----
        private static DateTime _switchesCheckedAt = DateTime.MinValue;
        private static double? _volumeFromFile;
        private static bool _debugFromFile;

        /// <summary>sfx フォルダの調整ファイルを読み直す。クリックのたびに走るので 1 秒キャッシュ。</summary>
        private static void RefreshSwitches() {
            var now = DateTime.Now;
            if ((now - _switchesCheckedAt).TotalSeconds < 1.0) return;
            _switchesCheckedAt = now;
            try {
                var dir = SfxDir;
                _debugFromFile = File.Exists(Path.Combine(dir, "debug.flag"));
                _volumeFromFile = null;
                var volumePath = Path.Combine(dir, "volume.txt");
                if (File.Exists(volumePath)) {
                    double v;
                    if (double.TryParse(File.ReadAllText(volumePath).Trim(),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v)) {
                        _volumeFromFile = NormalizeVolume(v);
                    }
                }
            } catch { }
        }

        /// <summary>CM の Logging はファイルに出ないので自前で書く。%TEMP%\gtsfx.log</summary>
        private static void Log(string line) {
            if (!Debug) return;
            try {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gtsfx.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            } catch { }
        }

        private static string SfxDir {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Static", "gt7", "sfx"); }
        }

        /// <summary>GT ページの Loaded から呼ぶ（何度呼んでも初回だけ効く）。</summary>
        public static void Init() {
            if (_init) return;
            _init = true;
            try {
                EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewMouseLeftButtonDownEvent,
                        new MouseButtonEventHandler(OnClick), true);
                EventManager.RegisterClassHandler(typeof(UIElement), UIElement.MouseEnterEvent,
                        new MouseEventHandler(OnHover), true);
                Warm();
            } catch (Exception ex) {
                Logging.Warning("[GtSfx] init: " + ex.Message);
            }
        }

        /// <summary>最初の一発が遅れないよう、無音で開いておく。</summary>
        private static void Warm() {
            foreach (var n in new[] { "ui_move", "ui_select", "ui_cancel" }) {
                GetPool(n);
            }
        }

        private static List<MediaPlayer> GetPool(string name) {
            List<MediaPlayer> pool;
            if (Pools.TryGetValue(name, out pool)) return pool;
            pool = new List<MediaPlayer>();
            try {
                var path = Path.Combine(SfxDir, name + ".mp3");
                if (File.Exists(path)) {
                    // 同時発音数のぶんだけ MediaPlayer が要る（連打で足りなくなると鳴らない音が出る）
                    var n = name == "ui_move" ? 6 : 6;
                    for (var i = 0; i < n; i++) {
                        var p = new MediaPlayer { Volume = 0 };
                        // ★鳴り終わったら必ず Stop する。これをしないと「再生完了状態」のまま残り、
                        //   Position を戻して Play しても鳴らないことがある（＝鳴ったり鳴らなかったりの正体）
                        p.MediaEnded += (o, e) => {
                            try { ((MediaPlayer)o).Stop(); } catch { }
                        };
                        p.MediaOpened += (o, e) => {
                            Ready[o] = true;
                            Log("opened " + name);
                        };
                        p.MediaFailed += (o, e) => Log("FAILED " + name + ": " + e.ErrorException);
                        p.Open(new Uri(path));
                        pool.Add(p);
                    }
                }
            } catch (Exception ex) {
                Log("load " + name + " ERROR: " + ex.Message);
            }
            Pools[name] = pool;
            return pool;
        }

        private static void Play(string name, double gain) {
            if (!Enabled) return;
            try {
                var pool = GetPool(name);
                if (pool.Count == 0) {
                    Log("pool empty: " + name + " (" + SfxDir + ")");
                    return;
                }
                int c;
                Cursor.TryGetValue(name, out c);
                // 準備できている個体を順に探す（Open は非同期なので最初の数回は間に合わないことがある）
                MediaPlayer player = null;
                for (var i = 0; i < pool.Count; i++) {
                    var cand = pool[(c + i) % pool.Count];
                    bool ok;
                    if (Ready.TryGetValue(cand, out ok) && ok) {
                        player = cand;
                        Cursor[name] = c + i + 1;
                        break;
                    }
                }
                if (player == null) {
                    Log("no ready player: " + name);
                    return;
                }
                player.Stop();                      // ★これが要る
                player.Position = TimeSpan.Zero;
                player.Volume = Math.Max(0, Math.Min(1, gain * Volume * GtMasterVolume.Gain));   // [fork] × 全体音量
                Log("play " + name + " vol=" + player.Volume.ToString("0.000", CultureInfo.InvariantCulture));
                player.Play();
            } catch { }
        }

        public static void PlaySelect() {
            Play("ui_select", 1.0);
        }

        public static void PlayCancel() {
            Play("ui_cancel", 0.9);
        }

        /// <summary>[fork] 選択が「動いた」音。パッド操作（GtFocus）で鳴らす。
        /// HoverEnabled はマウスのホバー音の設定なので、こちらはそれとは無関係に鳴る。</summary>
        public static void PlayMove() {
            Play("ui_move", 0.9);
        }

        /// <summary>押せる要素か。CM7 の自前パーツは Border + Cursor=Hand で作ってあるのでそれも拾う。</summary>
        private static bool IsClickable(object o) {
            var el = o as UIElement;
            if (el == null || !el.IsEnabled || !el.IsVisible) return false;
            if (el is ButtonBase || el is ListBoxItem || el is ComboBoxItem || el is MenuItem || el is TabItem) return true;
            var fe = el as FrameworkElement;
            return fe != null && Equals(fe.Cursor, Cursors.Hand);
        }

        /// <summary>親を1つ辿る。
        /// ★TextBlock の文字の上をクリックすると OriginalSource が `Run`（＝ContentElement）になり、
        ///   VisualTreeHelper.GetParent(run) は **例外を投げる**（Visual ではないため）。
        ///   `??` では受けられないので、型を見て論理ツリー側へ回す。
        ///   これを踏んで「ボタンの文字の上だけ鳴らない」状態になっていた（2026-08-28 ログで確定）。</summary>
        private static DependencyObject ParentOf(DependencyObject d) {
            if (d == null) return null;
            try {
                if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) {
                    var p = VisualTreeHelper.GetParent(d);
                    if (p != null) return p;
                }
            } catch { }
            try {
                return LogicalTreeHelper.GetParent(d);
            } catch {
                return null;
            }
        }

        private static UIElement FindClickable(DependencyObject from) {
            var d = from;
            for (var i = 0; i < 14 && d != null; i++) {
                if (IsClickable(d)) return d as UIElement;
                d = ParentOf(d);
            }
            return null;
        }

        /// <summary>【現在は未使用】「地」ではなく何か具体的な部品を押したか。
        /// 一度これで判定を緩めたら、ワールドマップの背景（Image）でも鳴ってしまった。
        /// 押せる要素は必ず Cursor=Hand を持っている（ログで確認済み）ので FindClickable だけで足りる。
        /// CM7 の GT7 パーツは Border + MouseLeftButtonUp + Cursor=Hand で作ってあるが、
        /// 実際にクリックが当たるのは中の TextBlock / Image / Rectangle だったりする。
        /// Cursor=Hand を親に辿って見つけられない作りのものもあるので、こちらで拾う。</summary>
        private static bool LooksInteractive(DependencyObject src) {
            if (src == null) return false;
            if (src is Window || src is Panel || src is ScrollViewer || src is ScrollContentPresenter) return false;
            return true;
        }

        private static void OnClick(object sender, MouseButtonEventArgs e) {
            if (!Enabled) return;
            // 全 UIElement にクラスハンドラが載っているので、同じクリックで何度も呼ばれる。時刻で1回に絞る
            if (e.Timestamp == _lastClickStamp) return;
            _lastClickStamp = e.Timestamp;
            try {
                var src = e.OriginalSource as DependencyObject;
                var hit = FindClickable(src);
                var locallyHandled = hit != null && GetHandlesClickSound(hit);
                if (Debug) {
                    // 親を辿った経路をそのまま残す。どの型に Cursor=Hand が付いているかが一目で分かる
                    var chain = new List<string>();
                    var d = src;
                    for (var i = 0; i < 14 && d != null; i++) {
                        var fe = d as FrameworkElement;
                        var cur = fe == null || fe.Cursor == null ? "" : "{" + fe.Cursor + "}";
                        var nm = fe == null || string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name;
                        chain.Add(d.GetType().Name + nm + cur);
                        d = ParentOf(d);
                    }
                    Log("click " + string.Join(" < ", chain.ToArray())
                            + "  => " + (hit == null ? "SKIP" : locallyHandled ? "LOCAL" : "PLAY (" + hit.GetType().Name + ")"));
                }
                if (hit == null || locallyHandled) return;
                Play("ui_select", 1.0);
            } catch (Exception ex) {
                Log("click ERROR: " + ex.Message);
            }
        }

        private static void OnHover(object sender, MouseEventArgs e) {
            if (!Enabled || !HoverEnabled) return;
            try {
                if (!IsClickable(sender)) return;
                if (ReferenceEquals(sender, _lastHoverTarget)) return;
                var now = DateTime.Now;
                if ((now - _lastHoverAt).TotalMilliseconds < 45) return;
                _lastHoverTarget = sender;
                _lastHoverAt = now;
                Play("ui_move", 0.9);
            } catch { }
        }
    }
}
