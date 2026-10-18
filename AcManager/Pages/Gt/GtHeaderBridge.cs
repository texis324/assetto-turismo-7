using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AcTools.Utils;

namespace AcManager.Pages.Gt {
    internal interface IGtHeaderBridgeHost {
        string RequestText { get; }
        string FlagText { get; }
        long UnixNow { get; }
        int AcProcessId { get; }
        bool Available { get; }
        string Title { get; }
        bool Playing { get; }
        double Volume { get; }
        bool SetPreviewAllowed(bool allowed);
        void Execute(string command, double value);
        void MinimizeGame(int processId);
        void WriteResponse(string text);
    }

    // The Lua header controls the existing CM player; this class owns only its preview lease.
    internal sealed class GtHeaderBridgeSession {
        private readonly IGtHeaderBridgeHost _host;
        private readonly HashSet<string> _retired = new HashSet<string>(StringComparer.Ordinal);
        private string _token;
        private long _stamp;
        private int _processId;
        private long _lastHeartbeat;
        private long _lastResponse;
        private long _ackSequence;
        private bool _permissionGranted;
        private bool _responsePending;

        public GtHeaderBridgeSession(IGtHeaderBridgeHost host) { _host = host; }
        public bool IsActive { get { return _token != null; } }

        private sealed class Request {
            public string Token, Phase, Command;
            public long Stamp, Heartbeat, Sequence;
            public double Value;
        }

