// [fork] 準備画面の「ベストタイム」面の元データと、走行記録の恒久保存（2026-09-27）。
//   要望「準備画面にベストタイムを表示しておいてほしい。同一車両のワールドレコードとかも表示しておいてほしい。参考にしたいから」
//        「ベストタイムは、車別に表示とかできるようにしてほしい。車関係なく表示する、とかもできてほしい」
//   コースインの時に Documents\Assetto Corsa\cfg\gt_start_records.ini を書く → AC 側の GtStart（previewtools.lua）が読んで右パネルに出す。
//   ・自己ベスト = AC の personalbest.ini（この車 / このコースの全車）。走った条件は Data\Static\gt7\records\best_laps.json（save_record.py が作る）
//   ・世界記録 = RSR Live Timing（AC のホットラップ記録サイト。http のみ）の world_records 表（全コース×全車・約 2MB）を 3 日キャッシュ。
//     その組の順位表（1 位の日付・気温・路面温度・操作デバイス・記録数）は 7 日キャッシュ。取りに行くのは裏で、コースインは待たせない
//     （取れたら同じファイルを書き直す＝準備画面は 2 秒おきに読み直す）
//   ・走り終わったら records フォルダの save_record.py を裏で回して、ベストと条件を恒久保存
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using AcTools.Utils;
using FirstFloor.ModernUI.Helpers;
using Newtonsoft.Json.Linq;

