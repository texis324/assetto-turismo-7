// [fork] GT7の「乗り換え」画面 — 白グレーの所有車リスト(GT7実測: クルマ買い取りサービスと同じ行部品)。
//   ガレージの乗り換え・GtStartからの車変更要求から開く。クリックで即決定・Escで戻る。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using AcManager.Tools.Managers;
using AcManager.Tools.Objects;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    public partial class GarageCarPicker {
        public CarObject Picked;
        /// <summary>[fork] 「ブランドセントラルでクルマを探す」が押された（呼び元がそちらへ遷移する）。</summary>
        public bool GoBrandCentral;
        /// <summary>設定への移動はモーダルの呼び元が後始末を終えてから行う。</summary>
        public bool GoSettings;
        private readonly CarObject _current;
        private List<CarObject> _all = new List<CarObject>();
        private bool _itemsLoaded;
        private bool _closed;
        private readonly Stopwatch _diagnosticWatch;

        public GarageCarPicker(CarObject current) {
            if (GtTestChannel.DiagnosticsEnabled) {
                _diagnosticWatch = Stopwatch.StartNew();
                LogDiagnostic("picker-created current=" + (current?.Id ?? "-"));
            }
            _current = current;
            InitializeComponent();
            Closed += (sender, args) => {
                _closed = true;
                _itemsLoaded = false;
                if (Picked == null) LogDiagnostic("picker-cancel reason=" + (GoSettings ? "settings" : GoBrandCentral ? "brandcentral" : "cancel"));
            };
            Header.SettingsRequested = () => {
                GoSettings = true;
                CancelSelection();
            };
        }

        private async void OnLoaded(object sender, RoutedEventArgs e) {
            try {
                await CarsManager.Instance.EnsureLoadedAsync();
                if (_closed) return;
                // [fork] 2026-09-06 母集団はガレージ（GtGarage）の車だけ。全車から選ぶのはブランドセントラルの仕事
                _all = GtGarage.OwnedCars()
                        .OrderBy(x => x.Brand ?? "", StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.DisplayName ?? "", StringComparer.OrdinalIgnoreCase)
                        .ToList();
                ApplyFilter();
                _itemsLoaded = true;
                LogDiagnostic("picker-ready cars=" + _all.Count + " readiness=items-bound");
            } catch (Exception ex) {
                Logging.Warning("[GarageCarPicker] load: " + ex);
            }
        }

        private void ApplyFilter() {
            var q = (SearchBox.Text ?? "").Trim();
            var items = string.IsNullOrEmpty(q)
                    ? _all
                    : _all.Where(x => (x.DisplayName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || (x.Brand ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            List.ItemsSource = items;
            CountText.Text = "ガレージ  " + items.Count + " 台";
            var selected = items.FirstOrDefault(x => x.Id == _current?.Id) ?? items.FirstOrDefault();
            if (selected != null) {
                List.SelectedItem = selected;
                List.ScrollIntoView(selected);
            }
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e) {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (IsLoaded) ApplyFilter();
        }

        private void OnItemClick(object sender, MouseButtonEventArgs e) {
            var item = sender as ListBoxItem;
            var car = item?.DataContext as CarObject;
            if (car == null) return;
            e.Handled = true;
            CompleteSelection(car);
        }

        private void CompleteSelection(CarObject car) {
            if (_closed || car == null || !car.Enabled || !GtGarage.IsOwned(car) || !_all.Contains(car)) return;
            LogDiagnostic("picker-selected car=" + car.Id);
            Picked = car;
            DialogResult = true;
            Close();
        }

        /// <summary>Opt-in diagnostics use the same final selection as a real card click.</summary>
        internal bool TestSelect(string carId) {
            if (!GtTestChannel.DiagnosticsEnabled || !IsLoaded || !IsVisible || !_itemsLoaded
                    || Picked != null || string.IsNullOrWhiteSpace(carId)) return false;
            var car = _all.FirstOrDefault(x => string.Equals(x.Id, carId, StringComparison.OrdinalIgnoreCase));
            if (car == null || !car.Enabled || !GtGarage.IsOwned(car)) return false;
            CompleteSelection(car);
            return true;
        }

        private void LogDiagnostic(string message) {
            if (_diagnosticWatch == null || !GtTestChannel.DiagnosticsEnabled) return;
            Logging.Write("[GtCarSwitch] " + message + " elapsed_ms="
                    + _diagnosticWatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        private void OnGoBrand(object sender, RoutedEventArgs e) {
            e.Handled = true;
            OpenBrandCentral();
        }

        private void OpenBrandCentral() {
            if (_closed) return;
            GtSfx.PlaySelect();
            GoBrandCentral = true;
            DialogResult = false;
            Close();
        }

        private void OnKeyDown(object sender, KeyEventArgs e) {
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
            // Header controls and their popup keep their native Tab/Space/Enter/Escape behavior.
            var headerMenu = Header.FindName("HeaderMenu") as ContextMenu;
            if (headerMenu?.IsOpen == true) return;
            if (e.Key == Key.Escape) {
                e.Handled = true;
                if (!e.IsRepeat) { GtSfx.PlayCancel(); CancelSelection(); }
                return;
            }
            if (Header.IsKeyboardFocusWithin) return;
            if (Keyboard.FocusedElement is TextBoxBase) {
                // Down leaves search without interpreting the typed text as a command.
                if (e.Key == Key.Down) { e.Handled = true; FocusSelection(); }
                return;
            }
            if (Keyboard.FocusedElement is ButtonBase) return;
            GtPadCommand command;
            switch (e.Key) {
                case Key.Up: command = GtPadCommand.Up; break;
                case Key.Down: command = GtPadCommand.Down; break;
                case Key.Left: command = GtPadCommand.Left; break;
                case Key.Right: command = GtPadCommand.Right; break;
                case Key.Enter: case Key.Space: command = GtPadCommand.Confirm; break;
                default: return;
            }
            e.Handled = HandleCommand(command, e.IsRepeat);
        }

        /// <summary>Called only for the active modal picker; never forwards commands to the page behind it.</summary>
        internal bool HandleCommand(GtPadCommand command, bool repeat = false) {
            if (_closed || !IsLoaded || !IsVisible || !IsActive || !IsEnabled) return false;
            var direction = command >= GtPadCommand.Up && command <= GtPadCommand.Right;
            if (repeat && !direction) return true;
            if (GtHeader.HandleMenuPad(command)) return true;
            if (command == GtPadCommand.Back) { GtSfx.PlayCancel(); CancelSelection(); return true; }
            if (command == GtPadCommand.Triangle) { OpenBrandCentral(); return true; }
            if (command != GtPadCommand.Confirm && !direction) return true;
            if (command == GtPadCommand.Confirm && GoBrandLink.IsKeyboardFocusWithin) { OpenBrandCentral(); return true; }
            if (!_itemsLoaded || List.Items.Count == 0) return true;
            if (command == GtPadCommand.Confirm) {
                GtSfx.PlaySelect();
                CompleteSelection(List.SelectedItem as CarObject);
                return true;
            }
            var next = NextSelection(List.SelectedIndex, List.Items.Count, Columns(), command);
            if (next != List.SelectedIndex) { List.SelectedIndex = next; GtSfx.PlayMove(); }
            FocusSelection();
            return true;
        }

        private void FocusSelection() {
            if (!_itemsLoaded || List.Items.Count == 0) return;
            if (List.SelectedIndex < 0) List.SelectedIndex = 0;
            List.ScrollIntoView(List.SelectedItem);
            List.UpdateLayout();
            var item = List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as ListBoxItem;
            if (item != null) item.Focus();
            else List.Focus();
        }

        private int Columns() {
            // Count the rendered first row so wrapping follows the actual window size and DPI.
            List.UpdateLayout();
            var first = List.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            if (first == null) return 1;
            var y = first.TranslatePoint(new Point(), List).Y;
            for (var i = 1; i < List.Items.Count; i++) {
                var item = List.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                if (item == null || Math.Abs(item.TranslatePoint(new Point(), List).Y - y) > 1) return i;
            }
            return Math.Max(1, List.Items.Count);
        }

        private static int NextSelection(int index, int count, int columns, GtPadCommand command) {
            if (count <= 0) return -1;
            if (index < 0 || index >= count) return 0;
            columns = Math.Max(1, columns);
            switch (command) {
                case GtPadCommand.Left: return index % columns > 0 ? index - 1 : index;
                case GtPadCommand.Right: return index % columns < columns - 1 && index + 1 < count ? index + 1 : index;
                case GtPadCommand.Up: return index >= columns ? index - columns : index;
                case GtPadCommand.Down: return (index / columns + 1) * columns < count ? Math.Min(count - 1, index + columns) : index;
                default: return index;
            }
        }

        /// <summary>検索欄にフォーカスがあっても、パッドの戻ると Esc は同じキャンセルを行う。</summary>
        internal void CancelSelection() {
            if (_closed) return;
            DialogResult = false;
            Close();
        }
    }

    /// <summary>車→PP文字列(PP=410+212·ln(PS/t÷143))。計算不能なら空。</summary>
    public class GaragePpConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var car = value as CarObject;
            if (car == null) return "";
            var pp = SettingSheet.ComputePp(car);
            return pp > 0 ? pp.ToString("0.00", CultureInfo.InvariantCulture) : "—";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotSupportedException();
        }
    }

    /// <summary>車→「579 PS / 1,620 kg / FR」のスペック行(GtHeaderと同表記)。</summary>
    public class GarageSpecConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var car = value as CarObject;
            if (car == null) return "";
            try { return GtHeader.BuildSpecLine(car) ?? ""; } catch { return ""; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotSupportedException();
        }
    }
}
