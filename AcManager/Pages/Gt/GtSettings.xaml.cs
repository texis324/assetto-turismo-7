using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AcManager.Pages.Gt {
    public partial class GtSettings {
        private bool _loading;
        private bool _keyboardTab;
        private bool _controllerTab;
        private bool _displayTab;   // [fork] 表示タブ（タイトル画面・オープニングムービー）
        private GtControllerBindingsPanel _controllerBindings;
        private bool _assistDirty;
        private GtKeyboardSettings _keyboardSettings;
        private Func<bool> _drivingState = GtInGameSettings.BlockKeyboardChanges;
        private readonly Dictionary<string, Button> _bindingButtons = new Dictionary<string, Button>();
        private readonly DispatcherTimer _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _assistSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private string _waitingBinding;
        private bool _cancelEscapeRepeats;
        private DateTime _captureUntil;
        private static GtSettings _captureOwner;

        internal static bool IsCapturingKey => _captureOwner != null && _captureOwner.IsLoaded && _captureOwner._waitingBinding != null;

        public GtSettings() {
            InitializeComponent();
            GtHints.Bind(HintBack, "Esc  戻る");
            _stateTimer.Tick += (s, e) => {
                RefreshDrivingState();
                if (_waitingBinding != null && DateTime.UtcNow >= _captureUntil) CancelCapture("キーの入力待ちを取り消しました。");
            };
            _assistSaveTimer.Tick += (s, e) => CommitAssist();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) {
            GtChrome.Enter();
            _loading = true;
            VolumeSlider.Value = GtSfx.VolumePercent;   // [fork] 目盛りは対数カーブ（GtVolumeCurve）
            MasterVolumeSlider.Value = GtMasterVolume.Percent;   // [fork] 全体音量
            _loading = false;
            UpdateVolumeText();
            UpdateMasterVolumeText();
            ResetButton.Content = "初期値に戻す（" + GtSfx.DefaultPercent + "％）";
            MovieVolumeResetButton.Content = "初期値に戻す（" + GtOpening.DefaultVolumePercent + "％）";
            DisabledNote.Visibility = GtSfx.Enabled ? Visibility.Collapsed : Visibility.Visible;
            RefreshRumbleUi();
            RefreshTitleScreenUi();
            RefreshOpeningUi();
            try {
                _keyboardSettings = new GtKeyboardSettings(isDriving: _drivingState);
                BuildBindings();
                LoadKeyboardValues();
            } catch (Exception ex) {
                _keyboardSettings = null;
                KeyboardStatus.Text = "キーボード設定を読み込めませんでした。";
                FirstFloor.ModernUI.Helpers.Logging.Warning("[GtSettings] keyboard load: " + ex.Message);
            }
            if (_controllerTab || GtInGameSettings.ControllerPageRequested) SelectControllerTab();
            else if (_displayTab) SelectDisplayTab();
            else SelectTab(_keyboardTab);
            _stateTimer.Start();
        }

        private void RegisterFocus() {
            var items = new List<GtFocusItem> {
                ButtonFocus("sound-tab", SoundTabButton), ButtonFocus("keyboard-tab", KeyboardTabButton),
                ButtonFocus("controller-tab", ControllerTabButton), ButtonFocus("display-tab", DisplayTabButton)
            };
            if (_controllerTab) {
                if (_controllerBindings != null) { _controllerBindings.FocusBase = items.Count; items.AddRange(_controllerBindings.GetFocusItems()); }
            } else if (_keyboardTab) {
                if (KeyboardEditor.IsEnabled) {
                    items.Add(new GtFocusItem {
                        Name = "keyboard-assist", HandlesActivationSound = true,
                        SetHot = hot => { AssistRow.BorderBrush = hot ? Brushes.White : Brushes.Transparent; if (hot) AssistRow.BringIntoView(); },
                        Activate = GtSfx.PlaySelect,
                        Custom = command => {
                            if (command != GtPadCommand.Left && command != GtPadCommand.Right) return false;
                            AssistSlider.Value = Math.Max(0, Math.Min(100, AssistSlider.Value + (command == GtPadCommand.Left ? -5 : 5)));
                            CommitAssist();
                            GtSfx.PlayMove();
                            return true;
                        }
                    });
                    items.Add(ButtonFocus("assist-legacy", LegacyAssistButton));
                    items.Add(ButtonFocus("assist-normal", NormalAssistButton));
                    items.Add(ButtonFocus("assist-strong", StrongAssistButton));
                    foreach (var action in GtKeyboardSettings.Actions) items.Add(ButtonFocus("bind-" + action.Id, _bindingButtons[action.Id]));
                    items.Add(ButtonFocus("reset-bindings", ResetBindingsButton));
                }
            } else if (_displayTab) {
                items.Add(ButtonFocus("rumble-on", RumbleOnButton));
                items.Add(ButtonFocus("rumble-off", RumbleOffButton));
                items.Add(ButtonFocus("title-on", TitleScreenOnButton));
                items.Add(ButtonFocus("title-off", TitleScreenOffButton));
                items.Add(ButtonFocus("opening-on", OpeningOnButton));
                items.Add(ButtonFocus("opening-off", OpeningOffButton));
                items.Add(ButtonFocus("opening-play", OpeningPlayButton));
                items.Add(ButtonFocus("intro-on", IntroOnButton));
                items.Add(ButtonFocus("intro-off", IntroOffButton));
                items.Add(ButtonFocus("intro-play", IntroPlayButton));
                items.Add(new GtFocusItem {
                    Name = "movie-volume", HandlesActivationSound = true,
                    SetHot = hot => { MovieVolumeRow.BorderBrush = hot ? Brushes.White : Brushes.Transparent; if (hot) MovieVolumeRow.BringIntoView(); },
                    Activate = GtSfx.PlaySelect,
                    Custom = command => {
                        if (command == GtPadCommand.Left) { StepMovieVolume(-5); return true; }
                        if (command == GtPadCommand.Right) { StepMovieVolume(5); return true; }
                        return false;
                    }
                });
                items.Add(ButtonFocus("movie-volume-preview", MovieVolumePreviewButton));
                items.Add(ButtonFocus("movie-volume-reset", MovieVolumeResetButton));
            } else items.AddRange(new[] {
                new GtFocusItem {
                    Name = "master-volume", HandlesActivationSound = true,
                    SetHot = hot => { MasterVolumeRow.BorderBrush = hot ? Brushes.White : Brushes.Transparent; if (hot) MasterVolumeRow.BringIntoView(); },
                    Activate = GtSfx.PlaySelect,
                    Custom = command => {
                        if (command == GtPadCommand.Left) { StepMasterVolume(-5); return true; }
                        if (command == GtPadCommand.Right) { StepMasterVolume(5); return true; }
                        return false;
                    }
                },
                ButtonFocus("master-volume-preview", MasterVolumePreviewButton),
                ButtonFocus("master-volume-reset", MasterVolumeResetButton),
                new GtFocusItem {
                    Name = "click-volume", HandlesActivationSound = true,
                    SetHot = hot => VolumeRow.BorderBrush = hot ? Brushes.White : Brushes.Transparent,
                    Activate = GtSfx.PlaySelect,
                    Custom = command => {
                        if (command == GtPadCommand.Left) { Step(-1); return true; }
                        if (command == GtPadCommand.Right) { Step(1); return true; }
                        return false;
                    }
                },
                ButtonFocus("preview", PreviewButton),
                ButtonFocus("reset-volume", ResetButton)
            });
            items.Add(ButtonFocus("back", BackButton));
            GtFocus.Register(this, items, GtFocusLayout.Column, initial: 2);
            GtFocus.SetPageCommand(this, command => {
                if (command != GtPadCommand.L1 && command != GtPadCommand.R1) return false;
                var tab = _displayTab ? 3 : _controllerTab ? 2 : _keyboardTab ? 1 : 0;
                tab = (tab + (command == GtPadCommand.R1 ? 1 : 3)) % 4;
                if (tab == 3) SelectDisplayTab(); else if (tab == 2) SelectControllerTab(); else SelectTab(tab == 1);
                GtSfx.PlaySelect();
                return true;
            });
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            CommitAssist();
            CancelCapture();
            _stateTimer.Stop();
            _assistSaveTimer.Stop();
            GtFocus.Clear(this);
            GtChrome.Leave();
            GtInGameSettings.SettingsUnloaded();
        }

        internal void PrepareReturnToGame() {
            CommitAssist();
            CancelCapture();
        }

        private static GtFocusItem ButtonFocus(string name, Button button) {
            return new GtFocusItem {
                Name = name, HandlesActivationSound = true,
                SetHot = hot => { button.BorderBrush = hot ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x53, 0x53, 0x59)); if (hot) button.BringIntoView(); },
                Activate = () => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))
            };
        }

        private void UpdateVolumeText() {
            if (VolumeText == null) return;
            var percent = VolumeSlider.Value;
            VolumeText.Text = percent == 0 ? "消音  0％" : percent.ToString("0.##", CultureInfo.InvariantCulture) + "％";
        }

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            UpdateVolumeText();
            if (_loading || !IsLoaded) return;
            GtSfx.VolumePercent = (int)Math.Round(VolumeSlider.Value);
        }

        private void Step(int delta) {
            var previous = VolumeSlider.Value;
            VolumeSlider.Value = Math.Max(0, Math.Min(100, previous + delta));
            if (VolumeSlider.Value != previous) GtSfx.PlaySelect();
        }

        private void OnDecreaseClick(object sender, RoutedEventArgs e) { e.Handled = true; Step(-1); }
        private void OnIncreaseClick(object sender, RoutedEventArgs e) { e.Handled = true; Step(1); }
        private void OnPreviewClick(object sender, RoutedEventArgs e) { e.Handled = true; GtSfx.PlaySelect(); }

        private void OnResetClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            VolumeSlider.Value = GtSfx.DefaultPercent;
            // Also save when the slider already displays the default (legacy value).
            GtSfx.VolumePercent = GtSfx.DefaultPercent;
            GtSfx.PlaySelect();
        }

        // ---- [fork] 全体音量（クリック音・オープニング・BGM に掛かるマスター）-------------------------
        private void UpdateMasterVolumeText() {
            if (MasterVolumeText == null) return;
            var percent = (int)MasterVolumeSlider.Value;
            MasterVolumeText.Text = percent == 0 ? "消音  0％" : percent.ToString(CultureInfo.InvariantCulture) + "％";
        }

        private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            UpdateMasterVolumeText();
            if (_loading || !IsLoaded) return;
            GtMasterVolume.Percent = (int)Math.Round(MasterVolumeSlider.Value);
        }

        private void StepMasterVolume(int delta) {
            var previous = MasterVolumeSlider.Value;
            MasterVolumeSlider.Value = Math.Max(0, Math.Min(100, previous + delta));
            if (MasterVolumeSlider.Value != previous) GtSfx.PlaySelect();
        }

        private void OnMasterVolumeDecreaseClick(object sender, RoutedEventArgs e) { e.Handled = true; StepMasterVolume(-5); }
        private void OnMasterVolumeIncreaseClick(object sender, RoutedEventArgs e) { e.Handled = true; StepMasterVolume(5); }
        private void OnMasterVolumeReleased(object sender, MouseButtonEventArgs e) { GtSfx.PlaySelect(); }

        private void OnMasterVolumeKeyUp(object sender, KeyEventArgs e) {
            if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down
                    || e.Key == Key.Home || e.Key == Key.End || e.Key == Key.PageUp || e.Key == Key.PageDown) GtSfx.PlaySelect();
        }

        private void OnMasterVolumePreviewClick(object sender, RoutedEventArgs e) { e.Handled = true; GtSfx.PlaySelect(); }

        private void OnMasterVolumeResetClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            MasterVolumeSlider.Value = GtMasterVolume.DefaultPercent;
            GtMasterVolume.Percent = GtMasterVolume.DefaultPercent;   // スライダーが既に既定の位置でも保存する
            GtSfx.PlaySelect();
        }

        private void OnVolumeReleased(object sender, MouseButtonEventArgs e) { GtSfx.PlaySelect(); }

        private void OnVolumeKeyUp(object sender, KeyEventArgs e) {
            if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down
                    || e.Key == Key.Home || e.Key == Key.End || e.Key == Key.PageUp || e.Key == Key.PageDown) GtSfx.PlaySelect();
        }

        private void OnBackClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            if (_waitingBinding != null) { CancelCapture(); GtSfx.PlayCancel(); return; }
            CommitAssist();
            GtSfx.PlayCancel();
            GtNav.GoBack();
        }

        private void SelectTab(bool keyboard) {
            CommitAssist();
            CancelCapture();
            _keyboardTab = keyboard;
            _controllerTab = false;
            _displayTab = false;
            SettingsHeader.Visibility = Visibility.Collapsed;   // [fork] 2026-09-19 要望: 設定画面ではどのタブでも上の帯を出さない
            if (keyboard) LoadKeyboardValues();
            SoundPanel.Visibility = keyboard ? Visibility.Collapsed : Visibility.Visible;
            KeyboardPanel.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
            ControllerPanel.Visibility = Visibility.Collapsed;
            DisplayPanel.Visibility = Visibility.Collapsed;
            ControllerTabButton.Background = Brushes.Transparent;
            DisplayTabButton.Background = Brushes.Transparent;
            SoundTabButton.Background = keyboard ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4B));
            KeyboardTabButton.Background = !keyboard ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4B));
            RefreshDrivingState(false);
            RegisterFocus();
        }

        private void OnSoundTabClick(object sender, RoutedEventArgs e) { e.Handled = true; SelectTab(false); GtSfx.PlaySelect(); }
        private void OnKeyboardTabClick(object sender, RoutedEventArgs e) { e.Handled = true; SelectTab(true); GtSfx.PlaySelect(); }
        private void OnControllerTabClick(object sender, RoutedEventArgs e) { e.Handled = true; SelectControllerTab(); GtSfx.PlaySelect(); }
        internal void OpenControllerBindings() {
            _controllerTab = true;
            _keyboardTab = false;
            if (IsLoaded) SelectControllerTab();
        }
        private void SelectControllerTab() {
            CommitAssist(); CancelCapture(); _keyboardTab = false; _controllerTab = true; _displayTab = false;
            SettingsHeader.Visibility = Visibility.Collapsed;
            DisplayPanel.Visibility = Visibility.Collapsed;
            DisplayTabButton.Background = Brushes.Transparent;
            if (_controllerBindings == null) {
                _controllerBindings = new GtControllerBindingsPanel();
                _controllerBindings.FocusItemsChanged += () => { if (_controllerTab) RegisterFocus(); };
                ControllerPanel.Content = _controllerBindings;
            }
            SoundPanel.Visibility = KeyboardPanel.Visibility = Visibility.Collapsed;
            ControllerPanel.Visibility = Visibility.Visible;
            SoundTabButton.Background = KeyboardTabButton.Background = Brushes.Transparent;
            ControllerTabButton.Background = new SolidColorBrush(Color.FromRgb(0x45,0x45,0x4B));
            _controllerBindings.Reload(); RegisterFocus();
        }

        private void RefreshDrivingState(bool refreshFocus = true) {
            if (_controllerTab) _controllerBindings?.RefreshState();
            var driving = _drivingState();
            var enabled = !driving && _keyboardSettings != null;
            var changed = KeyboardEditor.IsEnabled != enabled;
            KeyboardEditor.IsEnabled = enabled;
            DrivingNote.Visibility = driving ? Visibility.Visible : Visibility.Collapsed;
            if (driving && _waitingBinding != null) CancelCapture();
            if (changed && _keyboardTab && refreshFocus) RegisterFocus();
        }

        private void BuildBindings() {
            BindingsPanel.Children.Clear();
            _bindingButtons.Clear();
            string group = null;
            foreach (var action in GtKeyboardSettings.Actions) {
                var id = action.Id;
                // [fork] 2026-09-19: 項目が増えたので「走行 / 視点 / そのほか」の見出しで区切る
                if (action.Group != group) {
                    group = action.Group;
                    BindingsPanel.Children.Add(new TextBlock {
                        Text = group, Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xB4, 0x74)), FontSize = 13,
                        Margin = new Thickness(0, BindingsPanel.Children.Count == 0 ? 0 : 14, 0, 8)
                    });
                }
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
                row.Children.Add(new TextBlock { Text = action.Label, Foreground = Brushes.White, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
                var button = new Button { Style = (Style)FindResource("SettingsButton"), Padding = new Thickness(18, 8, 18, 8) };
                System.Windows.Automation.AutomationProperties.SetName(button, action.Label + "の割り当てキー");
                button.Click += (s, e) => { e.Handled = true; BeginCapture(id); };
                Grid.SetColumn(button, 1);
                row.Children.Add(button);
                BindingsPanel.Children.Add(row);
                _bindingButtons[id] = button;
            }
        }

        private void LoadKeyboardValues() {
            if (_keyboardSettings == null) return;
            _loading = true;
            AssistSlider.Value = _keyboardSettings.Assist;
            _loading = false;
            UpdateAssistText();
            foreach (var pair in _bindingButtons) pair.Value.Content = GtKeyboardSettings.KeyName(_keyboardSettings.GetKey(pair.Key));
        }

        private void UpdateAssistText() { AssistText.Text = ((int)AssistSlider.Value).ToString(CultureInfo.InvariantCulture) + "％"; }

        private void OnAssistChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            if (AssistText != null) UpdateAssistText();
            if (_loading || !IsLoaded || _keyboardSettings == null) return;
            _assistDirty = true;
            SaveNote.Text = "保存中…";
            _assistSaveTimer.Stop();
            _assistSaveTimer.Start();
        }

        private void CommitAssist() {
            _assistSaveTimer.Stop();
            if (!_assistDirty || _keyboardSettings == null) return;
            _assistDirty = false;
            string error;
            if (_keyboardSettings.TrySetAssist((int)Math.Round(AssistSlider.Value), out error)) {
                SaveNote.Text = "変更は自動的に保存されます";
                KeyboardStatus.Text = GtInGameSettings.IsSessionActive
                        ? "保存しました。コースに戻ると使えます。" : "保存しました。次の走行から使えます。";
            } else {
                KeyboardStatus.Text = error;
                SaveNote.Text = "保存できませんでした";
                LoadKeyboardValues();
            }
        }

        private void OnAssistPresetClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            AssistSlider.Value = Convert.ToInt32(((Button)sender).Tag, CultureInfo.InvariantCulture);
            CommitAssist();
            GtSfx.PlaySelect();
        }
        private void OnAssistReleased(object sender, MouseButtonEventArgs e) { CommitAssist(); GtSfx.PlaySelect(); }
        private void OnAssistKeyUp(object sender, KeyEventArgs e) { CommitAssist(); }

        private void BeginCapture(string id) {
            if (_keyboardSettings == null || !_bindingButtons.ContainsKey(id)) return;
            RefreshDrivingState();
            if (!KeyboardEditor.IsEnabled) return;
            CancelCapture();
            CommitAssist();
            _waitingBinding = id;
            _captureOwner = this;
            _captureUntil = DateTime.UtcNow.AddSeconds(20);
            _bindingButtons[id].Content = "キーを押してください…";
            _bindingButtons[id].Focus();
            KeyboardStatus.Text = "割り当てたいキーを押してください。Escで取り消します。";
            GtSfx.PlaySelect();
        }

        private void CancelCapture(string message = "") {
            if (_waitingBinding == null) return;
            var id = _waitingBinding;
            _waitingBinding = null;
            if (ReferenceEquals(_captureOwner, this)) _captureOwner = null;
            _bindingButtons[id].Content = GtKeyboardSettings.KeyName(_keyboardSettings.GetKey(id));
            KeyboardStatus.Text = message;
        }

        private void OnSettingsKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Escape && e.IsRepeat && _cancelEscapeRepeats) { e.Handled = true; return; }
            if (!e.IsRepeat) _cancelEscapeRepeats = false;
            // [fork] コントローラー割り当ての一覧を開いている最中の B / Esc は、画面を戻らず「一覧を閉じて取り消し」にする
            if (e.Key == Key.Escape && !e.IsRepeat && _controllerBindings != null && _controllerBindings.CancelBrowse()) {
                _cancelEscapeRepeats = true; e.Handled = true; return;
            }
            if (_waitingBinding == null) return;
            e.Handled = true;
            if (e.IsRepeat) return;
            if (e.Key == Key.Escape) { _cancelEscapeRepeats = true; CancelCapture(); return; } // GtNav owns the Escape sound.
            if (e.Key == Key.System || Keyboard.Modifiers != ModifierKeys.None) {
                KeyboardStatus.Text = "Ctrl・Alt・Shiftなどとの組み合わせは使えません。単独のキーを押してください。";
                return;
            }
            AssignCapturedKey(KeyInterop.VirtualKeyFromKey(e.Key));
        }

        private bool AssignCapturedKey(int vk) {
            if (_waitingBinding == null || _keyboardSettings == null) return false;
            string error;
            if (!_keyboardSettings.TrySetKey(_waitingBinding, vk, out error)) {
                KeyboardStatus.Text = error;
                return false;
            }
            var id = _waitingBinding;
            CancelCapture();
            LoadKeyboardValues();
            KeyboardStatus.Text = "「" + GtKeyboardSettings.Actions.First(x => x.Id == id).Label + "」を " + GtKeyboardSettings.KeyName(vk) + " に変更しました。";
            SaveNote.Text = "変更は自動的に保存されます";
            GtSfx.PlaySelect();
            return true;
        }

        private void OnResetBindingsClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            ResetKeyboardBindings();
        }

        private bool ResetKeyboardBindings() {
            CancelCapture();
            CommitAssist();
            if (_keyboardSettings == null) return false;
            string error;
            if (_keyboardSettings.TryResetBindings(out error)) {
                LoadKeyboardValues();
                KeyboardStatus.Text = "元のキー割り当てに戻しました。";
                GtSfx.PlaySelect();
                return true;
            }
            KeyboardStatus.Text = error;
            return false;
        }

        // ---- [fork] 表示タブ: タイトル画面・オープニングムービー -----------------------------------
        private void OnDisplayTabClick(object sender, RoutedEventArgs e) { e.Handled = true; SelectDisplayTab(); GtSfx.PlaySelect(); }

        private void SelectDisplayTab() {
            CommitAssist();
            CancelCapture();
            _keyboardTab = false;
            _controllerTab = false;
            _displayTab = true;
            SettingsHeader.Visibility = Visibility.Collapsed;   // [fork] 2026-09-19 要望: 設定画面ではどのタブでも上の帯を出さない
            SoundPanel.Visibility = KeyboardPanel.Visibility = ControllerPanel.Visibility = Visibility.Collapsed;
            DisplayPanel.Visibility = Visibility.Visible;
            SoundTabButton.Background = KeyboardTabButton.Background = ControllerTabButton.Background = Brushes.Transparent;
            DisplayTabButton.Background = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4B));
            RefreshRumbleUi();
            RefreshTitleScreenUi();
            RefreshOpeningUi();
            RegisterFocus();
        }

        // ---- [fork] 2026-10-11 コントローラーの振動 ---------------------------------------------------
        //   pad.txt の bridge_rumble=on|off を書き換えるだけ。ds4_bridge.py が 2 秒おきに読み直すので走行中でも効く。
        //   行が無い時は「オン」扱い（ブリッジの既定と同じ）。コメントはそのまま残す。
        private static string RumblePadTxt => System.IO.Path.Combine(GtPad.PadDir, "pad.txt");

        private static bool RumbleEnabled {
            get {
                try {
                    var m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(RumblePadTxt),
                            @"(?m)^[ \t]*bridge_rumble[ \t]*=[ \t]*([^\s#;]+)");
                    if (m.Success) {
                        var v = m.Groups[1].Value.ToLowerInvariant();
                        return !(v == "0" || v == "false" || v == "off" || v == "no");
                    }
                } catch (Exception) { }
                return true;
            }
            set {
                var path = RumblePadTxt;
                var text = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
                var rx = new System.Text.RegularExpressions.Regex(@"(?m)^([ \t]*bridge_rumble[ \t]*=[ \t]*)([^\s#;]+)");
                var word = value ? "on" : "off";
                if (rx.IsMatch(text)) {
                    text = rx.Replace(text, m => m.Groups[1].Value + word, 1);
                } else {
                    var nl = text.Contains("\r\n") ? "\r\n" : "\n";
                    if (text.Length > 0 && !text.EndsWith("\n")) text += nl;
                    text += "bridge_rumble=" + word + nl;
                }
                System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            }
        }

        private void RefreshRumbleUi() {
            if (RumbleStateText == null) return;
            var on = RumbleEnabled;
            RumbleStateText.Text = on ? "オン" : "オフ";
            var selected = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4B));
            var normal = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34));
            RumbleOnButton.Background = on ? selected : normal;
            RumbleOffButton.Background = on ? normal : selected;
        }

        private void SetRumbleEnabled(bool on) {
            try {
                RumbleEnabled = on;
                SaveNote.Text = on ? "コントローラーの振動をオンにしました。" : "コントローラーの振動をオフにしました。";
            } catch (Exception ex) {
                SaveNote.Text = "振動の設定を保存できませんでした: " + ex.Message;
            }
            RefreshRumbleUi();
            GtSfx.PlaySelect();
        }

        private void OnRumbleOnClick(object sender, RoutedEventArgs e) { e.Handled = true; SetRumbleEnabled(true); }
        private void OnRumbleOffClick(object sender, RoutedEventArgs e) { e.Handled = true; SetRumbleEnabled(false); }

        private void RefreshTitleScreenUi() {
            if (TitleScreenStateText == null) return;
            var on = TitleScreen.Enabled;
            TitleScreenStateText.Text = on ? "表示する" : "表示しない";
            var selected = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4B));
            var normal = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34));
            TitleScreenOnButton.Background = on ? selected : normal;
            TitleScreenOffButton.Background = on ? normal : selected;
        }

        private void SetTitleScreenEnabled(bool on) {
            TitleScreen.Enabled = on;
            RefreshTitleScreenUi();
            SaveNote.Text = "変更を保存しました。タイトル画面の設定は次回起動から反映されます。";
            GtSfx.PlaySelect();
        }

        private void OnTitleScreenOnClick(object sender, RoutedEventArgs e) { e.Handled = true; SetTitleScreenEnabled(true); }
        private void OnTitleScreenOffClick(object sender, RoutedEventArgs e) { e.Handled = true; SetTitleScreenEnabled(false); }

        /// <summary>今の設定値を表示に反映（選ばれている方のボタンを明るく・動画が無ければ注意書き・音量スライダー）。</summary>
        private void RefreshOpeningUi() {
            if (OpeningStateText == null) return;
            var on = GtOpening.Enabled;
            var available = GtOpening.Available;
            OpeningStateText.Text = on ? "再生する" : "再生しない";
            var selected = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4B));
            var normal = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34));
            OpeningOnButton.Background = on ? selected : normal;
            OpeningOffButton.Background = on ? normal : selected;
            OpeningMissingNote.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            OpeningPlayButton.IsEnabled = available;
            var intro = GtOpening.IntroEnabled;
            IntroStateText.Text = intro ? "表示する" : "表示しない";
            IntroOnButton.Background = intro ? selected : normal;
            IntroOffButton.Background = intro ? normal : selected;
            IntroPlayButton.IsEnabled = GtOpening.IntroAvailable;
            var wasLoading = _loading;
            _loading = true;
            MovieVolumeSlider.Value = GtOpening.VolumePercent;
            _loading = wasLoading;
            UpdateMovieVolumeText();
        }

        private void UpdateMovieVolumeText() {
            if (MovieVolumeText == null) return;
            var percent = (int)MovieVolumeSlider.Value;
            MovieVolumeText.Text = percent == 0 ? "消音  0％" : percent.ToString(CultureInfo.InvariantCulture) + "％";
        }

        private void OnMovieVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            UpdateMovieVolumeText();
            if (_loading || !IsLoaded) return;
            GtOpening.VolumePercent = (int)Math.Round(MovieVolumeSlider.Value);
        }

        private void StepMovieVolume(int delta) {
            var previous = MovieVolumeSlider.Value;
            MovieVolumeSlider.Value = Math.Max(0, Math.Min(100, previous + delta));
            if (MovieVolumeSlider.Value != previous) GtSfx.PlaySelect();
        }

        private void OnMovieVolumeDecreaseClick(object sender, RoutedEventArgs e) { e.Handled = true; StepMovieVolume(-5); }
        private void OnMovieVolumeIncreaseClick(object sender, RoutedEventArgs e) { e.Handled = true; StepMovieVolume(5); }
        private void OnMovieVolumeReleased(object sender, MouseButtonEventArgs e) { GtSfx.PlaySelect(); }

        private void OnMovieVolumeKeyUp(object sender, KeyEventArgs e) {
            if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down
                    || e.Key == Key.Home || e.Key == Key.End || e.Key == Key.PageUp || e.Key == Key.PageDown) GtSfx.PlaySelect();
        }

        private void OnMovieVolumePreviewClick(object sender, RoutedEventArgs e) { e.Handled = true; GtSfx.PlaySelect(); GtOpening.PreviewSound(); }

        private void OnMovieVolumeResetClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            MovieVolumeSlider.Value = GtOpening.DefaultVolumePercent;
            GtOpening.VolumePercent = GtOpening.DefaultVolumePercent;
            GtSfx.PlaySelect();
        }

        private void SetIntroEnabled(bool on) {
            GtOpening.IntroEnabled = on;
            RefreshOpeningUi();
            SaveNote.Text = "変更は自動的に保存されます";
            GtSfx.PlaySelect();
        }

        private void OnIntroOnClick(object sender, RoutedEventArgs e) { e.Handled = true; SetIntroEnabled(true); }
        private void OnIntroOffClick(object sender, RoutedEventArgs e) { e.Handled = true; SetIntroEnabled(false); }

        private void OnIntroPlayClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            CommitAssist();
            CancelCapture();
            GtSfx.PlaySelect();
            GtOpening.Play(Window.GetWindow(this), "settings-intro", intro: true, movie: false);
        }

        private void SetOpeningEnabled(bool on) {
            GtOpening.Enabled = on;
            RefreshOpeningUi();
            SaveNote.Text = "変更は自動的に保存されます";
            GtSfx.PlaySelect();
        }

        private void OnOpeningOnClick(object sender, RoutedEventArgs e) { e.Handled = true; SetOpeningEnabled(true); }
        private void OnOpeningOffClick(object sender, RoutedEventArgs e) { e.Handled = true; SetOpeningEnabled(false); }

        private void OnOpeningPlayClick(object sender, RoutedEventArgs e) {
            e.Handled = true;
            CommitAssist();
            CancelCapture();
            GtSfx.PlaySelect();
            GtOpening.Play(Window.GetWindow(this), "settings");
        }

        internal string TestDisplayDump() {
            return "display=tab:" + _displayTab + " opening:" + GtOpening.Enabled + " file:" + GtOpening.Available
                    + " intro:" + GtOpening.IntroEnabled + " introfile:" + GtOpening.IntroAvailable
                    + " volume:" + GtOpening.VolumePercent + " slider:" + (int)MovieVolumeSlider.Value
                    + " title:" + TitleScreen.Enabled + " titleState:" + TitleScreenStateText.Text + Environment.NewLine;
        }

        internal bool TestTitleCommand(string command) {
            if (command != "on" && command != "off") return false;
            SelectDisplayTab();
            var on = command == "on";
            (on ? TitleScreenOnButton : TitleScreenOffButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return TitleScreen.Enabled == on && TitleScreenStateText.Text == (on ? "表示する" : "表示しない");
        }

        internal bool TestOpeningCommand(string command) {
            if (command == "tab") { SelectDisplayTab(); return _displayTab; }
            if (command == "on") { SetOpeningEnabled(true); return GtOpening.Enabled; }
            if (command == "off") { SetOpeningEnabled(false); return !GtOpening.Enabled; }
            if (command == "play") { OpeningPlayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return GtOpening.IsShowing; }
            if (command == "intro-on") { SetIntroEnabled(true); return GtOpening.IntroEnabled; }
            if (command == "intro-off") { SetIntroEnabled(false); return !GtOpening.IntroEnabled; }
            if (command == "intro-play") { IntroPlayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return GtOpening.IsShowing; }
            if (command == "preview") { MovieVolumePreviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return true; }
            if (command == "reset") { MovieVolumeResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return GtOpening.VolumePercent == GtOpening.DefaultVolumePercent; }
            int number;
            if (command.StartsWith("volume:") && int.TryParse(command.Substring(7), out number) && number >= 0 && number <= 100) {
                MovieVolumeSlider.Value = number;
                return GtOpening.VolumePercent == number;
            }
            return false;
        }

        internal static bool HandleBindingPad(GtPadCommand command) {
            if (!IsCapturingKey) return false;
            if (command == GtPadCommand.Back) { _captureOwner.CancelCapture(); GtSfx.PlayCancel(); }
            return true; // Never assign a synthetic Enter/Escape from the pad as a keyboard binding.
        }

        internal string TestKeyboardDump() {
            return "keyboard=tab:" + _keyboardTab + " assist:" + (_keyboardSettings?.Assist ?? -1)
                    + " editable:" + KeyboardEditor.IsEnabled + " capture:" + (_waitingBinding ?? "-")
                    + " bindings:" + (_keyboardSettings == null ? "-" : string.Join(",", GtKeyboardSettings.Actions.Select(x => x.Id + ":" + _keyboardSettings.GetKey(x.Id))))
                    + Environment.NewLine;
        }

        internal bool TestKeyboardCommand(string command) {
            if (command == "tab") { SelectTab(true); return true; }
            if (command == "sound") { SelectTab(false); return true; }
            if (command == "cancel") { CancelCapture(); return true; }
            if (command.StartsWith("capture:")) { BeginCapture(command.Substring(8)); return _waitingBinding != null; }
            int number;
            if (command.StartsWith("key:") && int.TryParse(command.Substring(4), out number)) return AssignCapturedKey(number);
            if (command.StartsWith("assist:") && int.TryParse(command.Substring(7), out number) && number >= 0 && number <= 100) {
                RefreshDrivingState();
                if (!KeyboardEditor.IsEnabled) return false;
                AssistSlider.Value = number;
                CommitAssist();
                return _keyboardSettings.Assist == number;
            }
            if (command == "reset") return ResetKeyboardBindings();
            return false;
        }
    }
}
