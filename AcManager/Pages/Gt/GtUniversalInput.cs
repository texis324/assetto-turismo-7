using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using FirstFloor.ModernUI.Helpers;
using SlimDX.DirectInput;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;

namespace AcManager.Pages.Gt {
    /// <summary>Native wheel menu input. Never creates a virtual wheel or sends force-feedback effects.</summary>
    internal static class GtUniversalInput {
        private static bool _initialized;
        private static DirectInput _directInput;
        private static Joystick _wheel;
        private static bool _requireNeutral = true;
        private static int _nextScan;
        private static bool _scanScheduled;
        private static DateTime _settingsChecked;
        private static string _product = "0004346e-0000-0000-0000-504944564944";
        private static string _instance = "";
        private static bool _enabled = true;
        private static readonly bool[] WheelState = new bool[15];
        private static readonly Dictionary<GtPadCommand, int> Defaults = GtUniversalInputRules.CreateDefaultWheelButtons();
        private static readonly Dictionary<GtPadCommand, int> Buttons = new Dictionary<GtPadCommand, int>(Defaults);
        internal static string DeviceInfo { get; private set; }

        internal static void Init() {
            if (_initialized) return;
            _initialized = true;
            // Arrows must reach GT selection before WPF moves focus among header controls.
            // Other page shortcuts run first until a GT selection is visible.
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
                    new KeyEventHandler(OnKeyDown), false);
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.KeyDownEvent,
                    new KeyEventHandler(OnKeyDown), false);
            if (Application.Current != null) Application.Current.Exit += (s, e) => SuspendWheel();
        }

        private static void OnKeyDown(object sender, KeyEventArgs e) {
            var main = Application.Current?.MainWindow;
            var focused = Keyboard.FocusedElement;
            if (!GtUniversalInputRules.KeyboardAllowed(e.Handled, GtPad.SendingKey, Keyboard.Modifiers != ModifierKeys.None,
                    main != null && ReferenceEquals(sender, main), main != null && main.IsActive, main != null && main.IsEnabled,
                    focused is TextBoxBase || focused is PasswordBox || focused is ComboBox || focused is Slider)) return;
            var direction = e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right;
            var confirm = e.Key == Key.Enter || e.Key == Key.Space;
            if (!GtUniversalInputRules.KeyboardRouteAllowed(e.RoutedEvent == Keyboard.PreviewKeyDownEvent,
                    GtFocus.KeyboardNavigationActive, direction, e.Key == Key.Escape, focused is ButtonBase, confirm)) return;
            GtPadCommand command;
            switch (e.Key) {
                case Key.Up: command = GtPadCommand.Up; break;
                case Key.Down: command = GtPadCommand.Down; break;
                case Key.Left: command = GtPadCommand.Left; break;
                case Key.Right: command = GtPadCommand.Right; break;
                case Key.Enter: case Key.Space: command = GtPadCommand.Confirm; break;
                case Key.Escape: command = GtPadCommand.Back; break;
                case Key.PageUp: command = GtPadCommand.L1; break;
                case Key.PageDown: command = GtPadCommand.R1; break;
                default: return;
            }
            if (GtPad.HandleKeyboard(command, e.IsRepeat)) e.Handled = true;
        }

        private static void RefreshSettings() {
            var now = DateTime.UtcNow;
            if ((now - _settingsChecked).TotalSeconds < 2) return;
            _settingsChecked = now;
            try {
                var enabled = true;
                var product = "0004346e-0000-0000-0000-504944564944";
                var instance = "";
                var next = new Dictionary<GtPadCommand, int>(Defaults);
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Static", "gt7", "pad", "pad.txt");
                if (File.Exists(path)) foreach (var raw in File.ReadAllLines(path)) {
                    var line = raw.Split('#', ';')[0].Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var value = line.Substring(eq + 1).Trim();
                    if (key == "wheel_enabled") enabled = value != "0" && value != "off" && value != "false";
                    else if (key == "wheel_product") product = value;
                    else if (key == "wheel_instance") instance = value;
                    else if (key.StartsWith("wheel_")) {
                        GtPadCommand command;
                        int button;
                        if (Enum.TryParse(key.Substring(6), true, out command)
                                && command > GtPadCommand.None && command <= GtPadCommand.R3
                                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out button)
                                && button >= -1 && button < 128) next[command] = button;
                    }
                }
                var changed = product != _product || instance != _instance || enabled != _enabled;
                foreach (var pair in next) {
                    int old;
                    if (!Buttons.TryGetValue(pair.Key, out old) || old != pair.Value) changed = true;
                }
                if (Buttons.Count != next.Count) changed = true;
                Buttons.Clear();
                foreach (var pair in next) Buttons[pair.Key] = pair.Value;
                _product = product;
                _instance = instance;
                _enabled = enabled;
                if (changed) SuspendWheel();
            } catch (Exception ex) {
                Logging.Warning("[GtUniversalInput] settings: " + ex.Message);
            }
        }

        internal static void SuspendWheel() {
            if (_wheel != null) {
                try { _wheel.Unacquire(); } catch { }
                try { _wheel.Dispose(); } catch { }
                _wheel = null;
            }
            if (_directInput != null) {
                try { _directInput.Dispose(); } catch { }
                _directInput = null;
            }
            _requireNeutral = true;
            Array.Clear(WheelState, 0, WheelState.Length);
            DeviceInfo = null;
        }

        private static bool Connect(int now) {
            if (_scanScheduled && unchecked(now - _nextScan) < 0) return false;
            _scanScheduled = true;
            _nextScan = unchecked(now + 2000);
            var main = Application.Current?.MainWindow;
            if (main == null) return false;
            var handle = new WindowInteropHelper(main).Handle;
            if (handle == IntPtr.Zero) return false;
            _directInput = new DirectInput();
            foreach (var device in _directInput.GetDevices(DeviceClass.GameController, DeviceEnumerationFlags.AttachedOnly)) {
                if (!string.IsNullOrEmpty(_instance)
                        ? !device.InstanceGuid.ToString().Equals(_instance, StringComparison.OrdinalIgnoreCase)
                        : !device.ProductGuid.ToString().Equals(_product, StringComparison.OrdinalIgnoreCase)) continue;
                _wheel = new Joystick(_directInput, device.InstanceGuid);
                _wheel.SetCooperativeLevel(handle, CooperativeLevel.Background | CooperativeLevel.Nonexclusive);
                if (!_wheel.Acquire().IsSuccess) { SuspendWheel(); return false; }
                _requireNeutral = true;
                DeviceInfo = "wheel:" + device.InstanceName;
                Logging.Write("[GtUniversalInput] " + DeviceInfo + " connected (shared read only)");
                return true;
            }
            SuspendWheel();
            return false;
        }

        // [fork] 2026-10-04 要望「コントローラー割り当て画面でボタン番号が分かるように」「番号はモザの番号と一致させたい」。
        //   ホイールの生のボタン状態（DirectInput の 0 始まり）。表示側が +1 して MOZA 表記にする。
        internal static readonly bool[] RawButtons = new bool[128];
        internal static int RawStamp;   // Environment.TickCount。古ければ「読めていない」

        private static void CopyRaw(bool[] buttons) {
            var n = Math.Min(buttons.Length, RawButtons.Length);
            for (var i = 0; i < n; i++) RawButtons[i] = buttons[i];
            for (var i = n; i < RawButtons.Length; i++) RawButtons[i] = false;
            RawStamp = Environment.TickCount;
        }

        /// <summary>Merge into GtPad state before its edge/repeat/gate handling. Initial/reconnected held buttons are ignored until neutral.</summary>
        internal static bool MergeWheel(bool[] commands, int now) {
            RefreshSettings();
            if (!_enabled) return false;
            try {
                if (_wheel == null && !Connect(now)) return false;
                if (!_wheel.Poll().IsSuccess) { SuspendWheel(); return false; }
                var state = _wheel.GetCurrentState();
                var buttons = state.GetButtons();
                CopyRaw(buttons);   // [fork] コントローラー割り当て画面の「いま押しているボタン」表示用（読むだけ）
                var povs = state.GetPointOfViewControllers();
                Array.Clear(WheelState, 0, WheelState.Length);
                GtUniversalInputRules.WritePov(povs.Length > 0 ? povs[0] : -1, WheelState);
                GtUniversalInputRules.WriteButtons(Buttons, buttons, WheelState);
                GtUniversalInputRules.MergeAfterNeutral(ref _requireNeutral, WheelState, commands);
                return true;
            } catch (Exception ex) {
                Logging.Warning("[GtUniversalInput] wheel disconnected/unavailable: " + ex.Message);
                SuspendWheel();
                _nextScan = unchecked(now + 2000);
                _scanScheduled = true;
                return false;
            }
        }
    }

    // Pure production rules are also used by the isolated regression harness (no devices/UI required).
    internal static class GtUniversalInputRules {
        // MOZA's ES guide numbers are one-based; DirectInput is zero-based.
        // The ES D-pad reports buttons as well as (firmware-dependent) POV input.
        // Keep category switching on WIP/BOX so Left/Right cannot also switch tabs.
        internal static System.Collections.Generic.Dictionary<GtPadCommand, int> CreateDefaultWheelButtons()
                => new System.Collections.Generic.Dictionary<GtPadCommand, int> {
            { GtPadCommand.Up, 4 }, { GtPadCommand.Right, 5 }, { GtPadCommand.Down, 6 }, { GtPadCommand.Left, 7 },
            { GtPadCommand.Confirm, 13 }, { GtPadCommand.Back, 12 },
            { GtPadCommand.Select, 37 }, { GtPadCommand.Start, 34 },
            { GtPadCommand.L1, 19 }, { GtPadCommand.R1, 32 },
            { GtPadCommand.Triangle, 0 }, { GtPadCommand.Square, 2 },
            { GtPadCommand.L3, 21 }, { GtPadCommand.R3, 22 }
        };

        internal static void WriteButtons(System.Collections.Generic.IReadOnlyDictionary<GtPadCommand, int> bindings, bool[] buttons, bool[] state) {
            foreach (var pair in bindings) {
                if (pair.Key > GtPadCommand.None && (int)pair.Key < state.Length
                        && pair.Value >= 0 && pair.Value < buttons.Length && buttons[pair.Value]) state[(int)pair.Key] = true;
            }
        }

        internal static bool KeyboardRouteAllowed(bool preview, bool selectionActive, bool direction, bool back, bool nativeButton, bool confirm) {
            if (nativeButton && confirm) return false;
            return !preview || direction || (selectionActive && !back);
        }

        internal static bool KeyboardAllowed(bool handled, bool synthetic, bool modified, bool main, bool active, bool enabled, bool editing) {
            return !handled && !synthetic && !modified && main && active && enabled && !editing;
        }

        internal static void WritePov(int pov, bool[] state) {
            if (pov < 0 || pov >= 36000) return;
            var sector = ((pov + 2250) / 4500) % 8;
            state[(int)GtPadCommand.Up] = sector == 0 || sector == 1 || sector == 7;
            state[(int)GtPadCommand.Right] = sector >= 1 && sector <= 3;
            state[(int)GtPadCommand.Down] = sector >= 3 && sector <= 5;
            state[(int)GtPadCommand.Left] = sector >= 5 && sector <= 7;
        }

        internal static void MergeAfterNeutral(ref bool requireNeutral, bool[] source, bool[] destination) {
            if (requireNeutral) {
                for (var i = 1; i < source.Length; i++) if (source[i]) return;
                requireNeutral = false;
                return;
            }
            for (var i = 1; i < source.Length && i < destination.Length; i++) destination[i] |= source[i];
        }
    }
}
