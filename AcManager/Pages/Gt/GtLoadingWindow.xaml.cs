// [fork] GT7 風ローディングの「幕」。プライマリモニタ全面・最前面の黒い窓（3 画面なら AC の窓の左右も黒い窓で覆う・2026-09-27）。
//   AC の「Initializing DirectX」窓・CSP のロード画面・ピット画面(DRIVE待ち)を全部この裏に隠し、
//   AC 側の GtStart が cfg\gt_start.ini に STATE=preview（または menu / off）を書いたら幕を下ろす。
//   保険: Esc で手動、AC の窓が出てから 90 秒で自動、RunAsync が返ったら必ず閉じる。
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AcManager.Tools.Objects;
using AcTools.Utils;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    public partial class GtLoadingWindow {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private readonly DispatcherTimer _timer;
        private DateTime _acSeenAt;
        private bool _closing;
        private readonly string _flagPath;

        public GtLoadingWindow() {
            InitializeComponent();
            var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Left = screen.Left; Top = screen.Top; Width = screen.Width; Height = screen.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Topmost = true;
            _flagPath = System.IO.Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt_start.ini");
            _curtainPath = System.IO.Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt_start_curtain.ini");
            // 250 → 100ms: STATE=preview から幕が下りるまでの遅れを縮める（下の gt_start_curtain.ini の件）
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnTick, Dispatcher);
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Drop("Esc"); } };
            Loaded += (s, e) => { WriteCurtain("up"); CoverExpected(); Activate(); _timer.Start(); };
            Closed += (s, e) => { _timer.Stop(); CloseSides(); WriteCurtain("down"); };
        }

        // ★左右の画面の幕（2026-09-27 録画で実測: 3 画面の時、この幕は中央しか覆っていないので、ロードが終わると AC 自身の
        //   黒からの明けで左右の画面にだけピット前の景色が約 1 秒映り（3.6〜4.6 秒）、その後 GtStart の黒 → 3 面いっしょに明ける、
        //   だった。本人には「中央がワンテンポ遅れて映る」に見えていた）。AC の窓が中央からはみ出している部分を黒い窓で覆い、
        //   本体の幕と同時に下ろす。1 画面（AC の窓 = 中央）では何も出さない。4 画面（AC の窓が中央の外）は AC の窓全部を覆う
        private readonly System.Collections.Generic.List<Window> _sides = new System.Collections.Generic.List<Window>();
        private System.Drawing.Rectangle _sidesFor;

        private void CoverSides(IntPtr ac) {
            if (!GetWindowRect(ac, out var r)) return;
            var rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (rect.Width < 200 || rect.Height < 200) return;   // 起動直後の小さい窓
            _acRectSeen = true;
            CoverRect(rect);
        }

        // ★幕を出した瞬間から左右も黒く（2026-10-15 要望「スタートを押すと中央だけ先にロード画面になり、左右は後から切り替わる。
        //   3 画面同時に」）。左右の幕は AC の窓が見つかってから出していたので、それまで左右は CM7 の外＝デスクトップのままだった。
        //   AC の窓が出るまでは、画面モード（GtDisplayMode が video.ini に書くのと同じ判定）から AC の窓の位置を予想して覆う
        private bool _acRectSeen;

        private static System.Drawing.Rectangle? ExpectedAcRect() {
            try {
                switch (GtDisplayMode.Mode) {
                    case GtDisplayMode.LaunchMode.Single: return null;
                    case GtDisplayMode.LaunchMode.Triple: return new System.Drawing.Rectangle(-1920, 0, 5760, 1080);
                    case GtDisplayMode.LaunchMode.Spectator: return new System.Drawing.Rectangle(1920, 0, 5760, 1080);
                    default:
                        var a = GtDisplayTopology.Automatic();
                        return a.Triple ? a.Bounds : (System.Drawing.Rectangle?)null;
                }
            } catch (Exception ex) {
                Logging.Warning("[GtLoading] expected rect: " + ex.Message);
                return null;
            }
        }

        private void CoverExpected() {
            if (_acRectSeen) return;
            var rect = ExpectedAcRect();
            if (rect.HasValue) CoverRect(rect.Value);
        }

        private void CoverRect(System.Drawing.Rectangle rect) {
            if (rect == _sidesFor) {
                foreach (var w in _sides) PlaceSide(w, (System.Drawing.Rectangle)w.Tag);   // AC が前に出ようとしても手前を保つ
                return;
            }
            _sidesFor = rect;
            var primary = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            var parts = new System.Collections.Generic.List<System.Drawing.Rectangle>();
            if (rect.Left < primary.Left) parts.Add(System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, Math.Min(rect.Right, primary.Left), rect.Bottom));
            if (rect.Right > primary.Right) parts.Add(System.Drawing.Rectangle.FromLTRB(Math.Max(rect.Left, primary.Right), rect.Top, rect.Right, rect.Bottom));
            while (_sides.Count > parts.Count) { var w = _sides[_sides.Count - 1]; _sides.RemoveAt(_sides.Count - 1); try { w.Close(); } catch { } }
            for (var i = 0; i < parts.Count; i++) {
                if (i >= _sides.Count) {
                    var w = new Window {
                        Title = "GT Loading (side)", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        Background = System.Windows.Media.Brushes.Black, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
                        WindowStartupLocation = WindowStartupLocation.Manual, Left = parts[i].Left, Top = parts[i].Top, Width = parts[i].Width, Height = parts[i].Height
                    };
                    w.Show();
                    _sides.Add(w);
                }
                _sides[i].Tag = parts[i];
                PlaceSide(_sides[i], parts[i]);
            }
            Logging.Write("[GtLoading] AC window " + rect + " → side curtains: " + string.Join(", ", parts));
        }

        private static void PlaceSide(Window w, System.Drawing.Rectangle p) {
            // 位置は物理ピクセルで（WPF の Left/Top は DPI 次第でずれる）。HWND_TOPMOST・SWP_NOACTIVATE|SWP_SHOWWINDOW
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1), p.Left, p.Top, p.Width, p.Height, 0x0010 | 0x0040);
        }

        private void CloseSides() {
            foreach (var w in _sides) { try { w.Close(); } catch { } }
            _sides.Clear();
        }

        // ★幕の上げ下げを AC 側（GtStart）に知らせる（2026-09-27 要望「3 画面で、読み込んで準備画面に行く時に、黒い画面から
        //   3 画面同時に映してほしい。今は中央画面がワンテンポ遅れて表示される」）。幕はプライマリ＝中央の画面しか覆っていないので、
        //   GtStart が黒からの明けを始めた後に幕が下りると、左右だけ先に映っていた。GtStart は STATE=up の間は黒のまま待ち、
        //   down を読んでから 3 面いっしょに明ける（最大 2 秒で諦める）。TIME は古い up（CM7 が落ちた等）を無視するため
        private readonly string _curtainPath;

        private void WriteCurtain(string state) {
            try {
                var now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                File.WriteAllText(_curtainPath, "[CURTAIN]\r\nSTATE=" + state + "\r\nTIME=" + now + "\r\n");
            } catch (Exception ex) {
                Logging.Warning("[GtLoading] curtain " + state + ": " + ex.Message);
            }
        }

        /// <summary>コースのロゴ画像。AC の extension\lua\loading-screen\gt7\logos\&lt;trackId&gt;.png（AC 側ロード画面と共有）→ CM の Data\Static\gt7\logos\ の順。</summary>
        internal static string FindTrackLogo(string trackId) {
            if (string.IsNullOrEmpty(trackId)) return null;
            try {
                var root = AcManager.Tools.Managers.AcRootDirectory.Instance.Value;
                var candidates = new[] {
                    root == null ? null : System.IO.Path.Combine(root, "extension", "lua", "loading-screen", "gt7", "logos", trackId + ".png"),
                    System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "logos", trackId + ".png"),
                };
                return candidates.FirstOrDefault(c => c != null && File.Exists(c));
            } catch { return null; }
        }

        public void SetContent(TrackObject track, TrackObjectBase layout, string layoutName, CarObject car, string timeText) {
            var logo = FindTrackLogo(track?.Id);
            if (logo != null) {
                LoadLogo.Source = WorldMap.LoadBitmap(logo, 0);
                LoadLogo.Visibility = Visibility.Visible;
                LoadTextBlock.Visibility = Visibility.Collapsed;
            }
            LoadTrackName.Text = GtNames.TrackName(track);   // [fork] 日本語名
            LoadTrackSub.Text = string.Join("  ·  ", new[] { layoutName, GtNames.CountryJa(track?.Country), timeText }.Where(x => !string.IsNullOrEmpty(x)));
            LoadOutline.Source = WorldMap.LoadBitmap(layout?.OutlineImage, 700) ?? WorldMap.LoadBitmap(track?.OutlineImage, 700);
            LoadCarImage.Source = car == null ? null : WorldMap.LoadBitmap(car.SelectedSkin?.PreviewImage, 900);
            LoadCarName.Text = car?.DisplayName ?? "";
            LoadCarBrand.Text = (car?.Brand ?? "").ToUpperInvariant() + (car?.Year != null ? "  ·  " + car.Year : "");
        }

        public void SetStatus(string s) {
            if (!string.IsNullOrEmpty(s)) LoadStatus.Text = s;
        }

        /// <summary>幕を下ろす（AC の窓を前に出してから閉じる）。</summary>
        public void Drop(string why) {
            if (_closing) return;
            _closing = true;
            Logging.Write("[GtLoading] drop: " + why);
            try {
                Topmost = false;
                var ac = FindAcWindow();
                if (ac != IntPtr.Zero) SetForegroundWindow(ac);
            } catch { }
            CloseSides();   // 左右の幕も同時に（down を書くのは本体の Closed＝全部下りてから）
            try { Close(); } catch { }
        }

        private static IntPtr FindAcWindow() {
            var h = FindWindow(null, "Assetto Corsa");
            if (h == IntPtr.Zero) h = FindWindow("AC", null);
            return h;
        }

        private void OnTick(object sender, EventArgs e) {
            if (_closing) return;
            try {
                var state = ReadState();
                // view / done = 鑑賞モード（GtView アプリ）のカメラが付いた・戻った（2026-09-28）
                if (state == "preview" || state == "menu" || state == "off" || state == "ingame" || state == "view" || state == "done") { Drop("state=" + state); return; }
                var ac = FindAcWindow();
                if (ac != IntPtr.Zero) {
                    if (_acSeenAt == default(DateTime)) _acSeenAt = DateTime.Now;
                    else if ((DateTime.Now - _acSeenAt).TotalSeconds > 90) { Drop("timeout"); return; }
                    Topmost = true;   // AC が前に出ようとしても幕を維持
                    CoverSides(ac);   // 3 画面なら左右も
                } else if (_sides.Count > 0) {
                    foreach (var w in _sides) PlaceSide(w, (System.Drawing.Rectangle)w.Tag);   // 3 画面の点灯（CCD）後も手前を保つ
                }
            } catch (Exception ex) {
                Logging.Warning("[GtLoading] tick: " + ex.Message);
            }
        }

        private string ReadState() {
            try {
                if (!File.Exists(_flagPath)) return null;
                foreach (var line in File.ReadAllLines(_flagPath)) {
                    var t = line.Trim();
                    if (t.StartsWith("STATE=", StringComparison.OrdinalIgnoreCase)) return t.Substring(6).Trim().ToLowerInvariant();
                }
            } catch { }
            return null;
        }
    }
}
