// [fork] GT ページのパッド用フォーカス。
//   GT ページは全部「マウス専用」（Border/Grid + Cursor=Hand + MouseLeftButtonUp のコード生成部品）で、
//   WPF のフォーカス機構を使っていない。なのでフォーカスの実体を自前で持つ。
//
//   ページ側は Loaded / 画面再構築のたびに Register(owner, items, layout) を呼ぶだけ。
//   既存のマウス経路（MouseEnter/MouseLeave/MouseLeftButtonUp）は一切変えず、
//   同じラムダを SetHot / Activate として渡す（RegionMap の SetHot が手本）。
//
//   ★Register 直後に枠は出さない（マウスユーザーの画面に勝手に枠が出ないように）。
//     最初の方向入力で initial に枠が出る。本物のマウスが動いたら枠は一旦消える（dormant）。
using System;
using System.Collections.Generic;
using System.Windows;

namespace AcManager.Pages.Gt {
    /// <summary>[fork] パッドで選べる 1 個の候補。</summary>
    internal sealed class GtFocusItem {
        /// <summary>dump 用の名前。</summary>
        public string Name;

        /// <summary>画面座標（MainWindow 基準の論理 px）。要素が Loaded 前なら (0,0) でよい。</summary>
        public Func<Point> Center;

        /// <summary>[fork] 任意: 見えている枠（MainWindow 基準の論理 px）。あれば Spatial は「押した向きに一直線に並ぶ枠」を最優先する
        /// （横長のカードは中心だけだと斜めの判定がずれる）。null／空なら中心だけで決める。</summary>
        public Func<Rect> Bounds;

        /// <summary>true=フォーカス表現（ホバーと同じ見た目） / false=解除。</summary>
        public Action<bool> SetHot;

        /// <summary>× で起きること。</summary>
        public Action Activate;

        /// <summary>Activate chooses its own select/cancel sound; do not also play the default select.</summary>
        public bool HandlesActivationSound;

        /// <summary>任意。方向/決定より先に呼ばれ、true を返せば消費（時刻スライダの左右など）。</summary>
        public Func<GtPadCommand, bool> Custom;

        /// <summary>false なら候補から外す（無効なピル等）。</summary>
        public bool Enabled = true;
    }

    // [fork] Rows = Spatial の「左右は同じ段の中だけ」版。段ごとに幅の違うピルが並ぶ画面（モード選択）用。
    //   Spatial だと、上段の端で左右を押した時に「ほんの少し外側にある下段のピル」が候補になり、横を押したのに下へ落ちた。
    internal enum GtFocusLayout { Spatial, Row, Column, Grid, Rows }

    internal static class GtFocus {
        private static FrameworkElement _owner;
        private static IList<GtFocusItem> _items;
        private static GtFocusLayout _layout;
        private static int _columns;
        private static int _index;

        /// <summary>今フォーカス表現を出しているか。false = dormant（マウス操作中 or まだ一度も方向が来ていない）。</summary>
        private static bool _shown;

        // Once arrows have selected a GT item, Enter must activate that item before a page's
        // generic Enter shortcut (for example CourseEntry's direct Launch handler) sees it.
        internal static bool KeyboardNavigationActive { get { return _shown && IsLive(); } }

        /// <summary>owner ページが Loaded / 画面再構築のたびに呼ぶ。同じ owner の再登録は置き換え。</summary>
        public static void Register(FrameworkElement owner, IList<GtFocusItem> items, GtFocusLayout layout, int columns = 0, int initial = 0) {
            try {
                // ★パッドで操作中の再登録（ステップ遷移・オーバーレイ開閉・画面再構築）で枠が消えると、
                //   毎回「方向を 1 回空押し」しないと進めなくなる。枠が出ていた／今パッドで操作中なら出し直す。
                //   マウスしか使っていない時は従来どおり出さない（Register 直後に枠を出さない＝仕様 §2.2）。
                var keep = _shown || GtPad.PadDriving;
                HideCurrent();
                _owner = owner;
                _items = items;
                _layout = layout;
                _columns = columns > 0 ? columns : 0;
                _index = items == null || items.Count == 0 ? -1 : Clamp(initial, items.Count);
                _shown = false;
                if (keep && _index >= 0) {
                    SetHot(_index, true);
                    _shown = true;
                }
            } catch { }
        }

