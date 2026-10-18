// [fork] GT7 風「ガレージ所有」のデータ層 — ブランドセントラル（全車カタログ）とガレージ（手元の車）を分ける。
//   所有リスト = Data\Static\gt7\garage.json（購入/クレジットの概念は出さない: 要望 2026-09-05）。
//   所有感の正体は「カタログ（全車）と手元（選んだ車だけ）を分けること」(要望 2026-09-05)。
//   ・初期状態は今の愛車 1 台（GT7 の最初の 1 台に倣う）。乗っている車は常に所有扱い＝手放せない
//   ・ブランドセントラルで「ガレージに入れる」→ Owned + Pending に積む → ワールドマップ復帰時に「届きました」の納車演出（WorldMap）
//   ・レースで使えるのはガレージの車だけ（GarageCarPicker の母集団をここに絞る＝制約が所有感を本物にする）
//   ・「手放す」は売却ではない（クレジット無し）。ブランドセントラルでいつでも取り戻せる
//   json を手で直したら CM7 再起動（メモリキャッシュあり）。テストチャンネル: garage=add:<id>|remove:<id>|list
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using FirstFloor.ModernUI.Helpers;
using Newtonsoft.Json;

namespace AcManager.Pages.Gt {
    internal sealed class GtGarageEvent {
        public string Car, Kind, At;
    }

    internal sealed class GtGarageData {
        public List<string> Owned = new List<string>();
        public List<string> Pending = new List<string>();
        public List<GtGarageEvent> History = new List<GtGarageEvent>();
    }

    internal static class GtGarage {
        public static string FilePath => Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "garage.json");

        private static GtGarageData _data;

        /// <summary>所有リストが変わった（ヘッダー等の表示更新用）。</summary>
        public static event EventHandler Changed;

        private static GtGarageData Data {
            get {
                if (_data == null) _data = Load();
                return _data;
            }
        }

        private static bool Has(List<string> list, string id) {
            return id != null && list.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        }

        private static string Now() {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }

        private static GtGarageData Load() {
            GtGarageData d = null;
            var fresh = true;
            try {
                if (File.Exists(FilePath)) {
                    d = JsonConvert.DeserializeObject<GtGarageData>(File.ReadAllText(FilePath));
                    fresh = false;
                }
            } catch (Exception e) {
                Logging.Warning("[GtGarage] load: " + e.Message);
            }
            if (d == null) d = new GtGarageData();
            if (d.Owned == null) d.Owned = new List<string>();
            if (d.Pending == null) d.Pending = new List<string>();
            if (d.History == null) d.History = new List<GtGarageEvent>();
            // 最初の 1 台 = 今の愛車（GT7 の最初の 1 台に倣う）
            var added = EnsureCurrent(d);
            if (added || fresh) Save(d);
            Logging.Write("[GtGarage] owned=" + d.Owned.Count + " pending=" + d.Pending.Count + (fresh ? " (new)" : ""));
            return d;
        }

        /// <summary>乗っている車は常に所有扱い。無ければ黙って足す（納車演出は無し）。</summary>
        private static bool EnsureCurrent(GtGarageData d) {
            try {
                var cur = WorldMap.FindCurrentCar();
                if (cur == null || Has(d.Owned, cur.Id)) return false;
                d.Owned.Add(cur.Id);
                d.History.Add(new GtGarageEvent { Car = cur.Id, Kind = d.Owned.Count == 1 ? "initial" : "current", At = Now() });
                return true;
            } catch {
                return false;
            }
        }

