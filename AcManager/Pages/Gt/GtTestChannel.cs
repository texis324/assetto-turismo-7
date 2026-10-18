// [fork] CM7 テストチャンネル — Claude の自動検証用の裏口。UI の見た目には一切影響しない。
//   Data\cm7_test_enabled.flag がある時だけ起動。Data\cm7_test.ini に書かれたコマンドを実行して
//   Data\cm7_test_result.txt に結果と状態を書き出す（コマンドファイルは実行後に消す）。
//
// コマンド（1行1つ・; で始まる行はコメント）:
//   goto=title|worldmap|worldcircuits|settingsheet|gallery|tuningshop|brandcentral|region:eu|course:ks_nurburgring,eu
//   brand=<メーカー名> / pick=<idx|carId> / bc=confirm|cancel|back|zukan|showcase|watch|region:<jp|eu|am>   …ブランドセントラルの操作
//   garage=add:<carId>|remove:<carId>|list|reload   所有リスト(GtGarage)の操作。add は納車待ちにも積む
//   picker=open|close|select:<carId>   GT7「乗り換え」画面を開く/閉じる/所有車を選択。開いている間 shot= はこちらを撮る
//   screen=<pngパス>         メインウィンドウのあるモニタ全体を画面キャプチャ（ショールーム(別HWND)や透明窓を撮る時。モニタが点いている時だけ）
//   garage=open              ガレージ(ショールーム)へ入る
//   slides=show|hide         放置スライドを強制表示/解除
//   click=<x>,<y>            CM ウィンドウ内の座標を「本物のマウス」でクリック（＝放置タイマもリセットされる）
//   move=<x>,<y>             本物のマウスを動かすだけ（放置スライド解除に使う）
//   key=<vk16進>             メインウィンドウへキー送信（例 key=1B は Esc）
//   pad=<up|down|left|right|confirm|back|start|select|l1|r1|triangle|square|l3|r3>
//                            パッド入力を注入（XInput とゲートを飛ばして GtPad.Dispatch へ）
//   pad=<cmd>x<n>            n 回連続（例 pad=downx3）
//   mode=race|timetrial / layout=<idx|名> / time=<分> / next / back    …CourseEntry の操作
//   open=setting|race / close / race=laps:5|opponents:8|ai:95 / tyre
//   wait=<秒>                次のコマンドまで待つ（最大30秒）
//   dump                     状態を書き出す（毎回自動で付くので明示不要）
//   shot=<pngパス>           メインウィンドウを CM 内部で描画して PNG に保存（RenderTargetBitmap。
//                            ★モニタがスリープして PrintWindow が黒になる夜間でも撮れる。前面化もしない）
//   header=menu|settings|close|back   ヘッダーの実際の RoutedEvent を発火（OS の入力は送らない）
//   oheader=menu|settings|min|close   ガレージ/ブランドセントラルの透明窓にあるヘッダーを同じ方法で押す
//   settings=volume:<0..100>|preview|reset|decrease|increase   独自設定の実コントロールを操作
//   settings=keyboard:tab|sound|assist:<0..100>|capture:<action>|key:<VK>|cancel|reset
//   settings=opening:tab / settings=title:on|off   表示タブとタイトル画面の表示設定
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows.Media;

namespace AcManager.Pages.Gt {
    internal static class GtTestChannel {
        private static DispatcherTimer _timer;
        internal static CourseEntry ActiveCourseEntry;
        internal static RegionMap ActiveRegionMap;

        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

        private static string FlagPath => Path.Combine(EntryPoint.ApplicationDataDirectory, "cm7_test_enabled.flag");
        private static string CmdPath => Path.Combine(EntryPoint.ApplicationDataDirectory, "cm7_test.ini");
        private static string ResultPath => Path.Combine(EntryPoint.ApplicationDataDirectory, "cm7_test_result.txt");

        internal static bool DiagnosticsEnabled {
            get {
                try { return File.Exists(FlagPath); } catch { return false; }
            }
        }

        public static void Start() {
            if (_timer != null) return;
            try { if (!File.Exists(FlagPath)) return; } catch { return; }
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += OnTick;
            _timer.Start();
            Logging.Write("[GtTestChannel] active");
        }

