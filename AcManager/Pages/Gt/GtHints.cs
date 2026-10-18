// [fork] GT ページ隅のヒント表記を「キーボード ⇔ パッド」で出し分ける。
//
//   GT7 本家はコントローラーで操作している間だけ、画面隅のヒントを ○ 戻る / × 決定 のボタン記号にする。
//   ここはその置換規則（Format）と、TextBlock を GtPad.PadModeChanged に繋ぐ配線（Bind）だけを持つ。
//
//   ★記号は pad.txt の confirm= に追従する（confirm=circle なら決定が ○・戻るが ×）。
//     なので置換結果を定数で持たず、毎回 ConfirmGlyph / BackGlyph から組み立てる。
//
//   ★モードの判定は GtPad.PadMode（ラッチ版）。GtPad.PadDriving は「直近 3 秒以内にパッド入力」なので
//     放っておくと勝手に false へ落ちるが、表記は入力が途切れただけで戻ってはいけない。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace AcManager.Pages.Gt {
    internal static class GtHints {
        // ---- 記号 ------------------------------------------------------------
        /// <summary>決定ボタンの記号（pad.txt の confirm=circle なら ○）。</summary>
        internal static string ConfirmGlyph {
            get { return GtPad.ConfirmIsCircle ? "○" : "×"; }
        }

        /// <summary>戻るボタンの記号（決定の逆）。</summary>
        internal static string BackGlyph {
            get { return GtPad.ConfirmIsCircle ? "×" : "○"; }
        }

        /// <summary>□（セッティング）。入替設定の対象外なので固定。</summary>
        internal const string SquareGlyph = "□";

        // ---- 置換 ------------------------------------------------------------
        // ★順番が意味を持つ。「〜をクリックで」→「クリックで」→「〜をクリック」→ 裸の「クリック」の順に
        //   広い規則から潰さないと、「時刻をクリックで次へ」が「時刻を×で次へ」になる。
        private static readonly Regex RxClickWith = new Regex(@"(?:\S+?を)?クリックで", RegexOptions.CultureInvariant);
        private static readonly Regex RxClickObj = new Regex(@"(\S+?)をクリック", RegexOptions.CultureInvariant);
        private static readonly Regex RxClickBare = new Regex(@"クリック", RegexOptions.CultureInvariant);
        private static readonly Regex RxEsc = new Regex(@"\bEsc\b", RegexOptions.CultureInvariant);
        private static readonly Regex RxEnter = new Regex(@"\bEnter\b", RegexOptions.CultureInvariant);
        private static readonly Regex RxSetting = new Regex(@"\bS(?=\s+セッティング)", RegexOptions.CultureInvariant);

        /// <summary>キーボード向けのヒント文字列を、パッド操作中なら記号表記に置き換えて返す。
        /// パッド中でなければそのまま返す（＝マウス／キーボードの見た目は今までどおり）。</summary>
        internal static string Format(string keyboardHint) {
            if (string.IsNullOrEmpty(keyboardHint)) return keyboardHint;
            if (!GtPad.PadMode) return keyboardHint;
            return ToPad(keyboardHint);
        }

        /// <summary>パッド中だけ別の文面を出したい時（機械置換だと同じ記号が 2 回出てしまう箇所）。
        /// padHint もキーボード表記で書く＝同じ置換規則を通すので、confirm=circle にもそのまま追従する。</summary>
        internal static string Format(string keyboardHint, string padHint) {
            if (!GtPad.PadMode) return keyboardHint;
            return string.IsNullOrEmpty(padHint) ? ToPad(keyboardHint) : ToPad(padHint);
        }

        private static string ToPad(string s) {
            try {
                var confirm = ConfirmGlyph;
                var back = BackGlyph;
                s = RxClickWith.Replace(s, confirm + " で");                    // 時刻をクリックで次へ → × で次へ
                s = RxClickObj.Replace(s, confirm + "  $1を選択");              // コースをクリック → ×  コースを選択
                s = RxClickBare.Replace(s, confirm);                            // クリック / Esc 閉じる → × / ○ 閉じる
                s = RxEsc.Replace(s, back);
                s = RxEnter.Replace(s, confirm);
                s = RxSetting.Replace(s, SquareGlyph);                          // S  セッティング → □  セッティング
                s = s.Replace("←", "◀").Replace("→", "▶");                    // ← → 30分 → ◀ ▶ 30分
                return s;
            } catch (Exception ex) {
                FirstFloor.ModernUI.Helpers.Logging.Warning("[GtHints] format: " + ex.Message);
                return s;
            }
        }

        // ---- 配線 ------------------------------------------------------------
        // 元のキーボード表記は TextBlock 自身に持たせる（Bind の呼び直し＝文面の差し替えだけで済む）。
        private static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
                "GtHintSource", typeof(string), typeof(GtHints), new PropertyMetadata(null));

        private static readonly DependencyProperty PadSourceProperty = DependencyProperty.RegisterAttached(
                "GtHintPadSource", typeof(string), typeof(GtHints), new PropertyMetadata(null));

        // Loaded/Unloaded を二重に張らないための印（Bind は ShowStep 毎に何度も呼ばれる）。
        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
                "GtHintHooked", typeof(bool), typeof(GtHints), new PropertyMetadata(false));

        // ★弱参照で持つ。ページを捨てても GtHints 側が TextBlock を掴んだままにならない。
        private static readonly List<WeakReference> Bound = new List<WeakReference>();
        private static bool _hooked;

        /// <summary>tb.Text をヒントとして管理する。パッド／キーボードが切り替わったら自動で書き換わる。</summary>
        internal static void Bind(TextBlock tb, string keyboardHint) {
            Bind(tb, keyboardHint, null);
        }

        /// <summary>padHint 付き。パッド中だけ別の文面（padHint もキーボード表記で書く）。</summary>
        internal static void Bind(TextBlock tb, string keyboardHint, string padHint) {
            if (tb == null) return;
            try {
                tb.SetValue(SourceProperty, keyboardHint);
                tb.SetValue(PadSourceProperty, padHint);
                tb.Text = Format(keyboardHint, padHint);
                Hook(tb);
                Add(tb);
            } catch (Exception ex) {
                FirstFloor.ModernUI.Helpers.Logging.Warning("[GtHints] bind: " + ex.Message);
            }
        }

        private static void Hook(TextBlock tb) {
            if (!_hooked) {
                _hooked = true;
                GtPad.PadModeChanged += OnPadModeChanged;
            }
            if ((bool)tb.GetValue(HookedProperty)) return;
            tb.SetValue(HookedProperty, true);
            tb.Loaded += OnBoundLoaded;
            tb.Unloaded += OnBoundUnloaded;
        }

        private static void Add(TextBlock tb) {
            for (var i = Bound.Count - 1; i >= 0; i--) {
                var t = Bound[i].Target as TextBlock;
                if (t == null) { Bound.RemoveAt(i); continue; }
                if (ReferenceEquals(t, tb)) return;
            }
            Bound.Add(new WeakReference(tb));
        }

        private static void Remove(TextBlock tb) {
            for (var i = Bound.Count - 1; i >= 0; i--) {
                var t = Bound[i].Target as TextBlock;
                if (t == null || ReferenceEquals(t, tb)) Bound.RemoveAt(i);
            }
        }

        // ページが戻ってくる（フレームの使い回し）ことがあるので、Loaded で購読し直す。
        private static void OnBoundLoaded(object sender, RoutedEventArgs e) {
            var tb = sender as TextBlock;
            if (tb == null) return;
            Add(tb);
            Refresh(tb);
        }

        private static void OnBoundUnloaded(object sender, RoutedEventArgs e) {
            var tb = sender as TextBlock;
            if (tb != null) Remove(tb);
        }

        private static void OnPadModeChanged() {
            for (var i = Bound.Count - 1; i >= 0; i--) {
                var tb = Bound[i].Target as TextBlock;
                if (tb == null) { Bound.RemoveAt(i); continue; }
                Refresh(tb);
            }
        }

        private static void Refresh(TextBlock tb) {
            try {
                var src = tb.GetValue(SourceProperty) as string;
                if (src == null) return;
                tb.Text = Format(src, tb.GetValue(PadSourceProperty) as string);
            } catch { }
        }
    }
}
