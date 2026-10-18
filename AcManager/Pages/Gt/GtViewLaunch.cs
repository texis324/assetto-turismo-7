// [fork] 鑑賞モード（AC 本体で車を眺める）の起動。2026-09-28 Claude
//   要望「ワイはただ鑑賞したいだけ…これができなきゃ車を眺められないじゃないか」:
//   CSP で守られた車（暗号化 kn5）は CM のショールームでは 3D が出ずプレビュー画像に逃げる。AC 本体なら出るので、
//   ブランドセントラルの「ACで鑑賞」から、AC 側の GtView アプリ（apps\lua\GtView）が受け持つ鑑賞モードで起動する。
//   ・札 gt_start.ini: ENABLED=1 / MODE=view / TRACK / LAYOUT / CAR / SKIN / STAMP（GtStart は MODE=view を見たら何もしない）
//   ・幕（GtLoadingWindow）は GtView が STATE=view を書いたら下りる
//   ・戻り: GtView が REQUEST=view_done / VIEW_SKIN=<最後に見ていた色> を書いて AC を閉じる → その色を返す
//   ・場所と時刻は Data\Static\gt7\view\view.txt（track= / layout= / time=HH:MM / session=hotlap|practice）。無ければニュル GP の 15:00
//     practice = ピットから出る（撮影スタジオ型のコース「preview」はピットの位置がスタジオの真ん中＝作者の指定）
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AcManager.Pages.Drive;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using AcTools.Utils;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal static class GtViewLaunch {
        private const string DefaultTrack = "ks_nurburgring";
        private const string DefaultLayout = "layout_gp_a";
        private const int DefaultTimeMinutes = 15 * 60;

        internal static bool IsBusy { get; private set; }

        private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Static", "gt7", "view", "view.txt");
        private static string FlagPath => Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt_start.ini");

        private static void ReadConfig(out string track, out string layout, out int timeMinutes, out bool practice) {
            track = DefaultTrack;
            layout = DefaultLayout;
            timeMinutes = DefaultTimeMinutes;
            practice = false;
            try {
                if (!File.Exists(ConfigPath)) return;
                foreach (var raw in File.ReadAllLines(ConfigPath)) {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var value = line.Substring(eq + 1).Trim();
                    switch (key) {
                        case "track": if (value.Length > 0) track = value; break;
                        case "layout": layout = value; break;
                        case "session": practice = value.Equals("practice", StringComparison.OrdinalIgnoreCase); break;
                        case "time":
                            var p = value.Split(':');
                            int h, m = 0;
                            if (p.Length >= 1 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
                                    && (p.Length < 2 || int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
                                    && h >= 0 && h < 24 && m >= 0 && m < 60) {
                                timeMinutes = h * 60 + m;
                            }
                            break;
                    }
                }
            } catch (Exception e) {
                Logging.Warning("[GtView] config: " + e.Message);
            }
        }

        private static void WriteFlag(TrackObjectBase layout, CarObject car, string skinId) {
            var dir = AcPaths.GetDocumentsCfgDirectory();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var stamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            File.WriteAllText(FlagPath, "[GT]\r\nENABLED=1\r\nMODE=view\r\nTRACK=" + (layout.MainTrackObject?.Id ?? layout.Id)
                    + "\r\nLAYOUT=" + (layout.LayoutId ?? "") + "\r\nCAR=" + car.Id + "\r\nSKIN=" + (skinId ?? "")
                    + "\r\nSTAMP=" + stamp.ToString(CultureInfo.InvariantCulture)
                    + "\r\nUI_VOLUME=" + GtSfx.Volume.ToString("R", CultureInfo.InvariantCulture) + "\r\n");
        }

        // ピットクルーを隠す（CSP の「コース：その他」[CREW] HIDE_ENTIRELY）。鑑賞の間だけユーザー上書きの
        //   cfg\extension\track_adjustments.ini に書き、終わったら元に戻す（2026-09-28 要望「ピットの人邪魔やな」）。
        //   元のファイルがあれば退避して、その中身の後ろに足す（本人の他の上書きはそのまま効く）。自分の印の付いたファイルだけ戻す
        private const string CrewMarker = "; written by CM fork (GtView) — removed automatically after viewing";

        private static void SetHideCrew(bool on) {
            try {
                var dir = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "extension");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "track_adjustments.ini");
                var backup = path + ".gtview-bak";
                if (on) {
                    var original = "";
                    if (File.Exists(path)) {
                        original = File.ReadAllText(path);
                        if (original.Contains(CrewMarker)) return;   // 前回の後始末が済んでいない＝もう入っている
                        File.Copy(path, backup, true);
                        original = original.TrimEnd() + Environment.NewLine + Environment.NewLine;
                    }
                    File.WriteAllText(path, original + CrewMarker + Environment.NewLine + "[CREW]" + Environment.NewLine + "HIDE_ENTIRELY=1" + Environment.NewLine);
                } else if (File.Exists(path) && File.ReadAllText(path).Contains(CrewMarker)) {
                    if (File.Exists(backup)) {
                        File.Copy(backup, path, true);
                        File.Delete(backup);
                    } else {
                        File.Delete(path);
                    }
                }
            } catch (Exception e) {
                Logging.Warning("[GtView] track_adjustments.ini: " + e.Message);
            }
        }

        /// <summary>GtView が書いた VIEW_SKIN を読み、札を無効（ENABLED=0）に戻す。</summary>
        private static string ReadResultAndReset() {
            try {
                if (!File.Exists(FlagPath)) return null;
                string request = null, skin = null;
                foreach (var raw in File.ReadAllLines(FlagPath)) {
                    var t = raw.Trim();
                    if (t.StartsWith("REQUEST=", StringComparison.OrdinalIgnoreCase)) request = t.Substring(8).Trim();
                    if (t.StartsWith("VIEW_SKIN=", StringComparison.OrdinalIgnoreCase)) skin = t.Substring(10).Trim();
                }
                File.WriteAllText(FlagPath, "[GT]" + Environment.NewLine + "ENABLED=0" + Environment.NewLine);
                return request == "view_done" && !string.IsNullOrEmpty(skin) ? skin : null;
            } catch (Exception e) {
                Logging.Warning("[GtView] result: " + e.Message);
                return null;
            }
        }

        /// <summary>AC を鑑賞モードで起動し、閉じるまで待つ。戻り値 = AC で最後に見ていた色（スキン ID）。起動できなければ null。</summary>
        /// <param name="onCurtain">幕を出した直後に呼ぶ（呼び出し側が 3D ショールームを幕の裏で閉じるため）。起動を始められなかった時は呼ばない。</param>
        internal static async Task<string> RunAsync(CarObject car, string skinId, Action onCurtain = null) {
            if (IsBusy || car == null) return null;
            if (Process.GetProcessesByName("acs").Length > 0) {
                NonfatalError.Notify("AC がすでに起動しています", "鑑賞モードは AC を閉じてからもう一度選んでください。");
                return null;
            }
            IsBusy = true;
            GtLoadingWindow loading = null;
            GtGameUi gtUi = null;
            try {
                string trackId, layoutId;
                int timeMinutes;
                bool practice;
                ReadConfig(out trackId, out layoutId, out timeMinutes, out practice);
                await TracksManager.Instance.EnsureLoadedAsync();
                var track = TracksManager.Instance.GetById(trackId) ?? TracksManager.Instance.GetById(DefaultTrack) ?? TracksManager.Instance.GetDefault();
                if (track == null) {
                    NonfatalError.Notify("鑑賞モードを開けませんでした", "コースが見つかりません: " + trackId);
                    return null;
                }
                TrackObjectBase layout = track;
                if (track.MultiLayouts != null && track.MultiLayouts.Count > 0) {
                    layout = track.MultiLayouts.FirstOrDefault(l => string.Equals(l.LayoutId, layoutId, StringComparison.OrdinalIgnoreCase))
                            ?? track.MultiLayouts.First();
                }
                WriteFlag(layout, car, skinId);
                loading = new GtLoadingWindow();
                loading.SetContent(track, layout, null, car, null);
                loading.Show();
                try { onCurtain?.Invoke(); } catch (Exception e) { Logging.Warning("[GtView] onCurtain: " + e.Message); }
                CourseEntry.SetHideMainMenu(true);   // ピットの画面を出さない（DRIVE は GtView が内部で押す）
                SetHideCrew(true);
                gtUi = new GtGameUi();
                var w = loading;
                gtUi.StatusChanged += msg => w.Dispatcher.BeginInvoke(new Action(() => w.SetStatus(msg)));
                GtGameUi.Pending = gtUi;
                string saved = null;
                try { saved = ValuesStorage.Get<string>("__QuickDrive_Main"); } catch (Exception e) { Logging.Warning("[GtView] preset: " + e.Message); }
                Logging.Write("[GtView] launch " + car.Id + "/" + skinId + " @ " + layout.Id + (practice ? " practice " : " hotlap ") + timeMinutes / 60 + ":" + (timeMinutes % 60).ToString("00"));
                var ok = await QuickDrive.RunAsync(car: car, carSkinId: string.IsNullOrEmpty(skinId) ? null : skinId, track: layout,
                        time: timeMinutes * 60, serializedPreset: string.IsNullOrEmpty(saved) ? null : saved,
                        mode: practice ? QuickDrive.ModePractice : QuickDrive.ModeHotlap);
                if (!ok) Logging.Warning("[GtView] RunAsync returned false");
            } catch (Exception e) {
                Logging.Warning("[GtView] launch: " + e);
                NonfatalError.Notify("鑑賞モードを開けませんでした", e.Message);
            } finally {
                if (ReferenceEquals(GtGameUi.Pending, gtUi)) GtGameUi.Pending = null;
                CourseEntry.SetHideMainMenu(false);   // 自分の印の付いた gui.ini だけ戻す（書いていなければ何もしない）
                SetHideCrew(false);
                try { loading?.Drop("done"); } catch { }
                IsBusy = false;
            }
            return ReadResultAndReset();
        }
    }
}
