using System.Linq;
using BridgeArchitect3D.Core;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>
	/// Level design mode (main menu): name, river width, bank heights, water / bed depth, budget, allowed
	/// materials, vehicles, ship, no-pier zone and extra fixed nodes, with a live side-view preview. Levels are
	/// saved by name to user://custom_levels and can be played, edited or removed (moved to a deleted folder).
	/// </summary>
	public class LevelDesigner
	{
		private GameManager _gm;
		private Control _overlay;
		private VBoxContainer _form, _savedList, _anchorBox;
		private LevelPreview _preview;
		private Label _errors;
		private CustomLevelSpec _spec = new();

		public static LevelDesigner Create(Control parent, GameManager gm)
		{
			var d = new LevelDesigner { _gm = gm };
			var (overlay, content) = UiTheme.MakeDialog(parent, Loc.T("关卡设计"), 1000);
			d._overlay = overlay;
			var hb = new BoxContainer(); hb.AddThemeConstantOverride("separation", 16);
			content.AddChild(hb);
			var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(380, 400), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			d._form = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			d._form.AddThemeConstantOverride("separation", 6);
			scroll.AddChild(d._form); hb.AddChild(scroll);
			var right = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
			right.AddThemeConstantOverride("separation", 8);
			hb.AddChild(right);
			d._preview = new LevelPreview { CustomMinimumSize = new Vector2(380, 235) };
			right.AddChild(d._preview);
			d._errors = UiTheme.MakeLabel("", 13, UiTheme.Bad);
			d._errors.AutowrapMode = TextServer.AutowrapMode.WordSmart; d._errors.CustomMinimumSize = new Vector2(380, 0);
			right.AddChild(d._errors);
			var buttons = new HBoxContainer(); buttons.AddThemeConstantOverride("separation", 8);
			buttons.AddChild(UiTheme.MakeButton(Loc.T("新建"), () => { d._spec = new CustomLevelSpec(); d.BuildForm(); }, 70));
			buttons.AddChild(UiTheme.MakeButton(Loc.T("保存关卡"), () => d.Save(false), 100));
			buttons.AddChild(UiTheme.MakeButton(Loc.T("保存并试玩"), () => d.Save(true), 110));
			buttons.AddChild(UiTheme.MakeButton(Loc.T("关闭"), () => overlay.Visible = false, 70));
			right.AddChild(buttons);
			right.AddChild(UiTheme.MakeLabel(Loc.T("已保存的关卡"), 14, UiTheme.Muted));
			var listScroll = new ScrollContainer { CustomMinimumSize = new Vector2(380, 160) };
			d._savedList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			listScroll.AddChild(d._savedList); right.AddChild(listScroll);
			void Fit(){hb.Vertical=parent.GetViewportRect().Size.X<980;}
            overlay.Resized+=Fit;Fit();
            d.BuildForm();
            return d;
		}

		public void Open() { RefreshSaved(); _overlay.Visible = true; }

		// ---------------- form ----------------
		private SpinBox Spin(string label, double min, double max, double step, double value, System.Action<double> set, string suffix = " m")
		{
			var row = new HBoxContainer();
			row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) });
			var sp = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, CustomMinimumSize = new Vector2(130, 0) };
			sp.ValueChanged += v => { set(v); Refresh(); };
			row.AddChild(sp); _form.AddChild(row);
			return sp;
		}

		private void Header(string text) { _form.AddChild(new HSeparator()); _form.AddChild(UiTheme.MakeLabel(text, 14, UiTheme.Muted)); }

		private void BuildForm()
		{
			foreach (var c in _form.GetChildren()) c.QueueFree();
			var s = _spec; var db = _gm.Db;
			var nameRow = new HBoxContainer();
			nameRow.AddChild(new Label { Text = Loc.T("关卡名称"), CustomMinimumSize = new Vector2(150, 0) });
			var name = new LineEdit { Text = s.Name, MaxLength = 40, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			name.TextChanged += t => { s.Name = t; Refresh(); };
			nameRow.AddChild(name); _form.AddChild(nameRow);
			var desc = new LineEdit { Text = s.Description, PlaceholderText = Loc.T("关卡说明（可选）"), MaxLength = 80 };
			desc.TextChanged += t => s.Description = t;
			_form.AddChild(desc);

			Header(Loc.T("河道与两岸"));
			Spin(Loc.T("河宽"), 6, 80, 0.5, s.RiverWidth, v => s.RiverWidth = v);
			Spin(Loc.T("左岸高度"), -20, 20, 0.5, s.LeftBankHeight, v => s.LeftBankHeight = v);
			Spin(Loc.T("右岸高度"), -20, 20, 0.5, s.RightBankHeight, v => s.RightBankHeight = v);
			Spin(Loc.T("水面低于低岸"), 0.5, 30, 0.5, s.WaterDepth, v => s.WaterDepth = v);
			Spin(Loc.T("河床低于低岸"), 1.5, 40, 0.5, s.BedDepth, v => s.BedDepth = v);
			Spin(Loc.T("岸壁角度（90° 为直立）"), 30, 90, 1, s.BankSlope, v => s.BankSlope = v, " °");
			var water = new CheckBox { Text = Loc.T("河道有水"), ButtonPressed = s.HasWater };
			water.Toggled += on => { s.HasWater = on; Refresh(); };
			_form.AddChild(water);
			var bedRow = new HBoxContainer(); bedRow.AddChild(new Label { Text = Loc.T("河床材质"), CustomMinimumSize = new Vector2(150, 0) });
			var bedOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			bedOpt.AddItem(Loc.T("岩石（浅基础即可）")); bedOpt.AddItem(Loc.T("软土（建议桩基础）"));
			bedOpt.Selected = s.BedSoil == "soft" ? 1 : 0;
			bedOpt.ItemSelected += i => { s.BedSoil = i == 1 ? "soft" : "rock"; Refresh(); };
			bedRow.AddChild(bedOpt); _form.AddChild(bedRow);
			var noPier = new CheckBox { Text = Loc.T("河道内禁止建墩"), ButtonPressed = s.NoPiers };
			noPier.Toggled += on => { s.NoPiers = on; Refresh(); };
			_form.AddChild(noPier);
			var ship = new CheckBox { Text = Loc.T("有船只通过（需要净空或开启桥）"), ButtonPressed = s.Ship };
			ship.Toggled += on => { s.Ship = on; Refresh(); };
			_form.AddChild(ship);
            var shipRow=new HBoxContainer();shipRow.AddChild(UiTheme.MakeLabel(Loc.T("船只类型"),14));
            var shipType=new OptionButton {SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
            for(int i=0;i<ShipCatalog.Types.Count;i++)
            {shipType.AddItem(ShipCatalog.Types[i].Name);if(ShipCatalog.Types[i].Id==s.ShipKind)shipType.Selected=i;}
            shipType.ItemSelected+=index=>{s.ShipKind=ShipCatalog.Types[(int)index].Id;s.ShipHeight=ShipCatalog.Types[(int)index].Height;BuildForm();Refresh();};
            shipRow.AddChild(shipType);_form.AddChild(shipRow);
			Spin(Loc.T("船高（水面以上）"), 1, 20, 0.5, s.ShipHeight, v => s.ShipHeight = v);
			Spin(Loc.T("预算"), 1000, 500000, 500, s.Budget, v => s.Budget = v, " $");

			Header(Loc.T("可用建材"));
			var mg = new GridContainer { Columns = 2 };
			foreach (var m in db.Materials.Values)
			{
				var cb = new CheckBox { Text = m.Name, ButtonPressed = s.Materials.Contains(m.MaterialId) };
				string id = m.MaterialId;
				cb.Toggled += on => { s.Materials.Remove(id); if (on) s.Materials.Add(id); Refresh(); };
				mg.AddChild(cb);
			}
			_form.AddChild(mg);

			Header(Loc.T("通行车辆（按顺序出发，每 2 s 一辆）"));
			var vg = new GridContainer { Columns = 2 };
			foreach (var v in db.Vehicles.Values)
			{
				var cb = new CheckBox { Text = v.Name, ButtonPressed = s.Vehicles.Contains(v.Id) };
				string id = v.Id;
				cb.Toggled += on => { s.Vehicles.Remove(id); if (on) s.Vehicles.Add(id); Refresh(); };
				vg.AddChild(cb);
			}
			_form.AddChild(vg);

			Header(Loc.T("固定节点（左岸 x ≤ 0、右岸 x ≥ 河宽；岸壁坡面上直接固定，河道中央会生成岩墩）"));
			_form.AddChild(UiTheme.MakeLabel(Loc.T("自动生成：L1 / R1 为道路起终点，L2 / R2 为岸坡锚点"), 12, UiTheme.Muted));
			_anchorBox = new VBoxContainer();
			_form.AddChild(_anchorBox);
			_form.AddChild(UiTheme.MakeButton(Loc.T("＋ 添加固定节点"), () => { s.Anchors.Add(new CustomAnchorSpec { X = System.Math.Round(s.RiverWidth / 2), Y = System.Math.Min(s.LeftBankHeight, s.RightBankHeight) - 2 }); BuildAnchors(); Refresh(); }, 160));
			var quick = new GridContainer { Columns = 2 };
			void Quick(string text, bool left, bool wall) => quick.AddChild(UiTheme.MakeButton(text, () =>
			{
				// Next free spot: 4 m steps along the bank top, 2 m steps down the wall
				int k = 1 + s.Anchors.Count(a => wall
					? (left ? a.X > 0.01 && a.X < s.RiverWidth / 2 : a.X < s.RiverWidth - 0.01 && a.X >= s.RiverWidth / 2)
					: (left ? a.X <= 0.01 : a.X >= s.RiverWidth - 0.01));
				var p = s.BankPoint(left, wall, wall ? 2.0 * (k + 1) : 4.0 * k);
				p.X = System.Math.Round(p.X, 2); p.Y = System.Math.Round(p.Y, 2);
				s.Anchors.Add(p); BuildAnchors(); Refresh();
			}, 150));
			Quick(Loc.T("＋ 左岸平地"), true, false); Quick(Loc.T("＋ 右岸平地"), false, false);
			Quick(Loc.T("＋ 左岸岸壁"), true, true); Quick(Loc.T("＋ 右岸岸壁"), false, true);
			_form.AddChild(quick);
			BuildAnchors();
			Refresh();
		}

		private void BuildAnchors()
		{
			foreach (var c in _anchorBox.GetChildren()) c.QueueFree();
			for (int i = 0; i < _spec.Anchors.Count; i++)
			{
				var a = _spec.Anchors[i]; int idx = i;
				var row = new HBoxContainer();
				row.AddChild(new Label { Text = $"C{i + 1}", CustomMinimumSize = new Vector2(36, 0) });
				row.AddChild(new Label { Text = "x" });
				var sx = new SpinBox { MinValue = -8, MaxValue = 90, Step = 0.25, Value = a.X, Suffix = " m", CustomMinimumSize = new Vector2(110, 0) };
				sx.ValueChanged += v => { a.X = v; Refresh(); };
				row.AddChild(sx);
				row.AddChild(new Label { Text = "y" });
				var sy = new SpinBox { MinValue = -60, MaxValue = 40, Step = 0.25, Value = a.Y, Suffix = " m", CustomMinimumSize = new Vector2(110, 0) };
				sy.ValueChanged += v => { a.Y = v; Refresh(); };
				row.AddChild(sy);
				row.AddChild(UiTheme.MakeButton(Loc.T("删除"), () => { _spec.Anchors.RemoveAt(idx); BuildAnchors(); Refresh(); }, 60));
				_anchorBox.AddChild(row);
			}
		}

		private void Refresh()
		{
			var errs = CustomLevelBuilder.Validate(_spec, _gm.Db);
			_errors.AddThemeColorOverride("font_color", UiTheme.Bad);
			_errors.Text = errs.Count == 0 ? "" : "• " + string.Join("\n• ", errs);
			_preview.Level = CustomLevelBuilder.Build(_spec, _gm.Db);
			_preview.QueueRedraw();
		}

		private void Save(bool play)
		{
			var errs = CustomLevelBuilder.Validate(_spec, _gm.Db);
			if (errs.Count > 0) { _errors.Text = Loc.T("无法保存：\n• ") + string.Join("\n• ", errs); return; }
			if (!SaveManager.SaveCustomLevel(_spec, out var err)) { _errors.Text = Loc.T("保存失败：") + err; return; }
			_errors.AddThemeColorOverride("font_color", UiTheme.Good);
			_errors.Text = Loc.F("已保存关卡“{0}”", (_spec.Name));
			RefreshSaved();
			if (play) _gm.StartLevel(_spec.Id, SaveManager.HasSave(_spec.Id));
		}

		private void RefreshSaved()
		{
			foreach (var c in _savedList.GetChildren()) c.QueueFree();
			var list = SaveManager.ListCustomLevels();
			if (list.Count == 0) _savedList.AddChild(UiTheme.MakeLabel(Loc.T("还没有自定义关卡"), 13, UiTheme.Muted));
			foreach (var s in list)
			{
				var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 6);
				row.AddChild(new Label { Text = Loc.F("{0}（河宽 {1:0.#} m · ${2:N0}）", (s.Name), (s.RiverWidth), (s.Budget)), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true });
				var spec = s;
				row.AddChild(UiTheme.MakeButton(Loc.T("编辑"), () => { _spec = spec.Clone(); BuildForm(); }, 60));
				row.AddChild(UiTheme.MakeButton(Loc.T("游玩"), () => _gm.StartLevel(spec.Id, SaveManager.HasSave(spec.Id)), 60));
				Button del = null;
				del = UiTheme.MakeButton(Loc.T("删除"), () =>
				{
					// Two clicks: the file is moved to custom_levels/deleted, not destroyed
					if (del.Text != Loc.T("确认删除")) { del.Text = Loc.T("确认删除"); return; }
					SaveManager.DeleteCustomLevel(spec.Id); RefreshSaved();
				}, 80);
				row.AddChild(del);
				_savedList.AddChild(row);
			}
		}
	}
}
