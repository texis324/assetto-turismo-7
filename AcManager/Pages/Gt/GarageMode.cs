// [fork] GT7風ガレージ — ワールドマップの「ガレージ」ノードから入る。
//   CmJaOverlay.dll の Viewer.OpenGarage(Window, car, skin, Action<string>) をリフレクションで呼び、
//   ショーケース(3D・自動カメラ)＋左スペックパネル(PP/スペック/ボディカラー)を表示する。
//   "switch"（乗り換え）が来たら SelectCarDialog → __QuickDrive_Main に反映 → ガレージへ再入場。
//   DLL が無い環境では従来どおり車一覧ページへ。
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using AcManager.Pages.Windows;
using AcManager.Tools.Objects;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal static class GarageMode {
        private static Type ViewerType() {
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CmJaOverlay.Viewer", false)).FirstOrDefault(t => t != null);
        }

        /// <summary>ショールームの黒幕を下ろす（遷移先の画面が乗った後に呼ぶ＝ワールドマップのちら見え防止）。</summary>
        private static void DropCurtain() {
            try { ViewerType()?.GetMethod("DropCurtain", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null); } catch (Exception e) { Logging.Warning("[GarageMode] drop curtain: " + e.Message); }
        }

        /// <summary>次のガレージ入場でエンジン始動演出を鳴らす（乗り換えで車が変わった時だけ）。</summary>
        private static void RequestEngineIntro() {
            try { ViewerType()?.GetField("NextGarageEngineIntro", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, true); } catch (Exception e) { Logging.Warning("[GarageMode] intro flag: " + e.Message); }
        }

        public static void Enter() {
            try {
                var car = WorldMap.FindCurrentCar();
                var type = ViewerType();
                var open = type?.GetMethod("OpenGarage", BindingFlags.Public | BindingFlags.Static);
                if (open != null && car != null) {
                    open.Invoke(null, new object[] { Application.Current?.MainWindow, car, car.SelectedSkin, new Action<string>(OnAction) });
                    return;
                }
                Logging.Warning("[GarageMode] OpenGarage not available, falling back");
            } catch (Exception e) {
                Logging.Warning("[GarageMode] " + e.Message);
            }
            (Application.Current?.MainWindow as MainWindow)?.NavigateTo(new Uri("/Pages/Lists/CarsListPage.xaml", UriKind.Relative));
        }

        private static void OnAction(string cmd) {
            if (cmd == "setup") {
                // ガレージの🛠 → GT7風セッティングシートへ（Escでワールドマップに戻る）
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() => {
                    var car = WorldMap.FindCurrentCar();
                    var uri = "/Pages/Gt/SettingSheet.xaml" + (car != null ? "?CarId=" + Uri.EscapeDataString(car.Id) : "");
                    (Application.Current?.MainWindow as MainWindow)?.NavigateTo(new Uri(uri, UriKind.Relative));
                    // セッティングシートが乗ってから幕を下ろす(ワールドマップのちら見え防止)
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(DropCurtain), System.Windows.Threading.DispatcherPriority.Background);
                }));
                return;
            }
            if (cmd == "settings") {
                // [fork] ガレージのヘッダー（本物の GtHeader）の「設定」→ ショールームが閉じた後に設定画面へ（2026-09-27）
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() => {
                    WorldMap.Navigate(GtHeader.SettingsUri);
                    // 幕は張ったまま渡ってくる（Viewer.Finish の curtain held）→ 設定画面が乗ってから下ろす
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(DropCurtain), System.Windows.Threading.DispatcherPriority.Background);
                }));
                return;
            }
            if (cmd != "switch") return;
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() => {
                try {
                    var current = WorldMap.FindCurrentCar();
                    // [fork] GT7の「乗り換え」画面(白グレーの所有車リスト・GarageCarPicker)。スキンはガレージの←→で変える
                    var picker = new GarageCarPicker(current);
                    picker.ShowDialog();
                    if (picker.GoSettings) {
                        WorldMap.Navigate(GtHeader.SettingsUri);
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(DropCurtain), System.Windows.Threading.DispatcherPriority.Background);
                        return;   // 設定を開いた直後にガレージへ再入場しない。
                    }
                    if (picker.GoBrandCentral) {
                        // [fork] 2026-09-06 「ブランドセントラルでクルマを探す」→ ガレージへは戻らずカタログへ（幕は乗ってから下ろす）
                        WorldMap.Navigate("/Pages/Gt/BrandCentral.xaml");
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(DropCurtain), System.Windows.Threading.DispatcherPriority.Background);
                        return;
                    }
                    if (picker.Picked != null) {
                        CourseEntry.SetPresetCar(picker.Picked);
                        // 車が変わった時だけエンジン始動演出(要望 2026-08-25: 入場のたびには鳴らさない)
                        if (!string.Equals(picker.Picked.Id, current?.Id, StringComparison.OrdinalIgnoreCase)) RequestEngineIntro();
                    }
                } catch (Exception e) {
                    Logging.Warning("[GarageMode] switch: " + e.Message);
                }
                Enter();   // 選んでもキャンセルでもガレージへ戻る（GT7 と同じ）
            }));
        }
    }
}