        /// <summary>Unloaded で呼ぶ（owner が一致する時だけ消す）。</summary>
        public static void Clear(FrameworkElement owner) {
            if (ReferenceEquals(_pageCommandOwner, owner)) {
                _pageCommandOwner = null;
                _pageCommand = null;
            }
            if (!ReferenceEquals(_owner, owner)) return;
            HideCurrent();
            _owner = null;
            _items = null;
            _index = -1;
            _shown = false;
        }

        // ---- ページ共通のコマンド口 --------------------------------------------
        // [fork] 「どの候補に居ても効く」ボタン（CourseEntry の □＝セッティング開閉）のための 1 口。
        //   候補の Custom が消費しなかった時にだけ呼ばれるので、既存の Custom は書き換えなくてよい。
        private static FrameworkElement _pageCommandOwner;
        private static Func<GtPadCommand, bool> _pageCommand;

        /// <summary>[fork] owner ページ共通のコマンド受け口を張る（Register の直後に呼ぶ）。
        /// 今 Register されている owner と一致する時しか呼ばれないので、ページを出た後の取りこぼしは無い。</summary>
        public static void SetPageCommand(FrameworkElement owner, Func<GtPadCommand, bool> handler) {
            _pageCommandOwner = owner;
            _pageCommand = handler;
        }

        /// <summary>ページ側から現在位置を指定（ステップ遷移後の初期位置など）。</summary>
        public static void SetIndex(int index) {
            if (_items == null || _items.Count == 0) return;
            var next = Clamp(index, _items.Count);
            if (next == _index) return;
            if (_shown) SetHot(_index, false);
            _index = next;
            if (_shown) SetHot(_index, true);
        }

        /// <summary>本物のマウスが動いた: フォーカス表現を一旦消す。次の方向入力で同じ index から再開。</summary>
        public static void OnRealMouseMove() {
            if (!_shown) return;
            SetHot(_index, false);
            _shown = false;
        }

        public static string Dump() {
            try {
                if (_owner == null || _items == null) return "focus=(none)";
                var name = _index >= 0 && _index < _items.Count ? (_items[_index].Name ?? "?") : "-";
                return "focus=owner:" + _owner.GetType().Name + " items=" + _items.Count
                        + " index=" + _index + " name=" + name + " dormant=" + (_shown ? "0" : "1");
            } catch {
                return "focus=(error)";
            }
        }

        /// <summary>GtPad から。消費したら true。</summary>
        public static bool Handle(GtPadCommand cmd) {
            try {
                if (!IsLive()) return false;
                if (cmd == GtPadCommand.Back) return false;   // ★Back は常に Escape のキー合成に任せる

                var dir = IsDirection(cmd);

                // dormant 中の方向入力は「枠を出すだけ」で移動しない
                if (dir && !_shown) {
                    if (_index < 0) _index = FirstEnabled();
                    if (_index < 0) return false;
                    SetHot(_index, true);
                    _shown = true;
                    return true;
                }

                var cur = Current();
                if (cur != null && cur.Custom != null) {
                    bool consumed;
                    try {
                        consumed = cur.Custom(cmd);
                    } catch {
                        consumed = false;
                    }
                    if (consumed) return true;
                }

                if (dir) {
                    var next = Move(cmd);
                    if (next >= 0 && next != _index) {
                        SetHot(_index, false);
                        _index = next;
                        SetHot(_index, true);
                        GtSfx.PlayMove();
                    }
                    return true;   // 端では動かない（ラップしない）が、パッドの入力としては消費する
                }

                if (cmd == GtPadCommand.Confirm) {
                    // ★枠が出ていない時は決定せず「枠を出すだけ」で消費する。
                    //   ここで false を返すと GtPad が Key.Enter を合成し、裏の画面（Go ステップ＝コースイン）に落ちる。
                    if (!_shown) {
                        if (_index < 0) _index = FirstEnabled();
                        if (_index < 0) return false;
                        SetHot(_index, true);
                        _shown = true;
                        return true;
                    }
                    if (cur == null || !cur.Enabled || cur.Activate == null) return false;
                    if (!cur.HandlesActivationSound) GtSfx.PlaySelect();
                    try {
                        cur.Activate();
                    } catch (Exception ex) {
                        FirstFloor.ModernUI.Helpers.Logging.Warning("[GtFocus] activate: " + ex.Message);
                    }
                    return true;
                }

                // [fork] 候補が消費しなかったコマンドは owner ページの共通口へ（□＝セッティング開閉など）
                if (_pageCommand != null && ReferenceEquals(_pageCommandOwner, _owner)) {
                    try {
                        if (_pageCommand(cmd)) return true;
                    } catch (Exception ex) {
                        FirstFloor.ModernUI.Helpers.Logging.Warning("[GtFocus] page command: " + ex.Message);
                    }
                }

                return false;   // Start/Select/L1/R1/△/L3/R3 は GtPad 側に返す
            } catch {
                return false;
            }
        }

