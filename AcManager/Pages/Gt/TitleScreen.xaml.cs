using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Pages.Gt {
    /// <summary>The local Assetto Turismo front door: opening movie, title screen, world map.</summary>
    public partial class TitleScreen {
        public const string PagePath = "/Pages/Gt/TitleScreen.xaml";
        public static readonly Uri PageUri = new Uri(PagePath, UriKind.Relative);
        private const string KeyEnabled = "__GtTitleScreen";

        public static bool Enabled {
            get { return ValuesStorage.Get(KeyEnabled, false); }
            set { ValuesStorage.Set(KeyEnabled, value); }
        }

        // Shared by the initial XAML source and Loaded so an unwanted page never flashes first.
        public static Uri StartupPageUri => Enabled ? PageUri : WorldMap.PageUri;

        public static readonly DependencyProperty IsPadFocusedProperty = DependencyProperty.RegisterAttached(
                "IsPadFocused", typeof(bool), typeof(TitleScreen), new PropertyMetadata(false));
        public static bool GetIsPadFocused(DependencyObject element) { return (bool)element.GetValue(IsPadFocusedProperty); }
        public static void SetIsPadFocused(DependencyObject element, bool value) { element.SetValue(IsPadFocusedProperty, value); }

        private readonly List<Button> _cardButtons = new List<Button>();
        private Button _returnFocus;

        public TitleScreen() {
            InitializeComponent();
            VersionText.Text = "バージョン " + typeof(TitleScreen).Assembly.GetName().Version;
            BuildCards();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) {
            GtChrome.Enter();
            OpeningButton.IsEnabled = GtOpening.Available || GtOpening.IntroAvailable;
            RegisterFocus();
            // Avoid competing with the existing opening movie for keyboard focus.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                if (IsLoaded && IsVisible && !GtOpening.IsShowing) WorldMapButton.Focus();
            }));
            Logging.Write("[TitleScreen] ready; cards=" + _cardButtons.Count + "; music-rally=unavailable");
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            InfoOverlay.Visibility = Visibility.Collapsed;
            ControlsLayer.IsEnabled = true;
            GtFocus.Clear(this);
            GtChrome.Leave();
        }

        private GtFocusItem FocusItem(Button button) {
            return new GtFocusItem {
                Name = AutomationProperties.GetName(button) is string name && !string.IsNullOrWhiteSpace(name)
                        ? name : button.Content as string ?? button.Name,
                Center = () => WorldMap.ElementCenter(button),
                Enabled = button.IsEnabled,
                SetHot = hot => {
                    SetIsPadFocused(button, hot);
                    if (hot) {
                        button.Focus();
                        if (_cardButtons.Contains(button)) button.BringIntoView();
                    }
                },
                Activate = () => { if (button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
            };
        }

        private void RegisterFocus() {
            var items = new List<GtFocusItem>();
            if (InfoOverlay.Visibility == Visibility.Visible) {
                items.Add(FocusItem(CloseInfoButton));
            } else {
                items.Add(FocusItem(WorldMapButton));
                items.Add(FocusItem(AnnouncementButton));
                foreach (var button in _cardButtons) items.Add(FocusItem(button));
                foreach (var button in new[] { PreviousButton, NextButton, NewsButton, GuideButton, OpeningButton, SettingsButton, ExitButton }) {
                    if (button.IsEnabled) items.Add(FocusItem(button));
                }
            }
            GtFocus.Register(this, items, GtFocusLayout.Spatial);
        }

        private ImageSource LoadCardImage(string relativePath) {
            try {
                var path = Path.Combine(EntryPoint.ApplicationDataDirectory, "Static", "gt7", relativePath);
                if (!File.Exists(path)) return CoverImage.Source;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 640;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            } catch (Exception ex) {
                Logging.Warning("[TitleScreen] card image: " + ex.Message);
                return CoverImage.Source;
            }
        }

        private void AddCard(string title, string category, string image, Action action, bool isNew = false) {
            var content = new StackPanel();
            var picture = new Grid { Height = 162, ClipToBounds = true };
            picture.Children.Add(new Image { Source = image == null ? CoverImage.Source : LoadCardImage(image), Stretch = Stretch.UniformToFill });
            if (isNew) {
                picture.Children.Add(new Border {
                    Background = new SolidColorBrush(Color.FromRgb(0xE7, 0x42, 0x57)), CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(12), Padding = new Thickness(8, 3, 8, 3),
                    Child = new TextBlock { Text = "NEW", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White }
                });
            }
            content.Children.Add(picture);
            content.Children.Add(new TextBlock {
                Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                LineHeight = 25, Height = 54, Margin = new Thickness(12, 13, 12, 0), Foreground = Brushes.White
            });
            content.Children.Add(new TextBlock {
                Text = "●  " + category, FontSize = 13, Opacity = 0.82, Margin = new Thickness(12, 10, 12, 0), Foreground = Brushes.White
            });
            var button = new Button { Style = (Style)FindResource("NewsCard"), Content = content };
            AutomationProperties.SetName(button, title);
            button.Click += (s, e) => { e.Handled = true; action(); };
            _cardButtons.Add(button);
            CardsPanel.Children.Add(button);
        }

        private void BuildCards() {
            AddCard("ASSETTO TURISMOへようこそ。新しいドライブが、ここから始まる。", "ASSETTO TURISMO", null,
                    () => ShowInfo("ASSETTO TURISMO", "Assetto Corsa をベースにした、私たちのドライビング体験。\n\n「ワールドマップ」から、お気に入りのクルマと次のドライブへ。"), true);
            AddCard("世界のサーキットへ。次の舞台を見つけよう。", "ワールドサーキット", "carphotos\\ks_ferrari_488_gt3.jpg",
                    () => WorldMap.Navigate("/Pages/Gt/WorldCircuits.xaml"));
            AddCard("憧れの一台と出会う。クルマの世界を広げよう。", "ブランドセントラル", "carphotos\\j8_nissan_r32_vspec_2.jpg",
                    () => WorldMap.Navigate("/Pages/Gt/BrandCentral.xaml"));
            AddCard("愛車の可能性を引き出す。自分だけのセッティングへ。", "チューニングショップ", "carphotos\\ks_porsche_911_gt1.jpg",
                    () => WorldMap.Navigate("/Pages/Gt/TuningShop.xaml"));
            AddCard("心に残る一瞬を。ドライブの記憶を眺めよう。", "スケープス", "carphotos\\ks_lamborghini_miura_sv.jpg",
                    () => WorldMap.Navigate("/Pages/Gt/Gallery.xaml"));
            AddCard("走る楽しさを、自分のスタイルで。", "設定", "carphotos\\ks_mazda_787b.jpg",
                    () => WorldMap.Navigate("/Pages/Gt/GtSettings.xaml"));
        }

        private void ShowInfo(string title, string text) {
            _returnFocus = Keyboard.FocusedElement as Button;
            InfoTitle.Text = title;
            InfoText.Text = text;
            InfoOverlay.Visibility = Visibility.Visible;
            ControlsLayer.IsEnabled = false;
            RegisterFocus();
            CloseInfoButton.Focus();
        }

        private void CloseInfo() {
            InfoOverlay.Visibility = Visibility.Collapsed;
            ControlsLayer.IsEnabled = true;
            RegisterFocus();
            (_returnFocus ?? WorldMapButton).Focus();
        }

        private void OnWorldMap(object sender, RoutedEventArgs e) { WorldMap.Navigate(WorldMap.PageUri.OriginalString); }
        private void OnSettings(object sender, RoutedEventArgs e) { WorldMap.Navigate("/Pages/Gt/GtSettings.xaml"); }
        private void OnExit(object sender, RoutedEventArgs e) { Window.GetWindow(this)?.Close(); }
        private void OnAnnouncement(object sender, RoutedEventArgs e) {
            ShowInfo("ASSETTO TURISMOへようこそ", "「ワールドマップ」からドライブを始められます。\n\n下のお知らせカードから、サーキットやクルマ、チューニングへ直接進むこともできます。");
        }
        private void OnGuide(object sender, RoutedEventArgs e) {
            ShowInfo("操作ガイド", "マウス：項目をクリック\nキーボード：方向キーで選択、Enterで決定\nコントローラー：方向キーで選択、決定ボタンで開く\n\nEsc／戻るボタン：ひとつ前へ\nタイトル画面では、そのまま待機します。");
        }
        private void OnNews(object sender, RoutedEventArgs e) { NewsScroller.ScrollToLeftEnd(); _cardButtons[0].Focus(); }
        private void OnPrevious(object sender, RoutedEventArgs e) { NewsScroller.ScrollToHorizontalOffset(Math.Max(0, NewsScroller.HorizontalOffset - 624)); }
        private void OnNext(object sender, RoutedEventArgs e) { NewsScroller.ScrollToHorizontalOffset(Math.Min(NewsScroller.ScrollableWidth, NewsScroller.HorizontalOffset + 624)); }
        private void OnOpening(object sender, RoutedEventArgs e) {
            if (!GtOpening.Play(Window.GetWindow(this), "title-screen")) {
                ShowInfo("オープニング", "オープニング動画を再生できませんでした。");
            }
        }
        private void OnCloseInfo(object sender, RoutedEventArgs e) { CloseInfo(); }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
            if (InfoOverlay.Visibility == Visibility.Visible && (e.Key == Key.Escape || e.Key == Key.Enter)) {
                CloseInfo();
                e.Handled = true;
            } else if (e.Key == Key.Escape) {
                e.Handled = true;
            } else if (e.Key == Key.Enter && Keyboard.FocusedElement == this && !GtFocus.KeyboardNavigationActive) {
                OnWorldMap(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }
}