        private static void OnTick(object sender, EventArgs e) {
            string[] lines;
            try {
                if (!File.Exists(CmdPath)) return;
                lines = File.ReadAllLines(CmdPath);
                File.Delete(CmdPath);
            } catch {
                return;
            }
            var sb = new StringBuilder();
            foreach (var raw in lines) {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                try {
                    sb.AppendLine((Execute(line) ? "ok " : "fail ") + line);
                } catch (Exception ex) {
                    sb.AppendLine("error " + line + " :: " + ex.Message);
                }
            }
            sb.AppendLine("-- state --");
            try {
                sb.Append(Dump());
            } catch (Exception ex) {
                sb.AppendLine("dump error: " + ex.Message);
            }
            try { File.WriteAllText(ResultPath, sb.ToString()); } catch { }
        }

        /// <summary>今どの画面で、どの窓が出ているか。スクショを見なくても判定できるようにする。</summary>
        private static string Dump() {
            var sb = new StringBuilder();
            var main = Application.Current?.MainWindow as AcManager.Pages.Windows.MainWindow;
            sb.AppendLine("page=" + (main?.CurrentSource?.OriginalString ?? "(none)"));
            var wins = new List<string>();
            try {
                foreach (Window w in Application.Current.Windows) {
                    if (!w.IsVisible) continue;
                    var n = w.GetType().Name;
                    var t = w.Title;
                    wins.Add(n == "MainWindow" ? n : (string.IsNullOrEmpty(t) ? n : n + "(" + t + ")"));
                }
            } catch { }
            sb.AppendLine("windows=" + string.Join(", ", wins));
            sb.AppendLine("acs=" + (System.Diagnostics.Process.GetProcessesByName("acs").Length > 0 ? "running" : "no"));
            sb.AppendLine("slides=" + (GtIdleSlides.IsShowing ? "showing" : "hidden") + " (" + GtIdleSlides.Reason + ")");
            sb.AppendLine("title=enabled:" + TitleScreen.Enabled + " startup:" + TitleScreen.StartupPageUri.OriginalString);
            sb.AppendLine("opening=" + (GtOpening.IsShowing ? "showing" : "hidden") + " enabled:" + GtOpening.Enabled
                    + " file:" + GtOpening.Available + " intro:" + GtOpening.IntroEnabled + " introfile:" + GtOpening.IntroAvailable
                    + " volume:" + GtOpening.VolumePercent + " last:" + GtOpening.LastResult);
            sb.AppendLine("pad=" + GtPad.Reason + " dev=" + GtPad.DeviceInfo);
            sb.AppendLine("display=triple:" + GtDisplayMode.Enabled);
            sb.AppendLine("displayMode=" + GtDisplayMode.Mode);
            sb.AppendLine("prelight=" + GtDisplayMode.PrelightStatus);   // [fork] 4画面モードの先点け
            sb.AppendLine(GtFocus.Dump());
            try {
                var car = WorldMap.FindCurrentCar();
                sb.AppendLine("car=" + (car?.Id ?? "(none)") + " pp=" + SettingSheet.ComputePp(car).ToString("0.00"));
            } catch { }
            try { sb.AppendLine(GtGarage.Dump()); } catch { }
            try { var bc = BrandCentral.Active; if (bc != null) sb.Append(bc.TestDump()); } catch { }
            try {
                var header = FindHeader();
                if (header != null) sb.AppendLine("header=menu:" + ((ContextMenu)header.FindName("HeaderMenu")).IsOpen
                        + " bgm:" + ((Panel)header.FindName("BgmHost")).Children.Count);
            } catch { }
            var ce = ActiveCourseEntry;
            try {
                var settings = Application.Current?.MainWindow?.FindVisualChild<GtSettings>();
                if (settings != null && settings.IsLoaded) {
                    sb.Append(settings.TestKeyboardDump());
                    sb.Append(settings.TestDisplayDump());
                    sb.AppendLine("settings=volume:"
                        + ((Slider)settings.FindName("VolumeSlider")).Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " effective:" + GtSfx.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " enabled:" + GtSfx.Enabled
                        + " master:" + GtMasterVolume.Percent + "% gain:" + GtMasterVolume.Gain.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
                        + " masterSlider:" + (int)((Slider)settings.FindName("MasterVolumeSlider")).Value);
                }
            } catch { }
            if (ce != null) sb.Append(ce.TestDump());
            var rm = ActiveRegionMap;
            if (rm != null) { try { sb.Append(rm.TestDumpCards()); } catch { } }
            return sb.ToString();
        }

