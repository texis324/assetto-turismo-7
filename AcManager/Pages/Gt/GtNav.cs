// [fork] Esc で必ず「一つ前の画面」へ戻る。ワールドマップが原点（そこでは戻らない）。
//
//   仕掛け:
//     ① GT ページに入るたび GtChrome.Enter() から NotePage() が呼ばれ、通ってきたページを Trail に積む
//     ② Esc は **Window の KeyDown（バブル・handledEventsToo=false）** で拾う
//        → ページ側が自前で Esc を処理していれば e.Handled が立っているのでここには来ない
//          （CourseEntry のステップ戻し / ダイアログ閉じ などはページ側の方が正しい）
//        → ページ側に Esc が無い画面（ガレージ・設定・車一覧など CM 本来のページ）だけをここで拾う
//     ③ 戻り先は Trail の1つ前。無ければ画面ごとの既定（最終的にワールドマップ）
//
//   ★ページの PreviewKeyDown だけに頼ると、フォーカスがページの外にある時に届かない。
//     ウィンドウで拾うのはその穴を塞ぐため。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AcManager.Pages.Windows;

namespace AcManager.Pages.Gt {
    internal static class GtNav {
        private const string WorldMapUri = "/Pages/Gt/WorldMap.xaml";
        private static bool _init;
        private static readonly List<string> Trail = new List<string>();

        public static void Init() {
            if (_init) return;
            _init = true;
            try {
                EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent,
                        new KeyEventHandler(OnKeyDown), false);
                // 音だけは Tunnel で先に拾う。ページ側が Esc を自分で処理する画面
                // （コースインのステップ戻し等）でも鳴らしたいので、ここでは Handled にしない
                EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent,
                        new KeyEventHandler(OnPreviewKeyDown), true);
            } catch { }
        }

        private static string CurrentUri {
            get { return (Application.Current?.MainWindow as MainWindow)?.CurrentSource?.OriginalString ?? ""; }
        }

        private static bool Same(string a, string b) {
            if (a == null || b == null) return false;
            // クエリ（?Track=… など）が違っても同じページなら同一とみなす
            var ia = a.IndexOf('?');
            var ib = b.IndexOf('?');
            var pa = ia < 0 ? a : a.Substring(0, ia);
            var pb = ib < 0 ? b : b.Substring(0, ib);
            return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Is(string uri, string page) {
            return uri.IndexOf(page, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>[fork] 履歴に残してはいけない一発コースインの印（Auto=1）を落とす。
        /// これを付けたまま履歴に積むと、Esc で戻った瞬間に CourseEntry の OnLoaded が
        /// また AC を起動してしまう（要望 2026-08-29「AC 終了後に Esc を押すとまたコースインする」）。
        /// 戻り先としては「コースイン画面をふつうに開いた状態」が正しいので、印だけ外す。</summary>
        private static string StripAuto(string uri) {
            if (string.IsNullOrEmpty(uri)) return uri;
            if (uri.IndexOf("Auto=1", StringComparison.OrdinalIgnoreCase) < 0) return uri;
            var i = uri.IndexOf('?');
            if (i < 0) return uri;
            var path = uri.Substring(0, i);
            var parts = uri.Substring(i + 1).Split('&')
                    .Where(p => !p.Equals("Auto=1", StringComparison.OrdinalIgnoreCase)).ToArray();
            return parts.Length == 0 ? path : path + "?" + string.Join("&", parts);
        }

        /// <summary>GT ページの Loaded（GtChrome.Enter）から呼ぶ。通ってきた順を覚える。</summary>
        public static void NotePage() {
            try {
                var uri = StripAuto(CurrentUri);
                if (GtInGameSettings.IsRestoringPage || GtInGameSettings.IsSessionActive && GtPreviewSettingsSession.IsSettingsPage(uri)) return;
                if (string.IsNullOrEmpty(uri)) return;
                if (Trail.Count > 0 && Same(Trail[Trail.Count - 1], uri)) {
                    Trail[Trail.Count - 1] = uri;   // クエリだけ更新
                    return;
                }
                Trail.Add(uri);
                if (Trail.Count > 24) Trail.RemoveAt(0);
            } catch { }
        }

        /// <summary>画面ごとの既定の戻り先（Trail が使えない時）。</summary>
        private static string Fallback(string uri) {
            if (Is(uri, "/Pages/Gt/WorldCircuits.xaml")) return WorldMapUri;
            if (Is(uri, "/Pages/Gt/RegionMap.xaml")) return "/Pages/Gt/WorldCircuits.xaml";
            if (Is(uri, "/Pages/Gt/CourseEntry.xaml")) {
                // 地域が分かればその地域マップへ戻す
                var q = "";
                var i = uri.IndexOf("Region=", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) {
                    var v = uri.Substring(i + 7);
                    var amp = v.IndexOf('&');
                    if (amp >= 0) v = v.Substring(0, amp);
                    q = "?Region=" + v;
                }
                return "/Pages/Gt/RegionMap.xaml" + q;
            }
            return WorldMapUri;   // それ以外はどこからでもワールドマップへ
        }

        /// <summary>一つ前へ戻る。ワールドマップに居る時は何もしない。</summary>
        public static bool GoBack() {
            try {
                if (GtInGameSettings.TryReturnToGame()) return true;
                var cur = CurrentUri;
                if (string.IsNullOrEmpty(cur)) return false;
                if (Is(cur, WorldMapUri) || Is(cur, TitleScreen.PagePath)) return true;   // 原点と起動タイトル。ここで止まる（true = 処理済み扱い）

                // Trail の末尾は今のページ。落としてから1つ前を見る
                while (Trail.Count > 0 && Same(Trail[Trail.Count - 1], cur)) {
                    Trail.RemoveAt(Trail.Count - 1);
                }
                var target = StripAuto(Trail.Count > 0 ? Trail[Trail.Count - 1] : Fallback(cur));
                WorldMap.Navigate(target);
                return true;
            } catch {
                return false;
            }
        }

        /// <summary>Esc の「戻る音」。処理そのものはページ側 / OnKeyDown に任せる。</summary>
        private static void OnPreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key != Key.Escape || e.IsRepeat) return;
            try {
                if (!ReferenceEquals(sender, Application.Current?.MainWindow)) return;
                var focused = Keyboard.FocusedElement;
                if (focused is TextBoxBase || focused is PasswordBox) return;
                if (Is(CurrentUri, WorldMapUri) || Is(CurrentUri, TitleScreen.PagePath)) return;   // 原点と起動タイトル。ここでは戻らないので鳴らさない
                GtSfx.PlayCancel();
            } catch { }
        }

        private static void OnKeyDown(object sender, KeyEventArgs e) {
            if (e.Handled || e.Key != Key.Escape) return;
            try {
                // 文字入力中は邪魔しない
                var focused = Keyboard.FocusedElement;
                if (focused is TextBoxBase || focused is PasswordBox || focused is ComboBox) return;
                // 別ウィンドウ（ダイアログ）が前に居る時はそちらに任せる
                if (!ReferenceEquals(sender, Application.Current?.MainWindow)) return;
                if (GoBack()) e.Handled = true;
            } catch { }
        }
    }
}
