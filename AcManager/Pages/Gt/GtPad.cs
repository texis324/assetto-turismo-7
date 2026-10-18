// [fork] DS4（DS4Windows 経由の XInput）で GT7 風ページを操作する — 入力源・ゲート・ディスパッチ。
//
//   経路: DS4 → DS4Windows(ViGEmBus 仮想 X360) → XInput → ここ → GtFocus / キー合成
//     AC 側の DS4 プリセットが INPUT_METHOD=X360＝XInput なので、CM 側も XInput を主経路にしてある。
//
//   ★XInput はフォアグラウンド無関係に読める。AC 走行中に × を押すと CM が反応してしまうので、
//     ゲート（§ Gate）が本丸。1 つでも落ちたら prev=cur に同期して差分を捨てる（＝復帰時の幽霊入力防止）。
//
//   設定 = Data\Static\gt7\pad\pad.txt（GtSfx の volume.txt と同じ「1 秒キャッシュ・無くても動く」運用）。
//   debug=1 で %TEMP%\gtpad.log に押下/送出/ゲート理由が出る。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AcManager.Pages.Windows;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows;

namespace AcManager.Pages.Gt {
    /// <summary>[fork] パッドの「意味」。ボタンの物理名ではなく機能で持つ（confirm=circle で ×/○ が入れ替わるため）。</summary>
    internal enum GtPadCommand {
        None, Up, Down, Left, Right, Confirm, Back, Start, Select, L1, R1, Triangle, Square, L3, R3
    }

    internal static class GtPad {
        private const int CommandCount = 15;              // GtPadCommand の要素数
        private const uint ErrorSuccess = 0;
        private const uint ErrorDeviceNotConnected = 1167;

        // ---- XInput P/Invoke -------------------------------------------------
        // ★DllImport は「ロード失敗が起きうる dll ごとに別クラス」に分ける。
        //   同じクラスに 2 つ書くと、型初期化のタイミング次第で巻き添えになる。
        [StructLayout(LayoutKind.Sequential)]
        private struct XinputGamepad {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XinputState {
            public uint dwPacketNumber;
            public XinputGamepad Gamepad;
        }

        private static class XInput14 {
            [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
            internal static extern uint GetState(uint dwUserIndex, out XinputState state);
        }

        private static class XInput910 {
            [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
            internal static extern uint GetState(uint dwUserIndex, out XinputState state);
        }

        // 0=未判定 1=xinput1_4 2=xinput9_1_0 3=使えない
        private static int _xinputMode;

        private static uint XInputGetState(uint index, out XinputState state) {
            state = new XinputState();
            if (_xinputMode == 3) return ErrorDeviceNotConnected;
            if (_xinputMode == 0) {
                XinputState probe;
                try {
                    XInput14.GetState(0, out probe);
                    _xinputMode = 1;
                    Log("xinput: xinput1_4.dll");
                } catch (Exception e14) {
                    try {
                        XInput910.GetState(0, out probe);
                        _xinputMode = 2;
                        Log("xinput: xinput9_1_0.dll (1_4: " + e14.Message + ")");
                    } catch (Exception e91) {
                        _xinputMode = 3;
                        Logging.Warning("[GtPad] XInput unavailable: " + e91.Message);
                        Log("xinput: NONE (" + e91.Message + ")");
                        return ErrorDeviceNotConnected;
                    }
                }
            }
            try {
                return _xinputMode == 1 ? XInput14.GetState(index, out state) : XInput910.GetState(index, out state);
            } catch (Exception e) {
                _xinputMode = 3;
                Log("xinput call failed: " + e.Message);
                state = new XinputState();
                return ErrorDeviceNotConnected;
            }
        }

        // XINPUT_GAMEPAD_* のビット
        private const ushort BtnDpadUp = 0x0001, BtnDpadDown = 0x0002, BtnDpadLeft = 0x0004, BtnDpadRight = 0x0008;
        private const ushort BtnStart = 0x0010, BtnBack = 0x0020, BtnLeftThumb = 0x0040, BtnRightThumb = 0x0080;
        private const ushort BtnLb = 0x0100, BtnRb = 0x0200;
        private const ushort BtnA = 0x1000, BtnB = 0x2000, BtnX = 0x4000, BtnY = 0x8000;

        // ---- 公開 API --------------------------------------------------------
        private static bool _init;

        /// <summary>直近のゲート判定。GtTestChannel の dump で読む。</summary>
        public static string Reason { get; private set; }

        /// <summary>接続情報（"xinput:0 pkt=1234"）。dump 用。</summary>
        public static string DeviceInfo { get; private set; }

        /// <summary>最後にパッドのコマンドが出た時刻（GtIdleSlides が見る）。</summary>
        public static DateTime LastInputAt { get; private set; }

        /// <summary>[fork] 今パッドで操作中か（＝画面の再構築後もフォーカス枠を出しておいてよいか）。
        /// カーソルを隠したまま＝本物のマウスが動いていない、かつ直近 3 秒以内にパッド入力がある。</summary>
        internal static bool PadDriving {
            get { return _cursorHidden && (DateTime.UtcNow - LastInputAt).TotalSeconds < 3.0; }
        }

        /// <summary>[fork] 画面のヒント表記をパッド記号（○/×/□/◀▶）にしているか＝PadDriving のラッチ版。
        /// ★PadDriving は「直近 3 秒以内」なので放っておくと勝手に false へ落ちるが、ヒントの表記は
        ///   手が止まっただけで戻ってはいけない。true になるのはパッド入力を Dispatch した時、
        ///   false に戻るのは本物のマウスが動いた時／GT ページを出た時／AC が走り出した時だけ。</summary>
        internal static bool PadMode {
            get { return _padMode; }
        }

        /// <summary>[fork] PadMode が変わった時だけ発火（UI スレッド）。GtHints が購読してヒントを書き換える。</summary>
        internal static event Action PadModeChanged;

        private static bool _padMode;

        private static void SetPadMode(bool on) {
            if (_padMode == on) return;
            _padMode = on;
            Log("pad mode -> " + (on ? "pad" : "kbd"));
            var h = PadModeChanged;
            if (h == null) return;
            try {
                h();
            } catch (Exception ex) {
                Logging.Warning("[GtPad] PadModeChanged: " + ex.Message);
            }
        }

        /// <summary>[fork] pad.txt の confirm=circle（○決定／×戻る）。GtHints の記号がこれに追従する。</summary>
        internal static bool ConfirmIsCircle {
            get {
                RefreshConfig();
                return _cfgConfirmCircle;
            }
        }