        private static bool Execute(string line) {
            var i = line.IndexOf('=');
            var key = i < 0 ? line : line.Substring(0, i);
            var value = i < 0 ? "" : line.Substring(i + 1);
            switch (key) {
                case "goto":
                    switch (value.Split(':')[0]) {
                        case "title": WorldMap.Navigate(TitleScreen.PagePath); return true;
                        case "worldmap": WorldMap.Navigate("/Pages/Gt/WorldMap.xaml"); return true;
                        case "worldcircuits": WorldMap.Navigate("/Pages/Gt/WorldCircuits.xaml"); return true;
                        case "gallery": WorldMap.Navigate("/Pages/Gt/Gallery.xaml"); return true;
                        case "settingsheet": WorldMap.Navigate("/Pages/Gt/SettingSheet.xaml"); return true;
                        case "tuningshop": WorldMap.Navigate("/Pages/Gt/TuningShop.xaml"); return true;
                        case "brandcentral": WorldMap.Navigate("/Pages/Gt/BrandCentral.xaml"); return true;
                        case "region":
                            WorldMap.Navigate("/Pages/Gt/RegionMap.xaml?Region=" + Uri.EscapeDataString(After(value)));
                            return true;
                        case "course": {
                            var p = After(value).Split(',');
                            WorldMap.Navigate("/Pages/Gt/CourseEntry.xaml?Track=" + Uri.EscapeDataString(p[0])
                                    + "&Region=" + Uri.EscapeDataString(p.Length > 1 ? p[1] : "eu"));
                            return true;
                        }
                    }
                    return false;
                case "garage":
                    if (value == "open") { GarageMode.Enter(); return true; }
                    if (value == "list" || value == "reload") { if (value == "reload") GtGarage.Reload(); return GtGarage.Count >= 0; }
                    if (value.StartsWith("add:")) { var c = AcManager.Tools.Managers.CarsManager.Instance.GetById(value.Substring(4)); return c != null && GtGarage.Add(c); }
                    if (value.StartsWith("remove:")) { var c = AcManager.Tools.Managers.CarsManager.Instance.GetById(value.Substring(7)); string why = null; var ok = c != null && GtGarage.Remove(c, out why); if (!ok) Logging.Write("[GtTestChannel] remove: " + why); return ok; }
                    if (value == "close") {
                        var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CmJaOverlay.Viewer", false)).FirstOrDefault(x => x != null);
                        var m = t?.GetMethod("CloseShowroom", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        return m != null && (bool)m.Invoke(null, null);
                    }
                    return false;
                case "opening": {
                    if (value == "play") return GtOpening.Play(Application.Current?.MainWindow, "test");
                    if (value == "intro") return GtOpening.Play(Application.Current?.MainWindow, "test-intro", intro: true, movie: false);
                    if (value == "movie") return GtOpening.Play(Application.Current?.MainWindow, "test-movie", intro: false, movie: true);
                    if (value == "skip") return GtOpening.Skip();
                    if (value == "on" || value == "off") { GtOpening.Enabled = value == "on"; return GtOpening.Enabled == (value == "on"); }
                    if (value == "intro:on" || value == "intro:off") { GtOpening.IntroEnabled = value == "intro:on"; return GtOpening.IntroEnabled == (value == "intro:on"); }
                    int vol;
                    if (value.StartsWith("volume:") && int.TryParse(value.Substring(7), out vol)) { GtOpening.VolumePercent = vol; return GtOpening.VolumePercent == Math.Max(0, Math.Min(100, vol)); }
                    if (value == "sound") return GtOpening.PreviewSound();
                    return false;
                }
                case "slides":
                    if (value == "show") { GtIdleSlides.ForceShow(); return true; }
                    if (value == "hide") { GtIdleSlides.ForceHide(); return true; }
                    return false;
                case "click": return RealMouse(value, true);
                case "move": return RealMouse(value, false);
                case "key": {
                    var main = Application.Current?.MainWindow;
                    if (main == null) return false;
                    var h = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                    var vk = Convert.ToInt32(value, 16);
                    SetForegroundWindow(h);
                    PostMessage(h, 0x0100, (IntPtr)vk, IntPtr.Zero);
                    PostMessage(h, 0x0101, (IntPtr)vk, (IntPtr)unchecked((int)0xC0000000));   // x86: uint→IntPtr は溢れる
                    return true;
                }
                // [fork] wait は「呼び出し側(PowerShell)が待つ」もの。ここで待つと UI スレッドを入れ子ポンプで
                //   止めてしまい、テストチャンネル自体が死ぬ（2026-08-26 に踏んだ）。互換のため受理だけする。
                case "wait": return true;
                case "mode": return ActiveCourseEntry?.TestSelectMode(value) ?? false;
                case "layout": return ActiveCourseEntry?.TestSelectLayout(value) ?? false;
                case "time": return ActiveCourseEntry?.TestSetTime(int.Parse(value)) ?? false;
                case "startmode": return ActiveCourseEntry?.TestSetStartMode(value) ?? false;
                case "triple":
                    if (value != "on" && value != "off" && value != "auto" && value != "spectator") return false;
                    if (value == "auto") GtDisplayMode.Mode = GtDisplayMode.LaunchMode.Automatic;
                    else if (value == "spectator") GtDisplayMode.Mode = GtDisplayMode.LaunchMode.Spectator;   // [fork] 4画面(観戦)
                    else GtDisplayMode.Enabled = value == "on";
                    return true;
                case "next": return ActiveCourseEntry?.TestNext() ?? false;
                case "back": return ActiveCourseEntry?.TestBack() ?? false;
                case "open": return ActiveCourseEntry?.TestOpen(value) ?? false;
                case "close": return ActiveCourseEntry?.TestCloseDialogs() ?? false;
                case "race": return ActiveCourseEntry?.TestSetRace(value) ?? false;
                case "tyre": return ActiveCourseEntry?.TestCycleTyre() ?? false;
                case "launch": return ActiveCourseEntry?.TestLaunch() ?? false;   // コースイン(AC起動)。HUD検証用
                case "picker":
                    if (value.StartsWith("select:", StringComparison.Ordinal)) {
                        if (!DiagnosticsEnabled) return false;
                        var pk = Application.Current?.Windows.OfType<GarageCarPicker>().FirstOrDefault(w => w.IsVisible && w.IsLoaded);
                        return pk?.TestSelect(value.Substring(7)) ?? false;
                    }
                    if (value == "open") {
                        // ShowDialog は入れ子ポンプで戻ってこないので、ここでは投げるだけ（タイマーは入れ子の中でも回る）
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() => {
                            try {
                                var p = new GarageCarPicker(WorldMap.FindCurrentCar());
                                p.ShowDialog();
                                Logging.Write("[GtTestChannel] picker closed picked=" + (p.Picked?.Id ?? "-") + " brandcentral=" + p.GoBrandCentral);
                            } catch (Exception ex) {
                                Logging.Warning("[GtTestChannel] picker: " + ex.Message);
                            }
                        }));
                        return true;
                    }
                    if (value == "close") {
                        var pk = Application.Current?.Windows.OfType<GarageCarPicker>().FirstOrDefault();
                        if (pk == null) return false;
                        pk.Close();
                        return true;
                    }
                    return false;
                case "brand": return BrandCentral.Active?.TestOpenBrand(value) ?? false;
                case "pick": return BrandCentral.Active?.TestPick(value) ?? false;
                case "bc": return BrandCentral.Active?.TestCommand(value) ?? false;
                case "pad": return Pad(value);
                case "header": return HeaderCommand(value);
                case "oheader": return OverlayHeaderCommand(value);
                case "settings": return SettingsCommand(value);
                case "shot": return Shot(value);
                case "screen": return Screen(value);
                case "tuning": { var tc = WorldMap.FindCurrentCar(); return value == "prepare" ? GtTuning.Prepare(tc) : value == "restore" && GtTuning.Restore(tc); }   // [fork] 失敗を成功扱いにしない。
                case "dump": return true;
            }
            return false;
        }

