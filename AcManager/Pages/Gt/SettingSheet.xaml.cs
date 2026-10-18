// [fork] GT7風「カーセッティング」シート — GT7実機リファレンス(gt7-ref/car_settings)準拠。
//   左=車情報＋基本性能＋シート一覧(GT7左パネル構造・#16191E→#000グラデ)、右=セッティング面(#282830)。
//   指紋: 中央寄せ＋左右ヘアラインの見出し / 黒い入力フィールド(値は右寄せ白) / 単位はフィールド外左に小さく / 枠線は使わない。
//   URI: /Pages/Gt/SettingSheet.xaml?CarId=<id>（省略時は現在の車）。Esc でワールドマップへ。
//   データ層は CM 既製(CarSetupsManager / CarSetupValues / CarSetupEntry)を再利用。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AcManager.Pages.Selected;
using AcManager.Pages.Windows;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using AcTools.DataFile;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows;

namespace AcManager.Pages.Gt {
    public partial class SettingSheet : IParametrizedUriContent {
        private CarObject _car;
        private CarSetupsManager _setupsManager;
        private CarSetupObject _setup;
        private CarSetupValues _values;

        // [fork] パッド（GtPad→GtFocus）用。左パネル（シート一覧）と右パネル（セッティングの行）を別々に持ち、
        //   左右キーでどちらを登録するかを切り替える。位置はパネルごとに覚えておく。
        private List<GtFocusItem> _leftFocus = new List<GtFocusItem>();
        private List<GtFocusItem> _rightFocus = new List<GtFocusItem>();
        private int _leftIndex, _rightIndex;
        private bool _focusOnRight;
        private bool _gone;   // Unloaded 済み（async の戻りで登録し直さないため）

        public SettingSheet() {
            InitializeComponent();
            GtHints.Bind(HintBack, "Esc  戻る");   // [fork] パッド中は ○ 表記
            Loaded += (s, e) => { Focus(); GtChrome.Enter(); };
            Unloaded += (s, e) => {
                _gone = true;
                GtFocus.Clear(this);   // [fork] パッドのフォーカス候補を手放す
                GtChrome.Leave();
                _values?.Dispose();
                _values = null;
            };
        }

