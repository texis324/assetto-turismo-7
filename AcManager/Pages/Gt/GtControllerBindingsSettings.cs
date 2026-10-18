using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AcTools.DataFile;
using AcTools.Utils;

namespace AcManager.Pages.Gt {
    /// <summary>Persistent button edits, separate from the temporary race input profile.</summary>
    internal sealed class GtControllerBindingsSettings {
        internal const string RearViewSection = "tench.rearview/Rear view";
        /// <summary>[fork] 2026-09-20 要望「カーリペアキーも割り当て可能になってる？」。CSP アプリ CarRepairKey の操作（節名に小文字・空白・/ を含む）。</summary>
        internal const string RepairSection = "app.CarRepairKey/Repair car";
        /// <summary>[fork] 2026-10-04 要望「一覧にスターターボタンがない」。CSP アプリ EngineStall（Ynte 製）のスターター。押し続けるとエンジンがかかる（既定は Backspace）。</summary>
        internal const string StarterSection = "tench.stall/Starter";
        // The same physical input drives AC's external cameras. The cockpit script
        // replaces the viewpoint only in cockpit/dashboard cameras.
        private static string[] ActionSections(string section) => section == RearViewSection
                ? new[] { RearViewSection, "GLANCEBACK" } : MfdKeyOf(section) != null ? new string[0] : new[] { section };

        // ---- [fork] 2026-10-04 要望「MFD の操作も左右上下ぜんぶ割り当てられるように一覧に追加して」---------------------
        //   MFD（GT7HUD の多機能ディスプレイ）の十字キーは AC の controls.ini ではなく、GT7HUD 自身の
        //   apps\lua\GT7HUD\settings\mfd_controls.ini の [WHEEL] MFD_UP/DOWN/LEFT/RIGHT（0 始まりのボタン番号）に入っている。
        //   一覧には疑似の節名 "gt7hud:MFD_UP" などで出し、保存はそのファイルへ書く。ハンドルの機器は GT7HUD が毎回 ID と名前で探すので番号は触らない。
        internal const string MfdPrefix = "gt7hud:";
        internal static readonly string[] MfdKeys = { "MFD_UP", "MFD_DOWN", "MFD_LEFT", "MFD_RIGHT" };
        internal static string MfdSection(string key) => MfdPrefix + key;
        private static string MfdKeyOf(string section) => section != null && section.StartsWith(MfdPrefix, StringComparison.Ordinal)
                ? section.Substring(MfdPrefix.Length) : null;
        private static string MfdPath {
            get {
                var root = AcManager.Tools.Managers.AcRootDirectory.Instance?.Value;
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "apps", "lua", "GT7HUD", "settings", "mfd_controls.ini");
            }
        }