        public static bool Enabled {
            get {
                RefreshConfig();
                return _cfgEnabled;
            }
        }

        static GtPad() {
            Reason = "not started";
            DeviceInfo = "-";
            LastInputAt = DateTime.MinValue;
        }

        /// <summary>GtChrome.Enter() から呼ぶ（何度呼んでも初回だけ効く）。</summary>
        public static void Init() {
            if (_init) return;
            _init = true;
            try {
                GtInGameSettings.Init();
                RefreshConfig();
                // ★毎フレーム（CompositionTarget.Rendering）を張りっぱなしにすると、GT ページを出ても AC 走行中でも
                //   WPF が描画をアイドルさせられない。「本当に効かせる状態」の時だけ張り、それ以外は 50ms のタイマに落とす
                //   （非 GT ページの脱出口 = OPTIONS の一瞬の押下を取りこぼさない程度には速く回す）。
                //   タイマは張っている間も止めない: 最小化中は Rendering が来ないので、acs 消滅に気づけなくなる。
                _slowTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
                _slowTimer.Tick += OnFrame;
                _slowTimer.Start();
                Attach();
                // 本物のマウスが動いたらカーソルを戻す（パッド操作中は隠している）
                EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent,
                        new MouseEventHandler(OnPreviewMouseMove), true);
                Reason = "starting";
                Logging.Write("[GtPad] init");
            } catch (Exception ex) {
                Logging.Warning("[GtPad] init: " + ex.Message);
            }
        }

        // ---- 毎フレーム / 低頻度の張り替え ------------------------------------
        private static DispatcherTimer _slowTimer;
        private static bool _attached;

        /// <summary>毎フレーム購読に切り替える。
        /// ★ここで入力食い込み（_eatArmed）を立ててはいけない: 立てると次フレームがゲート落ち→Detach→開く→Attach…の
        ///   往復になって入力が通らなくなる。張り替えても幽霊入力は出ない（外している間も 100ms タイマが
        ///   Frame() を回して prev=cur を同期し続けるので、押しっぱなしの押下エッジは立たない）。</summary>
        private static void Attach() {
            if (_attached) return;
            _attached = true;
            try {
                CompositionTargetEx.Rendering += OnFrame;
                Log("attach (per-frame)");
            } catch (Exception ex) {
                _attached = false;
                Log("attach failed: " + ex.Message);
            }
        }

        /// <summary>毎フレーム購読をやめる（＝CM の描画がアイドルできるようになる）。判定は 100ms タイマが続ける。</summary>
        private static void Detach() {
            if (!_attached) return;
            _attached = false;
            try {
                CompositionTargetEx.Rendering -= OnFrame;
                Log("detach (timer only)");
            } catch { }
        }

        /// <summary>★テスト用: XInput とゲートを飛ばして Dispatch に流す（GtTestChannel の pad= から）。</summary>
        public static bool Inject(GtPadCommand cmd) {
            if (cmd == GtPadCommand.None) return false;
            try {
                Log("inject " + cmd);
                foreach (Window window in Application.Current.Windows) {
                    var picker = window as GarageCarPicker;
                    if (picker == null || !picker.IsVisible) continue;
                    // Diagnostics use the same owned dialog route as the physical input gate.
                    if (!picker.IsActive || !picker.IsEnabled) return false;
                    var handled = picker.HandleCommand(cmd, false);
                    if (handled) { LastInputAt = DateTime.UtcNow; GtIdleSlides.NotePadInput(); }
                    return handled;
                }
                Dispatch(cmd);
                return true;
            } catch (Exception ex) {
                Logging.Warning("[GtPad] inject: " + ex.Message);
                return false;
            }
        }

        // ---- pad.txt ---------------------------------------------------------
        private static DateTime _configCheckedAt = DateTime.MinValue;
        private static bool _cfgEnabled = true;
        private static bool _cfgConfirmCircle;          // true = ○決定 / ×戻る
        private static bool _cfgStick = true;
        private static double _cfgDeadzone = 0.55;
        private static double _cfgDeadzoneRelease = 0.35;
        private static int _cfgRepeatDelay = 400;
        private static int _cfgRepeatRate = 90;
        private static int _cfgIndex = -1;              // -1 = auto
        private static string _cfgDs4Windows = "auto";
        private static string _cfgBridge = "auto";        // auto|off|<ds4_bridge.py のフルパス>（2026-09-05 M3）
        private static bool _cfgDebug;

        internal static string PadDir {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Static", "gt7", "pad"); }
        }

        /// <summary>pad.txt を読み直す。毎フレーム走るので 1 秒キャッシュ（GtSfx.RefreshSwitches と同じ流儀）。</summary>
        private static void RefreshConfig() {
            var now = DateTime.Now;
            if ((now - _configCheckedAt).TotalSeconds < 1.0) return;
            _configCheckedAt = now;
            try {
                // ★毎回まず既定値へ戻してから読み直す。そうしないと「enabled=0 と書いた行（またはファイル自体）を消しても
                //   CM を再起動するまでパッドが死んだまま」になる（仕様: 失敗時＝既定値）。
                _cfgEnabled = true;
                _cfgConfirmCircle = false;
                _cfgStick = true;
                _cfgDeadzone = 0.55;
                _cfgDeadzoneRelease = 0.35;
                _cfgRepeatDelay = 400;
                _cfgRepeatRate = 90;
                _cfgIndex = -1;
                _cfgDs4Windows = "auto";
                _cfgBridge = "auto";
                _cfgDebug = false;

                var path = Path.Combine(PadDir, "pad.txt");
                if (!File.Exists(path)) return;   // 無ければ既定値のまま動く
                foreach (var raw in File.ReadAllLines(path)) {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    var h = line.IndexOf('#');
                    if (h > 0) line = line.Substring(0, h).Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var value = line.Substring(eq + 1).Trim();
                    switch (key) {
                        case "enabled": _cfgEnabled = ToBool(value, true); break;
                        case "confirm": _cfgConfirmCircle = value.Equals("circle", StringComparison.OrdinalIgnoreCase); break;
                        case "stick": _cfgStick = ToBool(value, true); break;
                        case "deadzone": _cfgDeadzone = ToDouble(value, 0.55); break;
                        case "deadzone_release": _cfgDeadzoneRelease = ToDouble(value, 0.35); break;
                        case "repeat_delay": _cfgRepeatDelay = (int)ToDouble(value, 400); break;
                        case "repeat_rate": _cfgRepeatRate = (int)ToDouble(value, 90); break;
                        case "index":
                            int ix;
                            _cfgIndex = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ix) && ix >= 0 && ix <= 3 ? ix : -1;
                            break;
                        case "ds4windows": _cfgDs4Windows = value; break;
                        case "bridge": _cfgBridge = value; break;
                        case "debug": _cfgDebug = ToBool(value, false); break;
                    }
                }
                if (_cfgDeadzone < 0.05) _cfgDeadzone = 0.05;
                if (_cfgDeadzone > 0.98) _cfgDeadzone = 0.98;
                if (_cfgDeadzoneRelease > _cfgDeadzone) _cfgDeadzoneRelease = _cfgDeadzone * 0.6;
                if (_cfgRepeatDelay < 60) _cfgRepeatDelay = 60;
                if (_cfgRepeatRate < 20) _cfgRepeatRate = 20;
            } catch (Exception ex) {
                Log("pad.txt: " + ex.Message);
            }
        }

