// [fork] GT7風チューニングショップのデータ層。
//   ・パーツカタログ（GT7 の5カテゴリ: スポーツ/クラブスポーツ/セミレーシング/レーシング/エクストリーム）
//   ・車ごとの装着状態 = Data\Static\gt7\tuning\<carId>.json（購入/クレジットの概念は出さない: 要望 2026-09-05）
//   ・PP/スペックの「チューン後」計算（ヘッダー・ガレージ・セッティングシートが参照）
//   ・コースイン時の実車反映 = data.acd を退避して data\ フォルダにチューン済みデータを展開（AC は data.acd 優先なので退避が要る）
//       出力系   → power.lut のトルクを倍率で伸ばす / ターボ・SC は engine.ini の [TURBO_n]（NA 車には新設）
//       軽量化   → car.ini [BASIC] TOTALMASS
//       ブレーキ → brakes.ini [DATA] MAX_TORQUE
//       クラッチ → engine.ini [ENGINE_DATA] INERTIA（フライホイール軽量化＝吹け上がり）
//       LSD      → drivetrain.ini [DIFFERENTIAL] POWER/COAST
//       ミッション → drivetrain.ini [GEARS] FINAL（ロー/ハイクロス）
//       タイヤ   → tyres.ini [COMPOUND_DEFAULT] INDEX（コースインの既定コンパウンド）
//       リストリクター/バラスト → race.ini（CM の PlayerRestrictor/PlayerBallast＝AC 純正機能）
//     走行後 Restore() で元に戻す。CM 起動時にも RestoreAll() で取り残しを掃除する（クラッシュ対策）。
//   ⚠data.acd 自体は書き換えない（退避→復元のみ）。Steam 整合性チェックでも壊れない。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using AcTools.AcdFile;
using AcTools.DataFile;
using FirstFloor.ModernUI.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcManager.Pages.Gt {
    internal sealed class GtPart {
        public string Id, Name, Desc, Cat, Group, Glyph;
        public double Power;        // 出力倍率の増分（0.04 = +4%）
        public double Mass;         // 車重の増分（-0.04 = -4%）
        public double Brake = 1;    // ブレーキ最大トルク倍率
        public double Inertia = 1;  // エンジン慣性倍率（フライホイール軽量化）
        public double Final = 1;    // 最終減速比倍率
        public double Boost;        // 過給圧（NA 車は新設、ターボ車は既存に加算）
        public double RefRpm;       // ターボ新設時の REFERENCE_RPM
        public double LagUp = -1;   // 過給機の立ち上がり（LAG_UP）。SC はほぼラグ無し
        public double AntiLag;      // アンチラグ: 既存 LAG_UP から引く量
        public double DiffPower = -1, DiffCoast = -1;   // LSD（%）
        public bool TurboOnly;      // ターボ車（または過給機装着車）専用
        public bool AwdOnly;
        public int TyreIndex = -1;  // タイヤ（車ごとに生成）
        public string TyreShort;
        public bool IsValue;        // リストリクター/バラスト（数値をセッティングシートで決める）

        public bool HasPhysics => Power != 0 || Mass != 0 || Brake != 1 || Inertia != 1 || Final != 1 || Boost != 0 || AntiLag != 0 || DiffPower >= 0 || TyreIndex >= 0;
        public bool IsForced => Boost != 0;
    }

    internal sealed class GtTuningState {
        public List<string> Installed = new List<string>();
        public int Restrictor;   // %（0 = 無制限）
        public int Ballast;      // kg
    }

    internal sealed class GtTunedSpec {
        public double BhpStock, Bhp, KgStock, Kg, NmStock, Nm, PpStock, Pp;
        public bool Any;
        public bool Changed => Math.Abs(Bhp - BhpStock) > 0.01 || Math.Abs(Kg - KgStock) > 0.01;
    }

    internal static class GtTuning {
        public static readonly string[] Cats = { "スポーツ", "クラブスポーツ", "セミレーシング", "レーシング", "エクストリーム" };

        // 排他グループ: 同じグループのパーツは一つしか付かない（GT7 と同じ。マフラーを替えれば前のは外れる）
        private static GtPart P(string id, string cat, string name, string desc, string group = null, string glyph = "🔧") {
            return new GtPart { Id = id, Cat = cat, Name = name, Desc = desc, Group = group, Glyph = glyph };
        }

        public static readonly List<GtPart> Catalog = new List<GtPart> {
            // ── スポーツ ──
            new GtPart { Id = "sport_ecu", Cat = Cats[0], Group = "ecu", Name = "スポーツコンピュータ", Desc = "燃料噴射と点火時期を見直した ECU。手軽に出力を上げる定番の一手。", Power = 0.03, Glyph = "💾" },
            new GtPart { Id = "sport_intake", Cat = Cats[0], Group = "intake", Name = "スポーツ・エアクリーナ", Desc = "吸気系チューニングの代表。吸気抵抗を減らして出力を稼ぐ。", Power = 0.02, Glyph = "🌀" },
            new GtPart { Id = "sport_muffler", Cat = Cats[0], Group = "exhaust", Name = "スポーツ・マフラー", Desc = "排気効率を高めたマフラー。音も少し野太くなる。", Power = 0.04, Glyph = "🔩" },
            new GtPart { Id = "sport_pad", Cat = Cats[0], Group = "pad", Name = "スポーツ・ブレーキパッド", Desc = "摩擦材を強化したパッド。制動力と耐フェード性が上がる。", Brake = 1.05, Glyph = "🛑" },
            new GtPart { Id = "street_susp", Cat = Cats[0], Group = "susp", Name = "ストリート・サスペンション", Desc = "街乗りより少し引き締めた足まわり。減衰と車高は固定。", Glyph = "🔧" },
            new GtPart { Id = "weight1", Cat = Cats[0], Group = "weight", Name = "軽量化ステージ1", Desc = "内装の一部を取り外す軽量化。最初の一歩。", Mass = -0.04, Glyph = "🪶" },
            // ── クラブスポーツ ──
            new GtPart { Id = "bore", Cat = Cats[1], Name = "ボアアップ", Desc = "シリンダー内径を拡大して排気量を増やす。全域でトルクが太る。", Power = 0.06, Glyph = "⚙" },
            new GtPart { Id = "cam", Cat = Cats[1], Name = "ハイリフトカムシャフト", Desc = "バルブリフト量を増やしたカム。高回転で伸びる。", Power = 0.05, Glyph = "⚙" },
            new GtPart { Id = "piston", Cat = Cats[1], Name = "ハイコンプピストン", Desc = "圧縮比を高めたピストン。燃焼効率が上がる。", Power = 0.04, Glyph = "⚙" },
            new GtPart { Id = "sport_susp", Cat = Cats[1], Group = "susp", Name = "スポーツ・サスペンション", Desc = "スポーツ走行向けの固めの足まわり。車高は固定。", Glyph = "🔧" },
            new GtPart { Id = "sport_brake", Cat = Cats[1], Group = "brake", Name = "スポーツ・ブレーキシステム", Desc = "大径ローターと対向キャリパー。制動力を底上げする。", Brake = 1.10, Glyph = "🛑" },
            new GtPart { Id = "sport_clutch", Cat = Cats[1], Group = "clutch", Name = "スポーツ・クラッチ＆フライホイール", Desc = "軽量フライホイールでエンジンの吹け上がりが速くなる。", Inertia = 0.85, Glyph = "⭕" },
            new GtPart { Id = "lsd_1way", Cat = Cats[1], Group = "lsd", Name = "１ウェイLSD", Desc = "加速側だけ効く LSD。立ち上がりのトラクションを稼ぐ。", DiffPower = 30, DiffCoast = 0, Glyph = "🔩" },
            new GtPart { Id = "lsd_2way", Cat = Cats[1], Group = "lsd", Name = "２ウェイLSD", Desc = "加速・減速の両方で効く LSD。安定志向。", DiffPower = 30, DiffCoast = 30, Glyph = "🔩" },
            new GtPart { Id = "trans_low", Cat = Cats[1], Group = "trans", Name = "ロークロス・ミッション", Desc = "ギア比を低めに寄せたミッション。加速重視。", Final = 1.08, Glyph = "🔩" },
            new GtPart { Id = "trans_high", Cat = Cats[1], Group = "trans", Name = "ハイクロス・ミッション", Desc = "ギア比を高めに寄せたミッション。最高速重視。", Final = 0.93, Glyph = "🔩" },
            new GtPart { Id = "weight2", Cat = Cats[1], Group = "weight", Name = "軽量化ステージ2", Desc = "内装をさらに省き、軽量パネルに置き換える。", Mass = -0.08, Glyph = "🪶" },
            new GtPart { Id = "restrictor", Cat = Cats[1], Name = "パワーリストリクター", Desc = "あらゆる車種に対応する吸気制限装置。出力を絞って PP を合わせる。値はカーセッティングで決める。", IsValue = true, Glyph = "🔌" },
            new GtPart { Id = "ballast", Cat = Cats[1], Name = "バラスト", Desc = "車体に積む重り。重量を増やして PP を合わせる。値はカーセッティングで決める。", IsValue = true, Glyph = "⬛" },
            // ── セミレーシング ──
            new GtPart { Id = "crank", Cat = Cats[2], Name = "レーシングクランクシャフト", Desc = "軽量・高強度のクランク。高回転域のロスを減らす。", Power = 0.05, Glyph = "⚙" },
            new GtPart { Id = "full_ecu", Cat = Cats[2], Group = "ecu", Name = "フルカスタムコンピュータ", Desc = "書き換え自由の ECU。エンジンの実力を引き出す。", Power = 0.06, Glyph = "💾" },
            new GtPart { Id = "semi_muffler", Cat = Cats[2], Group = "exhaust", Name = "セミレーシング・マフラー", Desc = "消音を最小限にした排気系。", Power = 0.08, Glyph = "🔩" },
            new GtPart { Id = "turbo_low", Cat = Cats[2], Group = "forced", Name = "ターボチャージャー・低回転型", Desc = "小型タービン。低回転から過給が立ち上がる。", Boost = 0.20, RefRpm = 2500, LagUp = 0.992, Glyph = "🌀" },
            new GtPart { Id = "turbo_mid", Cat = Cats[2], Group = "forced", Name = "ターボチャージャー・中回転型", Desc = "中型タービン。中回転から力強く過給する。", Boost = 0.30, RefRpm = 4000, LagUp = 0.994, Glyph = "🌀" },
            new GtPart { Id = "turbo_high", Cat = Cats[2], Group = "forced", Name = "ターボチャージャー・高回転型", Desc = "大型タービン。高回転で爆発的な出力。ラグは大きい。", Boost = 0.45, RefRpm = 5500, LagUp = 0.996, Glyph = "🌀" },
            new GtPart { Id = "sc_low", Cat = Cats[2], Group = "forced", Name = "スーパーチャージャー・低回転型", Desc = "機械式過給機。ラグ無しで低回転からトルクが出る。", Boost = 0.25, RefRpm = 1500, LagUp = 0.90, Glyph = "🌀" },
            new GtPart { Id = "ic_sport", Cat = Cats[2], Group = "ic", Name = "スポーツインタークーラー", Desc = "過給気を冷やして密度を上げる。過給機付きの車専用。", Power = 0.03, TurboOnly = true, Glyph = "🧊" },
            new GtPart { Id = "susp_height", Cat = Cats[2], Group = "susp", Name = "車高調整式スポーツ・サスペンション", Desc = "車高を調整できるスポーツサス。", Glyph = "🔧" },
            new GtPart { Id = "semi_clutch", Cat = Cats[2], Group = "clutch", Name = "セミレーシング・クラッチ＆フライホイール", Desc = "さらに軽いフライホイール。レスポンス重視。", Inertia = 0.75, Glyph = "⭕" },
            new GtPart { Id = "lsd_full", Cat = Cats[2], Group = "lsd", Name = "フルカスタマイズLSD", Desc = "イニシャル・加速・減速を自由に調整できる LSD。", DiffPower = 45, DiffCoast = 25, Glyph = "🔩" },
            new GtPart { Id = "trans_full", Cat = Cats[2], Group = "trans", Name = "フルカスタマイズ・マニュアル・ミッション", Desc = "各ギア比と最終減速比を自由に設定できる。", Glyph = "🔩" },
            new GtPart { Id = "weight3", Cat = Cats[2], Group = "weight", Name = "軽量化ステージ3", Desc = "ガラスや外装パネルも軽量素材に。", Mass = -0.12, Glyph = "🪶" },
            new GtPart { Id = "rigidity", Cat = Cats[2], Name = "ボディ剛性アップ", Desc = "スポット増しと補強バー。足まわりの動きが正確になる。", Glyph = "🏗" },
            // ── レーシング ──
            new GtPart { Id = "stroke", Cat = Cats[3], Name = "ストロークアップ", Desc = "クランクのストロークを伸ばして排気量アップ。", Power = 0.07, Glyph = "⚙" },
            new GtPart { Id = "balance", Cat = Cats[3], Name = "エンジンバランスチューン", Desc = "回転部品の重量を揃える。高回転が滑らかに。", Power = 0.03, Glyph = "⚙" },
            new GtPart { Id = "port", Cat = Cats[3], Name = "ポート研磨", Desc = "吸排気ポートを磨いて流れを良くする。", Power = 0.03, Glyph = "⚙" },
            new GtPart { Id = "antilag", Cat = Cats[3], Name = "アンチラグシステム", Desc = "排気側で燃焼させてタービンを回し続ける。ターボラグを消す。", AntiLag = 0.008, TurboOnly = true, Glyph = "🔥" },
            new GtPart { Id = "sc_high", Cat = Cats[3], Group = "forced", Name = "スーパーチャージャー・高回転型", Desc = "大容量の機械式過給機。高回転まで過給が続く。", Boost = 0.40, RefRpm = 3000, LagUp = 0.90, Glyph = "🌀" },
            new GtPart { Id = "ic_racing", Cat = Cats[3], Group = "ic", Name = "レーシングインタークーラー", Desc = "大容量インタークーラー。過給機付きの車専用。", Power = 0.06, TurboOnly = true, Glyph = "🧊" },
            new GtPart { Id = "racing_intake", Cat = Cats[3], Group = "intake", Name = "レーシング・エアクリーナ", Desc = "吸気抵抗を極限まで減らしたエアクリーナ。", Power = 0.04, Glyph = "🌀" },
            new GtPart { Id = "racing_muffler", Cat = Cats[3], Group = "exhaust", Name = "レーシング・マフラー", Desc = "消音器無しに近い直管。最大の排気効率。", Power = 0.12, Glyph = "🔩" },
            new GtPart { Id = "manifold", Cat = Cats[3], Name = "レーシング・エキゾーストマニホールド", Desc = "等長エキマニ。排気干渉を減らす。", Power = 0.05, Glyph = "🔩" },
            new GtPart { Id = "racing_pad", Cat = Cats[3], Group = "pad", Name = "レーシング・ブレーキパッド", Desc = "レース用摩擦材。高温でも制動力が落ちない。", Brake = 1.10, Glyph = "🛑" },
            new GtPart { Id = "racing_brake", Cat = Cats[3], Group = "brake", Name = "レーシング・ブレーキシステム", Desc = "スリットディスクとモノブロックキャリパー。", Brake = 1.15, Glyph = "🛑" },
            new GtPart { Id = "brake_ctrl", Cat = Cats[3], Name = "ブレーキバランスコントローラ", Desc = "前後の制動配分を調整できる。", Glyph = "🎚" },
            new GtPart { Id = "full_susp", Cat = Cats[3], Group = "susp", Name = "フルカスタマイズ・サスペンション", Desc = "車高・バネ・減衰・アライメントを全て調整できる。", Glyph = "🔧" },
            new GtPart { Id = "racing_clutch", Cat = Cats[3], Group = "clutch", Name = "レーシング・クラッチ＆フライホイール", Desc = "超軽量フライホイール。エンジンが一瞬で吹ける。", Inertia = 0.65, Glyph = "⭕" },
            new GtPart { Id = "center_diff", Cat = Cats[3], Name = "駆動力配分式センターデフ", Desc = "前後の駆動配分を調整できる。4WD 専用。", AwdOnly = true, Glyph = "🔩" },
            new GtPart { Id = "active_lsd", Cat = Cats[3], Group = "lsd", Name = "アクティブLSDコントローラ", Desc = "走行状況に応じて効きを変える電子制御 LSD。", DiffPower = 50, DiffCoast = 35, Glyph = "🔩" },
            new GtPart { Id = "seq_trans", Cat = Cats[3], Group = "trans", Name = "フルカスタマイズ・シーケンシャル・ミッション", Desc = "シーケンシャル化した多段ミッション。ギア比は自由。", Glyph = "🔩" },
            new GtPart { Id = "weight4", Cat = Cats[3], Group = "weight", Name = "軽量化ステージ4", Desc = "カーボン外装。レースカー並みの軽さに。", Mass = -0.16, Glyph = "🪶" },
            // ── エクストリーム ──
            new GtPart { Id = "turbo_ultra", Cat = Cats[4], Group = "forced", Name = "ターボチャージャー・超高回転型", Desc = "巨大タービン。最高回転域だけで暴力的な出力。", Boost = 0.60, RefRpm = 6500, LagUp = 0.997, Glyph = "🌀" },
            new GtPart { Id = "weight5", Cat = Cats[4], Group = "weight", Name = "軽量化ステージ5", Desc = "骨格まで手を入れた究極の軽量化。", Mass = -0.20, Glyph = "🪶" },
            new GtPart { Id = "carbon_brake", Cat = Cats[4], Group = "brake", Name = "カーボンセラミック・ブレーキシステム", Desc = "カーボンセラミックディスク。最強の制動力。", Brake = 1.25, Glyph = "🛑" },
        };

        // ── タイヤ（車の tyres.ini から生成） ─────────────────────────────
        /// <summary>AC のコンパウンド名 → GT7 のタイヤ名とカテゴリ。</summary>
        private static bool MapTyre(string name, string shortName, out string cat, out string label) {
            var n = (name ?? "").ToLowerInvariant();
            var s = (shortName ?? "").ToUpperInvariant();
            cat = Cats[0]; label = null;
            if (n.Contains("eco") || s == "E") { label = "コンフォートタイヤ・ハード"; return true; }
            if (n.Contains("vintage") || s == "V") { label = "コンフォートタイヤ・ミディアム"; return true; }
            if (n.Contains("street") || s == "ST") { label = "スポーツタイヤ・ハード"; return true; }
            if (n.Contains("hypercar road") || s == "HR") { label = "スポーツタイヤ・ミディアム"; return true; }
            if (n.Contains("semislick") || n.Contains("semi slick") || s == "SM" && n.Contains("semi") || s == "HT") { label = "スポーツタイヤ・ソフト"; return true; }
            if (n.Contains("intermediate") || s == "I") { cat = Cats[4]; label = "インターミディエイトタイヤ"; return true; }
            if (n.Contains("wet") || s == "W") { cat = Cats[4]; label = "ヘビーウェットタイヤ"; return true; }
            // スリック（"Trofeo H Slicks" のような名前も含む）: 硬さは名前の単語か SHORT_NAME で判定
            var words = n.Replace("(", " ").Replace(")", " ").Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            Func<string, bool> word = w => Array.IndexOf(words, w) >= 0;
            var racing = n.Contains("slick") || s == "SH" || s == "SM" || s == "SS" || s == "SSS" || s == "H" || s == "M" || s == "S";
            if (racing) {
                cat = Cats[3];
                if (n.Contains("supersoft") || s == "SSS" || word("ss")) label = "レーシングタイヤ・ソフト";
                else if (n.Contains("soft") || s == "SS" || s == "S" || word("s")) label = "レーシングタイヤ・ソフト";
                else if (n.Contains("hard") || s == "SH" || s == "H" || word("h")) label = "レーシングタイヤ・ハード";
                else label = "レーシングタイヤ・ミディアム";
                return true;
            }
            if (n.Contains("trofeo")) { label = "スポーツタイヤ・ソフト"; return true; }
            label = name;
            return !string.IsNullOrEmpty(name);
        }

        private static readonly Dictionary<string, List<GtPart>> TyreCache = new Dictionary<string, List<GtPart>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>車の tyres.ini にあるコンパウンドをタイヤパーツとして返す（FRONT, FRONT_1, … の順＝AC の index）。</summary>
        public static List<GtPart> TyreParts(CarObject car) {
            if (car == null) return new List<GtPart>();
            List<GtPart> cached;
            if (TyreCache.TryGetValue(car.Id, out cached)) return cached;
            var list = new List<GtPart>();
            try {
                var ini = car.AcdData?.GetIniFile("tyres.ini");
                if (ini != null) {
                    var seen = new Dictionary<string, int>();
                    for (var i = 0; i < 12; i++) {
                        var key = i == 0 ? "FRONT" : "FRONT_" + i;
                        if (!ini.ContainsKey(key)) break;
                        var sec = ini[key];
                        var name = sec.GetPossiblyEmpty("NAME");
                        var sh = sec.GetPossiblyEmpty("SHORT_NAME");
                        string cat, label;
                        if (!MapTyre(name, sh, out cat, out label)) continue;
                        int dup;
                        if (seen.TryGetValue(label, out dup)) {
                            seen[label] = dup + 1;
                            label = label + " (" + (string.IsNullOrEmpty(sh) ? name : sh) + ")";
                        } else {
                            seen[label] = 1;
                        }
                        list.Add(new GtPart {
                            Id = "tyre_" + i, Cat = cat, Group = "tyre", Name = label,
                            Desc = "AC コンパウンド: " + name + (string.IsNullOrEmpty(sh) ? "" : " (" + sh + ")") + "。コースインの既定タイヤになる。",
                            TyreIndex = i, TyreShort = sh, Glyph = "🛞"
                        });
                    }
                }
            } catch (Exception e) {
                Logging.Warning("[GtTuning] tyres: " + e.Message);
            }
            TyreCache[car.Id] = list;
            return list;
        }

        /// <summary>この車に出すパーツ一覧（カテゴリ順・タイヤ込み・4WD 専用などは車で絞る）。</summary>
        public static List<GtPart> PartsFor(CarObject car, string cat) {
            var awd = IsAwd(car);
            var list = new List<GtPart>();
            foreach (var p in Catalog) {
                if (p.Cat != cat) continue;
                if (p.AwdOnly && !awd) continue;
                list.Add(p);
            }
            // タイヤは GT7 と同じくカテゴリの末尾（スポーツ: コンフォート→スポーツ、レーシング: レーシング）
            foreach (var t in TyreParts(car)) if (t.Cat == cat) list.Add(t);
            return list;
        }

        public static GtPart Find(CarObject car, string id) {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith("tyre_", StringComparison.Ordinal)) return TyreParts(car).FirstOrDefault(x => x.Id == id);
            return Catalog.FirstOrDefault(x => x.Id == id);
        }

        public static bool IsAwd(CarObject car) {
            try {
                var t = car?.AcdData?.GetIniFile("drivetrain.ini")["TRACTION"].GetPossiblyEmpty("TYPE")?.ToUpperInvariant();
                return t == "AWD" || t == "4WD";
            } catch { return false; }
        }

        public static bool HasTurbo(CarObject car) {
            try {
                var ini = car?.AcdData?.GetIniFile("engine.ini");
                return ini != null && ini.ContainsKey("TURBO_0") && ini["TURBO_0"].GetDouble("MAX_BOOST", 0) > 0;
            } catch { return false; }
        }

        // ── 装着状態 ─────────────────────────────────────────────
        private static readonly Dictionary<string, GtTuningState> Cache = new Dictionary<string, GtTuningState>(StringComparer.OrdinalIgnoreCase);

        public static string Dir => Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "tuning");

        private static string StateFile(string carId) {
            return Path.Combine(Dir, carId + ".json");
        }

        private static GtTuningState CopyState(GtTuningState s) {
            return new GtTuningState { Installed = new List<string>(s.Installed ?? new List<string>()), Restrictor = s.Restrictor, Ballast = s.Ballast };
        }

        private static bool TryGetState(CarObject car, out GtTuningState s) {
            if (Cache.TryGetValue(car.Id, out s)) return true;
            try {
                var f = StateFile(car.Id);
                s = File.Exists(f) ? JsonConvert.DeserializeObject<GtTuningState>(File.ReadAllText(f)) : new GtTuningState();
                if (s == null) throw new InvalidDataException("Empty tuning state: " + f);
                if (s.Installed == null) s.Installed = new List<string>();
                Cache[car.Id] = s;
                return true;
            } catch (Exception e) {
                Logging.Warning("[GtTuning] load: " + e.Message);
                return false; // 読めない既存ファイルを空の装着状態で上書きしない。
            }
        }

        /// <summary>編集用のコピー。保存が成功するまで共有キャッシュは変わらない。</summary>
        public static GtTuningState Get(CarObject car) {
            GtTuningState s;
            return car != null && TryGetState(car, out s) ? CopyState(s) : new GtTuningState();
        }

        public static bool Save(CarObject car, GtTuningState s) {
            if (car == null || s == null) return false;
            try {
                GtTuningState existing;
                if (!TryGetState(car, out existing)) return false;
                var saved = CopyState(s);
                WriteAtomic(StateFile(car.Id), JsonConvert.SerializeObject(saved, Formatting.Indented));
                Cache[car.Id] = saved;
            } catch (Exception e) {
                Logging.Warning("[GtTuning] save: " + e.Message);
                return false;
            }
            try { Changed?.Invoke(null, EventArgs.Empty); } catch (Exception e) { Logging.Warning("[GtTuning] changed: " + e.Message); }
            return true;
        }

        /// <summary>装着状態が変わった（ヘッダーの PP ピル等が拾う）。</summary>
        public static event EventHandler Changed;

        public static bool IsInstalled(CarObject car, string id) {
            return Get(car).Installed.Contains(id);
        }

        /// <summary>取り付け。同じグループのパーツは外れる。過給機専用パーツは過給機が無ければ付かない。</summary>
        public static bool Install(CarObject car, GtPart part) {
            if (car == null || part == null) return false;
            GtTuningState current;
            if (!TryGetState(car, out current)) return false;
            if (!Applicable(car, part)) return false;
            var s = CopyState(current);
            if (!string.IsNullOrEmpty(part.Group)) {
                s.Installed.RemoveAll(id => { var p = Find(car, id); return p != null && p.Group == part.Group; });
            }
            if (!s.Installed.Contains(part.Id)) s.Installed.Add(part.Id);
            return Save(car, s);
        }

        public static bool Uninstall(CarObject car, GtPart part) {
            if (car == null || part == null) return false;
            GtTuningState current;
            if (!TryGetState(car, out current)) return false;
            var s = CopyState(current);
            if (s.Installed.Remove(part.Id)) {
                // 過給機を外したらインタークーラー/アンチラグも外す（ターボ車以外）
                if (part.IsForced && !HasTurbo(car)) s.Installed.RemoveAll(id => { var p = Find(car, id); return p != null && p.TurboOnly; });
                if (part.Id == "restrictor") s.Restrictor = 0;
                if (part.Id == "ballast") s.Ballast = 0;
                return Save(car, s);
            }
            return true;
        }

        /// <summary>この車に付けられるか（過給機専用・4WD 専用）。</summary>
        public static bool Applicable(CarObject car, GtPart part) {
            if (part.AwdOnly && !IsAwd(car)) return false;
            if (part.TurboOnly && !HasTurbo(car) && !Get(car).Installed.Any(id => Find(car, id)?.IsForced == true)) return false;
            return true;
        }

        // ── チューン後スペック ─────────────────────────────────────
        private static double Num1(string s) {
            if (string.IsNullOrEmpty(s)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(s.Replace(",", ""), @"(\d+(\.\d+)?)");
            double v;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static double PpOf(double bhp, double kg) {
            if (bhp <= 0 || kg < 100) return 0;
            var pp = 410.0 + 212.0 * Math.Log(bhp / (kg / 1000.0) / 143.0);
            return pp < 1 ? 1 : pp > 1500 ? 1500 : pp;
        }

        /// <summary>装着パーツ（installed が null なら現在の状態）から出力/重量/PP を出す。</summary>
        public static GtTunedSpec Compute(CarObject car, IEnumerable<string> installed = null, int? restrictor = null, int? ballast = null) {
            var r = new GtTunedSpec();
            if (car == null) return r;
            var s = Get(car);
            var ids = (installed ?? s.Installed).ToList();
            var rest = restrictor ?? s.Restrictor;
            var bal = ballast ?? s.Ballast;
            r.BhpStock = Num1(car.SpecsBhp);
            r.KgStock = Num1(car.SpecsWeight);
            r.NmStock = Num1(car.SpecsTorque);
            var power = 1.0;
            var mass = 1.0;
            var boost = 0.0;
            var turbo = HasTurbo(car);
            foreach (var id in ids) {
                var p = Find(car, id);
                if (p == null) continue;
                if (p.TurboOnly && !turbo && !ids.Any(x => Find(car, x)?.IsForced == true)) continue;
                power *= 1 + p.Power;
                mass *= 1 + p.Mass;
                boost += p.Boost;
            }
            // ターボ車の既存過給は SpecsBhp に織り込み済み。追加分だけ効かせる（既存 MAX_BOOST に対する比率）
            if (boost > 0) {
                var baseBoost = turbo ? ExistingBoost(car) : 0;
                power *= (1 + baseBoost + boost) / (1 + baseBoost);
            }
            if (ids.Contains("restrictor")) power *= 1 - Math.Max(0, Math.Min(90, rest)) / 100.0;
            r.Bhp = r.BhpStock * power;
            r.Nm = r.NmStock * power;
            r.Kg = r.KgStock * mass + (ids.Contains("ballast") ? bal : 0);
            r.PpStock = PpOf(r.BhpStock, r.KgStock);
            r.Pp = PpOf(r.Bhp, r.Kg);
            r.Any = ids.Count > 0;
            return r;
        }

        private static double ExistingBoost(CarObject car) {
            try {
                var ini = car.AcdData?.GetIniFile("engine.ini");
                if (ini == null) return 0;
                var sum = 0.0;
                for (var i = 0; i < 4; i++) {
                    var key = "TURBO_" + i;
                    if (!ini.ContainsKey(key)) break;
                    var s = ini[key];
                    var wg = s.GetDouble("WASTEGATE", 0);
                    var mb = s.GetDouble("MAX_BOOST", 0);
                    sum += wg > 0 ? Math.Min(wg, mb) : mb;
                }
                return sum;
            } catch { return 0; }
        }

        /// <summary>PP（チューン後）。GarageGt / GtHeader / SettingSheet が使う。</summary>
        public static double Pp(object car) {
            var c = car as CarObject;
            if (c == null) return 0;
            return Compute(c).Pp;
        }

        /// <summary>チューン後の諸元を「元の単位のまま」の文字列で返す（"571 bhp" → "600 bhp"）。
        /// DLL 側（GarageGt）がリフレクションで呼び、既存の PS/kgfm 換算にそのまま流せる。</summary>
        public static string SpecsBhp(object car) { return Scaled((car as CarObject)?.SpecsBhp, c => Compute(c).Bhp, car as CarObject); }
        public static string SpecsWeight(object car) { return Scaled((car as CarObject)?.SpecsWeight, c => Compute(c).Kg, car as CarObject); }
        public static string SpecsTorque(object car) { return Scaled((car as CarObject)?.SpecsTorque, c => Compute(c).Nm, car as CarObject); }

        private static string Scaled(string raw, Func<CarObject, double> tuned, CarObject car) {
            if (string.IsNullOrEmpty(raw) || car == null) return raw;
            var st = Get(car);
            if (st.Installed.Count == 0) return raw;
            var m = System.Text.RegularExpressions.Regex.Match(raw.Replace(",", ""), @"(\d+(\.\d+)?)\s*(.*)$");
            if (!m.Success) return raw;
            var v = tuned(car);
            if (v <= 0) return raw;
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + (m.Groups[3].Value.Length > 0 ? " " + m.Groups[3].Value.Trim() : "");
        }

        // ── コースイン時の実車反映 ──────────────────────────────────
        private const string Marker = "gt7_tuning.txt";
        private static string RegistryFile => Path.Combine(Dir, "_applied.json");

        // 同じディレクトリの一時ファイルを確定する。既存ファイルを切り詰めない。
        internal static void WriteAtomic(string filename, string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(filename));
            var temp = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)) {
                        writer.Write(text);
                        writer.Flush();
                    }
                    stream.Flush(true);
                }
                if (File.Exists(filename)) File.Replace(temp, filename, null);
                else File.Move(temp, filename);
            } finally {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static List<string> ReadRegistry(string filename) {
            if (!File.Exists(filename)) return new List<string>();
            var ids = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(filename));
            if (ids == null || ids.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid tuning recovery registry: " + filename);
            return ids;
        }

        private static void WriteRegistry(string filename, List<string> ids) {
            // 空も [] として原子的に保存する。破損した台帳を空扱いしない。
            WriteAtomic(filename, JsonConvert.SerializeObject(ids));
        }

        /// <summary>実車反映に成功した時だけ true。途中失敗時はコースインを止める。</summary>
        public static bool Prepare(CarObject car) {
            if (car == null) return false;
            try {
                // パーツをすべて外した場合も、前回の退避を先に戻す。
                RestoreDirectory(car.Location, RegistryFile);
                GtTuningState s;
                if (!TryGetState(car, out s)) return false;
                var parts = s.Installed.Select(id => Find(car, id)).Where(p => p != null && p.HasPhysics).ToList();
                if (parts.Count == 0) return true;
                PrepareDirectory(car.Location, RegistryFile, dataDir => {
                    var acd = Path.Combine(car.Location, "data.acd");
                    if (File.Exists(acd)) Acd.FromFile(acd).ExportDirectory(dataDir);
                    else CopyDirectory(Path.Combine(car.Location, "data"), dataDir);
                    ApplyToData(car, dataDir, parts);
                });
                Logging.Write("[GtTuning] applied " + parts.Count + " parts to " + car.Id + ": " + string.Join(", ", parts.Select(p => p.Id)));
                return true;
            } catch (Exception e) {
                Logging.Warning("[GtTuning] prepare: " + e);
                return false;
            }
        }

        public static bool Restore(CarObject car) {
            return car != null && RestoreDir(car.Location);
        }

        private static bool RestoreDir(string loc) {
            try {
                RestoreDirectory(loc, RegistryFile);
                return true;
            } catch (Exception e) {
                Logging.Warning("[GtTuning] restore: " + e);
                return false;
            }
        }

        private static string CarDirectory(string loc) {
            if (string.IsNullOrWhiteSpace(loc)) throw new ArgumentException("Missing car directory");
            var full = Path.GetFullPath(loc).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full == Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) throw new IOException("Car directory cannot be a drive root");
            return full;
        }

        private static void CheckOrdinaryDirectory(string path) {
            if (File.Exists(path)) throw new IOException("Expected a directory: " + path);
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to modify a linked data directory: " + path);
        }

        private static bool OwnedData(string path) {
            return Directory.Exists(path) && File.Exists(Path.Combine(path, Marker));
        }

        private static bool HasDeleteRecord(string path) {
            var record = path + "~gt7_delete";
            if (Directory.Exists(record)) throw new IOException("Unexpected tuning deletion record directory: " + record);
            if (!File.Exists(record)) return false;
            if (!string.Equals(File.ReadAllText(record), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unknown tuning deletion record is preserved: " + record);
            return true;
        }

        private static void DeleteOwnedData(string loc, string path) {
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), loc, StringComparison.OrdinalIgnoreCase)
                    || (Path.GetFileName(full) != "data" && Path.GetFileName(full) != "data~gt7_work")) throw new IOException("Unexpected tuning data path: " + path);
            CheckOrdinaryDirectory(full);
            if (!OwnedData(full)) throw new IOException("Unmarked data directory is preserved: " + full);
            FinishDeletion(loc);
            var deleting = Path.Combine(loc, "data~gt7_deleting");
            // 所有記録は削除専用の場所だけに適用する。ユーザーが戻した live data には適用しない。
            WriteAtomic(deleting + "~gt7_delete", deleting);
            Directory.Move(full, deleting);
            FinishDeletion(loc);
        }

        private static void FinishDeletion(string loc) {
            var deleting = Path.Combine(loc, "data~gt7_deleting");
            // 再帰削除の対象は、正規化済みの車フォルダ直下にある専用フォルダだけ。
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(deleting)), loc, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected tuning deletion path");
            CheckOrdinaryDirectory(deleting);
            var recorded = HasDeleteRecord(deleting);
            if (Directory.Exists(deleting)) {
                if (!OwnedData(deleting) && !recorded) throw new IOException("Unmarked deletion directory is preserved: " + deleting);
                if (!recorded) WriteAtomic(deleting + "~gt7_delete", deleting);
                Directory.Delete(deleting, true);
            }
            if (File.Exists(deleting + "~gt7_delete")) File.Delete(deleting + "~gt7_delete");
        }

        /// <summary>元データを動かす前に作業フォルダへの展開・反映を完了する。テストもこの実装を使う。</summary>
        internal static void PrepareDirectory(string location, string registryFile, Action<string> buildData) {
            var loc = CarDirectory(location);
            RestoreDirectory(loc, registryFile);
            var acd = Path.Combine(loc, "data.acd");
            var data = Path.Combine(loc, "data");
            var work = Path.Combine(loc, "data~gt7_work");
            if (!File.Exists(acd) && !Directory.Exists(data)) throw new IOException("No car physics data: " + loc);
            var registry = ReadRegistry(registryFile);
            if (!registry.Any(x => string.Equals(x, loc, StringComparison.OrdinalIgnoreCase))) registry.Add(loc);
            WriteRegistry(registryFile, registry); // 成功するまで元データへ触らない。
            try {
                Directory.CreateDirectory(work);
                File.WriteAllText(Path.Combine(work, Marker), "tuned by CM fork " + DateTime.Now.ToString("s"));
                buildData(work);
                if (Directory.Exists(data)) Directory.Move(data, Path.Combine(loc, "data~gt7_orig"));
                if (File.Exists(acd)) File.Move(acd, Path.Combine(loc, "data.acd~gt7_stock"));
                Directory.Move(work, data);
            } catch {
                try { RestoreDirectory(loc, registryFile); } catch (Exception e) { Logging.Warning("[GtTuning] rollback: " + e); }
                throw;
            }
        }

        /// <summary>未マーカーの data と競合する原本は残す。復元が完了するまでは台帳から外さない。</summary>
        internal static void RestoreDirectory(string location, string registryFile) {
            var loc = CarDirectory(location);
            var registry = ReadRegistry(registryFile); // 壊れた台帳を消したり空扱いしない。
            if (!Directory.Exists(loc)) throw new DirectoryNotFoundException("Car directory is unavailable; retaining recovery registration: " + loc);
            var acd = Path.Combine(loc, "data.acd");
            var data = Path.Combine(loc, "data");
            var stock = Path.Combine(loc, "data.acd~gt7_stock");
            var orig = Path.Combine(loc, "data~gt7_orig");
            var work = Path.Combine(loc, "data~gt7_work");
            var deleting = Path.Combine(loc, "data~gt7_deleting");
            foreach (var path in new[] { data, orig, work, deleting }) CheckOrdinaryDirectory(path);
            var deletionRecorded = HasDeleteRecord(deleting);
            if (Directory.Exists(deleting) && !OwnedData(deleting) && !deletionRecorded) throw new IOException("Unmarked deletion directory is preserved: " + deleting);
            if (Directory.Exists(work) && !OwnedData(work)) throw new IOException("Unmarked tuning work directory is preserved: " + work);
            if (File.Exists(stock) && File.Exists(acd)) throw new IOException("Both original and current data.acd exist; preserving both: " + loc);
            if (Directory.Exists(orig) && Directory.Exists(data) && !OwnedData(data)) throw new IOException("Both original and unmarked data exist; preserving both: " + loc);
            if (OwnedData(data) && !File.Exists(stock) && !Directory.Exists(orig)) throw new IOException("Tuned data has no original backup; preserving it: " + loc);
            var touched = Directory.Exists(work) || Directory.Exists(orig) || File.Exists(stock) || OwnedData(data) || Directory.Exists(deleting) || deletionRecorded;
            FinishDeletion(loc);
            if (OwnedData(work)) DeleteOwnedData(loc, work);
            if (OwnedData(data)) DeleteOwnedData(loc, data);
            if (File.Exists(stock)) File.Move(stock, acd);
            if (Directory.Exists(orig)) Directory.Move(orig, data);
            if (registry.RemoveAll(x => string.Equals(x, loc, StringComparison.OrdinalIgnoreCase)) > 0) WriteRegistry(registryFile, registry);
            if (touched) Logging.Write("[GtTuning] restored " + loc);
        }

        /// <summary>CM 起動時: 前回クラッシュ等で戻し損ねた車を戻す。</summary>
        public static void RestoreAll() {
            try {
                foreach (var loc in ReadRegistry(RegistryFile).ToList()) RestoreDir(loc);
            } catch (Exception e) {
                Logging.Warning("[GtTuning] restore all: " + e.Message);
            }
        }

        private static void CopyDirectory(string from, string to) {
            CheckOrdinaryDirectory(from);
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }

        private static string F(double v) {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>展開済み data\ にパーツを反映する。</summary>
        private static void ApplyToData(CarObject car, string dataDir, List<GtPart> parts) {
            var power = 1.0;
            var mass = 1.0;
            var brake = 1.0;
            var inertia = 1.0;
            var final = 1.0;
            var boost = 0.0;
            var refRpm = 0.0;
            var lagUp = -1.0;
            var antiLag = 0.0;
            double diffPower = -1, diffCoast = -1;
            var tyre = -1;
            var turbo = HasTurbo(car);
            foreach (var p in parts) {
                if (p.TurboOnly && !turbo && !parts.Any(x => x.IsForced)) continue;
                power *= 1 + p.Power;
                mass *= 1 + p.Mass;
                brake *= p.Brake;
                inertia *= p.Inertia;
                final *= p.Final;
                if (p.Boost > 0) { boost += p.Boost; refRpm = p.RefRpm; lagUp = p.LagUp; }
                antiLag += p.AntiLag;
                if (p.DiffPower >= 0) { diffPower = p.DiffPower; diffCoast = p.DiffCoast; }
                if (p.TyreIndex >= 0) tyre = p.TyreIndex;
            }

            // power.lut: "rpm|torque" のトルクを倍率で伸ばす
            var engineIni = Path.Combine(dataDir, "engine.ini");
            var engine = File.Exists(engineIni) ? new IniFile(engineIni) : null;
            if (Math.Abs(power - 1) > 1e-6) {
                var lutName = engine?["HEADER"].GetNonEmpty("POWER_CURVE") ?? "power.lut";
                var lutPath = Path.Combine(dataDir, lutName);
                if (File.Exists(lutPath)) {
                    var sb = new StringBuilder();
                    foreach (var line in File.ReadAllLines(lutPath)) {
                        var t = line.Trim();
                        var bar = t.IndexOf('|');
                        double x, y;
                        if (bar > 0 && double.TryParse(t.Substring(0, bar), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                                && double.TryParse(t.Substring(bar + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) {
                            sb.Append(F(x)).Append('|').Append(F(y * power)).Append("\r\n");
                        } else {
                            sb.Append(line).Append("\r\n");
                        }
                    }
                    File.WriteAllText(lutPath, sb.ToString());
                }
            }

            if (engine != null) {
                var changed = false;
                if (Math.Abs(inertia - 1) > 1e-6) {
                    var ed = engine["ENGINE_DATA"];
                    ed.Set("INERTIA", ed.GetDouble("INERTIA", 0.15) * inertia, "0.####");
                    changed = true;
                }
                if (boost > 0 || antiLag > 0) {
                    var total = 0.0;
                    if (turbo) {
                        for (var i = 0; i < 4; i++) {
                            var key = "TURBO_" + i;
                            if (!engine.ContainsKey(key)) break;
                            var t = engine[key];
                            if (boost > 0) {
                                t.Set("MAX_BOOST", t.GetDouble("MAX_BOOST", 0) + boost, "0.###");
                                var wg = t.GetDouble("WASTEGATE", 0);
                                if (wg > 0) t.Set("WASTEGATE", wg + boost, "0.###");
                                t.Set("DISPLAY_MAX_BOOST", t.GetDouble("DISPLAY_MAX_BOOST", 0) + boost, "0.###");
                            }
                            if (antiLag > 0) t.Set("LAG_UP", Math.Max(0.9, t.GetDouble("LAG_UP", 0.995) - antiLag), "0.####");
                            total += t.GetDouble("MAX_BOOST", 0);
                        }
                    } else if (boost > 0) {
                        var t = engine["TURBO_0"];
                        t.Set("LAG_DN", lagUp < 0.95 ? 0.95 : 0.99, "0.###");
                        t.Set("LAG_UP", Math.Max(0.9, (lagUp < 0 ? 0.994 : lagUp) - antiLag), "0.####");
                        t.Set("MAX_BOOST", boost, "0.###");
                        t.Set("WASTEGATE", boost, "0.###");
                        t.Set("DISPLAY_MAX_BOOST", boost, "0.###");
                        t.Set("REFERENCE_RPM", refRpm > 0 ? refRpm : 3000, "0");
                        t.Set("GAMMA", 1.0, "0.#");
                        t.Set("COCKPIT_ADJUSTABLE", 0);
                        total = boost;
                    }
                    var dmg = engine["DAMAGE"];
                    var threshold = dmg.GetDouble("TURBO_BOOST_THRESHOLD", 0);
                    if (threshold < total + 0.3) dmg.Set("TURBO_BOOST_THRESHOLD", total + 0.5, "0.##");
                    if (!dmg.ContainsKey("TURBO_DAMAGE_K")) dmg.Set("TURBO_DAMAGE_K", 5);
                    changed = true;
                }
                if (changed) engine.Save();
            }

            if (Math.Abs(mass - 1) > 1e-6) {
                var carIni = Path.Combine(dataDir, "car.ini");
                if (File.Exists(carIni)) {
                    var ini = new IniFile(carIni);
                    var b = ini["BASIC"];
                    b.Set("TOTALMASS", Math.Round(b.GetDouble("TOTALMASS", 0) * mass), "0");
                    ini.Save();
                }
            }

            if (Math.Abs(brake - 1) > 1e-6) {
                var brakesIni = Path.Combine(dataDir, "brakes.ini");
                if (File.Exists(brakesIni)) {
                    var ini = new IniFile(brakesIni);
                    var d = ini["DATA"];
                    d.Set("MAX_TORQUE", Math.Round(d.GetDouble("MAX_TORQUE", 0) * brake), "0");
                    ini.Save();
                }
            }

            if (Math.Abs(final - 1) > 1e-6 || diffPower >= 0) {
                var dtIni = Path.Combine(dataDir, "drivetrain.ini");
                if (File.Exists(dtIni)) {
                    var ini = new IniFile(dtIni);
                    if (Math.Abs(final - 1) > 1e-6) {
                        var g = ini["GEARS"];
                        g.Set("FINAL", g.GetDouble("FINAL", 3.5) * final, "0.###");
                    }
                    if (diffPower >= 0) {
                        var d = ini["DIFFERENTIAL"];
                        d.Set("POWER", diffPower / 100.0, "0.##");
                        d.Set("COAST", diffCoast / 100.0, "0.##");
                    }
                    ini.Save();
                }
            }

            if (tyre >= 0) {
                var tyresIni = Path.Combine(dataDir, "tyres.ini");
                if (File.Exists(tyresIni)) {
                    var ini = new IniFile(tyresIni);
                    ini["COMPOUND_DEFAULT"].Set("INDEX", tyre);
                    ini.Save();
                }
            }
        }

        /// <summary>race.ini 行き: クイックドライブのプリセット JSON に PlayerBallast / PlayerRestrictor を注入する。
        /// レースは ModeData.RaceGridSerialized の中、タイムトライアル（Hotlap）は ModeData 直下（CM の保存形式に合わせる）。</summary>
        public static string InjectPreset(string saved, CarObject car, bool race, Uri modeUri) {
            try {
                var s = Get(car);
                var rest = s.Installed.Contains("restrictor") ? s.Restrictor : 0;
                var bal = s.Installed.Contains("ballast") ? s.Ballast : 0;
                var o = string.IsNullOrEmpty(saved) ? new JObject() : JObject.Parse(saved);
                var uri = modeUri.OriginalString;
                JObject rd = null;
                if (string.Equals((string)o["Mode"], uri, StringComparison.OrdinalIgnoreCase)) {
                    try { rd = JObject.Parse((string)o["ModeData"] ?? ""); } catch { }
                }
                if (rd == null) {
                    // 今のモードの保存値（__QuickDrive_Hotlap 等）を種にする。無ければ空
                    try {
                        var key = race ? "__QuickDrive_Race" : "__QuickDrive_Hotlap";
                        var cur = ValuesStorage.Get<string>(key);
                        if (!string.IsNullOrEmpty(cur)) rd = JObject.Parse(cur);
                    } catch { }
                    if (rd == null) rd = new JObject();
                }
                if (race) {
                    JObject rg = null;
                    try { rg = JObject.Parse((string)rd["RaceGridSerialized"] ?? ""); } catch { }
                    if (rg == null) rg = new JObject();
                    rg["PlayerBallast"] = (double)bal;
                    rg["PlayerRestrictor"] = (double)rest;
                    rd["RaceGridSerialized"] = rg.ToString(Formatting.None);
                } else {
                    rd["PlayerBallast"] = bal;
                    rd["PlayerRestrictor"] = rest;
                }
                o["Mode"] = uri;
                o["ModeData"] = rd.ToString(Formatting.None);
                return o.ToString(Formatting.None);
            } catch (Exception e) {
                Logging.Warning("[GtTuning] inject: " + e.Message);
                return saved;
            }
        }
    }
}