        /// <summary>MFD の各方向に割り当てられているハンドルのボタン番号（0 始まり・無ければ -1）。</summary>
        public Dictionary<string, int> ReadMfdWheel() {
            var result = MfdKeys.ToDictionary(k => k, k => -1);
            try {
                var path = MfdPath;
                if (path == null || !File.Exists(path)) return result;
                var inWheel = false;
                foreach (var raw in File.ReadAllLines(path)) {
                    var line = raw.Trim();
                    if (line.StartsWith("[")) { inWheel = line.Equals("[WHEEL]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!inWheel) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim(); int n;
                    if (result.ContainsKey(key) && int.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) result[key] = n;
                }
            } catch (Exception) { }
            return result;
        }

        /// <summary>mfdKey にこのボタンを割り当てる（null なら、このボタンの MFD 割り当てを外すだけ）。同じボタンの他の MFD 操作は外す。</summary>
        private void UpdateMfdWheel(int button, string mfdKey) {
            var path = MfdPath;
            if (path == null) throw new InvalidOperationException("ACのフォルダを取得できません。");
            var current = ReadMfdWheel();
            if (mfdKey == null && !current.Any(x => x.Value == button)) return;   // 外すものが無ければ何も書かない
            var text = File.Exists(path) ? File.ReadAllText(path) : "[WHEEL]\r\n";
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = new List<string>(Regex.Split(text, @"\r\n|\n"));
            var start = lines.FindIndex(l => l.Trim().Equals("[WHEEL]", StringComparison.OrdinalIgnoreCase));
            if (start < 0) { if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1); lines.Add("[WHEEL]"); start = lines.Count - 1; }
            var end = lines.Count;
            for (var i = start + 1; i < lines.Count; i++) if (lines[i].Trim().StartsWith("[")) { end = i; break; }
            Action<string, string> set = (key, value) => {
                for (var i = start + 1; i < end; i++)
                    if (lines[i].Trim().StartsWith(key + "=", StringComparison.Ordinal)) { lines[i] = key + "=" + value; return; }
                lines.Insert(end, key + "=" + value); end++;
            };
            foreach (var k in MfdKeys) {
                if (k == mfdKey) set(k, button.ToString(CultureInfo.InvariantCulture));
                else if (current[k] == button) set(k, "");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var backup = path + ".bak_cm7";
            if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
            var temporary = path + ".cm7-" + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary, string.Join(nl, lines.ToArray()), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                temporary = null;
            } finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception) { } }
        }
        private readonly string _cfgPath;
        private readonly string _menuPath;
        private readonly Func<bool> _isRunning;
        // Set by the authenticated preparation/pause bridge, never by the ordinary settings page.
        internal static Func<bool> CanEditLive;
        internal static Action LiveBindingsSaved;
        internal static Func<string> LiveStatus;
        private static readonly string[] PadButtons = {
            "-1", "A", "B", "X", "Y", "LSHOULDER", "RSHOULDER", "LTHUMB_PRESS", "RTHUMB_PRESS",
            "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT", "BACK", "START"
        };
        private string CustomWheelPath => Path.Combine(_cfgPath, "controllers", "savedsetups", "CM7-R5.ini");
        private string JournalPath => Path.Combine(_cfgPath, "cm7-driving-input-restore.json");
        private string LeasePath => Path.Combine(_cfgPath, "cm7-driving-input.lock");
        public string OriginalWheelPath => Path.Combine(_cfgPath, "controllers", "savedsetups", "R5.ini");
        public string WheelPath => File.Exists(CustomWheelPath) ? CustomWheelPath : OriginalWheelPath;
        public string ControlsPath => Path.Combine(_cfgPath, "controls.ini");
        public string MenuPath => _menuPath;
        // Names and zero-based defaults shared by CM's GtUniversalInput and Lua padlib.
        internal static readonly Dictionary<string, int> MenuWheelDefaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
            {"up",4},{"down",6},{"left",7},{"right",5},{"confirm",13},{"back",12},
            {"select",37},{"start",34},{"l1",19},{"r1",32},{"triangle",0},{"square",2},{"l3",21},{"r3",22}
        };

        public GtControllerBindingsSettings(string cfgPath = null, Func<bool> isRunning = null, string menuPath = null) {
            _cfgPath = Path.GetFullPath(cfgPath ?? AcPaths.GetDocumentsCfgDirectory());
            _isRunning = isRunning ?? AcRunning;
            _menuPath = Path.GetFullPath(menuPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Static", "gt7", "pad", "pad.txt"));
        }

        public bool CanEdit {
            get {
                try { return LiveAuthorized() || !_isRunning() && !File.Exists(JournalPath); }
                catch { return false; }
            }
        }

        public string StatusText {
            get { try { return LiveAuthorized() ? LiveStatus?.Invoke() : null; } catch { return null; } }
        }
        public bool CanReset => CanEdit && !LiveAuthorized();

        private bool LiveAuthorized() => CanEditLive?.Invoke() == true && GtDrivingInput.CanEditLiveBindings(_cfgPath);

        public IniFile ReadWheel() => ReadForDisplay(WheelPath);
        public IniFile ReadPad() => ReadForDisplay(ControlsPath);

        public Dictionary<string, string> ReadMenuValues(bool padParser = false) {
            using (var source = FileSnapshot.Open(MenuPath, false)) {
                Encoding encoding; byte[] preamble;
                return ParseMenuValues(source == null ? "" : DecodeMenu(source.Bytes, out encoding, out preamble), padParser);
            }
        }

        public Dictionary<string, int> ReadMenuWheelButtons() => MenuButtons(ReadMenuValues());

        private static Dictionary<string, string> ParseMenuValues(string text, bool padParser = false) {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in Regex.Split(text, @"\r\n|\r|\n")) {
                var line = raw.Trim();
                if (line.StartsWith("#") || line.StartsWith(";")) continue;
                line = padParser ? line.Split('#')[0] : line.Split('#', ';')[0];
                var eq = line.IndexOf('=');
                if (eq > 0) result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }

        private static Dictionary<string, int> MenuButtons(Dictionary<string, string> values) {
            var buttons = new Dictionary<string, int>(MenuWheelDefaults, StringComparer.OrdinalIgnoreCase);
            foreach (var key in MenuWheelDefaults.Keys) {
                string raw; int button;
                if (values.TryGetValue("wheel_" + key, out raw)
                        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out button) && button >= -1 && button < 128)
                    buttons[key] = button;
            }
            return buttons;
        }

        /// <summary>Assign a menu command and remove duplicate physical-button assignments. Never edits driving bindings.</summary>
        public bool SetMenuWheelButton(string command, int button, out string error) {
            if (command == null || !MenuWheelDefaults.ContainsKey(command) || button < -1 || button >= 128) {
                error = "メニュー操作またはボタンの指定が正しくありません。";
                return false;
            }
            string temporary = null;
            try {
                EnsureCanEdit();
                Directory.CreateDirectory(Path.GetDirectoryName(MenuPath));
                // The driving lease can be held by a valid preparation/pause session.
                // pad.txt is an independent live-read file, serialized by its own lease.
                using (new FileStream(MenuPath + ".cm7-menu.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
                using (var before = FileSnapshot.Open(MenuPath, false)) {
                    EnsureCanEdit();
                    Encoding encoding; byte[] preamble;
                    var text = DecodeMenu(before?.Bytes ?? new byte[0], out encoding, out preamble);
                    var buttons = MenuButtons(ParseMenuValues(text));
                    var edits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["wheel_" + command.ToLowerInvariant()] = button };
                    if (button >= 0) foreach (var pair in buttons)
                        if (!pair.Key.Equals(command, StringComparison.OrdinalIgnoreCase) && pair.Value == button) edits["wheel_" + pair.Key] = -1;
                    var pieces = Regex.Split(text, @"(\r\n|\r|\n)");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < pieces.Length; i += 2) {
                        // Preserve indentation, spelling, inline comments, unknown keys and every newline byte.
                        var match = Regex.Match(pieces[i], @"\A(\s*)([^\s=;#]+)(\s*=\s*)([^;#]*?)(\s*)([;#].*)?\z");
                        int value;
                        if (match.Success && edits.TryGetValue(match.Groups[2].Value, out value)) {
                            pieces[i] = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value
                                    + value.ToString(CultureInfo.InvariantCulture) + match.Groups[5].Value + match.Groups[6].Value;
                            seen.Add(match.Groups[2].Value);
                        }
                    }
                    var changed = string.Concat(pieces);
                    var newline = Regex.Match(text, @"\r\n|\r|\n");
                    var separator = newline.Success ? newline.Value : Environment.NewLine;
                    foreach (var edit in edits.Where(x => !seen.Contains(x.Key))) {
                        if (changed.Length > 0 && !changed.EndsWith("\n") && !changed.EndsWith("\r")) changed += separator;
                        changed += edit.Key + "=" + edit.Value.ToString(CultureInfo.InvariantCulture) + separator;
                    }
                    var bytes = preamble.Concat(encoding.GetBytes(changed)).ToArray();
                    if (before != null && before.Bytes.SequenceEqual(bytes)) { error = null; return true; }
                    var backupDirectory = Path.Combine(Path.GetDirectoryName(MenuPath), "cm7-menu-binding-backups");
                    Directory.CreateDirectory(backupDirectory);
                    var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                    if (before != null) Backup(before, Path.Combine(backupDirectory, stamp + "-source-pad.txt.bak"));
                    temporary = MenuPath + ".cm7-" + Guid.NewGuid().ToString("N") + ".tmp";
                    WriteNew(temporary, bytes);
                    EnsureCanEdit(); before?.Verify(); before?.Dispose();
                    if (before != null) {
                        using (var check = FileSnapshot.Open(MenuPath, true, FileShare.Read | FileShare.Delete)) {
                            if (!check.Bytes.SequenceEqual(before.Bytes)) throw new IOException("メニュー設定が別の操作で変更されました。読み直してください。");
                            EnsureCanEdit();
                            var atomicBackup = Path.Combine(backupDirectory, stamp + "-replaced-pad.txt.bak");
                            File.Replace(temporary, MenuPath, atomicBackup); temporary = null;
                            if (!ReadBytes(atomicBackup).SequenceEqual(before.Bytes)) throw new IOException("保存直前にメニュー設定が変更されました。退避ファイルを確認してください。");
                        }
                    } else { EnsureCanEdit(); File.Move(temporary, MenuPath); temporary = null; }
                    if (!ReadBytes(MenuPath).SequenceEqual(bytes)) throw new IOException("メニュー設定の保存結果を確認できませんでした。");
                }
                error = null; return true;
            } catch (Exception e) {
                error = e is IOException && e.HResult == unchecked((int)0x80070020)
                        ? "メニュー設定が使用中です。少し待ってから変更してください。" : e.Message;
                return false;
            } finally { if (temporary != null) try { File.Delete(temporary); } catch (Exception) { } }
        }

        private static string DecodeMenu(byte[] bytes, out Encoding encoding, out byte[] preamble) {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) {
                encoding = new UTF8Encoding(false, true); preamble = new byte[] { 0xEF, 0xBB, 0xBF };
            } else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) {
                encoding = new UnicodeEncoding(false, false, true); preamble = new byte[] { 0xFF, 0xFE };
            } else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) {
                encoding = new UnicodeEncoding(true, false, true); preamble = new byte[] { 0xFE, 0xFF };
            } else { encoding = new UTF8Encoding(false, true); preamble = new byte[0]; }
            return encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        }

        private static IniFile ReadForDisplay(string path) {
            using (var file = FileSnapshot.Open(path, false)) return file == null ? IniFile.Empty : Parse(file.Bytes);
        }

        public bool SetWheelButton(string section, int button, out string error) {
            if (!ValidSection(section) || button < -1 || button > 127) {
                error = "ボタンの指定が正しくありません。";
                return false;
            }
            return Save(() => WheelPath, CustomWheelPath, ini => {
                if (new[] { "STEER", "THROTTLE", "BRAKES", "CLUTCH", "SHIFTER" }.Contains(section, StringComparer.Ordinal)
                        || ini.ContainsKey(section) && ini[section].GetInt("AXLE", -1) >= 0)
                    throw new InvalidOperationException("ハンドル・ペダル・アナログ軸の割当はこの画面では変更できません。");
                int joy;
                var rawJoy = ini.ContainsKey("STEER") ? ini["STEER"].GetPossiblyEmpty("JOY") : null;
                if (!int.TryParse(rawJoy, NumberStyles.Integer, CultureInfo.InvariantCulture, out joy) || joy < 0
                        || !HasControllerIdentity(ini, joy))
                    throw new InvalidDataException("R5設定のハンドル機器を識別できません。R5.iniを確認してください。");
                foreach (var targetSection in ActionSections(section)) {
                    if (section == RearViewSection && !SimpleWheelTarget(ini, targetSection, joy))
                        throw new InvalidOperationException("後方視点に別の機器・複合ボタンの割当があります。標準CMで確認してください。");
                    var target = ini[targetSection];
                    foreach (var key in target.Keys.ToArray()) {
                        if (key.Equals("POV", StringComparison.OrdinalIgnoreCase)
                                || key.StartsWith("__CM_POV", StringComparison.OrdinalIgnoreCase)
                                || key.StartsWith("__CM_ALT", StringComparison.OrdinalIgnoreCase)
                                || key.Equals("JOY_MODIFICATOR", StringComparison.OrdinalIgnoreCase)
                                || key.Equals("BUTTON_MODIFICATOR", StringComparison.OrdinalIgnoreCase)) target.Remove(key);
                    }
                    target.Set("JOY", joy);
                    target.Set("BUTTON", button);
                }
            }, out error);
        }

        public bool SetPadButton(string section, string button, out string error) {
            if (!ValidSection(section) || !PadButtons.Contains(button, StringComparer.Ordinal)) {
                error = "パッドボタンの指定が正しくありません。";
                return false;
            }
            return Save(() => ControlsPath, ControlsPath, ini => {
                foreach (var target in ActionSections(section)) ini[target].Set("XBOXBUTTON", button);
            }, out error);
        }

        /// <summary>One physical ES button owns one simple action. Complex or other-device bindings are never replaced.</summary>
        public bool SetWheelAction(int button, string sectionOrNull, out string error) {
            var section = string.IsNullOrEmpty(sectionOrNull) ? null : sectionOrNull;
            if (button < 0 || button > 127 || section != null && !ValidSection(section)) {
                error = "ボタンの指定が正しくありません。";
                return false;
            }
            return Save(() => WheelPath, CustomWheelPath, ini => {
                var joy = ini.ContainsKey("STEER") ? ini["STEER"].GetInt("JOY", -1) : -1;
                if (joy < 0 || !HasControllerIdentity(ini, joy)) throw new InvalidDataException("R5設定のハンドル機器を識別できません。");
                // [fork] 2026-10-04 要望「コントローラー割り当て画面でカーリペアキーを割り当てようとしたが、うまくいかない」。
                //   画面側（AvailableWheelAction）は「車を修理」を別機器（シフター）からでも選べるようにしていたが、ここで
                //   「別の機器に入っている」を理由に必ず拒否していた＝選ぶたびに下のエラーになっていた。車を修理だけは別機器からハンドルへ移せる
                //   （アナログ軸・複合ボタン・修飾ボタンは従来どおり拒否）。
                var movable = section == RepairSection || section == StarterSection;
                if (section != null && ActionSections(section).Any(target => !SimpleWheelTarget(ini, target, joy, movable)))
                    throw new InvalidOperationException("この操作にはアナログ軸・別の機器・複合ボタンの割当があります。標準CMで確認してください。");
                foreach (var entry in ini.ToArray()) {
                    if (SimpleWheelTarget(ini, entry.Key, joy) && entry.Value.GetInt("JOY", -1) == joy
                            && entry.Value.GetInt("BUTTON", -1) == button) entry.Value.Set("BUTTON", -1);
                }
                if (section != null) {
                    foreach (var target in ActionSections(section)) {
                        ini[target].Set("JOY", joy);
                        ini[target].Set("BUTTON", button);
                    }
                }
            }, out error);
        }

        public bool SetPadAction(string xboxButton, string sectionOrNull, out string error) {
            var section = string.IsNullOrEmpty(sectionOrNull) ? null : sectionOrNull;
            if (xboxButton == "-1" || xboxButton == "START" || !PadButtons.Contains(xboxButton, StringComparer.Ordinal)
                    || section != null && (!ValidSection(section) || IsAxisSection(section) || section == "__CM_PAUSE")) {
                error = "パッドボタンの指定が正しくありません。OPTIONS / STARTはポーズ専用です。";
                return false;
            }
            return Save(() => ControlsPath, ControlsPath, ini => {
                foreach (var entry in ini.ToArray()) {
                    if (!IsAxisSection(entry.Key) && entry.Key != "__CM_PAUSE"
                            && entry.Value.GetPossiblyEmpty("XBOXBUTTON") == xboxButton) entry.Value.Set("XBOXBUTTON", "-1");
                }
                if (section != null) foreach (var target in ActionSections(section)) ini[target].Set("XBOXBUTTON", xboxButton);
            }, out error);
        }

        private static bool IsAxisSection(string section) => new[] { "STEER", "THROTTLE", "BRAKES", "CLUTCH", "SHIFTER" }
                .Contains(section, StringComparer.Ordinal);

        private static bool SimpleWheelTarget(IniFile ini, string section, int joy, bool movable = false) {
            if (IsAxisSection(section)) return false;
            if (!ini.ContainsKey(section)) return true;
            var target = ini[section];
            if (target.GetInt("AXLE", -1) >= 0 || target.GetInt("BUTTON_MODIFICATOR", -1) >= 0
                    || target.GetInt("JOY_MODIFICATOR", -1) >= 0 || target.GetInt("POV", -1) >= 0
                    || target.GetInt("__CM_POV", -1) >= 0 || target.GetInt("__CM_ALT_BUTTON", -1) >= 0
                    || target.GetInt("__CM_ALT_POV", -1) >= 0) return false;
            var oldJoy = target.GetInt("JOY", -1);
            return oldJoy < 0 || oldJoy == joy || movable;   // [fork] movable = 別の機器に入っていてもハンドルへ移してよい（車を修理）
        }

        public bool ResetWheel(out string error) {
            if (LiveAuthorized()) { error = "全体の初期化はACを終了してから行ってください。走行中は各ボタンを変更できます。"; return false; }
            return Save(() => OriginalWheelPath, CustomWheelPath, null, out error);
        }

        private static bool ValidSection(string section) => section != null && section.Length <= 128
                && (section == RearViewSection || section == RepairSection || section == StarterSection
                    || MfdKeyOf(section) != null && MfdKeys.Contains(MfdKeyOf(section))
                    || Regex.IsMatch(section, @"\A[A-Z0-9_]+\z", RegexOptions.CultureInvariant));

        private static bool HasControllerIdentity(IniFile ini, int joy) {
            if (!ini.ContainsKey("CONTROLLERS")) return false;
            Guid id;
            return Guid.TryParse(ini["CONTROLLERS"].GetPossiblyEmpty("__IGUID" + joy), out id) && id != Guid.Empty
                    || Guid.TryParse(ini["CONTROLLERS"].GetPossiblyEmpty("PGUID" + joy), out id) && id != Guid.Empty;
        }

        private void EnsureCanEdit() {
            if (!CanEdit) throw new InvalidOperationException("ACの終了と入力設定の復元が完了してから変更してください。");
        }

        private FileStream AcquireLease() {
            Directory.CreateDirectory(_cfgPath);
            // Same lease as GtDrivingInput: no launch/recovery can overlap an edit.
            return new FileStream(LeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        }

        private bool Save(Func<string> getSourcePath, string destination, Action<IniFile> change, out string error) {
            string temporary = null;
            try {
                EnsureCanEdit();
                if (LiveAuthorized()) {
                    if (change == null) throw new InvalidOperationException("走行中は各ボタンを変更してください。");
                    GtDrivingInput.EditLiveBindings(_cfgPath, LiveAuthorized, getSourcePath, destination, change);
                    // A reload notification failure cannot turn a committed save into a reported rollback.
                    try { LiveBindingsSaved?.Invoke(); } catch (Exception) { }
                    error = null;
                    return true;
                }
                using (AcquireLease()) {
                    EnsureCanEdit();
                    var sourcePath = getSourcePath(); // Resolve custom/original only after acquiring the lease.
                    using (var source = FileSnapshot.Open(sourcePath, true))
                    using (var previous = sourcePath.Equals(destination, StringComparison.OrdinalIgnoreCase)
                            ? null : FileSnapshot.Open(destination, false)) {
                        var before = sourcePath.Equals(destination, StringComparison.OrdinalIgnoreCase) ? source : previous;
                        byte[] bytes;
                        if (change == null) bytes = source.Bytes; // Reset is an exact clone of the original R5 preset.
                        else {
                            var ini = Parse(source.Bytes);
                            change(ini);
                            bytes = new UTF8Encoding(false).GetBytes(ini.Stringify());
                        }
                        var backupDirectory = Path.Combine(_cfgPath, "cm7-binding-backups");
                        Directory.CreateDirectory(backupDirectory);
                        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                        Backup(source, Path.Combine(backupDirectory, stamp + "-source-" + Path.GetFileName(sourcePath) + ".bak"));
                        if (previous != null) Backup(previous, Path.Combine(backupDirectory, stamp + "-previous-" + Path.GetFileName(destination) + ".bak"));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        temporary = destination + ".cm7-" + Guid.NewGuid().ToString("N") + ".tmp";
                        WriteNew(temporary, bytes);
                        EnsureCanEdit();
                        source.Verify();
                        previous?.Verify();
                        // Release the destination's delete-denying read handle, then compare again
                        // under a read handle which permits the single atomic replacement below.
                        before?.Dispose();
                        if (before != null) {
                            using (var check = FileSnapshot.Open(destination, true, FileShare.Read | FileShare.Delete)) {
                                if (!check.Bytes.SequenceEqual(before.Bytes)) throw new IOException("設定が別の操作で変更されました。読み直してから変更してください。");
                                EnsureCanEdit();
                                // Preserve the precise file replaced by the atomic operation as well.
                                var atomicBackup = Path.Combine(backupDirectory, stamp + "-replaced-" + Path.GetFileName(destination) + ".bak");
                                File.Replace(temporary, destination, atomicBackup);
                                temporary = null;
                                if (!ReadBytes(atomicBackup).SequenceEqual(before.Bytes))
                                    throw new IOException("保存直前に別の操作が設定を変更しました。退避ファイルを確認してください: " + atomicBackup);
                            }
                        } else {
                            EnsureCanEdit();
                            File.Move(temporary, destination); // Fails safely if another editor created it meanwhile.
                            temporary = null;
                        }
                        if (!ReadBytes(destination).SequenceEqual(bytes))
                            throw new IOException("設定の保存結果を確認できませんでした。退避ファイルを確認してください。");
                    }
                }
                error = null;
                return true;
            } catch (Exception e) {
                error = e is IOException && e.HResult == unchecked((int)0x80070020)
                        ? "入力設定が使用中です。ACの終了・復元後にもう一度変更してください。" : e.Message;
                return false;
            } finally {
                if (temporary != null) try { File.Delete(temporary); } catch (Exception) { }
            }
        }

        private static void Backup(FileSnapshot snapshot, string path) {
            WriteNew(path, snapshot.Bytes);
            if (!ReadBytes(path).SequenceEqual(snapshot.Bytes)) throw new IOException("入力設定の退避を確認できませんでした。");
        }

        private static void WriteNew(string path, byte[] bytes) {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static IniFile Parse(byte[] bytes) {
            using (var memory = new MemoryStream(bytes, false))
            using (var reader = new StreamReader(memory, Encoding.UTF8, true)) return IniFile.Parse(reader.ReadToEnd(), IniFileMode.Comments);
        }

        private static byte[] ReadBytes(string path) {
            using (var file = FileSnapshot.Open(path, true)) return file.Bytes;
        }

        private sealed class FileSnapshot : IDisposable {
            private readonly string _path;
            private FileStream _stream;
            public readonly byte[] Bytes;
            private FileSnapshot(string path, FileStream stream) {
                _path = path;
                _stream = stream;
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); Bytes = memory.ToArray(); }
            }
            public static FileSnapshot Open(string path, bool required, FileShare share = FileShare.Read) {
                FileStream stream = null;
                try {
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
                    return new FileSnapshot(path, stream);
                }
                catch (FileNotFoundException) { if (required) throw; return null; }
                catch (DirectoryNotFoundException) { if (required) throw; return null; }
                catch { stream?.Dispose(); throw; }
            }
            public void Verify() {
                if (!ReadBytes(_path).SequenceEqual(Bytes)) throw new IOException("入力設定が読み取り後に変更されました。");
            }
            public void Dispose() { _stream?.Dispose(); _stream = null; }
        }

        private static bool AcRunning() {
            foreach (var name in new[] { "acs", "acs_x86" }) {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Length > 0) return true; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return false;
        }
    }
}
