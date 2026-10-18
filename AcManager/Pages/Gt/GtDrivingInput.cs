using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AcManager.Tools.SemiGui;
using AcTools.DataFile;
using AcTools.Processes;
using AcTools.Utils;
using FirstFloor.ModernUI.Helpers;
using Newtonsoft.Json;
using SlimDX.DirectInput;

namespace AcManager.Pages.Gt {
    /// <summary>Temporary offline driving input profile. Physical device changes take effect next launch.</summary>
    internal static class GtDrivingInput {
        private static bool _initialized;
        private static int _active;
        private static readonly object SessionGate = new object();
        private static Session _bindingSession;
        private static string ControlsPath => AcPaths.GetCfgControlsFilename();
        private static string JournalPath => Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "cm7-driving-input-restore.json");
        private static string LeasePath => Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "cm7-driving-input.lock");
        internal static string OriginalWheelPath => Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "controllers", "savedsetups", "R5.ini");
        internal static string WheelPath {
            get {
                var edited = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "controllers", "savedsetups", "CM7-R5.ini");
                return File.Exists(edited) ? edited : OriginalWheelPath;
            }
        }
        private static string SwitchPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Static", "gt7", "pad", "pad.txt");

        internal static bool Enabled {
            get {
                try {
                    if (!File.Exists(SwitchPath)) return false;
                    var enabled = false;
                    foreach (var original in File.ReadLines(SwitchPath)) {
                        var line = original.Split('#', ';')[0];
                        var equal = line.IndexOf('=');
                        if (equal < 0 || !line.Substring(0, equal).Trim().Equals("universal_driving", StringComparison.OrdinalIgnoreCase)) continue;
                        var value = line.Substring(equal + 1).Trim();
                        enabled = value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                    return enabled;
                } catch (Exception e) { Logging.Warning("[GtDrivingInput] switch: " + e.Message); }
                return false;
            }
        }

        public static void Init() {
            if (_initialized) return;
            _initialized = true;
            // Recovery remains enabled even if the user has since switched the feature off.
            try { RecoverBeforePresets(); }
            catch (Exception e) { Logging.Warning("[GtDrivingInput] recovery: " + e); }
            GameWrapper.SetPropertiesCallback(properties => {
                var launch = GtGameUi.Pending;
                if (!Enabled || launch == null || !IsGtLaunchPending() || properties.ReplayProperties != null || properties.BenchmarkProperties != null
                        || properties.ModeProperties is Game.OnlineProperties
                        || Section(properties.PreparedConfig, "REMOTE")?.GetBool("ACTIVE", false) == true) return;
                RecoverBeforePresets(); // Do not retry an old journal after a new run's presets have already been applied.
                // Set follows RaceIniProperties/presets; insertion-first Dispose precedes their restorers.
                foreach (var previous in properties.AdditionalPropertieses.OfType<Session>().ToArray()) {
                    if (ReferenceEquals(previous.Launch, launch)) return;
                    properties.AdditionalPropertieses.Remove(previous);
                }
                properties.AdditionalPropertieses.Insert(0, new Session(launch));
            });
        }

        /// <summary>Call before App.RevertFileChanges restores mode/car presets. Failure must stop the later preset restores.</summary>
        internal static void RecoverBeforePresets() {
            if (File.Exists(JournalPath)) using (AcquireLease()) RecoverLocked();
        }

        internal sealed class Device {
            public string Name;
            public Guid InstanceGuid, ProductGuid;
        }
        internal sealed class Change {
            public string Section, Key, Before, After;
        }
        internal sealed class Candidate {
            public bool Wheel;
            public int WheelIndex = -1;
            public int UnresolvedBindings;
            public List<Change> Changes = new List<Change>();
        }
        private sealed class RestoreState {
            public int Version = 1;
            public int Owner;
            public long OwnerStartedUtc;
            public List<Change> Changes;
        }

        private static Device[] GetDevices() {
            using (var input = new SlimDX.DirectInput.DirectInput()) {
                // No sorting: AC uses the same DirectInput attached-device enumeration indices.
                return input.GetDevices(DeviceClass.GameController, DeviceEnumerationFlags.AttachedOnly)
                        .Select(x => new Device { Name = x.InstanceName, InstanceGuid = x.InstanceGuid, ProductGuid = x.ProductGuid }).ToArray();
            }
        }

        private static IniFileSection Section(IniFile ini, string section) => ini != null && ini.ContainsKey(section) ? ini[section] : null;
        private static string Value(IniFile ini, string section, string key) => Section(ini, section)?.GetPossiblyEmpty(key);

        /// <summary>[fork] controls.ini の KEY= は "0x62" と "98" の両方の書き方がある。</summary>
        private static bool TryVirtualKey(string text, out int key) {
            key = -1;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;
            var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key)
                    : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out key);
            return parsed && key > 0;
        }

        /// <summary>[fork] GT7 HUD の MFD を動かすハンコンのボタン番号。apps\lua\GT7HUD\settings\mfd_controls.ini の [WHEEL]、無ければ ES の十字（4〜7）。
        /// 0 / 9000 / 18000 / 27000 はボタンではなくハットスイッチの向きを表す値なので除く（Lua 側と同じ解釈）。</summary>
        private static HashSet<int> MfdWheelButtons() {
            var defaults = new Dictionary<string, int> { { "MFD_UP", 4 }, { "MFD_RIGHT", 5 }, { "MFD_DOWN", 6 }, { "MFD_LEFT", 7 } };
            IniFileSection section = null;
            try {
                var root = AcManager.Tools.Managers.AcRootDirectory.Instance?.Value;
                var path = root == null ? null : Path.Combine(root, "apps", "lua", "GT7HUD", "settings", "mfd_controls.ini");
                if (path != null && File.Exists(path)) section = Section(new IniFile(path), "WHEEL");
            } catch (Exception e) { Logging.Warning("[GtDrivingInput] mfd_controls.ini: " + e.Message); }
            var result = new HashSet<int>();
            foreach (var pair in defaults) {
                int button;
                var text = section?.GetPossiblyEmpty(pair.Key);
                if (!int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out button)) button = pair.Value;
                if (button > 0 && button != 9000 && button != 18000 && button != 27000) result.Add(button);
            }
            return result;
        }

        /// <summary>[fork] GT7 HUD の MFD を動かすキー。apps\lua\GT7HUD\settings\mfd_controls.ini の [KEYBOARD]、無ければ Lua 側と同じ既定（テンキー 4/6/8/2）。</summary>
        private static HashSet<int> MfdKeyboardKeys() {
            var defaults = new Dictionary<string, int> { { "MFD_LEFT", 0x64 }, { "MFD_RIGHT", 0x66 }, { "MFD_UP", 0x68 }, { "MFD_DOWN", 0x62 } };
            IniFileSection section = null;
            try {
                var root = AcManager.Tools.Managers.AcRootDirectory.Instance?.Value;
                var path = root == null ? null : Path.Combine(root, "apps", "lua", "GT7HUD", "settings", "mfd_controls.ini");
                if (path != null && File.Exists(path)) section = Section(new IniFile(path), "KEYBOARD");
            } catch (Exception e) { Logging.Warning("[GtDrivingInput] mfd_controls.ini: " + e.Message); }
            var result = new HashSet<int>();
            foreach (var pair in defaults) {
                int key;
                result.Add(section != null && TryVirtualKey(section.GetPossiblyEmpty(pair.Key), out key) ? key : pair.Value);
            }
            return result;
        }

        private static bool IsDeviceKey(string key) => key.Equals("JOY", StringComparison.OrdinalIgnoreCase)
                || key.Equals("JOY_MODIFICATOR", StringComparison.OrdinalIgnoreCase)
                || key.Equals("__CM_ALT_JOY", StringComparison.OrdinalIgnoreCase);

        // These are CM's WheelButtonEntry, WheelButtonAltEntry and WheelAxleEntry fields.
        // Do not copy KEY/KEY_MODIFICATOR/XBOXBUTTON or steering/FFB tuning from the wheel preset.
        private static bool IsWheelBindingKey(string section, string key, bool axis) {
            if (IsDeviceKey(key)) return true;
            switch (key.ToUpperInvariant()) {
                case "AXLE": case "BUTTON": case "BUTTON_MODIFICATOR":
                case "__CM_POV": case "__CM_POV_DIR":
                case "__CM_ALT_BUTTON": case "__CM_ALT_POV": case "__CM_ALT_POV_DIR": return true;
                case "MIN": case "MAX": case "GAMMA": return axis;
            }
            return section.Equals("SHIFTER", StringComparison.OrdinalIgnoreCase)
                    && (key.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)
                            || Regex.IsMatch(key, @"^GEAR_(\d+|R)$", RegexOptions.IgnoreCase));
        }

        private static bool HasWheelBinding(IniFileSection section) => section.Keys.Any(key => IsDeviceKey(key)
                || key.Equals("AXLE", StringComparison.OrdinalIgnoreCase) || key.Equals("BUTTON", StringComparison.OrdinalIgnoreCase)
                || key.Equals("__CM_POV", StringComparison.OrdinalIgnoreCase) || key.Equals("__CM_ALT_BUTTON", StringComparison.OrdinalIgnoreCase));

        private static int BindingNumber(string value) {
            int number;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : -1;
        }

        internal static bool CanEditLiveBindings(string cfgPath) {
            lock (SessionGate) return _bindingSession != null && _bindingSession._ownsLease
                    && _bindingSession._lease != null && _bindingSession._restore != null
                    && Path.GetFullPath(cfgPath).TrimEnd(Path.DirectorySeparatorChar).Equals(
                            _bindingSession._cfgPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Called only by the authenticated preparation/pause editor while this process owns the race lease.</summary>
        internal static void EditLiveBindings(string cfgPath, Func<bool> authorized, Func<string> getSourcePath,
                string destination, Action<IniFile> edit) {
            lock (SessionGate) {
                Action check = () => {
                    if (authorized?.Invoke() != true || !CanEditLiveBindings(cfgPath))
                        throw new InvalidOperationException("準備画面またはポーズ中に、コントローラー割り当てを開き直してください。");
                };
                check();
                var session = _bindingSession;
                var sourcePath = getSourcePath();
                var wheelEdit = !Path.GetFullPath(destination).Equals(Path.GetFullPath(ControlsPath), StringComparison.OrdinalIgnoreCase);
                var expectedWheel = Path.Combine(session._cfgPath, "controllers", "savedsetups", "CM7-R5.ini");
                if (wheelEdit && !Path.GetFullPath(destination).Equals(expectedWheel, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("保存先がCM7の入力設定ではありません。");
                var sourceBytes = File.ReadAllBytes(sourcePath);
                var sameSourceDestination = Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);
                var destinationBytes = sameSourceDestination ? sourceBytes : File.Exists(destination) ? File.ReadAllBytes(destination) : null;
                if (wheelEdit && !sameSourceDestination && destinationBytes != null)
                    throw new IOException("保存設定が別の操作で作成されました。割当画面を開き直してください。");
                var before = ParseBytes(sourceBytes);
                var edited = ParseBytes(sourceBytes);
                edit(edited);
                var controlBytes = wheelEdit ? File.ReadAllBytes(ControlsPath) : sourceBytes;
                var controls = ParseBytes(controlBytes);
                var live = ParseBytes(controlBytes);
                var nextRestore = new RestoreState {
                    Version = session._restore.Version, Owner = session._restore.Owner, OwnerStartedUtc = session._restore.OwnerStartedUtc,
                    Changes = session._restore.Changes.Select(x => new Change { Section = x.Section, Key = x.Key, Before = x.Before, After = x.After }).ToList()
                };
                var differences = IniDifferences(before, edited);
                if (wheelEdit) {
                    var presetJoy = BindingNumber(Value(before, "STEER", "JOY"));
                    var liveJoy = BindingNumber(Value(controls, "STEER", "JOY"));
                    if (presetJoy < 0 || liveJoy < 0 || !SameSteeringDevice(before, presetJoy, controls, liveJoy))
                        throw new InvalidOperationException("この走行セッションのハンドル機器を確認できません。ACを終了して割当を変更してください。");
                    foreach (var section in differences.Select(x => x.Section).Distinct()) {
                        if (!SimpleLiveWheelBinding(before, section, presetJoy) || !SimpleLiveWheelBinding(controls, section, liveJoy))
                            throw new InvalidOperationException("アナログ軸・別の機器・複合ボタンの割当は走行中に変更できません。");
                    }
                    foreach (var difference in differences) {
                        if (!IsEditableButtonKey(difference.Section, difference.Key)
                                || BindingNumber(Value(before, difference.Section, "AXLE")) >= 0)
                            throw new InvalidOperationException("走行中に変更できるのはボタン割当のみです。ハンドル・ペダル設定は保持されます。");
                        var value = difference.After;
                        if (difference.Key == "JOY" && BindingNumber(value) >= 0) {
                            if (BindingNumber(value) != presetJoy) throw new InvalidOperationException("別の機器の割当は走行中に変更できません。");
                            value = liveJoy.ToString(CultureInfo.InvariantCulture);
                        }
                        SetValue(live, difference.Section, difference.Key, value);
                    }
                    // Unchanged preset JOY can still differ numerically from the connected session's index.
                    foreach (var section in differences.Select(x => x.Section).Distinct()) {
                        if (BindingNumber(Value(edited, section, "JOY")) == presetJoy)
                            live[section].Set("JOY", liveJoy);
                    }
                    foreach (var difference in IniDifferences(controls, live)) {
                        var owned = nextRestore.Changes.FirstOrDefault(x => x.Section == difference.Section && x.Key == difference.Key);
                        if (owned == null) nextRestore.Changes.Add(difference);
                        else owned.After = difference.After; // Keep the original pre-race value for restoration.
                    }
                } else {
                    foreach (var difference in differences) {
                        if (difference.Key != "XBOXBUTTON") throw new InvalidOperationException("パッドのボタン割当以外は変更できません。");
                        SetValue(live, difference.Section, difference.Key, difference.After);
                        var owned = nextRestore.Changes.FirstOrDefault(x => x.Section == difference.Section && x.Key == difference.Key);
                        if (owned != null) owned.Before = owned.After = difference.After; // Deliberate pad edits persist after restoration.
                    }
                }
                if (differences.Count == 0) return;
                var journalBytes = File.Exists(JournalPath) ? File.ReadAllBytes(JournalPath) : null;
                if (journalBytes == null && session._restore.Changes.Count > 0
                        || journalBytes != null && !SameRestore(JsonConvert.DeserializeObject<RestoreState>(Encoding.UTF8.GetString(journalBytes)), session._restore))
                    throw new IOException("入力設定の復元記録が変更されました。保存を中止しました。");
                var savedJournal = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(nextRestore, Formatting.Indented));
                var backupDirectory = Path.Combine(session._cfgPath, "cm7-binding-backups");
                Directory.CreateDirectory(backupDirectory);
                var transaction = new List<LiveWrite>();
                try {
                    check();
                    VerifyBytes(sourcePath, sourceBytes);
                    VerifyBytes(ControlsPath, controlBytes);
                    // Recovery instructions reach disk before any live control field changes.
                    CheckedWrite(JournalPath, journalBytes, savedJournal, backupDirectory, transaction, check);
                    if (wheelEdit) {
                        CheckedWrite(destination, destinationBytes, Encode(edited), backupDirectory, transaction, () => { check(); VerifyBytes(sourcePath, sourceBytes); });
                    }
                    CheckedWrite(ControlsPath, controlBytes, Encode(live), backupDirectory, transaction, check);
                    session._restore = nextRestore;
                    Logging.Write("[GtDrivingInput] live binding edit committed: " + (wheelEdit ? "wheel" : "pad"));
                } catch {
                    // Roll controls back before their journal; a failed rollback keeps the expanded recovery record.
                    var rollbackComplete = true;
                    for (var i = transaction.Count - 1; i >= 0; i--) {
                        var item = transaction[i];
                        if (item.Path == JournalPath && !rollbackComplete) { session._restore = nextRestore; break; }
                        try { item.Rollback(backupDirectory); }
                        catch (Exception e) { rollbackComplete = false; Logging.Error("[GtDrivingInput] live binding rollback retained backups: " + e); }
                    }
                    if (!rollbackComplete) session._restore = nextRestore;
                    throw;
                }
            }
        }

        private static bool SameSteeringDevice(IniFile preset, int a, IniFile live, int b) {
            Guid first, second;
            if (Guid.TryParse(Value(preset, "CONTROLLERS", "__IGUID" + a), out first) && first != Guid.Empty
                    && Guid.TryParse(Value(live, "CONTROLLERS", "__IGUID" + b), out second) && first == second) return true;
            if (!Guid.TryParse(Value(preset, "CONTROLLERS", "PGUID" + a), out first) || first == Guid.Empty
                    || !Guid.TryParse(Value(live, "CONTROLLERS", "PGUID" + b), out second) || first != second) return false;
            return live["CONTROLLERS"].Count(x => Regex.IsMatch(x.Key, @"^PGUID\d+$", RegexOptions.IgnoreCase)
                    && Guid.TryParse(x.Value, out second) && second == first) == 1;
        }

        private static bool IsEditableButtonKey(string section, string key) => !new[] { "STEER", "THROTTLE", "BRAKES", "CLUTCH", "SHIFTER" }.Contains(section)
                && new[] { "JOY", "BUTTON", "JOY_MODIFICATOR", "BUTTON_MODIFICATOR", "POV", "__CM_POV", "__CM_POV_DIR", "__CM_ALT_JOY",
                    "__CM_ALT_BUTTON", "__CM_ALT_POV", "__CM_ALT_POV_DIR" }.Contains(key, StringComparer.OrdinalIgnoreCase);

        private static bool SimpleLiveWheelBinding(IniFile ini, string section, int joy) {
            var binding = Section(ini, section);
            if (binding == null) return true;
            if (binding.GetInt("JOY", -1) >= 0 && binding.GetInt("JOY", -1) != joy) return false;
            return new[] { "AXLE", "JOY_MODIFICATOR", "BUTTON_MODIFICATOR", "POV", "__CM_POV", "__CM_ALT_BUTTON", "__CM_ALT_POV" }
                    .All(key => binding.GetInt(key, -1) < 0);
        }

        private static bool SameRestore(RestoreState a, RestoreState b) => a != null && b != null && a.Version == b.Version
                && a.Owner == b.Owner && a.OwnerStartedUtc == b.OwnerStartedUtc && a.Changes != null && b.Changes != null
                && JsonConvert.SerializeObject(a.Changes) == JsonConvert.SerializeObject(b.Changes);

        private static List<Change> IniDifferences(IniFile before, IniFile after) {
            var result = new List<Change>();
            foreach (var section in before.Keys.Concat(after.Keys).Distinct())
                foreach (var key in (Section(before, section)?.Keys ?? Enumerable.Empty<string>()).Concat(Section(after, section)?.Keys ?? Enumerable.Empty<string>()).Distinct()) {
                    var oldValue = Value(before, section, key);
                    var newValue = Value(after, section, key);
                    if (oldValue != newValue) result.Add(new Change { Section = section, Key = key, Before = oldValue, After = newValue });
                }
            return result;
        }

        private static void SetValue(IniFile ini, string section, string key, string value) {
            if (value == null) ini[section].Remove(key); else ini[section].Set(key, value);
        }
        private static IniFile ParseBytes(byte[] bytes) {
            using (var memory = new MemoryStream(bytes, false))
            using (var reader = new StreamReader(memory, Encoding.UTF8, true)) return IniFile.Parse(reader.ReadToEnd(), IniFileMode.Comments);
        }
        private static byte[] Encode(IniFile ini) => new UTF8Encoding(false).GetBytes(ini.Stringify());
        private static void VerifyBytes(string path, byte[] expected) {
            if (expected == null ? File.Exists(path) : !File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(expected))
                throw new IOException("入力設定が別の操作で変更されました。保存を中止しました: " + Path.GetFileName(path));
        }
        private sealed class LiveWrite {
            public string Path;
            public byte[] Before, After;
            public void Rollback(string backupDirectory) {
                VerifyBytes(Path, After);
                if (Before == null) File.Delete(Path);
                else CheckedWrite(Path, After, Before, backupDirectory, new List<LiveWrite>(), () => { });
            }
        }
        private static void CheckedWrite(string path, byte[] before, byte[] after, string backupDirectory,
                List<LiveWrite> transaction, Action check) {
            var temporary = path + ".cm7-live-" + Guid.NewGuid().ToString("N") + ".tmp";
            var backup = Path.Combine(backupDirectory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N") + "-live-" + Path.GetFileName(path) + ".bak");
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                    stream.Write(after, 0, after.Length); stream.Flush(true);
                }
                check();
                VerifyBytes(path, before);
                var record = new LiveWrite { Path = path, Before = before, After = after };
                if (before == null) File.Move(temporary, path);
                else File.Replace(temporary, path, backup);
                transaction.Add(record);
                if (before != null) {
                    var replaced = File.ReadAllBytes(backup);
                    record.Before = replaced;
                    if (!replaced.SequenceEqual(before)) throw new IOException("保存直前に別の操作が設定を変更しました。退避を復元します。");
                }
                VerifyBytes(path, after);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static bool IsSupportedGtSession(IniFile flag, DateTime utcNow) {
            var section = Section(flag, "GT");
            if (section == null || !section.GetBool("ENABLED", false)) return false;
            var mode = section.GetPossiblyEmpty("MODE");
            if (!new[] { "timetrial", "race", "dev" }.Contains(mode, StringComparer.OrdinalIgnoreCase)) return false;
            long stamp;
            if (!long.TryParse(section.GetPossiblyEmpty("STAMP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out stamp)) return false;
            var now = (long)(utcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return stamp <= now && stamp >= now - 600;
        }

        private static bool IsGtLaunchPending() {
            try {
                var path = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt_start.ini");
                return File.Exists(path) && IsSupportedGtSession(new IniFile(path), DateTime.UtcNow);
            } catch (Exception e) { Logging.Warning("[GtDrivingInput] GT session flag: " + e.Message); return false; }
        }

        /// <summary>Pure planning: no device or filesystem access. Unknown identities are disabled, never guessed by old numeric index.</summary>
        internal static Candidate BuildCandidate(IniFile current, IniFile wheel, IReadOnlyList<Device> devices, int xinputIndex) {
            if (current == null || devices == null) throw new ArgumentNullException();
            if (devices.Any(x => x == null || x.InstanceGuid == Guid.Empty)
                    || devices.Select(x => x.InstanceGuid).Distinct().Count() != devices.Count)
                throw new InvalidDataException("DirectInputの機器識別情報が重複または欠落しています。");
            var result = new Candidate();
            var planned = new Dictionary<Tuple<string, string>, string>();
            Action<string, string, string> put = (section, key, value) => planned[Tuple.Create(section, key)] = value;
            Func<IniFile, string, int> remap = (ini, raw) => {
                int oldIndex;
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out oldIndex) || oldIndex < 0) return -1;
                Guid instance, product;
                if (Guid.TryParse(Value(ini, "CONTROLLERS", "__IGUID" + oldIndex), out instance) && instance != Guid.Empty) {
                    for (var i = 0; i < devices.Count; i++) if (devices[i].InstanceGuid == instance) return i;
                }
                if (Guid.TryParse(Value(ini, "CONTROLLERS", "PGUID" + oldIndex), out product) && product != Guid.Empty) {
                    var matches = Enumerable.Range(0, devices.Count).Where(i => devices[i].ProductGuid == product).ToArray();
                    if (matches.Length == 1) return matches[0];
                }
                result.UnresolvedBindings++;
                return -1;
            };
            foreach (var section in current) {
                if (section.Key.Equals("KEYBOARD", StringComparison.OrdinalIgnoreCase)
                        || section.Key.Equals("X360", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var key in section.Value.Keys) {
                    if (IsDeviceKey(key))
                        put(section.Key, key, remap(current, section.Value.GetPossiblyEmpty(key)).ToString(CultureInfo.InvariantCulture));
                }
            }
            result.WheelIndex = remap(wheel, Value(wheel, "STEER", "JOY"));
            result.Wheel = result.WheelIndex >= 0;
            put("HEADER", "INPUT_METHOD", result.Wheel ? "WHEEL" : "X360");
            put("ADVANCED", "COMBINE_WITH_KEYBOARD_CONTROL", "0");
            put("X360", "JOYPAD_INDEX", Math.Max(0, Math.Min(3, xinputIndex)).ToString(CultureInfo.InvariantCulture));
            if (result.Wheel) {
                foreach (var source in wheel) {
                    if (source.Key.Equals("KEYBOARD", StringComparison.OrdinalIgnoreCase)
                            || source.Key.Equals("X360", StringComparison.OrdinalIgnoreCase) || !HasWheelBinding(source.Value)) continue;
                    var section = source.Key;
                    var axis = !section.Equals("STEER", StringComparison.OrdinalIgnoreCase)
                            && (source.Value.ContainsKey("AXLE") || Section(current, section)?.ContainsKey("AXLE") == true);
                    var keys = source.Value.Keys.Concat(Section(current, section)?.Keys ?? Enumerable.Empty<string>())
                            .Where(key => IsWheelBindingKey(section, key, axis)).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var key in keys) {
                        var value = Value(wheel, section, key);
                        if (IsDeviceKey(key) && value != null) value = remap(wheel, value).ToString(CultureInfo.InvariantCulture);
                        // Missing fields must be removed too: otherwise an old POV or modifier overrides the saved button.
                        put(section, key, value);
                    }
                }
                foreach (var section in new[] { "STEER", "THROTTLE", "BRAKES", "CLUTCH" }) {
                    var rawJoy = Value(wheel, section, "JOY");
                    if (section != "STEER" && BindingNumber(rawJoy) < 0) continue; // Unassigned/absent optional pedal, including a two-pedal set.
                    Guid identity;
                    var index = BindingNumber(rawJoy);
                    var hasIdentity = Guid.TryParse(Value(wheel, "CONTROLLERS", "__IGUID" + index), out identity) && identity != Guid.Empty
                            || Guid.TryParse(Value(wheel, "CONTROLLERS", "PGUID" + index), out identity) && identity != Guid.Empty;
                    if (!hasIdentity || BindingNumber(Value(wheel, section, "AXLE")) < 0)
                        throw new InvalidDataException("ハンコン保存設定のハンドル・ペダル割当を確認できませんでした。");
                }
            }
            // A missing modifier must not turn a two-button shortcut into an unmodified action.
            foreach (var section in current.Select(x => x.Key).Concat(result.Wheel ? wheel.Select(x => x.Key) : Enumerable.Empty<string>())
                    .Distinct()) {
                if (section.Equals("KEYBOARD", StringComparison.OrdinalIgnoreCase)
                        || section.Equals("X360", StringComparison.OrdinalIgnoreCase)) continue;
                Func<string, string> value = key => {
                    string next;
                    return planned.TryGetValue(Tuple.Create(section, key), out next) ? next : Value(current, section, key);
                };
                if (BindingNumber(value("BUTTON_MODIFICATOR")) >= 0 && BindingNumber(value("JOY_MODIFICATOR")) < 0) {
                    put(section, "JOY", "-1");
                    if (BindingNumber(value("__CM_ALT_JOY")) >= 0) put(section, "__CM_ALT_JOY", "-1");
                }
            }
            // Replace only indexed controller identity keys; unrelated controller metadata stays intact.
            var controllers = Section(current, "CONTROLLERS");
            if (controllers != null) foreach (var key in controllers.Keys) {
                if (Regex.IsMatch(key, @"^(CON|__IGUID|PGUID)\d+$", RegexOptions.IgnoreCase)) put("CONTROLLERS", key, null);
            }
            for (var i = 0; i < devices.Count; i++) {
                put("CONTROLLERS", "CON" + i, devices[i].Name ?? "");
                put("CONTROLLERS", "__IGUID" + i, devices[i].InstanceGuid.ToString("D").ToUpperInvariant());
                put("CONTROLLERS", "PGUID" + i, devices[i].ProductGuid.ToString("D").ToUpperInvariant());
            }
            foreach (var pair in planned) {
                var before = Value(current, pair.Key.Item1, pair.Key.Item2);
                if (before != pair.Value) result.Changes.Add(new Change { Section = pair.Key.Item1, Key = pair.Key.Item2, Before = before, After = pair.Value });
            }
            return result;
        }

        private static FileStream AcquireLease() {
            Directory.CreateDirectory(AcPaths.GetDocumentsCfgDirectory());
            return new FileStream(LeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        }
        private static bool AcRunning() {
            foreach (var name in new[] { "acs", "acs_x86" }) {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Length > 0) return true; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return false;
        }
        private static void SaveJournal(RestoreState state) {
            var temporary = JournalPath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(state, Formatting.Indented));
            if (File.Exists(JournalPath)) File.Replace(temporary, JournalPath, null);
            else File.Move(temporary, JournalPath);
        }
        private static void ApplyChanges(IReadOnlyList<Change> changes, bool restore) {
            var ini = new IniFile(ControlsPath);
            foreach (var change in changes) {
                var value = restore ? change.Before : change.After;
                if (value == null) ini[change.Section].Remove(change.Key);
                else ini[change.Section].Set(change.Key, value);
            }
            ini.Save();
            var check = new IniFile(ControlsPath);
            foreach (var change in changes) {
                if (Value(check, change.Section, change.Key) != (restore ? change.Before : change.After))
                    throw new IOException("走行入力設定の保存を確認できませんでした: " + change.Section + "/" + change.Key);
            }
        }
        private static void Restore(RestoreState state) {
            if (state.Version != 1 || state.Changes == null) throw new InvalidDataException("走行入力の復元記録が不正です。");
            if (state.Changes.Count > 0) {
                ApplyChanges(state.Changes, true);
                state.Changes.Clear();
                SaveJournal(state); // A failed journal deletion must not replay completed restores later.
            }
            File.Delete(JournalPath);
            Logging.Write("[GtDrivingInput] restored controls keys");
        }
        private static void RecoverLocked() {
            if (!File.Exists(JournalPath)) return;
            if (AcRunning()) throw new InvalidOperationException("AC実行中のため走行入力設定を復元できません。");
            var state = JsonConvert.DeserializeObject<RestoreState>(File.ReadAllText(JournalPath));
            if (state == null) throw new InvalidDataException("走行入力の復元記録がありません。");
            if (state.Owner != Process.GetCurrentProcess().Id) {
                try {
                    using (var process = Process.GetProcessById(state.Owner)) {
                        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == state.OwnerStartedUtc)
                            throw new InvalidOperationException("別のCMが走行入力設定を使用しています。");
                    }
                } catch (ArgumentException) { }
            }
            Restore(state);
        }

        private sealed class Session : Game.AdditionalProperties, IDisposable {
            internal readonly GtGameUi Launch;
            internal FileStream _lease;
            internal RestoreState _restore;
            internal bool _ownsLease;
            internal string _cfgPath;
            public Session(GtGameUi launch) { Launch = launch; }
            public override IDisposable Set() {
                lock (SessionGate) return SetLocked();
            }
            private IDisposable SetLocked() {
                if (!Enabled || !ReferenceEquals(GtGameUi.Pending, Launch) || !IsGtLaunchPending()) return null;
                if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("走行入力設定の切替が進行中です。");
                _ownsLease = true;
                try {
                    _cfgPath = Path.GetFullPath(AcPaths.GetDocumentsCfgDirectory()).TrimEnd(Path.DirectorySeparatorChar);
                    _lease = AcquireLease();
                    RecoverLocked();
                    if (AcRunning()) throw new InvalidOperationException("AC実行中は走行入力設定を変更できません。");
                    if (!File.Exists(ControlsPath)) throw new FileNotFoundException("走行入力設定がありません。", ControlsPath);
                    var current = new IniFile(ControlsPath);
                    var wheel = File.Exists(WheelPath) ? new IniFile(WheelPath) : null;
                    var candidate = BuildCandidate(current, wheel, GetDevices(), FindXInputIndex());
                    // [fork] 2026-09-19: CM7 のキーボード設定のうち、AC 本体が controls.ini から直接読むキー（左右/後ろを見る・視点切替・
                    //   ライト・ホーン）。走行の間だけ KEY= を書き換え、終了時は他の変更と同じ仕組みで元へ戻す（controls.ini を恒久的には変えない）
                    try {
                        foreach (var pair in GtKeyboardSettings.NativeOverrides()) {
                            var before = Value(current, pair.Key, "KEY");
                            if (before == pair.Value || candidate.Changes.Any(x => x.Section == pair.Key && x.Key == "KEY")) continue;
                            candidate.Changes.Add(new Change { Section = pair.Key, Key = "KEY", Before = before, After = pair.Value });
                        }
                    } catch (Exception e) { Logging.Warning("[GtDrivingInput] keyboard view keys: " + e.Message); }
                    // [fork] 2026-09-19 要望「MFD をテンキーの 4・8・6・2 で動かしたい」。キーを読むのは GT7 HUD（Lua）側。
                    //   同じキーを修飾キーなしで使う CSP 拡張の操作（実例: 追加ライトB=テンキー2・ミラー調整↑=テンキー8）が
                    //   一緒に動いてしまうので、走行の間だけ未割り当てにする。終了時は他の変更と同じ仕組みで元へ戻る
                    try {
                        var mfdKeys = MfdKeyboardKeys();
                        foreach (var section in current.Where(x => x.Key.StartsWith("__EXT_", StringComparison.OrdinalIgnoreCase)).ToList()) {
                            var modifier = (section.Value.GetPossiblyEmpty("KEY_MODIFICATOR") ?? "").Trim();
                            if (modifier.Length > 0 && modifier != "-1" && modifier != "0") continue;
                            var pending = candidate.Changes.FirstOrDefault(x => x.Section == section.Key && x.Key == "KEY");
                            var before = section.Value.GetPossiblyEmpty("KEY");
                            int key;
                            if (!TryVirtualKey(pending != null ? pending.After : before, out key) || !mfdKeys.Contains(key)) continue;
                            if (pending != null) pending.After = "-1";
                            else candidate.Changes.Add(new Change { Section = section.Key, Key = "KEY", Before = before, After = "-1" });
                            Logging.Write("[GtDrivingInput] MFD key 0x" + key.ToString("X2", CultureInfo.InvariantCulture) + ": " + section.Key
                                    + " is unbound while driving");
                        }
                    } catch (Exception e) { Logging.Warning("[GtDrivingInput] MFD key conflicts: " + e.Message); }
                    // [fork] 2026-09-20 要望「ES ホイールの十字キーで HUD の MFD を操作できるように」。MFD を読むのは GT7 HUD（Lua）で、
                    //   ボタン番号は apps\lua\GT7HUD\settings\mfd_controls.ini の [WHEEL]（既定 = 十字 ↑4 →5 ↓6 ←7）。
                    //   同じボタンに走行側の操作（実例: 左右を見る=7/5・後ろを見る=6）が入っていると一緒に動いてしまうので、
                    //   ハンコンで走る間だけ未割り当てにする。終了時は他の変更と同じ仕組みで元へ戻る
                    try {
                        if (candidate.Wheel && candidate.WheelIndex >= 0) {
                            var mfdButtons = MfdWheelButtons();
                            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "STEER", "THROTTLE", "BRAKES", "CLUTCH", "HANDBRAKE", "GEARUP", "GEARDN" };
                            Func<string, string, string> effective = (section, key) => {
                                var pending = candidate.Changes.FirstOrDefault(x => x.Section == section && x.Key == key);
                                return pending != null ? pending.After : Value(current, section, key);
                            };
                            var names = current.Select(x => x.Key).Concat(candidate.Changes.Select(x => x.Section)).Distinct().ToList();
                            foreach (var name in names) {
                                if (keep.Contains(name) || name.Equals("KEYBOARD", StringComparison.OrdinalIgnoreCase)
                                        || name.Equals("X360", StringComparison.OrdinalIgnoreCase)) continue;
                                if (BindingNumber(effective(name, "JOY")) != candidate.WheelIndex) continue;
                                var button = BindingNumber(effective(name, "BUTTON"));
                                if (button < 0 || !mfdButtons.Contains(button)) continue;
                                var pendingButton = candidate.Changes.FirstOrDefault(x => x.Section == name && x.Key == "BUTTON");
                                if (pendingButton != null) pendingButton.After = "-1";
                                else candidate.Changes.Add(new Change { Section = name, Key = "BUTTON", Before = Value(current, name, "BUTTON"), After = "-1" });
                                Logging.Write("[GtDrivingInput] MFD wheel button " + button + ": " + name + " is unbound while driving");
                            }
                        }
                    } catch (Exception e) { Logging.Warning("[GtDrivingInput] MFD wheel conflicts: " + e.Message); }
                    using (var process = Process.GetCurrentProcess()) {
                        _restore = new RestoreState { Owner = process.Id, OwnerStartedUtc = process.StartTime.ToUniversalTime().Ticks, Changes = candidate.Changes };
                    }
                    if (_restore.Changes.Count > 0) {
                        SaveJournal(_restore);
                        ApplyChanges(_restore.Changes, false);
                    }
                    Logging.Write("[GtDrivingInput] applied mode=" + (candidate.Wheel ? "WHEEL" : "X360")
                            + " wheel=" + candidate.WheelIndex + " changed=" + candidate.Changes.Count + " unresolved=" + candidate.UnresolvedBindings);
                    _bindingSession = this;
                    return null; // This instance is already first in the IDisposable insertion order.
                } catch { Dispose(); throw; }
            }
            public void Dispose() {
                lock (SessionGate) DisposeLocked();
            }
            private void DisposeLocked() {
                if (!_ownsLease) return;
                try { if (_restore != null) Restore(_restore); }
                catch (Exception e) { Logging.Error("[GtDrivingInput] restore failed; journal retained: " + e); }
                finally {
                    if (ReferenceEquals(_bindingSession, this)) _bindingSession = null;
                    _restore = null;
                    _lease?.Dispose();
                    _lease = null;
                    _ownsLease = false;
                    Interlocked.Exchange(ref _active, 0);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct XInputState {
            public uint Packet;
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LeftX, LeftY, RightX, RightY;
        }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint XInput14(uint index, out XInputState state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] private static extern uint XInput910(uint index, out XInputState state);
        private static int FindXInputIndex() {
            for (uint i = 0; i < 4; i++) {
                XInputState state;
                try { if (XInput14(i, out state) == 0) return (int)i; }
                catch (DllNotFoundException) { try { if (XInput910(i, out state) == 0) return (int)i; } catch (DllNotFoundException) { return 0; } }
                catch (EntryPointNotFoundException) { try { if (XInput910(i, out state) == 0) return (int)i; } catch (EntryPointNotFoundException) { return 0; } }
            }
            return 0; // The bridge normally owns index zero, including keyboard-only operation.
        }
    }
}