        // ---- 内部 ------------------------------------------------------------
        private static bool IsLive() {
            if (_owner == null || _items == null || _items.Count == 0) return false;
            if (!_owner.IsLoaded) return false;                                  // Navigate 後は死んでいる
            if (PresentationSource.FromVisual(_owner) == null) return false;
            return true;
        }

        private static GtFocusItem Current() {
            return _index >= 0 && _index < _items.Count ? _items[_index] : null;
        }

        private static int Clamp(int i, int count) {
            if (count <= 0) return -1;
            if (i < 0) return 0;
            return i >= count ? count - 1 : i;
        }

        private static int FirstEnabled() {
            for (var i = 0; i < _items.Count; i++) {
                if (_items[i].Enabled) return i;
            }
            return -1;
        }

        private static void HideCurrent() {
            if (!_shown) return;
            SetHot(_index, false);
            _shown = false;
        }

        private static void SetHot(int index, bool hot) {
            try {
                if (_items == null || index < 0 || index >= _items.Count) return;
                var h = _items[index].SetHot;
                if (h != null) h(hot);
            } catch { }
        }

        private static bool IsDirection(GtPadCommand cmd) {
            return cmd == GtPadCommand.Up || cmd == GtPadCommand.Down
                    || cmd == GtPadCommand.Left || cmd == GtPadCommand.Right;
        }

        private static Rect BoundsOf(int i) {
            try {
                var f = _items[i].Bounds;
                return f == null ? Rect.Empty : f();
            } catch {
                return Rect.Empty;
            }
        }

        private static Point CenterOf(int i) {
            try {
                var f = _items[i].Center;
                return f == null ? new Point(0, 0) : f();
            } catch {
                return new Point(0, 0);
            }
        }

        /// <summary>移動先の index（無ければ -1）。</summary>
        private static int Move(GtPadCommand cmd) {
            switch (_layout) {
                case GtFocusLayout.Row:
                    if (cmd == GtPadCommand.Left) return Step(-1);
                    if (cmd == GtPadCommand.Right) return Step(1);
                    return -1;                       // 上下は無視
                case GtFocusLayout.Column:
                    if (cmd == GtPadCommand.Up) return Step(-1);
                    if (cmd == GtPadCommand.Down) return Step(1);
                    return -1;                       // 左右は無視
                case GtFocusLayout.Grid: {
                    var cols = _columns > 0 ? _columns : 1;
                    if (cmd == GtPadCommand.Left) return Step(-1);
                    if (cmd == GtPadCommand.Right) return Step(1);
                    return Step(cmd == GtPadCommand.Up ? -cols : cols);
                }
                case GtFocusLayout.Rows:
                    return MoveSpatial(cmd, true);
                default:
                    return MoveSpatial(cmd);
            }
        }

        /// <summary>[fork] Rows で「同じ段」とみなす縦ズレの上限（DIP）。同じ段のピルは中心の高さがほぼ揃う（高さの違いで数 px）。
        /// 段の間隔は 90 px 以上あるので、その手前で切る。</summary>
        private const double SameRowTolerance = 40.0;

        /// <summary>index を step ずつ動かし、Enabled な候補に着いたらそこ。端で止まる（ラップしない）。</summary>
        private static int Step(int step) {
            var i = _index;
            for (var guard = 0; guard < _items.Count + 2; guard++) {
                i += step;
                if (i < 0 || i >= _items.Count) return -1;
                if (_items[i].Enabled) return i;
            }
            return -1;
        }

