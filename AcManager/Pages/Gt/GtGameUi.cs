// [fork] GT7 風コースインの間だけ、CM 標準の「ゲーム実行中」ダイアログ(GameDialog)の代わりに使う IGameUi。
//   画面は CourseEntry のローディング画面が受け持つので、ここは進捗メッセージを流すだけ。結果表示はしない（ラップタイム等は
//   LapTimesManager / RaceResultsStorage が GameWrapper のイベントで別途保存している）。
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using AcTools.DataFile;
using AcTools.Utils;
using AcManager.Tools.SemiGui;
using AcTools.Processes;
using FirstFloor.ModernUI.Dialogs;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    public class GtGameUi : IGameUi {
        /// <summary>次の GameWrapper.StartAsync でこれを使わせる（GameWrapperUiFactory が拾う）。</summary>
        public static GtGameUi Pending;

        public event Action<string> StatusChanged;
        internal Action<Game.StartProperties> OnStart;
        private string _message = "";

        /// <summary>[fork] 2026-09-20 要望「セッティングにアシスト設定を追加して、MT か AT か選択できるように」。
        /// GT7 HUD のポーズ画面「アシスト・明るさ」が保存した設定（Documents\Assetto Corsa\cfg\gt7_assists.ini の [ASSISTS]）を、
        /// 起動設定のアシストへ上書きする＝次のコースインからも同じ設定で始まる。ファイルが無い・読めない項目は CM の設定のまま。</summary>
        internal static void ApplySavedAssists(Game.StartProperties properties) {
            try {
                var path = Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt7_assists.ini");
                var assists = properties?.AssistsProperties;
                if (assists == null || !File.Exists(path)) return;
                var section = new IniFile(path)["ASSISTS"];
                Func<string, int?> read = key => {
                    int value;
                    return int.TryParse((section.GetPossiblyEmpty(key) ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : (int?)null;
                };
                var v = read("AUTO_SHIFTER"); if (v.HasValue) assists.AutoShifter = v.Value != 0;
                v = read("AUTO_CLUTCH"); if (v.HasValue) assists.AutoClutch = v.Value != 0;
                v = read("AUTO_BLIP"); if (v.HasValue) assists.AutoBlip = v.Value != 0;
                v = read("IDEAL_LINE"); if (v.HasValue) assists.IdealLine = v.Value != 0;
                v = read("ABS"); if (v.HasValue && v.Value >= 0 && v.Value <= 2) assists.Abs = (AssistState)v.Value;
                v = read("TRACTION_CONTROL"); if (v.HasValue && v.Value >= 0 && v.Value <= 2) assists.TractionControl = (AssistState)v.Value;
                v = read("STABILITY_CONTROL"); if (v.HasValue) assists.StabilityControl = Math.Max(0, Math.Min(100, v.Value));
                // [fork] 2026-10-17 要望「ダメージ 0 で走ってたのにタイヤが曲がる」: GT7 HUD の「車のダメージ」(CAR_DAMAGE) を起動時の DAMAGE にも反映する。
                //   走行中の physics.setAssists({ damageRate = 0 }) は効かず（ログで mechanicalDamageRate=1 のまま）、CM の既定 DAMAGE=100 で走っていた。
                v = read("CAR_DAMAGE"); if (v.HasValue) assists.Damage = v.Value != 0 ? 100d : 0d;
                Logging.Write("[GtAssists] applied from gt7_assists.ini: autoShifter=" + assists.AutoShifter + " autoClutch=" + assists.AutoClutch
                        + " abs=" + assists.Abs + " tc=" + assists.TractionControl + " stability=" + assists.StabilityControl + " damage=" + assists.Damage);
            } catch (Exception e) { Logging.Warning("[GtAssists] " + e.Message); }
        }

        public void Show(Game.StartProperties properties, GameMode mode) {
            OnStart?.Invoke(properties);
            Set("準備中…");
        }

        public void OnProgress(string message, AsyncProgressEntry? subProgress = null, Action subCancellationCallback = null) {
            Set(message);
        }

        public void OnProgress(Game.ProgressState progress) {
            switch (progress) {
                case Game.ProgressState.Preparing: Set("準備中…"); break;
                case Game.ProgressState.Launching: Set("Assetto Corsa を起動中…"); break;
                case Game.ProgressState.Waiting: Set("走行中"); break;
                case Game.ProgressState.Finishing: Set("終了処理中…"); break;
            }
        }

        public void OnResult(Game.Result result, ReplayHelper replayHelper) {
            Set("");
        }

        public void OnError(Exception exception) {
            Logging.Warning("[GtGameUi] " + exception);
            NonfatalError.Notify("コースインに失敗しました", exception);
        }

        public string GetCurrentMessage() => _message;

        public CancellationToken CancellationToken => CancellationToken.None;

        private void Set(string s) {
            _message = s;
            try { StatusChanged?.Invoke(s); } catch { }
        }

        public void Dispose() {
            if (ReferenceEquals(Pending, this)) Pending = null;
        }
    }
}