        private static Dictionary<string, string> Parse(string text, string section) {
            if (text == null || text.Length > 16384) throw new InvalidDataException("Missing or oversized header request");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inside = false;
            foreach (var raw in text.Split('\n')) {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) {
                    inside = string.Equals(line.Substring(1, line.Length - 2), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                var equal = line.IndexOf('=');
                if (inside && equal > 0) result[line.Substring(0, equal).Trim()] = line.Substring(equal + 1).Trim();
            }
            return result;
        }

        private static string Get(Dictionary<string, string> values, string key) {
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private bool ReadRequest(out Request request, out bool retryableIo) {
            request = null;
            retryableIo = false;
            try {
                var values = Parse(_host.RequestText, "HEADER");
                var token = Get(values, "TOKEN");
                long stamp;
                if (!Regex.IsMatch(token, "^[A-Za-z0-9]{1,64}$") || !long.TryParse(Get(values, "START_STAMP"),
                        NumberStyles.None, CultureInfo.InvariantCulture, out stamp) || stamp <= 0) return false;
                request = new Request { Token = token, Stamp = stamp, Phase = Get(values, "PHASE"), Command = Get(values, "COMMAND") };
                if (!long.TryParse(Get(values, "HEARTBEAT"), NumberStyles.None, CultureInfo.InvariantCulture, out request.Heartbeat)
                        || !long.TryParse(Get(values, "SEQ"), NumberStyles.None, CultureInfo.InvariantCulture, out request.Sequence)) return false;
                var age = _host.UnixNow - request.Heartbeat;
                if (age < -1 || age > 3 || request.Phase != "preview" && request.Phase != "closed") return false;
                if (request.Command != "none" && request.Command != "toggle" && request.Command != "next"
                        && request.Command != "volume" && request.Command != "minimize") return false;
                if (request.Command == "volume" && (!double.TryParse(Get(values, "VALUE"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out request.Value) || double.IsNaN(request.Value)
                        || double.IsInfinity(request.Value) || request.Value < 0 || request.Value > 1)) return false;
                // Closing must revoke promptly even if gt_start.ini has already moved beyond preview.
                if (request.Phase == "closed") return true;
                var flag = Parse(_host.FlagText, "GT");
                long actualStamp;
                return Get(flag, "ENABLED") == "1" && Get(flag, "STATE") == "preview"
                        && long.TryParse(Get(flag, "STAMP"), NumberStyles.None, CultureInfo.InvariantCulture, out actualStamp)
                        && actualStamp == stamp && _host.AcProcessId > 0;
            } catch (InvalidDataException) {
                return false;
            } catch (IOException) {
                retryableIo = true;
                return false;
            } catch {
                return false;
            }
        }

        private static string Key(string token, long stamp) { return stamp.ToString(CultureInfo.InvariantCulture) + ":" + token; }
        private bool ResponseFresh() {
            var age = _host.UnixNow - _lastResponse;
            return age >= -1 && age < 3;
        }

        private bool CanRetryRead(Request partial) {
            if (_host.AcProcessId != _processId || !ResponseFresh()) return false;
            if (partial != null && (partial.Token != _token || partial.Stamp != _stamp || partial.Phase != "preview")) return false;
            var age = _host.UnixNow - _lastHeartbeat;
            return age >= -1 && age <= 3;
        }

        public void Tick() {
            try {
                Request request;
                bool retryableIo;
                var valid = ReadRequest(out request, out retryableIo);
                if (IsActive) {
                    if (!valid && retryableIo && CanRetryRead(request)) return;
                    if (!valid || request.Token != _token || request.Stamp != _stamp || request.Phase != "preview"
                            || _host.AcProcessId != _processId || !ResponseFresh()) {
                        Stop();
                        return;
                    }
                } else {
                    if (!valid || request == null || _retired.Contains(Key(request.Token, request.Stamp))) return;
                    if (request.Phase == "closed") { _retired.Add(Key(request.Token, request.Stamp)); return; }
                    _token = request.Token;
                    _stamp = request.Stamp;
                    _processId = _host.AcProcessId;
                    _ackSequence = -1;
                    _lastResponse = _host.UnixNow;
                    _responsePending = true;
                }
                _lastHeartbeat = request.Heartbeat;
                if (!_permissionGranted) _permissionGranted = _host.SetPreviewAllowed(true);
                if (request.Sequence > _ackSequence) {
                    // Record before effects: a failed/lost acknowledgement must never repeat Toggle/Next.
                    _ackSequence = request.Sequence;
                    _responsePending = true;
                    if (request.Command == "minimize") _host.MinimizeGame(_processId);
                    else if (request.Command != "none" && _permissionGranted && _host.Available) _host.Execute(request.Command, request.Value);
                }
                if (_responsePending || _host.UnixNow != _lastResponse) {
                    try { Respond(true); }
                    catch (IOException) { _responsePending = true; }
                }
            } catch {
                Stop();
            }
        }

        private void Respond(bool active) {
            var now = _host.UnixNow;
            var available = active && _permissionGranted && _host.Available;
            var title = available ? (_host.Title ?? "") : "";
            title = new string(title.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            if (title.Length > 240) title = title.Substring(0, 240);
            var volume = _host.Volume;
            if (double.IsNaN(volume) || double.IsInfinity(volume)) volume = 0;
            volume = Math.Max(0, Math.Min(1, volume));
            _host.WriteResponse("[HEADER]\r\nTOKEN=" + _token + "\r\nSTART_STAMP=" + _stamp.ToString(CultureInfo.InvariantCulture)
                    + "\r\nHEARTBEAT=" + now.ToString(CultureInfo.InvariantCulture)
                    + "\r\nACK_SEQ=" + Math.Max(0, _ackSequence).ToString(CultureInfo.InvariantCulture)
                    + "\r\nTITLE=" + title + "\r\nPLAYING=" + (available && _host.Playing ? "1" : "0")
                    + "\r\nAVAILABLE=" + (available ? "1" : "0")
                    + "\r\nVOLUME=" + volume.ToString("0.####", CultureInfo.InvariantCulture) + "\r\n");
            _lastResponse = now;
            _responsePending = false;
        }

        public void Stop() {
            if (!IsActive) return;
            _retired.Add(Key(_token, _stamp));
            try {
                // Withdraw audio permission before replying. A locked response file cannot prolong playback.
                try { _host.SetPreviewAllowed(false); } finally { _permissionGranted = false; }
                try { Respond(false); } catch { }
            } catch { }
            finally {
                _token = null;
                _permissionGranted = false;
                _responsePending = false;
            }
        }
    }

    internal static class GtHeaderBridge {
        private static GtHeaderBridgeSession _session;

        internal static void Init() {
            if (_session != null) return;
            _session = new GtHeaderBridgeSession(new DesktopHost());
            if (Application.Current != null) Application.Current.Exit += (s, e) => _session.Stop();
        }

        internal static void Tick() { if (_session != null) _session.Tick(); }

        private sealed class DesktopHost : IGtHeaderBridgeHost {
            private readonly string _requestPath = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "cm7_header_request.ini");
            private readonly string _responsePath = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "cm7_header_response.ini");
            private readonly string _flagPath = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt_start.ini");

            private static MethodInfo PreviewMethod {
                get {
                    var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CmJaOverlay.Bgm", false)).FirstOrDefault(t => t != null);
                    return type == null ? null : type.GetMethod("SetPreviewAllowed", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
                }
            }

            private static bool Controls(out TextBlock title, out Button play, out Button next, out Slider volume) {
                title = null;
                play = next = null;
                volume = null;
                var panel = GtChrome.FindBgmPanel();
                if (panel == null) return false;
                title = panel.Children.OfType<TextBlock>().FirstOrDefault();
                play = panel.Children.OfType<Button>().FirstOrDefault(b => Equals(b.ToolTip, "BGM再生") || Equals(b.ToolTip, "BGM停止"));
                next = panel.Children.OfType<Button>().FirstOrDefault(b => Equals(b.ToolTip, "次の曲"));
                volume = panel.Children.OfType<Slider>().FirstOrDefault(s => Equals(s.ToolTip, "BGM音量"));
                return title != null && play != null && next != null && volume != null && PreviewMethod != null;
            }

            public string RequestText { get { return ReadSharedText(_requestPath); } }
            public string FlagText { get { return ReadSharedText(_flagPath); } }
            public long UnixNow { get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; } }
            public int AcProcessId { get { return GtInGameSettings.FindAcProcessId(); } }
            public bool Available {
                get { TextBlock title; Button play, next; Slider volume; return Controls(out title, out play, out next, out volume); }
            }
            public string Title {
                get {
                    TextBlock title; Button play, next; Slider volume;
                    if (!Controls(out title, out play, out next, out volume)) return "";
                    return title.Text.StartsWith("♪ ", StringComparison.Ordinal) ? title.Text.Substring(2) : title.Text;
                }
            }
            public bool Playing {
                get { TextBlock title; Button play, next; Slider volume; return Controls(out title, out play, out next, out volume) && Equals(play.ToolTip, "BGM停止"); }
            }
            public double Volume {
                get { TextBlock title; Button play, next; Slider volume; return Controls(out title, out play, out next, out volume) ? volume.Value : 0; }
            }

            public bool SetPreviewAllowed(bool allowed) {
                var method = PreviewMethod;
                if (method == null) return false;
                method.Invoke(null, new object[] { allowed });
                return true;
            }

            public void Execute(string command, double value) {
                TextBlock title; Button play, next; Slider volume;
                if (!Controls(out title, out play, out next, out volume)) return;
                if (command == "toggle") play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, play));
                else if (command == "next") next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, next));
                else if (command == "volume") {
                    volume.Value = value;
                    // The existing handler persists the same quadratic slider value as mouse use.
                    volume.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                        RoutedEvent = Mouse.PreviewMouseUpEvent,
                        Source = volume
                    });
                }
            }

            public void MinimizeGame(int processId) {
                if (processId <= 0 || AcProcessId != processId) return;
                using (var process = Process.GetProcessById(processId)) {
                    if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero) ShowWindow(process.MainWindowHandle, 6);
                }
            }

            private static string ReadSharedText(string path) {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
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

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    }
}