        /// <summary>方向 d の半平面（内積 &gt; 0）にある候補のうち score = 距離 + 2.0 × 直交ズレ が最小のもの。</summary>
        private static int MoveSpatial(GtPadCommand cmd, bool sameRowOnly = false) {
            double dx = 0, dy = 0;
            switch (cmd) {
                case GtPadCommand.Up: dy = -1; break;
                case GtPadCommand.Down: dy = 1; break;
                case GtPadCommand.Left: dx = -1; break;
                default: dx = 1; break;
            }

            var c = CenterOf(_index);
            var best = -1;
            var bestScore = double.MaxValue;

            // [fork] 枠がある画面（地域マップのカード）は、まず「押した向きに一直線上にある枠」＝直交方向に重なる枠の中で
            //   一番すき間が近いものへ。カードは横に長いので、中心どうしの角度だと真上のカードが扇から外れ、遠くの枠へ飛んだ
            //   （要望 2026-10-15「トレントボンドーネから上でニュルブルクリンクGPまで飛ぶ」→ 真上のグロースグロックナーへ）
            var a = BoundsOf(_index);
            if (!a.IsEmpty && !sameRowOnly) {
                for (var i = 0; i < _items.Count; i++) {
                    if (i == _index || !_items[i].Enabled) continue;
                    var b = BoundsOf(i);
                    if (b.IsEmpty) continue;
                    var p = CenterOf(i);
                    var vx = p.X - c.X;
                    var vy = p.Y - c.Y;
                    if (vx * dx + vy * dy <= 0) continue;
                    double gap, overlap, own;
                    if (dy != 0) {
                        gap = dy < 0 ? a.Top - b.Bottom : b.Top - a.Bottom;
                        overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                        own = a.Height;
                    } else {
                        gap = dx < 0 ? a.Left - b.Right : b.Left - a.Right;
                        overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                        own = a.Width;
                    }
                    if (overlap <= 0 || gap < -0.5 * own) continue;   // 一直線上に無い／ほぼ真横に並んでいる
                    var score = Math.Max(gap, 0) + 0.05 * Math.Sqrt(vx * vx + vy * vy);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = i;
                }
                if (best >= 0) return best;
            }

            // [fork] まず押した向きの ±45° の扇の中だけで探し、無い時だけ半平面全体へ広げる。
            //   半平面だけだと「ほんの少し右・ずっと下」の近い候補が「真横の少し遠い候補」に勝ち、右を押したのに下へ飛んだ
            //   （要望 2026-10-15「ヨーロッパのコース選択、十字キーで上行ったり下行ったり選びにくい」）
            for (var pass = 0; pass < 2 && best < 0; pass++) {
                for (var i = 0; i < _items.Count; i++) {
                    if (i == _index || !_items[i].Enabled) continue;
                    var p = CenterOf(i);
                    if (p.X == 0 && p.Y == 0) continue;         // まだ配置されていない要素は候補にしない
                    var vx = p.X - c.X;
                    var vy = p.Y - c.Y;
                    var proj = vx * dx + vy * dy;
                    if (proj <= 0) continue;
                    // [fork] Rows: 左右の移動は同じ段の中だけ（上下の移動は従来どおり斜めの段へも行ける）
                    if (sameRowOnly && dy == 0 && Math.Abs(vy) > SameRowTolerance) continue;
                    var ortho = Math.Abs(vx * dy - vy * dx);    // 直交成分（2D なので外積の絶対値でよい）
                    if (pass == 0 && ortho > proj) continue;    // 扇の外
                    var score = Math.Sqrt(vx * vx + vy * vy) + 2.0 * ortho;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = i;
                }
            }

            // 現在位置がまだ (0,0)（＝Loaded 前）だと空間探索が使えない。その時だけ index±1 で凌ぐ
            if (best < 0 && c.X == 0 && c.Y == 0) {
                return Step(cmd == GtPadCommand.Left || cmd == GtPadCommand.Up ? -1 : 1);
            }
            return best;
        }
    }
}
