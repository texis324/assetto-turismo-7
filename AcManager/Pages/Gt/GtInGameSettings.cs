using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AcManager.Pages.Windows;
using AcTools.Utils;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows.Controls;
using FirstFloor.ModernUI.Windows.Media;

namespace AcManager.Pages.Gt {
    // All effects are behind this host so lease/return tests never need a real game or UI.
    internal interface IGtPreviewSettingsHost {
        string RequestText { get; }
        string FlagText { get; }
        long UnixNow { get; }
        int AcProcessId { get; }
        string CurrentPage { get; }
        bool SettingsVisible { get; }
        double UiVolume { get; }
        void OpenSettings();
        void CancelOpening();
        void RestorePage(string page);
        void CommitAssist();
        bool FocusGame(int processId);
        void WriteResponse(string text);
    }

    internal sealed class GtPreviewSettingsSession {
        internal const string SettingsPage = "/Pages/Gt/GtSettings.xaml";
        private readonly IGtPreviewSettingsHost _host;
        private readonly HashSet<string> _handled = new HashSet<string>(StringComparer.Ordinal);
        private string _token;
        private long _startStamp;
        private int _processId;
        private string _returnPage;
        private long _openedAt;
        private long _lastReply;
        private long _lastLeaseHeartbeat;
        private bool _replyAttempted;
        private bool _settingsSeen;
        private bool _replyHealthy;
        private bool _closing;
        private string _phase;
        private string _page;
        private long _reloadRevision;
        private long _reloadApplied;
        private string _reloadError;

        public GtPreviewSettingsSession(IGtPreviewSettingsHost host) { _host = host; }
        public bool IsActive { get { return _token != null; } }
        public bool IsOpen { get { return IsActive && _settingsSeen && _replyHealthy && IsReplyFresh() && IsSettingsPage(_host.CurrentPage); } }
        public bool ControllerPageRequested { get { return IsActive && _page == "controllers"; } }
        public string ControllerBindingsStatus {
            get {
                if (!IsActive) return null;
                if (!string.IsNullOrEmpty(_reloadError)) return "保存しましたが、ゲームへの反映を確認できませんでした: " + _reloadError;
                if (_reloadRevision > _reloadApplied) return "保存済み。ゲームに割り当てを反映しています…";
                return _reloadRevision > 0 ? "保存した割り当てを、この走行にも反映しました。" : "変更すると、この走行にも割り当てが反映されます。";
            }
        }

        public void RequestControlsReload() {
            if (!IsActive || _closing) return;
            _reloadRevision++;
            _reloadError = null;
            try { RespondOpen(); } catch { _replyHealthy = false; }
        }

        private bool IsReplyFresh() {
            var age = _host.UnixNow - _lastReply;
            return age >= -1 && age < 3;
        }

