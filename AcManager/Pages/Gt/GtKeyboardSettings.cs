using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using AcTools.Utils;

namespace AcManager.Pages.Gt {
    /// <summary>CM7 keyboard overrides. The original AC controls.ini is always read-only.</summary>
    internal sealed class GtKeyboardSettings {
        internal sealed class BindingAction {
            public readonly string Id;
            public readonly string Label;
            public readonly int DefaultKey;
            /// <summary>[fork] 2026-09-19: null = 走行系。素の割り当ては controls.ini の [KEYBOARD] Id、CM7 の上書きは DS4 ブリッジが pad.txt から読んで仮想パッドへ合成する。
            /// 値あり = AC 本体がその節の KEY= を直接読む操作（視点・ライトなど）。CM7 の上書きは走行の間だけ controls.ini へ反映して、終了時に戻す（GtDrivingInput）。</summary>
            public readonly string NativeSection;
            public readonly string Group;

            internal BindingAction(string id, string label, int defaultKey, string nativeSection = null, string group = "走行") {
                Id = id;
                Label = label;
                DefaultKey = defaultKey;
                NativeSection = nativeSection;
                Group = group;
            }
        }

        internal static readonly BindingAction[] Actions = {
            new BindingAction("gas", "アクセル", 0x57),
            new BindingAction("brake", "ブレーキ", 0x53),
            new BindingAction("left", "左に曲がる", 0x41),
            new BindingAction("right", "右に曲がる", 0x44),
            new BindingAction("gear_up", "シフトアップ", 0x54),
            new BindingAction("gear_down", "シフトダウン", 0x47),
            new BindingAction("handbrake", "ハンドブレーキ", 0x20),
            // [fork] 2026-09-19 要望「割り当て項目が少なすぎる。少なくとも後ろを見る・横を見る」。既定値は AC の標準キー
            new BindingAction("look_left", "左を見る", 0x51, "GLANCELEFT", "視点"),
            new BindingAction("look_right", "右を見る", 0x45, "GLANCERIGHT", "視点"),
            new BindingAction("look_back", "後ろを見る", 0x46, "GLANCEBACK", "視点"),
            new BindingAction("camera", "視点を切り替える", 0x56, "ACTION_CHANGE_CAMERA", "視点"),
            new BindingAction("headlights", "ヘッドライト", 0x4C, "ACTION_HEADLIGHTS", "そのほか"),
            new BindingAction("horn", "ホーン", 0x48, "ACTION_HORN", "そのほか")
            // ※ AI 走行(Ctrl+C)などの「Ctrl と組で効くキー」は修飾キーの扱いが別なので、ここには入れていない
        };