        private static bool ToBool(string v, bool fallback) {
            if (string.IsNullOrEmpty(v)) return fallback;
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static double ToDouble(string v, double fallback) {
            double d;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }

        private static void Log(string line) {
            if (!_cfgDebug) return;
            try {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gtpad.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            } catch { }
        }

        // ---- 走査 ------------------------------------------------------------
        private static int _deviceIndex = -1;
        private static uint _lastPacket;
        private static int _lastScanAt;
        private static bool _scanned;

        /// <summary>接続済み index は毎フレーム、未接続 index は 2 秒に 1 回だけ再走査
        /// （未接続 index への XInputGetState は遅い）。</summary>
        private static bool Poll(out XinputState state) {
            state = new XinputState();
            if (_deviceIndex >= 0) {
                if (XInputGetState((uint)_deviceIndex, out state) == ErrorSuccess) return true;
                Log("device " + _deviceIndex + " lost");
                _deviceIndex = -1;
                _lastScanAt = Environment.TickCount;   // 切れた直後に総当たりしない
                _scanned = true;
                _lastPacket = 0;                       // 再接続時に samePacket が偶然一致して押下を取りこぼさないように
                state = new XinputState();
                return false;
            }

            var now = Environment.TickCount;
            if (_scanned && unchecked(now - _lastScanAt) < 2000) return false;
            _scanned = true;
            _lastScanAt = now;

            var from = _cfgIndex >= 0 ? _cfgIndex : 0;
            var to = _cfgIndex >= 0 ? _cfgIndex : 3;
            for (var i = from; i <= to; i++) {
                XinputState s;
                if (XInputGetState((uint)i, out s) != ErrorSuccess) continue;
                _deviceIndex = i;
                state = s;
                Log("device found: index=" + i);
                Logging.Write("[GtPad] controller on XInput index " + i);
                return true;
            }
            return false;
        }

        // ---- 状態 ------------------------------------------------------------
        private static readonly bool[] Prev = new bool[CommandCount];
        private static readonly bool[] Cur = new bool[CommandCount];
        private static readonly int[] NextRepeatAt = new int[CommandCount];
        /// <summary>[fork] このフレームまでに「ゲートが開いた状態で押し直された」か。
        /// リピートは押下エッジを踏んだキーだけ。ゲートが閉じている間に押しっぱなしだった方向が、
        /// ゲートが開いた 400ms 後に勝手に連射されるのを防ぐ。</summary>
        private static readonly bool[] Armed = new bool[CommandCount];
        private static readonly bool[] StickDir = new bool[4];   // 0=Up 1=Down 2=Left 3=Right（ヒステリシス保持）

        private static bool _prevAcs;
        private static bool _prevForeground;
        private static bool _prevConnected;
        private static int _eatUntil;
        private static bool _eatArmed;

        private static void OnFrame(object sender, EventArgs e) {
            try {
                RefreshConfig();
                Frame();
            } catch (Exception ex) {
                Reason = "error " + ex.Message;
                Log("frame ERROR: " + ex.Message);
            }
        }

        private static void Frame() {
            var now = Environment.TickCount;

            // ★AC 走行中は一切触らない。XInput の P/Invoke もウィンドウ列挙もやらずに抜ける
            //   （毎フレーム購読も外すので、走行中に CM が裏でフル回転しない）。
            if (AcsRunning() && !GtInGameSettings.AllowPadNavigation) {
                Reason = "acs running";
                GtUniversalInput.SuspendWheel();
                _prevAcs = true;
                for (var i = 0; i < CommandCount; i++) { Cur[i] = false; Prev[i] = false; Armed[i] = false; }
                for (var i = 0; i < 4; i++) StickDir[i] = false;
                if (_cursorHidden) RestoreCursor();
                Detach();
                return;
            }

            // ---- 読み取り ----
            XinputState st;
            var connected = Poll(out st);
            DeviceInfo = connected
                    ? "xinput:" + _deviceIndex + " pkt=" + st.dwPacketNumber
                    : (_xinputMode == 3 ? "xinput unavailable" : "none");

            // 切断中は全ボタン・全軸を 0 扱い（BT 切断でリピートが暴走しないため）
            var buttons = connected ? st.Gamepad.wButtons : (ushort)0;
            var lx = connected ? st.Gamepad.sThumbLX : (short)0;
            var ly = connected ? st.Gamepad.sThumbLY : (short)0;

            var samePacket = connected && st.dwPacketNumber == _lastPacket;
            if (connected) _lastPacket = st.dwPacketNumber;

            BuildState(buttons, lx, ly);
            // Preserve the real XInput connection for bridge startup. A connected wheel must not
            // prevent the DS4 bridge from starting, and its state is independent of XInput packets.
            var xinputConnected = connected;
            var wheelConnected = GtUniversalInput.MergeWheel(Cur, now);
            if (wheelConnected) {
                DeviceInfo += " + " + GtUniversalInput.DeviceInfo;
                connected = true;
                samePacket = false;
            }

            // ---- ゲート ----
            var mode = Gate(connected, now);

            // 効かせる状態の時だけ毎フレーム。それ以外は 100ms タイマに落として CM の描画をアイドルさせる
            if (mode == GateMode.Full || mode == GateMode.PickerBack || mode == GateMode.Garage || mode == GateMode.Native || mode == GateMode.SlideDismiss) Attach();
            else Detach();

            // カーソルは GT ページを出たら必ず戻す
            if (_cursorHidden && !IsGtPage()) RestoreCursor();

            if (mode == GateMode.Blocked) {
                SyncPrev();
                // 押しっぱなしのままゲートが開いた時に連射が始まらないよう、リピートは毎回 delay から張り直す
                for (var i = 1; i <= 4; i++) NextRepeatAt[i] = unchecked(now + _cfgRepeatDelay);
                for (var i = 0; i < CommandCount; i++) Armed[i] = false;
                MaybeStartDs4Windows(xinputConnected, now);
                return;
            }

            // dwPacketNumber が前回と同じフレームはボタン処理をスキップ（ただしリピートは続ける）
            if (!samePacket) {
                for (var i = 1; i < CommandCount; i++) {
                    if (!Cur[i] || Prev[i]) continue;
                    NextRepeatAt[i] = unchecked(now + _cfgRepeatDelay);
                    Armed[i] = true;   // ★ゲートが開いた状態で押し直された＝この先リピートしてよい
                    Emit((GtPadCommand)i, mode, false);
                }
            }

            // 方向だけリピート。Confirm/Back/Start は絶対にリピートしない
            // （合成 KeyEventArgs は IsRepeat=false になるので GtNav の連打ガードが効かない＝二重コースインの危険）
            for (var i = 1; i <= 4; i++) {
                if (!Cur[i] || !Armed[i]) continue;   // 押下エッジを踏んでいない＝ゲートを跨いだ押しっぱなしはリピートしない
                if (unchecked(now - NextRepeatAt[i]) < 0) continue;
                NextRepeatAt[i] = unchecked(now + _cfgRepeatRate);
                Emit((GtPadCommand)i, mode, true);
            }

            SyncPrev();
            MaybeStartDs4Windows(xinputConnected, now);
        }

        private static void SyncPrev() {
            for (var i = 0; i < CommandCount; i++) {
                Prev[i] = Cur[i];
                if (!Cur[i]) Armed[i] = false;   // 離したら arm も落とす
            }
        }

        /// <summary>ボタンビット＋左スティックを、コマンドごとのデジタル状態に落とす。</summary>
        private static void BuildState(ushort buttons, short lx, short ly) {
            for (var i = 0; i < CommandCount; i++) Cur[i] = false;

            // 左スティック（ヒステリシス: 入り deadzone / 抜け deadzone_release）
            if (_cfgStick) {
                var x = lx / 32767.0;
                var y = ly / 32767.0;
                StickDir[0] = StickDir[0] ? y > _cfgDeadzoneRelease : y > _cfgDeadzone;
                StickDir[1] = StickDir[1] ? -y > _cfgDeadzoneRelease : -y > _cfgDeadzone;
                StickDir[2] = StickDir[2] ? -x > _cfgDeadzoneRelease : -x > _cfgDeadzone;
                StickDir[3] = StickDir[3] ? x > _cfgDeadzoneRelease : x > _cfgDeadzone;
            } else {
                StickDir[0] = StickDir[1] = StickDir[2] = StickDir[3] = false;
            }

            Cur[(int)GtPadCommand.Up] = (buttons & BtnDpadUp) != 0 || StickDir[0];
            Cur[(int)GtPadCommand.Down] = (buttons & BtnDpadDown) != 0 || StickDir[1];
            Cur[(int)GtPadCommand.Left] = (buttons & BtnDpadLeft) != 0 || StickDir[2];
            Cur[(int)GtPadCommand.Right] = (buttons & BtnDpadRight) != 0 || StickDir[3];

            var a = (buttons & BtnA) != 0;      // × (PS)
            var b = (buttons & BtnB) != 0;      // ○ (PS)
            Cur[(int)GtPadCommand.Confirm] = _cfgConfirmCircle ? b : a;
            Cur[(int)GtPadCommand.Back] = _cfgConfirmCircle ? a : b;

            Cur[(int)GtPadCommand.Start] = (buttons & BtnStart) != 0;
            Cur[(int)GtPadCommand.Select] = (buttons & BtnBack) != 0;
            Cur[(int)GtPadCommand.L1] = (buttons & BtnLb) != 0;
            Cur[(int)GtPadCommand.R1] = (buttons & BtnRb) != 0;
            Cur[(int)GtPadCommand.Triangle] = (buttons & BtnY) != 0;
            Cur[(int)GtPadCommand.Square] = (buttons & BtnX) != 0;
            Cur[(int)GtPadCommand.L3] = (buttons & BtnLeftThumb) != 0;
            Cur[(int)GtPadCommand.R3] = (buttons & BtnRightThumb) != 0;
        }

        // ---- ゲート ----------------------------------------------------------
        private enum GateMode {
            Blocked,      // 全部捨てる
            Full,         // 全部通す
            OnlyStart,    // 非 GT ページ: Start だけ（脱出口）
            PickerBack,   // 車選択ダイアログ専用の論理入力へ（メインページには流さない）
            Garage,       // 前面の所有ショールームへ（メインページには流さない）
            Native,       // CM7から開く対応済み標準ページのWPF入力へ
            SlideDismiss, // 放置スライド表示中: 最初の 1 押しはスライドを消すだけ
            OpeningSkip   // オープニングムービー再生中: 1 押しでスキップ（裏の画面には流さない）
        }

        private static Window _pickerWindow;
        private static System.Reflection.PropertyInfo _garageInputActive;
        private static System.Reflection.MethodInfo _garageDispatch;

        private static bool GarageInputActive() {
            try {
                if (_garageInputActive == null || _garageDispatch == null) {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                        var type = assembly.GetType("CmJaOverlay.Viewer", false);
                        if (type == null) continue;
                        _garageInputActive = type.GetProperty("IsGarageMenuInputActive");
                        _garageDispatch = type.GetMethod("DispatchMenuInput", new[] { typeof(string) });
                        break;
                    }
                }
                return _garageInputActive != null && _garageDispatch != null && (bool)_garageInputActive.GetValue(null, null);
            } catch { return false; }
        }