        internal static bool IsSettingsPage(string page) {
            return string.Equals((page ?? "").Split('?')[0], SettingsPage, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Lease {
            public string Token;
            public string State;
            public long StartStamp;
            public long Heartbeat;
            public string Phase;
            public string Page;
            public long ReloadApplied;
            public string ReloadError;
        }

        private static Dictionary<string, string> ParseSection(string text, string section) {
            if (text == null || text.Length > 16384) throw new InvalidDataException("Missing or oversized settings request");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inside = false;
            foreach (var raw in text.Split('\n')) {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) {
                    inside = string.Equals(line.Substring(1, line.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                var equal = line.IndexOf('=');
                if (inside && equal > 0) values[line.Substring(0, equal).Trim()] = line.Substring(equal + 1).Trim();
            }
            return values;
        }

        private static string Value(Dictionary<string, string> values, string key) {
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private bool ReadLease(out Lease lease, out string error, out bool retryableIo) {
            lease = null;
            error = "設定画面との接続が切れました。";
            retryableIo = false;
            try {
                var request = ParseSection(_host.RequestText, "SETTINGS");
                var token = Value(request, "TOKEN");
                if (!Regex.IsMatch(token, "^[A-Za-z0-9]{1,64}$")) return false;
                long stamp;
                if (!long.TryParse(Value(request, "START_STAMP"), NumberStyles.None, CultureInfo.InvariantCulture, out stamp) || stamp <= 0) return false;
                lease = new Lease { Token = token, State = Value(request, "STATE"), StartStamp = stamp,
                    Phase = Value(request, "PHASE"), Page = Value(request, "PAGE") };
                if (lease.Page == "") lease.Page = "settings"; // Existing preview clients.
                long heartbeat;
                if (!long.TryParse(Value(request, "HEARTBEAT"), NumberStyles.None, CultureInfo.InvariantCulture, out heartbeat)) return false;
                lease.Heartbeat = heartbeat;
                var age = _host.UnixNow - heartbeat;
                if (age < -1 || age > 3) return false;
                if (lease.Phase != "preview" && lease.Phase != "pause"
                        || lease.Page != "settings" && lease.Page != "controllers"
                        || lease.State != "open" && lease.State != "closed") return false;
                var appliedText = Value(request, "RELOAD_APPLIED");
                if (appliedText != "" && !long.TryParse(appliedText, NumberStyles.None, CultureInfo.InvariantCulture, out lease.ReloadApplied)) return false;
                lease.ReloadError = Value(request, "RELOAD_ERROR");
                if (lease.ReloadError.Length > 180) lease.ReloadError = lease.ReloadError.Substring(0, 180);
                var flag = ParseSection(_host.FlagText, "GT");
                long actualStamp;
                if (Value(flag, "ENABLED") != "1" || !long.TryParse(Value(flag, "STAMP"), NumberStyles.None, CultureInfo.InvariantCulture, out actualStamp)
                        || actualStamp != stamp || _host.AcProcessId <= 0) {
                    error = "現在の準備画面・ポーズ画面を確認できませんでした。";
                    return false;
                }
                return true;
            } catch (InvalidDataException) {
                return false;
            } catch (IOException) {
                retryableIo = true;
                return false;
            } catch {
                return false;
            }
        }

        // A render callback can run between session ticks; require a fresh lease before revealing CM.
        public bool CanPresentSettings() {
            if (_closing || !IsActive) return false;
            Lease lease;
            string error;
            bool retryableIo;
            return ReadLease(out lease, out error, out retryableIo) && lease.State == "open" && lease.Token == _token
                    && lease.StartStamp == _startStamp && lease.Phase == _phase && lease.Page == _page
                    && lease.ReloadApplied <= _reloadRevision && _host.AcProcessId == _processId;
        }

        // No cached lease here: the model calls this again immediately before its atomic save.
        public bool CanEdit() {
            if (_closing || !IsOpen || !_host.SettingsVisible) return false;
            Lease lease;
            string error;
            bool retryableIo;
            return ReadLease(out lease, out error, out retryableIo) && lease.State == "open" && lease.Token == _token
                    && lease.StartStamp == _startStamp && lease.Phase == _phase && lease.Page == _page
                    && lease.ReloadApplied <= _reloadRevision && _host.AcProcessId == _processId;
        }

        private string LeaseKey(Lease lease) { return lease.StartStamp.ToString(CultureInfo.InvariantCulture) + ":" + lease.Token; }

        private bool CanRetryRead(Lease partialLease) {
            // Keep only the screen alive. CanEdit still requires a completely fresh read.
            // Neither failed reads nor our replies extend the last validated request heartbeat.
            if (_host.AcProcessId != _processId || !IsSettingsPage(_host.CurrentPage) || !IsReplyFresh()) return false;
            if (partialLease != null && (partialLease.Token != _token || partialLease.StartStamp != _startStamp
                    || partialLease.State != "open" || partialLease.Phase != _phase || partialLease.Page != _page)) return false;
            var age = _host.UnixNow - _lastLeaseHeartbeat;
            return age >= -1 && age <= 3;
        }

        private void RespondOpen() {
            // Lua revokes at four seconds. Stop before that deadline, even if its request keeps arriving.
            if (_replyAttempted && !IsReplyFresh()) {
                Finish("error", "設定画面への応答が途切れました。", false);
                return;
            }
            _replyAttempted = true;
            try {
                Respond(_token, _startStamp, "open", "");
            } catch (IOException) {
                // Do not claim an acknowledgement or permit a save until a later write succeeds.
                _replyHealthy = false;
                if (!IsReplyFresh()) Finish("error", "設定画面への応答が途切れました。", false);
            }
        }

        public void Tick() {
            if (_closing) return;
            try {
                Lease lease;
                string error;
                bool retryableIo;
                var valid = ReadLease(out lease, out error, out retryableIo);
                if (IsActive) {
                    if (!valid && retryableIo && CanRetryRead(lease)) return;
                    if (!valid || lease.Token != _token || lease.StartStamp != _startStamp || lease.Phase != _phase || lease.Page != _page
                            || _host.AcProcessId != _processId || lease.ReloadApplied > _reloadRevision) {
                        Finish("error", error, false);
                        return;
                    }
                    _lastLeaseHeartbeat = lease.Heartbeat;
                    _reloadApplied = Math.Max(_reloadApplied, lease.ReloadApplied);
                    _reloadError = _reloadApplied >= _reloadRevision ? null : lease.ReloadError;
                    if (lease.State == "closed") { Finish("closed", "", false); return; }
                    if (!IsSettingsPage(_host.CurrentPage)) {
                        if (!_settingsSeen && _host.UnixNow - _openedAt <= 4) return;
                        Finish("closed", "", false, false);
                        return;
                    }
                    if (_host.SettingsVisible) _settingsSeen = true;
                    if (!_settingsSeen) {
                        if (_host.UnixNow - _openedAt > 4) Finish("error", "設定画面を開けませんでした。", false);
                        return;
                    }
                    if (!_replyHealthy || _host.UnixNow != _lastReply) RespondOpen();
                    return;
                }
                if (lease == null || _handled.Contains(LeaseKey(lease))) return;
                if (!valid) {
                    if (retryableIo) return;
                    _handled.Add(LeaseKey(lease));
                    Respond(lease.Token, lease.StartStamp, "error", error, lease.Phase, lease.Page);
                    return;
                }
                if (lease.State != "open") { _handled.Add(LeaseKey(lease)); return; }
                _token = lease.Token;
                _phase = lease.Phase;
                _page = lease.Page;
                _reloadRevision = _reloadApplied = 0;
                _reloadError = null;
                _startStamp = lease.StartStamp;
                _processId = _host.AcProcessId;
                _returnPage = _host.CurrentPage;
                _openedAt = _host.UnixNow;
                _lastReply = _openedAt;
                _lastLeaseHeartbeat = lease.Heartbeat;
                _replyAttempted = false;
                _settingsSeen = false;
                _replyHealthy = false;
                _handled.Add(LeaseKey(lease));
                Logging.Write("[GtInGameSettings] open requested: phase=" + _phase + " page=" + _page + " acPid=" + _processId + " from=" + _returnPage);   // [fork] 2026-09-19
                _host.OpenSettings();
                if (IsSettingsPage(_host.CurrentPage) && _host.SettingsVisible) {
                    _settingsSeen = true;
                    RespondOpen();
                }
            } catch (Exception e) {
                // [fork] 2026-09-19: ここは黙って握りつぶしていたので「開かない」の原因が追えなかった。理由を必ずログに残す
                Logging.Warning("[GtInGameSettings] tick failed (active=" + IsActive + "): " + e + (e.InnerException != null ? " / inner: " + e.InnerException : ""));
                if (IsActive) Finish("error", "設定画面との接続を解除しました。", false);
            }
        }

        public bool TryReturnToGame() {
            if (!IsActive || _closing) return false;
            try {
                // Keep the lease active through CommitAssist, including its final save guard.
                _host.CommitAssist();
                Finish("closed", "", true);
            } catch {
                Finish("error", "設定画面を閉じる処理に失敗しました。", false);
            }
            return true;
        }

        public void PageUnloaded() {
            if (IsActive && !_closing) Finish("closed", "", false, false);
        }

        private void Respond(string token, long stamp, string state, string message, string phase = null, string page = null) {
            var now = _host.UnixNow;
            var volume = _host.UiVolume;
            if (double.IsNaN(volume) || double.IsInfinity(volume)) volume = 0.35;
            volume = Math.Max(0, Math.Min(1, volume));
            message = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 180) message = message.Substring(0, 180);
            phase = phase ?? _phase;
            page = page ?? _page;
            if (phase != "preview" && phase != "pause") phase = "preview";
            if (page != "settings" && page != "controllers") page = "settings";
            _host.WriteResponse("[SETTINGS]\r\nTOKEN=" + token + "\r\nSTATE=" + state
                    + "\r\nHEARTBEAT=" + now.ToString(CultureInfo.InvariantCulture)
                    + "\r\nSTART_STAMP=" + stamp.ToString(CultureInfo.InvariantCulture)
                    + "\r\nPHASE=" + phase + "\r\nPAGE=" + page
                    + "\r\nRELOAD_CONTROLS=" + _reloadRevision.ToString(CultureInfo.InvariantCulture)
                    + "\r\nUI_VOLUME=" + volume.ToString("0.####", CultureInfo.InvariantCulture)
                    + "\r\nMESSAGE=" + message + "\r\n");
            _lastReply = now;
            _replyHealthy = state == "open";
        }

        private void Finish(string state, string message, bool focusGame, bool restorePage = true) {
            if (!IsActive || _closing) return;
            _closing = true;
            Logging.Write("[GtInGameSettings] finish: state=" + state + " phase=" + _phase + " page=" + _page + " message=" + message);   // [fork] 2026-09-19
            var token = _token;
            var stamp = _startStamp;
            var processId = _processId;
            var page = _returnPage;
            var stillSettings = IsSettingsPage(_host.CurrentPage);
            _replyHealthy = false;
            try {
                try { Respond(token, stamp, state, message); } catch { }
                // Revocation happens before navigation/unload: no late autosave can use this lease.
                _token = null;
                _settingsSeen = false;
                _host.CancelOpening();
                if (restorePage && stillSettings && !string.IsNullOrWhiteSpace(page)) {
                    try { _host.RestorePage(page); } catch { }
                }
                if (focusGame && _host.AcProcessId == processId) {
                    try {
                        if (!_host.FocusGame(processId)) Respond(token, stamp, "error", "AC を前面に戻せませんでした。AC のウィンドウに切り替えてください。");
                    } catch { }
                }
            } finally {
                _token = null;
                _returnPage = null;
                _settingsSeen = false;
                _replyHealthy = false;
                _closing = false;
            }
        }
    }

    // Prepare the real frame while invisible; never expose its previous page or transition presenter.
    internal sealed class GtSettingsReveal {
        private readonly Window _window;
        private readonly ModernFrame _frame;
        private readonly Func<bool> _ready;
        private readonly Func<bool> _current;
        private readonly Action _activate;
        private readonly double _opacity;
        private readonly bool _showActivated;
        private readonly bool _visible;
        private readonly WindowState _state;
        private readonly string _transition;
        private DispatcherTimer _timer;
        private EventHandler _rendered;
        private DispatcherOperation _queued;
        private bool _pending;

        public bool IsReady { get; private set; }
        public Exception Error { get; private set; }

        public GtSettingsReveal(Window window, ModernFrame frame, Func<bool> ready, Func<bool> current, Action activate) {
            _window = window;
            _frame = frame;
            _ready = ready;
            _current = current;
            _activate = activate;
            _opacity = window.Opacity;
            _showActivated = window.ShowActivated;
            _visible = window.IsVisible;
            _state = window.WindowState;
            _transition = frame.TransitionName;
        }

        public void Start(Action navigate) {
            _pending = true;
            try {
                _window.SetCurrentValue(UIElement.OpacityProperty, 0d);
                // [fork] 2026-09-19: WPF は「ShowActivated=false かつ最大化」の窓を Show できない（InvalidOperationException）。
                //   要望 の CM7 は最大化で使うので、走行中の設定が一度も開けなかった（準備画面からもポーズからも）。
                //   最大化の時だけ ShowActivated を触らない＝Show の瞬間にアクティブになるが、不透明度 0 なので古い絵は見えない。
                //   どのみち直後の _activate() で CM7 を前面に出す流れなので、早めにアクティブになるだけ
                if (_window.WindowState != WindowState.Maximized) _window.ShowActivated = false;
                _frame.SetCurrentValue(ModernFrame.TransitionNameProperty, "Normal");
                var presenter = _frame.FindVisualChild<TransitioningContentControl>();
                if (presenter != null) presenter.AbortTransition();
                navigate();
                // A hidden window must participate in layout/Loaded, but must not activate or reveal old pixels.
                if (!_window.IsVisible) _window.Show();
                if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
                _timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
                _timer.Tick += (s, e) => CheckReady();
                _timer.Start();
            } catch (Exception e) { Fail(e); }
        }

        private void CheckReady() {
            try {
                if (!_pending) return;
                if (!_current()) { Cancel(); return; }
                if (_frame.IsLoadingContent || !_ready()) return;
                _window.UpdateLayout();
                var presenter = _frame.FindVisualChild<TransitioningContentControl>();
                if (presenter != null) presenter.AbortTransition();
                _timer.Stop();
                _rendered = (s, e) => {
                    CompositionTarget.Rendering -= _rendered;
                    _rendered = null;
                    // Run after this render pass, not merely after a Source/Loaded notification.
                    _queued = _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reveal));
                };
                CompositionTarget.Rendering += _rendered;
                _window.InvalidateVisual();
            } catch (Exception e) { Fail(e); }
        }

        private void Reveal() {
            try {
                if (!_pending) return;
                if (!_current()) { Cancel(); return; }
                // A transient shared-file lock is not a cancelled session. Wait and render again.
                if (_frame.IsLoadingContent || !_ready()) { _timer.Start(); return; }
                RestoreProperties();
                _activate();
                IsReady = true;
                _pending = false;
            } catch (Exception e) { Fail(e); }
        }

        private void RestoreProperties() {
            _frame.SetCurrentValue(ModernFrame.TransitionNameProperty, _transition);
            _window.SetCurrentValue(UIElement.OpacityProperty, _opacity);
            _window.ShowActivated = _showActivated;
        }

        public void Cancel() {
            if (_timer != null) _timer.Stop();
            if (_rendered != null) { CompositionTarget.Rendering -= _rendered; _rendered = null; }
            if (_queued != null && _queued.Status == DispatcherOperationStatus.Pending) _queued.Abort();
            if (!_pending) return;
            _pending = false;
            _window.Hide();
            _window.WindowState = _state;
            RestoreProperties();
            if (_visible) {
                // [fork] 最大化の窓は ShowActivated=false のまま Show できない（上の Start と同じ制約）
                if (_window.WindowState != WindowState.Maximized) _window.ShowActivated = false;
                _window.Show();
                _window.ShowActivated = _showActivated;
            }
        }

        private void Fail(Exception error) {
            Logging.Warning("[GtSettingsReveal] failed: " + error);   // [fork] 2026-09-19: 失敗の理由をログへ
            Error = error;
            Cancel();
        }
    }

    internal static class GtInGameSettings {
        private static DispatcherTimer _timer;
        private static GtPreviewSettingsSession _session;
        internal static bool IsRestoringPage { get; private set; }
        internal static bool IsSessionActive { get { return _session != null && _session.IsActive; } }
        internal static bool AllowPadNavigation { get { return _session != null && _session.IsOpen; } }
        internal static bool ControllerPageRequested { get { return _session != null && _session.ControllerPageRequested; } }

        internal static void Init() {
            if (_timer != null) return;
            _session = new GtPreviewSettingsSession(new DesktopHost());
            GtControllerBindingsSettings.CanEditLive = () => _session != null && _session.CanEdit();
            GtControllerBindingsSettings.LiveBindingsSaved = () => _session?.RequestControlsReload();
            GtControllerBindingsSettings.LiveStatus = () => _session?.ControllerBindingsStatus;
            GtHeaderBridge.Init();
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => { _session.Tick(); GtHeaderBridge.Tick(); };
            _timer.Start();
        }

        internal static bool BlockKeyboardChanges() {
            if (!GtKeyboardSettings.IsDriving()) return false;
            return _session == null || !_session.CanEdit();
        }

        internal static bool TryReturnToGame() { return _session != null && _session.TryReturnToGame(); }
        internal static void SettingsUnloaded() { if (_session != null) _session.PageUnloaded(); }

        private sealed class DesktopHost : IGtPreviewSettingsHost {
            private readonly string _requestPath = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "cm7_settings_request.ini");
            private readonly string _responsePath = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "cm7_settings_response.ini");
            private readonly string _flagPath = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt_start.ini");
            private WindowState _previousWindowState;
            private GtSettingsReveal _reveal;
            private MainWindow Main { get { return Application.Current == null ? null : Application.Current.MainWindow as MainWindow; } }
            public string RequestText { get { return ReadSharedText(_requestPath); } }
            public string FlagText { get { return ReadSharedText(_flagPath); } }

            private static string ReadSharedText(string path) {
                // The other process atomically replaces these files; do not hold a delete lock.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true)) {
                    return reader.ReadToEnd();
                }
            }
            public long UnixNow { get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; } }
            public int AcProcessId { get { return FindAcProcessId(); } }
            public string CurrentPage { get { return Main == null || Main.CurrentSource == null ? "" : Main.CurrentSource.OriginalString; } }
            public bool SettingsVisible {
                get {
                    if (_reveal != null && _reveal.Error != null) throw new InvalidOperationException("Could not present CM settings", _reveal.Error);
                    var frame = Main == null ? null : Main.Template.FindName("ContentFrame", Main) as ModernFrame;
                    var page = frame == null ? null : frame.Content as GtSettings;
                    return _reveal != null && _reveal.IsReady && page != null && page.IsLoaded;
                }
            }
            public double UiVolume { get { return GtSfx.Volume; } }

