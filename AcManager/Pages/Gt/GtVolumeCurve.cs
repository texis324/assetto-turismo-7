// [fork] 音量スライダーの目盛り ↔ 実ゲインの変換（聴こえ方に合わせた対数カーブ）。
//   要望 2026-09-12「クリック音もムービー音量も、ちょうどいいのが 2% とかなので調節に余裕を持たせたい」
//   → 直線（p/100）だと下の方に全部が詰まる。ここでは 0%=消音、1〜100% を −59.4〜0 dB に等間隔（1% = 0.6 dB）で割り当てる。
//   旧・直線 2%（−34 dB）は新目盛りで約 43%、50% = −30 dB、100% = 従来の 100% と同じ最大。
using System;

namespace AcManager.Pages.Gt {
    internal static class GtVolumeCurve {
        public const double RangeDb = 60.0;   // 1% あたり 0.6 dB

        /// <summary>目盛り（0〜100）→ MediaPlayer/MediaElement の Volume（0〜1・振幅）。</summary>
        public static double ToGain(int percent) {
            percent = Math.Max(0, Math.Min(100, percent));
            if (percent == 0) return 0;
            var db = -RangeDb + RangeDb * percent / 100.0;
            return Math.Max(0, Math.Min(1, Math.Pow(10, db / 20.0)));
        }

        /// <summary>Volume（0〜1）→ 目盛り。旧・直線保存値の移行に使う。</summary>
        public static int FromGain(double gain) {
            if (double.IsNaN(gain) || gain <= 0) return 0;
            gain = Math.Min(1, gain);
            var db = 20.0 * Math.Log10(gain);
            var percent = (int)Math.Round((db + RangeDb) / RangeDb * 100.0);
            return Math.Max(1, Math.Min(100, percent));
        }
    }
}