        private static bool DispatchGarage(GtPadCommand command) {
            if (!GarageInputActive()) return false;
            try {
                var handled = (bool)_garageDispatch.Invoke(null, new object[] { command.ToString() });
                if (handled) { LastInputAt = DateTime.UtcNow; GtIdleSlides.NotePadInput(); }
                return handled;
            } catch (Exception e) { Log("garage input ERROR: " + e.Message); return false; }
        }

        private static bool _acsChecked;
        private static bool _acsRunning;
        private static int _acsCheckedAt;

        /// <summary>acs.exe が居るか。毎フレーム列挙しないよう 1 秒キャッシュ。</summary>
        private static bool AcsRunning() {
            var now = Environment.TickCount;
            if (_acsChecked && unchecked(now - _acsCheckedAt) < 1000) return _acsRunning;
            _acsChecked = true;
            _acsCheckedAt = now;
            try {
                // ★返る Process[] は Dispose しないとハンドルを撒き続ける
                var ps = Process.GetProcessesByName("acs");
                _acsRunning = ps.Length > 0;
                foreach (var p in ps) {
                    try { p.Dispose(); } catch { }
                }
            } catch {
                _acsRunning = false;
            }
            return _acsRunning;
        }

        private static bool IsGtPage() {
            try {
                var src = (Application.Current?.MainWindow as MainWindow)?.CurrentSource?.OriginalString;
                return src != null && src.IndexOf("/Pages/Gt/", StringComparison.OrdinalIgnoreCase) >= 0;
            } catch {
                return false;
            }
        }

