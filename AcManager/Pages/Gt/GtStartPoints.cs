// [fork] 2026-09-20 「レイアウト＝出発地点」型のコース（首都高 SRP）の見せ方。
//   要望「首都高はレイアウトが多すぎて混乱する。出発地点がただ違うだけのやつと、作った人が違う改造版が混ざっている。
//          どれを選んでも地図は全部読み込まれるので、出発地点を選ぶ、みたいな感じの方が分かりやすい。
//          Takeyoh さんのはちょっと違うから例外かも」
//   続けて「出発場所の名前だと、やっぱちょっと分かりにくい。首都高のでかい地図をバンと載っけて、それぞれの出発地点を添える感じがいい」
//   実測（ui_track.json）: SRP 純正 9 個は地図が同じで、出発する PA とピット数が違うだけ。
//     Takeyoh の 3 個は AI レーンが敷いてある別物（AI と走れる）。AI が走れる純正は箱崎PA EXT-1 だけ（C1 一周ぶん）。
//   → レイアウトの段を「出発地点」に言い換え、地図にピンを打って、左右に出発地点のカードを添える。レイアウトの中身・起動の仕方は変えない。
//   他のコースには何もしない（For が null を返す）。コースを足す時は Plans に 1 つ足すだけ。
//
//   地図とピンの作り方（別リポジトリのツール）:
//     srp_start_points.py   … 各レイアウトのピット 0 番（AC_PIT_0）を kn5 から読み、コースの map.png 上の位置 (U, V = 0..1) に直す
//     srp_make_startmap.py  … map.png から MapCrop の範囲を切り出して線を太くした絵を作る → Data\Static\gt7\maps\<MapFile>
//   ★MapCrop は srp_make_startmap.py の U0,U1,V0,V1 と同じ値にすること
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AcManager.Pages.Gt {
    internal static class GtStartPoints {
        internal sealed class Entry {
            public string LayoutId, Title, Sub;
            public int Group;
            public double U, V;   // コースの map.png 上の位置 (0..1)。ピンに使う
        }

        internal sealed class Plan {
            public string TrackIdPrefix, StepTitle, FootLabel, MapFile;
            public double MapU0, MapU1, MapV0, MapV1;   // MapFile が map.png のどの範囲を切り出した絵か
            public string[] GroupTitles, GroupNotes;
            public Entry[] Entries;

            /// <summary>表に無いレイアウト（コースの更新で増えた物）は null。呼び出し側は元の名前で最後の見出しに足す。</summary>
            public Entry Find(string layoutId) {
                return Entries.FirstOrDefault(x => string.Equals(x.LayoutId, layoutId ?? "", StringComparison.OrdinalIgnoreCase));
            }

            public int OrderOf(string layoutId) {
                var i = Array.FindIndex(Entries, x => string.Equals(x.LayoutId, layoutId ?? "", StringComparison.OrdinalIgnoreCase));
                return i < 0 ? int.MaxValue : i;
            }

            /// <summary>地図の絵のフルパス。置いてなければ null（＝地図なしの、見出しつきカードの並びで出す）。</summary>
            public string FindMap() {
                try {
                    if (string.IsNullOrEmpty(MapFile)) return null;
                    var f = Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "maps", MapFile);
                    return File.Exists(f) ? f : null;
                } catch {
                    return null;
                }
            }

            /// <summary>地図の絵の上での位置 (0..1)。範囲の外なら端に寄せる。</summary>
            public void ToMap(Entry e, out double x, out double y) {
                x = Math.Max(0.01, Math.Min(0.99, (e.U - MapU0) / (MapU1 - MapU0)));
                y = Math.Max(0.01, Math.Min(0.99, (e.V - MapV0) / (MapV1 - MapV0)));
            }
        }

        private static Entry E(int group, string id, string title, string sub, double u, double v) {
            return new Entry { Group = group, LayoutId = id, Title = title, Sub = sub, U = u, V = v };
        }

        private static readonly Plan[] Plans = {
            new Plan {
                // フォルダ名は版で変わる（shutoko_revival_project_094_ptb1 等）ので前方一致
                TrackIdPrefix = "shutoko_revival_project",
                StepTitle = "出発地点",
                FootLabel = "出発地点選択",
                MapFile = "shutoko_revival_project.png",
                MapU0 = 0, MapU1 = 1, MapV0 = 0, MapV1 = 1,   // 首都高の全体（横浜・大黒・アクアラインまで）
                GroupTitles = new[] { "出発するパーキングエリア", "AI と走る（Takeyoh カスタム）", "そのほか" },
                GroupNotes = new[] {
                    "地図はどれも同じ首都高の全線。出発する場所だけが違います",
                    "AI の走るレーンが敷いてあるレイアウト。ラップも計れます",
                    null,
                },
                // U, V は srp_start_points.py の出力（0.9.4 PTB1 で実測）
                Entries = new[] {
                    E(0, "tatsumi_pa", "辰巳PA", "9号深川線・湾岸線の近く", 0.69958, 0.21107),
                    E(0, "hakozaki_pa", "箱崎PA", "6号向島線・C1 の近く", 0.62867, 0.05047),
                    E(0, "shibaura_pa", "芝浦PA", "1号羽田線・レインボーブリッジの近く", 0.52220, 0.21140),
                    E(0, "yoyogi_pa", "代々木PA", "4号新宿線", 0.32045, 0.05465),
                    E(0, "heiwajima_pa_n", "平和島PA（上り）", "1号羽田線・都心方面", 0.47303, 0.43378),
                    E(0, "heiwajima_pa_s", "平和島PA（下り）", "1号羽田線・横浜方面", 0.47639, 0.43782),
                    E(0, "daishi_pa", "大師PA", "神奈川1号横羽線", 0.47007, 0.61114),
                    E(1, "battlelane_takeyoh_custom_c1outer", "C1 外回り", "C1 の北側から発進・AI 16台まで", 0.59031, 0.04894),
                    E(1, "battlelane_takeyoh_custom_c1inner", "C1 内回り", "C1 の南側から発進・AI 16台まで", 0.50466, 0.16902),
                    E(1, "battlelane_takeyoh_custom", "バトルレーン 全線", "辰巳PA 発・AI 30台まで", 0.69958, 0.21107),
                    E(2, "hakozaki_pa_ext_1", "箱崎PA EXT-1", "箱崎PA 発・ピット 64台（C1 一周の AI ライン入り）", 0.62867, 0.05047),
                    E(2, "main_layout", "メインレイアウト", "辰巳PA 発・オンライン用・ピット 170台", 0.69958, 0.21108),
                },
            },
        };

        /// <summary>このコースが「出発地点を選ぶ」型なら見せ方の表、そうでなければ null（＝今までどおりのレイアウト選択）。
        /// まず Data\Static\gt7\startpoints\*.json を探す（要望 2026-09-21「そのうち大黒あたりのスタート地点も作りたい」＝
        /// レイアウトを足すたびにビルドし直さなくて済むように、表はデータで持つ。画面を開くたびに読むので CM7 の再起動も不要）。
        /// 無い・壊れている時だけ、上の作り付けの表。</summary>
        internal static Plan For(string trackId) {
            if (string.IsNullOrEmpty(trackId)) return null;
            try {
                var dir = Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "startpoints");
                if (Directory.Exists(dir)) {
                    foreach (var file in Directory.GetFiles(dir, "*.json")) {
                        var plan = Load(file);
                        if (plan != null && trackId.StartsWith(plan.TrackIdPrefix, StringComparison.OrdinalIgnoreCase)) return plan;
                    }
                }
            } catch (Exception e) {
                FirstFloor.ModernUI.Helpers.Logging.Warning("[GtStartPoints] " + e.Message);
            }
            return Plans.FirstOrDefault(p => trackId.StartsWith(p.TrackIdPrefix, StringComparison.OrdinalIgnoreCase));
        }

        private static Plan Load(string file) {
            try {
                var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                var prefix = (string)o["TrackIdPrefix"];
                var groups = o["Groups"] as Newtonsoft.Json.Linq.JArray;
                var entries = o["Entries"] as Newtonsoft.Json.Linq.JArray;
                if (string.IsNullOrEmpty(prefix) || groups == null || groups.Count == 0 || entries == null) return null;
                var crop = (o["MapCrop"] as Newtonsoft.Json.Linq.JArray)?.Select(x => (double)x).ToArray();
                if (crop == null || crop.Length != 4 || crop[1] <= crop[0] || crop[3] <= crop[2]) crop = new[] { 0d, 1d, 0d, 1d };
                return new Plan {
                    TrackIdPrefix = prefix,
                    StepTitle = (string)o["StepTitle"] ?? "出発地点",
                    FootLabel = (string)o["FootLabel"] ?? "出発地点選択",
                    MapFile = (string)o["MapFile"],
                    MapU0 = crop[0], MapU1 = crop[1], MapV0 = crop[2], MapV1 = crop[3],
                    GroupTitles = groups.Select(x => (string)x["Title"] ?? "").ToArray(),
                    GroupNotes = groups.Select(x => (string)x["Note"]).ToArray(),
                    Entries = entries.Where(x => !string.IsNullOrEmpty((string)x["LayoutId"])).Select(x => new Entry {
                        Group = Math.Max(0, Math.Min(groups.Count - 1, (int?)x["Group"] ?? 0)),
                        LayoutId = (string)x["LayoutId"],
                        Title = (string)x["Title"] ?? (string)x["LayoutId"],
                        Sub = (string)x["Sub"] ?? "",
                        U = (double?)x["U"] ?? 0.5, V = (double?)x["V"] ?? 0.5,
                    }).ToArray(),
                };
            } catch (Exception e) {
                FirstFloor.ModernUI.Helpers.Logging.Warning("[GtStartPoints] " + Path.GetFileName(file) + ": " + e.Message);
                return null;
            }
        }
    }
}