        /// <summary>[fork] モニタ全体の画面キャプチャ（CopyFromScreen）。ショールーム(RenderForm)や透明オーバーレイ窓は
        /// RenderTargetBitmap では撮れないのでこちら。モニタがスリープしていると黒。</summary>
        private static bool Screen(string path) {
            try {
                var main = Application.Current?.MainWindow;
                if (main == null || string.IsNullOrWhiteSpace(path)) return false;
                var h = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                var b = System.Windows.Forms.Screen.FromHandle(h).Bounds;
                using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height)) {
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size);
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                return true;
            } catch (Exception e) {
                Logging.Warning("[GtTestChannel] screen: " + e.Message);
                return false;
            }
        }

        /// <summary>[fork] メインウィンドウを WPF の RenderTargetBitmap で PNG に描き出す。
        /// PrintWindow と違い DWM/モニタの状態に依存しない（モニタがスリープしていても撮れる）ので、
        /// 夜間の自律検証用。DropShadow 等のエフェクトもソフトウェア描画される。</summary>
        private static GtHeader FindHeader() {
            var main = Application.Current?.Windows.OfType<GarageCarPicker>().FirstOrDefault(w => w.IsVisible)
                    as Window ?? Application.Current?.MainWindow;
            return main?.FindVisualChild<GtHeader>();
        }

        private static bool SettingsCommand(string action) {
            var page = Application.Current?.MainWindow?.FindVisualChild<GtSettings>();
            if (page == null || !page.IsLoaded) return false;
            if (action.StartsWith("volume:")) {
                double percent;
                if (!double.TryParse(action.Substring(7), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out percent)
                        || double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0 || percent > 100) return false;
                ((Slider)page.FindName("VolumeSlider")).Value = percent;
                return true;
            }
            if (action == "controller:tab") { page.OpenControllerBindings(); return true; }   // [fork] 設定の「コントローラー」タブを開く（割り当て画面の確認用）
            if (action.StartsWith("master:")) {   // [fork] 全体音量: settings=master:<0..100>
                int master;
                if (!int.TryParse(action.Substring(7), out master) || master < 0 || master > 100) return false;
                ((Slider)page.FindName("MasterVolumeSlider")).Value = master;
                return GtMasterVolume.Percent == master;
            }
            if (action.StartsWith("keyboard:")) return page.TestKeyboardCommand(action.Substring(9));
            if (action.StartsWith("title:")) return page.TestTitleCommand(action.Substring(6));
            if (action.StartsWith("opening:")) return page.TestOpeningCommand(action.Substring(8));
            var name = action == "preview" ? "PreviewButton" : action == "reset" ? "ResetButton"
                    : action == "decrease" ? "DecreaseButton" : action == "increase" ? "IncreaseButton" : null;
            if (name == null) return false;
            ((Button)page.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return true;
        }

        private static bool HeaderCommand(string action) {
            var header = FindHeader();
            if (header == null) return false;
            var menu = (ContextMenu)header.FindName("HeaderMenu");
            switch (action) {
                case "menu":
                    if (!menu.IsOpen) ((Button)header.FindName("LogoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return menu.IsOpen;
                case "settings":
                    if (!menu.IsOpen) return false;
                    ((MenuItem)header.FindName("SettingsMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    return !menu.IsOpen;
                case "close": menu.IsOpen = false; return true;
                case "back":
                    var page = Application.Current?.MainWindow?.FindVisualChild<GtSettings>();
                    if (page == null) return false;
                    ((Button)page.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return true;
                default: return false;
            }
        }

        /// <summary>[fork] ショールームの上の透明窓（ガレージ=CmJaOverlay GarageGt / ブランドセントラル=CM7 BrandCentral）にある
        /// GtHeader のボタンを、実際の RoutedEvent で押す（OS の入力は送らない・2026-09-27）。menu→settings で設定、min で最小化、close で閉じる。</summary>
        private static bool OverlayHeaderCommand(string action) {
            var win = Application.Current?.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.IsVisible && (w.Title == "CmJaOverlay GarageGt" || w.Title == "CM7 BrandCentral"));
            var header = win?.FindVisualChild<GtHeader>();
            if (header == null) return false;
            var menu = (ContextMenu)header.FindName("HeaderMenu");
            switch (action) {
                case "menu":
                    if (!menu.IsOpen) ((Button)header.FindName("LogoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return menu.IsOpen;
                case "settings":
                    if (!menu.IsOpen) return false;
                    ((MenuItem)header.FindName("SettingsMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    return true;
                case "min":
                case "close":
                    var tb = (TextBlock)header.FindName(action == "min" ? "BtnMin" : "BtnClose");
                    tb.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                            System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    return true;
                default: return false;
            }
        }

        private static bool Shot(string path) {
            Window main = Application.Current?.MainWindow;
            if (main == null || string.IsNullOrWhiteSpace(path)) return false;
            // [fork] 乗り換え画面(モーダル)が開いていればそちらを撮る
            try {
                var picker = Application.Current.Windows.OfType<GarageCarPicker>().FirstOrDefault(pw => pw.IsVisible);
                if (picker != null) main = picker;
            } catch { }
            // ★Window 自体を描く。ルートの子要素を描くと CM の UI スケール(RenderTransform 1.1 等)が
            //   子の ActualWidth/Height に反映されず、右・下が切れる（2026-09-05 に踏んだ）。
            //   サイズは Window の DIP 実寸（システム DPI 100% 前提。違えば PresentationSource の変換行列で割る）
            var src = PresentationSource.FromVisual(main);
            var sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            var w = (int)Math.Ceiling(main.ActualWidth * sx);
            var h = (int)Math.Ceiling(main.ActualHeight * sy);
            if (w <= 0 || h <= 0) return false;
            var rtb = new RenderTargetBitmap(w, h, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
            rtb.Render(main);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = File.Create(path)) enc.Save(fs);
            return true;
        }

        /// <summary>[fork] パッド入力の注入。`pad=down` / `pad=downx3`（n 回連続）。
        /// XInput とゲートを飛ばすので、実機の DS4 が無くても結線を確かめられる。</summary>
        private static bool Pad(string value) {
            var v = (value ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0) return false;
            var count = 1;
            var xi = v.LastIndexOf('x');
            if (xi > 0) {
                int n;
                if (int.TryParse(v.Substring(xi + 1), out n) && n > 0) {
                    count = Math.Min(n, 50);
                    v = v.Substring(0, xi);
                }
            }
            GtPadCommand cmd;
            switch (v) {
                case "up": cmd = GtPadCommand.Up; break;
                case "down": cmd = GtPadCommand.Down; break;
                case "left": cmd = GtPadCommand.Left; break;
                case "right": cmd = GtPadCommand.Right; break;
                case "confirm": cmd = GtPadCommand.Confirm; break;
                case "back": cmd = GtPadCommand.Back; break;
                case "start": cmd = GtPadCommand.Start; break;
                case "select": cmd = GtPadCommand.Select; break;
                case "l1": cmd = GtPadCommand.L1; break;
                case "r1": cmd = GtPadCommand.R1; break;
                case "l3": cmd = GtPadCommand.L3; break;
                case "r3": cmd = GtPadCommand.R3; break;
                case "triangle": cmd = GtPadCommand.Triangle; break;
                case "square": cmd = GtPadCommand.Square; break;
                default: return false;
            }
            var ok = true;
            for (var i = 0; i < count; i++) {
                if (!GtPad.Inject(cmd)) ok = false;
            }
            return ok;
        }

        /// <summary>CM ウィンドウ内の座標へ「本物の」マウス入力。合成入力と違い放置タイマもリセットされるので、
        /// 放置スライドが出ている状態からでも確実に操作できる。</summary>
        private static bool RealMouse(string value, bool click) {
            var p = value.Split(',');
            if (p.Length != 2 || !int.TryParse(p[0].Trim(), out var x) || !int.TryParse(p[1].Trim(), out var y)) return false;
            var main = Application.Current?.MainWindow;
            if (main == null) return false;
            var h = new System.Windows.Interop.WindowInteropHelper(main).Handle;
            if (!GetWindowRect(h, out var r)) return false;
            SetForegroundWindow(h);
            SetCursorPos(r.Left + x, r.Top + y);
            System.Threading.Thread.Sleep(60);
            if (click) {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(50);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);

        private static string After(string v) {
            var i = v.IndexOf(':');
            return i < 0 ? "" : v.Substring(i + 1);
        }
    }
}