        private static GateMode Gate(bool connected, int now, bool keyboard = false) {
            _pickerWindow = null;

            if (!_cfgEnabled && !keyboard) { Reason = "disabled"; return GateMode.Blocked; }

            // ★acs / 前面 / 接続の「前回値」は、MainWindow が見えていなくても（最小化中でも）更新する。
            //   ここより後ろで early return すると、「コースイン時に CM が最小化 → AC 終了 → CM 復帰」の経路で
            //   エッジが 1 度も立たず、500ms の食い込みが armed されない＝押しっぱなしの × が持ち越される。
            // acs（走行中）: 「あり→なし」の直後は 500ms 食う
            var acs = AcsRunning();
            if (_prevAcs && !acs) { _eatUntil = unchecked(now + 500); _eatArmed = true; }
            _prevAcs = acs;

            // 前面: false→true の直後も 500ms 食う（押しっぱなしの持ち越し対策）
            var fg = GtIdleSlides.IsOurForeground(null);
            if (!_prevForeground && fg) { _eatUntil = unchecked(now + 500); _eatArmed = true; }
            _prevForeground = fg;

            // 接続: なし→あり の直後も 500ms 食う（BT 再接続 / DS4Windows 起動直後 / 仮想パッド再生成の瞬間、
            //   切断中は全ボタン 0 扱い＝Prev が潰れているので、握ったままだと押下エッジが立ってしまう）
            if (!keyboard) {
                if (!_prevConnected && connected) { _eatUntil = unchecked(now + 500); _eatArmed = true; }
                _prevConnected = connected;
            }

            var main = Application.Current?.MainWindow as MainWindow;
            if (main == null || !main.IsVisible || main.WindowState == WindowState.Minimized) {
                Reason = "main window";
                return GateMode.Blocked;
            }

            if (acs && !GtInGameSettings.AllowPadNavigation) { Reason = "acs running"; return GateMode.Blocked; }
            if (!fg) { Reason = "other app"; return GateMode.Blocked; }

            if (_eatArmed) {
                if (unchecked(now - _eatUntil) < 0) { Reason = "settling"; return GateMode.Blocked; }
                _eatArmed = false;
            }

            if (!connected) { Reason = "no device"; return GateMode.Blocked; }

            // オープニングムービー中は 1 押しでスキップするだけ（GT7 と同じ）
            if (GtOpening.IsShowing) { Reason = "opening movie (skip only)"; return GateMode.OpeningSkip; }

            // 放置スライドが出ている間は、最初の 1 押しは「スライドを消すだけ」に使う（GT7 と同じ）。
            // ★スライドの窓は名前無しの new Window なので、下のウィンドウ走査では引っかからない
            if (GtIdleSlides.IsShowing) { Reason = "idle slides (dismiss only)"; return GateMode.SlideDismiss; }

            // ロード中 / ショールームツール中は止める。車選択ダイアログ中は Back だけ通す
            try {
                foreach (Window w in Application.Current.Windows) {
                    if (!w.IsVisible) continue;
                    var n = w.GetType().Name;
                    if (n == "GtLoadingWindow" || n == "LiteShowroomTools") { Reason = "window " + n; return GateMode.Blocked; }
                    if (n == "GarageCarPicker") _pickerWindow = w;
                    else if (!ReferenceEquals(w, main) && w.IsActive) { Reason = "active dialog " + n; return GateMode.Blocked; }
                }
            } catch { }

            if (_pickerWindow != null) {
                if (!_pickerWindow.IsActive || !_pickerWindow.IsEnabled) { Reason = "car picker inactive"; return GateMode.Blocked; }
                Reason = "car picker"; return GateMode.PickerBack;
            }

            if (!main.IsEnabled) { Reason = "modal dialog"; return GateMode.Blocked; }
            if (!keyboard && GtNativeMenuInput.IsSupportedPage(main.CurrentSource?.OriginalString)) {
                Reason = "native CM page"; return GateMode.Native;
            }
            var focused = Keyboard.FocusedElement;
            if (focused is TextBoxBase || focused is PasswordBox || focused is ComboBox) {
                Reason = "text/control input";
                return GateMode.Blocked;
            }

            if (GtIdleSlides.IsGarageOpen()) {
                if (!GarageInputActive()) { Reason = "garage inactive"; return GateMode.Blocked; }
                Reason = "garage menu"; return GateMode.Garage;
            }

            if (!IsGtPage()) { Reason = "not a GT page (start only)"; return GateMode.OnlyStart; }

            Reason = "ok";
            return GateMode.Full;
        }

        private static void Emit(GtPadCommand cmd, GateMode mode, bool repeat) {
            if (mode == GateMode.OnlyStart && cmd != GtPadCommand.Start) return;
            if (mode == GateMode.OpeningSkip) {
                if (repeat) return;
                Log("skip opening movie (" + cmd + ")");
                LastInputAt = DateTime.UtcNow;
                GtIdleSlides.NotePadInput();
                GtOpening.Skip();
                return;
            }
            if (mode == GateMode.SlideDismiss) {
                // 最初の 1 押しはスライドを消すだけ（裏の画面の「スタート」が起動しないように）
                if (repeat) return;
                Log("dismiss idle slides (" + cmd + ")");
                LastInputAt = DateTime.UtcNow;
                GtIdleSlides.NotePadInput();
                GtIdleSlides.ForceHide();
                return;
            }
            if (mode == GateMode.PickerBack) {
                if ((_pickerWindow as GarageCarPicker)?.HandleCommand(cmd, repeat) == true) {
                    LastInputAt = DateTime.UtcNow;
                    GtIdleSlides.NotePadInput();
                }
                return;
            }
            if (mode == GateMode.Garage) {
                Log((repeat ? "garage repeat " : "garage press ") + cmd);
                DispatchGarage(cmd);
                return;
            }
            Log((repeat ? "repeat " : "press ") + cmd);
            Dispatch(cmd);
        }

