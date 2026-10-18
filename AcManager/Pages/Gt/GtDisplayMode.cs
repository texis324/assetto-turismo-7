using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AcManager.Tools.Data;
using AcManager.Tools.SemiGui;
using AcTools.DataFile;
using AcTools.Processes;
using AcTools.Utils;
using FirstFloor.ModernUI.Helpers;
using Newtonsoft.Json;

namespace AcManager.Pages.Gt {
    internal static class GtDisplayMode {
        private const string Setting = "__GtDisplayLaunchMode";
        internal enum LaunchMode { Automatic, Single, Triple, Spectator }
        private static bool _initialized;
        private static int _active;
        public static event EventHandler Changed;
        public static LaunchMode Mode {
            get {
                var saved = ValuesStorage.Get(Setting, "auto");
                return saved == "single" ? LaunchMode.Single : saved == "triple" ? LaunchMode.Triple : saved == "spectator" ? LaunchMode.Spectator : LaunchMode.Automatic;
            }
            set {
                if (!Enum.IsDefined(typeof(LaunchMode), value)) throw new ArgumentOutOfRangeException(nameof(value));
                if (Mode == value) return;
                ValuesStorage.Set(Setting, value == LaunchMode.Single ? "single" : value == LaunchMode.Triple ? "triple" : value == LaunchMode.Spectator ? "spectator" : "auto");
                Changed?.Invoke(null, EventArgs.Empty);
                SchedulePrelight(1500);   // [fork] 3画面/4画面に 1.5 秒止まったら先に点灯／離れたら戻す
            }
        }
        // Compatibility for explicit on/off commands. Automatic is the new default,
        // independent of the old __GtTripleLaunch preference.
        public static bool Enabled {
            get {
                if (Mode != LaunchMode.Automatic) return Mode == LaunchMode.Triple || Mode == LaunchMode.Spectator;
                try { return GtDisplayTopology.Automatic().Triple; } catch { return false; }
            }
            set => Mode = value ? LaunchMode.Triple : LaunchMode.Single;
        }
        public static bool? ToggleValue {
            get => Mode == LaunchMode.Automatic ? (bool?)null : Mode == LaunchMode.Triple || Mode == LaunchMode.Spectator;
            set => Mode = value == null ? LaunchMode.Automatic : value.Value ? LaunchMode.Triple : LaunchMode.Single;
        }
        public static void StepMode(int step) => Mode = (LaunchMode)(((int)Mode + Math.Sign(step) + 4) % 4);
        // [fork] 4画面(観戦) = 3画面 + GigaCrysta に TV カメラ（別プロセス SpectatorViewer.exe・AC 側 apps\lua\SpectatorCam）
        public static bool Spectator => Mode == LaunchMode.Spectator;
        public static string DisplayLabel {
            get {
                if (Mode == LaunchMode.Spectator) return "4画面で起動";
                if (Mode != LaunchMode.Automatic) return Mode == LaunchMode.Triple ? "3画面で起動" : "1画面で起動";
                try { return GtDisplayTopology.Automatic().Triple ? "自動：3画面" : "自動：1画面"; }
                catch { return "画面：自動"; }
            }
        }
        private static string JournalPath => Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "cm7-display-restore.json");
        private static string VideoPath => Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "video.ini");

        public static void Init() {
            if (_initialized) return;
            _initialized = true;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => Changed?.Invoke(null, EventArgs.Empty);
            // A cancelled/failed start uses Dispose. A crashed CM can recover next time it opens.
            try { RecoverBeforePresets(); } catch (Exception e) { Logging.Warning("[GtDisplayMode] recovery: " + e); }
            // [fork] 先点け: 起動時に保存済みモードが 3画面/4画面ならここで点く。前回の先点けが残っていて今は別モードなら戻す
            SchedulePrelight(4000);
            try {
                if (System.Windows.Application.Current != null) System.Windows.Application.Current.Exit += (s, e) => RestorePrelightOnExit();
            } catch (Exception e) { Logging.Warning("[GtDisplayMode] prelight exit hook: " + e.Message); }
            GameWrapper.SetPropertiesCallback(properties => {
                if (properties.ReplayProperties != null || properties.BenchmarkProperties != null) return;
                RecoverBeforePresets();
                var session = new Session(Mode);
                // AdditionalProperties.Set follows all RaceIniProperties (including video presets).
                // RevertChanges disposes in insertion order, so restore our keys before presets.
                properties.AdditionalPropertieses.Insert(0, session);
                properties.SetAdditional(new ObserveWindow(session));
            });
        }

        // Called before CM's original preset crash recovery. Do not swallow a failed restore here.
        internal static void RecoverBeforePresets() { Recover(); }

        // ------------------------------------------------------------ [fork] 4画面モードの先点け（2026-09-18 要望）
        //   「モニタがスリープから起きるのに時間がかかる。4画面を選んでいるならコースイン前から 4 台点けておきたい」
        //   ・トグルが 4画面に 1.5 秒止まったら giga+3画面 を点灯（巡回で通過しただけでは発動しない）
        //   ・別モードへ変えた時／CM7 終了時に、点灯前の構成へ戻す（点灯前の構成は cfg に保存＝CM7 が落ちても次回戻せる）
        //   ・2026-10-17 3画面モードにも拡張（要望「作ろう」）。giga が消え、CM7 は新しい primary(=3画面の真ん中 0,0) に移る
        //   ・AC 実行中とコースイン処理中(_active)は触らない。Windows 側の画面変更イベントでは発動しない（手動切替と喧嘩しない）
        //   ・コースイン時の Session は「今の構成(=4台点灯)」を復元先として覚えるので、走行後も点灯したまま＝連続で走っても速い
        private static string PrelightPath => Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "cm7-display-prelight.json");
        internal sealed class PrelightState { public int Owner; public GtDisplayTopology.Snapshot Topology; }
        private static int _prelightSeq;
        private static string _prelightNote = "idle";
        public static string PrelightStatus => (File.Exists(PrelightPath) ? "lit" : "off") + " (" + _prelightNote + ")";

        private static void SchedulePrelight(int delayMs) {
            var seq = Interlocked.Increment(ref _prelightSeq);
            Task.Run(async () => {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (seq != Volatile.Read(ref _prelightSeq)) return;   // その後にまた切り替わった＝通過しただけ
                try { ApplyPrelight(); }
                catch (Exception e) {
                    _prelightNote = "error: " + e.Message;
                    Logging.Warning("[GtDisplayMode] prelight: " + e.Message);
                }
            });
        }

        private static bool LayoutActive(bool spectator) {
            try {
                if (spectator) GtDisplayTopology.VerifySpectator(); else GtDisplayTopology.VerifyTriple();
                return true;
            } catch { return false; }
        }

        private static void ApplyPrelight() {
            if (AcRunning()) { _prelightNote = "skipped: AC running"; return; }
            if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) { _prelightNote = "skipped: launch in progress"; return; }
            try {
                var mode = Mode;
                if (mode != LaunchMode.Spectator && mode != LaunchMode.Triple) { RestorePrelight("mode=" + mode); return; }
                var spectator = mode == LaunchMode.Spectator;
                var what = spectator ? "4 screens" : "3 screens";
                if (LayoutActive(spectator)) { _prelightNote = "already lit " + what; return; }
                var layout = spectator ? GtDisplayTopology.Spectator() : GtDisplayTopology.Triple();   // モニタが見つからなければここで throw（何も変えない）
                // 3画面⇔4画面の行き来では journal を書き直さない＝戻し先は最初の先点け前の構成のまま
                if (!File.Exists(PrelightPath)) {
                    var before = new PrelightState { Owner = Process.GetCurrentProcess().Id, Topology = GtDisplayTopology.Capture() };
                    File.WriteAllText(PrelightPath, JsonConvert.SerializeObject(before, Formatting.Indented));
                }
                GtDisplayTopology.Apply(layout, true);
                if (spectator) GtDisplayTopology.VerifySpectator(); else GtDisplayTopology.VerifyTriple();
                _prelightNote = "lit " + what;
                Logging.Write("[GtDisplayMode] prelight: " + what + " lit");
            } finally { Interlocked.Exchange(ref _active, 0); }
        }

        // 先点け前の構成へ戻す。戻せなくても journal は消す（4 台点いたままは無害。壊れた journal で毎回失敗し続ける方が悪い）
        private static void RestorePrelight(string why) {
            if (!File.Exists(PrelightPath)) { _prelightNote = "idle"; return; }
            try {
                var state = JsonConvert.DeserializeObject<PrelightState>(File.ReadAllText(PrelightPath));
                if (state?.Topology != null) GtDisplayTopology.Apply(state.Topology);
                _prelightNote = "restored (" + why + ")";
                Logging.Write("[GtDisplayMode] prelight: restored previous displays (" + why + ")");
            } finally {
                try { File.Delete(PrelightPath); } catch { }
            }
        }

        private static void RestorePrelightOnExit() {
            try {
                if (AcRunning() || Volatile.Read(ref _active) != 0) return;
                RestorePrelight("CM7 exit");
            } catch (Exception e) { Logging.Warning("[GtDisplayMode] prelight exit: " + e.Message); }
        }

        internal sealed class KeyValue { public string Section, Key, Value; }
        internal sealed class FileState { public string Kind; public List<KeyValue> Keys = new List<KeyValue>(); }
        internal sealed class RestoreState {
            public int Owner;
            public GtDisplayTopology.Snapshot Topology;
            public bool TopologyApplied;
            public int ViewerPid;   // [fork] 4画面モードの観戦ビューア（クラッシュ復旧でも閉じる）
            public List<FileState> Files = new List<FileState>();
        }
        private static string ConfigPath(string kind) {
            if (kind == "video") return VideoPath;
            if (kind == "position") return PatchHelper.GetWindowPositionConfig();
            if (kind == "dxgi") return PatchHelper.GetUserConfigFilename("dxgi_tweaks.ini");
            throw new InvalidDataException("Unknown display configuration");
        }
        private static bool AcRunning() => Process.GetProcessesByName("acs").Any() || Process.GetProcessesByName("acs_x86").Any();
        private static void SaveJournal(RestoreState state) {
            var path = JournalPath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        private static void Recover() {
            if (!File.Exists(JournalPath)) return;
            if (AcRunning()) throw new InvalidOperationException("AC実行中のため画面設定を復元できません。");
            var state = JsonConvert.DeserializeObject<RestoreState>(File.ReadAllText(JournalPath));
            if (state == null) throw new InvalidDataException("Missing display restore state");
            if (state.Owner != Process.GetCurrentProcess().Id) {
                try { if (!Process.GetProcessById(state.Owner).HasExited) throw new InvalidOperationException("別のCMが画面設定を使用しています。"); }
                catch (ArgumentException) { }
            }
            Restore(state);
        }
        private static void Restore(RestoreState state) {
            // Restore only the keys we own: post-processing and input changes remain intact.
            var errors = new List<Exception>();
            foreach (var file in state.Files.ToArray()) {
                try {
                    var path = ConfigPath(file.Kind);
                    var ini = new IniFile(path);
                    foreach (var key in file.Keys) {
                        if (key.Value == null) ini[key.Section].Remove(key.Key);
                        else ini[key.Section].Set(key.Key, key.Value);
                    }
                    ini.Save();
                    state.Files.Remove(file);
                } catch (Exception e) { errors.Add(e); }
            }
            // Even if an INI is locked, always attempt to bring the physical monitors back.
            try {
                if (state.TopologyApplied) GtDisplayTopology.Apply(state.Topology);
                state.TopologyApplied = false;
            }
            catch (Exception e) { errors.Add(e); }
            // [fork] 観戦ビューアが残っていれば閉じる（失敗しても復元全体は止めない）
            if (state.ViewerPid != 0) {
                StopViewer(state.ViewerPid);
                state.ViewerPid = 0;
            }
            if (errors.Count > 0) {
                SaveJournal(state); // Later recovery retries only the items that still need restoring.
                throw new AggregateException(errors);
            }
            File.Delete(JournalPath);
            Logging.Write("[GtDisplayMode] restored video and display configuration");
        }

        // [fork] 4画面(観戦)モードのビューア。GigaCrysta(primary) に AC の TV カメラを全画面表示する別プロセス。
        //   配備先 = Data\Static\gt7\spectator\SpectatorViewer.exe
        private static string ViewerPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Static", "gt7", "spectator", "SpectatorViewer.exe");
        private static int StartViewer() {
            var exe = ViewerPath;
            if (!File.Exists(exe)) {
                Logging.Warning("[GtDisplayMode] spectator viewer not found: " + exe);
                return 0;
            }
            // --show-when-live: 映像が来るまで窓を出さない（CM7 のロード画面を隠さない）
            // --exit-with-ac : AC が終わったら自分で閉じる
            // --place-ac     : 5760 幅の AC 窓が x=1920 に無ければ動かす保険（'all' 配置では 3画面が 1920 始まり）
            var process = Process.Start(new ProcessStartInfo(exe, "--topmost --show-when-live --exit-with-ac --place-ac --ac-x 1920") {
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "", UseShellExecute = false
            });
            Logging.Write("[GtDisplayMode] spectator viewer started pid=" + (process?.Id ?? 0));
            return process?.Id ?? 0;
        }
        private static void StopViewer(int pid) {
            if (pid == 0) return;
            try {
                var process = Process.GetProcessById(pid);
                if (process.ProcessName.IndexOf("SpectatorViewer", StringComparison.OrdinalIgnoreCase) < 0) return;
                if (!process.HasExited && !(process.CloseMainWindow() && process.WaitForExit(2000))) process.Kill();
                Logging.Write("[GtDisplayMode] spectator viewer stopped pid=" + pid);
            } catch (ArgumentException) {
                // already gone (--exit-with-ac)
            } catch (Exception e) { Logging.Warning("[GtDisplayMode] stop viewer: " + e.Message); }
        }

        private sealed class Session : Game.AdditionalProperties, IDisposable {
            private readonly LaunchMode _mode;
            private bool _triple;
            private RestoreState _restore;
            private bool _ownsLease;
            internal volatile bool Stopped;
            internal Rectangle Bounds;
            public Session(LaunchMode mode) { _mode = mode; }
            public Session(bool triple) : this(triple ? LaunchMode.Triple : LaunchMode.Single) { }
            public override IDisposable Set() {
                if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("画面設定の切替が進行中です。");
                _ownsLease = true;
                try {
                    Recover();
                    _restore = new RestoreState { Owner = Process.GetCurrentProcess().Id };
                    // Resolve Auto here, immediately before writing the game's config.
                    // Never enable, reposition or restore desktop monitors for automatic launches.
                    var automatic = _mode == LaunchMode.Automatic ? GtDisplayTopology.Automatic() : null;
                    _triple = automatic != null ? automatic.Triple : _mode == LaunchMode.Triple || _mode == LaunchMode.Spectator;
                    Bounds = automatic != null ? automatic.Bounds : System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                    if (automatic != null) Logging.Write("[GtDisplayMode] automatic active=" + automatic.ActiveScreens + " reason=" + automatic.Reason);
                    if (_mode == LaunchMode.Triple || _mode == LaunchMode.Spectator) {
                        // [fork] 4画面(観戦) = giga(0,0 primary) + 3画面(1920..7680)。AC は 3画面側、giga にはビューア
                        var spectator = _mode == LaunchMode.Spectator;
                        var layout = spectator ? GtDisplayTopology.Spectator() : GtDisplayTopology.Triple(); // Validate availability before any mutation.
                        _restore.Topology = GtDisplayTopology.Capture();
                        _restore.TopologyApplied = true;
                        SaveJournal(_restore);
                        GtDisplayTopology.Apply(layout, true);
                        if (spectator) GtDisplayTopology.VerifySpectator(); else GtDisplayTopology.VerifyTriple();
                        Bounds = spectator ? new Rectangle(1920, 0, 5760, 1080) : new Rectangle(-1920, 0, 5760, 1080);
                    }
                    var video = new Dictionary<string, string> {
                        ["CAMERA/MODE"] = _triple ? "TRIPLE" : "DEFAULT",
                        ["VIDEO/WIDTH"] = Bounds.Width.ToString(), ["VIDEO/HEIGHT"] = Bounds.Height.ToString(),
                        ["VIDEO/FULLSCREEN"] = "0", ["VIDEO/INDEX"] = "0", ["VIDEO/_EXT_PLACEMENT"] = null
                    };
                    Write("video", video);
                    Write("position", new Dictionary<string, string> {
                        ["AC_WINDOW_POSITION/REAL_X"] = Bounds.X.ToString(), ["AC_WINDOW_POSITION/REAL_Y"] = Bounds.Y.ToString(),
                        ["AC_WINDOW_POSITION/X"] = (Bounds.X + 8).ToString(), ["AC_WINDOW_POSITION/Y"] = (Bounds.Y + 31).ToString()
                    });
                    // CSP owns both the frame and client size. This is the standard DXGI
                    // "flat borderless" option; keep its save-window and compatibility options.
                    Write("dxgi", new Dictionary<string, string> {
                        ["WINDOW_TWEAKS/BORDERLESS_WINDOWED"] = "2"
                    });
                    if (_mode == LaunchMode.Spectator) {
                        _restore.ViewerPid = StartViewer();
                        SaveJournal(_restore);
                    }
                    Logging.Write("[GtDisplayMode] applied mode=" + _mode + " triple=" + _triple + " bounds=" + Bounds + " placement=CSP-standard-v1");
                    return null; // Already registered as IDisposable, before the preset restorers.
                } catch {
                    Dispose();
                    throw;
                }
            }
            private void Write(string kind, Dictionary<string, string> values) {
                var path = ConfigPath(kind);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var ini = new IniFile(path);
                var file = new FileState { Kind = kind };
                foreach (var pair in values) {
                    var key = pair.Key.Split('/');
                    file.Keys.Add(new KeyValue { Section = key[0], Key = key[1], Value = ini[key[0]].GetPossiblyEmpty(key[1]) });
                }
                _restore.Files.Add(file);
                SaveJournal(_restore);
                foreach (var pair in values) {
                    var key = pair.Key.Split('/');
                    if (pair.Value == null) ini[key[0]].Remove(key[1]);
                    else ini[key[0]].Set(key[1], pair.Value);
                }
                ini.Save();
                var check = new IniFile(path);
                foreach (var pair in values) {
                    var key = pair.Key.Split('/');
                    if (check[key[0]].GetPossiblyEmpty(key[1]) != pair.Value) throw new IOException("画面設定の保存を確認できませんでした。");
                }
            }
            public void Dispose() {
                Stopped = true;
                if (!_ownsLease) return;
                try {
                    if (_restore != null) Restore(_restore);
                } catch (Exception e) {
                    Logging.Error("[GtDisplayMode] restore failed; journal retained: " + e);
                } finally {
                    _ownsLease = false;
                    Interlocked.Exchange(ref _active, 0);
                    SchedulePrelight(1500);   // [fork] 走行中にモードが変わっていたら、ここで先点けを付け直す／戻す
                }
            }
        }

        // Observe only. Changing the style or sending SWP_FRAMECHANGED while CSP
        // creates its rendering window can race with CSP's own client-size handling.
        private sealed class ObserveWindow : Game.GameHandler {
            private readonly Session _session;
            public ObserveWindow(Session session) { _session = session; }
            public override IDisposable Set(Process process) {
                if (process == null) return null;
                Task.Run(async () => {
                    var clock = Stopwatch.StartNew();
                    string previous = null;
                    long lastLog = -10000;
                    Logging.Write("[GtDisplayMode] observing pid=" + process.Id + " expected=" + _session.Bounds);
                    while (!_session.Stopped && clock.Elapsed < TimeSpan.FromMinutes(3)) {
                        try {
                            if (process.HasExited) break;
                            process.Refresh();
                            var windows = new List<string>();
                            var pid = (uint)process.Id;
                            // Loading and rendering HWNDs can differ. Record every top-level
                            // window belonging to this AC process, not just MainWindowHandle.
                            EnumWindowsProc callback = (handle, unused) => {
                                uint owner;
                                GetWindowThreadProcessId(handle, out owner);
                                if (owner != pid) return true;
                                var name = new StringBuilder(256);
                                GetClassName(handle, name, name.Capacity);
                                NativeRect window, client;
                                var gotWindow = GetWindowRect(handle, out window);
                                var windowError = gotWindow ? 0 : Marshal.GetLastWin32Error();
                                var gotClient = GetClientRect(handle, out client);
                                var clientError = gotClient ? 0 : Marshal.GetLastWin32Error();
                                windows.Add("hwnd=0x" + handle.ToInt64().ToString("X") + " class=" + name
                                        + " visible=" + IsWindowVisible(handle) + " iconic=" + IsIconic(handle)
                                        + " zoomed=" + IsZoomed(handle) + " style=0x" + GetWindowLong(handle, -16).ToString("X8")
                                        + " exstyle=0x" + GetWindowLong(handle, -20).ToString("X8")
                                        + " window=" + (gotWindow ? window.ToString() : "error:" + windowError)
                                        + " client=" + (gotClient ? client.ToString() : "error:" + clientError));
                                return true;
                            };
                            if (!EnumWindows(callback, IntPtr.Zero)) windows.Add("EnumWindows error=" + Marshal.GetLastWin32Error());
                            windows.Sort(StringComparer.Ordinal);
                            var sample = "main=0x" + process.MainWindowHandle.ToInt64().ToString("X")
                                    + " windows=[" + string.Join("; ", windows) + "]";
                            if (sample != previous || clock.ElapsedMilliseconds - lastLog >= 10000) {
                                Logging.Write("[GtDisplayMode] observed pid=" + pid + " elapsedMs=" + clock.ElapsedMilliseconds + " " + sample);
                                previous = sample;
                                lastLog = clock.ElapsedMilliseconds;
                            }
                        } catch (Exception e) { Logging.Warning("[GtDisplayMode] observation: " + e.Message); break; }
                        await Task.Delay(clock.Elapsed.TotalSeconds < 15 ? 250 : 1000).ConfigureAwait(false);
                    }
                    Logging.Write("[GtDisplayMode] observation finished elapsedMs=" + clock.ElapsedMilliseconds);
                });
                return null;
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect {
            public int Left, Top, Right, Bottom;
            public override string ToString() => new Rectangle(Left, Top, Right - Left, Bottom - Top).ToString();
        }
        private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int length);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr handle);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    }
}
