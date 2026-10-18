using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AcTools.DataFile;

namespace AcManager.Pages.Gt {
    /// <summary>Saved bindings in a physical-control layout; never acquires a device or sends input.</summary>
    internal sealed class GtControllerBindingsPanel : UserControl {
        private sealed class Choice {
            public string Id, Label;
            public override string ToString() => Label;
        }
        private sealed class ActionInfo {
            public string Section, Label;
            public bool Pad;
            public ActionInfo(string section, string label, bool pad = false) { Section = section; Label = label; Pad = pad; }
        }
        private sealed class Physical {
            public string Id, Label, Mark, Fixed;
            public Point Point;
            public bool Right;
            public ComboBox Editor;
            public Border Row;
            public Polyline Line;
            public Ellipse Dot;
            public string Selected;
        }
        private static readonly ActionInfo[] Actions = {
            new ActionInfo("GEARUP", "シフトアップ", true), new ActionInfo("GEARDN", "シフトダウン", true),
            new ActionInfo("HANDBRAKE", "ハンドブレーキ", true), new ActionInfo("__EXT_KEYBOARD_CLUTCH", "クラッチ（ボタン）", true),
            new ActionInfo("ACTION_CHANGE_CAMERA", "視点切替", true), new ActionInfo("ACTION_HORN", "ホーン", true),
            new ActionInfo("ACTION_HEADLIGHTS", "ヘッドライト切替", true), new ActionInfo("ACTION_HEADLIGHTS_FLASH", "パッシング", true),
            new ActionInfo("GLANCELEFT", "左を見る", true), new ActionInfo("GLANCERIGHT", "右を見る", true),
            new ActionInfo(GtControllerBindingsSettings.RearViewSection, "後ろを見る（車内は独自）", true),
            new ActionInfo("ABSUP", "ABSを強く"), new ActionInfo("ABSDN", "ABSを弱く"),
            new ActionInfo("TCUP", "TCを強く"), new ActionInfo("TCDN", "TCを弱く"),
            new ActionInfo("TURBOUP", "ターボを上げる"), new ActionInfo("TURBODN", "ターボを下げる"),
            new ActionInfo("__CM_PAUSE", "ポーズ"), new ActionInfo("__EXT_CMD_RESET", "車をリセット"),
            new ActionInfo("__APP_TIMESHIFT_REWIND", "巻き戻し（TimeShift）"),
            new ActionInfo(GtControllerBindingsSettings.RepairSection, "車を修理（CarRepairKey）"),
            new ActionInfo(GtControllerBindingsSettings.StarterSection, "エンジン始動（押し続ける）"),
            new ActionInfo("__EXT_LOW_BEAM", "ロービーム"), new ActionInfo("__EXT_TURNSIGNAL_LEFT", "ウインカー左"),
            new ActionInfo("__EXT_TURNSIGNAL_RIGHT", "ウインカー右"), new ActionInfo("__EXT_HAZARDS", "ハザード"),
            new ActionInfo("__CM_TO_PITS", "ピットへ戻る"), new ActionInfo("__EXT_LIGHT_A", "追加ライトA"),
            new ActionInfo("__EXT_TELLTALE_RESET", "警告灯をリセット"), new ActionInfo("__EXT_WIPERS_MORE", "ワイパーを強く")
        };
        // DirectInput zero-based indices. Official ES documents disagree about X/Y (2/3).
        private static readonly Dictionary<int, string> WheelNames = new Dictionary<int, string> {
            {0,"A"},{1,"B"},{2,"X"},{3,"Y"},   // [fork] 2026-10-04 要望 実機確認「ボタン3が X、ボタン4が Y」（公式資料は食い違っていた）
            {4,"十字 ↑"},{5,"十字 →"},{6,"十字 ↓"},{7,"十字 ←"},
            {12,"左パドル"},{13,"右パドル"},{18,"N"},{19,"WIP"},{20,"FL"},
            {31,"P"},{32,"BOX"},{33,"PL"},{21,"CAM"},{22,"RADIO"},{23,"S1"},
            {24,"HOME"},{34,"R"},{35,"START"},{36,"S2"},{37,"MENU"}
        };
        private static readonly Brush Muted = Ink(0x98,0x98,0x9B), LineInk = Ink(0x55,0x55,0x58), Outline = Ink(0x4B,0x4B,0x4E);
        private static readonly Brush Accent = Ink(0xEB,0xDE,0xBB), Card = Ink(0x1C,0x1C,0x1F), BoardInk = Ink(0x17,0x17,0x19);
        private readonly GtControllerBindingsSettings _settings;
        private readonly TextBlock _source = Text("", 12), _status = Text("", 13), _axes = Text("", 12), _pressed = Text("", 13);
        // [fork] 「いま押しているボタン」を MOZA 表記（DirectInput + 1）で出す。読むだけで何も送らない
        private readonly DispatcherTimer _pressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private readonly StackPanel _content = new StackPanel(), _other = new StackPanel();
        private readonly List<Physical> _physical = new List<Physical>();
        private readonly Button _wheelTab, _padTab, _menuTab, _reset;
        private readonly Style _comboStyle = CreateComboStyle();
        private int _mode;
        private bool _suppressSelection;
        public event Action FocusItemsChanged;

