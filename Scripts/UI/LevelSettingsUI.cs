using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>
	/// "Level settings" dialog: edits a copy of the current level (budget, terrain levels, materials, traffic and
	/// weather events, objectives, or the whole definition as JSON). Saving replaces the built-in level data with a
	/// per-player override (user://level_overrides); "Restore defaults" removes it again.
	/// </summary>
	public static class LevelSettingsUI
	{
		public static void Open(Control parent, GameDatabase db, LevelDefinition current, bool overridden,
			Action<LevelDefinition> save, Action restore)
		{
			var work = GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(current));
			var (overlay, content) = UiTheme.MakeDialog(parent, Loc.T("关卡设置"), 820);
			overlay.VisibilityChanged += () => { if (!overlay.Visible) overlay.QueueFree(); };

			var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			body.AddThemeConstantOverride("separation", 10);
			content.AddChild(body);
			var status = UiTheme.MakeLabel("", 13, UiTheme.Muted);
			status.AutowrapMode = TextServer.AutowrapMode.WordSmart;

			void Rebuild()
			{
				foreach (var c in body.GetChildren()) { body.RemoveChild(c); c.QueueFree(); }
				Build(body, db, work, Rebuild, status);
			}
			Rebuild();

			content.AddChild(status);
			status.Text = (overridden ? Loc.T("本关正在使用你修改过的设置。") : Loc.T("本关使用内置设置。"))
				+ Loc.T("保存后替换本关的内置数据（存在本机，方案存档不变），本关重新开始；“恢复默认”还原内置数据。");
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 10);
			var ok = UiTheme.MakeButton(Loc.T("保存并重新开始本关"), () =>
			{
				string err = db.LevelError(work);
				if (err != null) { status.Text = "✘ " + err; status.AddThemeColorOverride("font_color", UiTheme.Bad); return; }
				overlay.Hide(); save(work);
			}, 200);
			UiTheme.Primary(ok);
			row.AddChild(ok);
			var reset = UiTheme.MakeButton(Loc.T("恢复默认"), () => { overlay.Hide(); restore(); }, 110);
			reset.Disabled = !overridden;
			row.AddChild(reset);
			row.AddChild(UiTheme.MakeButton(Loc.T("取消"), () => overlay.Hide(), 90));
			content.AddChild(row);
			overlay.Show();
		}

		private static Label Section(string t) => UiTheme.MakeLabel(t, 16, UiTheme.Accent);

		private static SpinBox Spin(double v, double min, double max, double step, Action<double> set, string suffix = "")
		{
			var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = v, Suffix = suffix, CustomMinimumSize = new Vector2(130, 0), AllowGreater = true, AllowLesser = true };
			s.ValueChanged += x => set(x);
			return s;
		}

		private static GridContainer Grid(int cols)
		{
			var g = new GridContainer { Columns = cols };
			g.AddThemeConstantOverride("h_separation", 12);
			g.AddThemeConstantOverride("v_separation", 6);
			return g;
		}

		private static void Field(GridContainer g, string label, Control c)
		{
			g.AddChild(UiTheme.MakeLabel(label, 13, UiTheme.Text));
			g.AddChild(c);
		}

		private static void Build(VBoxContainer body, GameDatabase db, LevelDefinition l, Action rebuild, Label status)
		{
			var t = l.Terrain; var o = l.Objectives;

			body.AddChild(Section(Loc.T("基本")));
			var g = Grid(4);
			Field(g, Loc.T("预算 $"), Spin(l.Budget, 0, 1e7, 500, v => l.Budget = v));
			Field(g, Loc.T("参考造价 $（0 = 预算 × 0.9）"), Spin(o.ReferenceCost, 0, 1e7, 500, v => o.ReferenceCost = v));
			Field(g, Loc.T("隧道坡度特许 %（0 = 按车辆）"), Spin(o.TunnelMaxGrade * 100, 0, 30, 0.5, v => o.TunnelMaxGrade = v / 100));
			Field(g, Loc.T("桥面允许下沉 m"), Spin(o.MaxDeckDeflection, 0.01, 5, 0.01, v => o.MaxDeckDeflection = v));
			Field(g, Loc.T("允许断裂构件数"), Spin(o.MaxBrokenMembers, 0, 100, 1, v => o.MaxBrokenMembers = (int)v));
			Field(g, Loc.T("最多拆除旧构件（-1 不限）"), Spin(l.MaxDemolitions, -1, 100, 1, v => l.MaxDemolitions = (int)v));
			body.AddChild(g);

			body.AddChild(Section(Loc.T("地形与水")));
			g = Grid(4);
			var water = new CheckBox { Text = Loc.T("有水"), ButtonPressed = t.HasWater };
			water.Toggled += on => t.HasWater = on;
			Field(g, Loc.T("常水位 m"), Spin(t.WaterLevel, -100, 50, 0.5, v => t.WaterLevel = v));
			Field(g, Loc.T("河床标高 m"), Spin(t.RiverBedDepth, -200, 50, 0.5, v => t.RiverBedDepth = v));
			Field(g, Loc.T("最低建造标高 m"), Spin(t.BuildMinY, -300, 50, 1, v => t.BuildMinY = v));
			Field(g, Loc.T("最高建造标高 m"), Spin(t.BuildMaxY, -50, 300, 1, v => t.BuildMaxY = v));
			g.AddChild(water); g.AddChild(new Control());
			body.AddChild(g);
			body.AddChild(UiTheme.MakeLabel(Loc.F("河宽 {0:0} m、两岸高度、锚点、地层和保护区在“高级（JSON）”里改。", (t.RiverWidth)), 12, UiTheme.Muted));

			body.AddChild(Section(Loc.T("可用建材")));
			var mg = Grid(4);
			foreach (var m in db.Materials.Values)
			{
				var cb = new CheckBox { Text = m.Name, ButtonPressed = l.AllowedMaterials.Contains(m.MaterialId), TooltipText = m.MaterialId };
				string id = m.MaterialId;
				cb.Toggled += on => { l.AllowedMaterials.Remove(id); if (on) l.AllowedMaterials.Add(id); };
				mg.AddChild(cb);
			}
			body.AddChild(mg);

			body.AddChild(Section(Loc.T("车辆、船只与天气（时间 = 测试开始后秒数）")));
			var vehicles = db.Vehicles.Values.ToList();
			var presets = db.Weather.Presets.ToList();
			foreach (var e in l.TrafficEvents.ToList())
			{
				var r = new HBoxContainer();
				r.AddThemeConstantOverride("separation", 8);
				r.AddChild(Spin(e.Time, 0, 3600, 0.5, v => e.Time = v, " s"));
				switch (e.Type)
				{
					case TrafficEventType.Vehicle:
					case TrafficEventType.Train:
						var vo = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
						foreach (var v in vehicles) vo.AddItem(v.Name);
						vo.Selected = Math.Max(0, vehicles.FindIndex(v => v.Id == e.VehicleId));
						vo.ItemSelected += i => e.VehicleId = vehicles[(int)i].Id;
						r.AddChild(vo);
						r.AddChild(UiTheme.MakeLabel(Loc.T("车道"), 13, UiTheme.Muted));
						r.AddChild(Spin(e.Lane, 0, 3, 1, v => e.Lane = (int)v));
						break;
					case TrafficEventType.Environment:
						var wo = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
						wo.AddItem(Loc.T("洪水（旧式，按上涨高度）"));
						foreach (var p in presets) wo.AddItem(p.Value.Name);
						wo.Selected = e.Weather == null ? 0 : Math.Max(0, presets.FindIndex(p => p.Key == e.Weather) + 1);
						wo.ItemSelected += i => { e.Weather = i == 0 ? null : presets[(int)i - 1].Key; };
						r.AddChild(wo);
						r.AddChild(UiTheme.MakeLabel(Loc.T("水位上涨"), 13, UiTheme.Muted));
						r.AddChild(Spin(e.Weather == null ? e.Height : e.Rise ?? db.Weather.Preset(e.Weather).WaterRise, -20, 50, 0.5,
							v => { if (e.Weather == null) e.Height = v; else e.Rise = v; }, " m"));
						r.AddChild(UiTheme.MakeLabel(Loc.T("持续"), 13, UiTheme.Muted));
						r.AddChild(Spin(e.Duration, 0, 3600, 1, v => e.Duration = v, " s"));
						break;
					case TrafficEventType.Ship:
						r.AddChild(UiTheme.MakeLabel(e.Name, 13, UiTheme.Text));
						r.AddChild(UiTheme.MakeLabel(Loc.T("船高"), 13, UiTheme.Muted));
						r.AddChild(Spin(e.Height, 0.5, 60, 0.5, v => e.Height = v, " m"));
						r.AddChild(UiTheme.MakeLabel(Loc.T("航道 x"), 13, UiTheme.Muted));
						r.AddChild(Spin(e.LaneX, -50, 500, 1, v => e.LaneX = v, " m"));
						r.AddChild(UiTheme.MakeLabel(Loc.T("航速"), 13, UiTheme.Muted));
						r.AddChild(Spin(e.Speed, 0.5, 30, 0.5, v => e.Speed = v, " m/s"));
						break;
				}
				r.AddChild(UiTheme.MakeButton(Loc.T("删除"), () => { l.TrafficEvents.Remove(e); rebuild(); }, 60));
				body.AddChild(r);
			}
			var add = new HBoxContainer();
			add.AddThemeConstantOverride("separation", 8);
			double next = l.TrafficEvents.Select(x => x.Time).DefaultIfEmpty(-1).Max() + 1;
			add.AddChild(UiTheme.MakeButton(Loc.T("+ 车辆"), () =>
			{
				var v = vehicles.FirstOrDefault(x => !RailTrack.IsTrain(x)) ?? vehicles[0];
				l.TrafficEvents.Add(new TrafficEvent { Time = next, Type = RailTrack.IsTrain(v) ? TrafficEventType.Train : TrafficEventType.Vehicle, VehicleId = v.Id }); rebuild();
			}, 90));
			add.AddChild(UiTheme.MakeButton(Loc.T("+ 天气"), () =>
			{
				l.TrafficEvents.Add(new TrafficEvent { Time = Math.Max(0, next), Type = TrafficEventType.Environment, Name = Loc.T("暴雨"), Weather = "rainstorm", Duration = 10 }); rebuild();
			}, 90));
			add.AddChild(UiTheme.MakeButton(Loc.T("+ 船"), () =>
			{
				l.TrafficEvents.Add(new TrafficEvent { Time = Math.Max(0, next), Type = TrafficEventType.Ship, LaneX = t.RiverWidth / 2 }); rebuild();
			}, 70));
			body.AddChild(add);

			body.AddChild(Section(Loc.T("高级（JSON）：本关全部设置")));
			var json = new TextEdit { Text = GameDatabase.SerializeLevel(l), CustomMinimumSize = new Vector2(0, 260), WrapMode = TextEdit.LineWrappingMode.None };
			body.AddChild(json);
			body.AddChild(UiTheme.MakeButton(Loc.T("应用 JSON 到上面的表单"), () =>
			{
				try
				{
					var parsed = GameDatabase.DeserializeLevel(json.Text) ?? throw new Exception(Loc.T("内容为空"));
					parsed.Id = l.Id;
					string err = db.LevelError(parsed);
					if (err != null) throw new Exception(err);
					CopyInto(parsed, l);
					status.Text = Loc.T("✔ 已应用 JSON（还没保存）"); status.AddThemeColorOverride("font_color", UiTheme.Good);
					rebuild();
				}
				catch (Exception ex) { status.Text = "✘ " + ex.Message; status.AddThemeColorOverride("font_color", UiTheme.Bad); }
			}, 220));
		}

		/// <summary>Copy every property of src into dst (dst keeps its identity so the form callbacks stay valid).</summary>
		private static void CopyInto(LevelDefinition src, LevelDefinition dst)
		{
			foreach (var p in typeof(LevelDefinition).GetProperties().Where(p => p.CanRead && p.CanWrite))
				p.SetValue(dst, p.GetValue(src));
		}
	}
}