        // ---- ディスパッチ ----------------------------------------------------
        internal static bool HandleKeyboard(GtPadCommand cmd, bool repeat) {
            RefreshConfig();
            var mode = Gate(true, Environment.TickCount, true);
            if (mode == GateMode.Blocked || mode == GateMode.OnlyStart || mode == GateMode.PickerBack || mode == GateMode.Native) return false;
            if (repeat && (cmd < GtPadCommand.Up || cmd > GtPadCommand.Right)) return true;
            if (mode == GateMode.SlideDismiss) { Emit(cmd, mode, repeat); return true; }
            return Dispatch(cmd, true);
        }

        private static bool Dispatch(GtPadCommand cmd, bool keyboard = false) {
            if (GtIdleSlides.IsGarageOpen()) return DispatchGarage(cmd);
            LastInputAt = DateTime.UtcNow;
            GtIdleSlides.NotePadInput();
            // Resolve existing header ownership before switching keyboard hints releases pad focus.
            var headerHandled = keyboard && GtHeader.HandleMenuPad(cmd);
            if (keyboard) RestoreCursor();
            else HideCursor();
            SetPadMode(!keyboard);

            var main = Application.Current?.MainWindow;
            if (main == null) return false;

            if (headerHandled || !keyboard && GtHeader.HandleMenuPad(cmd)) return true;
            if (GtSettings.HandleBindingPad(cmd)) return true;

            // Start = 脱出口。どこからでもワールドマップへ
            if (cmd == GtPadCommand.Start) {
                try {
                    var src = (main as MainWindow)?.CurrentSource?.OriginalString ?? "";
                    if (src.IndexOf("/Pages/Gt/WorldMap.xaml", StringComparison.OrdinalIgnoreCase) < 0) {
                        WorldMap.Navigate("/Pages/Gt/WorldMap.xaml");
                        GtSfx.PlayCancel();
                    }
                } catch (Exception ex) {
                    Log("start ERROR: " + ex.Message);
                }
                return true;
            }

            if (!keyboard && GtNativeMenuInput.IsSupportedPage((main as MainWindow)?.CurrentSource?.OriginalString)) {
                return GtNativeMenuInput.Handle(cmd, SendKey);
            }
            if (GtFocus.Handle(cmd)) return true;

            // A real key that no GT action consumed continues through WPF normally.
            // Never synthesize that same key here (which would re-enter the shared handler).
            if (keyboard) return false;

            // [fork] ブランドセントラル 3D 中の「戻る」はキー合成しない（本体の窓が前に出てちらつく・2026-09-27）
            if (cmd == GtPadCommand.Back && BrandCentral.HandlePadBack()) return true;

            // フォールバック = キー合成
            switch (cmd) {
                case GtPadCommand.Up: SendKey(main, Key.Up); break;
                case GtPadCommand.Down: SendKey(main, Key.Down); break;
                case GtPadCommand.Left: SendKey(main, Key.Left); break;
                case GtPadCommand.Right: SendKey(main, Key.Right); break;
                case GtPadCommand.Back: SendKey(main, Key.Escape); break;
                case GtPadCommand.Confirm:
                    SendKey(main, Key.Enter);
                    GtSfx.PlaySelect();     // Back は GtNav の PreviewKeyDown が鳴らすので鳴らさない
                    break;
            }
            return true;
        }

        /// <summary>合成キー。KeyboardDevice が Preview 未 Handled なら KeyDown へ昇格させるので、
        /// ページの PreviewKeyDown と GtNav の Window クラスハンドラの両方に届く。</summary>
        internal static bool SendingKey { get; private set; }

        private static void SendKey(Window target, Key key) {
            try {
                if (target == null) return;
                var src = PresentationSource.FromVisual(target);
                if (src == null) return;
                if (Keyboard.FocusedElement == null) target.Focus();
                var focused = Keyboard.FocusedElement;
                if (focused is TextBoxBase || focused is PasswordBox || focused is ComboBox) return;   // GtNav と同じガード
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, src, Environment.TickCount, key) {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                SendingKey = true;
                InputManager.Current.ProcessInput(e);
            } catch (Exception ex) {
                Log("sendkey " + key + " ERROR: " + ex.Message);
            } finally {
                SendingKey = false;
            }
        }

        // ---- カーソル --------------------------------------------------------
        private static bool _cursorHidden;
        private static Point _cursorAnchor;

        private static void HideCursor() {
            if (_cursorHidden) return;
            try {
                var main = Application.Current?.MainWindow;
                _cursorAnchor = main == null ? new Point(0, 0) : Mouse.GetPosition(main);
                Mouse.OverrideCursor = Cursors.None;
                _cursorHidden = true;
            } catch { }
        }

        private static void RestoreCursor() {
            // [fork] ヒントをキーボード表記へ。★早期 return より前に置く: HideCursor が失敗して
            //   _cursorHidden が立っていない時でも、表記だけは必ず戻す。
            SetPadMode(false);
            if (!_cursorHidden) return;
            _cursorHidden = false;
            try {
                Mouse.OverrideCursor = null;
            } catch { }
        }

        /// <summary>本物のマウスが 8px 以上動いたらカーソルを戻し、フォーカス表現も一旦消す。</summary>
        private static void OnPreviewMouseMove(object sender, MouseEventArgs e) {
            if (!_cursorHidden) return;
            try {
                // ★基準はいつも MainWindow。sender は子窓のこともあるので e.GetPosition(sender) では座標系がぶれる
                var main = Application.Current?.MainWindow;
                if (main == null) return;
                var p = Mouse.GetPosition(main);
                var dx = p.X - _cursorAnchor.X;
                var dy = p.Y - _cursorAnchor.Y;
                if (dx * dx + dy * dy < 64) return;
                RestoreCursor();
                GtFocus.OnRealMouseMove();
                Log("real mouse move -> cursor back");
            } catch { }
        }

