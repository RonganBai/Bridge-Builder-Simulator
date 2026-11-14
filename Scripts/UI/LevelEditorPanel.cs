using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>
	/// The one level editor, used by both the main-menu level designer and the in-game level settings, so both offer
	/// exactly the same options. Everything on the map (banks, river width, bank slope, river bed, water, build limits,
	/// fixed nodes, ships) is edited by dragging handles on the side view (<see cref="LevelPreview"/>); a toolbar holds
	/// the terrain switches and an inspector the selected node / ship. Settings that are not on the map (budget, goals,
	/// materials, traffic timeline, JSON) are in tabs below. The hosts only add buttons to <see cref="Actions"/> and
	/// <see cref="Extra"/>.
	/// </summary>
	public class LevelEditorPanel
	{
		private GameDatabase _db;
		private VBoxContainer _form;
		private HBoxContainer _tabBar, _toolbar, _inspector;
		private LevelPreview _preview;
		private Label _errors, _status, _summary;
		private int _tab;
		/// <summary>Control refreshers: an edit on the map can change what a form shows.</summary>
		private readonly List<Action> _sync = new();
		private readonly List<Action> _inspectorSync = new();
		public LevelDefinition Level { get; private set; }
		/// <summary>Row for the host's buttons, under the checks.</summary>
		public HBoxContainer Actions { get; private set; }
		/// <summary>Space under the buttons (saved level list, notes).</summary>
		public VBoxContainer Extra { get; private set; }
		/// <summary>Raised after every edit (the host may track unsaved changes).</summary>
		public event Action Changed;

		private static readonly string[] Tabs = { "基本", "建材", "交通", "高级" };

		public static LevelEditorPanel Create(Control parent, VBoxContainer content, GameDatabase db)
		{
			var p = new LevelEditorPanel { _db = db };
			// ---- map: toolbar, side view, inspector ----
			var mapCard = new PanelContainer(); mapCard.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bg, 8, UiTheme.Border, 10));
			content.AddChild(mapCard);
			var mapBox = new VBoxContainer(); mapBox.AddThemeConstantOverride("separation", 8); mapCard.AddChild(mapBox);
			p._toolbar = new HBoxContainer(); p._toolbar.AddThemeConstantOverride("separation", 12);
			mapBox.AddChild(p._toolbar);
			p._preview = new LevelPreview { CustomMinimumSize = new Vector2(0, 340), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			p._preview.Edited += p.Edited;
			p._preview.SelectionChanged += () => { p.BuildInspector(); if (p._tab == 2) p.BuildForm(); };
			mapBox.AddChild(p._preview);
			p._inspector = new HBoxContainer { CustomMinimumSize = new Vector2(0, 36) }; p._inspector.AddThemeConstantOverride("separation", 8);
			mapBox.AddChild(p._inspector);

			// ---- below: settings tabs | checks and host buttons ----
			var hb = new BoxContainer(); hb.AddThemeConstantOverride("separation", 20);
			content.AddChild(hb);
			var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; left.AddThemeConstantOverride("separation", 10);
			hb.AddChild(left);
			p._tabBar = new HBoxContainer(); p._tabBar.AddThemeConstantOverride("separation", 4);
			left.AddChild(p._tabBar);
			var card = new PanelContainer(); card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bg, 8, UiTheme.Border, 14));
			left.AddChild(card);
			var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(520, 300), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			p._form = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			p._form.AddThemeConstantOverride("separation", 12);
			var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; pad.AddThemeConstantOverride("margin_right", 16);
			pad.AddChild(p._form); scroll.AddChild(pad); card.AddChild(scroll);

			var right = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
			right.AddThemeConstantOverride("separation", 10);
			hb.AddChild(right);
			Label Wrap(Color c) { var x = UiTheme.MakeLabel("", 13, c); x.AutowrapMode = TextServer.AutowrapMode.WordSmart; x.CustomMinimumSize = new Vector2(400, 0); right.AddChild(x); return x; }
			p._summary = Wrap(UiTheme.Muted); p._errors = Wrap(UiTheme.Bad); p._status = Wrap(UiTheme.Muted);
			p.Actions = new HBoxContainer(); p.Actions.AddThemeConstantOverride("separation", 8);
			right.AddChild(p.Actions);
			p.Extra = new VBoxContainer(); p.Extra.AddThemeConstantOverride("separation", 6);
			right.AddChild(p.Extra);
			void Fit() { hb.Vertical = parent.GetViewportRect().Size.X < 1000; }
			parent.Resized += Fit; Fit();
			return p;
		}

		/// <summary>Edits a copy of the level (the original is untouched until the host saves <see cref="Level"/>).</summary>
		public void Load(LevelDefinition level)
		{
			Level = LevelEditing.Copy(level);
            if(Level.ProvidedInfrastructure!=null)Level.ProvidedTerrainSignature??=CustomLiftLevels.Fingerprint(Level);
			Level.Terrain.FoundationZones ??= new(); Level.Terrain.NoBuildZones ??= new(); Level.Terrain.Pillars ??= new();
			_preview.Level = Level; _preview.Selection = null;
			BuildAll();
		}

		public List<string> Problems() => LevelEditing.Validate(Level, _db);

		public void Status(string text, bool good = false)
		{
			_status.Text = text;
			_status.AddThemeColorOverride("font_color", good ? UiTheme.Good : UiTheme.Muted);
		}

		public void ShowErrors(string prefix, List<string> errs)
		{
			_errors.Text = errs.Count == 0 ? "" : prefix + "• " + string.Join("\n• ", errs);
		}

		private void Edited()
		{
			foreach (var s in _sync) s();
			foreach (var s in _inspectorSync) s();
			var errs = Problems();
			ShowErrors("", errs);
			var t = Level.Terrain;
			_summary.Text = (errs.Count == 0 ? "✔ " : "") + Loc.F("河宽 {0:0.#} m · 河道深 {1:0.#} m · 水深 {2:0.#} m · 预算 ${3:N0}",
				t.RiverWidth, LevelEditing.RiverDepth(Level), LevelEditing.WaterDepth(Level), Level.Budget);
			_summary.AddThemeColorOverride("font_color", errs.Count == 0 ? UiTheme.Good : UiTheme.Muted);
			_preview.QueueRedraw();
			Changed?.Invoke();
		}

		private void BuildAll() { BuildToolbar(); BuildInspector(); BuildForm(); }
		private void Rebuild() => Callable.From(BuildAll).CallDeferred();

		// ---------------- controls ----------------

		private static Label Hint(Control parent, string text)
		{
			var h = UiTheme.MakeLabel(text, 12, UiTheme.Muted);
			h.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			parent.AddChild(h);
			return h;
		}

		private void Section(string title, string hint = null)
		{
			if (_form.GetChildCount() > 0) _form.AddChild(new HSeparator());
			_form.AddChild(UiTheme.MakeLabel(title, 15, UiTheme.Accent));
			if (hint != null) Hint(_form, hint);
		}

		/// <summary>Label + value readout on one line, the slider under it, an optional hint below.</summary>
		private HSlider Slider(string label, double min, double max, double step, Func<double> get, Action<double> set,
			string unit = " m", string hint = null, string format = "0.##")
		{
			var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 2);
			var head = new HBoxContainer();
			var name = UiTheme.MakeLabel(label, 14); name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			head.AddChild(name);
			var readout = UiTheme.MakeLabel("", 14, UiTheme.Accent);
			head.AddChild(readout);
			box.AddChild(head);
			double v0 = get();
			var s = new HSlider { MinValue = Math.Min(min, v0), MaxValue = Math.Max(max, v0), Step = step, Value = v0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 22) };
			void Show(double v) => readout.Text = unit == " $" ? "$" + v.ToString("N0") : v.ToString(format) + unit;
			Show(v0);
			bool busy = false;
			s.ValueChanged += v => { if (busy) return; busy = true; set(v); Show(v); Edited(); busy = false; };
			_sync.Add(() =>
			{
				if (busy || !GodotObject.IsInstanceValid(s)) return;
				double v = get();
				if (v < s.MinValue) s.MinValue = v; if (v > s.MaxValue) s.MaxValue = v;
				s.SetValueNoSignal(v); Show(v);
			});
			box.AddChild(s);
			if (hint != null) Hint(box, hint);
			_form.AddChild(box);
			return s;
		}

		/// <summary>On/off switch with its description.</summary>
		private CheckButton Switch(string label, bool on, Action<bool> set, string hint = null, bool rebuild = false)
		{
			var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 0);
			var sw = new CheckButton { Text = label, ButtonPressed = on, Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			sw.Toggled += v => { set(v); if (rebuild) Rebuild(); else Edited(); };
			box.AddChild(sw);
			if (hint != null) Hint(box, hint);
			_form.AddChild(box);
			return sw;
		}

		/// <summary>Segmented control: one pressed button out of a row.</summary>
		private static HBoxContainer Segmented(Control parent, IList<string> items, int selected, Action<int> picked)
		{
			var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 0);
			var group = new ButtonGroup();
			for (int i = 0; i < items.Count; i++)
			{
				int k = i;
				var b = new Button { Text = items[i], ToggleMode = true, ButtonGroup = group, ButtonPressed = i == selected, CustomMinimumSize = new Vector2(64, 32) };
				var normal = UiTheme.Box(UiTheme.Surface, 0, UiTheme.Border, 6); var on = UiTheme.Box(UiTheme.Accent, 0, UiTheme.Accent, 6);
				foreach (var sb in new[] { normal, on })
				{
					if (i == 0) { sb.CornerRadiusTopLeft = 6; sb.CornerRadiusBottomLeft = 6; }
					if (i == items.Count - 1) { sb.CornerRadiusTopRight = 6; sb.CornerRadiusBottomRight = 6; }
				}
				b.AddThemeStyleboxOverride("normal", normal); b.AddThemeStyleboxOverride("pressed", on);
				b.AddThemeStyleboxOverride("hover_pressed", on);
				b.AddThemeColorOverride("font_pressed_color", UiTheme.Bg); b.AddThemeColorOverride("font_hover_pressed_color", UiTheme.Bg);
				b.Pressed += () => picked(k);
				row.AddChild(b);
			}
			parent.AddChild(row);
			return row;
		}

		/// <summary>Toggle chip (materials).</summary>
		private static Button Chip(string text, bool on, Action<bool> set)
		{
			var b = new Button { ToggleMode = true, ButtonPressed = on, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, CustomMinimumSize = new Vector2(0, 32) };
			var pressed = UiTheme.Box(new Color("2c435e"), 16, UiTheme.Accent, 8);
			b.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.Surface, 16, UiTheme.Border, 8));
			b.AddThemeStyleboxOverride("pressed", pressed); b.AddThemeStyleboxOverride("hover_pressed", pressed);
			void Mark(bool v) => b.Text = (v ? "✓ " : "") + text;
			Mark(on);
			b.Toggled += v => { Mark(v); set(v); };
			return b;
		}

		private static OptionButton Options(IEnumerable<string> items, int selected, Action<int> picked, float width = 0)
		{
			var o = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(width, 0) };
			foreach (var s in items) o.AddItem(s);
			o.Selected = Math.Clamp(selected, 0, Math.Max(0, o.ItemCount - 1));
			o.ItemSelected += i => picked((int)i);
			return o;
		}

		private void Field(string label, Control c)
		{
			var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 4);
			box.AddChild(UiTheme.MakeLabel(label, 14));
			c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			box.AddChild(c); _form.AddChild(box);
		}

		private static void Clear(Control c) { foreach (var n in c.GetChildren()) { c.RemoveChild(n); n.QueueFree(); } }

		// ---------------- map toolbar and inspector ----------------

		/// <summary>Terrain switches that have no handle on the map: water, river-bed soil, no-pier zone; plus add buttons.</summary>
		private void BuildToolbar()
		{
			Clear(_toolbar);
			var l = Level; var t = l.Terrain;
			var water = new CheckButton { Text = Loc.T("有水"), ButtonPressed = t.HasWater, TooltipText = Loc.T("关闭 = 干涸的山谷，不能通船") };
			water.Toggled += v => { LevelEditing.SetHasWater(l, v); if (!v && _preview.Selection is TrafficEvent) _preview.Selection = null; Rebuild(); Edited(); };
			_toolbar.AddChild(water);
			var piers = new CheckButton { Text = Loc.T("禁止建桥墩"), ButtonPressed = LevelEditing.NoPiers(l), TooltipText = Loc.T("整条河都不能立桥墩，只能一跨过河") };
			piers.Toggled += v => { LevelEditing.SetNoPiers(l, v); Edited(); };
			_toolbar.AddChild(piers);
			_toolbar.AddChild(UiTheme.MakeLabel(Loc.T("河床"), 13, UiTheme.Muted));
			bool mixed = LevelEditing.MixedSoil(l);
			var items = new List<string> { Loc.T("岩石"), Loc.T("软土") };
			if (mixed) items.Add(Loc.T("分区"));
			var seg = Segmented(_toolbar, items, mixed ? 2 : t.DefaultSoil == "soft" ? 1 : 0, i => { if (i < 2) { LevelEditing.SetSoil(l, i == 1 ? "soft" : "rock"); Rebuild(); Edited(); } });
			seg.TooltipText = Loc.T("岩石：浅基础即可；软土：桥墩建议用桩基础");
			_toolbar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			_toolbar.AddChild(UiTheme.MakeButton(Loc.T("＋ 节点"), () =>
			{
				var p = LevelEditing.BankPoint(l, true, false, 4);
				var a = LevelEditing.AddAnchor(l, new Vec3d(Math.Round(p.X, 2), Math.Round(p.Y, 2), 0));
				_preview.Selection = a; BuildInspector(); Edited();
			}, 80));
			var ship = UiTheme.MakeButton(Loc.T("＋ 船"), () =>
			{
				var s = LevelEditing.AddShip(l, ShipCatalog.Types[0].Id);
				LevelEditing.SetShipLane(l, s, s.LaneX);
				_preview.Selection = s; BuildInspector(); if (_tab == 2) BuildForm(); Edited();
			}, 70);
			ship.Disabled = !t.HasWater; ship.TooltipText = t.HasWater ? "" : Loc.T("无水河道不能有船只通过");
			_toolbar.AddChild(ship);
		}

		/// <summary>Precise values of the selected node or ship (drag on the map, or type here).</summary>
		private void BuildInspector()
		{
			Clear(_inspector); _inspectorSync.Clear();
			var l = Level;
			SpinBox Num(string label, double min, double max, double step, Func<double> get, Action<double> set, string suffix = " m")
			{
				_inspector.AddChild(UiTheme.MakeLabel(label, 13, UiTheme.Muted));
				var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = get(), Suffix = suffix, CustomMinimumSize = new Vector2(110, 0) };
				bool busy = false;
				s.ValueChanged += v => { busy = true; set(v); Edited(); busy = false; };
				_inspectorSync.Add(() => { if (!busy && GodotObject.IsInstanceValid(s)) s.SetValueNoSignal(get()); });
				_inspector.AddChild(s);
				return s;
			}
			void Delete(Action remove) => _inspector.AddChild(UiTheme.MakeButton(Loc.T("删除"), () => { remove(); _preview.Selection = null; BuildInspector(); if (_tab == 2) BuildForm(); Edited(); }, 70));
			switch (_preview.Selection)
			{
				case AnchorDefinition a when l.Anchors.Contains(a):
				{
					string tag = a.IsRoadStart ? Loc.T("（道路起点）") : a.IsRoadEnd ? Loc.T("（道路终点）") : LevelEditing.NeedsPillar(l, a.Position) ? Loc.T("（河中岩墩）") : "";
					_inspector.AddChild(UiTheme.MakeLabel(Loc.T("固定节点 ") + a.Id + tag, 14, UiTheme.Accent));
					Num("x", -400, 700, 0.25, () => a.Position.X, v => LevelEditing.MoveAnchor(l, a, new Vec3d(v, a.Position.Y, a.Position.Z)));
					Num("y", -300, 300, 0.25, () => a.Position.Y, v => LevelEditing.MoveAnchor(l, a, new Vec3d(a.Position.X, v, a.Position.Z)));
					if (!LevelEditing.IsRoadAnchor(a)) Delete(() => LevelEditing.RemoveAnchor(l, a));
					break;
				}
				case TrafficEvent e when l.TrafficEvents.Contains(e):
				{
					int kind = Math.Max(0, ShipCatalog.Types.ToList().FindIndex(s => s.Id == ShipCatalog.Kind(e)));
					var o = Options(ShipCatalog.Types.Select(s => s.Name), kind, i => { LevelEditing.SetShipKind(e, ShipCatalog.Types[i].Id, l.Terrain.RiverWidth); LevelEditing.SetShipLane(l, e, e.LaneX); BuildInspector(); if (_tab == 2) BuildForm(); Edited(); }, 120);
					o.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
					_inspector.AddChild(o);
					Num(Loc.T("航道 x"), -50, 700, 0.25, () => e.LaneX, v => LevelEditing.SetShipLane(l, e, v));
					Num(Loc.T("吃水"), 0.05, 30, 0.05, () => ShipCatalog.Draft(e), v => { LevelEditing.SetShipDraft(e, v); LevelEditing.SetShipLane(l, e, e.LaneX); });
					var pct = UiTheme.MakeLabel("", 12, UiTheme.Muted);
					_inspectorSync.Add(() => pct.Text = Loc.F("船高 {0:0.#} m · 吃水可设 {1:0.##}~{2:0.##} m（10%~50%）", e.Height, LevelEditing.MinDraft(e), LevelEditing.MaxDraft(e)));
					_inspector.AddChild(pct);
					Delete(() => l.TrafficEvents.Remove(e));
					break;
				}
				default:
					_inspector.AddChild(UiTheme.MakeLabel(Loc.T("拖动地图上的圆点修改河岸、河宽、岸坡、河床、水面、建造范围、节点和船；选中节点或船后可在这里输入精确数值"), 12, UiTheme.Muted));
					break;
			}
			foreach (var s in _inspectorSync) s();
		}

		// ---------------- settings tabs ----------------

		private void BuildTabBar()
		{
			Clear(_tabBar);
			var group = new ButtonGroup();
			for (int i = 0; i < Tabs.Length; i++)
			{
				int k = i;
				var b = new Button { Text = Loc.T(Tabs[i]), ToggleMode = true, ButtonGroup = group, ButtonPressed = i == _tab, CustomMinimumSize = new Vector2(0, 34), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				var on = UiTheme.Box(UiTheme.Accent, 6, null, 8);
				b.AddThemeStyleboxOverride("normal", UiTheme.Box(Colors.Transparent, 6, null, 8));
				b.AddThemeStyleboxOverride("pressed", on); b.AddThemeStyleboxOverride("hover_pressed", on);
				b.AddThemeColorOverride("font_color", UiTheme.Muted);
				b.AddThemeColorOverride("font_pressed_color", UiTheme.Bg); b.AddThemeColorOverride("font_hover_pressed_color", UiTheme.Bg);
				b.Pressed += () => { _tab = k; BuildForm(); };
				_tabBar.AddChild(b);
			}
		}

		private void BuildForm()
		{
			Clear(_form);
			_sync.Clear(); _eventBox = null;
			BuildTabBar();
			switch (_tab)
			{
				case 0: BuildBasics(); break;
				case 1: BuildMaterials(); break;
				case 2: BuildTraffic(); break;
				default: BuildAdvanced(); break;
			}
			Edited();
		}

		private void BuildBasics()
		{
			var l = Level; var o = l.Objectives;
            Section("玩法类型");
            string[] modes={"普通造桥关","电梯关 · 沉管通道","电梯关 · 悬浮通道","电梯关 · 盾构通道"};
            string[] ids={null,"tunnel_immersed","tunnel_floating","tunnel_shield"};
            int mode=Array.IndexOf(ids,l.ProvidedInfrastructure?.Tunnel?.MaterialId);
            var adapt=new CheckBox{Text="生成时自动适配河床深度和建造范围",ButtonPressed=true};
            void Generate(int selected)
            {
                if(selected==0){l.ProvidedInfrastructure=null;l.ProvidedTerrainSignature=null;Rebuild();Edited();return;}
                if(CustomLiftLevels.Generate(Level,_db,ids[selected],adapt.ButtonPressed,out var updated,out var error))
                {Load(updated);Status("已生成固定通道及外部支撑。试玩时只设计电梯；预算可继续调整。",true);Edited();}
                else{Status(error);Rebuild();}
            }
            Field("关卡类型",Options(modes,Math.Max(0,mode),Generate));
            _form.AddChild(adapt);
            _form.AddChild(UiTheme.MakeButton("生成 / 更新固定通道",()=>Generate(Math.Max(1,mode)),240));
            Hint(_form,"电梯关由作者提供固定通道，玩家设计两岸电梯。地形变动后请更新通道；首次生成会补齐电梯建材并给出预算，后续可自行调整。");
			Section(Loc.T("关卡信息"));
			var name = new LineEdit { Text = l.Name ?? "", MaxLength = 40, PlaceholderText = Loc.T("关卡名称") };
			name.TextChanged += s => { l.Name = s; Edited(); };
			Field(Loc.T("关卡名称"), name);
			var desc = new TextEdit { Text = l.Description ?? "", PlaceholderText = Loc.T("给玩家的任务说明（可选）"), CustomMinimumSize = new Vector2(0, 70), WrapMode = TextEdit.LineWrappingMode.Boundary };
			desc.TextChanged += () => { l.Description = desc.Text; Edited(); };
			Field(Loc.T("关卡说明"), desc);

			Section(Loc.T("预算与评分"));
			Slider(Loc.T("预算"), 1000, 300000, 500, () => l.Budget, v => l.Budget = v, " $", Loc.T("可以超支，超支只影响评分"));
			Slider(Loc.T("参考造价"), 0, 300000, 500, () => o.ReferenceCost, v => o.ReferenceCost = v, " $", Loc.T("评分的造价基准；0 = 按预算的 90%"));

			Section(Loc.T("通关条件"));
			Slider(Loc.T("桥面最大下沉"), 0.05, 3, 0.05, () => o.MaxDeckDeflection, v => o.MaxDeckDeflection = v, " m", Loc.T("车辆通过时桥面允许下沉的最大值"));
			Slider(Loc.T("允许断裂的构件"), 0, 20, 1, () => o.MaxBrokenMembers, v => o.MaxBrokenMembers = (int)v, Loc.T(" 根"), format: "0");
		}

		private void BuildMaterials()
		{
			var l = Level;
			var mats = _db.Materials.Values.Where(m => !m.IsLift).ToList();
			Section(Loc.T("可用建材"), Loc.T("点击切换；至少要有一种桥面"));
			var row = new HBoxContainer();
			row.AddChild(UiTheme.MakeButton(Loc.T("全选"), () => { foreach (var m in mats) if (!l.AllowedMaterials.Contains(m.MaterialId)) l.AllowedMaterials.Add(m.MaterialId); BuildForm(); }, 80));
			row.AddChild(UiTheme.MakeButton(Loc.T("全不选"), () => { l.AllowedMaterials.Clear(); BuildForm(); }, 80));
			_form.AddChild(row);
			var grid = new GridContainer { Columns = 3 };
			grid.AddThemeConstantOverride("h_separation", 8); grid.AddThemeConstantOverride("v_separation", 8);
			foreach (var m in mats)
			{
				string id = m.MaterialId;
				var chip = Chip(m.Name, l.AllowedMaterials.Contains(id), on => { l.AllowedMaterials.Remove(id); if (on) l.AllowedMaterials.Add(id); Edited(); });
				chip.TooltipText = id;
				grid.AddChild(chip);
			}
			_form.AddChild(grid);
		}

		private void BuildTraffic()
		{
			Section(Loc.T("测试时间线"), Loc.T("时间 = 测试开始后的秒数；车辆依次驶过桥面，船从桥下通过（位置和吃水在地图上拖动），天气改变水位"));
			BuildEvents();
		}

		private void BuildAdvanced()
		{
			var l = Level; var o = l.Objectives;
			Section(Loc.T("特殊规则"));
			Switch(Loc.T("限制拆除旧构件"), l.MaxDemolitions >= 0, v => l.MaxDemolitions = v ? 4 : -1, Loc.T("用于旧桥加固关卡"), true);
			if (l.MaxDemolitions >= 0)
				Slider(Loc.T("最多拆除"), 0, 30, 1, () => l.MaxDemolitions, v => l.MaxDemolitions = (int)v, Loc.T(" 根"), format: "0");
			Switch(Loc.T("隧道坡度特许"), o.TunnelMaxGrade > 0, v => o.TunnelMaxGrade = v ? 0.08 : 0, Loc.T("关闭 = 隧道坡度按车辆能力"), true);
			if (o.TunnelMaxGrade > 0)
				Slider(Loc.T("隧道最大坡度"), 1, 30, 0.5, () => o.TunnelMaxGrade * 100, v => o.TunnelMaxGrade = v / 100, " %");

			Section(Loc.T("JSON"), Loc.T("本关全部设置，可以复制分享，或粘贴后应用"));
			var json = new TextEdit { Text = GameDatabase.SerializeLevel(l), CustomMinimumSize = new Vector2(0, 240), WrapMode = TextEdit.LineWrappingMode.None };
			_form.AddChild(json);
			var jrow = new HBoxContainer(); jrow.AddThemeConstantOverride("separation", 8);
			jrow.AddChild(UiTheme.MakeButton(Loc.T("应用 JSON"), () =>
			{
				try
				{
					var parsed = GameDatabase.DeserializeLevel(json.Text) ?? throw new Exception(Loc.T("内容为空"));
					parsed.Id = l.Id;
					if (_db.LevelError(parsed) is { } err) throw new Exception(err);
					Level = parsed; _preview.Level = parsed; _preview.Selection = null;
					Rebuild();
					Status(Loc.T("✔ 已应用 JSON（还没保存）"), true);
				}
				catch (Exception ex) { Status("✘ " + ex.Message); }
			}, 120));
			jrow.AddChild(UiTheme.MakeButton(Loc.T("刷新 JSON"), () => json.Text = GameDatabase.SerializeLevel(Level), 110));
			_form.AddChild(jrow);
		}

		private VBoxContainer _eventBox;

		private void BuildEvents()
		{
			_eventBox = new VBoxContainer(); _eventBox.AddThemeConstantOverride("separation", 4);
			_form.AddChild(_eventBox);
			FillEvents();
			var l = Level;
			var vehicles = _db.Vehicles.Values.ToList();
			var add = new HBoxContainer(); add.AddThemeConstantOverride("separation", 6);
			int vSel = 0, sSel = 0;
			add.AddChild(Options(vehicles.Select(v => v.Name), 0, i => vSel = i, 130));
			add.AddChild(UiTheme.MakeButton(Loc.T("+ 车辆"), () => { LevelEditing.AddVehicle(l, vehicles[vSel]); FillEvents(); Edited(); }, 80));
			_form.AddChild(add);
			var add2 = new HBoxContainer(); add2.AddThemeConstantOverride("separation", 6);
			add2.AddChild(Options(ShipCatalog.Types.Select(s => s.Name), 0, i => sSel = i, 130));
			var ship = UiTheme.MakeButton(Loc.T("+ 船"), () => { LevelEditing.AddShip(l, ShipCatalog.Types[sSel].Id); FillEvents(); Edited(); }, 70);
			ship.Disabled = !l.Terrain.HasWater; ship.TooltipText = l.Terrain.HasWater ? "" : Loc.T("无水河道不能有船只通过");
			add2.AddChild(ship);
			add2.AddChild(UiTheme.MakeButton(Loc.T("+ 天气"), () => { LevelEditing.AddWeather(l, "rainstorm"); FillEvents(); Edited(); }, 80));
			_form.AddChild(add2);
		}

		private void FillEvents()
		{
			foreach (var c in _eventBox.GetChildren()) { _eventBox.RemoveChild(c); c.QueueFree(); }
			var l = Level;
			var vehicles = _db.Vehicles.Values.ToList();
			var presets = _db.Weather.Presets.ToList();
			if (l.TrafficEvents.Count == 0) _eventBox.AddChild(UiTheme.MakeLabel(Loc.T("还没有车辆：用下面的按钮添加"), 13, UiTheme.Muted));
			foreach (var e in l.TrafficEvents.OrderBy(x => x.Time).ToList())
			{
				var r = new HFlowContainer(); r.AddThemeConstantOverride("h_separation", 6);
				var time = new SpinBox { MinValue = 0, MaxValue = 3600, Step = 0.5, Value = e.Time, Suffix = " s", CustomMinimumSize = new Vector2(100, 0) };
				time.ValueChanged += v => { e.Time = v; Edited(); };
				r.AddChild(time);
				switch (e.Type)
				{
					case TrafficEventType.Vehicle:
					case TrafficEventType.Train:
						r.AddChild(Options(vehicles.Select(v => v.Name), vehicles.FindIndex(v => v.Id == e.VehicleId), i =>
						{
							e.VehicleId = vehicles[i].Id;
							e.Type = RailTrack.IsTrain(vehicles[i]) ? TrafficEventType.Train : TrafficEventType.Vehicle;
							Edited();
						}, 150));
						r.AddChild(UiTheme.MakeLabel(Loc.T("车道"), 13, UiTheme.Muted));
						var lane = new SpinBox { MinValue = 0, MaxValue = 3, Step = 1, Value = e.Lane, CustomMinimumSize = new Vector2(70, 0) };
						lane.ValueChanged += v => { e.Lane = (int)v; Edited(); };
						r.AddChild(lane);
						break;
					case TrafficEventType.Environment:
					{
						var items = new List<string> { Loc.T("洪水（旧式，按上涨高度）") };
						items.AddRange(presets.Select(p => p.Value.Name));
						r.AddChild(Options(items, e.Weather == null ? 0 : presets.FindIndex(p => p.Key == e.Weather) + 1, i =>
						{
							e.Weather = i == 0 ? null : presets[i - 1].Key;
							if (e.Weather != null) e.Name = presets[i - 1].Value.Name;
							FillEvents(); Edited();
						}, 140));
						r.AddChild(UiTheme.MakeLabel(Loc.T("水位上涨"), 13, UiTheme.Muted));
						double rise = e.Weather == null ? e.Height : e.Rise ?? _db.Weather.Preset(e.Weather)?.WaterRise ?? 0;
						var rs = new SpinBox { MinValue = -20, MaxValue = 50, Step = 0.5, Value = rise, Suffix = " m", CustomMinimumSize = new Vector2(100, 0) };
						rs.ValueChanged += v => { if (e.Weather == null) e.Height = v; else e.Rise = v; Edited(); };
						r.AddChild(rs);
						r.AddChild(UiTheme.MakeLabel(Loc.T("持续"), 13, UiTheme.Muted));
						var du = new SpinBox { MinValue = 0, MaxValue = 3600, Step = 1, Value = e.Duration, Suffix = " s", CustomMinimumSize = new Vector2(100, 0) };
						du.ValueChanged += v => { e.Duration = v; Edited(); };
						r.AddChild(du);
						break;
					}
					case TrafficEventType.Ship:
					{
						int kind = Math.Max(0, ShipCatalog.Types.ToList().FindIndex(s => s.Id == ShipCatalog.Kind(e)));
						r.AddChild(Options(ShipCatalog.Types.Select(s => s.Name), kind, i => { LevelEditing.SetShipKind(e, ShipCatalog.Types[i].Id, l.Terrain.RiverWidth); FillEvents(); Edited(); }, 120));
						void Num(string label, double min, double max, double step, double v, Action<double> set, string suffix)
						{
							r.AddChild(UiTheme.MakeLabel(label, 13, UiTheme.Muted));
							var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = v, Suffix = suffix, CustomMinimumSize = new Vector2(95, 0) };
							s.ValueChanged += x => { set(x); Edited(); };
							r.AddChild(s);
						}
						Num(Loc.T("船高"), 0.5, 60, 0.5, e.Height, x => e.Height = x, " m");
						Num(Loc.T("航速"), 0.5, 30, 0.5, e.Speed, x => e.Speed = x, " m/s");
						break;
					}
				}
				r.AddChild(UiTheme.MakeButton(Loc.T("删除"), () => { l.TrafficEvents.Remove(e); FillEvents(); Edited(); }, 60));
				_eventBox.AddChild(r);
			}
		}
	}
}
