using System.Collections.Generic;
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
		/// <summary>Named design saves (several per level).</summary>
		void SaveDesignAs(string name); System.Collections.Generic.IReadOnlyList<string> NamedDesigns(); void LoadNamedDesign(string name);
		void BackToMenu(); void OpenLevelSelect(); void OpenLevelSettings();
		void SelectTool(BuildTool tool, string materialId); void SetStressView(bool on); void DeleteSelected(); void RepairSelected(); void SetFoundationSelected(FoundationType type); void TunnelAction(string action);
		/// <summary>Movable-bridge / editing actions on the selection: "lock", "unlock", "leaf", "unleaf", "angle+", "angle-", "split".</summary>
		void MechanismAction(string action);
		void LocateDiagnostic(); void ClearDiagnostic();
		void StartTest(); void TogglePause(); void ResetTest(); void BackToBuild(); void SetSpeed(int x);
		void NextLevel(); void Replay(); void ShowSnapshot(); void LocateProblem(); void SetView(bool side);
		/// <summary>Progress bar drag: show the recorded test state at this time.</summary>
		void SeekTest(double time);
		void ApplySettings(double deformationScale, bool showGrid, double sensitivity, bool fullscreen);
		/// <summary>Engineering contract of the design (null = none), underground cutaway, follow-the-vehicle camera.</summary>
		void SetContract(string id); void SetCutaway(bool on); void SetFollowCamera(bool on); void SetAutoFast(bool on);
	}

	/// <summary>
	/// 游戏界面：顶部状态栏、左侧工具栏、右侧属性面板、底部测试栏、提示、失败分析与过关结算弹窗、设置。
	/// 全部由代码构建，数据由 LevelManager 实时推送。
	/// </summary>
	public partial class GameHUD : Control
	{
		private ILevelController _ctl;
		private Label _levelName, _budget, _used, _remaining, _modeBanner, _preview, _vehicleLabel, _statusLabel, _progressLabel;
		private Button _undo, _redo, _start, _pause, _resetTest, _back;
		private Button[] _toolButtons, _speedButtons;
		private CheckButton _stress;
		private PanelContainer _leftBar, _rightPanel, _bottomBar, _topBar;
		private Control _propertyContent;
		private PanelContainer _loadPanel;
		private RichTextLabel _props;
		private HSlider _progress;
		private bool _scrubbing;
		private Control _failOverlay, _successOverlay, _settingsOverlay;
		private VBoxContainer _failBox, _successBox, _settingsBox;
		private double _toastTimer;
		private RichTextLabel _toast;
		private PanelContainer _toastPanel;
		private Label _toastLegend;
		private Button _toastLocate;
		private bool _toastPersistent;
		private StyleBoxFlat _bottomStyle;

		public void Init(ILevelController ctl, string levelName, string goal, System.Collections.Generic.IReadOnlyList<MaterialDefinition> materials,
			System.Collections.Generic.IReadOnlyList<MaterialDefinition> catalog = null, System.Collections.Generic.IReadOnlyList<VehicleDefinition> levelVehicles = null)
		{
			_ctl = ctl;
			_materials = materials;
			_catalog = catalog ?? materials;
			_levelVehicles = levelVehicles ?? System.Array.Empty<VehicleDefinition>();
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
			BuildLoadBar();
			BuildOverlays();
			BuildMaterialInfo();
			SetMode(false);
			FitViewport();
		}

		// ---------------- 建材性能面板（鼠标移到建材按钮上显示） ----------------
		private System.Collections.Generic.IReadOnlyList<MaterialDefinition> _catalog;
		private System.Collections.Generic.IReadOnlyList<VehicleDefinition> _levelVehicles;
		private PanelContainer _matInfo;
		private VBoxContainer _matInfoBox;
		private static readonly Color CellOff = new(0.24f, 0.27f, 0.32f);

		private void BuildMaterialInfo()
		{
			_matInfo = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
			_matInfo.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 7, UiTheme.Border, 12));
			AddChild(_matInfo);
			_matInfoBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
			_matInfoBox.AddThemeConstantOverride("separation", 4);
			_matInfo.AddChild(_matInfoBox);
		}

		/// <summary>5 格进度条。</summary>
		private static HBoxContainer MakeCells(int cells, Color on)
		{
			var hb = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
			hb.AddThemeConstantOverride("separation", 2);
			for (int i = 0; i < MaterialRatings.MaxCells; i++)
				hb.AddChild(new ColorRect { Color = i < cells ? on : CellOff, CustomMinimumSize = new Vector2(18, 10), MouseFilter = MouseFilterEnum.Ignore });
			return hb;
		}

		private static void AddStatRow(GridContainer g, string label, int cells, Color on, string value)
		{
			g.AddChild(UiTheme.MakeLabel(label, 13, UiTheme.Text));
			g.AddChild(MakeCells(cells, on));
			if (g.Columns == 3) g.AddChild(UiTheme.MakeLabel(value ?? "", 13, UiTheme.Muted));
		}

		private static string Num(double v) => v >= 100 ? v.ToString("0") : v >= 10 ? v.ToString("0.#") : v.ToString("0.##");

		private static GridContainer MakeGrid(bool detail)
		{
			var g = new GridContainer { Columns = detail ? 3 : 2, MouseFilter = MouseFilterEnum.Ignore };
			g.AddThemeConstantOverride("h_separation", 10);
			g.AddThemeConstantOverride("v_separation", 3);
			return g;
		}

		private void ShowMaterialInfo(MaterialDefinition m, Control button)
		{
			if (_matInfo == null || m == null) return;
			bool detail = Core.GameManager.Instance?.Settings?.ShowMaterialValues == true;
			// 立即移除旧内容（QueueFree 要到帧末才生效，会让面板尺寸算错）
			foreach (var c in _matInfoBox.GetChildren()) { _matInfoBox.RemoveChild(c); c.QueueFree(); }
			_matInfoBox.AddChild(UiTheme.MakeLabel(m.Name, 16, UiTheme.Text));
			var desc = UiTheme.MakeLabel(m.Description ?? "", 12, UiTheme.Muted);
			desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			desc.CustomMinimumSize = new Vector2(300, 0);
			_matInfoBox.AddChild(desc);

			if(m.DeviceKind!=null)
			{
				_matInfoBox.AddChild(UiTheme.MakeLabel(m.DeviceKind=="pulley"?Loc.T("每个 $1800 · 额定轴载 600 kN\n定滑轮不省力，改变绳索方向；需要两段钢索。"):Loc.T("每块 5 吨 / $1300 · 每端最多 100 吨\n挂在钢索端点；配重也增加滑轮和支点负荷。"),12,UiTheme.Accent));
				_matInfoBox.AddChild(UiTheme.MakeLabel(Loc.T("Shift 点击可拆除滑轮或减少配重；支持撤销。"),12,UiTheme.Muted));return;
			}
			if (m.IsLift)
			{
				// Lift: 5-cell bars like the other materials (platform range, load, speed, price)
				var lc = TunnelRules.LiftCfg;
				var lg = MakeGrid(detail);
				AddStatRow(lg, Loc.T("最长平台"), MaterialRatings.LinearCells(lc.MaxLength, 30), UiTheme.Accent, Loc.F("{0:0}~{1:0} m", (lc.MinLength), (lc.MaxLength)));
				AddStatRow(lg, Loc.T("升降速度"), MaterialRatings.LinearCells(lc.Speed, 5), UiTheme.Accent, Loc.F("{0:0.#} m/s", (lc.Speed)));
				AddStatRow(lg, Loc.T("价格"), MaterialRatings.LinearCells(3, 5), UiTheme.Warn, Loc.T("平台、吊索、柱子按构件计价；另计驱动、配重、井壁拉结和井道开挖"));
				_matInfoBox.AddChild(lg);
				if (_levelVehicles.Count > 0)
				{
					_matInfoBox.AddChild(new HSeparator());
					_matInfoBox.AddChild(UiTheme.MakeLabel(Loc.T("本关车辆需要的平台长度"), 13, UiTheme.Muted));
					var vg = MakeGrid(detail);
					foreach (var v in _levelVehicles)
					{
						double rear = v.Axles.Count == 0 ? 0 : v.Axles.Min(a => a.Offset), body = 2 * System.Math.Max(0, (v.Length + rear) / 2) - rear;
						double need = body + 2 * lc.Margin;
						AddStatRow(vg, v.Name, MaterialRatings.LinearCells(need, lc.MaxLength), need <= lc.MaxLength ? UiTheme.Good : UiTheme.Bad, Loc.F("{0:0.#} m", (need)));
					}
					_matInfoBox.AddChild(vg);
				}
				if (!detail) _matInfoBox.AddChild(UiTheme.MakeLabel(Loc.T("在设置中勾选“显示数值”可查看具体数字"), 11, UiTheme.Muted));
				PlaceMaterialInfo(button);
				return;
			}
			if (m.IsTunnel)
			{
				// Tunnel strength: 5-cell bars for the strongest grade, then the per-grade table
				var tc0 = Core.GameManager.Instance?.Db?.TunnelConfig?.Type(m);
				if (tc0 != null && tc0.Grades.Count > 0)
				{
					var top = tc0.Grades.Values.OrderBy(g0 => g0.PressureCapacity).Last();
					var cg = MakeGrid(detail);
					AddStatRow(cg, Loc.T("管片强度"), MaterialRatings.LinearCells(top.PressureCapacity / 1e6, 0.6), UiTheme.Accent, Loc.F("{0:0.00} MPa", (top.PressureCapacity / 1e6)));
					AddStatRow(cg, Loc.T("自重"), MaterialRatings.LinearCells(top.MassPerMeter / 1000, 80), UiTheme.Accent, Loc.F("{0:0} 吨/m", (top.MassPerMeter / 1000)));
					AddStatRow(cg, Loc.T("价格"), MaterialRatings.LinearCells(top.CostPerMeter, 3000), UiTheme.Warn, Loc.F("${0:0}/m", (top.CostPerMeter)));
					_matInfoBox.AddChild(cg);
				}
				// Tunnel-specific properties use the same five-cell visuals as other materials.
				var tcfg = Core.GameManager.Instance?.Db?.TunnelConfig;
				var grades = TunnelMaterialRatings.For(tcfg, m);
				if (grades.Count > 0)
				{
					var tg = new GridContainer { Columns = grades.Count + 1, MouseFilter = MouseFilterEnum.Ignore };
					tg.AddThemeConstantOverride("h_separation", 12);
					tg.AddThemeConstantOverride("v_separation", 3);
					tg.AddChild(UiTheme.MakeLabel(Loc.T("属性"),12,UiTheme.Muted));
					foreach(var grade in grades)tg.AddChild(UiTheme.MakeLabel(grade.Name,12,UiTheme.Text));
					for(int index=0;index<grades[0].Stats.Count;index++)
					{
						tg.AddChild(UiTheme.MakeLabel(grades[0].Stats[index].Label,13,UiTheme.Text));
						foreach(var grade in grades)
						{
							var stat=grade.Stats[index];var cell=new VBoxContainer {MouseFilter=MouseFilterEnum.Ignore};
							cell.AddThemeConstantOverride("separation",2);cell.AddChild(MakeCells(stat.Cells,UiTheme.Accent));
							if(detail)cell.AddChild(UiTheme.MakeLabel($"{Num(stat.Value)} {stat.Unit}",12,UiTheme.Muted));
							tg.AddChild(cell);
						}
					}
					_matInfoBox.AddChild(new HSeparator());
					_matInfoBox.AddChild(tg);
				}
				var legend=UiTheme.MakeLabel(Loc.T("统一刻度：价格、自重格数越多越高；承载与容差格数越多越强。"),11,UiTheme.Muted);
				legend.AutowrapMode=TextServer.AutowrapMode.WordSmart;legend.CustomMinimumSize=new Vector2(360,0);_matInfoBox.AddChild(legend);
				foreach (var note in TunnelRules.MaterialNotes(tcfg, m))
				{
					var nl = UiTheme.MakeLabel("· " + note, 12, UiTheme.Muted);
					nl.AutowrapMode = TextServer.AutowrapMode.WordSmart; nl.CustomMinimumSize = new Vector2(360, 0);
					_matInfoBox.AddChild(nl);
				}
				if(!detail)_matInfoBox.AddChild(UiTheme.MakeLabel(Loc.T("设置中开启“显示数值”可查看具体数字"),11,UiTheme.Muted));
				PlaceMaterialInfo(button);
				return;
			}
			var grid = MakeGrid(detail);
			foreach (var s in MaterialRatings.StatsFor(m, _catalog))
				AddStatRow(grid, s.Label, s.Cells, UiTheme.Accent, s.Value > 0 ? $"{Num(s.Value)} {s.Unit}" : "—");
			_matInfoBox.AddChild(grid);

			// 本关车辆重量；桥面另给出承载力，用同一刻度对比
			double cap = MaterialRatings.DeckCapacityTonnes(m);
			double scale = _levelVehicles.Select(MaterialRatings.VehicleTonnes).Concat(_catalog.Select(MaterialRatings.DeckCapacityTonnes)).DefaultIfEmpty(1).Max();
			if (_levelVehicles.Count > 0 || m.IsDeck)
			{
				_matInfoBox.AddChild(new HSeparator());
				_matInfoBox.AddChild(UiTheme.MakeLabel(m.IsDeck ? Loc.T("承载对比（单段最长、整车压在跨中）") : Loc.T("本关车辆重量"), 13, UiTheme.Muted));
				var g2 = MakeGrid(detail);
				if (m.IsDeck) AddStatRow(g2, Loc.T("桥面承载"), MaterialRatings.LinearCells(cap, scale), UiTheme.Good, Loc.F("{0:0} 吨", (cap)));
				foreach (var v in _levelVehicles)
				{
					double t = MaterialRatings.VehicleTonnes(v);
					bool ok = !m.IsDeck || t <= cap;
					string name = m.IsDeck ? $"{(ok ? "✔" : "✘")} {v.Name}" : v.Name;
					string val = v.Units > 1 ? Loc.F("{0} 吨/节 ×{1}", (Num(t)), (v.Units)) : Loc.F("{0} 吨", (Num(t)));
					AddStatRow(g2, name, MaterialRatings.LinearCells(t, scale), m.IsDeck ? (ok ? UiTheme.Warn : UiTheme.Bad) : UiTheme.Warn, val);
				}
				_matInfoBox.AddChild(g2);
			}
			if (!detail) _matInfoBox.AddChild(UiTheme.MakeLabel(Loc.T("在设置中勾选“显示数值”可查看具体数字"), 11, UiTheme.Muted));

			PlaceMaterialInfo(button);
		}

		private void PlaceMaterialInfo(Control button)
		{
			_matInfo.Size = Vector2.Zero; // 收缩到内容大小
			var min = _matInfo.GetCombinedMinimumSize();
			var r = button.GetGlobalRect();
			float x = Mathf.Clamp(r.Position.X, 8, Mathf.Max(8, Size.X - min.X - 8));
			float y = Mathf.Max(62, r.Position.Y - min.Y - 10);
			_matInfo.Position = new Vector2(x, y);
			_matInfo.Visible = true;
		}

		private void HideMaterialInfo() { if (_matInfo != null) _matInfo.Visible = false; }

		private void FitViewport()
		{
			SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			bool compact=Size.X<1200;
			if(_goal!=null)_goal.Visible=!compact;
			if(_budget!=null)_budget.CustomMinimumSize=new Vector2(compact?160:320,0);
			if(_buildStatus!=null)_buildStatus.Visible=!compact;
			if(_leftBar!=null)_leftBar.Visible=Size.X>=1050 && _buildRow?.Visible==true;
			LayoutProperties();
			LayoutToast();
		}

		// ---------------- Named designs ----------------
		private Control _namedOverlay; private VBoxContainer _namedBox;

		private void ShowNamedDesigns()
		{
			if (_namedOverlay == null) (_namedOverlay, _namedBox) = UiTheme.MakeDialog(this, Loc.T("命名方案"), 460);
			foreach (var c in _namedBox.GetChildren()) if (c.GetIndex() > 0) c.QueueFree();
			var row = new HBoxContainer();
			var name = new LineEdit { PlaceholderText = Loc.T("输入方案名称"), SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = 40 };
			row.AddChild(name);
			row.AddChild(UiTheme.MakeButton(Loc.T("保存"), () => { _ctl.SaveDesignAs(name.Text); ShowNamedDesigns(); }, 80));
			_namedBox.AddChild(row);
			var names = _ctl.NamedDesigns();
			_namedBox.AddChild(UiTheme.MakeLabel(names.Count == 0 ? Loc.T("还没有命名方案") : Loc.T("已保存的方案（同名会覆盖）"), 13, UiTheme.Muted));
			foreach (var n in names)
			{
				var r = new HBoxContainer();
				r.AddChild(UiTheme.MakeLabel(n, 15));
				r.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
				string nn = n;
				r.AddChild(UiTheme.MakeButton(Loc.T("读取"), () => { _ctl.LoadNamedDesign(nn); _namedOverlay.Visible = false; }, 70));
				r.AddChild(UiTheme.MakeButton(Loc.T("用此名覆盖"), () => { name.Text = nn; }, 100));
				_namedBox.AddChild(r);
			}
			_namedBox.AddChild(UiTheme.MakeButton(Loc.T("关闭"), () => _namedOverlay.Visible = false, 80));
			_namedOverlay.Visible = true;
		}

		// ---------------- Live load bar ----------------
		private Control _loadTrack; private ColorRect _loadFill; private Label _loadValue;

		/// <summary>Vertical bar at the right edge: the current peak member utilization (0..100%+).</summary>
		private void BuildLoadBar()
		{
			var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, TooltipText = Loc.T("桥梁实时负载：最大构件利用率，越红越接近承载极限") };
			_loadPanel=panel;
			AddChild(panel);
			Place(panel, 1, 0.5f, 1, 0.5f, -58, -150, -8, 170);
			var vb = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
			vb.AddThemeConstantOverride("separation", 4);
			panel.AddChild(vb);
			vb.AddChild(UiTheme.MakeLabel(Loc.T("负载"), 12, UiTheme.Muted));
			_loadTrack = new Control { CustomMinimumSize = new Vector2(22, 240), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore, ClipContents = true };
			_loadTrack.AddChild(new ColorRect { Color = new Color(0.16f, 0.18f, 0.22f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
			_loadFill = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, AnchorLeft = 0, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1 };
			_loadTrack.AddChild(_loadFill);
			vb.AddChild(_loadTrack);
			_loadValue = UiTheme.MakeLabel("—", 12);
			_loadValue.HorizontalAlignment = HorizontalAlignment.Center;
			vb.AddChild(_loadValue);
			SetLoad(0, false);
		}

		/// <summary>Green (safe) -> yellow -> red (at the capacity limit) as utilization rises.</summary>
		public void SetLoad(double utilization, bool valid = true)
		{
			if (_loadFill == null) return;
			double u = valid ? System.Math.Max(0, utilization) : 0;
			float f = (float)System.Math.Clamp(u, 0, 1);
			var green = new Color(0.25f, 0.8f, 0.35f); var yellow = new Color(1f, 0.85f, 0.15f); var red = new Color(0.92f, 0.2f, 0.18f);
			_loadFill.Color = f < 0.6f ? green.Lerp(yellow, f / 0.6f) : yellow.Lerp(red, (f - 0.6f) / 0.4f);
			float h = _loadTrack.CustomMinimumSize.Y;
			_loadFill.OffsetTop = -h * f; _loadFill.OffsetBottom = 0;
			_loadValue.Text = valid ? $"{u * 100:0}%" : "—";
			_loadValue.AddThemeColorOverride("font_color", valid ? _loadFill.Color : UiTheme.Muted);
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
			_topBar.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, 0, UiTheme.Border, 10));
			AddChild(_topBar);
			Place(_topBar, 0, 0, 1, 0, 0, 0, 0, 52);
			var hb = new HBoxContainer(); hb.AddThemeConstantOverride("separation", 8);
			_topBar.AddChild(hb);
			// 顶栏只保留：关卡与目标、预算、撤销 / 重做、视角切换；其余操作收进“菜单”
			var titleBox = new VBoxContainer();
			titleBox.AddThemeConstantOverride("separation", 0);
			_levelName = UiTheme.MakeLabel(levelName, 15, UiTheme.Text);
			_levelName.ClipText=true;_levelName.TooltipText=levelName;
			titleBox.SizeFlagsHorizontal=SizeFlags.ExpandFill;titleBox.CustomMinimumSize=new Vector2(150,0);
			_goal = UiTheme.MakeLabel(goal, 12, UiTheme.Muted);_goal.ClipText=true;_goal.TooltipText=goal;
			titleBox.AddChild(_levelName); titleBox.AddChild(_goal);
			hb.AddChild(titleBox);
			hb.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
			_budget = UiTheme.MakeLabel("", 13, UiTheme.Text);_budget.ClipText=true;_budget.CustomMinimumSize=new Vector2(180,0);
			hb.AddChild(_budget);
			hb.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
			_undo = UiTheme.MakeButton(Loc.T("撤销"), () => _ctl.Undo(), 50);
			_redo = UiTheme.MakeButton(Loc.T("重做"), () => _ctl.Redo(), 50);
			hb.AddChild(_undo); hb.AddChild(_redo);
			hb.AddChild(new Control { CustomMinimumSize = new Vector2(10, 0) });
			var vg = new ButtonGroup();
			_sideBtn = new Button { Text = Loc.T("侧视"), ToggleMode = true, ButtonGroup = vg, ButtonPressed = true, CustomMinimumSize = new Vector2(56, 34), TooltipText = Loc.T("正交侧视（F1），适合建造") };
			_3dBtn = new Button { Text = "3D", ToggleMode = true, ButtonGroup = vg, CustomMinimumSize = new Vector2(48, 34), TooltipText = Loc.T("自由 3D 视角（F3），右键旋转") };
			_sideBtn.Pressed += () => _ctl.SetView(true);
			_3dBtn.Pressed += () => _ctl.SetView(false);
			hb.AddChild(_sideBtn); hb.AddChild(_3dBtn);
			_menu = new MenuButton { Text = Loc.T("菜单"), Flat = false, CustomMinimumSize = new Vector2(80, 34) };
			var pm = _menu.GetPopup();
			pm.AddItem(Loc.T("参考方案提示"), 0); pm.AddItem(Loc.T("重置关卡"), 1); pm.AddSeparator();
			pm.AddItem(Loc.T("保存方案"), 2); pm.AddItem(Loc.T("加载方案"), 3);
			pm.AddItem(Loc.T("方案另存为（命名）…"), 6); pm.AddItem(Loc.T("读取命名方案…"), 7); pm.AddSeparator();
			pm.AddItem(Loc.T("关卡设置…"), 8); pm.AddItem(Loc.T("设置"), 4); pm.AddItem(Loc.T("返回主菜单"), 5);
			pm.IdPressed += id =>
			{
				switch (id)
				{
					case 0: _ctl.LoadReference(); break;
					case 1: _ctl.ResetLevel(); break;
					case 2: _ctl.SaveDesign(); break;
					case 3: _ctl.LoadDesign(); break;
					case 4: HideToast(); _settingsOverlay.Visible = true; break;
					case 5: _ctl.BackToMenu(); break;
					case 6: case 7: HideToast(); ShowNamedDesigns(); break;
					case 8: HideToast(); _ctl.OpenLevelSettings(); break;
				}
			};
			hb.AddChild(_menu);

			_modeBanner = UiTheme.MakeLabel("", 13, UiTheme.Text);
			_modeBanner.HorizontalAlignment = HorizontalAlignment.Center;
			AddChild(_modeBanner);
			_modeBanner.AddThemeStyleboxOverride("normal",UiTheme.Box(UiTheme.Panel,4,UiTheme.Border,5));
			Place(_modeBanner, 0.5f, 0, 0.5f, 0, -58, 62, 58, 90);

			_toastPanel=new PanelContainer { Visible=false, ZIndex=90, MouseFilter=MouseFilterEnum.Stop };
			_toastPanel.AddThemeStyleboxOverride("panel",UiTheme.Box(UiTheme.Panel,6,UiTheme.Border,8));
			AddChild(_toastPanel);
			var notice=new VBoxContainer(); _toastPanel.AddChild(notice);
			var heading=new HBoxContainer(); notice.AddChild(heading);
			var title=UiTheme.MakeLabel(Loc.T("提示"),12); title.SizeFlagsHorizontal=SizeFlags.ExpandFill;
			heading.AddChild(title);
			_toastLocate=UiTheme.MakeButton(Loc.T("定位高亮"),()=>_ctl.LocateDiagnostic()); heading.AddChild(_toastLocate);
			var close=UiTheme.MakeButton(Loc.T("关闭"),()=> { HideToast(); _ctl.ClearDiagnostic(); }); heading.AddChild(close);
			foreach(var button in new[]{_toastLocate,close})
			{
				button.CustomMinimumSize=new Vector2(0,22);button.AddThemeFontSizeOverride("font_size",12);
				foreach(string state in new[]{"normal","hover","pressed"})
					button.AddThemeStyleboxOverride(state,UiTheme.Box(UiTheme.Panel,3,null,3));
			}
			_toast=new RichTextLabel { BbcodeEnabled=false, ScrollActive=true, SelectionEnabled=true,
				AutowrapMode=TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal=SizeFlags.ExpandFill,
				CustomMinimumSize=new Vector2(0,56) };
			_toast.AddThemeFontSizeOverride("normal_font_size",13); notice.AddChild(_toast);
			_toastLegend=UiTheme.MakeLabel(Loc.T("青：当前杆　橙：欠约束　紫：死点　红：冲突"),11,UiTheme.Muted);
			_toastLegend.AutowrapMode=TextServer.AutowrapMode.WordSmart; notice.AddChild(_toastLegend);

			_preview = UiTheme.MakeLabel("", 14);
			_preview.AddThemeStyleboxOverride("normal", UiTheme.Box(new Color(0, 0, 0, 0.7f), 4, null, 4));
			_preview.Visible = false;
			AddChild(_preview);
		}

		private void BuildLeftBar()
		{
			_leftBar = new PanelContainer();
			AddChild(_leftBar);
			Place(_leftBar, 0, 0, 0, 0, 12, 66, 172, 360);
			var vb = new VBoxContainer(); vb.AddThemeConstantOverride("separation", 6);
			_leftBar.AddChild(vb);
			_stress = new CheckButton { Text = Loc.T("受力显示") };
			_stress.Toggled += on => _ctl.SetStressView(on);
			vb.AddChild(_stress);
			_cutawayBuild = new CheckButton { Text = Loc.T("结构剖视"), TooltipText = Loc.T("切开前侧地形和隧道外壳，保留岸线、水位与地层，查看内部结构") };
			_cutawayBuild.Toggled += on => { _ctl.SetCutaway(on); if (_cutawayTest != null) _cutawayTest.SetPressedNoSignal(on); };
			vb.AddChild(_cutawayBuild);
			vb.AddChild(UiTheme.MakeLabel(Loc.T("工程合同"), 12, UiTheme.Muted));
			_contract = new OptionButton { CustomMinimumSize = new Vector2(150, 30) };
			_contract.ItemSelected += i => _ctl.SetContract(i <= 0 || i > _contractIds.Count ? null : _contractIds[(int)i - 1]);
			vb.AddChild(_contract);
			vb.AddChild(new HSeparator());
			var help = UiTheme.MakeLabel(Loc.T("从已有节点开始建造\n长按节点拖动\nCtrl 暂停节点吸附\nX 删除所选\nEsc 取消 · 空格测试"), 12, UiTheme.Muted);
			help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			vb.AddChild(help);
		}

		/// <summary>
		/// Bottom tool strip: icon + text tiles for every material of the level (decks first), then select and
		/// delete. The hotkey on each tile follows the player's settings.
		/// </summary>
		private Control BuildToolTiles()
		{
			var group = new ButtonGroup();
			var decks = _materials.Where(m => m.IsDeck).ToList();
			var ordered = decks.Concat(_materials.Where(m => !m.IsDeck)).ToList();
			_materials = ordered;
			int count = ordered.Count + 2;
			_toolButtons = new Button[count];
			_toolMaterial = new string[count];
			var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
			var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 4);
			scroll.AddChild(row);
			Button Make(int i, Texture2D icon, string tip, BuildTool tool, string matId)
			{
				_toolMaterial[i] = matId;
				var b = new Button
				{
					ToggleMode = true, ButtonGroup = group, CustomMinimumSize = new Vector2(72, 58), TooltipText = tip ?? "",
					Icon = icon, SizeFlagsVertical=SizeFlags.ShrinkCenter, IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top,
				};
				b.AddThemeFontSizeOverride("font_size", 11);
				foreach(string state in new[]{"normal","hover","pressed","disabled"})
				{
					var style=(StyleBoxFlat)UiTheme.Get().GetStylebox(state,"Button").Duplicate();
					style.SetContentMarginAll(4);b.AddThemeStyleboxOverride(state,style);
				}
				b.Pressed += () => _ctl.SelectTool(tool, matId);
				_toolButtons[i] = b;
				return b;
			}
			row.AddChild(Make(0, ToolIcon(null, false), Loc.T("选择构件查看属性；拖动节点；空白处拖动可框选"), BuildTool.Select, null));
			for (int j = 0; j < ordered.Count; j++)
			{
				if (j == decks.Count && j > 0) row.AddChild(new VSeparator());
				var m = ordered[j];
				var mb = Make(j + 1, ToolIcon(m, false), null, BuildTool.Build, m.MaterialId);
				mb.MouseEntered += () => ShowMaterialInfo(m, mb);
				mb.MouseExited += HideMaterialInfo;
				mb.Pressed += HideMaterialInfo;
				row.AddChild(mb);
			}
			row.AddChild(new VSeparator());
			row.AddChild(Make(count - 1, ToolIcon(null, true), Loc.T("直接点击构件或节点删除；空白处拖动可框选；已多选时直接删除所选"), BuildTool.Delete, null));
			RefreshToolKeys();
			return scroll;
		}

		private readonly System.Collections.Generic.Dictionary<string, int> _badges = new();

		/// <summary>Tile captions: name, remaining count for limited materials, and the current hotkey.</summary>
		public void RefreshToolKeys()
		{
			if (_toolButtons == null) return;
			var keys = (Core.GameManager.Instance?.Settings ?? new Core.GameSettings()).ResolveKeys(_materials.Select(m => m.MaterialId));
			bool oneFree = !keys.ContainsValue("1");
			for (int i = 0; i < _toolButtons.Length; i++)
			{
				string text;
				if (i == 0) text = Loc.T("选择 / 拖动") + (oneFree ? "\n[1]" : "");
				else if (i == _toolButtons.Length - 1) text = Loc.T("删除\n[X]");
				else
				{
					var m = _materials[i - 1];
					string key = keys.TryGetValue(m.MaterialId, out var k) && k != "" ? $"[{k}]" : "";
					string badge = _badges.TryGetValue(m.MaterialId, out int r) && r >= 0 ? Loc.F(" 剩{0}", (r)) : "";
					text = $"{m.Name}{badge}\n{key}";
				}
				_toolButtons[i].Text = text;
			}
		}

		/// <summary>Small procedural icon: deck = flat slab, beam = thick bar, cable = thin line, column = upright,
		/// hydraulic = yellow cylinder with a chrome rod; select = arrow, delete = cross.</summary>
		private static Texture2D ToolIcon(MaterialDefinition m, bool delete)
		{
			const int N = 30;
			var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
			img.Fill(new Color(0, 0, 0, 0));
			void Px(int x, int y, Color c) { if (x >= 0 && y >= 0 && x < N && y < N) img.SetPixel(x, y, c); }
			void Line(float x0, float y0, float x1, float y1, float w, Color c)
			{
				for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
				{
					float dx = x1 - x0, dy = y1 - y0, t = Mathf.Clamp(((x - x0) * dx + (y - y0) * dy) / (dx * dx + dy * dy), 0, 1);
					float ex = x0 + dx * t - x, ey = y0 + dy * t - y;
					if (ex * ex + ey * ey <= w * w / 4) Px(x, y, c);
				}
			}
			if (m == null)
			{
				if (delete) { Line(7, 7, 23, 23, 4, UiTheme.Bad); Line(23, 7, 7, 23, 4, UiTheme.Bad); }
				else { Line(9, 6, 9, 24, 3, UiTheme.Text); Line(9, 6, 21, 18, 3, UiTheme.Text); Line(15, 18, 21, 18, 3, UiTheme.Text); Line(14, 18, 19, 26, 3, UiTheme.Text); }
				img.Resize(20,20,Image.Interpolation.Nearest);
			return ImageTexture.CreateFromImage(img);
			}
			var col = string.IsNullOrEmpty(m.Color) ? new Color(0.7f, 0.7f, 0.7f) : new Color(m.Color);
			if(m.DeviceKind=="pulley")
			{
				for(int y=0;y<N;y++)for(int x=0;x<N;x++){float r=new Vector2(x-15,y-15).Length();if(r>7&&r<11)Px(x,y,new Color("e5ba55"));}
				Line(7,15,23,15,2,UiTheme.Text);Line(15,7,15,23,2,UiTheme.Text);
				Line(4,15,4,28,2,UiTheme.Text);Line(26,15,26,28,2,UiTheme.Text);
			}
			else if(m.DeviceKind=="counterweight")
			{
				Line(15,1,15,9,2,UiTheme.Text);
				for(int y=10;y<27;y++)for(int x=5;x<25;x++)Px(x,y,new Color("8c9eac"));
				Line(8,14,22,14,2,new Color("e5ba55"));Line(8,23,22,23,2,new Color("e5ba55"));
			}
			else if (m.IsDeck) { for (int y = 12; y < 19; y++) for (int x = 2; x < 28; x++) Px(x, y, col); Line(5, 15, 25, 15, 1, new Color(0.95f, 0.92f, 0.75f)); }
			else if (m.Extendable) { Line(4, 25, 16, 13, 7, col); Line(14, 15, 26, 4, 3, new Color(0.85f, 0.87f, 0.9f)); }
			else if (m.ComponentType == ComponentType.Cable) Line(4, 26, 26, 4, 2, col);
			else if (m.ComponentType == ComponentType.Column) { for (int y = 3; y < 27; y++) for (int x = 11; x < 19; x++) Px(x, y, col); }
			else Line(4, 25, 26, 5, 6, col);
			img.Resize(20,20,Image.Interpolation.Nearest);
			return ImageTexture.CreateFromImage(img);
		}

		private void BuildRightPanel()
		{
			_rightPanel = new PanelContainer();
			AddChild(_rightPanel);
			Place(_rightPanel, 1, 0, 1, 0, -302, 66, -12, 360);
			var scroll=new ScrollContainer {HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,FollowFocus=true};
			_rightPanel.AddChild(scroll);
			var vb = new VBoxContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill};
			scroll.AddChild(vb);_propertyContent=vb;
			vb.MinimumSizeChanged+=()=>Callable.From(LayoutProperties).CallDeferred();
			BuildLiveMetrics(vb);
			_props = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(270, 0) };
			vb.AddChild(_props);
			// 旧构件修复按钮（加固关）
			_repairBtn = UiTheme.MakeButton(Loc.T("修复旧构件"), () => _ctl.RepairSelected(), 240);
			_repairBtn.Visible = false;
			vb.AddChild(_repairBtn);
			// 桥墩基础切换（选中墩底节点时显示）
			_foundRow = new HBoxContainer();
			_shallowBtn = UiTheme.MakeButton(Loc.F("浅基础 ${0:0}", (FoundationRules.ShallowCost)), () => _ctl.SetFoundationSelected(FoundationType.Shallow), 120);
			_pileBtn = UiTheme.MakeButton(Loc.F("桩基础 ${0:0}", (FoundationRules.PileCost)), () => _ctl.SetFoundationSelected(FoundationType.Pile), 120);
			_foundRow.AddChild(_shallowBtn); _foundRow.AddChild(_pileBtn);
			_foundRow.Visible = false;
			vb.AddChild(_foundRow);
			// Tube element: grade buttons, ground reinforcement, apply to every element
			_tunnelBox = new VBoxContainer { Visible = false };
			var tunnelFocus = new HBoxContainer();
			foreach (var item in new[] { (Loc.T("入口"), "entry"), (Loc.T("全线"), "route"), (Loc.T("出口"), "exit") })
			{
				string destination = item.Item2;
				var focus = UiTheme.MakeButton(Loc.T(item.Item1), () => _ctl.TunnelAction("focus:" + destination), 80);
				focus.TooltipText = Loc.T("定位实际通行路线及岸上出入口");
				tunnelFocus.AddChild(focus);
			}
			_tunnelBox.AddChild(tunnelFocus);
			_tunnelGrades = new VBoxContainer();
			_tunnelBox.AddChild(_tunnelGrades);
			_tunnelReinforce = UiTheme.MakeButton(Loc.T("加固地基"), () => _ctl.TunnelAction("reinforce"), 240);
			_tunnelReinforce.TooltipText = Loc.T("软土承载力提高，冲刷区不再被掏空；按管节长度计费");
			_tunnelBox.AddChild(_tunnelReinforce);
			// Floating tunnel: buoyancy level, cable count and cable kind
			_tunnelBuoy = new HBoxContainer();
			_tunnelBox.AddChild(_tunnelBuoy);
			_tunnelCableRow = new HBoxContainer();
			_tunnelCableRow.AddChild(UiTheme.MakeButton("−", () => _ctl.TunnelAction("cables:-1"), 36));
			_tunnelCableLabel = UiTheme.MakeLabel("", 13, UiTheme.Text);
			_tunnelCableRow.AddChild(_tunnelCableLabel);
			_tunnelCableRow.AddChild(UiTheme.MakeButton("+", () => _ctl.TunnelAction("cables:+1"), 36));
			_tunnelBox.AddChild(_tunnelCableRow);
			_tunnelCableKinds = new VBoxContainer();
			_tunnelBox.AddChild(_tunnelCableKinds);
			_tunnelAll = new CheckBox { Text = Loc.T("应用到全部管节") };
			_tunnelBox.AddChild(_tunnelAll);
			// Bank lifts (whole tunnel, both banks): platform length
			_tunnelPortalRow = new HBoxContainer();
			_tunnelPortalRow.AddChild(UiTheme.MakeButton("−", () => _ctl.TunnelAction("lift:-1"), 36));
			_tunnelRaiseLabel = new Label();
			_tunnelPortalRow.AddChild(_tunnelRaiseLabel);
			_tunnelPortalRow.AddChild(UiTheme.MakeButton("+", () => _ctl.TunnelAction("lift:+1"), 36));
			_tunnelGate = UiTheme.MakeButton(Loc.T("重建电梯结构（按本关车辆）"), () => _ctl.TunnelAction("liftauto"), 240);
			_tunnelGate.TooltipText = Loc.T("删掉两个井道里的电梯构件，按本关车辆选平台长度，并用最便宜的够用模板重新搭出平台、吊索和柱子；之后可以像建桥一样逐根修改");
			_tunnelPump = new Button { Visible = false };
			_tunnelBox.AddChild(_tunnelPortalRow);
			_tunnelBox.AddChild(_tunnelGate);
			_liftSummary = UiTheme.MakeLabel("", 12, UiTheme.Muted);
			_liftSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart; _liftSummary.CustomMinimumSize = new Vector2(240, 0);
			_tunnelBox.AddChild(_liftSummary);
			BuildOperationChoices(_tunnelBox);
			vb.AddChild(_tunnelBox);
			// Lift sheave node (top of a hoist rope): drive and counterweight
			_sheaveBox = new VBoxContainer { Visible = false };
			_sheaveLabel = UiTheme.MakeLabel("", 13, UiTheme.Accent);
			_sheaveLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart; _sheaveLabel.CustomMinimumSize = new Vector2(240, 0);
			_sheaveBox.AddChild(_sheaveLabel);
			_driveRow = new HBoxContainer(); _sheaveBox.AddChild(_driveRow);
			var cwRow = new HBoxContainer();
			cwRow.AddChild(UiTheme.MakeButton(Loc.T("配重 −"), () => _ctl.TunnelAction("liftset:cw:-1"), 118));
			cwRow.AddChild(UiTheme.MakeButton(Loc.T("配重 +"), () => _ctl.TunnelAction("liftset:cw:1"), 118));
			_sheaveBox.AddChild(cwRow);
			vb.AddChild(_sheaveBox);
			// Movable bridge: deck lock on a node, bascule leaf on a hydraulic member, split a member at its midpoint
			_lockBtn = UiTheme.MakeButton(Loc.T("设为中央锁扣"), () => _ctl.MechanismAction(_lockIsSet ? "unlock" : "lock"), 240);
			_lockBtn.TooltipText = Loc.T("把桥面接缝节点拆成左右两个节点：闭合时锁扣传递力，解锁后两侧完全分开");
			_lockBtn.Visible = false; vb.AddChild(_lockBtn);
			_leafBtn = UiTheme.MakeButton(Loc.T("查看驱动关系（自动识别）"), () => _ctl.MechanismAction("describe"), 240);
			_leafBtn.TooltipText = Loc.T("液压杆驱动哪片桥叶、转轴在哪、开桥时分离哪些接缝都由结构自动识别");
			_leafBtn.Visible = false; vb.AddChild(_leafBtn);
			_directionRow=new HBoxContainer();
			// Target length slider: range = what the current structure allows (shortest .. longest), auto computed
			var sliderBox=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};
			_strokeLabel=UiTheme.MakeLabel("",13);
			_strokeSlider=new HSlider{Step=1,CustomMinimumSize=new Vector2(240,20),SizeFlagsHorizontal=SizeFlags.ExpandFill,
				TooltipText=Loc.T("拖动设定液压杆目标长度；范围由当前结构自动计算（最短收缩 ~ 最长伸展）")};
			// Live while dragging (no undo step each tick), committed on release
			_strokeSlider.ValueChanged+=v=>{ UpdateStrokeLabel(v); if(_strokeDragging) _ctl.MechanismAction("preview:"+v.ToString(System.Globalization.CultureInfo.InvariantCulture)); else SendStroke(v); };
			_strokeSlider.DragStarted+=()=>_strokeDragging=true;
			_strokeSlider.DragEnded+=_=>{ _strokeDragging=false; SendStroke(_strokeSlider.Value); };
			_strokeRange=UiTheme.MakeLabel("",11,UiTheme.Muted);
			sliderBox.AddChild(_strokeLabel); sliderBox.AddChild(_strokeSlider); sliderBox.AddChild(_strokeRange);
			_directionRow.AddChild(sliderBox);
			_directionRow.Visible=false; vb.AddChild(_directionRow);
			_splitBtn = UiTheme.MakeButton(Loc.T("中点拆分：两根独立构件"), () => _ctl.MechanismAction("split"), 240);
			_splitBtn.TooltipText = Loc.T("拆成两根独立构件，以可转动的节点连接；给新节点增加支撑，才能缩短实际受弯跨度");
			_splitBtn.Visible = false; vb.AddChild(_splitBtn);
			_rigidSplitBtn=UiTheme.MakeButton(Loc.T("中点添加刚性点（不可折叠）"),()=>_ctl.MechanismAction("rigidSplit"),240);
			_rigidSplitBtn.TooltipText=Loc.T("保持原构件刚性，新增点随构件运动，不作为折叠铰接");
			_rigidSplitBtn.Visible=false;vb.AddChild(_rigidSplitBtn);
			_convertSplitBtn=UiTheme.MakeButton(Loc.T("改为普通连接点（两侧独立）"),()=>_ctl.MechanismAction("convertSplit"),240);
			_convertSplitBtn.Visible=false;vb.AddChild(_convertSplitBtn);
			_rightPanel.Visible = false;
		}

		private string[] _toolMaterial;
		private System.Collections.Generic.IReadOnlyList<MaterialDefinition> _materials = System.Array.Empty<MaterialDefinition>();


		private Button _lockBtn, _leafBtn, _splitBtn, _rigidSplitBtn, _convertSplitBtn;
		private HBoxContainer _directionRow;
		private HSlider _strokeSlider; private Label _strokeLabel, _strokeRange;
		private bool _strokeDragging; private double _strokeL0 = 1;

		private void UpdateStrokeLabel(double pct) =>
			_strokeLabel.Text = Loc.F("目标长度 {0:0.00} m（{1} {2:0}%）", (_strokeL0 * (1 + pct / 100)), ((pct >= 0 ? Loc.T("伸展") : Loc.T("收缩"))), (System.Math.Abs(pct)));

		private void SendStroke(double pct) => _ctl.MechanismAction("target:" + pct.ToString(System.Globalization.CultureInfo.InvariantCulture));

		/// <summary>Shows the hydraulic slider with the structure's reachable range (percent of the built length).</summary>
		public void SetHydraulicRange(bool visible, double builtLength, double minPct, double maxPct, double currentPct, bool leaf)
		{
			_directionRow.Visible = visible;
			if (!visible) return;
			_strokeL0 = builtLength;
			_strokeSlider.MinValue = System.Math.Floor(minPct * 100); _strokeSlider.MaxValue = System.Math.Ceiling(maxPct * 100);
				if(!_strokeDragging) _strokeSlider.SetValueNoSignal(System.Math.Round(currentPct * 100));
			UpdateStrokeLabel(_strokeSlider.Value);
			_strokeRange.Text = Loc.F("可调范围 {0:0.00} ~ {1:0.00} m（{2:+0%;-0%;0%} ~ {3:+0%;-0%;0%}）", (builtLength * (1 + minPct)), (builtLength * (1 + maxPct)), (minPct), (maxPct)) + (leaf ? Loc.T("，按桥叶开启角自动换算") : "");
		}
		private bool _lockIsSet, _leafIsSet;

		/// <summary>Shows the movable-bridge buttons that apply to the current selection.</summary>
		public void SetMechanismRow(bool lockable, bool isLock, bool hydraulic, bool isLeaf, bool splittable, bool linkage=false, bool driver=false, double stroke=0, bool rigidNode=false)
		{
			_lockIsSet = isLock; _leafIsSet = isLeaf;
			// Manual deck locks also define release points for the general linkage mode.
			_lockBtn.Visible = lockable || isLock;
			_lockBtn.Text = isLock ? Loc.T("取消中央锁扣（两侧重新刚接）") : Loc.T("设为中央锁扣");
			_leafBtn.Visible = hydraulic;
			_leafBtn.Text = Loc.T("查看驱动关系（自动识别）");
			if(!hydraulic) _directionRow.Visible=false;
			_splitBtn.Visible = splittable;
			_rigidSplitBtn.Visible=splittable;_convertSplitBtn.Visible=rigidNode;
		}

		private Button _repairBtn, _shallowBtn, _pileBtn;
		private HBoxContainer _foundRow;
		private VBoxContainer _tunnelBox;
		private VBoxContainer _tunnelGrades;
		private Button _tunnelReinforce;
		private CheckBox _tunnelAll;
		private HBoxContainer _tunnelBuoy, _tunnelCableRow;
		private VBoxContainer _tunnelCableKinds;
		private Label _tunnelCableLabel, _tunnelRaiseLabel;
		private HBoxContainer _tunnelPortalRow;
		private Button _tunnelGate, _tunnelPump;

		private Label _liftSummary;
		private VBoxContainer _sheaveBox;
		private Label _sheaveLabel;
		private HBoxContainer _driveRow;

		/// <summary>Lift row on a tube element: platform length and the state of both lifts (built from members in the map).</summary>
		public void SetLiftRow(double length, string summary,string operation=LiftOperation.Shared)
		{
			if (_tunnelRaiseLabel == null) return;
			var lc = TunnelRules.LiftCfg;
			_tunnelRaiseLabel.Text = Loc.F("平台 {0:0} m\n能装车身 ≤ {1:0.#} m\n{2}", (length), (TunnelRules.LiftFits(length)),LiftOperation.Name(operation));
			_tunnelRaiseLabel.TooltipText = Loc.F("两岸各一部，平台 {0:0}~{1:0} m，速度 {2:0.#} m/s；也可以直接拖动平台外端节点改长度。载重由你在井道里搭的平台、吊索和柱子决定", (lc.MinLength), (lc.MaxLength), (lc.Speed));
			if (_liftSummary != null) _liftSummary.Text = summary;
			MarkOperation(operation);
		}

		/// <summary>Machine row on a selected sheave node (top of a hoist rope): drive and counterweight of both lifts.</summary>
		public void SetSheaveRow(bool visible, LiftDesign lift = null, GameDatabase db = null)
		{
			if (_sheaveBox == null) return;
			_sheaveBox.Visible = visible && lift != null && db != null;
			if (!_sheaveBox.Visible) return;
			var c = Physics.LiftStructure.Cfg(db);
			if (_driveRow.GetChildCount() == 0)
				foreach (var (id, dr) in c.Drives)
				{
					var b = UiTheme.MakeButton(Loc.F("{0}\n{1:0} kN ${2:0}", (dr.Name), (dr.Force / 1000), (dr.Cost)), () => _ctl.TunnelAction("liftset:drive:" + id), 80);
					b.SetMeta("drive", id); _driveRow.AddChild(b);
				}
			foreach (var ch in _driveRow.GetChildren()) if (ch is Button b) b.Disabled = (string)b.GetMeta("drive") == lift.Drive;
			_sheaveLabel.Text = Loc.F("电梯滑轮：配重 {0:0} 吨（每 {1:0} 吨 ${2:0}），两岸共用", (lift.Counterweight), (c.CounterweightStep), (c.CounterweightStep * c.CounterweightCostPerTonne));
		}
		private TunnelTypeConfig _tunnelRowType;
		public bool TunnelApplyAll => _tunnelAll?.ButtonPressed ?? false;

		public void SetTunnelRow(bool visible, TunnelTypeConfig type = null, string grade = null, bool reinforced = false,
			int cables = 0, string cable = null, string buoyancy = null)
		{
			_tunnelBox.Visible = visible;
			if (!visible) return;
			if (_tunnelRowType != type)
			{
				_tunnelRowType = type;
				void Fill(BoxContainer box, string meta, System.Collections.Generic.IEnumerable<(string id, string text)> items, string prefix)
				{
					foreach (var c in box.GetChildren()) c.QueueFree();
					foreach (var (id, text) in items)
					{
						var b = UiTheme.MakeButton(text, () => _ctl.TunnelAction(prefix + id), 80);
						b.SetMeta(meta, id);
						box.AddChild(b);
					}
				}
				Fill(_tunnelGrades, "grade", type.Grades.Select(kv => (kv.Key, Loc.F("{0} ${1:0}/m", (kv.Value.Name), (kv.Value.CostPerMeter)))), "grade:");
				Fill(_tunnelBuoy, "buoy", type.Buoyancy.Select(kv => (kv.Key, kv.Value.Name)), "buoy:");
				Fill(_tunnelCableKinds, "cable", type.Cables.Select(kv => (kv.Key, Loc.F("{0} ${1:0}/m", (kv.Value.Name), (kv.Value.CostPerMeter)))), "cable:");
			}
			void Mark(BoxContainer box, string meta, string current)
			{
				foreach (var c in box.GetChildren())
					if (c is Button b && b.HasMeta(meta)) b.Disabled = (string)b.GetMeta(meta) == current && !TunnelApplyAll;
			}
			Mark(_tunnelGrades, "grade", grade);
			// Anchor cables are built by the player as members now: only the buoyancy level stays a tube setting
			_tunnelBuoy.Visible = type.IsFloating;
			_tunnelCableRow.Visible = _tunnelCableKinds.Visible = false;
			if (type.IsFloating) Mark(_tunnelBuoy, "buoy", buoyancy);
			else if (type.IsShield)
			{
				_tunnelReinforce.Text = reinforced ? Loc.T("取消注浆加固") : Loc.F("注浆加固地层 ${0:0}/m", (type.ReinforceCostPerMeter));
				_tunnelReinforce.TooltipText = Loc.T("降低作用在管片上的土压；按管节长度计费");
			}
			// Only material strength damages a tunnel now: ground / anchor improvement only matters for the shield (earth pressure)
			_tunnelReinforce.Visible = type.IsShield;
		}

		/// <summary>Properties of one tube element: load breakdown from the latest tunnel check.</summary>
		public static string TunnelElementProperties(TunnelGeometry g, int i, Physics.TunnelState st, LevelDefinition level, TunnelConfig cfg, string caseName)
		{
			var sb = new StringBuilder();
			sb.Append(Loc.F("[b]管节 #{0}（{1}）[/b]\n", (i + 1), (g.Grade[i].Name)));
			if (g.Type.IsShield) sb.Append(Loc.F("长度 {0:0.0} m · 底部标高 {1:0.0} m{2}\n", (g.Len[i]), (System.Math.Min(g.Y0[i], g.Y1[i])), (g.Reinforced[i] ? Loc.T(" · 已注浆加固") : "")));
			else sb.Append(Loc.F("长度 {0:0.0} m · 底部标高 {1:0.0} m\n", (g.Len[i]), (System.Math.Min(g.Y0[i], g.Y1[i]))));
			sb.Append(caseName).Append('\n');
			if (st != null && i < st.Count && g.Type.IsFloating)
			{
				string Pf(double u) => $"[color={(u >= 1 ? "#e04436" : u >= cfg.RiskOrange ? "#ef8a2e" : u >= cfg.RiskYellow ? "#e8c33a" : "#4caf50")}]{u * 100:0}%[/color]";
				double t = 1000 * cfg.Gravity;
				sb.Append(Loc.F("{0}（配重 {1:0.#} 吨/m）\n", (g.Buoy[i].Name), (Physics.TunnelStructure.Ballast(g, level, cfg, i) / 1000)));
				sb.Append(Loc.F("水压 {0:0} / {1:0} kPa  {2}\n", (st.Pressure[i] / 1000), (g.Grade[i].PressureCapacity / 1000), Pf(st.PressureUtil[i])));
				sb.Append(Loc.F("净上浮力 {0:0} 吨/节 → 管身 → 接缝 → 你建的锚索\n", (st.NetBuoyancy[i] / t)));
				sb.Append(Loc.F("接缝弯矩 {0:0.0} / {1:0.0} MN·m  {2}\n", (System.Math.Abs(st.Moment[i]) / 1e6), (Physics.TunnelStructure.JointMomentCapacity(g.Type, g.Grade[i]) / 1e6), Pf(st.BendUtil[i])));
				if (st.LoadPath[i] != null) sb.Append(st.LoadPath[i]).Append('\n');
				sb.Append(Loc.F("横向摆动（简化估算）{0:0} cm，不参与判定\n", (st.Sway[i] * 100)));
				sb.Append(Loc.F("强度利用率 {0}", Pf(st.Util[i])));
			}
			else if (st != null && i < st.Count && g.Type.IsShield)
			{
				string Ps(double u) => $"[color={(u >= 1 ? "#e04436" : u >= cfg.RiskOrange ? "#ef8a2e" : u >= cfg.RiskYellow ? "#e8c33a" : "#4caf50")}]{u * 100:0}%[/color]";
				var gs = cfg.Geo(st.CrownSoil[i]);
				sb.Append(Loc.F("地下水压 {0:0} kPa（地下水上涨 {1:0.00} m）\n", (st.Pressure[i] / 1000), (st.Groundwater[i])));
				sb.Append(Loc.F("土压 {0:0} kPa\n", (st.EarthPressure[i] / 1000)));
				sb.Append(Loc.F("管片外压 {0:0} / {1:0} kPa  {2}\n", ((st.Pressure[i] + st.EarthPressure[i]) / 1000), (g.Grade[i].PressureCapacity / 1000), Ps(st.PressureUtil[i])));
				sb.Append(Loc.F("强度利用率 {0}", Ps(st.Util[i])));
			}
			else if (st != null && i < st.Count)
			{
				string Pct(double u) => $"[color={(u >= 1 ? "#e04436" : u >= cfg.RiskOrange ? "#ef8a2e" : u >= cfg.RiskYellow ? "#e8c33a" : "#4caf50")}]{u * 100:0}%[/color]";
				sb.Append(Loc.F("水压 {0:0} / {1:0} kPa  {2}\n", (st.Pressure[i] / 1000), (g.Grade[i].PressureCapacity / 1000), Pct(st.PressureUtil[i])));
				sb.Append(Loc.F("净重 {0:0} 吨/节 → 管身 → 接缝 → 你建的桥墩 / 柱子\n", (-st.NetBuoyancy[i] / (1000 * cfg.Gravity))));
				sb.Append(Loc.F("接缝弯矩 {0:0.0} / {1:0.0} MN·m  {2}\n", (System.Math.Abs(st.Moment[i]) / 1e6), (Physics.TunnelStructure.JointMomentCapacity(g.Type, g.Grade[i]) / 1e6), Pct(st.BendUtil[i])));
				if (st.LoadPath[i] != null) sb.Append(st.LoadPath[i]).Append('\n');
				sb.Append(Loc.F("强度利用率 {0}", Pct(st.Util[i])));
			}
			return sb.ToString();
		}

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
			if (visible) _repairBtn.Text = Loc.F("🔧 修复旧构件（${0:0}，恢复全部承载力）", (cost));
		}

		private string _demolitionInfo;
		public void SetDemolitionInfo(string text) { _demolitionInfo = text; }

		private readonly System.Collections.Generic.Dictionary<string, string> _toolBaseText = new();

		/// <summary>限量材料：在工具按钮上显示剩余根数。</summary>
		public void SetToolBadge(string materialId, int remaining)
		{
			_badges[materialId] = remaining;
			for (int i = 0; i < _toolButtons.Length; i++)
				if (_toolMaterial[i] == materialId) _toolButtons[i].Disabled = remaining == 0;
			RefreshToolKeys();
		}

		private void BuildBottomBar()
		{
			_bottomBar = new PanelContainer();
			_bottomStyle = UiTheme.Box(UiTheme.Panel, 0, UiTheme.Border, 10);
			_bottomBar.AddThemeStyleboxOverride("panel", _bottomStyle);
			AddChild(_bottomBar);
			Place(_bottomBar, 0, 1, 1, 1, 0, -96, 0, 0);
			var stack = new Control();
			_bottomBar.AddChild(stack);

			// 建造模式：只突出“开始测试”，旁边显示自重下的结构状态
			_buildRow = new HBoxContainer(); _buildRow.AddThemeConstantOverride("separation", 16);
			_buildRow.SetAnchorsPreset(LayoutPreset.FullRect);
			stack.AddChild(_buildRow);
			_start = UiTheme.MakeButton(Loc.T("开始测试"), () => _ctl.StartTest(), 140);
			_start.CustomMinimumSize = new Vector2(140, 46);
			_start.AddThemeFontSizeOverride("font_size", 15);
			UiTheme.Primary(_start);
			_buildRow.AddChild(_start);
			_buildRow.AddChild(BuildToolTiles());
			_buildStatus = UiTheme.MakeLabel("", 14);
			_buildStatus.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			_buildStatus.CustomMinimumSize = new Vector2(170, 0); _buildStatus.ClipText = true;
			_buildRow.AddChild(_buildStatus);

			// 测试模式：暂停 / 倍速 / 进度 / 返回建造
			var hb = new HBoxContainer(); hb.AddThemeConstantOverride("separation", 10);
			_testRow = hb;
			_testScroll=new ScrollContainer {VerticalScrollMode=ScrollContainer.ScrollMode.Disabled,FollowFocus=true};
			stack.AddChild(_testScroll);_testScroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			hb.SizeFlagsHorizontal=SizeFlags.ExpandFill;_testScroll.AddChild(hb);
			_pause = UiTheme.MakeButton(Loc.T("⏸ 暂停"), () => _ctl.TogglePause(), 90);
			_resetTest = UiTheme.MakeButton(Loc.T("↻ 重新测试"), () => _ctl.ResetTest(), 100);
			_back = UiTheme.MakeButton(Loc.T("← 返回建造"), () => _ctl.BackToBuild(), 110);
			UiTheme.Primary(_back);
			hb.AddChild(_back); hb.AddChild(_pause); hb.AddChild(_resetTest);
			hb.AddChild(UiTheme.MakeLabel(Loc.T("速度"), 14, UiTheme.Muted));
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
			_stressTest = new CheckButton { Text = Loc.T("受力显示"), ButtonPressed = true };
			_stressTest.Toggled += on => _ctl.SetStressView(on);
			hb.AddChild(_stressTest);
			_cutawayTest = new CheckButton { Text = Loc.T("结构剖视"), TooltipText = Loc.T("切开前侧地形和隧道外壳，保留岸线、水位与地层，查看内部结构") };
			_cutawayTest.Toggled += on => { _ctl.SetCutaway(on); _cutawayBuild?.SetPressedNoSignal(on); };
			hb.AddChild(_cutawayTest);
			_follow = new CheckButton { Text = Loc.T("跟车视角"), TooltipText = Loc.T("相机跟着最前面还在行驶的车辆（滚轮仍可缩放）") };
			_follow.Toggled += on => _ctl.SetFollowCamera(on);
			hb.AddChild(_follow);
			var fast = new CheckButton { Text = Loc.T("空闲快进"), ButtonPressed = true, TooltipText = Loc.T("没有活动车辆、升降作业、船只和天气事件时快进；岸上、隧道内与电梯上的车辆保持所选播放倍率") };
			fast.Toggled += on => _ctl.SetAutoFast(on);
			hb.AddChild(fast);
			var vb = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			_progressLabel = UiTheme.MakeLabel(Loc.T("测试进度"), 13, UiTheme.Muted);
			_progress = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.1, CustomMinimumSize = new Vector2(160, 18), TooltipText = Loc.T("测试结束后可拖动，查看对应时刻的实际情况") };
			_progress.DragStarted += () => _scrubbing = true;
			_progress.DragEnded += _ => _scrubbing = false;
			// Only a finished test can be scrubbed; while running the bar is display-only (no accidental freeze)
			_progress.Editable = false; _progress.MouseFilter = MouseFilterEnum.Ignore;
			_progress.ValueChanged += v => { if (_progress.Editable) _ctl.SeekTest(v); };
			vb.AddChild(_progressLabel); vb.AddChild(_progress);
			hb.AddChild(vb);
			var vb2 = new VBoxContainer { CustomMinimumSize = new Vector2(180, 0) };
			vb2.SizeFlagsHorizontal=SizeFlags.ExpandFill;
			_vehicleLabel = UiTheme.MakeLabel(Loc.T("当前车辆：—"), 13);_vehicleLabel.ClipText=true;
			_statusLabel = UiTheme.MakeLabel(Loc.T("结构状态：—"), 14);_statusLabel.ClipText=true;_statusLabel.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;_vehicleLabel.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;
			_statusLabel.ClipText = true;
			vb2.AddChild(_vehicleLabel); vb2.AddChild(_statusLabel);
			hb.AddChild(vb2);
		}

		private HBoxContainer _buildRow, _testRow;
		private ScrollContainer _testScroll;
		private Label _buildStatus;
		private static readonly Color Accent2 = new(0.2f, 0.36f, 0.6f);

		private void BuildOverlays()
		{
			(_failOverlay, _failBox) = UiTheme.MakeDialog(this, Loc.T("测试失败"), 600);
			(_successOverlay, _successBox) = UiTheme.MakeDialog(this, Loc.T("关卡完成！"), 520);
			(_settingsOverlay, _settingsBox) = UiTheme.MakeDialog(this, Loc.T("设置"), Loc.IsEnglish ? 560 : 460);
			BuildSettings();
		}

		private HSlider _defSlider, _sensSlider;
		private CheckBox _gridCheck, _valuesCheck, _lowCheck;
		private System.Action _commitDisplay, _commitGraphics;
		private readonly System.Collections.Generic.List<(string id, OptionButton pick)> _keyPickers = new();
		private Label _defValue;

		private void BuildSettings()
		{
			var gm = Core.GameManager.Instance;
			var s = gm?.Settings ?? new Core.GameSettings();
			_settingsBox.AddChild(UiTheme.MakeLabel(Loc.T("静态桥变形放大（液压机构保持真实比例）"), 14, UiTheme.Muted));
			var row = new HBoxContainer();
			_defSlider = new HSlider { MinValue = 1, MaxValue = 100, Step = 1, Value = s.DeformationScale, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			_defValue = UiTheme.MakeLabel($"{s.DeformationScale:0}×");
			_defSlider.ValueChanged += v => _defValue.Text = $"{v:0}×";
			row.AddChild(_defSlider); row.AddChild(_defValue);
			_settingsBox.AddChild(row);
			_settingsBox.AddChild(UiTheme.MakeLabel(Loc.T("相机灵敏度"), 14, UiTheme.Muted));
			_sensSlider = new HSlider { MinValue = 0.3, MaxValue = 3, Step = 0.1, Value = s.CameraSensitivity };
			_settingsBox.AddChild(_sensSlider);
			_gridCheck = new CheckBox { Text = Loc.T("显示建造网格"), ButtonPressed = s.ShowGrid };
			_settingsBox.AddChild(_gridCheck);
			_commitDisplay = DisplaySettingsUI.Build(_settingsBox, s);
			_settingsBox.AddChild(UiTheme.MakeLabel(Loc.T("画面"), 14, UiTheme.Muted));
			_commitGraphics = GraphicsSettingsUI.Build(_settingsBox, s, false);
			_valuesCheck = new CheckBox { Text = Loc.T("显示数值（建材性能面板显示具体数字）"), ButtonPressed = s.ShowMaterialValues };
			_settingsBox.AddChild(_valuesCheck);
			_settingsBox.AddChild(UiTheme.MakeLabel(Loc.T("建材快捷键（数字键 1~0，未占用的 1 = 选择 / 拖动）"), 14, UiTheme.Muted));
			var keyGrid = new GridContainer { Columns = 4 };
			var resolved = s.ResolveKeys(_materials.Select(m => m.MaterialId));
			_keyPickers.Clear();
			foreach (var m in _materials)
			{
				var pick = new OptionButton { CustomMinimumSize = new Vector2(64, 0) };
				pick.AddItem(Loc.T("无"));
				foreach (var dg in Core.GameSettings.Digits) pick.AddItem(dg);
				string cur = resolved.TryGetValue(m.MaterialId, out var ck) ? ck : "";
				pick.Selected = cur == "" ? 0 : System.Array.IndexOf(Core.GameSettings.Digits, cur) + 1;
				keyGrid.AddChild(UiTheme.MakeLabel(m.Name, 13)); keyGrid.AddChild(pick);
				_keyPickers.Add((m.MaterialId, pick));
			}
			_settingsBox.AddChild(keyGrid);
			var hb = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
			hb.AddChild(UiTheme.MakeButton(Loc.T("确定"), () =>
			{
				var st = Core.GameManager.Instance?.Settings;
				if (st != null)
				{
					st.ShowMaterialValues = _valuesCheck.ButtonPressed;
					_commitGraphics?.Invoke();
					st.MaterialKeys ??= new System.Collections.Generic.Dictionary<string, string>();
					// A key picked twice keeps the first material; the later one gets no key
					var taken = new System.Collections.Generic.HashSet<string>();
					foreach (var (id, pick) in _keyPickers)
					{
						string k = pick.Selected <= 0 ? "" : Core.GameSettings.Digits[pick.Selected - 1];
						if (k != "" && !taken.Add(k)) { k = ""; pick.Selected = 0; }
						st.MaterialKeys[id] = k;
					}
				}
				_commitDisplay?.Invoke();
				_ctl.ApplySettings(_defSlider.Value, _gridCheck.ButtonPressed, _sensSlider.Value, st != null && st.Mode != Core.DisplayMode.Windowed);
				RefreshToolKeys();
				_settingsOverlay.Visible = false;
			}, 90));
			_settingsBox.AddChild(hb);
		}

		public override void _Process(double delta)
		{
			if(_toastPanel?.Visible==true)
			{
				LayoutToast();
				if(!_toastPersistent)
				{
					_toastTimer-=delta;
					if(_toastTimer<=0) HideToast();
				}
			}
		}

		// ---------------- 数据更新 ----------------
		public void UpdateBudget(double used, double budget, bool canUndo, bool canRedo)
		{
			double rem = budget - used;
			// 与结算同一套规则：超出预算仍可建造和通关，但会降低预算分
			_budget.Text = rem >= 0
				? Loc.F("预算目标 ${0:N0} · 已用 ${1:N0} · 剩余 ${2:N0}", (budget), (used), (rem))
				: Loc.F("预算目标 ${0:N0} · 已超 ${1:N0}（扣预算分）", (budget), (-rem));
			if (!string.IsNullOrEmpty(_demolitionInfo)) _budget.Text += " · " + _demolitionInfo;
			_budget.TooltipText=_budget.Text;
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
		public void SetCutawayChecked(bool on) {_cutawayBuild?.SetPressedNoSignal(on);_cutawayTest?.SetPressedNoSignal(on);}
		private CheckButton _stressTest;

		/// <summary>切换建造 / 测试模式的界面外观（测试模式橙色强调、锁定工具栏）。</summary>
		public void SetMode(bool test)
		{
			if(test) HideToast();
			_modeBanner.Text = test ? Loc.T("● 测试模式") : Loc.T("● 建造模式");
			_modeBanner.AddThemeColorOverride("font_color", test ? UiTheme.TestAccent : UiTheme.Accent);
			foreach (var b in _toolButtons) b.Disabled = test;
			if (test) { _repairBtn.Visible = false; _foundRow.Visible = false; }
			_undo.Disabled |= test; _redo.Disabled |= test;
			_undo.Visible = !test; _redo.Visible = !test;
			_leftBar.Visible = !test && Size.X>=1050;
			if(_loadPanel!=null)_loadPanel.Visible=test;                       // 测试时隐藏建造工具
			_buildRow.Visible = !test; _testRow.Visible = test;_testScroll.Visible=test;
			_bottomStyle.BorderWidthTop = 1;
			_bottomStyle.BorderColor = UiTheme.Border;
			// 回到建造时清空上次测试的进度与时间，避免误以为测试仍在进行
			_pause.Text = Loc.T("⏸ 暂停"); _progress.SetValueNoSignal(0);
			_progressLabel.Text = Loc.T("测试进度 0%"); _vehicleLabel.Text = Loc.T("当前车辆：—"); _statusLabel.Text = Loc.T("结构状态：—");
			_failOverlay.Visible = false; _successOverlay.Visible = false;
			_preview.Visible = false;
		}

		public void UpdateTest(double progress, string vehicle, string status, Color statusColor, bool paused, double time, double recorded = 0, bool finished = false)
		{
			_progress.Editable = finished; _progress.MouseFilter = finished ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
			if (!finished) _scrubbing = false;
			_progress.MaxValue = System.Math.Max(recorded, 0.1);
			if (!_scrubbing) _progress.SetValueNoSignal(time);
			_progressLabel.Text = Loc.F("测试进度 {0:0}%   时间 {1:0.0} / {2:0.0}s{3}", (progress * 100), (time), (recorded), ((finished ? Loc.T("（可拖动回看）") : "")));
			_vehicleLabel.Text = Loc.T("当前车辆：") + vehicle;
			_statusLabel.Text = Loc.T("结构状态：") + status;
			_statusLabel.AddThemeColorOverride("font_color", statusColor);
			_pause.Text = paused ? Loc.T("▶ 继续") : Loc.T("⏸ 暂停");
		}

		public void SetStructureStatus(string status, Color c)
		{
			_buildStatus.Text = status;
			_buildStatus.AddThemeColorOverride("font_color", c);
			_statusLabel.Text = Loc.T("结构状态：") + status;
			_statusLabel.AddThemeColorOverride("font_color", c);
		}

		public void ShowProperties(string bbcode, System.Collections.Generic.IReadOnlyList<LivePropertyMetric> metrics=null)
		{
			_rightPanel.Visible = !string.IsNullOrEmpty(bbcode);
			_props.Text = bbcode ?? "";
			SetLiveMetrics(metrics,bbcode);
			Callable.From(LayoutProperties).CallDeferred();
		}

		public void ShowToast(string text, bool error, double seconds = 0, bool hasSceneTargets = false)
		{
			bool warning=error || (text?.Contains(Loc.T("暂不动作")) ?? false) || (text?.Contains(Loc.T("锁死")) ?? false);
			_toast.Text=text ?? "";
			_toast.AddThemeColorOverride("default_color",warning?UiTheme.Bad:UiTheme.Text);
			_toastLocate.Visible=hasSceneTargets;
			_toastLegend.Visible=false;
			_toastPersistent=seconds<=0 && (warning || (text?.Length ?? 0)>100);
			_toastTimer=seconds>0?seconds:System.Math.Clamp((text?.Length ?? 0)/12.0,3,12);
			_toastPanel.Visible=!string.IsNullOrEmpty(text);
			LayoutToast();
		}

		private void HideToast() { _toastTimer=0; _toastPanel.Visible=false; }

		/// <summary>Reserve room for both sidebars; long messages scroll inside a bounded foreground card.</summary>
		private void LayoutToast()
		{
			if(_toastPanel==null) return;
			var viewport=GetViewportRect().Size;
			float left=_leftBar?.Visible==true?_leftBar.GetGlobalRect().End.X+12:12;
			float right=_rightPanel?.Visible==true?_rightPanel.GetGlobalRect().Position.X-12:viewport.X-12;
			if(right-left<330) {left=12;right=viewport.X-12;}
			float width=Mathf.Min(640,Mathf.Max(240,right-left));
			float top=Mathf.Max(90,_topBar?.GetGlobalRect().End.Y+12 ?? 90);
			float height=Mathf.Clamp(viewport.Y*0.13f,86,116);
			_toast.CustomMinimumSize=new Vector2(0,Mathf.Clamp(height-58,28,58));
			_toastPanel.Position=new Vector2(left+(right-left-width)/2,top);
			_toastPanel.Size=new Vector2(width,height);
		}

		private void LayoutProperties()
		{
			if(_rightPanel==null || _propertyContent==null)return;
			float available=Mathf.Max(100,GetViewportRect().Size.Y-182);
			float height=Mathf.Clamp(_propertyContent.GetCombinedMinimumSize().Y+28,100,available);
			_rightPanel.OffsetBottom=66+height;
			if(_loadPanel!=null && _testRow?.Visible==true)
				_loadPanel.Visible=!_rightPanel.Visible || _rightPanel.GetGlobalRect().End.Y<160;
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
			_preview.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			var font = _preview.GetThemeFont("font"); int fontSize = _preview.GetThemeFontSize("font_size");
			float textWidth = text.Split('\n').Select(line => font.GetStringSize(line, HorizontalAlignment.Left, -1, fontSize).X).DefaultIfEmpty(0).Max() + 12;
			_preview.CustomMinimumSize = new Vector2(Mathf.Min(Mathf.Min(420, Mathf.Max(180, Size.X - 32)), Mathf.Max(80, textWidth)), 0);
			_preview.AddThemeColorOverride("font_color", ok ? UiTheme.Good : UiTheme.Bad);
			_preview.ResetSize();
			var minimum = _preview.GetCombinedMinimumSize();
			_preview.Position = new Vector2(Mathf.Clamp(pos.X + 18, 8, Mathf.Max(8, Size.X - minimum.X - 8)),
				Mathf.Clamp(pos.Y + 18, 62, Mathf.Max(62, Size.Y - minimum.Y - 112)));
		}

		// ---------------- 结算弹窗 ----------------
		private void ClearBox(VBoxContainer box)
		{
			foreach (var c in box.GetChildren().Skip(1)) c.QueueFree();   // 保留标题
		}

		public static string PhaseName(TestPhase p) => p switch
		{
			TestPhase.Static => Loc.T("① 静态测试"), TestPhase.Vehicles => Loc.T("② 车辆测试"),
			TestPhase.Environment => Loc.T("③ 环境测试"), _ => Loc.T("④ 结果评估"),
		};

		/// <summary>Phase-4 evaluation: one line per phase plus the weakest place, deformation and weather margin.</summary>
		public static string PhaseReport(SimulationResult r)
		{
			var sb = new StringBuilder();
			for (int p = 0; p < 3; p++)
			{
				var ph = (TestPhase)p; double u = r.PhaseMaxUtil[p];
				bool failedHere = !r.IsSuccess && r.FailurePhase == ph;
				bool after = !r.IsSuccess && (int)r.FailurePhase < p;
				string state = failedHere ? Loc.T("[color=#f09797]✘ 失败[/color]")
					: after || u < 0 ? (ph == TestPhase.Environment && u < 0 && !after ? Loc.T("[color=#a3afbf]本关没有天气事件[/color]") : Loc.T("[color=#a3afbf]未进行[/color]"))
					: Loc.F("[color=#8ecdb1]✔ 通过[/color]　最大利用率 {0:0}%", (u * 100));
				sb.Append(PhaseName(ph)).Append("　").Append(state).Append('\n');
			}
			if (!string.IsNullOrEmpty(r.WeakestText)) sb.Append(Loc.F("最薄弱位置：{0}（利用率 {1:0}%）", (r.WeakestText), (r.MaxUtilization * 100))).Append('\n');
			sb.Append(Loc.F("最大变形：桥面下沉 {0:0.000} m", (r.MaxDeckSag)));
			if (r.MaxTunnelMove > 0) sb.Append(Loc.F(" · 隧道位移 {0:0.000} m", (r.MaxTunnelMove)));
			sb.Append('\n');
			if (!double.IsNaN(r.WeatherMargin)) sb.Append(Loc.F("天气期间安全裕量：{0:0}%", (System.Math.Min(r.WeatherMargin, 9.99) * 100))).Append('\n');
			return sb.ToString();
		}

		public void ShowFailure(SimulationResult r, bool canReplay)
		{
			HideToast();
			ClearBox(_failBox);
			// 先说发生了什么、该怎么改；编号、受力与回放放进“详情”
			var headline = UiTheme.MakeLabel(string.IsNullOrEmpty(r.Headline) ? FailureDetector.ReasonText(r.FailureReason) : r.Headline, 22, UiTheme.Bad);
			headline.AutowrapMode = TextServer.AutowrapMode.WordSmart; // English headlines are longer
			_failBox.AddChild(headline);
			if (!string.IsNullOrEmpty(r.TopSuggestion))
			{
				var tip = UiTheme.MakeLabel(Loc.T("建议：") + r.TopSuggestion, 17, UiTheme.Text);
				tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
				tip.CustomMinimumSize = new Vector2(560, 0);
				_failBox.AddChild(tip);
			}
			var hb = new HBoxContainer(); hb.AddThemeConstantOverride("separation", 10);
			var locate = UiTheme.MakeButton(Loc.T("定位问题并返回建造"), () => _ctl.LocateProblem(), 200);
			locate.CustomMinimumSize = new Vector2(220, 44);
			UiTheme.Primary(locate);
			hb.AddChild(locate);
			hb.AddChild(UiTheme.MakeButton(Loc.T("重新测试"), () => _ctl.ResetTest(), 100));
			_failBox.AddChild(hb);

			var details = new VBoxContainer { Visible = false };
			var toggle = UiTheme.MakeButton(Loc.T("▸ 详情（构件编号、受力与回放）"), null);
			toggle.Flat = true; toggle.Alignment = HorizontalAlignment.Left;
			toggle.Pressed += () => { details.Visible = !details.Visible; toggle.Text = (details.Visible ? "▾" : "▸") + Loc.T(" 详情（构件编号、受力与回放）"); };
			_failBox.AddChild(toggle);
			var sb = new StringBuilder();
			sb.Append($"[color=#f09797]{FailureDetector.ReasonText(r.FailureReason)}[/color]\n{r.FailureMessage}\n\n");
			sb.Append(Loc.F("[color=#a3afbf]发生时间：[/color]{0:0.00} s（{1}）", (r.FailureTime), PhaseName(r.FailurePhase))).Append('\n');
			sb.Append(Loc.F("[color=#a3afbf]涉及构件：[/color]{0}\n", ((r.InvolvedMemberIds.Count > 0 ? string.Join(", ", r.InvolvedMemberIds.Distinct().Select(i => "#" + i)) : "—"))));
			sb.Append(Loc.F("[color=#a3afbf]通过车辆：[/color]{0}/{1}\n\n", (r.PassedVehicles), (r.VehicleResults.Count)));
			sb.Append(PhaseReport(r)).Append('\n');
			sb.Append(Loc.T("[b]其他改进方向：[/b]\n"));
			foreach (var s in r.Suggestions.Distinct()) sb.Append($" • {s}\n");
			details.AddChild(new RichTextLabel { BbcodeEnabled = true, FitContent = true, Text = sb.ToString(), CustomMinimumSize = new Vector2(560, 0) });
			var hb2 = new HBoxContainer(); hb2.AddThemeConstantOverride("separation", 10);
			var snap = UiTheme.MakeButton(Loc.T("首次失效快照"), () => { _failOverlay.Visible = false; _ctl.ShowSnapshot(); }, 120);
			var rep = UiTheme.MakeButton(Loc.T("回放最后几秒"), () => { _failOverlay.Visible = false; _ctl.Replay(); }, 120);
			snap.Disabled = !canReplay; rep.Disabled = !canReplay;
			hb2.AddChild(snap); hb2.AddChild(rep);
			hb2.AddChild(UiTheme.MakeButton(Loc.T("仅返回建造"), () => _ctl.BackToBuild(), 100));
			details.AddChild(hb2);
			_failBox.AddChild(details);
			_failOverlay.Visible = true;
		}

		public void ShowSuccess(SimulationResult r, bool hasNext, int previousBest, System.Collections.Generic.List<string> records = null)
		{
			HideToast();
			ClearBox(_successBox);
			_successBox.AddChild(UiTheme.MakeLabel(Loc.F("工程积分 {0:N0}", (r.Score)), 32, UiTheme.Accent));
			if (r.Score > previousBest) _successBox.AddChild(UiTheme.MakeLabel(previousBest > 0 ? Loc.F("新纪录！（原最高 {0:N0}）", (previousBest)) : Loc.T("首次通关！"), 16, UiTheme.Good));
			else _successBox.AddChild(UiTheme.MakeLabel(Loc.F("本关最高分 {0:N0}", (previousBest)), 14, UiTheme.Muted));
			if (records is { Count: > 0 })
				_successBox.AddChild(UiTheme.MakeLabel(Loc.T("已保存为命名方案：") + string.Join(Loc.T("、"), records.Select(LevelRecord.KindName)), 13, UiTheme.Good));
			// Where the points come from: five base items, then the achievements that multiply them
			var sb = new StringBuilder();
			double refCost = r.ReferenceCost > 0 ? r.ReferenceCost : r.Budget * EngineeringScore.Config.ReferenceCostRatio;
			sb.Append(Loc.F("经济性 {0:N0}　造价 ${1:N0}，参考 ${2:N0}（越省越高，不封顶）\n", (r.EconomyPoints), (r.TotalCost), (refCost)));
			sb.Append(Loc.F("结构安全 {0:N0}　安全裕量 {1:0}%（最大利用率 {2:0}%）\n", (r.SafetyPoints), (System.Math.Min(r.SafetyMargin, 9.99) * 100), (r.MaxUtilization * 100)));
			sb.Append(double.IsNaN(r.WeatherMargin)
				? Loc.F("天气适应 {0:N0}　本关没有天气事件，按结构安全计\n", (r.WeatherPoints))
				: Loc.F("天气适应 {0:N0}　天气期间安全裕量 {1:0}%\n", (r.WeatherPoints), (System.Math.Min(r.WeatherMargin, 9.99) * 100)));
			sb.Append(Loc.F("通行效率 {0:N0}　完成 {1:0.0} s，理想 {2:0.0} s\n", (r.TrafficPoints), (r.CompletionTime), (r.ReferenceTime)));
			sb.Append(Loc.F("环境影响 {0:N0}　河床扰动 {1:0} m / 河宽 {2:0} m\n", (r.EnvironmentPoints), (r.Disturbance), (r.RiverWidth)));
			if (r.DeckIntegrity < 0.999) sb.Append(Loc.F("[color=#e3c384]桥面完整度 {0:0}%，以上各项按比例折算[/color]\n", (r.DeckIntegrity * 100)));
			sb.Append(Loc.F("[b]基础分 {0:N0}[/b]\n", (r.BaseScore)));
			foreach (var a in r.Achievements)
				sb.Append(a.Earned ? Loc.F("[color=#8ecdb1]✔ {0} ×{1:0.00}[/color]　{2}\n", (a.Name), (a.Multiplier), (a.Note))
					: Loc.F("[color=#a3afbf]✘ {0} ×{1:0.00}　{2}[/color]\n", (a.Name), (a.Multiplier), (a.Note)));
			if (r.Achievements.Count > 0) sb.Append(Loc.F("[b]成就倍率 ×{0:0.00}[/b]\n", (r.AchievementMultiplier)));
			if (!string.IsNullOrEmpty(r.ContractNote)) sb.Append(r.ContractMet ? $"[color=#8ecdb1]✔ {r.ContractNote}[/color]\n" : $"[color=#e3c384]✘ {r.ContractNote}[/color]\n");
			sb.Append(Loc.F("[b]工程积分 {0:N0}[/b]\n", (r.Score)));
			if (r.TotalCost > r.Budget + 1e-6) sb.Append(Loc.F("[color=#f09797]超预算 ${0:N0}[/color]\n", (r.TotalCost - r.Budget)));
			sb.Append(Loc.F("通过车辆 {0}/{1}", (r.PassedVehicles), (r.VehicleResults.Count)));
			if (r.TotalShips > 0) sb.Append(Loc.F("　船只 {0}/{1}", (r.ShipsPassed), (r.TotalShips)));
			sb.Append('\n');
			_successBox.AddChild(new RichTextLabel { BbcodeEnabled = true, FitContent = true, Text = sb.ToString(), CustomMinimumSize = new Vector2(520, 0) });
			_successBox.AddChild(new RichTextLabel { BbcodeEnabled = true, FitContent = true, Text = PhaseReport(r), CustomMinimumSize = new Vector2(520, 0) });
			var hb = new HBoxContainer(); hb.AddThemeConstantOverride("separation", 10);
			var next = UiTheme.MakeButton(Loc.T("下一关"), () => _ctl.NextLevel(), 100);
			next.Disabled = !hasNext;
			hb.AddChild(next);
			hb.AddChild(UiTheme.MakeButton(Loc.T("返回关卡选择"), () => _ctl.OpenLevelSelect(), 120));
			hb.AddChild(UiTheme.MakeButton(Loc.T("继续修改"), () => _ctl.BackToBuild(), 100));
			_successBox.AddChild(hb);
			_successOverlay.Visible = true;
		}

		public void HideDialogs() { _failOverlay.Visible = false; _successOverlay.Visible = false; }

		private CheckButton _cutawayBuild, _cutawayTest, _follow;
		private OptionButton _contract;
		private readonly System.Collections.Generic.List<string> _contractIds = new();
		private string _contractShown = "-";

		/// <summary>Contracts this level offers and the design's current one (rebuilt only when something changed).</summary>
		public void SetContracts(System.Collections.Generic.IReadOnlyList<string> ids, string current)
		{
			if (_contract == null) return;
			string key = string.Join(",", ids) + "|" + current;
			if (key == _contractShown) return;
			_contractShown = key;
			_contract.Clear(); _contractIds.Clear();
			_contract.AddItem(Loc.T("无合同"));
			var cfg = EngineeringScore.Config;
			foreach (var id in ids)
			{
				var c = cfg.Contract(id); if (c == null) continue;
				_contractIds.Add(id); _contract.AddItem(c.Name);
				_contract.SetItemTooltip(_contractIds.Count, c.Description);
			}
			int sel = current == null ? 0 : _contractIds.IndexOf(current) + 1;
			_contract.Select(System.Math.Max(0, sel));
			var cc = cfg.Contract(current);
			_contract.TooltipText = cc == null ? Loc.T("可选：接受一份工程合同，改变评分重点并多一个条件，完成后总分有奖励") : cc.Description;
		}

		public void SetFollowChecked(bool on) => _follow?.SetPressedNoSignal(on);

		// ---------------- 属性文本 ----------------
		public static string NodeProperties(BridgeNode n, LevelDefinition level, BridgeDesign d, BridgeSimulation stat)
		{
			if(n.Provided)return Loc.F("[b]关卡提供的固定节点[/b]\n位置：{0}\n隧道、通道和外部支撑不占电梯预算，不能移动或删除。请在两岸电梯井内设计平台、吊索与井架。",n.Position);
			if(n.TunnelPoint)return Loc.F("[b]隧道路线控制点[/b]\n位置：X {0:0.00} m · 底部标高 {1:0.00} m\n",n.Position.X,n.Position.Y)
				+Loc.T("[color=#21c6e1]拖动青色圆点：调整埋深（两端贴合岸壁，只能上下）\n双击圆点：放大后精细调整[/color]\n两岸垂直电梯随端点自动连接。\n每个管节接缝上都有圆点（沉管只有两端），角度变化时管节自动拼合。点击管体可选择管节。\n管底橙色方点是接缝连接点：在下面立桥墩 / 柱子或拉锚索来承担管身。");
			var sb = new StringBuilder();
			if(n.Pulley)sb.Append(Loc.T("[b]定滑轮[/b] · 额定轴载 600 kN · $1800\n两段钢索为一根连续绳索，两侧等张力；Shift 用滑轮工具点击拆除。\n"));
			if(n.CounterweightTonnes>0)sb.Append(Loc.F("[b]配重块 {0:0} 吨[/b] · 自重 {1:0.0} kN · ${2:0}\n用配重工具点击增重，Shift 点击减重。\n", (n.CounterweightTonnes), (n.CounterweightTonnes*9.81), (n.CounterweightTonnes*260)));
			sb.Append(n.IsAnchor ? Loc.F("[b]固定锚点 {0}[/b]\n", (n.AnchorId)) : n.PierBase ? Loc.F("[b]桥墩基础 #{0}[/b]\n", (n.Id)) : Loc.F("[b]节点 #{0}[/b]{1}\n", (n.Id), ((n.PierPart ? Loc.T("（桥墩支点）") : ""))));
			if(n.Pulley && stat?.Output.Stable==true && stat.Model.NodeIndex.TryGetValue(n.Id,out int pulleyIndex))
			{
				double fx=0,fy=0;
				foreach(var rope in d.Members.Where(m=>stat.Db.GetMaterial(m.MaterialId)?.IsCable==true&&(m.StartNodeId==n.Id||m.EndNodeId==n.Id)))
					if(stat.Model.MemberIndex.TryGetValue(rope.Id,out int k))
					{
						double t=stat.Output.AxialForce[k],sign=rope.StartNodeId==n.Id?1:-1;
						fx+=sign*t*stat.Model.Cos[k];fy+=sign*t*stat.Model.Sin[k];sb.Append(Loc.F("钢索 #{0} 张力 {1:0.0} kN\n", (rope.Id), (System.Math.Max(0,t)/1000)));
					}
				sb.Append(Loc.F("滑轮轴合力 {0:0.0} / 600 kN\n", (System.Math.Sqrt(fx*fx+fy*fy)/1000)));
			}
			if (n.IsTunnelSupport)
			{
				var db = stat?.Db ?? Core.GameManager.Instance?.Db;
				var g = db == null ? null : TunnelGeometry.Build(d, db);
				var zone = level.Terrain.ZoneAt(n.Position.X);
				int j = n.TunnelJoint;
				if (g != null && j > 0 && j < g.Count)
				{
					double cap = System.Math.Min(Physics.TunnelStructure.JointMomentCapacity(g.Type, g.Grade[j - 1]), Physics.TunnelStructure.JointMomentCapacity(g.Type, g.Grade[j]));
					int held = d.Members.Count(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id);
					sb.Append(Loc.F("隧道接缝圆点（第 {0} 道接缝，管底）\n接缝抗弯 {1:0.0} MN·m\n这里接了 {2} 根构件{3}\n可以接桥墩、柱子、梁或钢索（不能接桥面）；构件在这里铰接\n", (j), (cap / 1e6), (held), (held == 0 ? Loc.T("：没有支撑，荷载只靠管身传到相邻接缝") : "")));
				}
			}
			if (n.PierBase)
			{
				var zone = level.Terrain.ZoneAt(n.Position.X);
				sb.Append(Loc.F("地基：{0}\n基础：{1}（${2:0}）\n", (zone.Name), (FoundationRules.Name(n.Foundation)), (FoundationRules.Cost(n.Foundation))));
				sb.Append(Loc.F("地基承载力：{0:0} kN\n", (FoundationRules.Capacity(zone, n.Foundation) / 1000)));
				if (zone.Scour) sb.Append(n.Foundation == FoundationType.Pile ? Loc.T("[color=#8ecdb1]冲刷区：桩基础不受冲刷影响[/color]\n") : Loc.T("[color=#e3b67b]冲刷区：洪水时浅基础会被掏空[/color]\n"));
				if (!zone.IsRock && n.Foundation != FoundationType.Pile) sb.Append(Loc.T("[color=#e3c384]软土上的浅基础承载很低，重车可能压垮[/color]\n"));
				sb.Append(Loc.T("[color=#a3afbf]删除墩底节点会拆掉整座桥墩[/color]\n"));
			}
			if(n.IsSplitJoint) sb.Append(Loc.T("普通连接节点：两侧构件独立，可连接其他构件\n"));
			if(n.IsRigidSplit) sb.Append(Loc.T("[color=#91bbeb]刚性拆分点：保持原构件直线，不可折叠（蓝色方块）[/color]\n"));
			sb.Append(Loc.F("位置：{0}\n", (n.Position)));
			int deg = d.Members.Count(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id);
			sb.Append(Loc.F("连接构件：{0}\n", (deg)));
			if (n.IsAnchor)
			{
				var a = level.Anchors.FirstOrDefault(x => x.Id == n.AnchorId);
				if (a != null)
				{
					sb.Append(Loc.F("支撑类型：{0}\n", ((a.SupportType == SupportType.Pinned ? Loc.T("铰支（固定 X/Y）") : Loc.T("滚动（固定 Y）")))));
					sb.Append(a.MaxLoad > 0 ? Loc.F("最大承载：{0:0} kN\n", (a.MaxLoad / 1000)) : Loc.T("最大承载：无限\n"));
					if (stat?.Model != null && stat.Output.Stable && stat.Model.NodeIndex.TryGetValue(n.Id, out int i))
					{
						double r = System.Math.Sqrt(stat.Output.Reaction[2 * i] * stat.Output.Reaction[2 * i] + stat.Output.Reaction[2 * i + 1] * stat.Output.Reaction[2 * i + 1]);
						sb.Append(Loc.F("自重支反力：{0:0.0} kN\n", (r / 1000)));
					}
					sb.Append(Loc.T("[color=#a3afbf]锚点不可移动、不可删除[/color]\n"));
				}
			}
			else if (stat?.Model != null && stat.Output.Stable && stat.Model.NodeIndex.TryGetValue(n.Id, out int i))
				sb.Append(Loc.F("自重下位移：{0:0.0} mm（竖向）\n", (stat.Output.Displacement[2 * i + 1] * 1000)));
			return sb.ToString();
		}

		public static string MemberProperties(BridgeMember m, GameDatabase db, StructuralModel model, MemberStatus[] status, bool stable, string loadCase)
		{
			var mat = db.GetMaterial(m.MaterialId);
			var sb = new StringBuilder();
			sb.Append(Loc.F("[b]构件 #{0}[/b]  {1}\n", (m.Id), (mat?.Name)));
			if(m.Provided)sb.Append(Loc.T("[color=#a3afbf]关卡提供的固定设施 · 不计设计费用 · 不可修改[/color]\n"));
			if (m.Existing)
			{
				string col = m.Condition >= 0.999 ? "#8ecdb1" : m.Condition >= 0.7 ? "#e3c384" : "#e3b67b";
				sb.Append(Loc.F("[color={0}]旧桥原有构件 · 状况 {1:0}%{2}[/color]\n", (col), (m.Condition * 100), ((m.Condition >= 0.999 ? Loc.T("（已修复）") : Loc.T("（承载力按此折减）")))));
				sb.Append(Loc.T("[color=#a3afbf]不计费用；节点不能移动；拆除计入上限[/color]\n"));
			}
			sb.Append(Loc.F("长度：{0:0.00} m（最大 {1:0.#} m）\n", (m.Length), (mat?.MaximumLength)));
			sb.Append(Loc.F("质量：{0:0.0} kg\n", ((mat?.MassPerMeter ?? 0) * m.Length)));
			sb.Append(Loc.F("费用：${0:0}", (m.EffectiveCost())) + (m.CostStroke is double cs && cs > 0 ? Loc.F("（含行程 {0:0%} 加价 ${1:0}）", (cs), (m.EffectiveCost() - m.Cost)) : "") + "\n");
			if (mat != null)
			{
				sb.Append(Loc.F("抗拉承载力：{0:0} kN", (mat.TensionCapacity * CrossBeams.TensionFactor(mat, m.Length) / 1000)) + (CrossBeams.Has(mat, m.Length) ? Loc.F("（{0}，共 {1} 根，抗拉 +{2:0%}；横梁会挡住车辆）", (CrossBeams.RuleText(mat)), (CrossBeams.Count(mat, m.Length)), (CrossBeams.TensionBonus)) : "") + "\n");
				sb.Append(mat.IsCable ? Loc.T("抗压：无（钢索仅受拉）\n") : Loc.F("抗压承载力：{0:0} kN（含屈曲）\n", (mat.CompressionCapacity(m.Length) / 1000)));
				if (mat.IsCable || mat.PinnedEnds) sb.Append(Loc.T("连接：两端铰接（只受轴力）\n"));
				else if (mat.BendingCapacity > 0) sb.Append(Loc.F("抗弯承载力：{0:0} kN·m（刚接）\n", (mat.BendingCapacity / 1000)));
				if (mat.IsDeck) sb.Append(Loc.F("桥面宽度：{0:0.#} m\n", (mat.DeckWidth)));
				if (mat.IsColumn)
				{
					sb.Append(mat.BaseRule == BaseRule.Land ? Loc.T("基础：底端须在陆地上，形成固定基础\n") : Loc.T("基础：须建在地图锚点上，形成刚接基础\n"));
					sb.Append(Loc.F("最大倾角：{0:0}°\n", (mat.MaxTiltDegrees)));
				}
				if (mat.Extendable) sb.Append(Loc.F("液压行程：收缩 {0:0%} / 伸长 {1:0%}；驱动力 {2:0} 吨\n",mat.RetractRatio,mat.ExtendRatio,MaterialRatings.Tonnes(mat.ActuatorForce)));
				if (!string.IsNullOrEmpty(mat.Description)) sb.Append($"[color=#a3afbf]{mat.Description}[/color]\n");
			}
			sb.Append($"\n[color=#a3afbf]{loadCase}[/color]\n");
			if (!stable) { sb.Append(Loc.T("[color=#f09797]结构不稳定，无法给出内力[/color]\n")); return sb.ToString(); }
			if (model != null && status != null && model.MemberIndex.TryGetValue(m.Id, out int k) && k < status.Length)
			{
				var s = status[k];
				if (model.Broken[k]) { sb.Append(Loc.T("[color=#f09797]状态：已断裂[/color]\n")); return sb.ToString(); }
				string mode = s.Mode switch { MemberMode.Tension => Loc.T("受拉"), MemberMode.Compression => Loc.T("受压"), MemberMode.Slack => Loc.T("松弛（不受力）"), _ => Loc.T("不参与承载") };
				sb.Append(Loc.F("轴力：{0:0.0} kN（{1}）\n", (s.Force / 1000), (mode)));
				if (s.Moment > 0) sb.Append(Loc.F("最大弯矩：{0:0.0} kN·m\n", (s.Moment / 1000)));
				string col = s.Utilization > 1 ? "#f09797" : s.Utilization > 0.8 ? "#e3c384" : "#8ecdb1";
				sb.Append(Loc.F("利用率：[color={0}]{1:0}%[/color]\n", (col), (s.Utilization * 100)));
				string safe = s.Utilization > 1 ? Loc.T("超过允许极限") : s.Utilization > 0.8 ? Loc.T("接近允许极限") : Loc.T("安全");
				sb.Append(Loc.F("安全状态：[color={0}]{1}[/color]\n", (col), (safe)));
			}
			return sb.ToString();
		}
	}
}