            public void OpenSettings() {
                var main = Main;
                if (main == null) throw new InvalidOperationException("CM main window is unavailable");
                _previousWindowState = main.WindowState;
                GtIdleSlides.ForceHide();
                CancelOpening();
                var frame = main.Template.FindName("ContentFrame", main) as ModernFrame;
                if (frame == null) throw new InvalidOperationException("CM content frame is unavailable");
                _reveal = new GtSettingsReveal(main, frame,
                    () => frame.Content is GtSettings && ((GtSettings)frame.Content).IsLoaded
                            && _session != null && _session.CanPresentSettings(),
                    () => IsSessionActive && GtPreviewSettingsSession.IsSettingsPage(CurrentPage),
                    () => { if (!FocusWindow(new WindowInteropHelper(main).Handle)) throw new InvalidOperationException("Could not activate CM settings"); });
                _reveal.Start(() => {
                    if (!GtPreviewSettingsSession.IsSettingsPage(CurrentPage) || !(frame.Content is GtSettings)) WorldMap.Navigate(GtHeader.SettingsUri);
                    else if (ControllerPageRequested) ((GtSettings)frame.Content).OpenControllerBindings();
                });
            }

            public void CancelOpening() {
                if (_reveal != null) { _reveal.Cancel(); _reveal = null; }
                // AppHibernator might have already restored CM before a late cancellation arrives.
                // With no game left, do not strand the main window in its original hidden state.
                var main = Main;
                if (AcProcessId == 0 && main != null && !main.IsVisible) main.Show();
            }