        /// <summary>[fork] pad.txt に明示の上書きがある「AC 本体が読むキー」= 節名 → controls.ini の KEY に書く値。
        /// 上書きが無い操作は入らない（controls.ini のまま）。重複で無効になった物は -1（未割り当て）。</summary>
        internal static Dictionary<string, string> NativeOverrides(string padPath = null, string controlsPath = null) {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var settings = new GtKeyboardSettings(padPath, controlsPath, () => false);
            var pad = ParseValues(FileSnapshot.Read(settings._padPath).Text, null);
            foreach (var action in Actions.Where(x => x.NativeSection != null)) {
                int ignored;
                if (!TryKey(GetValue(pad, "keyboard_" + action.Id), out ignored)) continue;
                var key = settings._keys[action.Id];
                result[action.NativeSection] = key < 0 ? "-1" : "0x" + key.ToString("X2", CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static readonly object SaveLock = new object();
        private readonly string _padPath;
        private readonly string _controlsPath;
        private readonly Func<bool> _isDriving;
        private Dictionary<string, int> _keys = Actions.ToDictionary(x => x.Id, x => x.DefaultKey);
        private int _assist;

        public GtKeyboardSettings(string padPath = null, string controlsPath = null, Func<bool> isDriving = null) {
            _padPath = Path.GetFullPath(padPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Data", "Static", "gt7", "pad", "pad.txt"));
            _controlsPath = Path.GetFullPath(controlsPath ?? Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "controls.ini"));
            _isDriving = isDriving ?? IsDriving;
            Reload();
        }

        public int Assist {
            get { Reload(); return _assist; }
        }

        public int GetKey(string id) {
            var action = FindAction(id);
            if (action == null) throw new ArgumentException("Unknown keyboard action", "id");
            Reload();
            return _keys[action.Id];
        }

        public void Reload() {
            try {
                ApplySnapshot(FileSnapshot.Read(_padPath), FileSnapshot.Read(_controlsPath));
            } catch (IOException) {
                // A transient lock must not replace the last readable settings with defaults.
            } catch (UnauthorizedAccessException) {
            } catch (DecoderFallbackException) {
            }
        }

        private void ApplySnapshot(FileSnapshot pad, FileSnapshot controls) {
            var values = ParseValues(pad.Text, null);
            _keys = EffectiveKeys(values, controls.Text);
            int percent;
            _assist = TryInteger(GetValue(values, "keyboard_assist"), out percent) && percent >= 0 && percent <= 100 ? percent : 0;
        }

        public bool TrySetAssist(int percent, out string error) {
            if (percent < 0 || percent > 100) {
                error = "アシストは 0～100％で指定してください。";
                return false;
            }
            return Save("keyboard_assist", percent, null, false, out error);
        }

        public bool TrySetKey(string id, int vk, out string error) {
            var action = FindAction(id);
            if (action == null) {
                error = "変更する操作が見つかりません。";
                return false;
            }
            if (!IsAssignableKey(vk)) {
                error = "このキーは割り当てできません。文字・数字・矢印などのキーを押してください。";
                return false;
            }
            return Save("keyboard_" + action.Id, vk, action, false, out error);
        }

        public bool TryResetBindings(out string error) {
            return Save(null, 0, null, true, out error);
        }

        private bool Save(string key, int value, BindingAction action, bool reset, out string error) {
            error = null;
            lock (SaveLock) {
                string temporary = null;
                try {
                    if (_isDriving()) {
                        error = "走行中は変更できません。AC を終了してから変更してください。";
                        return false;
                    }
                    var pad = FileSnapshot.Read(_padPath);
                    var controls = FileSnapshot.Read(_controlsPath);
                    if (action != null) {
                        var effective = EffectiveKeys(ParseValues(pad.Text, null), controls.Text, false);
                        var duplicate = Actions.FirstOrDefault(x => x.Id != action.Id && effective[x.Id] == value);
                        if (duplicate != null) {
                            error = KeyName(value) + " は「" + duplicate.Label + "」に割り当て済みです。先にその割り当てを変更してください。";
                            return false;
                        }
                    }
                    var nextText = EditValues(pad.Text, key, value, reset);
                    var nextBytes = pad.Encode(nextText);
                    var directory = Path.GetDirectoryName(_padPath);
                    Directory.CreateDirectory(directory);
                    temporary = Path.Combine(directory, "." + Path.GetFileName(_padPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        stream.Write(nextBytes, 0, nextBytes.Length);
                        stream.Flush(true);
                    }
                    // Recheck after all preparation: AC may have started or either source may have changed.
                    if (_isDriving()) {
                        error = "走行が始まったため保存しませんでした。AC を終了してから変更してください。";
                        return false;
                    }
                    if (!pad.SameContent(FileSnapshot.Read(_padPath)) || !controls.SameContent(FileSnapshot.Read(_controlsPath))) {
                        error = "設定が別の操作で更新されました。画面を開き直して、もう一度変更してください。";
                        return false;
                    }
                    if (pad.Exists) {
                        File.Replace(temporary, _padPath, null);
                    } else {
                        File.Move(temporary, _padPath);
                    }
                    temporary = null;
                    pad.Text = nextText;
                    ApplySnapshot(pad, controls);
                    return true;
                } catch (Exception e) {
                    // Do not include config content or user paths in the UI/error channel.
                    error = e is UnauthorizedAccessException ? "設定ファイルへの書き込みが許可されていません。"
                            : e is DecoderFallbackException ? "設定ファイルの文字コードを読み取れないため、保存しませんでした。"
                            : "設定を保存できませんでした。ほかのアプリで設定を編集中でないか確認してください。";
                    return false;
                } finally {
                    if (temporary != null) {
                        try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }

        public static bool IsDriving() {
            try {
                foreach (var name in new[] { "acs", "acs_x86", "acs_pro" }) {
                    var processes = Process.GetProcessesByName(name);
                    try {
                        if (processes.Length > 0) return true;
                    } finally {
                        foreach (var process in processes) process.Dispose();
                    }
                }
                return false;
            } catch {
                return true; // If process enumeration fails, keep writes disabled.
            }
        }

        public static bool IsAssignableKey(int vk) {
            return vk == 0x08 || vk == 0x09 || vk == 0x0D || vk == 0x20
                    || vk >= 0x21 && vk <= 0x28 || vk == 0x2D || vk == 0x2E
                    || vk >= 0x30 && vk <= 0x39 || vk >= 0x41 && vk <= 0x5A
                    || vk >= 0x60 && vk <= 0x6F || vk >= 0xBA && vk <= 0xC0
                    || vk >= 0xDB && vk <= 0xDE || vk == 0xE2;
        }

        public static string KeyName(int vk) {
            if (vk == -1) return "未割り当て";
            if (vk >= 0x30 && vk <= 0x39 || vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60).ToString(CultureInfo.InvariantCulture);
            switch (vk) {
                case 0x20: return "Space";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x0D: return "Enter";
                case 0x08: return "Backspace";
            }
            var key = vk > 0 && vk < 255 ? KeyInterop.KeyFromVirtualKey(vk) : Key.None;
            return key != Key.None ? key.ToString() : "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
        }

        private static BindingAction FindAction(string id) {
            return Actions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, int> EffectiveKeys(Dictionary<string, string> pad, string controls, bool suppressDuplicates = true) {
            var native = ParseValues(controls, "KEYBOARD");
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in Actions) {
                int value;
                // 走行系は [KEYBOARD] の Id、視点などは各節の KEY が「素の割り当て」
                var nativeText = action.NativeSection == null ? GetValue(native, action.Id)
                        : GetValue(ParseValues(controls, action.NativeSection), "KEY");
                var fallback = TryKey(nativeText, out value) ? value : action.DefaultKey;
                result[action.Id] = TryKey(GetValue(pad, "keyboard_" + action.Id), out value) ? value : fallback;
            }
            if (suppressDuplicates) {
                var duplicates = new HashSet<int>(result.Values.Where(x => x >= 0).GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key));
                foreach (var id in result.Keys.ToArray()) {
                    if (duplicates.Contains(result[id])) result[id] = -1;
                }
            }
            return result;
        }

        private static bool TryKey(string text, out int value) {
            return TryInteger(text, out value) && (value == -1 || value > 0 && value < 255);
        }

        private static bool TryInteger(string text, out int value) {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
                    : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static string GetValue(Dictionary<string, string> values, string key) {
            string result;
            return values.TryGetValue(key, out result) ? result : null;
        }

        private static Dictionary<string, string> ParseValues(string text, string section) {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inSection = section == null;
            foreach (var line in Lines(text)) {
                var clean = WithoutComment(line.Text).Trim();
                if (clean.StartsWith("[") && clean.EndsWith("]")) {
                    inSection = section == null || string.Equals(clean.Substring(1, clean.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                var equal = clean.IndexOf('=');
                if (inSection && equal > 0) result[clean.Substring(0, equal).Trim()] = clean.Substring(equal + 1).Trim();
            }
            return result;
        }

        private static int CommentAt(string text) {
            var hash = text.IndexOf('#');
            var semicolon = text.IndexOf(';');
            return hash < 0 ? semicolon : semicolon < 0 ? hash : Math.Min(hash, semicolon);
        }

        private static string WithoutComment(string text) {
            var index = CommentAt(text);
            return index < 0 ? text : text.Substring(0, index);
        }

        private static string EditValues(string text, string key, int value, bool reset) {
            var lines = Lines(text);
            var newline = lines.Select(x => x.End).FirstOrDefault(x => x.Length > 0) ?? Environment.NewLine;
            var output = new StringBuilder();
            var found = false;
            foreach (var line in lines) {
                var clean = WithoutComment(line.Text);
                var equal = clean.IndexOf('=');
                var name = equal < 0 ? "" : clean.Substring(0, equal).Trim();
                var matched = reset ? Actions.Any(x => string.Equals("keyboard_" + x.Id, name, StringComparison.OrdinalIgnoreCase))
                        : string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
                if (!matched) {
                    output.Append(line.Text).Append(line.End);
                    continue;
                }
                found = true;
                var comment = CommentAt(line.Text);
                if (reset) {
                    if (comment >= 0) output.Append(line.Text.Substring(comment)).Append(line.End);
                    continue;
                }
                var start = equal + 1;
                while (start < clean.Length && char.IsWhiteSpace(clean[start])) ++start;
                var end = clean.Length;
                while (end > start && char.IsWhiteSpace(clean[end - 1])) --end;
                output.Append(line.Text.Substring(0, start)).Append(value.ToString(CultureInfo.InvariantCulture))
                        .Append(line.Text.Substring(end)).Append(line.End);
            }
            if (!reset && !found) {
                if (output.Length > 0 && output[output.Length - 1] != '\n' && output[output.Length - 1] != '\r') output.Append(newline);
                output.Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture)).Append(newline);
            }
            return output.ToString();
        }

        private sealed class ConfigLine {
            public string Text;
            public string End;
        }

        private static List<ConfigLine> Lines(string text) {
            var lines = new List<ConfigLine>();
            var position = 0;
            while (position < text.Length) {
                var start = position;
                while (position < text.Length && text[position] != '\r' && text[position] != '\n') ++position;
                var line = text.Substring(start, position - start);
                start = position;
                if (position < text.Length && text[position++] == '\r' && position < text.Length && text[position] == '\n') ++position;
                lines.Add(new ConfigLine { Text = line, End = text.Substring(start, position - start) });
            }
            return lines;
        }

        private sealed class FileSnapshot {
            public bool Exists;
            public byte[] Bytes;
            public string Text;
            private Encoding _encoding;
            private int _preamble;

            public static FileSnapshot Read(string path) {
                var snapshot = new FileSnapshot { Exists = true };
                try {
                    snapshot.Bytes = File.ReadAllBytes(path);
                } catch (FileNotFoundException) {
                    snapshot.Exists = false;
                    snapshot.Bytes = new byte[0];
                } catch (DirectoryNotFoundException) {
                    snapshot.Exists = false;
                    snapshot.Bytes = new byte[0];
                }
                var bytes = snapshot.Bytes;
                snapshot._encoding = new UTF8Encoding(false, true);
                if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) {
                    snapshot._encoding = new UTF32Encoding(false, true, true);
                    snapshot._preamble = 4;
                } else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) {
                    snapshot._encoding = new UTF32Encoding(true, true, true);
                    snapshot._preamble = 4;
                } else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) {
                    snapshot._encoding = new UTF8Encoding(true, true);
                    snapshot._preamble = 3;
                } else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) {
                    snapshot._encoding = new UnicodeEncoding(false, true, true);
                    snapshot._preamble = 2;
                } else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) {
                    snapshot._encoding = new UnicodeEncoding(true, true, true);
                    snapshot._preamble = 2;
                }
                snapshot.Text = snapshot._encoding.GetString(bytes, snapshot._preamble, bytes.Length - snapshot._preamble);
                return snapshot;
            }

            public byte[] Encode(string text) {
                var body = _encoding.GetBytes(text);
                return _preamble == 0 ? body : _encoding.GetPreamble().Concat(body).ToArray();
            }

            public bool SameContent(FileSnapshot other) {
                return Exists == other.Exists && Bytes.SequenceEqual(other.Bytes);
            }
        }
    }
}