        // ---- DS4Windows 自動起動 ---------------------------------------------
        private static bool _ds4Tried;
        private static int _noDeviceSince;
        private static bool _noDeviceArmed;
        private static int _ds4Probes;          // 探索した回数（3 秒間隔 × 20 回 ≒ 1 分で打ち切る）
        private static int _diCheckedAt;
        private static bool _diChecked;
        private static bool _diHasDs4;

        /// <summary>XInput に 1 台も居ない状態が 3 秒続いた ∧ 走行中でない → 仮想 X360 パッドの供給元を起こす。
        /// ★2026-09-05(M3): 既定は自作ブリッジ ds4_bridge.py（pad.txt bridge=auto）。生 DS4 の有無は見ない＝
        ///   キーボードだけの時も X360 モードの AC に「パッドが 1 台居る」状態を保証する（ブリッジ側が CM/acs の生存で挿抜を管理）。
        ///   pad.txt bridge=off の時だけ従来の DS4Windows 経路（DS4Windows が動いていない ∧ 生の DS4 が DirectInput に
        ///   見える → -m で最小化起動。1 セッション 1 回だけ）。</summary>
        private static void MaybeStartDs4Windows(bool connected, int now) {
            if (connected) { _noDeviceArmed = false; return; }
            // 走行中に裏で立ち上げない（AC が掴んでいる入力に割り込むし、要望 の走行を邪魔する）
            if (AcsRunning()) { _noDeviceArmed = false; return; }
            var useBridge = BridgeConfigured();
            if (!useBridge) {
                // enabled は「CM 側をパッドで操作する」マスタースイッチ。ブリッジ（AC へのパッド供給）は bridge= だけで制御する
                if (!_cfgEnabled) return;
                if (_ds4Tried) return;
                if (string.IsNullOrEmpty(_cfgDs4Windows) || _cfgDs4Windows.Equals("off", StringComparison.OrdinalIgnoreCase)) return;
            }

            if (!_noDeviceArmed) {
                _noDeviceArmed = true;
                _noDeviceSince = now;
                return;
            }
            if (unchecked(now - _noDeviceSince) < 3000) return;
            // ★ここを抜けた後は毎フレーム下のプロセス列挙が走ってしまう（パッド未接続＝要望 の既定状態）。
            //   次の試行を最短 3 秒後にし、DS4Windows 経路は 20 回（≒1 分）で打ち切る（ブリッジ経路は Mutex 確認が安いので打ち切らない）。
            _noDeviceSince = now;
            if (useBridge) { MaybeStartBridge(now); return; }
            if (++_ds4Probes > 20) {
                _ds4Tried = true;
                Log("ds4windows: give up (no DS4 seen)");
                return;
            }

            try {
                var ps = Process.GetProcessesByName("DS4Windows");
                var running = ps.Length > 0;
                foreach (var p in ps) {
                    try { p.Dispose(); } catch { }
                }
                if (running) { _ds4Tried = true; return; }
            } catch { }

            if (!HasRawDs4(now)) return;

            _ds4Tried = true;
            var exe = FindDs4Windows();
            if (exe == null) {
                Logging.Warning("[GtPad] DS4 found but DS4Windows.exe not located");
                Log("ds4windows: exe not found");
                return;
            }
            try {
                Process.Start(new ProcessStartInfo(exe, "-m") {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
                });
                Logging.Write("[GtPad] started DS4Windows: " + exe);
                Log("ds4windows: started " + exe);
            } catch (Exception ex) {
                Logging.Warning("[GtPad] DS4Windows start failed: " + ex.Message);
                Log("ds4windows: start failed " + ex.Message);
            }
        }

        // ---- 自作ブリッジ ds4_bridge.py の自動起動（2026-09-05 M3・DS4Windows 代替） -------------
        //   配備: Data\Static\gt7\pad\bridge\ds4_bridge.py（正本は別リポジトリ）
        //   生存判定は ds4_bridge.py が握る名前付き Mutex "Local\ds4-bridge"（プロセス名 pythonw では他の Python と区別できない）
        private static int _bridgeNextTryAt;
        private static bool _bridgeInit;
        private static bool _bridgeSeenRunning;
        private static int _bridgeStarts;            // 起こしたのに一度も Mutex が見えていない連続回数（起動直後に死んでいる検出）
        private static bool _bridgeWarnedNotFound;
        private static bool _bridgeWarnedDying;
        private static bool _bridgeOffLogged;