            public void RestorePage(string page) {
                var main = Main;
                if (main == null) return;
                IsRestoringPage = true;
                try {
                    // Keep the course setup page behind the game on the return path as well.
                    if (AcProcessId > 0) main.Hide();
                    if (!string.Equals(CurrentPage, page, StringComparison.Ordinal)) WorldMap.Navigate(page);
                    if (_previousWindowState == WindowState.Minimized) main.WindowState = WindowState.Minimized;
                } finally {
                    main.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => IsRestoringPage = false));
                }
            }

            public void CommitAssist() {
                var page = Main == null ? null : Main.FindVisualChild<GtSettings>();
                if (page != null && page.IsLoaded) page.PrepareReturnToGame();
            }

            public bool FocusGame(int processId) {
                using (var process = Process.GetProcessById(processId)) {
                    if (process.HasExited) return false;
                    return FocusWindow(process.MainWindowHandle);
                }
            }

            public void WriteResponse(string text) {
                var temporary = _responsePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    File.WriteAllText(temporary, text, new UTF8Encoding(false));
                    if (File.Exists(_responsePath)) File.Replace(temporary, _responsePath, null);
                    else File.Move(temporary, _responsePath);
                } finally {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        internal static int FindAcProcessId() {
            var result = 0;
            foreach (var name in new[] { "acs", "acs_x86", "acs_pro" }) {
                var processes = Process.GetProcessesByName(name);
                try {
                    foreach (var process in processes) {
                        if (result != 0) return 0; // Ambiguous sessions must never grant a write lease.
                        result = process.Id;
                    }
                } finally { foreach (var process in processes) process.Dispose(); }
            }
            return result;
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private static bool FocusWindow(IntPtr target) {
            if (target == IntPtr.Zero) return false;
            if (IsIconic(target)) ShowWindow(target, 9);
            if (GetForegroundWindow() == target) return true;
            SetForegroundWindow(target);
            if (GetForegroundWindow() == target) return true;
            // This is initiated by the player's in-game Settings click; do not synthesize keys.
            uint ignored;
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try {
                SetForegroundWindow(target);
                return GetForegroundWindow() == target;
            } finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        }
    }
}
