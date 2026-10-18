// [fork] CM7 の全体音量。メニュー操作音・オープニング（ロゴ演出/ムービー）・BGM に共通で掛かるマスター。
//   要望 2026-10-04「CM7について、全体音量を調節できるようにしたい」
//   目盛り 0〜100 は GtVolumeCurve（1% = 0.6 dB・100% = 従来どおりの大きさ・0% = 消音）。既定 100。
//   各スライダー（クリック音/ムービー/BGM）はそのまま「釣り合い」用。マスターはその上に掛け算するだけ。
//
//   保存先 = Data\Locales\ja-overlay\master.ini（percent=NN / gain=0.0000）。
//     BGM は自作 DLL（CmJaOverlay）の側で鳴らしているので、同じファイルを DLL も読む＝起動順に依存しない。
//     変更したら DLL の Bgm.SetMasterGain をリフレクションで叩いて即反映（DLL が古くて無ければ、1 秒以内に tick が拾う）。
//   ⚠AC 側（走行中の音・GT7 HUD の音）は対象外。AC の audio.ini とは別の話。
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal static class GtMasterVolume {
        public const int DefaultPercent = 100;

        private static int? _percent;
        private static MethodInfo _bgmSet;
        private static bool _bgmLooked;

        private static string IniPath {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Locales", "ja-overlay", "master.ini"); }
        }

        /// <summary>目盛り 0〜100。ファイルが無い/読めない時は 100（＝今までと同じ大きさ）。</summary>
        public static int Percent {
            get {
                if (_percent.HasValue) return _percent.Value;
                var p = DefaultPercent;
                try {
                    var path = IniPath;
                    if (File.Exists(path)) {
                        foreach (var line in File.ReadAllLines(path)) {
                            var eq = line.IndexOf('=');
                            if (eq <= 0 || line.Substring(0, eq).Trim() != "percent") continue;
                            int v;
                            if (int.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) p = v;
                        }
                    }
                } catch { }
                _percent = Clamp(p);
                return _percent.Value;
            }
            set {
                var p = Clamp(value);
                _percent = p;
                Save(p);
                GtSfx.ApplyMaster();
                PushToBgm();
            }
        }

        /// <summary>各音量に掛ける 0〜1（振幅）。</summary>
        public static double Gain {
            get { return GtVolumeCurve.ToGain(Percent); }
        }

        private static int Clamp(int p) { return Math.Max(0, Math.Min(100, p)); }

        private static void Save(int percent) {
            try {
                var path = IniPath;
                var dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(path,
                        "percent=" + percent.ToString(CultureInfo.InvariantCulture) + "\r\n"
                        + "gain=" + GtVolumeCurve.ToGain(percent).ToString("R", CultureInfo.InvariantCulture) + "\r\n");
            } catch (Exception ex) {
                Logging.Warning("[GtMasterVolume] save: " + ex.Message);
            }
        }

        /// <summary>BGM（DLL 側）へ今の掛け率を即座に渡す。DLL が見つからない/古い時は黙って何もしない（tick が拾う）。</summary>
        private static void PushToBgm() {
            try {
                if (!_bgmLooked) {
                    _bgmLooked = true;
                    var type = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(a => a.GetName().Name == "CmJaOverlay")
                            .Select(a => a.GetType("CmJaOverlay.Bgm"))
                            .FirstOrDefault(t => t != null);
                    if (type != null) _bgmSet = type.GetMethod("SetMasterGain", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (_bgmSet != null) _bgmSet.Invoke(null, new object[] { Gain });
            } catch (Exception ex) {
                Logging.Warning("[GtMasterVolume] bgm push: " + ex.Message);
            }
        }
    }
}