        /// <summary>pad.txt bridge= が有効値か。他キーが 0/1 表記なので 0/false/no/none も off と読む。</summary>
        private static bool BridgeConfigured() {
            var v = (_cfgBridge ?? "").Trim();
            if (v.Length == 0) return false;
            if (v == "0" || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || v.Equals("no", StringComparison.OrdinalIgnoreCase) || v.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>ブリッジが動いているか（Mutex を開けるか）。</summary>
        internal static bool BridgeRunning() {
            System.Threading.Mutex m = null;
            try {
                return System.Threading.Mutex.TryOpenExisting(@"Local\ds4-bridge", out m);
            } catch (UnauthorizedAccessException) {
                return true;        // 居るが開く権利が無い＝動いてはいる
            } catch {
                return false;
            } finally {
                if (m != null) {
                    try { m.Dispose(); } catch { }
                }
            }
        }

        /// <summary>3 秒毎に呼ばれる。ブリッジが居なければ pythonw で起こす。見つからない／失敗は 30〜60 秒後に再試行。</summary>
        private static void MaybeStartBridge(int now) {
            if (!_bridgeInit) { _bridgeInit = true; _bridgeNextTryAt = now; }
            if (unchecked(now - _bridgeNextTryAt) < 0) return;

            // bridge-stop.bat が置く停止スイッチ。ブリッジ自身も見て終了するので、GtPad も起こし直さない
            //（無いと「停止」が数秒で取り消される。再開は bridge-start.bat＝フラグ削除）
            try {
                if (File.Exists(Path.Combine(PadDir, "bridge_off.flag"))) {
                    if (!_bridgeOffLogged) { _bridgeOffLogged = true; Log("bridge: skipped (bridge_off.flag)"); }
                    _bridgeNextTryAt = unchecked(now + 5000);
                    return;
                }
            } catch { }
            _bridgeOffLogged = false;

            if (BridgeRunning()) {
                if (!_bridgeSeenRunning) { _bridgeSeenRunning = true; Log("bridge: already running"); }
                _bridgeStarts = 0;
                _bridgeWarnedNotFound = false;
                _bridgeWarnedDying = false;
                _bridgeNextTryAt = unchecked(now + 10000);
                return;
            }
            _bridgeSeenRunning = false;
            var script = FindBridgeScript();
            var pyw = FindPythonw();
            if (script == null || pyw == null) {
                if (!_bridgeWarnedNotFound) {
                    _bridgeWarnedNotFound = true;   // アプリログには 1 回だけ（パッド未接続が既定状態なので毎分出すと埋まる）
                    Logging.Warning("[GtPad] ds4-bridge not started: script=" + (script ?? "(none)") + " pythonw=" + (pyw ?? "(none)"));
                }
                Log("bridge: not found script=" + (script ?? "-") + " pythonw=" + (pyw ?? "-"));
                _bridgeNextTryAt = unchecked(now + 60000);
                return;
            }
            try {
                using (Process.Start(new ProcessStartInfo(pyw, "\"" + script + "\" --from-cm") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(script) ?? ""
                })) { }
                _bridgeStarts++;
                Logging.Write("[GtPad] started ds4-bridge (" + _bridgeStarts + "): " + pyw + " " + script);
                Log("bridge: started (" + _bridgeStarts + ") " + script);
                // 起こしても一度も Mutex が見えない＝起動直後に死んでいる（pythonw は traceback をどこにも出せない）。
                // 3 回目からは 5 分に間引き、1 回だけ警告する（旧 DS4Windows 経路の「1 セッション 1 回」に相当する保護）
                if (_bridgeStarts >= 3) {
                    if (!_bridgeWarnedDying) {
                        _bridgeWarnedDying = true;
                        Logging.Warning("[GtPad] ds4-bridge keeps dying at startup; see %LOCALAPPDATA%\\ds4-bridge\\bridge.log");
                    }
                    _bridgeNextTryAt = unchecked(now + 300000);
                } else {
                    _bridgeNextTryAt = unchecked(now + 30000);
                }
            } catch (Exception ex) {
                Logging.Warning("[GtPad] ds4-bridge start failed: " + ex.Message);
                Log("bridge: start failed " + ex.Message);
                _bridgeNextTryAt = unchecked(now + 60000);
            }
        }

        private static string FindBridgeScript() {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(_cfgBridge) && !_cfgBridge.Equals("auto", StringComparison.OrdinalIgnoreCase)) {
                candidates.Add(_cfgBridge);
            }
            candidates.Add(Path.Combine(PadDir, "bridge", "ds4_bridge.py"));
            foreach (var c in candidates) {
                try {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
                } catch { }
            }
            return null;
        }

        /// <summary>vgamepad / hidapi が入っている Python の pythonw.exe。PATH 上で Lib\site-packages\vgamepad を持つものを優先。</summary>
        private static string FindPythonw() {
            var candidates = new List<string>();
            try {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(';')) {
                    var d = dir.Trim();
                    if (d.Length == 0) continue;
                    try {
                        var exe = Path.Combine(d, "pythonw.exe");
                        if (File.Exists(exe)) candidates.Add(exe);
                    } catch { }
                }
            } catch { }
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, @"Programs\Python\Python310\pythonw.exe"));
            foreach (var c in candidates) {
                try {
                    var dir = Path.GetDirectoryName(c) ?? "";
                    if (Directory.Exists(Path.Combine(dir, "Lib", "site-packages", "vgamepad"))) return c;
                } catch { }
            }
            foreach (var c in candidates) {
                try {
                    if (File.Exists(c)) return c;
                } catch { }
            }
            return null;
        }

        /// <summary>生の DS4 が DirectInput に見えるか（SlimDX。列挙は 5 秒に 1 回まで）。</summary>
        private static bool HasRawDs4(int now) {
            if (_diChecked && unchecked(now - _diCheckedAt) < 5000) return _diHasDs4;
            _diChecked = true;
            _diCheckedAt = now;
            _diHasDs4 = false;
            SlimDX.DirectInput.DirectInput di = null;
            try {
                di = new SlimDX.DirectInput.DirectInput();
                var devices = di.GetDevices(SlimDX.DirectInput.DeviceClass.GameController,
                        SlimDX.DirectInput.DeviceEnumerationFlags.AttachedOnly);
                if (devices == null) return false;
                foreach (var d in devices) {
                    var name = d.ProductName ?? "";
                    if (name.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0) {
                        _diHasDs4 = true;
                        break;
                    }
                    // ProductGuid の Data1 = (PID << 16) | VID
                    var s = d.ProductGuid.ToString("N");
                    uint data1;
                    if (s.Length >= 8 && uint.TryParse(s.Substring(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data1)) {
                        var vid = data1 & 0xFFFF;
                        var pid = (data1 >> 16) & 0xFFFF;
                        if (vid == 0x054C && (pid == 0x09CC || pid == 0x05C4)) {
                            _diHasDs4 = true;
                            break;
                        }
                    }
                }
            } catch (Exception ex) {
                Log("directinput: " + ex.Message);
            } finally {
                try {
                    if (di != null) di.Dispose();
                } catch { }
            }
            return _diHasDs4;
        }

        private static string FindDs4Windows() {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(_cfgDs4Windows) && !_cfgDs4Windows.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    && !_cfgDs4Windows.Equals("off", StringComparison.OrdinalIgnoreCase)) {
                candidates.Add(_cfgDs4Windows);
            }
            var lnk = FindStartMenuTarget("DS4Windows.lnk");
            if (lnk != null) candidates.Add(lnk);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, @"DS4Windows\DS4Windows.exe"));
            foreach (var c in candidates) {
                try {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
                } catch { }
            }
            return null;
        }

        /// <summary>スタートメニューの .lnk のリンク先（WScript.Shell）。見つからなければ null。</summary>
        private static string FindStartMenuTarget(string linkName) {
            try {
                var roots = new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                };
                foreach (var root in roots) {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    string[] found;
                    try {
                        found = Directory.GetFiles(root, linkName, SearchOption.AllDirectories);
                    } catch {
                        continue;
                    }
                    foreach (var f in found) {
                        try {
                            var type = Type.GetTypeFromProgID("WScript.Shell");
                            if (type == null) return null;
                            dynamic shell = Activator.CreateInstance(type);
                            dynamic shortcut = shell.CreateShortcut(f);
                            var target = (string)shortcut.TargetPath;
                            if (!string.IsNullOrEmpty(target)) return target;
                        } catch { }
                    }
                }
            } catch { }
            return null;
        }
    }
}