namespace AcManager.Pages.Gt {
    internal static class GtRecords {
        private const string WrUrl = "http://www.radiators-champ.com/RSRLiveTiming/index.php?page=world_records";
        private const string RankUrl = "http://www.radiators-champ.com/RSRLiveTiming/index.php?page=rank&track={0}&car={1}";
        private static string RecordsDir {
            get {
                var dir = Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "records");
                try {
                    // records_dir.txt（この既定フォルダに置く）の最初の行で保存先を変えられる。# で始まる行は無視
                    var cfg = Path.Combine(dir, "records_dir.txt");
                    if (File.Exists(cfg)) {
                        var custom = File.ReadAllLines(cfg).Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !x.StartsWith("#"));
                        if (!string.IsNullOrEmpty(custom) && Directory.Exists(custom)) return custom;
                    }
                } catch (Exception e) {
                    Logging.Warning("[GtRecords] records_dir.txt: " + e.Message);
                }
                return dir;
            }
        }
        private static readonly TimeSpan WrMaxAge = TimeSpan.FromDays(3), RankMaxAge = TimeSpan.FromDays(7);
        private static readonly object Sync = new object();

        private static string CacheDir => Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "records");
        private static string WrCache => Path.Combine(CacheDir, "rsr_world_records.tsv");
        private static string OutPath => Path.Combine(AcPaths.GetDocumentsCfgDirectory(), "gt_start_records.ini");

        private sealed class Ctx {
            public string TrackId, LayoutId, CarId, TrackName, CarName, KunosTrack;
            public string[] TrackKeys, CarKeys;
            public long CarPbMs, TrackPbMs;
            public string CarPbDate, CarPbNote, TrackPbCar, TrackPbDate;
        }

        private sealed class Wr {
            public string Car, Track, Driver, TrackRsr, CarRsr;
            public long Ms;
        }

        /// <summary>コースインの直前に呼ぶ（UI スレッド）。手元の分をすぐ書き、世界記録の取り直しは裏で。</summary>
        public static void PrepareForLaunch(TrackObjectBase layout, CarObject car) {
            if (layout == null || car == null) return;
            try {
                var main = layout.MainTrackObject;
                var ctx = new Ctx {
                    TrackId = main.Id, LayoutId = layout.LayoutId ?? "", CarId = car.Id, KunosTrack = layout.KunosIdWithLayout,
                    TrackName = layout.Name ?? main.Name ?? main.Id, CarName = car.Name ?? car.DisplayName ?? car.Id,
                };
                var tk = new List<string> { layout.Name, main.Name };
                if (!string.IsNullOrEmpty(layout.LayoutName)) { tk.Add(main.Name + " - " + layout.LayoutName); tk.Add(main.Name + " " + layout.LayoutName); }
                ctx.TrackKeys = tk.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Norm).Distinct().ToArray();
                ctx.CarKeys = new[] { car.Name, car.DisplayName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Norm).Distinct().ToArray();
                ReadPersonalBests(ctx);
                ReadConditions(ctx);
                Write(ctx);
                Task.Run(() => {
                    try { if (Refresh(ctx)) Write(ctx); } catch (Exception e) { Logging.Warning("[GtRecords] refresh: " + e.Message); }
                });
            } catch (Exception e) {
                Logging.Warning("[GtRecords] prepare: " + e);
            }
        }

        /// <summary>AC が終わった後に呼ぶ。save_record.py（標準ライブラリだけ）を裏で回して、ベストと条件を恒久保存する。</summary>
        public static void SaveAfterRun() {
            try {
                var script = Path.Combine(RecordsDir, "save_record.py");
                var pyw = FindPythonw();
                if (!File.Exists(script) || pyw == null) { Logging.Warning("[GtRecords] save skipped: script/python not found"); return; }
                using (Process.Start(new ProcessStartInfo(pyw, "\"" + script + "\"") {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RecordsDir
                })) { }
                Logging.Write("[GtRecords] save_record.py started");
            } catch (Exception e) {
                Logging.Warning("[GtRecords] save: " + e.Message);
            }
        }

        private static string FindPythonw() {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var v in new[] { "Python312", "Python313", "Python311", "Python310" }) {
                var p = Path.Combine(local, "Programs", "Python", v, "pythonw.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // ── 自己ベスト（personalbest.ini: [CAR@TRACK] または [CAR@TRACK-LAYOUT]・TIME=ms・DATE=ms epoch）──
        private static void ReadPersonalBests(Ctx ctx) {
            var path = Path.Combine(AcPaths.GetDocumentsDirectory(), "personalbest.ini");
            if (!File.Exists(path)) return;
            var suffix = "@" + ctx.KunosTrack.ToUpperInvariant();
            var carKey = ctx.CarId.ToUpperInvariant() + suffix;
            string section = null;
            long time = 0, date = 0;
            var best = new Dictionary<string, Tuple<long, long>>();
            Action flush = () => {
                if (section != null && time > 0 && section.EndsWith(suffix, StringComparison.Ordinal)) best[section] = Tuple.Create(time, date);
            };
            foreach (var raw in File.ReadAllLines(path)) {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { flush(); section = line.Substring(1, line.Length - 2).ToUpperInvariant(); time = date = 0; continue; }
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                long v;
                if (!long.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) continue;
                var k = line.Substring(0, eq).Trim().ToUpperInvariant();
                if (k == "TIME") time = v; else if (k == "DATE") date = v;
            }
            flush();
            if (best.TryGetValue(carKey, out var mine)) { ctx.CarPbMs = mine.Item1; ctx.CarPbDate = EpochDate(mine.Item2); }
            if (best.Count > 0) {
                var top = best.OrderBy(x => x.Value.Item1).First();
                ctx.TrackPbMs = top.Value.Item1;
                ctx.TrackPbDate = EpochDate(top.Value.Item2);
                var carId = top.Key.Substring(0, top.Key.Length - suffix.Length).ToLowerInvariant();
                ctx.TrackPbCar = CarsManager.Instance.GetById(carId)?.DisplayName ?? carId;
            }
        }

        private static string EpochDate(long ms) {
            if (ms <= 0) return "";
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); } catch { return ""; }
        }

        // ── 走った条件（Data\Static\gt7\records\best_laps.json。時間が自己ベストと同じ記録の時だけ）──
        private static void ReadConditions(Ctx ctx) {
            var path = Path.Combine(RecordsDir, "best_laps.json");
            if (ctx.CarPbMs <= 0 || !File.Exists(path)) return;
            try {
                var db = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var key = ctx.TrackId + (string.IsNullOrEmpty(ctx.LayoutId) ? "" : "/" + ctx.LayoutId) + "@" + ctx.CarId;
                var best = db["records"]?[key]?["best"];
                if (best == null || (long?)best["time_ms"] != ctx.CarPbMs) return;
                var c = best["conditions"];
                var a = c?["assists"];
                Func<JToken, bool> on = t => t != null && t.Type != JTokenType.Null && (string)t != "0" && (string)t != "";
                var parts = new List<string> {
                    WeatherJa((string)c?["weather"]), (string)c?["time_of_day"],
                    c?["ambient_c"] != null ? string.Format("気温{0}℃/路面{1}℃", c["ambient_c"], c["road_c"]) : null,
                    (string)c?["tyre"], (string)a?["shifter"], on(a?["abs"]) ? "ABS" : null, on(a?["tc"]) ? "TC" : null,
                };
                ctx.CarPbNote = string.Join("・", parts.Where(x => !string.IsNullOrEmpty(x)));
            } catch (Exception e) {
                Logging.Warning("[GtRecords] conditions: " + e.Message);
            }
        }

        private static string WeatherJa(string name) {
            if (string.IsNullOrEmpty(name)) return null;
            var n = name.ToLowerInvariant();
            if (n.Contains("heavy_fog")) return "濃い霧";
            if (n.Contains("light_fog") || n.Contains("fog")) return "霧";
            if (n.Contains("mid_clear")) return "晴れ時々曇り";
            if (n.Contains("clear")) return "晴れ";
            if (n.Contains("light_clouds")) return "薄曇り";
            if (n.Contains("heavy_clouds")) return "厚い曇り";
            if (n.Contains("clouds")) return "曇り";
            if (n.Contains("rain")) return "雨";
            return name;
        }

        // ── 世界記録（RSR）──
        private static List<Wr> LoadWrCache() {
            var list = new List<Wr>();
            if (!File.Exists(WrCache)) return list;
            foreach (var line in File.ReadAllLines(WrCache, Encoding.UTF8)) {
                var p = line.Split('\t');
                if (p.Length < 6 || !long.TryParse(p[3], out var ms)) continue;
                list.Add(new Wr { Car = p[0], Track = p[1], Driver = p[2], Ms = ms, TrackRsr = p[4], CarRsr = p[5] });
            }
            return list;
        }

        private static string Download(string url) {
            using (var wc = new WebClient { Encoding = Encoding.UTF8 }) {
                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (CM7 personal lap reference)";
                try {
                    return wc.DownloadString(url);
                } catch (WebException we) when (we.Response != null) {
                    // RSR の順位表は中身を返しながら HTTP 500 になる（2026-09-27 実測）
                    using (var r = new StreamReader(we.Response.GetResponseStream() ?? Stream.Null, Encoding.UTF8)) return r.ReadToEnd();
                }
            }
        }

        private static string Text(string html) {
            var t = Regex.Replace(html, "<script.*?</script>|<style.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<[^>]+>", "|");
            t = WebUtility.HtmlDecode(t);
            t = Regex.Replace(t, @"\s+", " ");
            return Regex.Replace(t, @"(\|\s*)+", "|");
        }

        /// <summary>裏スレッド。古ければ取り直す。書き直す必要があれば true。</summary>
        private static bool Refresh(Ctx ctx) {
            lock (Sync) {
                Directory.CreateDirectory(CacheDir);
                var changed = false;
                if (!File.Exists(WrCache) || DateTime.Now - File.GetLastWriteTime(WrCache) > WrMaxAge) {
                    var html = Download(WrUrl);
                    var cells = Regex.Matches(html, "<a href=\"index\\.php\\?page=rank&track=(\\d+)&car=(\\d+)\" title=\"([^\"]*)\">\\s*<b>([^<]*)</b><br />\\s*([\\d:.]+)");
                    var sb = new StringBuilder();
                    foreach (Match m in cells) {
                        var title = WebUtility.HtmlDecode(m.Groups[3].Value);
                        var cut = title.LastIndexOf(" / ", StringComparison.Ordinal);
                        var ms = ParseTime(m.Groups[5].Value);
                        if (cut <= 0 || ms <= 0) continue;
                        sb.Append(title.Substring(0, cut)).Append('\t').Append(title.Substring(cut + 3)).Append('\t')
                          .Append(WebUtility.HtmlDecode(m.Groups[4].Value).Trim().Replace('\t', ' ')).Append('\t').Append(ms).Append('\t')
                          .Append(m.Groups[1].Value).Append('\t').Append(m.Groups[2].Value).Append('\n');
                    }
                    if (cells.Count > 1000) {   // 壊れたページで良いキャッシュを潰さない
                        File.WriteAllText(WrCache, sb.ToString(), new UTF8Encoding(false));
                        Logging.Write("[GtRecords] RSR world records: " + cells.Count + " cells");
                        changed = true;
                    } else {
                        Logging.Warning("[GtRecords] RSR world records: unexpected page (" + cells.Count + " cells)");
                    }
                }
                var mine = FindCarWr(ctx, LoadWrCache());
                if (mine != null) {
                    var rank = Path.Combine(CacheDir, "rsr_rank_" + mine.TrackRsr + "_" + mine.CarRsr + ".txt");
                    if (!File.Exists(rank) || DateTime.Now - File.GetLastWriteTime(rank) > RankMaxAge) {
                        var t = Text(Download(string.Format(RankUrl, mine.TrackRsr, mine.CarRsr)));
                        var m = Regex.Match(t, @"\|1\|([^|]+)\|(\d+:\d\d\.\d{3})\|(?:[^|]*\|){5}(\d{4})\.(\d\d)\.(\d\d)\. [\d:]+\|([^|]+)\|Air temp: \|(-?\d+)°C\|Track temp: \|(-?\d+)°C");
                        if (m.Success) {
                            var tail = t.Substring(m.Index, Math.Min(1500, t.Length - m.Index));
                            var input = Regex.Match(tail, @"Input method: \|([^|]+)\|").Groups[1].Value.Trim();
                            var triple = Regex.Match(tail, @"Triple screen: \|([^|]+)\|").Groups[1].Value.Trim();
                            var tyreCount = Regex.Match(t, Regex.Escape(m.Groups[6].Value.Trim()) + @" \((\d+)\)").Groups[1].Value;
                            File.WriteAllText(rank, string.Join("\n", new[] {
                                "DATE=" + m.Groups[3].Value + "-" + m.Groups[4].Value + "-" + m.Groups[5].Value,
                                "TYRE=" + m.Groups[6].Value.Trim(), "AIR=" + m.Groups[7].Value, "ROAD=" + m.Groups[8].Value,
                                "INPUT=" + input, "TRIPLE=" + triple, "COUNT=" + tyreCount,
                            }), new UTF8Encoding(false));
                            changed = true;
                        } else {
                            Logging.Warning("[GtRecords] RSR rank page: no first row for " + mine.Car + " / " + mine.Track);
                        }
                    }
                }
                return changed;
            }
        }

        private static long ParseTime(string s) {
            var m = Regex.Match(s ?? "", @"^(\d+):(\d\d)\.(\d{3})$");
            if (!m.Success) return 0;
            return long.Parse(m.Groups[1].Value) * 60000 + long.Parse(m.Groups[2].Value) * 1000 + long.Parse(m.Groups[3].Value);
        }

        internal static string Norm(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            var d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in d) {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        private static IEnumerable<Wr> TrackRows(Ctx ctx, List<Wr> all) => all.Where(w => ctx.TrackKeys.Contains(Norm(w.Track)));
        private static Wr FindCarWr(Ctx ctx, List<Wr> all) => TrackRows(ctx, all).FirstOrDefault(w => ctx.CarKeys.Contains(Norm(w.Car)));

        // ── 書き出し（GtStart の previewtools.lua が読む。値に ; を入れない＝INI のコメント扱いになる）──
        private static void Write(Ctx ctx) {
            lock (Sync) {
                var all = LoadWrCache();
                var carWr = FindCarWr(ctx, all);
                var trackWr = TrackRows(ctx, all).OrderBy(w => w.Ms).FirstOrDefault();
                var rank = new Dictionary<string, string>();
                if (carWr != null) {
                    var rp = Path.Combine(CacheDir, "rsr_rank_" + carWr.TrackRsr + "_" + carWr.CarRsr + ".txt");
                    if (File.Exists(rp)) {
                        foreach (var l in File.ReadAllLines(rp, Encoding.UTF8)) { var eq = l.IndexOf('='); if (eq > 0) rank[l.Substring(0, eq)] = l.Substring(eq + 1); }
                    }
                }
                Func<string, string> clean = s => (s ?? "").Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();
                var wrNote = new List<string>();
                if (rank.TryGetValue("AIR", out var air) && rank.TryGetValue("ROAD", out var road)) wrNote.Add("気温" + air + "℃/路面" + road + "℃");
                if (rank.TryGetValue("INPUT", out var input) && input != "") wrNote.Add(input.Equals("Wheel", StringComparison.OrdinalIgnoreCase) ? "ハンコン" : input);
                if (rank.TryGetValue("TRIPLE", out var triple) && triple != "") wrNote.Add(triple.Equals("ON", StringComparison.OrdinalIgnoreCase) ? "3画面" : "1画面");
                if (rank.TryGetValue("TYRE", out var tyre) && tyre != "") {
                    var shortTyre = Regex.Match(tyre, @"\(([^)]+)\)\s*$");   // "Hypercar road (HR)" → "HR"（準備画面の 1 行に収める）
                    wrNote.Add(shortTyre.Success ? shortTyre.Groups[1].Value : tyre);
                }
                var sb = new StringBuilder();
                sb.Append("; CM7 がコースインの時に書く（GtRecords.cs）。GtStart の準備画面「ベストタイム」面が読む\n");
                sb.Append("[META]\nTRACK=").Append(ctx.TrackId).Append("\nLAYOUT=").Append(ctx.LayoutId).Append("\nCAR=").Append(ctx.CarId)
                  .Append("\nTRACK_NAME=").Append(clean(ctx.TrackName)).Append("\nCAR_NAME=").Append(clean(ctx.CarName))
                  .Append("\nWR_SOURCE=RSR Live Timing\nWR_FETCHED=")
                  .Append(File.Exists(WrCache) ? File.GetLastWriteTime(WrCache).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "").Append('\n');
                sb.Append("[CAR_PB]\nMS=").Append(ctx.CarPbMs).Append("\nDATE=").Append(ctx.CarPbDate).Append("\nNOTE=").Append(clean(ctx.CarPbNote)).Append('\n');
                sb.Append("[CAR_WR]\nMS=").Append(carWr?.Ms ?? 0).Append("\nDRIVER=").Append(clean(carWr?.Driver))
                  .Append("\nDATE=").Append(rank.TryGetValue("DATE", out var wd) ? wd : "")
                  .Append("\nCOUNT=").Append(rank.TryGetValue("COUNT", out var cnt) ? cnt : "")
                  .Append("\nNOTE=").Append(clean(string.Join("・", wrNote))).Append('\n');
                sb.Append("[TRACK_PB]\nMS=").Append(ctx.TrackPbMs).Append("\nCAR=").Append(clean(ctx.TrackPbCar)).Append("\nDATE=").Append(ctx.TrackPbDate).Append('\n');
                sb.Append("[TRACK_WR]\nMS=").Append(trackWr?.Ms ?? 0).Append("\nDRIVER=").Append(clean(trackWr?.Driver)).Append("\nCAR=").Append(clean(trackWr?.Car)).Append('\n');
                File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(false));
                Logging.Write("[GtRecords] wrote " + ctx.CarId + "@" + ctx.KunosTrack + " pb=" + ctx.CarPbMs + " wr=" + (carWr?.Ms ?? 0) + " trackWr=" + (trackWr?.Ms ?? 0));
            }
        }
    }
}
