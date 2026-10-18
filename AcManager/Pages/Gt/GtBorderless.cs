// [fork] GT7 ボーダーレス全画面。CM7 はゲーム機の画面のように、枠もタスクバーも無しで全面に出す
//   （要望 2026-08-30「カードがタスクバーと被る」「ボーダーレスウィンドウにしたい」）。
//   最大化は使わない: ModernWindow は SingleBorderWindow ＋ WindowChrome なので、
//   最大化するとタスクバーを避けた作業領域に収まってしまう。WindowStyle=None ＋ モニタ実寸配置なら
//   タスクバーの上まで覆う。F11 で通常ウィンドウとトグル（設定は ValuesStorage に残る）。
using System;
using System.Windows;
using System.Windows.Interop;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    internal static class GtBorderless {
        private const string KeyEnabled = "__GtBorderless";

        public static bool Enabled {
            get { return ValuesStorage.Get(KeyEnabled, true); }
            set { ValuesStorage.Set(KeyEnabled, value); }
        }

        /// <summary>今ウィンドウが乗っているモニタの実寸（WPF 単位）。DPI スケールも吸収する。</summary>
        internal static Rect ScreenBounds(Window w) {
            var handle = new WindowInteropHelper(w).Handle;
            var b = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
            var t = PresentationSource.FromVisual(w)?.CompositionTarget;
            if (t != null) {
                var tl = t.TransformFromDevice.Transform(new Point(b.Left, b.Top));
                var br = t.TransformFromDevice.Transform(new Point(b.Right, b.Bottom));
                return new Rect(tl, br);
            }
            return new Rect(b.Left, b.Top, b.Width, b.Height);
        }

        /// <summary>MainWindow の Loaded から呼ぶ。設定が ON ならボーダーレス化。</summary>
        public static void Apply(Window w) {
            if (!Enabled) return;
            try {
                w.WindowStyle = WindowStyle.None;
                w.ResizeMode = ResizeMode.NoResize;
                w.WindowState = WindowState.Normal;
                var r = ScreenBounds(w);
                w.Left = r.Left; w.Top = r.Top; w.Width = r.Width; w.Height = r.Height;
                Logging.Write("[GtBorderless] on: " + r);
            } catch (Exception ex) {
                Logging.Warning("[GtBorderless] " + ex.Message);
            }
        }

        /// <summary>F11。OFF に戻す時は画面中央の通常ウィンドウにする（元のサイズは覚えない＝要らない）。</summary>
        public static void Toggle(Window w) {
            Enabled = !Enabled;
            if (Enabled) { Apply(w); return; }
            try {
                w.WindowStyle = WindowStyle.SingleBorderWindow;
                w.ResizeMode = ResizeMode.CanResizeWithGrip;
                var r = ScreenBounds(w);
                w.Width = Math.Min(1600, r.Width - 80);
                w.Height = Math.Min(950, r.Height - 100);
                w.Left = r.Left + (r.Width - w.Width) / 2;
                w.Top = r.Top + (r.Height - w.Height) / 2;
                Logging.Write("[GtBorderless] off");
            } catch (Exception ex) {
                Logging.Warning("[GtBorderless] " + ex.Message);
            }
        }
    }
}
