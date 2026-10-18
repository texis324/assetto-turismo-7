// [fork] コース名・国名の日本語表記（2026-09-27 要望「コース選択のカード、英語じゃなくて、すべて日本語に直しておいて」）。
//   コース = トラック ID の前方一致（長い方が勝つ）。表に無いコースは今まで通り DisplayName（英語）。
//   追加・上書きは Data\Static\gt7\names_ja.txt（1 行「トラックID=日本語名」・# はコメント）＝ビルドも再起動も不要（画面を開くたびに読む）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AcManager.Tools.Objects;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal static class GtNames {
        private static readonly Dictionary<string, string> BuiltIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["90sgdsp_happogahara"] = "八方ヶ原",
            ["abu_dhabi"] = "ヤス・マリーナ・サーキット",
            ["am_tsukuba1000"] = "筑波サーキット コース1000",
            ["am_tsukuba"] = "筑波サーキット",
            ["baku"] = "バクー市街地コース",
            ["bristol_motor_speedway"] = "ブリストル・モーター・スピードウェイ",
            ["drift"] = "ドリフト",
            ["ets2_gg_look"] = "グロースグロックナー（ETS2 見た目テスト）",
            ["ets2_test"] = "ETS2 テスト直線",
            ["fonteny"] = "フォンテニー",
            ["hakone_nanamagari"] = "箱根七曲り",
            ["imola"] = "イモラ",
            ["ks_barcelona"] = "バルセロナ＝カタルーニャ",
            ["ks_black_cat_county"] = "ブラックキャット・カウンティ",
            ["ks_brands_hatch"] = "ブランズハッチ",
            ["ks_drag"] = "ドラッグ",
            ["ks_highlands"] = "ハイランズ",
            ["ks_laguna_seca"] = "ラグナ・セカ",
            ["ks_monza66"] = "モンツァ 1966",
            ["ks_nordschleife"] = "ニュルブルクリンク 北コース",
            ["ks_nurburgring"] = "ニュルブルクリンク GP",
            ["ks_red_bull_ring"] = "レッドブル・リンク",
            ["ks_silverstone1967"] = "シルバーストーン 1967",
            ["ks_silverstone"] = "シルバーストーン",
            ["ks_vallelunga"] = "ヴァレルンガ",
            ["ks_zandvoort"] = "ザントフォールト",
            ["la_canyons"] = "ロサンゼルス・キャニオンズ",
            ["magione"] = "マジョーネ",
            ["meihan"] = "名阪スポーツランド",
            ["monaco"] = "モナコ市街地コース",
            ["monza"] = "モンツァ",
            ["mugello"] = "ムジェロ",
            ["pk_akina"] = "秋名山",
            ["rt_fuji_speedway"] = "富士スピードウェイ",
            ["rt_suzuka"] = "鈴鹿サーキット",
            ["shutoko_revival_project"] = "首都高速",
            ["spa"] = "スパ・フランコルシャン",
            ["trento-bondone"] = "トレント＝ボンドーネ",
        };

        private static readonly Dictionary<string, string> CountriesJa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["japan"] = "日本", ["usa"] = "アメリカ", ["u.s.a."] = "アメリカ", ["united states"] = "アメリカ", ["united states of america"] = "アメリカ",
            ["america"] = "アメリカ", ["canada"] = "カナダ", ["mexico"] = "メキシコ", ["brazil"] = "ブラジル", ["argentina"] = "アルゼンチン",
            ["italy"] = "イタリア", ["germany"] = "ドイツ", ["france"] = "フランス", ["spain"] = "スペイン", ["portugal"] = "ポルトガル",
            ["great britain"] = "イギリス", ["united kingdom"] = "イギリス", ["uk"] = "イギリス", ["england"] = "イギリス", ["scotland"] = "スコットランド",
            ["wales"] = "ウェールズ", ["ireland"] = "アイルランド", ["belgium"] = "ベルギー", ["netherlands"] = "オランダ", ["austria"] = "オーストリア",
            ["switzerland"] = "スイス", ["sweden"] = "スウェーデン", ["norway"] = "ノルウェー", ["finland"] = "フィンランド", ["denmark"] = "デンマーク",
            ["poland"] = "ポーランド", ["czech republic"] = "チェコ", ["czechia"] = "チェコ", ["hungary"] = "ハンガリー", ["russia"] = "ロシア",
            ["monaco"] = "モナコ", ["turkey"] = "トルコ", ["azerbaijan"] = "アゼルバイジャン", ["united arab emirates"] = "アラブ首長国連邦",
            ["uae"] = "アラブ首長国連邦", ["bahrain"] = "バーレーン", ["qatar"] = "カタール", ["saudi arabia"] = "サウジアラビア",
            ["china"] = "中国", ["korea"] = "韓国", ["south korea"] = "韓国", ["taiwan"] = "台湾", ["singapore"] = "シンガポール",
            ["malaysia"] = "マレーシア", ["thailand"] = "タイ", ["india"] = "インド", ["australia"] = "オーストラリア", ["new zealand"] = "ニュージーランド",
            ["south africa"] = "南アフリカ", ["croatia"] = "クロアチア",
        };

        private static Dictionary<string, string> _file;
        private static DateTime _fileStamp;

        private static Dictionary<string, string> FromFile() {
            var path = Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "names_ja.txt");
            try {
                if (!File.Exists(path)) return _file = null;
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_file != null && stamp == _fileStamp) return _file;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8)) {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                _fileStamp = stamp;
                return _file = d;
            } catch (Exception e) {
                Logging.Warning("[GtNames] names_ja.txt: " + e.Message);
                return null;
            }
        }

        /// <summary>コースの日本語名（無ければ null）。</summary>
        public static string TrackJa(string trackId) {
            if (string.IsNullOrEmpty(trackId)) return null;
            var file = FromFile();
            foreach (var table in new[] { file, BuiltIn }) {
                if (table == null) continue;
                var hit = table.Keys.Where(k => trackId.StartsWith(k, StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k.Length).FirstOrDefault();
                if (hit != null) return table[hit];
            }
            return null;
        }

        /// <summary>コースの表示名: 日本語名があればそれ、無ければ英語の DisplayName（末尾のレイアウト数「(4)」は外す）。</summary>
        public static string TrackName(TrackObjectBase track) {
            if (track == null) return "";
            var ja = TrackJa(track.MainTrackObject?.Id ?? track.Id);
            if (ja != null) return ja;
            return System.Text.RegularExpressions.Regex.Replace(track.DisplayName ?? "", @"\s*\(\d+\)\s*$", "");
        }

        /// <summary>国名の日本語（無ければ元のまま）。</summary>
        public static string CountryJa(string country) {
            if (string.IsNullOrWhiteSpace(country)) return country ?? "";
            return CountriesJa.TryGetValue(country.Trim(), out var ja) ? ja : country;
        }
    }
}
