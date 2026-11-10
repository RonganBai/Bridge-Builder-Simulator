using System.Linq;
using System.Text;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using Godot;

namespace BridgeArchitect3D.UI
{
    /// <summary>HUD 调用的关卡控制接口（由 LevelManager 实现），降低 UI 与具体系统的耦合。</summary>
    public interface ILevelController
    {
        void Undo(); void Redo(); void ResetLevel(); void SaveDesign(); void LoadDesign(); void LoadReference();
        void BackToMenu(); void OpenLevelSelect();
        void SelectTool(BuildTool tool, string materialId); void SetStressView(bool on); void DeleteSelected(); void ResizeSelected(double delta); void RepairSelected(); void SetFoundationSelected(FoundationType type);
        void StartTest(); void TogglePause(); void ResetTest(); void BackToBuild(); void SetSpeed(int x);
        void NextLevel(); void Replay(); void ShowSnapshot(); void LocateProblem(); void SetView(bool side);
        void ApplySettings(double deformationScale, bool showGrid, double sensitivity, bool fullscreen);
    }

    /// <summary>
    /// 游戏界面：顶部状态栏、左侧工具栏、右侧属性面板、底部测试栏、提示、失败分析与过关结算弹窗、设置。
    /// 全部由代码构建，数据由 LevelManager 实时推送。
    /// </summary>
    public partial class GameHUD : Control
    {
        private ILevelController _ctl;
        private Label _levelName, _budget, _used, _remaining, _modeBanner, _toast, _preview, _vehicleLabel, _statusLabel, _progressLabel;
        private Button _undo, _redo, _start, _pause, _resetTest, _back, _deleteSel;
        private Button[] _toolButtons, _speedButtons;
        private CheckButton _stress;
        private PanelContainer _leftBar, _rightPanel, _bottomBar, _topBar;
        private RichTextLabel _props;
        private ProgressBar _progress;
        private Control _failOverlay, _successOverlay, _settingsOverlay;
        private VBoxContainer _failBox, _successBox, _settingsBox;
        private double _toastTimer;
        private StyleBoxFlat _bottomStyle;

        public void Init(ILevelController ctl, string levelName, string goal, System.Collections.Generic.IReadOnlyList<MaterialDefinition> materials)
        {
            _ctl = ctl;
            _materials = materials;
            Theme = UiTheme.Get();
            MouseFilter = MouseFilterEnum.Ignore;
            // 根节点必须铺满整个视口，否则底部测试栏会被放到屏幕之外
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            FitViewport();
            GetViewport().SizeChanged += FitViewport;
            BuildTopBar(levelName, goal);
            BuildLeftBar();
            BuildRightPanel();
            BuildBottomBar();
            BuildOverlays();
            SetMode(false);
        }

        private void FitViewport()
        {
            Position = Vector2.Zero;
            Size = GetViewportRect().Size;
        }

        // ---------------- 布局 ----------------
        private static void Place(Control c, float anchorL, float anchorT, float anchorR, float anchorB, float offL, float offT, float offR, float offB)
        {
            c.AnchorLeft = anchorL; c.AnchorTop = anchorT; c.AnchorRight = anchorR; c.AnchorBottom = anchorB;
            c.OffsetLeft = offL; c.OffsetTop = offT; c.OffsetRight = offR; c.OffsetBottom = offB;
        }

        private Label _goal;
        private Button _sideBtn, _3dBtn;
        private MenuButton _menu;

        public void SetViewButtons(bool side) { _sideBtn.SetPressedNoSignal(side); _3dBtn.SetPressedNoSignal(!side); }