        public GtControllerBindingsPanel(GtControllerBindingsSettings settings = null) {
            _settings = settings ?? new GtControllerBindingsSettings();
            FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");
            Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar),CreateScrollBarStyle());
            MaxWidth = 1400; Margin = new Thickness(28,16,28,12);
            var dock = new DockPanel();
            var top = new DockPanel { Margin = new Thickness(0,0,0,8) };
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            _wheelTab = MakeButton("MOZA ES",()=>ShowMode(0));
            _padTab = MakeButton("DUALSHOCK 4",()=>ShowMode(1));
            _menuTab = MakeButton("CM7の操作",()=>ShowMode(2));
            tabs.Children.Add(_wheelTab); tabs.Children.Add(_padTab); tabs.Children.Add(_menuTab);
            DockPanel.SetDock(tabs,Dock.Left); top.Children.Add(tabs);
            _source.Foreground = Muted; _source.TextAlignment = TextAlignment.Right;
            _source.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(_source,Dock.Right); top.Children.Add(_source);
            // [fork] 「いま押しているボタン」は常に見える上の帯に出す（MOZA 表記）
            _pressed.Foreground = Accent; _pressed.FontSize = 13; _pressed.Margin = new Thickness(24,0,24,0);
            _pressed.VerticalAlignment = VerticalAlignment.Center; _pressed.TextWrapping = TextWrapping.NoWrap; _pressed.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(_pressed);
            DockPanel.SetDock(top,Dock.Top); dock.Children.Add(top);
            var footer = new DockPanel { Margin = new Thickness(0,8,0,0) };
            _reset = MakeButton("以前のCMの割当へ戻す",ResetWheel);
            _reset.FontSize = 12; _reset.Padding = new Thickness(12,6,12,6);
            DockPanel.SetDock(_reset,Dock.Right); footer.Children.Add(_reset);
            _status.Foreground = Accent; _status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_status);
            DockPanel.SetDock(footer,Dock.Bottom); dock.Children.Add(footer);
            dock.Children.Add(new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            Content = dock; ShowMode(0);
            _pressTimer.Tick += (s,e) => UpdatePressed();
            Loaded += (s,e) => { _pressTimer.Start(); UpdatePressed(); };
            Unloaded += (s,e) => { _pressTimer.Stop(); CancelBrowse(); };
        }
        // [fork] MOZA の番号（Pit House・公式ガイド）= DirectInput の番号 + 1。AC の controls.ini・pad.txt・Input Probe は 0 始まり。
        private static string MozaNo(int id) => "MOZA "+(id+1);
        private static string WheelTag(int id) => WheelNames.ContainsKey(id) ? WheelNames[id]+"（"+MozaNo(id)+"）" : "ボタン（"+MozaNo(id)+"）";
        private void UpdatePressed() {
            if (_mode!=0) return;
            if (unchecked(Environment.TickCount-GtUniversalInput.RawStamp)>1500 || GtUniversalInput.RawStamp==0) {
                _pressed.Text = "押したボタン：読み取れていません（AC走行中は不可）";
                return;
            }
            var down = new List<string>();
            for (var i=0;i<GtUniversalInput.RawButtons.Length;i++) if (GtUniversalInput.RawButtons[i])
                down.Add(WheelNames.ContainsKey(i) ? WheelNames[i]+" ・ "+MozaNo(i) : MozaNo(i));
            _pressed.Text = down.Count==0 ? "押したボタン：なし（押すと MOZA 表記の番号が出ます）"
                    : "押したボタン：" + string.Join("  /  ", down.ToArray());
        }
        private static Brush Ink(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r,g,b));
        private static TextBlock Text(string value,double size) => new TextBlock {
            Text = value, FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap
        };
        private static Button MakeButton(string label,Action click) {
            var button = new Button { Content = label, Background = Brushes.Transparent, Foreground = Brushes.White,
                BorderBrush = LineInk, BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(15,7,15,7),
                Margin = new Thickness(0,0,10,0), FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand };
            button.Click += (s,e) => { e.Handled = true; click(); };
            return button;
        }
        private static Style CreateComboStyle() => (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ComboBox}'>
 <Setter Property='Foreground' Value='#EEEEEE'/><Setter Property='Background' Value='#1B1B1D'/>
 <Setter Property='BorderBrush' Value='#77777A'/><Setter Property='BorderThickness' Value='1'/>
 <Setter Property='Padding' Value='9,3,25,3'/><Setter Property='FontSize' Value='13'/>
 <Setter Property='HorizontalContentAlignment' Value='Left'/><Setter Property='VerticalContentAlignment' Value='Center'/>
 <Setter Property='ScrollViewer.VerticalScrollBarVisibility' Value='Auto'/><Setter Property='MaxDropDownHeight' Value='330'/>
 <Setter Property='ItemContainerStyle'><Setter.Value><Style TargetType='{x:Type ComboBoxItem}'>
  <Setter Property='Foreground' Value='#EEEEEE'/><Setter Property='Background' Value='#202023'/>
  <Setter Property='Padding' Value='9,7'/><Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='{x:Type ComboBoxItem}'><Border x:Name='Cell' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'>
    <ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'>
     <Setter TargetName='Cell' Property='Background' Value='#505052'/></Trigger><Trigger Property='IsSelected' Value='True'>
     <Setter Property='Foreground' Value='#F2E4BF'/></Trigger></ControlTemplate.Triggers></ControlTemplate>
  </Setter.Value></Setter></Style></Setter.Value></Setter>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBox}'>
  <Grid><ToggleButton x:Name='Toggle' Focusable='False' ClickMode='Press'
    IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
   <ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'>
    <Border Background='{Binding Background, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}'
      BorderBrush='{Binding BorderBrush, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}' BorderThickness='1'>
     <Path Data='M 0 0 L 7 0 M 0 3 L 7 3 M 0 6 L 7 6' Stroke='#A7A7AA' StrokeThickness='1'
       HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,9,0'/></Border>
   </ControlTemplate></ToggleButton.Template></ToggleButton>
   <ContentPresenter Margin='{TemplateBinding Padding}' Content='{TemplateBinding SelectionBoxItem}'
    ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' IsHitTestVisible='False'
    HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
   <Popup x:Name='PART_Popup' Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False' PopupAnimation='None'>
    <Border Background='#202023' BorderBrush='#77777A' BorderThickness='1' MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
     MaxHeight='{TemplateBinding MaxDropDownHeight}'><ScrollViewer CanContentScroll='True'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer></Border>
   </Popup></Grid><ControlTemplate.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
   </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        private static Style CreateScrollBarStyle() => (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
 TargetType='{x:Type ScrollBar}'><Setter Property='Width' Value='7'/><Setter Property='MinHeight' Value='0'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'>
  <Grid Background='#1B1B1D'><Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='Vertical'>
   <Track.DecreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False'>
    <RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
   </RepeatButton></Track.DecreaseRepeatButton>
   <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Border Background='#56565A' Margin='2,0'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
   <Track.IncreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False'>
    <RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
   </RepeatButton></Track.IncreaseRepeatButton>
  </Track></Grid></ControlTemplate></Setter.Value></Setter></Style>");
        private static string Value(IniFile ini,string section,string key) => ini != null && ini.ContainsKey(section) ? ini[section].GetPossiblyEmpty(key) : null;
        private static int Number(IniFile ini,string section,string key,int fallback = -1) { int n; return int.TryParse(Value(ini,section,key),out n) ? n : fallback; }
        private static string WheelName(int id) => WheelNames.ContainsKey(id) ? WheelNames[id] : "ボタン"+(id+1);
        private static string ActionName(string section) => Actions.FirstOrDefault(x=>x.Section==section)?.Label
                ?? (section == "GLANCEBACK" ? "後ろを見る（AC標準）" : section == "app.CarRepairKey/Repair car" ? "車を修理（CarRepairKey）" : section);
        private static string DeviceName(IniFile ini,int joy) => Value(ini,"CONTROLLERS","CON"+joy) ?? "未接続の機器";
        private static bool SimpleWheelButton(IniFile ini,string section) => Number(ini,"STEER","JOY") >= 0
                && Number(ini,section,"JOY") == Number(ini,"STEER","JOY") && Number(ini,section,"BUTTON") >= 0
                && Number(ini,section,"AXLE") < 0 && Number(ini,section,"__CM_POV") < 0 && Number(ini,section,"POV") < 0
                && Number(ini,section,"BUTTON_MODIFICATOR") < 0 && Number(ini,section,"JOY_MODIFICATOR") < 0
                && Number(ini,section,"__CM_ALT_BUTTON") < 0 && Number(ini,section,"__CM_ALT_POV") < 0;
        private static string DescribeWheel(IniFile ini,string section) {
            var joy = Number(ini,section,"JOY"); if (joy < 0) return "未割当";
            string label;
            if (Number(ini,section,"AXLE") >= 0) label = "軸 "+Number(ini,section,"AXLE");
            else if (Number(ini,section,"__CM_POV") >= 0) {
                var dir = Number(ini,section,"__CM_POV_DIR"); label = "十字 "+(dir>=0 && dir<4 ? new[]{"←","↑","→","↓"}[dir] : "方向未設定");
            } else if (Number(ini,section,"BUTTON") >= 0) label = joy == Number(ini,"STEER","JOY") ? WheelTag(Number(ini,section,"BUTTON")) : "ボタン"+(Number(ini,section,"BUTTON")+1);
            else return "未割当";
            if (Number(ini,section,"BUTTON_MODIFICATOR") >= 0) label += " ＋ 修飾ボタン";
            return joy == Number(ini,"STEER","JOY") ? label : DeviceName(ini,joy)+" · "+label;
        }
        private static bool AvailableWheelAction(IniFile ini,string section) {
            if (section==GtControllerBindingsSettings.RearViewSection && !AvailableWheelAction(ini,"GLANCEBACK")) return false;
            // [fork] 車を修理 は今シフター側のボタンに入っている。ハンドル以外の機器に入っていても、単純なボタン割当なら選べるようにする
            //   （選ぶとハンドルのボタンへ移る）。他の操作は従来どおり、別の機器に入っている物は候補に出さない
            var movable = section==GtControllerBindingsSettings.RepairSection || section==GtControllerBindingsSettings.StarterSection;   // [fork] 別機器に入っていても選べる
            if (Number(ini,section,"AXLE")>=0 || Number(ini,section,"__CM_POV")>=0 || Number(ini,section,"POV")>=0
                    || Number(ini,section,"BUTTON_MODIFICATOR")>=0 || Number(ini,section,"JOY_MODIFICATOR")>=0
                    || Number(ini,section,"__CM_ALT_BUTTON")>=0 || Number(ini,section,"__CM_ALT_POV")>=0) return false;
            return movable || Number(ini,section,"JOY")<0 || Number(ini,section,"JOY")==Number(ini,"STEER","JOY");
        }

        internal void ShowMode(int mode) {
            _browsing = null;   // [fork] 画面を作り直すので、パッドの選択中は破棄
            _mode = Math.Max(0,Math.Min(2,mode)); _content.Children.Clear(); _physical.Clear(); _other.Children.Clear(); _status.Text = "";
            _pressed.Visibility = _mode==0 ? Visibility.Visible : Visibility.Collapsed;
            _wheelTab.BorderBrush = _mode==0 ? Accent : LineInk; _padTab.BorderBrush = _mode==1 ? Accent : LineInk; _menuTab.BorderBrush = _mode==2 ? Accent : LineInk;
            _reset.Visibility = _mode==0 ? Visibility.Visible : Visibility.Collapsed;
            try {
                if (_mode==2) BuildMenu();
                else {
                    var pad = _mode==1; var ini = pad ? _settings.ReadPad() : _settings.ReadWheel();
                    UpdateSource(); BuildCallouts(ini,pad); RefreshSelections(ini,pad); BuildOtherBindings(ini,pad);
                }
                RefreshState();
            } catch(Exception e) { _status.Text = "設定を読み込めませんでした："+e.Message; }
            FocusItemsChanged?.Invoke();
        }
        private void UpdateSource() { _source.Text = _mode==0 ? System.IO.Path.GetFileName(_settings.WheelPath)+"  ·  変更はCM7用に保存" : "走行用コントローラー割り当て"; }
        internal void RefreshState() {
            var editable = _settings.CanEdit;
            foreach (var item in _physical.Where(x=>x.Editor!=null)) item.Editor.IsEnabled = editable;
            _reset.IsEnabled = _settings.CanReset;
            if (!editable) _status.Text = "ACを終了すると割当を変更できます。現在は表示のみです。";
            else if (_mode!=2 && !string.IsNullOrEmpty(_settings.StatusText)) _status.Text = _settings.StatusText;
            else if (_status.Text.StartsWith("ACを終了") || string.IsNullOrEmpty(_status.Text))
                _status.Text = _mode==2 ? "メニュー用のボタンを選択 · 変更は自動保存され、約2秒で読み直されます。" : "ボタン横のプルダウンから操作を選択 · 変更は自動保存されます。";
        }
        internal void Reload() { ShowMode(_mode); }
        private void Define(string id,string label,string mark,double x,double y,bool right = false,string fixedAction = null) {
            _physical.Add(new Physical { Id=id,Label=label,Mark=mark,Point=new Point(x,y),Right=right,Fixed=fixedAction });
        }
        private void Wheel(int index,string mark,double x,double y,bool right=false) => Define(index.ToString(),WheelTag(index),mark,x,y,right);   // [fork] 名前＋MOZA 表記の番号
        private void BuildCallouts(IniFile ini,bool pad) {
            var board = new Canvas { Width=1180,Height=640,Background=BoardInk,ClipToBounds=false };   // [fork] 下の説明文が 2 行になるので 30 高くした
            var title = Text(pad ? "DUALSHOCK 4" : "MOZA ES STEERING WHEEL",19); title.FontWeight=FontWeights.SemiBold;
            title.Width=600; title.TextAlignment=TextAlignment.Center; Place(board,title,290,8);
            if (pad) {
                Define("axis:brake","L2 トリガー","L2",407,172,false,"ブレーキ（アナログ）");
                Define("LSHOULDER","L1 ボタン","L1",407,218);
                Define("DPAD_UP","方向キー ↑","↑",411,292); Define("DPAD_LEFT","方向キー ←","←",386,319);
                Define("DPAD_RIGHT","方向キー →","→",438,319); Define("DPAD_DOWN","方向キー ↓","↓",411,347);
                Define("LTHUMB_PRESS","L3 ボタン","L3",500,409); Define("BACK","SHARE ボタン","SHARE",505,266);
                Define("axis:gas","R2 トリガー","R2",773,172,true,"アクセル（アナログ）");
                Define("RSHOULDER","R1 ボタン","R1",773,218,true);
                Define("Y","△ ボタン","△",770,287,true); Define("X","□ ボタン","□",735,321,true);
                Define("B","○ ボタン","○",805,321,true); Define("A","× ボタン","×",770,355,true);
                Define("RTHUMB_PRESS","R3 ボタン","R3",680,409,true); Define("START","OPTIONS ボタン","OPTIONS",675,266,true,"ポーズメニュー");
            } else {
                Wheel(12,"−",389,265); Wheel(18,"N",425,283); Wheel(19,"WIP",455,265); Wheel(20,"FL",507,292);
                Wheel(4,"↑",454,326); Wheel(7,"←",430,350); Wheel(5,"→",478,350); Wheel(6,"↓",454,374);
                Wheel(21,"CAM",425,422); Wheel(22,"RADIO",486,466); Wheel(23,"S1",568,453); Wheel(24,"HOME",590,485);
                Wheel(13,"＋",791,265,true); Wheel(31,"P",755,283,true); Wheel(32,"BOX",725,265,true); Wheel(33,"PL",673,292,true);
                // [fork] 2026-10-04 要望「X と Y 逆や」。ボタン3=X は左、ボタン4=Y は上（Xbox と同じ並び）。一覧は上から Y → X の順
                Wheel(3,"Y",726,326,true); Wheel(2,"X",702,350,true); Wheel(1,"B",750,350,true); Wheel(0,"A",726,374,true);
                Wheel(34,"R",755,422,true); Wheel(35,"START",694,466,true); Wheel(36,"S2",612,453,true); Wheel(37,"MENU",590,515,true);
            }
            // Lines are behind the rim and controls, like the reference; the selected line is emphasized.
            foreach (var side in new[]{false,true}) {
                var controls = _physical.Where(x=>x.Right==side).ToArray();
                for(var i=0;i<controls.Length;i++) AddCallout(board,controls[i],i,controls.Length,pad);
            }
            if (pad) DrawPad(board); else DrawWheel(board);
            foreach(var control in _physical) { board.Children.Remove(control.Line); board.Children.Add(control.Line); }
            foreach(var control in _physical) DrawControl(board,control);
            var note = Text(pad ? "左スティック：操舵  ／  L3・R3：押し込み"
                    : "番号は MOZA 表記（Pit House・公式ガイドと同じ）。AC の設定ファイル・pad.txt・Input Probe の内部番号は MOZA 表記 −1（例：MOZA 36 ＝ 内部 35）",11);
            note.Foreground=Muted; note.Width=900; note.TextAlignment=TextAlignment.Center; Place(board,note,140,587);
            _content.Children.Add(new Viewbox { Child=board,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.Both,Margin=new Thickness(0,0,10,0) });
            _axes.Text = pad ? "アナログ入力   左スティック：操舵　　R2：アクセル　　L2：ブレーキ"
                    : "アナログ入力   操舵："+DescribeWheel(ini,"STEER")+"　　アクセル："+DescribeWheel(ini,"THROTTLE")
                    +"　　ブレーキ："+DescribeWheel(ini,"BRAKES")+"　　クラッチ："+DescribeWheel(ini,"CLUTCH");
            _axes.Foreground=Muted; _axes.Margin=new Thickness(0,10,0,5); _content.Children.Add(_axes);
            UpdatePressed();
        }
        private void AddCallout(Canvas board,Physical control,int index,int count,bool pad) {
            var y = 13+index*(pad ? 71 : 47); var x = control.Right ? 954 : 0; var end = control.Right ? 944 : 236;
            var bend = control.Right ? 886 : 294; var lineY=y+32;
            control.Line = new Polyline { Stroke=LineInk,StrokeThickness=0.8,
                Points=new PointCollection(new[]{new Point(end,lineY),new Point(bend,lineY),control.Point}),IsHitTestVisible=false };
            board.Children.Add(control.Line);
            var stack = new StackPanel(); var caption=Text(control.Label,11.5); caption.Foreground=Muted; caption.Margin=new Thickness(0,0,0,4); stack.Children.Add(caption);
            if (control.Fixed!=null) {
                stack.Children.Add(new Border { BorderBrush=LineInk,BorderThickness=new Thickness(1),Padding=new Thickness(9,4,9,4),Height=29,
                    Child=Text(control.Fixed,12.5),ToolTip=control.Id=="START" ? "Optionsはポーズ用の固定操作です。" : "アナログ軸の現在の割当を表示しています。" });
            } else {
                control.Editor = new ComboBox { Style=_comboStyle,Height=29,Cursor=System.Windows.Input.Cursors.Hand };
                System.Windows.Automation.AutomationProperties.SetName(control.Editor,control.Label+"の操作");
                control.Editor.SelectionChanged += (s,e)=>SaveSelection(control);
                // A closed picker must release native focus so mouse edits can immediately return to GT pad/wheel navigation.
                control.Editor.DropDownClosed += (s,e)=>control.Editor.Dispatcher.BeginInvoke(new Action(()=> {
                    if (_browsing==control && !control.Editor.IsDropDownOpen) { _browsing=null; }   // [fork] マウスで閉じられた時に、パッドの選択中の印を残さない
                    if (control.Editor.IsKeyboardFocusWithin) System.Windows.Input.Keyboard.ClearFocus();
                }),System.Windows.Threading.DispatcherPriority.Background);
                stack.Children.Add(control.Editor);
            }
            control.Row = new Border { Width=226,Child=stack,BorderBrush=Brushes.Transparent,BorderThickness=new Thickness(0),Background=BoardInk };
            control.Row.MouseEnter+=(s,e)=>SetHot(control,true); control.Row.MouseLeave+=(s,e)=>SetHot(control,false);
            Place(board,control.Row,x,y);
        }
        private void RefreshSelections(IniFile ini,bool pad) {
            _suppressSelection=true;
            try {
                foreach(var control in _physical.Where(x=>x.Editor!=null)) {
                    int button; var wheel = int.TryParse(control.Id,out button);
                    var sections = ini.Where(x=>pad ? string.Equals(Value(ini,x.Key,"XBOXBUTTON"),control.Id,StringComparison.Ordinal)
                            : wheel && SimpleWheelButton(ini,x.Key) && Number(ini,x.Key,"BUTTON")==button).Select(x=>x.Key).ToArray();
                    if (sections.Contains(GtControllerBindingsSettings.RearViewSection)) sections=sections.Where(x=>x!="GLANCEBACK").ToArray();
                    var choices = new List<Choice> { new Choice { Id="",Label="設定なし" } };
                    choices.AddRange(Actions.Where(x=>pad ? x.Pad : AvailableWheelAction(ini,x.Section)).Select(x=>new Choice { Id=x.Section,Label=x.Label }));
                    var selected = sections.Length==0 ? "" : sections.Length==1 && choices.Any(x=>x.Id==sections[0]) ? sections[0] : "saved";
                    if (selected=="saved") choices.Insert(0,new Choice { Id="saved",Label=string.Join(" / ",sections.Select(ActionName)) });
                    control.Selected=selected; control.Editor.ItemsSource=choices; control.Editor.SelectedItem=choices.First(x=>x.Id==selected);
                    control.Editor.ToolTip=sections.Length>1 ? "保存済み："+string.Join(" / ",sections.Select(ActionName))+"。操作を選ぶと、このボタンの割当を置き換えます。"
                            : selected==GtControllerBindingsSettings.RearViewSection ? "運転席・ダッシュボード視点ではRearView Tuner、それ以外ではAC標準の後方視点。位置調整はRearView Tunerで行えます。"
                            : control.Label+"に割り当てる操作を選択";
                    if (control.Dot!=null) control.Dot.Fill=sections.Length>0 ? Muted : LineInk;
                }
            } finally { _suppressSelection=false; }
        }
        private void SaveSelection(Physical control) {
            if (_suppressSelection) return;
            var choice=control.Editor.SelectedItem as Choice;
            if (choice==null || choice.Id=="saved" || choice.Id==control.Selected) return;
            string error; int button;
            var ok=_mode==1 ? _settings.SetPadAction(control.Id,choice.Id,out error)
                    : int.TryParse(control.Id,out button) ? _settings.SetWheelAction(button,choice.Id,out error) : Fail(out error);
            if (ok) {
                var ini=_mode==1 ? _settings.ReadPad() : _settings.ReadWheel();
                RefreshSelections(ini,_mode==1); UpdateSource(); BuildOtherBindings(ini,_mode==1);
                _status.Text=control.Label+" → "+choice.Label+" を保存しました。";
                if (!string.IsNullOrEmpty(_settings.StatusText)) _status.Text += "  "+_settings.StatusText;
            } else {
                _suppressSelection=true;
                try { control.Editor.SelectedItem=control.Editor.Items.Cast<Choice>().First(x=>x.Id==control.Selected); }
                finally { _suppressSelection=false; }
                _status.Text=error;
            }
        }
        private static bool Fail(out string error) { error="この割当は変更できません。"; return false; }
        private void BuildOtherBindings(IniFile ini,bool pad) {
            _other.Children.Clear();
            if (!_content.Children.Contains(_other)) _content.Children.Add(_other);
            if (pad) return;
            var others=ini.Where(x=>Number(ini,x.Key,"JOY")>=0
                    && (Number(ini,x.Key,"BUTTON")>=0 || Number(ini,x.Key,"__CM_POV")>=0 || Number(ini,x.Key,"AXLE")>=0)
                    && !new[]{"STEER","THROTTLE","BRAKES","CLUTCH"}.Contains(x.Key)
                    && (!SimpleWheelButton(ini,x.Key) || !WheelNames.ContainsKey(Number(ini,x.Key,"BUTTON")))).ToArray();
            if (others.Length==0 && Number(ini,"SHIFTER","ACTIVE",0)==0) return;
            var list=new StackPanel();
            foreach(var item in others) {
                var line=Text(ActionName(item.Key)+"　"+DescribeWheel(ini,item.Key),12); line.Foreground=Muted; line.Margin=new Thickness(0,3,0,3); list.Children.Add(line);
            }
            if (Number(ini,"SHIFTER","ACTIVE",0)!=0) {
                var gears=Enumerable.Range(1,7).Select(x=>x+"速: "+(Number(ini,"SHIFTER","GEAR_"+x)<0 ? "未割当" : "ボタン"+(Number(ini,"SHIFTER","GEAR_"+x)+1)))
                    .Concat(new[]{"R: "+(Number(ini,"SHIFTER","GEAR_R")<0 ? "未割当" : "ボタン"+(Number(ini,"SHIFTER","GEAR_R")+1))});
                var shifter=Text("Hシフター　"+DeviceName(ini,Number(ini,"SHIFTER","JOY"))+"\n"+string.Join("  /  ",gears),12); shifter.Foreground=Muted; list.Children.Add(shifter);
            }
            _other.Children.Add(new Expander { Header="他の機器・複合入力の保存済み割当",Content=list,Foreground=Muted,FontSize=12,Margin=new Thickness(0,5,0,0),IsExpanded=false });
        }
        private void ResetWheel() {
            string error; var ok=_settings.ResetWheel(out error); ShowMode(0); _status.Text=ok ? "以前のCMで保存したR5の割当に戻しました。" : error;
        }
        private static void Place(Canvas canvas,FrameworkElement element,double x,double y) { Canvas.SetLeft(element,x); Canvas.SetTop(element,y); canvas.Children.Add(element); }
        private static void PathShape(Canvas canvas,string geometry,Brush fill,Brush stroke,double thickness=1) {
            canvas.Children.Add(new System.Windows.Shapes.Path { Data=Geometry.Parse(geometry),Fill=fill,Stroke=stroke,StrokeThickness=thickness,IsHitTestVisible=false });
        }
        private static void Circle(Canvas canvas,double x,double y,double radius,Brush fill,Brush stroke,double width=1) {
            Place(canvas,new Ellipse { Width=radius*2,Height=radius*2,Fill=fill,Stroke=stroke,StrokeThickness=width,IsHitTestVisible=false },x-radius,y-radius);
        }
        private static void Label(Canvas canvas,string text,double x,double y,double width,double size=12) {
            var label=Text(text,size); label.Width=width; label.TextAlignment=TextAlignment.Center; label.Foreground=Muted; label.IsHitTestVisible=false; Place(canvas,label,x,y);
        }
        private static void DrawWheel(Canvas board) {
            // ES flat-bottom rim, thumb grips, two paddles and three-spoke face plate.
            PathShape(board,"M 590 83 C 450 83 354 188 354 330 C 354 447 395 539 463 562 L 717 562 C 785 539 826 447 826 330 C 826 188 730 83 590 83 Z",BoardInk,Outline,1.3);
            PathShape(board,"M 590 116 C 475 116 389 202 389 329 C 389 422 422 505 477 528 L 703 528 C 758 505 791 422 791 329 C 791 202 705 116 590 116 Z",null,Outline);
            PathShape(board,"M 590 91 C 456 91 365 192 365 330 C 365 442 405 530 467 550 L 713 550 C 775 530 815 442 815 330 C 815 192 724 91 590 91",null,Ink(0x30,0x30,0x33));
            PathShape(board,"M 580 84 L 580 116 M 600 84 L 600 116",null,Muted);
            PathShape(board,"M 379 232 Q 391 222 401 235 L 414 282 L 406 306 L 384 303 Z",Card,Outline);
            PathShape(board,"M 801 232 Q 789 222 779 235 L 766 282 L 774 306 L 796 303 Z",Card,Outline);
            PathShape(board,"M 382 401 L 404 393 L 415 446 L 400 468 L 389 454 Z M 798 401 L 776 393 L 765 446 L 780 468 L 791 454 Z",Card,Outline);
            PathShape(board,"M 406 309 L 408 281 Q 416 260 452 248 L 513 272 L 538 259 Q 590 248 642 259 L 667 272 L 728 248 Q 764 260 772 281 L 774 309 L 789 392 L 753 410 Q 711 414 678 447 L 638 474 L 623 524 L 557 524 L 542 474 L 502 447 Q 469 414 427 410 L 391 392 Z",Card,Outline,1.2);
            PathShape(board,"M 402 393 Q 482 414 512 453 L 548 478 L 555 513 M 778 393 Q 698 414 668 453 L 632 478 L 625 513",null,Muted,1.1);
            PathShape(board,"M 540 279 Q 590 271 640 279",null,Muted,3);
            PathShape(board,"M 552 326 L 568 309 L 612 309 L 628 326 L 635 366 L 611 399 L 569 399 L 545 366 Z",BoardInk,Outline,1.3);
            PathShape(board,"M 558 329 L 572 315 L 608 315 L 622 329 L 628 364 L 608 392 L 572 392 L 552 364 Z",null,Outline);
            Label(board,"MOZA",552,339,76,16); Label(board,"R A C I N G",550,360,80,7.5);
            foreach(var p in new[]{new Point(554,294),new Point(626,294),new Point(527,349),new Point(653,349),new Point(554,418),new Point(626,418)}) {
                Circle(board,p.X,p.Y,9,null,Outline); Circle(board,p.X,p.Y,3,null,Outline);
            }
            Circle(board,486,466,30,null,Outline); Circle(board,486,466,23,null,Outline);
            Circle(board,694,466,30,null,Outline); Circle(board,694,466,23,null,Outline);
            PathShape(board,"M 444 318 L 464 318 L 464 340 L 487 340 L 487 360 L 464 360 L 464 383 L 444 383 L 444 360 L 421 360 L 421 340 L 444 340 Z",BoardInk,Outline);
        }
        private static void DrawPad(Canvas board) {
            PathShape(board,"M 386 224 Q 348 233 341 283 L 316 450 Q 308 505 347 519 Q 373 529 397 491 L 437 435 Q 459 420 486 439 Q 512 464 538 442 L 642 442 Q 668 464 694 439 Q 721 420 743 435 L 783 491 Q 807 529 833 519 Q 872 505 864 450 L 839 283 Q 832 233 794 224 L 680 224 Q 590 210 500 224 Z",Card,Outline,1.5);
            PathShape(board,"M 355 326 L 333 451 Q 328 490 349 502 M 825 326 L 847 451 Q 852 490 831 502",null,Outline);
            PathShape(board,"M 377 152 Q 407 143 437 152 L 439 190 L 375 190 Z M 743 152 Q 773 143 803 152 L 805 190 L 741 190 Z",Card,Outline);
            PathShape(board,"M 375 202 L 439 202 L 443 232 L 371 232 Z M 741 202 L 805 202 L 809 232 L 737 232 Z",Card,Outline);
            PathShape(board,"M 538 247 Q 590 242 642 247 L 647 329 L 533 329 Z",BoardInk,Outline);
            PathShape(board,"M 545 257 L 635 257 M 545 267 L 635 267 M 545 277 L 635 277",null,Ink(0x28,0x28,0x2B));
            Circle(board,500,409,43,BoardInk,Outline); Circle(board,500,409,31,null,Outline);
            Circle(board,680,409,43,BoardInk,Outline); Circle(board,680,409,31,null,Outline);
            Circle(board,590,386,15,BoardInk,Outline); Label(board,"PS",573,376,34,13);
            PathShape(board,"M 578 351 L 602 351 M 578 357 L 602 357",null,Muted);
            PathShape(board,"M 401 281 L 421 281 L 421 309 L 449 309 L 449 329 L 421 329 L 421 357 L 401 357 L 401 329 L 373 329 L 373 309 L 401 309 Z",BoardInk,Outline);
        }
        private void DrawControl(Canvas board,Physical control) {
            var small=control.Mark.Length>3; var width=small ? 42 : control.Mark.Length>1 ? 32 : 25;
            if (!control.Id.StartsWith("axis:") && !control.Id.StartsWith("DPAD_") && !new[]{"4","5","6","7","12","13"}.Contains(control.Id))
                Circle(board,control.Point.X,control.Point.Y,small ? 15 : 12,BoardInk,Outline);
            Label(board,control.Mark,control.Point.X-width/2.0,control.Point.Y-7,width,small ? 8 : 11);
            control.Dot=new Ellipse { Width=3,Height=3,Fill=Muted,IsHitTestVisible=false };
            Place(board,control.Dot,control.Point.X-2,control.Point.Y+12);
            var hit = new Border { Width=Math.Max(34,width),Height=32,Background=Brushes.Transparent,Cursor=System.Windows.Input.Cursors.Hand,
                ToolTip=control.Label,Focusable=false };
            hit.MouseEnter+=(s,e)=>SetHot(control,true); hit.MouseLeave+=(s,e)=>SetHot(control,false);
            hit.MouseLeftButtonUp+=(s,e)=>{
                e.Handled=true; SetHot(control,true);
                if (control.Editor!=null && control.Editor.IsEnabled) { control.Editor.BringIntoView(); control.Editor.IsDropDownOpen=true; }
                else _status.Text=control.Label+"："+control.Fixed;
            };
            Place(board,hit,control.Point.X-hit.Width/2,control.Point.Y-16);
        }
        private static void SetHot(Physical control,bool hot) {
            if(control.Line!=null) { control.Line.Stroke=hot ? Accent : LineInk; control.Line.StrokeThickness=hot ? 1.4 : 0.8; }
            if (control.Editor!=null) control.Editor.BorderBrush=hot ? Accent : Muted;
        }
        private void BuildMenu() {
            _source.Text="ハンコンでCM7を操作  ·  変更は自動保存";
            var values=_settings.ReadMenuValues(); var padValues=_settings.ReadMenuValues(true);
            string confirm; var circle=padValues.TryGetValue("confirm",out confirm) && confirm.Equals("circle",StringComparison.OrdinalIgnoreCase);
            var menu=new[]{
                new[]{"up","上へ","十字 ↑ / 左スティック ↑"},new[]{"down","下へ","十字 ↓ / 左スティック ↓"},
                new[]{"left","左へ","十字 ← / 左スティック ←"},new[]{"right","右へ","十字 → / 左スティック →"},
                new[]{"confirm","決定",circle?"○":"×"},new[]{"back","戻る",circle?"×":"○"},
                new[]{"select","上部メニュー","Share"},new[]{"start","スタート操作","Options"},
                new[]{"l1","前のタブ","L1"},new[]{"r1","次のタブ","R1"},
                new[]{"triangle","補助操作（△）","△"},new[]{"square","補助操作（□）","□"},
                new[]{"l3","左スティック押込の操作","L3"},new[]{"r3","右スティック押込の操作","R3"}
            };
            _content.Children.Add(Text("CM7 MENU",22));
            var header=Text("各メニュー操作に使うESのボタンを選択できます。同じボタンを別の操作へ移すと、元のメニュー割当は「未割当」になります。走行用の割当は別に保存されます。",13);
            header.Foreground=Muted; header.Margin=new Thickness(0,12,0,16); _content.Children.Add(header);
            foreach(var row in new[]{new[]{"","操作","DUALSHOCK 4（表示のみ）"}}.Concat(menu)) {
                var grid=new Grid { Margin=new Thickness(0,0,0,1),Background=Card };
                foreach(var width in new[]{1.0,1.25,1.0}) grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(width,GridUnitType.Star) });
                var title=Text(row[1],14); title.Margin=new Thickness(18,10,18,10); title.VerticalAlignment=VerticalAlignment.Center; grid.Children.Add(title);
                var pad=Text(row[2],14); pad.Margin=new Thickness(18,10,18,10); pad.VerticalAlignment=VerticalAlignment.Center; Grid.SetColumn(pad,2); grid.Children.Add(pad);
                if(row[0]=="") {
                    var text=Text("MOZA ES（ボタンを選択）",14); text.Margin=new Thickness(12,10,12,10); Grid.SetColumn(text,1); grid.Children.Add(text);
                } else {
                    var control=new Physical { Id=row[0],Label=row[1],Editor=new ComboBox { Style=_comboStyle,Margin=new Thickness(12,5,12,5),MinHeight=30 } };
                    System.Windows.Automation.AutomationProperties.SetName(control.Editor,"CM7メニュー・"+control.Label+"のボタン");
                    control.Editor.SelectionChanged+=(s,e)=>SaveMenuSelection(control);
                    control.Editor.DropDownClosed+=(s,e)=>control.Editor.Dispatcher.BeginInvoke(new Action(()=> {
                        if(control.Editor.IsKeyboardFocusWithin) System.Windows.Input.Keyboard.ClearFocus();
                    }),System.Windows.Threading.DispatcherPriority.Background);
                    Grid.SetColumn(control.Editor,1); grid.Children.Add(control.Editor); _physical.Add(control);
                }
                _content.Children.Add(grid);
            }
            RefreshMenuSelections();
            var note=Text("保存後・初回・再接続時は、ボタンを一度離してから操作してください。機器のPOV十字入力は従来どおり移動に使えます。X / Y はボタン3 / 4表記です。",13);
            note.Foreground=Muted; note.Margin=new Thickness(0,16,0,0); _content.Children.Add(note);
            string enabled;
            if(values.TryGetValue("wheel_enabled",out enabled) && new[]{"0","off","false"}.Contains(enabled,StringComparer.Ordinal)) _status.Text="ハンコンのメニュー操作は無効です。保存済みの割当を表示しています。";
            else if(padValues.TryGetValue("enabled",out enabled) && new[]{"0","off","false"}.Contains(enabled,StringComparer.OrdinalIgnoreCase)) _status.Text="メニュー入力は無効です。保存済みの割当を表示しています。";
        }
        private void RefreshMenuSelections() {
            var bindings=_settings.ReadMenuWheelButtons();
            _suppressSelection=true;
            try {
                foreach(var control in _physical) {
                    var button=bindings[control.Id];
                    var choices=new List<Choice> { new Choice { Id="-1",Label="未割当" } };
                    choices.AddRange(WheelNames.OrderBy(x=>x.Key).Select(x=>new Choice { Id=x.Key.ToString(),Label=x.Value }));
                    if(button>=0 && !WheelNames.ContainsKey(button)) choices.Add(new Choice { Id=button.ToString(),Label=WheelName(button)+"（保存済み）" });
                    control.Selected=button.ToString(); control.Editor.ItemsSource=choices;
                    control.Editor.SelectedItem=choices.First(x=>x.Id==control.Selected);
                    control.Editor.ToolTip=control.Label+"に使うボタン。同じボタンの他のメニュー割当は解除します。";
                }
            } finally { _suppressSelection=false; }
        }
        private void SaveMenuSelection(Physical control) {
            if(_suppressSelection)return;
            var choice=control.Editor.SelectedItem as Choice; int button;
            if(choice==null || choice.Id==control.Selected || !int.TryParse(choice.Id,out button))return;
            string error;
            if(_settings.SetMenuWheelButton(control.Id,button,out error)) {
                RefreshMenuSelections();
                _status.Text=control.Label+" → "+choice.Label+" を保存しました。約2秒待ち、ボタンを一度離してから操作してください。";
            } else {
                _suppressSelection=true;
                try { control.Editor.SelectedItem=control.Editor.Items.Cast<Choice>().First(x=>x.Id==control.Selected); }
                finally { _suppressSelection=false; }
                _status.Text=error;
            }
        }
        /// <summary>[fork] GtSettings が登録した時の、このパネルの最初の候補の通し番号（左右移動で GtFocus.SetIndex に使う）。</summary>
        internal int FocusBase;
        internal IEnumerable<GtFocusItem> GetFocusItems() {
            var tabs=new[]{_wheelTab,_padTab,_menuTab,_reset}.Where(x=>x.Visibility==Visibility.Visible && x.IsEnabled).ToList();
            var list=_physical.Where(x=>x.Editor!=null && x.Editor.IsEnabled).ToList();
            var leftSide=list.Where(x=>!x.Right).ToList(); var rightSide=list.Where(x=>x.Right).ToList();
            for(var t=0;t<tabs.Count;t++) {
                var local=tabs[t]; var at=t;
                yield return new GtFocusItem { Name="controller-"+local.Content,SetHot=hot=>local.BorderBrush=hot ? Brushes.White : LineInk,
                    Activate=()=>local.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                    // [fork] 2026-10-04 要望「横移動できるように」。上のタブ（MOZA ES / DUALSHOCK 4 / CM7の操作 / 戻す）は左右で隣へ
                    Custom=command=>{
                        if(command!=GtPadCommand.Left && command!=GtPadCommand.Right) return false;
                        var next=at+(command==GtPadCommand.Right ? 1 : -1);
                        if(next>=0 && next<tabs.Count) { GtFocus.SetIndex(FocusBase+next); GtSfx.PlayMove(); }
                        return true;
                    } };
            }
            var tabCount=tabs.Count;
            // Keep the same editor objects after a save; GT focus never acquires native ComboBox keyboard focus.
            // [fork] 2026-10-04 要望「決定ボタンがおかしい」。以前は A(決定)・左右を押すたびに選択肢が 1 つ進み、そのまま保存されていた
            //   （10 回押す間に、通り過ぎた操作が次々に元のボタンから外れた＝シフトパドル・視点切替・ホーンが消えた）。
            //   今は A で選択肢の一覧を開く → ↑↓で選ぶ（保存しない）→ A で確定 / B で取り消し。左右は何もしない。
            foreach(var physical in list) {
                var local=physical; yield return new GtFocusItem { Name="controller-binding-"+System.Windows.Automation.AutomationProperties.GetName(local.Editor),
                    SetHot=hot=>{ SetHot(local,hot); if(hot)local.Editor.BringIntoView(); },Activate=()=>PadActivate(local),
                    Custom=command=>PadCommand(local,command,list,leftSide,rightSide,tabCount) };
            }
        }
        /// <summary>[fork] 一覧が閉じている時の左右 = 左列 ↔ 右列の「同じ段」へ移る。端（もう反対側が無い向き）では何もしない。</summary>
        private bool PadSideways(Physical p,GtPadCommand command,List<Physical> list,List<Physical> leftSide,List<Physical> rightSide,int tabCount) {
            if(command!=GtPadCommand.Left && command!=GtPadCommand.Right) return false;
            var toRight=command==GtPadCommand.Right;
            if(toRight==p.Right) return true;   // もう端
            var from=p.Right ? rightSide : leftSide; var to=toRight ? rightSide : leftSide;
            if(to.Count==0) return true;
            var target=to[Math.Min(Math.Max(from.IndexOf(p),0),to.Count-1)];
            GtFocus.SetIndex(FocusBase+tabCount+list.IndexOf(target));
            GtSfx.PlayMove();
            return true;
        }
        // ---- [fork] パッド操作: 一覧を開いて選ぶ（選ぶだけでは保存しない）-----------------------------------
        private Physical _browsing;
        private object _browseOriginal;
        /// <summary>一覧を開いている最中なら、変更せずに閉じる（B / Esc 用）。閉じたら true。</summary>
        internal bool CancelBrowse() {
            var p=_browsing; if(p==null)return false;
            _browsing=null;
            _suppressSelection=true;
            try { if(p.Editor!=null) { p.Editor.SelectedItem=_browseOriginal; p.Editor.IsDropDownOpen=false; } }
            finally { _suppressSelection=false; }
            _status.Text="取り消しました（変更していません）。";
            return true;
        }
        private void PadActivate(Physical p) {
            if(p.Editor==null || !p.Editor.IsEnabled)return;
            if(_browsing==p) { PadCommit(p); return; }
            CancelBrowse();
            _browsing=p; _browseOriginal=p.Editor.SelectedItem;
            p.Editor.IsDropDownOpen=true;
            _status.Text=p.Label+"：↑ ↓ で選ぶ　A（決定）で確定　B（戻る）で取り消し";
        }
        private void PadCommit(Physical p) {
            _browsing=null;
            p.Editor.IsDropDownOpen=false;
            SaveSelection(p);
        }
        private bool PadCommand(Physical p,GtPadCommand command,List<Physical> list,List<Physical> leftSide,List<Physical> rightSide,int tabCount) {
            if(_browsing!=p) return PadSideways(p,command,list,leftSide,rightSide,tabCount);
            if(command==GtPadCommand.Up || command==GtPadCommand.Down) { BrowseStep(p.Editor,command==GtPadCommand.Down?1:-1); return true; }
            return command==GtPadCommand.Left || command==GtPadCommand.Right;   // 開いている間、左右は何もしない
        }
        private void BrowseStep(ComboBox combo,int delta) {
            if(combo.Items.Count==0)return;
            var index=combo.SelectedIndex+delta;
            if(index>=0 && index<combo.Items.Count && ((Choice)combo.Items[index]).Id=="saved") index+=delta;
            if(index<0 || index>=combo.Items.Count)return;
            _suppressSelection=true;
            try { combo.SelectedIndex=index; } finally { _suppressSelection=false; }
        }
    }
}
