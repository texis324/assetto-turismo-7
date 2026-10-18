// [fork] GT7 風ページ共通クローム制御:
//   GT ページの間だけ ①ModernMenu（ページ見出し行）②ウィンドウのタイトルバー(PART_TitleBar)③戻るボタン を隠し、
//   ④コンテンツ余白(LayoutRoot の上35px)も詰めて全面表示にする（要望 2026-08-25「ヘッダーより上の帯が要らない」）。
//   BGMコントロール(CmJaOverlay の CmJaBgmPanel)は GtHeader が自分の BgmHost へ引っ越しさせ、
//   ページを付け替える間も参照を保持し、次のヘッダーへ引き継ぐ。
//   ページ遷移時は「新ページ Loaded → 旧ページ Unloaded」の順になることがあるので、Unloaded 側は即座に戻さず
//   少し後に「今のページが GT ページか」を見てから判断する。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AcManager.Pages.Windows;
using FirstFloor.ModernUI.Windows.Controls;
using FirstFloor.ModernUI.Windows.Media;

namespace AcManager.Pages.Gt {
    internal static class GtChrome {
        private static StackPanel _bgmPanel;
        private static Window Main => Application.Current?.MainWindow;
        private static ModernMenu Menu => Main?.FindVisualChild<ModernMenu>();

        private static bool IsGtPage() {
            var src = (Main as MainWindow)?.CurrentSource?.OriginalString;
            return src != null && src.IndexOf("/Pages/Gt/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static T FindByName<T>(DependencyObject root, string name) where T : FrameworkElement {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++) {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T fe && fe.Name == name) return fe;
                var r = FindByName<T>(child, name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>CmJaOverlay の BGM コントロール（タイトルバー常駐の StackPanel）。DLL 無し/旧版なら null。</summary>
        internal static StackPanel FindBgmPanel() {
            try {
                var found = FindByName<StackPanel>(Main, "CmJaBgmPanel");
                if (found != null) _bgmPanel = found;
                return _bgmPanel;
            } catch {
                return null;
            }
        }

        /// <summary>BGM コントロールを host へ引っ越しさせる（GtHeader / WorldMap から）。</summary>
        internal static void AdoptBgmInto(Panel host) {
            try {
                var bgm = FindBgmPanel();
                if (bgm == null || host == null) return;
                // [fork] BGM の音量スライダー／ボタンにキーボードフォーカスを持たせない。パッドの候補が無い瞬間に十字キーが
                //   矢印キーとしてスライダーへ流れ、「十字キーが BGM 音量しか動かさない」状態になった（要望 2026-10-15）。マウスはそのまま使える
                foreach (var child in bgm.Children) {
                    if (child is Control c) c.Focusable = false;
                }
                var parent = VisualTreeHelper.GetParent(bgm) as Panel;
                if (ReferenceEquals(parent, host)) return;
                parent?.Children.Remove(bgm);
                host.Children.Add(bgm);
            } catch { }
        }

        /// <summary>GT ページの Loaded で呼ぶ。</summary>
        public static void Enter() {
            try {
                GtSfx.Init();   // [fork] メニュー操作音（初回だけ効く）
                GtNav.Init();   // [fork] Esc で一つ前へ戻る（初回だけ効く）
                GtPad.Init();   // [fork] DS4（XInput）で操作する（初回だけ効く）
                GtDisplayMode.Init();
                GtUniversalInput.Init();
                GtDrivingInput.Init();
                GtNav.NotePage();
                var m = Menu;
                if (m != null) m.Visibility = Visibility.Collapsed;
                var tb = FindByName<DockPanel>(Main, "PART_TitleBar");
                if (tb != null) tb.Visibility = Visibility.Collapsed;
                var bb = FindByName<Button>(Main, "WindowBackButton");
                if (bb != null) bb.Visibility = Visibility.Collapsed;
                var lr = FindByName<DockPanel>(Main, "LayoutRoot");
                if (lr != null) lr.Margin = new Thickness(0);
            } catch { }
        }

        /// <summary>GT ページの Unloaded で呼ぶ。
        /// [fork] 原則「GT7にあるかどうか」(要望 2026-08-25): 従来ヘッダー(ModernMenu/タイトルバー)はGT7に無いので
        /// 非GTページでも復元しない=常時GT仕様。従来ページで詰まないよう戻るボタン(←)だけ復元する。</summary>
        public static void Leave() {
            var d = Application.Current?.Dispatcher;
            if (d == null) return;
            d.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                try {
                    if (IsGtPage()) return;
                    var bb = FindByName<Button>(Main, "WindowBackButton");
                    if (bb != null) bb.ClearValue(UIElement.VisibilityProperty);
                } catch { }
            }));
        }
    }
}
