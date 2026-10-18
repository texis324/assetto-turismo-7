// [fork] スケープス（写真ギャラリー）— GT7の「撮った写真を見る場所」の代わり。
//   AC のスクリーンショット（Documents\Assetto Corsa\screens）と、手持ち画像を置くフォルダ
//   （CM の Data\Static\gt7\gallery）を新しい順に並べて展示する。クリックで全画面・← →で送り。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AcManager.Tools.Managers;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    public partial class Gallery {
        public class Shot {
            public string Path { get; set; }
            public string Caption { get; set; }
        }

        private List<Shot> _shots = new List<Shot>();
        private int _index;

        public Gallery() {
            InitializeComponent();
            // [fork] パッド操作中はヒントを記号表記へ
            GtHints.Bind(HintBack, "↑ ↓ ← →  選ぶ    Enter  開く    Esc  戻る");
            GtHints.Bind(HintViewer, "← →  送る    Enter / クリック / Esc  閉じる", "← →  送る    Enter / Esc  閉じる");
        }

        /// <summary>手持ち画像を置く場所（ここに入れた画像がそのまま展示される）。</summary>
        internal static string GalleryDirectory {
            get { return System.IO.Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", "gallery"); }
        }

        private static IEnumerable<string> Dirs() {
            yield return GalleryDirectory;
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return System.IO.Path.Combine(docs, "Assetto Corsa", "screens");
            var root = AcRootDirectory.Instance?.Value;
            if (root != null) yield return System.IO.Path.Combine(root, "screens");
        }

        private void OnLoaded(object sender, RoutedEventArgs e) {
            GtFocus.Clear(this);
            GtChrome.Enter();
            Focus();
            var previous = _index >= 0 && _index < _shots.Count ? _shots[_index].Path : null;
            Viewer.Visibility = Visibility.Collapsed;
            ViewerImage.Source = null;
            try { Directory.CreateDirectory(GalleryDirectory); } catch { }

            var exts = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
            var found = new List<FileInfo>();
            foreach (var d in Dirs()) {
                try {
                    if (!Directory.Exists(d)) continue;
                    found.AddRange(new DirectoryInfo(d).GetFiles("*.*", SearchOption.AllDirectories)
                            .Where(f => exts.Contains(f.Extension.ToLowerInvariant()) && f.Length > 4096));
                } catch (Exception ex) {
                    Logging.Warning("[Gallery] scan " + d + ": " + ex.Message);
                }
            }

            _shots = found.GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(400)
                    .Select(f => new Shot { Path = f.FullName, Caption = f.Name + "   ·   " + f.LastWriteTime.ToString("yyyy/MM/dd HH:mm") })
                    .ToList();

            List.ItemsSource = _shots;
            var previousIndex = _shots.FindIndex(x => string.Equals(x.Path, previous, StringComparison.OrdinalIgnoreCase));
            _index = previousIndex >= 0 ? previousIndex : 0;
            CountText.Text = _shots.Count + " 枚";
            EmptyHint.Visibility = _shots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_shots.Count == 0) {
                HintPath.Text = "AC のスクリーンショット（Documents\\Assetto Corsa\\screens）と\n次のフォルダに入れた画像がここに並びます\n\n" + GalleryDirectory;
            }
            RegisterGalleryFocus();
            // 初回のカード生成より先にパッド枠が付いた場合も、配置完了後に選択写真を画面内へ戻す。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                if (IsLoaded && List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
            }));
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            GtFocus.Clear(this);
            Viewer.Visibility = Visibility.Collapsed;
            ViewerImage.Source = null;
            GtChrome.Leave();
        }

        private void RegisterGalleryFocus() {
            var items = new List<GtFocusItem>();
            for (var i = 0; i < _shots.Count; i++) {
                var index = i;
                items.Add(new GtFocusItem {
                    Name = _shots[index].Caption,
                    Center = () => WorldMap.ElementCenter(List.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement),
                    SetHot = on => {
                        if (on) {
                            _index = index;
                            List.SelectedIndex = index;
                            List.ScrollIntoView(_shots[index]);
                        } else if (List.SelectedIndex == index) {
                            List.SelectedIndex = -1;
                        }
                    },
                    Activate = () => { _index = index; OpenViewer(); }
                });
            }
            GtFocus.Register(this, items, GtFocusLayout.Spatial, 0, _index);
        }

        private void RegisterViewerFocus() {
            GtFocus.Register(this, new[] {
                new GtFocusItem {
                    Name = "写真を閉じる", Activate = CloseViewer,
                    Custom = command => {
                        switch (command) {
                            case GtPadCommand.Left: Step(-1); return true;
                            case GtPadCommand.Right: Step(1); return true;
                            // ビューアは写真が1枚だけなので、枠を出すための空押しは不要。
                            case GtPadCommand.Confirm: CloseViewer(); return true;
                            case GtPadCommand.Up: case GtPadCommand.Down: return true;
                            default: return false;
                        }
                    }
                }
            }, GtFocusLayout.Row);
        }

        private void OnItemClick(object sender, MouseButtonEventArgs e) {
            var item = sender as ListBoxItem;
            var shot = item?.DataContext as Shot;
            if (shot == null) return;
            e.Handled = true;
            _index = _shots.IndexOf(shot);
            OpenViewer();
        }

        private void OpenViewer() {
            if (_index < 0 || _index >= _shots.Count) return;
            var opening = Viewer.Visibility != Visibility.Visible;
            var shot = _shots[_index];
            ViewerImage.Source = WorldMap.LoadBitmap(shot.Path, 0);
            ViewerCaption.Text = shot.Caption + "      (" + (_index + 1) + " / " + _shots.Count + ")";
            Viewer.Visibility = Visibility.Visible;
            if (opening) RegisterViewerFocus();
            Focus();
        }

        private void CloseViewer() {
            Viewer.Visibility = Visibility.Collapsed;
            ViewerImage.Source = null;
            RegisterGalleryFocus();
            if (_index >= 0 && _index < _shots.Count) List.ScrollIntoView(_shots[_index]);
            Focus();
        }

        private void OnViewerClick(object sender, MouseButtonEventArgs e) {
            e.Handled = true;
            CloseViewer();
        }

        private void Step(int dir) {
            if (_shots.Count == 0) return;
            _index = ((_index + dir) % _shots.Count + _shots.Count) % _shots.Count;
            OpenViewer();
        }

        private void OnKeyDown(object sender, KeyEventArgs e) {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                    || Keyboard.FocusedElement is PasswordBox || Keyboard.FocusedElement is ComboBox) return;
            switch (e.Key) {
                case Key.Escape:
                    e.Handled = true;
                    if (e.IsRepeat) break;
                    if (Viewer.Visibility == Visibility.Visible) CloseViewer();
                    else WorldMap.Navigate("/Pages/Gt/WorldMap.xaml");
                    break;
                case Key.Enter:
                    e.Handled = true;
                    if (e.IsRepeat) break;
                    if (Viewer.Visibility == Visibility.Visible) CloseViewer();
                    else GtFocus.Handle(GtPadCommand.Confirm);
                    break;
                case Key.Up:
                    e.Handled = GtFocus.Handle(GtPadCommand.Up);
                    break;
                case Key.Down:
                    e.Handled = GtFocus.Handle(GtPadCommand.Down);
                    break;
                case Key.Left:
                    if (Viewer.Visibility == Visibility.Visible) { e.Handled = true; Step(-1); }
                    else e.Handled = GtFocus.Handle(GtPadCommand.Left);
                    break;
                case Key.Right:
                    if (Viewer.Visibility == Visibility.Visible) { e.Handled = true; Step(1); }
                    else e.Handled = GtFocus.Handle(GtPadCommand.Right);
                    break;
            }
        }
    }
}