        private void BuildTopBar(string levelName, string goal)
        {
            _topBar = new PanelContainer();
            _topBar.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bg, 0, null, 8));
            AddChild(_topBar);
            Place(_topBar, 0, 0, 1, 0, 0, 0, 0, 52);
            var hb = new HBoxContainer(); hb.AddThemeConstantOverride("separation", 14);
            _topBar.AddChild(hb);
            // 顶栏只保留：关卡与目标、预算、撤销 / 重做、视角切换；其余操作收进“菜单”
            var titleBox = new VBoxContainer();
            titleBox.AddThemeConstantOverride("separation", 0);
            _levelName = UiTheme.MakeLabel(levelName, 17, Colors.White);
            _goal = UiTheme.MakeLabel(goal, 13, UiTheme.Muted);
            titleBox.AddChild(_levelName); titleBox.AddChild(_goal);
            hb.AddChild(titleBox);
            hb.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            _budget = UiTheme.MakeLabel("", 16, UiTheme.Text);
            hb.AddChild(_budget);
            hb.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
            _undo = UiTheme.MakeButton("↶ 撤销", () => _ctl.Undo(), 70);
            _redo = UiTheme.MakeButton("↷ 重做", () => _ctl.Redo(), 70);
            hb.AddChild(_undo); hb.AddChild(_redo);
            hb.AddChild(new Control { CustomMinimumSize = new Vector2(10, 0) });
            var vg = new ButtonGroup();
            _sideBtn = new Button { Text = "侧视", ToggleMode = true, ButtonGroup = vg, ButtonPressed = true, CustomMinimumSize = new Vector2(56, 34), TooltipText = "正交侧视（F1），适合建造" };
            _3dBtn = new Button { Text = "3D", ToggleMode = true, ButtonGroup = vg, CustomMinimumSize = new Vector2(48, 34), TooltipText = "自由 3D 视角（F3），右键旋转" };
            _sideBtn.Pressed += () => _ctl.SetView(true);
            _3dBtn.Pressed += () => _ctl.SetView(false);
            hb.AddChild(_sideBtn); hb.AddChild(_3dBtn);
            _menu = new MenuButton { Text = "☰ 菜单", Flat = false, CustomMinimumSize = new Vector2(80, 34) };
            var pm = _menu.GetPopup();
            pm.AddItem("参考方案提示", 0); pm.AddItem("重置关卡", 1); pm.AddSeparator();
            pm.AddItem("保存方案", 2); pm.AddItem("加载方案", 3); pm.AddSeparator();
            pm.AddItem("设置", 4); pm.AddItem("返回主菜单", 5);
            pm.IdPressed += id =>
            {
                switch (id)
                {
                    case 0: _ctl.LoadReference(); break;
                    case 1: _ctl.ResetLevel(); break;
                    case 2: _ctl.SaveDesign(); break;
                    case 3: _ctl.LoadDesign(); break;
                    case 4: _settingsOverlay.Visible = true; break;
                    case 5: _ctl.BackToMenu(); break;
                }
            };
            hb.AddChild(_menu);

            _modeBanner = UiTheme.MakeLabel("", 16, Colors.White);
            _modeBanner.HorizontalAlignment = HorizontalAlignment.Center;
            AddChild(_modeBanner);
            Place(_modeBanner, 0.5f, 0, 0.5f, 0, -200, 58, 200, 84);

            _toast = UiTheme.MakeLabel("", 16);
            _toast.HorizontalAlignment = HorizontalAlignment.Center;
            AddChild(_toast);
            Place(_toast, 0.5f, 0, 0.5f, 0, -400, 88, 400, 116);