        #region Pad focus (GtFocus)
        /// <summary>[fork] MainWindow 基準の中心座標。まだ配置されていない要素は (0,0)。</summary>
        private static Point PadCenter(FrameworkElement e) {
            try {
                if (e == null || !e.IsLoaded || e.ActualWidth <= 0 || e.ActualHeight <= 0) return new Point(0, 0);
                var main = Application.Current?.MainWindow;
                if (main == null) return new Point(0, 0);
                return e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), main);
            } catch {
                return new Point(0, 0);
            }
        }

        /// <summary>[fork] パッドの候補を左パネル／右パネルのどちらかに差し替える。
        /// ★ページを出た後（async の戻りなど）は登録しない。次のページが入れた候補を奪ってしまうため。</summary>
        private void RegisterFocus(bool right) {
            if (_gone) return;
            if (right && _rightFocus.Count == 0) right = false;
            _focusOnRight = right;
            var items = right ? _rightFocus : _leftFocus;
            var index = right ? _rightIndex : _leftIndex;
            if (index < 0 || index >= items.Count) index = 0;
            GtFocus.Register(this, items, GtFocusLayout.Column, 0, index);
        }

        /// <summary>[fork] 左右キーでパネルを行き来する。</summary>
        private void SwitchPanel(bool right) {
            var items = right ? _rightFocus : _leftFocus;
            if (items.Count == 0 || right == _focusOnRight) return;
            RegisterFocus(right);
            // [fork] パッド操作中の Register は GtFocus 側が枠を出し直す（以前はここで Handle(Down) して出していた）
            GtSfx.PlayMove();
        }

        /// <summary>[fork] 左パネルの項目: 右で右パネルへ。左は端なので消費だけ（上下は Column が処理）。</summary>
        private bool PanelSwitchFromLeft(GtPadCommand cmd) {
            if (cmd == GtPadCommand.Right) { SwitchPanel(true); return true; }
            return cmd == GtPadCommand.Left;
        }

        /// <summary>[fork] 右パネルの行（値を持たないもの）: 左で左パネルへ。右は端なので消費だけ。L1 でも左へ。</summary>
        private bool PanelSwitchFromRight(GtPadCommand cmd) {
            if (cmd == GtPadCommand.Left || cmd == GtPadCommand.L1) { SwitchPanel(false); return true; }
            return cmd == GtPadCommand.Right;
        }

        /// <summary>[fork] 値を持つ行の左右は −/＋（CourseEntry のレース設定行と同じ設計）。
        /// 左パネルへ戻る操作は L1 に移す（左右を値の増減に使ってしまうため）。
        /// ★AC のセットアップはフロント/リアが別項目だが、1 行にまとめてあるのでパッドでは前後同値で動かす
        ///   （前後を別々に決めたい時はマウスで −/＋ を押す）。</summary>
        private Func<GtPadCommand, bool> RightStepCustom(Action<int> step) {
            return cmd => {
                if (step == null) return PanelSwitchFromRight(cmd);
                if (cmd == GtPadCommand.L1) { SwitchPanel(false); return true; }
                if (cmd == GtPadCommand.Left) { step(-1); GtSfx.PlayMove(); return true; }
                if (cmd == GtPadCommand.Right) { step(1); GtSfx.PlayMove(); return true; }
                return cmd == GtPadCommand.Confirm;   // × は消費だけ（Enter 合成に落とさない）
            };
        }

        /// <summary>[fork] 右パネルの行をパッドの候補に足す（見た目はホバーと同じ白 1px 枠）。
        /// step != null なら左右で値の −/＋。</summary>
        private void AddRightFocus(string name, Border row, Action activate, Action<int> step) {
            var index = _rightFocus.Count;
            _rightFocus.Add(new GtFocusItem {
                Name = name,
                Center = () => PadCenter(row),
                SetHot = on => {
                    row.BorderBrush = on ? Brushes.White : Brushes.Transparent;
                    if (!on) return;
                    _rightIndex = index;
                    EnsureVisible(row);
                },
                Activate = activate,
                Custom = RightStepCustom(step)
            });
        }

        /// <summary>[fork] 行が右パネルの表示範囲の外なら ScrollViewer を動かす（BringIntoView 相当）。</summary>
        private void EnsureVisible(FrameworkElement row) {
            try {
                if (row == null || !row.IsLoaded) return;
                var sv = FindScrollViewer(SheetPanel);
                if (sv == null || sv.ViewportHeight <= 0) return;
                var top = row.TranslatePoint(new Point(0, 0), sv).Y;
                var bottom = top + row.ActualHeight;
                if (top < 0) {
                    sv.ScrollToVerticalOffset(sv.VerticalOffset + top - 12);
                } else if (bottom > sv.ViewportHeight) {
                    sv.ScrollToVerticalOffset(sv.VerticalOffset + (bottom - sv.ViewportHeight) + 12);
                }
            } catch { }
        }

        /// <summary>[fork] XAML の ScrollViewer には名前が無いので、視覚ツリーを上に辿って探す。</summary>
        private static ScrollViewer FindScrollViewer(DependencyObject from) {
            var d = from;
            while (d != null) {
                var sv = d as ScrollViewer;
                if (sv != null) return sv;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }
        #endregion

        public void OnUri(Uri uri) {
            var carId = uri.GetQueryParam("CarId");
            _car = (string.IsNullOrEmpty(carId) ? null : CarsManager.Instance.GetById(carId)) ?? WorldMap.FindCurrentCar();
            if (_car == null) return;

            LeftBrand.Text = _car.Brand ?? "";
            LeftCarName.Text = _car.DisplayName;
            try {
                if (!string.IsNullOrEmpty(_car.BrandBadge) && System.IO.File.Exists(_car.BrandBadge)) {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(_car.BrandBadge, UriKind.Absolute);
                    bi.DecodePixelWidth = 64;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    BrandBadgeImg.Source = bi;
                }
            } catch { }

            BuildLeftSpecs();
            LoadSetupsAsync();
        }

        /// <summary>左パネル「基本性能」: PP/出力/トルク/重量/駆動形式（GT7 ref_1 の第1列と同構成）。</summary>
        private void BuildLeftSpecs() {
            LeftSpecPanel.Children.Clear();
            var pp = ComputePp(_car);
            if (pp > 0) LeftSpecPanel.Children.Add(SpecRow("パフォーマンスポイント", pp.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), "PP"));
            // [fork] チューニングショップのパーツ込みの値（元の単位のまま返るので既存の換算がそのまま効く）
            var bhp = GtTuning.SpecsBhp(_car);
            AddSpec("最高出力", GtHeader.PowerPs(bhp) ?? bhp);
            AddSpec("最大トルク", GtTuning.SpecsTorque(_car));
            AddSpec("車両重量", GtTuning.SpecsWeight(_car));
            var tuned = GtTuning.Compute(_car);
            if (tuned.Any && tuned.Bhp > 0) {
                var ps = (bhp ?? "").IndexOf("hp", StringComparison.OrdinalIgnoreCase) >= 0 ? tuned.Bhp * 1.0139 : tuned.Bhp;
                AddSpec("パワーウェイトレシオ", (tuned.Kg / ps).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " kg/PS");
            } else {
                AddSpec("パワーウェイトレシオ", _car.SpecsPwRatio);
            }
            var dt = Drivetrain(_car);
            if (!string.IsNullOrEmpty(dt)) LeftSpecPanel.Children.Add(SpecRow("駆動形式", dt, null));
        }

        private void AddSpec(string label, string raw) {
            if (string.IsNullOrEmpty(raw)) return;
            raw = raw.Trim();
            if (raw.Length == 0 || raw == "-" || raw == "?") return;
            // "420 bhp" → 値/単位を分けて GT7 流（値=白大・単位=小さく薄く）に
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^([\d.,+]+)\s*(.*)$");
            if (m.Success && m.Groups[1].Value.Length > 0) {
                LeftSpecPanel.Children.Add(SpecRow(label, m.Groups[1].Value, m.Groups[2].Value));
            } else {
                LeftSpecPanel.Children.Add(SpecRow(label, raw, null));
            }
        }

        private FrameworkElement SpecRow(string label, string value, string unit) {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("GtLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(l, 0);
            g.Children.Add(l);
            var vp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (unit == "PP") {
                vp.Children.Add(new TextBlock { Text = "PP ", FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
                unit = null;
            }
            vp.Children.Add(new TextBlock {
                Text = value, FontSize = 14.5, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("GtNum"),
                Foreground = (Brush)FindResource("GtValue")
            });
            if (!string.IsNullOrEmpty(unit)) {
                vp.Children.Add(new TextBlock { Text = " " + unit, FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
            }
            Grid.SetColumn(vp, 1);
            g.Children.Add(vp);
            return g;
        }

        private string Drivetrain(CarObject car) {
            try {
                var t = car.AcdData?.GetIniFile("drivetrain.ini")["TRACTION"].GetPossiblyEmpty("TYPE")?.ToUpperInvariant();
                if (t == "FWD") return "FF";
                if (t == "RWD") return "FR";
                if (t == "AWD" || t == "4WD") return "4WD";
                return t;
            } catch {
                return null;
            }
        }

        private async void LoadSetupsAsync() {
            try {
                _setupsManager = CarSetupsManager.Create(_car);
                await _setupsManager.EnsureLoadedAsync();

                var items = _setupsManager.Loaded.OfType<CarSetupObject>()
                        .OrderBy(x => x.Track == null ? 0 : 1)
                        .ThenBy(x => x.Track?.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                SetupList.Items.Clear();
                _leftFocus = new List<GtFocusItem>();   // [fork] パッド: 左パネルの候補を作り直す
                foreach (var s in items) {
                    Border focusBox;
                    var lbi = new ListBoxItem { Content = BuildSetupRow(s, out focusBox), Tag = s };
                    SetupList.Items.Add(lbi);
                    var index = _leftFocus.Count;
                    var item = lbi;
                    var box = focusBox;
                    _leftFocus.Add(new GtFocusItem {
                        Name = s.DisplayName,
                        Center = () => PadCenter(box),
                        SetHot = on => {
                            box.BorderBrush = on ? Brushes.White : Brushes.Transparent;
                            if (!on) return;
                            _leftIndex = index;
                            try { SetupList.ScrollIntoView(item); } catch { }
                        },
                        Activate = () => {
                            // 選択が変わると BuildSheet が Register し直す（枠は GtFocus 側が出し直す）
                            if (ReferenceEquals(SetupList.SelectedItem, item)) return;
                            SetupList.SelectedItem = item;
                        },
                        Custom = PanelSwitchFromLeft
                    });
                }

                if (items.Count == 0) {
                    NoSetups.Visibility = Visibility.Visible;
                    BuildSheet();   // [fork] セットアップが無くてもチューニングパーツ（リストリクター/バラスト）の行は出す
                } else {
                    SetupList.SelectedIndex = 0;   // → OnSetupSelected → BuildSheet → RegisterFocus
                }
                if (!_focusOnRight) RegisterFocus(false);
            } catch (Exception e) {
                Logging.Warning("[SettingSheet] load: " + e);
            }
        }

        /// <summary>[fork] focusBox = パッドのフォーカス枠（マウスのホバー/選択表現は ItemContainerStyle 側のまま）。
        /// 枠の分の 1px は常に確保してあるので、フォーカスしてもレイアウトはずれない。</summary>
        private FrameworkElement BuildSetupRow(CarSetupObject setup, out Border focusBox) {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock {
                Text = setup.DisplayName, FontSize = 13, Foreground = (Brush)FindResource("GtValue"), TextTrimming = TextTrimming.CharacterEllipsis
            });
            sp.Children.Add(new TextBlock {
                Text = setup.Track?.DisplayName ?? "汎用（全コース）", FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"), Margin = new Thickness(0, 2, 0, 0)
            });
            focusBox = new Border {
                Child = sp, CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent
            };
            return focusBox;
        }

        private void OnSetupSelected(object sender, SelectionChangedEventArgs e) {
            var item = SetupList.SelectedItem as ListBoxItem;
            var setup = item?.Tag as CarSetupObject;
            if (setup == null) return;
            _values?.Dispose();
            _setup = setup;
            _values = new CarSetupValues(_car, setup, null);
            SaveButton.Visibility = Visibility.Visible;
            SheetPill.Visibility = Visibility.Visible;
            SheetPillText.Text = setup.DisplayName;
            BuildSheet();
        }

        private void BuildSheet() {
            SheetPanel.Children.Clear();
            _rightFocus = new List<GtFocusItem>();   // [fork] パッド: 右パネルの候補を作り直す
            BuildTuningRows();   // [fork] チューニングショップで付けたリストリクター/バラストの値（GT7 はセッティングシートで決める）
            if (_values?.Tabs == null) {
                RegisterFocus(false);
                return;
            }

            // タイヤコンパウンド（GT7流: 黒フィールドのチップ、選択中は白1px枠）
            if (_values.Tyres != null && _values.Tyres.Length > 0) {
                SheetPanel.Children.Add(SectionHeader("タイヤ"));
                var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var t in _values.Tyres) {
                    var selected = Equals(_values.SelectedTyres, t);
                    var chip = new Border {
                        CornerRadius = new CornerRadius(3), Padding = new Thickness(18, 6, 18, 7), Margin = new Thickness(4, 0, 4, 8),
                        Background = (Brush)FindResource("GtField"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                        BorderBrush = selected ? (Brush)FindResource("GtValue") : Brushes.Transparent,
                        Child = new TextBlock {
                            Text = t.DisplayName, FontSize = 12.5,
                            Foreground = selected ? (Brush)FindResource("GtValue") : (Brush)FindResource("GtLabel")
                        }
                    };
                    var captured = t;
                    chip.MouseLeftButtonUp += (s, e2) => { _values.SelectedTyres = captured; BuildSheet(); };
                    row.Children.Add(chip);
                    // [fork] パッド: チップも候補に入れる（選択中の白枠は SetHot(false) で戻す）
                    var chipIndex = _rightFocus.Count;
                    var idle = selected ? (Brush)FindResource("GtValue") : Brushes.Transparent;
                    _rightFocus.Add(new GtFocusItem {
                        Name = t.DisplayName,
                        Center = () => PadCenter(chip),
                        SetHot = on => {
                            chip.BorderBrush = on ? Brushes.White : idle;
                            if (!on) return;
                            _rightIndex = chipIndex;
                            EnsureVisible(chip);
                        },
                        Activate = () => {
                            _values.SelectedTyres = captured;
                            BuildSheet();   // ここで Register し直される（枠は GtFocus 側が出し直す）
                        },
                        Custom = PanelSwitchFromRight
                    });
                }
                SheetPanel.Children.Add(row);
            }

            foreach (var tab in _values.Tabs) {
                SheetPanel.Children.Add(SectionHeader(TranslateTab(tab.DisplayName)));
                // [fork] GT7実測: フロント/リアの2値は「同じ幅の黒フィールドを横に2枚」の1行にまとめる。
                //   LF/RF→フロント代表・LR/RR→リア代表(編集すると左右同値になる=GT7と同じ左右対称)
                var entries = tab.Entries.ToList();
                var used = new HashSet<CarSetupEntry>();
                var captionDone = false;
                foreach (var entry in entries) {
                    if (used.Contains(entry)) continue;
                    if (TrySplitAxle(entry.DisplayName, out var baseName, out _)) {
                        var front = new List<CarSetupEntry>();
                        var rear = new List<CarSetupEntry>();
                        foreach (var e2 in entries) {
                            if (used.Contains(e2)) continue;
                            if (TrySplitAxle(e2.DisplayName, out var b2, out var f2) && string.Equals(b2, baseName, StringComparison.OrdinalIgnoreCase)) {
                                (f2 ? front : rear).Add(e2);
                            }
                        }
                        if (front.Count > 0 && rear.Count > 0) {
                            foreach (var e2 in front) used.Add(e2);
                            foreach (var e2 in rear) used.Add(e2);
                            if (!captionDone) { SheetPanel.Children.Add(AxleCaption()); captionDone = true; }
                            Action<int> pairStep;
                            var pair = PairRow(baseName, front, rear, out pairStep);
                            SheetPanel.Children.Add(pair);
                            AddRightFocus(TranslateEntry(baseName), pair, null, pairStep);
                            continue;
                        }
                    }
                    used.Add(entry);
                    Action<int> entryStep;
                    var single = WrapFocusRow(EntryRow(entry, out entryStep));
                    SheetPanel.Children.Add(single);
                    AddRightFocus(TranslateEntry(entry.DisplayName), single, null, entryStep);
                }
            }

            RegisterFocus(_focusOnRight);   // [fork] 作り直した候補で登録し直す（今いる側は変えない）
        }

        /// <summary>「Camber LF」→(Camber, front)。前後ペア判定に使う接尾の辞書。</summary>
        private static bool TrySplitAxle(string name, out string baseName, out bool isFront) {
            baseName = null; isFront = false;
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.Trim();
            foreach (var s in new[] { (" LF", true), (" RF", true), (" LR", false), (" RR", false), (" Front", true), (" Rear", false) }) {
                if (n.EndsWith(s.Item1, StringComparison.OrdinalIgnoreCase)) {
                    baseName = n.Substring(0, n.Length - s.Item1.Length).Trim();
                    isFront = s.Item2;
                    return true;
                }
            }
            return false;
        }

        /// <summary>ペア行の上に一度だけ出す「フロント / リア」の列見出し。</summary>
        private FrameworkElement AxleCaption() {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 2) };
            AddPairColumns(g);
            var f = new TextBlock { Text = "フロント", FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(f, 2); Grid.SetColumnSpan(f, 3);
            g.Children.Add(f);
            var r = new TextBlock { Text = "リア", FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(r, 6); Grid.SetColumnSpan(r, 3);
            g.Children.Add(r);
            return g;
        }

        private static void AddPairColumns(Grid g) {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        }

        /// <summary>前後2値の1行: ラベル | 単位 | −F＋ | −R＋。編集はグループ全輪(左右)に同値適用。
        /// [fork] step = パッド用（フロント/リアを同時に動かす）。</summary>
        private Border PairRow(string baseName, List<CarSetupEntry> front, List<CarSetupEntry> rear, out Action<int> step) {
            var g = new Grid();
            AddPairColumns(g);

            var name = new TextBlock {
                Text = TranslateEntry(baseName), FontSize = 12.5, Foreground = (Brush)FindResource("GtLabel"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (!string.IsNullOrEmpty(front[0].HelpInformation)) name.ToolTip = front[0].HelpInformation;
            Grid.SetColumn(name, 0);
            g.Children.Add(name);

            var unit = new TextBlock {
                Text = front[0].UnitsPostfix?.Trim(), FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(unit, 1);
            g.Children.Add(unit);

            TextBlock MakeField(int col, CarSetupEntry representative) {
                var tb = new TextBlock {
                    Text = FormatValue(representative), FontSize = 14, FontWeight = FontWeights.SemiBold,
                    FontFamily = (FontFamily)FindResource("GtNum"), Foreground = (Brush)FindResource("GtValue"),
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                var field = new Border {
                    Background = (Brush)FindResource("GtField"), CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 4, 12, 5),
                    VerticalAlignment = VerticalAlignment.Center, Child = tb
                };
                Grid.SetColumn(field, col);
                g.Children.Add(field);
                return tb;
            }

            var frontText = MakeField(3, front[0]);
            var rearText = MakeField(7, rear[0]);

            // [fork] ラムダから捕まえるのでローカル関数ではなくデリゲートで持つ
            Action<List<CarSetupEntry>, int, TextBlock> stepOne = (grp, dir, text) => {
                var v = grp[0].Value + dir * grp[0].Step;
                foreach (var e in grp) e.Value = Math.Max(e.Minimum, Math.Min(e.Maximum, v));
                text.Text = FormatValue(grp[0]);
            };

            var minusF = StepButton("−", () => stepOne(front, -1, frontText));
            Grid.SetColumn(minusF, 2); g.Children.Add(minusF);
            var plusF = StepButton("＋", () => stepOne(front, 1, frontText));
            Grid.SetColumn(plusF, 4); g.Children.Add(plusF);
            var minusR = StepButton("−", () => stepOne(rear, -1, rearText));
            Grid.SetColumn(minusR, 6); g.Children.Add(minusR);
            var plusR = StepButton("＋", () => stepOne(rear, 1, rearText));
            Grid.SetColumn(plusR, 8); g.Children.Add(plusR);

            step = dir => { stepOne(front, dir, frontText); stepOne(rear, dir, rearText); };
            return WrapFocusRow(g);
        }

        /// <summary>GT7実測「フォーカス中の行は白い1px枠」。行をホバーで白枠が付くBorderに包む。</summary>
        private static Border WrapFocusRow(FrameworkElement row) {
            var b = new Border {
                Child = row, Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent
            };
            b.MouseEnter += (o, e) => b.BorderBrush = Brushes.White;
            b.MouseLeave += (o, e) => b.BorderBrush = Brushes.Transparent;
            return b;
        }

        /// <summary>GT7の指紋: 中央寄せキャプション＋左右にヘアライン。</summary>
        private FrameworkElement SectionHeader(string text) {
            var g = new Grid { Margin = new Thickness(0, 22, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l1 = new System.Windows.Shapes.Rectangle { Height = 1, Fill = (Brush)FindResource("GtHair"), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
            Grid.SetColumn(l1, 0);
            var t = new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)FindResource("GtLabel"), Margin = new Thickness(14, 0, 14, 0) };
            Grid.SetColumn(t, 1);
            var l2 = new System.Windows.Shapes.Rectangle { Height = 1, Fill = (Brush)FindResource("GtHair"), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
            Grid.SetColumn(l2, 2);
            g.Children.Add(l1); g.Children.Add(t); g.Children.Add(l2);
            return g;
        }

        /// <summary>GT7流の行: ラベル(薄グレー左) | 単位(小・フィールド外左) | − | 黒フィールド(値右寄せ白) | ＋。枠線・バーは使わない。
        /// [fork] step = パッド用（左右で −/＋）。</summary>
        private FrameworkElement EntryRow(CarSetupEntry entry, out Action<int> step) {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            var name = new TextBlock {
                Text = TranslateEntry(entry.DisplayName), FontSize = 12.5, Foreground = (Brush)FindResource("GtLabel"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (!string.IsNullOrEmpty(entry.HelpInformation)) name.ToolTip = entry.HelpInformation;
            Grid.SetColumn(name, 0);
            g.Children.Add(name);

            var unit = new TextBlock {
                Text = entry.UnitsPostfix?.Trim(), FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(unit, 1);
            g.Children.Add(unit);

            // [fork] マウスの −/＋ とパッドの左右で同じ処理を使う
            Action<int> doStep = dir => {
                entry.Value = dir < 0
                        ? Math.Max(entry.Minimum, entry.Value - entry.Step)
                        : Math.Min(entry.Maximum, entry.Value + entry.Step);
                RefreshRow(g, entry);
            };
            step = doStep;

            var minus = StepButton("−", () => doStep(-1));
            Grid.SetColumn(minus, 2);
            g.Children.Add(minus);

            var field = new Border {
                Background = (Brush)FindResource("GtField"), CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 4, 12, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock {
                    Text = FormatValue(entry), FontSize = 14, FontWeight = FontWeights.SemiBold,
                    FontFamily = (FontFamily)FindResource("GtNum"), Foreground = (Brush)FindResource("GtValue"),
                    HorizontalAlignment = HorizontalAlignment.Right, Tag = "value"
                }
            };
            Grid.SetColumn(field, 3);
            g.Children.Add(field);

            var plus = StepButton("＋", () => doStep(1));
            Grid.SetColumn(plus, 4);
            g.Children.Add(plus);

            return g;
        }

        private static string FormatValue(CarSetupEntry entry) {
            return entry.Value.ToString(Math.Abs(entry.Step % 1d) > 1e-9 ? "0.##" : "0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void RefreshRow(Grid row, CarSetupEntry entry) {
            foreach (var child in row.Children.OfType<FrameworkElement>()) {
                if (child is Border b && b.Child is TextBlock tb && Equals(tb.Tag, "value")) tb.Text = FormatValue(entry);
            }
        }

        private FrameworkElement StepButton(string glyph, Action onClick) {
            var b = new Border {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(3, 0, 3, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x28)), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Child = new TextBlock {
                    Text = glyph, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0)
                }
            };
            b.MouseEnter += (s, e) => b.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38));
            b.MouseLeave += (s, e) => b.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x28));
            b.MouseLeftButtonUp += (s, e) => { try { onClick(); } catch (Exception ex) { Logging.Warning("[SettingSheet] step: " + ex.Message); } };
            return b;
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e) {
            try {
                if (_setup != null) await _setup.SaveAsync();
            } catch (Exception ex) {
                Logging.Warning("[SettingSheet] save: " + ex);
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                GtNav.GoBack();   // [fork] 一つ前（コースイン画面）へ。以前はワールドマップ直行だった
            }
        }

        /// <summary>setup.ini のタブ名→GT7風の日本語。未知の名前はそのまま。</summary>
        private static readonly Dictionary<string, string> TabJa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Alignment"] = "アライメント",
            ["Generic"] = "基本",
            ["Tyres"] = "タイヤ",
            ["Tires"] = "タイヤ",
            ["Dampers"] = "ダンパー",
            ["Suspensions"] = "サスペンション",
            ["Suspension"] = "サスペンション",
            ["Drivetrain"] = "ドライブトレイン",
            ["Differential"] = "ディファレンシャルギア",
            ["Aero"] = "エアロダイナミクス",
            ["Brakes"] = "ブレーキ",
            ["Gears"] = "トランスミッション",
            ["Gearbox"] = "トランスミッション",
            ["Electronics"] = "電子制御",
            ["Engine"] = "エンジン",
            ["Fuel"] = "燃料",
            ["Dampers Front"] = "ダンパー（フロント）",
            ["Dampers Rear"] = "ダンパー（リア）",
        };

        private static string TranslateTab(string name) {
            return name != null && TabJa.TryGetValue(name.Trim(), out var ja) ? ja : name;
        }

        /// <summary>よくある項目名の先頭語＋LF/RF/LR/RR接尾を日本語化。未知の名前はそのまま（MOD車対応）。</summary>
        private static readonly (string En, string Ja)[] EntryWordJa = {
            ("Camber", "ネガティブキャンバー角"),
            ("Toe", "トー角"),
            ("Pressure", "空気圧"),
            ("Fuel", "燃料"),
            ("Brake power", "ブレーキ制動力"),
            ("Brake bias", "前後ブレーキバランス"),
            ("Front bias", "前後ブレーキバランス"),
            ("Engine limiter", "レブリミッター"),
            ("Final gear ratio", "最終減速比"),
            ("Final ratio", "最終減速比"),
            ("Wing", "ウイング"),
            ("Spring rate", "バネレート"),
            ("Ride height", "車高調整"),
            ("Fast bump", "減衰比 縮み側(高速)"),
            ("Fast rebound", "減衰比 伸び側(高速)"),
            ("Traction control", "トラクションコントロール"),
        };

        private static string TranslateEntry(string name) {
            if (string.IsNullOrEmpty(name)) return name;
            var result = name;
            foreach (var pair in EntryWordJa) {
                if (result.StartsWith(pair.En, StringComparison.OrdinalIgnoreCase)) {
                    result = pair.Ja + result.Substring(pair.En.Length);
                    break;
                }
            }
            return result
                    .Replace(" LF", " 左前").Replace(" RF", " 右前")
                    .Replace(" LR", " 左後").Replace(" RR", " 右後");
        }

        /// <summary>PP=410+212·ln(PS/t÷143)。2026-09-05 からチューニングショップのパーツ込み（GtTuning.Pp）。</summary>
        internal static double ComputePp(CarObject car) {
            return car == null ? 0 : GtTuning.Pp(car);
        }

        /// <summary>[fork] 「チューニングパーツ」セクション: パワーリストリクター(%) / バラスト(kg)。装着している時だけ出す。
        /// 値は車ごとの tuning json（セットアップファイルとは別）。変えた瞬間に保存し、左の基本性能とヘッダーの PP を更新。</summary>
        private void BuildTuningRows() {
            if (_car == null) return;
            var st = GtTuning.Get(_car);
            var hasRestrictor = st.Installed.Contains("restrictor");
            var hasBallast = st.Installed.Contains("ballast");
            if (!hasRestrictor && !hasBallast) return;
            SheetPanel.Children.Add(SectionHeader("チューニングパーツ"));
            if (hasRestrictor) {
                Action<int> step;
                var row = WrapFocusRow(ValueRow("パワーリストリクター", "%", () => st.Restrictor, v => SaveTuningValue(st, true, v), 0, 50, 1, out step));
                SheetPanel.Children.Add(row);
                AddRightFocus("パワーリストリクター", row, null, step);
            }
            if (hasBallast) {
                Action<int> step;
                var row = WrapFocusRow(ValueRow("バラスト", "kg", () => st.Ballast, v => SaveTuningValue(st, false, v), 0, 200, 5, out step));
                SheetPanel.Children.Add(row);
                AddRightFocus("バラスト", row, null, step);
            }
        }

        private void SaveTuningValue(GtTuningState state, bool restrictor, int value) {
            var previous = restrictor ? state.Restrictor : state.Ballast;
            if (restrictor) state.Restrictor = value;
            else state.Ballast = value;
            if (!GtTuning.Save(_car, state)) {
                if (restrictor) state.Restrictor = previous;
                else state.Ballast = previous;
                NonfatalError.Notify("セッティングを保存できませんでした", "値は変更前に戻しました。保存先の空き容量と書き込み権限を確認してください。");
                return;
            }
            AfterTuningChange();
        }

        private void AfterTuningChange() {
            try { BuildLeftSpecs(); } catch { }
            try { (FindName("Header") as GtHeader)?.Refresh(); } catch { }
        }

        /// <summary>[fork] EntryRow と同じ見た目で、値の出し入れをデリゲートにした行（セットアップ以外の値用）。</summary>
        private FrameworkElement ValueRow(string label, string unitText, Func<int> get, Action<int> set, int min, int max, int stepSize, out Action<int> step) {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            var name = new TextBlock { Text = label, FontSize = 12.5, Foreground = (Brush)FindResource("GtLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 0);
            g.Children.Add(name);
            var unit = new TextBlock {
                Text = unitText, FontSize = 10.5, Foreground = (Brush)FindResource("GtLabel"),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(unit, 1);
            g.Children.Add(unit);

            var text = new TextBlock {
                Text = get().ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 14, FontWeight = FontWeights.SemiBold,
                FontFamily = (FontFamily)FindResource("GtNum"), Foreground = (Brush)FindResource("GtValue"), HorizontalAlignment = HorizontalAlignment.Right
            };
            Action<int> doStep = dir => {
                var v = Math.Max(min, Math.Min(max, get() + dir * stepSize));
                set(v);
                text.Text = get().ToString(System.Globalization.CultureInfo.InvariantCulture);
            };
            step = doStep;
            var minus = StepButton("−", () => doStep(-1));
            Grid.SetColumn(minus, 2);
            g.Children.Add(minus);
            var field = new Border {
                Background = (Brush)FindResource("GtField"), CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 4, 12, 5),
                VerticalAlignment = VerticalAlignment.Center, Child = text
            };
            Grid.SetColumn(field, 3);
            g.Children.Add(field);
            var plus = StepButton("＋", () => doStep(1));
            Grid.SetColumn(plus, 4);
            g.Children.Add(plus);
            return g;
        }
    }
}