        private static void Save(GtGarageData d) {
            try {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(d, Formatting.Indented));
            } catch (Exception e) {
                Logging.Warning("[GtGarage] save: " + e.Message);
            }
        }

        private static void Touch() {
            Save(Data);
            try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
        }

        /// <summary>json を手で直した時用（次のアクセスで読み直す）。</summary>
        public static void Reload() {
            _data = null;
        }

        public static IReadOnlyList<string> OwnedIds => Data.Owned;
        public static IReadOnlyList<string> PendingIds => Data.Pending;
        public static int Count => Data.Owned.Count;
        public static bool HasPending => Data.Pending.Count > 0;

        public static bool IsOwned(CarObject car) {
            return car != null && IsOwned(car.Id);
        }

        public static bool IsOwned(string id) {
            return Has(Data.Owned, id);
        }

        /// <summary>今乗っている車（__QuickDrive_Main）か。</summary>
        public static bool IsCurrent(CarObject car) {
            try {
                var cur = WorldMap.FindCurrentCar();
                return cur != null && car != null && string.Equals(cur.Id, car.Id, StringComparison.OrdinalIgnoreCase);
            } catch {
                return false;
            }
        }

        /// <summary>ガレージの車（CM に読み込まれている有効な車だけ・所有順）。乗っている車は必ず含む。</summary>
        public static List<CarObject> OwnedCars() {
            var d = Data;
            if (EnsureCurrent(d)) Touch();
            var list = new List<CarObject>();
            foreach (var id in d.Owned.ToList()) {
                try {
                    var car = CarsManager.Instance.GetById(id);
                    if (car != null && car.Enabled && !list.Contains(car)) list.Add(car);
                } catch (Exception e) {
                    Logging.Warning("[GtGarage] " + id + ": " + e.Message);
                }
            }
            return list;
        }

        /// <summary>カタログ全体の台数（有効な車）。CarsManager 未ロードなら 0。</summary>
        public static int TotalCount {
            get {
                try {
                    return CarsManager.Instance.Loaded.Count(x => x.Enabled);
                } catch {
                    return 0;
                }
            }
        }

        /// <summary>「ガレージに入れる」。すでに所有なら false。納車待ちに積む（ワールドマップで演出）。</summary>
        public static bool Add(CarObject car) {
            if (car == null || Has(Data.Owned, car.Id)) return false;
            Data.Owned.Add(car.Id);
            if (!Has(Data.Pending, car.Id)) Data.Pending.Add(car.Id);
            Data.History.Add(new GtGarageEvent { Car = car.Id, Kind = "in", At = Now() });
            Touch();
            Logging.Write("[GtGarage] in: " + car.Id + " (owned " + Data.Owned.Count + ")");
            return true;
        }

        /// <summary>「手放す」。乗っている車・最後の 1 台は手放せない（reason に理由）。</summary>
        public static bool Remove(CarObject car, out string reason) {
            reason = null;
            if (car == null || !Has(Data.Owned, car.Id)) {
                reason = "このクルマはガレージにありません";
                return false;
            }
            if (IsCurrent(car)) {
                reason = "乗っているクルマは手放せません。先にガレージで乗り換えてください";
                return false;
            }
            if (Data.Owned.Count <= 1) {
                reason = "最後の 1 台は手放せません";
                return false;
            }
            Data.Owned.RemoveAll(x => string.Equals(x, car.Id, StringComparison.OrdinalIgnoreCase));
            Data.Pending.RemoveAll(x => string.Equals(x, car.Id, StringComparison.OrdinalIgnoreCase));
            Data.History.Add(new GtGarageEvent { Car = car.Id, Kind = "out", At = Now() });
            Touch();
            Logging.Write("[GtGarage] out: " + car.Id + " (owned " + Data.Owned.Count + ")");
            return true;
        }

        /// <summary>納車待ちを取り出して空にする（ワールドマップの「届きました」演出用）。</summary>
        public static List<CarObject> TakePending() {
            var list = new List<CarObject>();
            if (Data.Pending.Count == 0) return list;
            foreach (var id in Data.Pending.ToList()) {
                try {
                    var car = CarsManager.Instance.GetById(id);
                    if (car != null) list.Add(car);
                } catch { }
            }
            Data.Pending.Clear();
            Touch();
            return list;
        }

        /// <summary>テストチャンネル用の一行。</summary>
        public static string Dump() {
            try {
                return "garage=" + Data.Owned.Count + "/" + TotalCount + " owned=[" + string.Join(",", Data.Owned) + "] pending=[" + string.Join(",", Data.Pending) + "]";
            } catch (Exception e) {
                return "garage=(error " + e.Message + ")";
            }
        }
    }
}