            _preview = UiTheme.MakeLabel("", 14);
            _preview.AddThemeStyleboxOverride("normal", UiTheme.Box(new Color(0, 0, 0, 0.7f), 4, null, 4));
            _preview.Visible = false;
            AddChild(_preview);
        }

        private void BuildLeftBar()
        {
            _leftBar = new PanelContainer();
            AddChild(_leftBar);
            Place(_leftBar, 0, 0, 0, 1, 8, 62, 158, -96);
            var vb = new VBoxContainer(); vb.AddThemeConstantOverride("separation", 6);
            _leftBar.AddChild(vb);
            // 工具栏分组：桥面 / 支撑（只显示本关可用材料，数字键按显示顺序）/ 工具（选择、删除）
            var group = new ButtonGroup();
            var decks = _materials.Where(m => m.IsDeck).ToList();
            var supports = _materials.Where(m => !m.IsDeck).ToList();
            var ordered = decks.Concat(supports).ToList();
            _materials = ordered;
            int count = ordered.Count + 2;
            _toolButtons = new Button[count];
            _toolMaterial = new string[count];
            string[] keys = { "2", "3", "4", "5", "6", "7", "8", "9", "0" };
            Button Make(int i, string text, string tip, BuildTool tool, string matId)
            {
                _toolMaterial[i] = matId;
                var b = new Button { Text = text, ToggleMode = true, ButtonGroup = group, CustomMinimumSize = new Vector2(0, 30), Alignment = HorizontalAlignment.Left, TooltipText = tip ?? "" };
                b.AddThemeFontSizeOverride("font_size", 14);
                b.Pressed += () => _ctl.SelectTool(tool, matId);
                _toolButtons[i] = b;
                return b;
            }
            for (int j = 0; j < ordered.Count; j++)
            {
                if (j == 0 && decks.Count > 0) vb.AddChild(UiTheme.MakeLabel("桥面", 13, UiTheme.Muted));
                if (j == decks.Count && supports.Count > 0) vb.AddChild(UiTheme.MakeLabel("支撑", 13, UiTheme.Muted));
                var m = ordered[j];
                string tip = $"{m.Name}  ${m.CostPerMeter:0}/m，最长 {m.MaximumLength:0.#} m\n{m.Description}";
                vb.AddChild(Make(j + 1, $"{(j < keys.Length ? keys[j] : " ")} {m.Name}", tip, BuildTool.Build, m.MaterialId));
            }
            vb.AddChild(UiTheme.MakeLabel("工具", 13, UiTheme.Muted));
            vb.AddChild(Make(0, "1 选择 / 拖动", "选择构件查看属性；拖动节点；空白处拖动可框选", BuildTool.Select, null));
            vb.AddChild(Make(count - 1, "X 删除", "直接点击构件或节点删除；空白处拖动可框选", BuildTool.Delete, null));
            vb.AddChild(new HSeparator());
            _stress = new CheckButton { Text = "受力显示" };
            _stress.Toggled += on => _ctl.SetStressView(on);
            vb.AddChild(_stress);
            _deleteSel = UiTheme.MakeButton("删除所选", () => _ctl.DeleteSelected());
            vb.AddChild(_deleteSel);
            vb.AddChild(new HSeparator());
            var help = UiTheme.MakeLabel("从已有节点开始建造\n长按节点可拖动\nEsc 取消  空格 测试", 12, UiTheme.Muted);
            help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            vb.AddChild(help);
        }

        private void BuildRightPanel()
        {
            _rightPanel = new PanelContainer();
            AddChild(_rightPanel);
            Place(_rightPanel, 1, 0, 1, 0, -298, 62, -8, 420);
            var vb = new VBoxContainer();
            _rightPanel.AddChild(vb);
            _props = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(270, 0) };
            vb.AddChild(_props);
            // 液压杆伸缩按钮（选中可伸缩构件时显示）
            _resizeRow = new HBoxContainer();
            _resizeRow.AddChild(UiTheme.MakeButton("缩短 0.5 m", () => _ctl.ResizeSelected(-0.5), 120));
            _resizeRow.AddChild(UiTheme.MakeButton("伸长 0.5 m", () => _ctl.ResizeSelected(0.5), 120));
            _resizeRow.Visible = false;
            vb.AddChild(_resizeRow);
            // 旧构件修复按钮（加固关）
            _repairBtn = UiTheme.MakeButton("修复旧构件", () => _ctl.RepairSelected(), 240);
            _repairBtn.Visible = false;
            vb.AddChild(_repairBtn);
            // 桥墩基础切换（选中墩底节点时显示）
            _foundRow = new HBoxContainer();
            _shallowBtn = UiTheme.MakeButton($"浅基础 ${FoundationRules.ShallowCost:0}", () => _ctl.SetFoundationSelected(FoundationType.Shallow), 120);
            _pileBtn = UiTheme.MakeButton($"桩基础 ${FoundationRules.PileCost:0}", () => _ctl.SetFoundationSelected(FoundationType.Pile), 120);
            _foundRow.AddChild(_shallowBtn); _foundRow.AddChild(_pileBtn);
            _foundRow.Visible = false;
            vb.AddChild(_foundRow);
            _rightPanel.Visible = false;
        }

        private HBoxContainer _resizeRow;
        private string[] _toolMaterial;
        private System.Collections.Generic.IReadOnlyList<MaterialDefinition> _materials = System.Array.Empty<MaterialDefinition>();

        public void SetResizeVisible(bool v) => _resizeRow.Visible = v;

        private Button _repairBtn, _shallowBtn, _pileBtn;
        private HBoxContainer _foundRow;

        public void SetFoundationRow(bool visible, FoundationType current)
        {
            _foundRow.Visible = visible;
            if (!visible) return;
            _shallowBtn.Disabled = current == FoundationType.Shallow;
            _pileBtn.Disabled = current == FoundationType.Pile;
        }
        public void SetRepair(bool visible, double cost)
        {
            _repairBtn.Visible = visible;
            if (visible) _repairBtn.Text = $"🔧 修复旧构件（${cost:0}，恢复全部承载力）";
        }

        private string _demolitionInfo;
        public void SetDemolitionInfo(string text) { _demolitionInfo = text; }

        private readonly System.Collections.Generic.Dictionary<string, string> _toolBaseText = new();

        /// <summary>限量材料：在工具按钮上显示剩余根数。</summary>
        public void SetToolBadge(string materialId, int remaining)
        {
            for (int i = 0; i < _toolButtons.Length; i++)
            {
                if (_toolMaterial[i] != materialId) continue;
                if (!_toolBaseText.ContainsKey(materialId)) _toolBaseText[materialId] = _toolButtons[i].Text;
                _toolButtons[i].Text = remaining >= 0 ? $"{_toolBaseText[materialId]}（剩 {remaining}）" : _toolBaseText[materialId];
                _toolButtons[i].Disabled = remaining == 0;
            }
        }

        private void BuildBottomBar()
        {
            _bottomBar = new PanelContainer();
            _bottomStyle = UiTheme.Box(UiTheme.Bg, 0, null, 10);
            _bottomBar.AddThemeStyleboxOverride("panel", _bottomStyle);
            AddChild(_bottomBar);
            Place(_bottomBar, 0, 1, 1, 1, 0, -86, 0, 0);
            var stack = new Control();
            _bottomBar.AddChild(stack);

            // 建造模式：只突出“开始测试”，旁边显示自重下的结构状态
            _buildRow = new HBoxContainer(); _buildRow.AddThemeConstantOverride("separation", 16);
            _buildRow.SetAnchorsPreset(LayoutPreset.FullRect);
            stack.AddChild(_buildRow);
            _start = UiTheme.MakeButton("▶  开始测试", () => _ctl.StartTest(), 200);
            _start.CustomMinimumSize = new Vector2(200, 52);
            _start.AddThemeFontSizeOverride("font_size", 20);
            _start.AddThemeStyleboxOverride("normal", UiTheme.Box(new Color(0.18f, 0.55f, 0.32f), 8));
            _start.AddThemeStyleboxOverride("hover", UiTheme.Box(new Color(0.22f, 0.65f, 0.38f), 8));
            _buildRow.AddChild(_start);
            _buildStatus = UiTheme.MakeLabel("", 15);
            _buildStatus.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            _buildRow.AddChild(_buildStatus);

            // 测试模式：暂停 / 倍速 / 进度 / 返回建造
            var hb = new HBoxContainer(); hb.AddThemeConstantOverride("separation", 10);
            hb.SetAnchorsPreset(LayoutPreset.FullRect);
            _testRow = hb;
            stack.AddChild(hb);
            _pause = UiTheme.MakeButton("⏸ 暂停", () => _ctl.TogglePause(), 90);
            _resetTest = UiTheme.MakeButton("↻ 重新测试", () => _ctl.ResetTest(), 100);
            _back = UiTheme.MakeButton("← 返回建造", () => _ctl.BackToBuild(), 110);
            _back.AddThemeStyleboxOverride("normal", UiTheme.Box(Accent2, 6));
            hb.AddChild(_back); hb.AddChild(_pause); hb.AddChild(_resetTest);
            hb.AddChild(UiTheme.MakeLabel("速度", 14, UiTheme.Muted));
            var g = new ButtonGroup();
            int[] speeds = { 1, 2, 4 };
            _speedButtons = new Button[3];
            for (int i = 0; i < 3; i++)
            {
                int s = speeds[i];
                var b = new Button { Text = $"{s}×", ToggleMode = true, ButtonGroup = g, CustomMinimumSize = new Vector2(44, 34), ButtonPressed = i == 0 };
                b.Pressed += () => _ctl.SetSpeed(s);
                _speedButtons[i] = b; hb.AddChild(b);
            }
            _stressTest = new CheckButton { Text = "受力显示", ButtonPressed = true };
            _stressTest.Toggled += on => _ctl.SetStressView(on);
            hb.AddChild(_stressTest);
            var vb = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _progressLabel = UiTheme.MakeLabel("测试进度", 13, UiTheme.Muted);
            _progress = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(160, 14) };
            vb.AddChild(_progressLabel); vb.AddChild(_progress);
            hb.AddChild(vb);
            var vb2 = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
            _vehicleLabel = UiTheme.MakeLabel("当前车辆：—", 14);
            _statusLabel = UiTheme.MakeLabel("结构状态：—", 14);
            _statusLabel.ClipText = true;
            vb2.AddChild(_vehicleLabel); vb2.AddChild(_statusLabel);
            hb.AddChild(vb2);
        }

        private HBoxContainer _buildRow, _testRow;
        private Label _buildStatus;
        private static readonly Color Accent2 = new(0.2f, 0.36f, 0.6f);

        private void BuildOverlays()
        {
            (_failOverlay, _failBox) = UiTheme.MakeDialog(this, "测试失败", 600);
            (_successOverlay, _successBox) = UiTheme.MakeDialog(this, "关卡完成！", 520);
            (_settingsOverlay, _settingsBox) = UiTheme.MakeDialog(this, "设置", 460);
            BuildSettings();
        }

        private HSlider _defSlider, _sensSlider;
        private CheckBox _gridCheck, _fullCheck;
        private Label _defValue;

        private void BuildSettings()
        {
            var gm = Core.GameManager.Instance;
            var s = gm?.Settings ?? new Core.GameSettings();
            _settingsBox.AddChild(UiTheme.MakeLabel("变形显示放大系数（仅影响画面，不影响判定）", 14, UiTheme.Muted));
            var row = new HBoxContainer();
            _defSlider = new HSlider { MinValue = 1, MaxValue = 100, Step = 1, Value = s.DeformationScale, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _defValue = UiTheme.MakeLabel($"{s.DeformationScale:0}×");
            _defSlider.ValueChanged += v => _defValue.Text = $"{v:0}×";
            row.AddChild(_defSlider); row.AddChild(_defValue);
            _settingsBox.AddChild(row);
            _settingsBox.AddChild(UiTheme.MakeLabel("相机灵敏度", 14, UiTheme.Muted));
            _sensSlider = new HSlider { MinValue = 0.3, MaxValue = 3, Step = 0.1, Value = s.CameraSensitivity };
            _settingsBox.AddChild(_sensSlider);
            _gridCheck = new CheckBox { Text = "显示建造网格", ButtonPressed = s.ShowGrid };
            _fullCheck = new CheckBox { Text = "全屏", ButtonPressed = s.Fullscreen };
            _settingsBox.AddChild(_gridCheck); _settingsBox.AddChild(_fullCheck);
            var hb = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            hb.AddChild(UiTheme.MakeButton("确定", () =>
            {
                _ctl.ApplySettings(_defSlider.Value, _gridCheck.ButtonPressed, _sensSlider.Value, _fullCheck.ButtonPressed);
                _settingsOverlay.Visible = false;
            }, 90));
            _settingsBox.AddChild(hb);
        }

        public override void _Process(double delta)
        {
            if (_toastTimer > 0)
            {
                _toastTimer -= delta;
                _toast.Modulate = new Color(1, 1, 1, (float)Mathf.Clamp(_toastTimer, 0, 1));
            }
        }

        // ---------------- 数据更新 ----------------
        public void UpdateBudget(double used, double budget, bool canUndo, bool canRedo)
        {
            double rem = budget - used;
            // 与结算同一套规则：超出预算仍可建造和通关，但会降低预算分
            _budget.Text = rem >= 0
                ? $"预算目标 ${budget:N0} · 已用 ${used:N0} · 剩余 ${rem:N0}"
                : $"预算目标 ${budget:N0} · 已超 ${-rem:N0}（扣预算分）";
            if (!string.IsNullOrEmpty(_demolitionInfo)) _budget.Text += " · " + _demolitionInfo;
            _budget.AddThemeColorOverride("font_color", rem < 0 ? UiTheme.Bad : rem < budget * 0.1 ? UiTheme.Warn : UiTheme.Text);
            _undo.Disabled = !canUndo; _redo.Disabled = !canRedo;
        }

        public void SetTool(BuildTool t, string materialId)
        {
            for (int i = 0; i < _toolButtons.Length; i++)
            {
                bool on = t switch
                {
                    BuildTool.Select => i == 0,
                    BuildTool.Delete => i == _toolButtons.Length - 1,
                    _ => _toolMaterial[i] != null && _toolMaterial[i] == materialId,
                };
                _toolButtons[i].SetPressedNoSignal(on);
            }
        }

        public void SetStressChecked(bool on) { _stress.SetPressedNoSignal(on); _stressTest?.SetPressedNoSignal(on); }
        private CheckButton _stressTest;

        /// <summary>切换建造 / 测试模式的界面外观（测试模式橙色强调、锁定工具栏）。</summary>
        public void SetMode(bool test)
        {
            _modeBanner.Text = test ? "● 测试模式" : "● 建造模式";
            _modeBanner.AddThemeColorOverride("font_color", test ? UiTheme.TestAccent : UiTheme.Accent);
            foreach (var b in _toolButtons) b.Disabled = test;
            _deleteSel.Disabled = test;
            if (test) { _resizeRow.Visible = false; _repairBtn.Visible = false; _foundRow.Visible = false; }
            _undo.Disabled |= test; _redo.Disabled |= test;
            _undo.Visible = !test; _redo.Visible = !test;
            _leftBar.Visible = !test;                       // 测试时隐藏建造工具
            _buildRow.Visible = !test; _testRow.Visible = test;
            _bottomStyle.BorderWidthTop = 3;
            _bottomStyle.BorderColor = test ? UiTheme.TestAccent : UiTheme.Accent;
            // 回到建造时清空上次测试的进度与时间，避免误以为测试仍在进行
            _pause.Text = "⏸ 暂停"; _progress.Value = 0;
            _progressLabel.Text = "测试进度 0%"; _vehicleLabel.Text = "当前车辆：—"; _statusLabel.Text = "结构状态：—";
            _failOverlay.Visible = false; _successOverlay.Visible = false;
            _preview.Visible = false;
        }

        public void UpdateTest(double progress, string vehicle, string status, Color statusColor, bool paused, double time)
        {
            _progress.Value = progress * 100;
            _progressLabel.Text = $"测试进度 {progress * 100:0}%   时间 {time:0.0}s";
            _vehicleLabel.Text = "当前车辆：" + vehicle;
            _statusLabel.Text = "结构状态：" + status;
            _statusLabel.AddThemeColorOverride("font_color", statusColor);
            _pause.Text = paused ? "▶ 继续" : "⏸ 暂停";
        }

        public void SetStructureStatus(string status, Color c)
        {
            _buildStatus.Text = status;
            _buildStatus.AddThemeColorOverride("font_color", c);
            _statusLabel.Text = "结构状态：" + status;
            _statusLabel.AddThemeColorOverride("font_color", c);
        }

        public void ShowProperties(string bbcode)
        {
            _rightPanel.Visible = !string.IsNullOrEmpty(bbcode);
            _props.Text = bbcode ?? "";
        }

        public void ShowToast(string text, bool error, double seconds = 0)
        {
            _toast.Text = text;
            _toast.AddThemeColorOverride("font_color", error ? UiTheme.Bad : UiTheme.Text);
            _toastTimer = seconds > 0 ? seconds : error ? 3.5 : 2.0;
            _toast.Modulate = Colors.White;
        }

        private Panel _boxRect;

        /// <summary>显示 / 隐藏框选矩形。</summary>
        public void ShowBoxRect(Rect2? r)
        {
            if (_boxRect == null)
            {
                _boxRect = new Panel { MouseFilter = MouseFilterEnum.Ignore };
                var sb = new StyleBoxFlat { BgColor = new Color(0.3f, 0.65f, 1f, 0.15f), BorderColor = UiTheme.Accent };
                sb.SetBorderWidthAll(1);
                _boxRect.AddThemeStyleboxOverride("panel", sb);
                AddChild(_boxRect);
            }
            _boxRect.Visible = r.HasValue;
            if (r.HasValue) { _boxRect.Position = r.Value.Position; _boxRect.Size = r.Value.Size; }
        }

        public void ShowPreview(string text, Vector2 pos, bool ok)
        {
            _preview.Visible = !string.IsNullOrEmpty(text);
            if (!_preview.Visible) return;
            _preview.Text = text;
            _preview.AddThemeColorOverride("font_color", ok ? UiTheme.Good : UiTheme.Bad);
            _preview.Position = pos + new Vector2(18, 18);
            _preview.ResetSize();
        }

        // ---------------- 结算弹窗 ----------------
        private void ClearBox(VBoxContainer box)
        {
            foreach (var c in box.GetChildren().Skip(1)) c.QueueFree();   // 保留标题
        }

        public void ShowFailure(SimulationResult r, bool canReplay)
        {
            ClearBox(_failBox);
            // 先说发生了什么、该怎么改；编号、受力与回放放进“详情”
            _failBox.AddChild(UiTheme.MakeLabel(string.IsNullOrEmpty(r.Headline) ? FailureDetector.ReasonText(r.FailureReason) : r.Headline, 22, UiTheme.Bad));
            if (!string.IsNullOrEmpty(r.TopSuggestion))
            {
                var tip = UiTheme.MakeLabel("建议：" + r.TopSuggestion, 17, UiTheme.Text);
                tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                tip.CustomMinimumSize = new Vector2(560, 0);
                _failBox.AddChild(tip);
            }
            var hb = new HBoxContainer(); hb.AddThemeConstantOverride("separation", 10);
            var locate = UiTheme.MakeButton("定位问题并返回建造", () => _ctl.LocateProblem(), 200);
            locate.CustomMinimumSize = new Vector2(220, 44);
            locate.AddThemeStyleboxOverride("normal", UiTheme.Box(new Color(0.18f, 0.55f, 0.32f), 8));
            locate.AddThemeStyleboxOverride("hover", UiTheme.Box(new Color(0.22f, 0.65f, 0.38f), 8));
            hb.AddChild(locate);
            hb.AddChild(UiTheme.MakeButton("重新测试", () => _ctl.ResetTest(), 100));
            _failBox.AddChild(hb);

            var details = new VBoxContainer { Visible = false };
            var toggle = UiTheme.MakeButton("▸ 详情（构件编号、受力与回放）", null);
            toggle.Flat = true; toggle.Alignment = HorizontalAlignment.Left;
            toggle.Pressed += () => { details.Visible = !details.Visible; toggle.Text = (details.Visible ? "▾" : "▸") + " 详情（构件编号、受力与回放）"; };
            _failBox.AddChild(toggle);
            var sb = new StringBuilder();
            sb.Append($"[color=#ff6a5a]{FailureDetector.ReasonText(r.FailureReason)}[/color]\n{r.FailureMessage}\n\n");
            sb.Append($"[color=#9aa3ad]发生时间：[/color]{r.FailureTime:0.00} s\n");
            sb.Append($"[color=#9aa3ad]涉及构件：[/color]{(r.InvolvedMemberIds.Count > 0 ? string.Join(", ", r.InvolvedMemberIds.Distinct().Select(i => "#" + i)) : "—")}\n");
            sb.Append($"[color=#9aa3ad]通过车辆：[/color]{r.PassedVehicles}/{r.VehicleResults.Count}\n\n");
            sb.Append("[b]其他改进方向：[/b]\n");
            foreach (var s in r.Suggestions.Distinct()) sb.Append($" • {s}\n");
            details.AddChild(new RichTextLabel { BbcodeEnabled = true, FitContent = true, Text = sb.ToString(), CustomMinimumSize = new Vector2(560, 0) });
            var hb2 = new HBoxContainer(); hb2.AddThemeConstantOverride("separation", 10);
            var snap = UiTheme.MakeButton("首次失效快照", () => { _failOverlay.Visible = false; _ctl.ShowSnapshot(); }, 120);
            var rep = UiTheme.MakeButton("回放最后几秒", () => { _failOverlay.Visible = false; _ctl.Replay(); }, 120);
            snap.Disabled = !canReplay; rep.Disabled = !canReplay;
            hb2.AddChild(snap); hb2.AddChild(rep);
            hb2.AddChild(UiTheme.MakeButton("仅返回建造", () => _ctl.BackToBuild(), 100));
            details.AddChild(hb2);
            _failBox.AddChild(details);
            _failOverlay.Visible = true;
        }

        public void ShowSuccess(SimulationResult r, bool hasNext, int previousBest)
        {
            ClearBox(_successBox);
            _successBox.AddChild(UiTheme.MakeLabel($"得分 {r.Score:N0}", 40, UiTheme.Warn));
            if (r.Score > previousBest) _successBox.AddChild(UiTheme.MakeLabel(previousBest > 0 ? $"新纪录！（原最高 {previousBest:N0}）" : "首次通关！", 16, UiTheme.Good));
            else _successBox.AddChild(UiTheme.MakeLabel($"本关最高分 {previousBest:N0}", 14, UiTheme.Muted));
            // 三项目标分开列出，玩家能看懂下一次要改什么
            bool inBudget = r.TotalCost <= r.Budget + 1e-6, intact = r.DeckIntegrity >= 0.999;
            var checks = new RichTextLabel { BbcodeEnabled = true, FitContent = true, CustomMinimumSize = new Vector2(480, 0) };
            checks.Text = "[color=#5ad870]✔ 通关[/color]　　"
                + (inBudget ? "[color=#5ad870]✔ 预算内[/color]" : $"[color=#ff6a5a]✘ 超预算 ${r.TotalCost - r.Budget:N0}[/color]") + "　　"
                + (intact ? "[color=#5ad870]✔ 桥面完整[/color]" : $"[color=#ffd040]△ 桥面完整度 {r.DeckIntegrity * 100:0}%[/color]");
            _successBox.AddChild(checks);
            var sb = new StringBuilder();
            double ratio = r.Budget > 0 ? r.TotalCost / r.Budget : 0;
            sb.Append($"预算分（×7）：{r.BudgetScore:N0}　使用 ${r.TotalCost:N0} / ${r.Budget:N0} = {ratio * 100:0}%，越省越高\n");
            sb.Append($"完整度分（×2）：{r.IntegrityScore:N0}　通过后桥面完整 {r.DeckIntegrity * 100:0}%\n");
            sb.Append(r.StressUsesMargin
                ? $"受力分（×1）：{r.StressScore:N0}　取安全冗余 {r.SafetyMargin * 100:0}%（利用率 {r.MaxUtilization * 100:0}%，冗余上限 150%）\n\n"
                : $"受力分（×1）：{r.StressScore:N0}　取利用率 {r.MaxUtilization * 100:0}%（安全冗余 {r.SafetyMargin * 100:0}%）\n\n");
            sb.Append($"成功通过车辆：{r.PassedVehicles}/{r.VehicleResults.Count}");
            if (r.TotalShips > 0) sb.Append($"　船只：{r.ShipsPassed}/{r.TotalShips}");
            sb.Append($"\n完成时间：{r.CompletionTime:0.0} s\n");
            _successBox.AddChild(UiTheme.MakeLabel(sb.ToString(), 16));
            var hb = new HBoxContainer(); hb.AddThemeConstantOverride("separation", 10);
            var next = UiTheme.MakeButton("下一关", () => _ctl.NextLevel(), 100);
            next.Disabled = !hasNext;
            hb.AddChild(next);
            hb.AddChild(UiTheme.MakeButton("返回关卡选择", () => _ctl.OpenLevelSelect(), 120));
            hb.AddChild(UiTheme.MakeButton("继续修改", () => _ctl.BackToBuild(), 100));
            _successBox.AddChild(hb);
            _successOverlay.Visible = true;
        }

        public void HideDialogs() { _failOverlay.Visible = false; _successOverlay.Visible = false; }

        // ---------------- 属性文本 ----------------
        public static string NodeProperties(BridgeNode n, LevelDefinition level, BridgeDesign d, BridgeSimulation stat)
        {
            var sb = new StringBuilder();
            sb.Append(n.IsAnchor ? $"[b]固定锚点 {n.AnchorId}[/b]\n" : n.PierBase ? $"[b]桥墩基础 #{n.Id}[/b]\n" : $"[b]节点 #{n.Id}[/b]{(n.PierPart ? "（桥墩支点）" : "")}\n");
            if (n.PierBase)
            {
                var zone = level.Terrain.ZoneAt(n.Position.X);
                sb.Append($"地基：{zone.Name}\n基础：{FoundationRules.Name(n.Foundation)}（${FoundationRules.Cost(n.Foundation):0}）\n");
                sb.Append($"地基承载力：{FoundationRules.Capacity(zone, n.Foundation) / 1000:0} kN\n");
                if (zone.Scour) sb.Append(n.Foundation == FoundationType.Pile ? "[color=#5ad870]冲刷区：桩基础不受冲刷影响[/color]\n" : "[color=#ff8a50]冲刷区：洪水时浅基础会被掏空[/color]\n");
                if (!zone.IsRock && n.Foundation != FoundationType.Pile) sb.Append("[color=#ffd040]软土上的浅基础承载很低，重车可能压垮[/color]\n");
                sb.Append("[color=#9aa3ad]删除墩底节点会拆掉整座桥墩[/color]\n");
            }
            sb.Append($"位置：{n.Position}\n");
            int deg = d.Members.Count(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id);
            sb.Append($"连接构件：{deg}\n");
            if (n.IsAnchor)
            {
                var a = level.Anchors.FirstOrDefault(x => x.Id == n.AnchorId);
                if (a != null)
                {
                    sb.Append($"支撑类型：{(a.SupportType == SupportType.Pinned ? "铰支（固定 X/Y）" : "滚动（固定 Y）")}\n");
                    sb.Append($"最大承载：{a.MaxLoad / 1000:0} kN\n");
                    if (stat?.Model != null && stat.Output.Stable && stat.Model.NodeIndex.TryGetValue(n.Id, out int i))
                    {
                        double r = System.Math.Sqrt(stat.Output.Reaction[2 * i] * stat.Output.Reaction[2 * i] + stat.Output.Reaction[2 * i + 1] * stat.Output.Reaction[2 * i + 1]);
                        sb.Append($"自重支反力：{r / 1000:0.0} kN\n");
                    }
                    sb.Append("[color=#9aa3ad]锚点不可移动、不可删除[/color]\n");
                }
            }
            else if (stat?.Model != null && stat.Output.Stable && stat.Model.NodeIndex.TryGetValue(n.Id, out int i))
                sb.Append($"自重下位移：{stat.Output.Displacement[2 * i + 1] * 1000:0.0} mm（竖向）\n");
            return sb.ToString();
        }

        public static string MemberProperties(BridgeMember m, GameDatabase db, StructuralModel model, MemberStatus[] status, bool stable, string loadCase)
        {
            var mat = db.GetMaterial(m.MaterialId);
            var sb = new StringBuilder();
            sb.Append($"[b]构件 #{m.Id}[/b]  {mat?.Name}\n");
            if (m.Existing)
            {
                string col = m.Condition >= 0.999 ? "#5ad870" : m.Condition >= 0.7 ? "#ffd040" : "#ff8a50";
                sb.Append($"[color={col}]旧桥原有构件 · 状况 {m.Condition * 100:0}%{(m.Condition >= 0.999 ? "（已修复）" : "（承载力按此折减）")}[/color]\n");
                sb.Append("[color=#9aa3ad]不计费用；节点不能移动；拆除计入上限[/color]\n");
            }
            sb.Append($"长度：{m.Length:0.00} m（最大 {mat?.MaximumLength:0.#} m）\n");
            sb.Append($"质量：{(mat?.MassPerMeter ?? 0) * m.Length:0.0} kg\n");
            sb.Append($"费用：${m.Cost:0}\n");
            if (mat != null)
            {
                sb.Append($"抗拉承载力：{mat.TensionCapacity / 1000:0} kN\n");
                sb.Append(mat.IsCable ? "抗压：无（钢索仅受拉）\n" : $"抗压承载力：{mat.CompressionCapacity(m.Length) / 1000:0} kN（含屈曲）\n");
                if (mat.IsCable || mat.PinnedEnds) sb.Append("连接：两端铰接（只受轴力）\n");
                else if (mat.BendingCapacity > 0) sb.Append($"抗弯承载力：{mat.BendingCapacity / 1000:0} kN·m（刚接）\n");
                if (mat.IsDeck) sb.Append($"桥面宽度：{mat.DeckWidth:0.#} m\n");
                if (mat.IsColumn)
                {
                    sb.Append(mat.BaseRule == BaseRule.Land ? "基础：底端须在陆地上，形成固定基础\n" : "基础：须建在地图锚点上，形成刚接基础\n");
                    sb.Append($"最大倾角：{mat.MaxTiltDegrees:0}°\n");
                }
                if (mat.Extendable) sb.Append("[color=#d9c22e]可伸缩：使用下方按钮调整长度[/color]\n");
                if (!string.IsNullOrEmpty(mat.Description)) sb.Append($"[color=#9aa3ad]{mat.Description}[/color]\n");
            }
            sb.Append($"\n[color=#9aa3ad]{loadCase}[/color]\n");
            if (!stable) { sb.Append("[color=#ff6a5a]结构不稳定，无法给出内力[/color]\n"); return sb.ToString(); }
            if (model != null && status != null && model.MemberIndex.TryGetValue(m.Id, out int k) && k < status.Length)
            {
                var s = status[k];
                if (model.Broken[k]) { sb.Append("[color=#ff6a5a]状态：已断裂[/color]\n"); return sb.ToString(); }
                string mode = s.Mode switch { MemberMode.Tension => "受拉", MemberMode.Compression => "受压", MemberMode.Slack => "松弛（不受力）", _ => "不参与承载" };
                sb.Append($"轴力：{s.Force / 1000:0.0} kN（{mode}）\n");
                if (s.Moment > 0) sb.Append($"最大弯矩：{s.Moment / 1000:0.0} kN·m\n");
                string col = s.Utilization > 1 ? "#ff5040" : s.Utilization > 0.8 ? "#ffd040" : "#5ad870";
                sb.Append($"利用率：[color={col}]{s.Utilization * 100:0}%[/color]\n");
                string safe = s.Utilization > 1 ? "超过允许极限" : s.Utilization > 0.8 ? "接近允许极限" : "安全";
                sb.Append($"安全状态：[color={col}]{safe}[/color]\n");
            }
            return sb.ToString();
        }
    }
}
