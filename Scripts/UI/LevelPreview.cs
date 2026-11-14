using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>
	/// Side view of a level that is edited in place: drag the round handles to change the banks (height, river width),
	/// the bank slope, the river bed, the water level, the build limits, fixed nodes and ships (lane and draft).
	/// Double-click empty space to add a fixed node, right-click a node or ship to remove it.
	/// All edits go through <see cref="LevelEditing"/>, so the map and the forms keep the same rules.
	/// </summary>
	public partial class LevelPreview : Control
	{
		private enum H { None, LeftBank, RightBank, Width, Slope, Bed, Water, BuildMin, BuildMax, Anchor, Ship, ShipDraft }

		public LevelDefinition Level { get; set; }
		/// <summary>Raised during a drag (values changed).</summary>
		public event Action Edited;
		/// <summary>Raised when nodes or ships were added / removed, or the selection changed.</summary>
		public event Action SelectionChanged;
		/// <summary>The selected <see cref="AnchorDefinition"/> or ship <see cref="TrafficEvent"/> (null = none).</summary>
		public object Selection { get; set; }

		private H _hover, _drag;
		private object _hoverObj, _dragObj;
		private bool _frozen;
		private double _x0, _x1, _y0, _y1; private float _sc, _ox, _oy;
		private string _readout = "";

		private static readonly Color Handle = new(0.57f, 0.73f, 0.92f), HandleHot = Colors.White;
		private static readonly Color NodeColor = new(1f, 0.82f, 0.4f), PillarNode = new(1f, 0.45f, 0.35f);

		public LevelPreview() { MouseFilter = MouseFilterEnum.Stop; FocusMode = FocusModeEnum.Click; ClipContents = true; }

		// ---------------- view ----------------

		private void UpdateView()
		{
			if (_frozen || Level == null) return;
			var t = Level.Terrain; double w = t.RiverWidth, m = Math.Max(8, w * 0.12);
			double top = Math.Max(t.LeftBankHeight, t.RightBankHeight);
			_x0 = -m; _x1 = w + m;
			_y0 = Math.Min(t.RiverBedDepth - 1.5, Math.Max(t.BuildMinY, t.RiverBedDepth - 12) - 1.5);
			_y1 = Math.Max(top + 4, Math.Min(t.BuildMaxY, top + 20) + 1.5);
			foreach (var e in Ships) _y1 = Math.Max(_y1, t.WaterLevel + e.Height + 1.5);
			foreach (var a in Level.Anchors) { _y1 = Math.Max(_y1, a.Position.Y + 2); _y0 = Math.Min(_y0, a.Position.Y - 2); }
			_sc = (float)Math.Min((Size.X - 24) / (_x1 - _x0), (Size.Y - 24) / (_y1 - _y0));
			_ox = (Size.X - (float)(_x1 - _x0) * _sc) / 2; _oy = (Size.Y - (float)(_y1 - _y0) * _sc) / 2;
		}

		private Vector2 P(double x, double y) => new(_ox + (float)(x - _x0) * _sc, Size.Y - _oy - (float)(y - _y0) * _sc);
		private (double X, double Y) W(Vector2 p) => (_x0 + (p.X - _ox) / _sc, _y0 + (Size.Y - _oy - p.Y) / _sc);
		private Rect2 R(double xa, double ya, double xb, double yb) { var a = P(xa, yb); var b = P(xb, ya); return new Rect2(a, b - a); }
		private IEnumerable<TrafficEvent> Ships => Level.TrafficEvents.Where(e => e.Type == TrafficEventType.Ship);
		private static double Snap(double v, double step) => Math.Round(v / step) * step;

		// ---------------- handles ----------------

		private IEnumerable<(H kind, object obj, Vector2 pos)> Handles()
		{
			var t = Level.Terrain; double w = t.RiverWidth;
			yield return (H.BuildMax, null, P(_x1 - (_x1 - _x0) * 0.04, ClampY(t.BuildMaxY)));
			yield return (H.BuildMin, null, P(_x1 - (_x1 - _x0) * 0.04, ClampY(t.BuildMinY)));
			yield return (H.LeftBank, null, P(_x0 * 0.5, t.LeftBankHeight));
			yield return (H.RightBank, null, P(w + (_x1 - w) * 0.5, t.RightBankHeight));
			yield return (H.Width, null, P(w - t.WallRun * (t.RightBankHeight - t.RiverBedDepth), t.RiverBedDepth));
			yield return (H.Slope, null, P(t.WallRun * (t.LeftBankHeight - t.RiverBedDepth), t.RiverBedDepth));
			yield return (H.Bed, null, P(w * 0.5, t.RiverBedDepth));
			if (t.HasWater) yield return (H.Water, null, P(w * 0.2, t.WaterLevel));
			foreach (var e in Ships)
			{
				yield return (H.ShipDraft, e, P(e.LaneX, t.WaterLevel - ShipCatalog.Draft(e)));
				yield return (H.Ship, e, P(e.LaneX, t.WaterLevel + e.Height * 0.55));
			}
			foreach (var a in Level.Anchors) yield return (H.Anchor, a, P(a.Position.X, a.Position.Y));
		}

		private double ClampY(double y) => Math.Clamp(y, _y0 + 0.3, _y1 - 0.3);

		private (H, object) HitTest(Vector2 m)
		{
			// Nodes first (drawn on top), then other handles, then ship hulls
			(H, object) best = (H.None, null); float bestD = 11;
			foreach (var (k, o, p) in Handles().Reverse())
			{
				float d = p.DistanceTo(m);
				if (k == H.Anchor) d -= 3;
				if (d < bestD) { best = (k, o); bestD = d; }
			}
			if (best.Item1 != H.None) return best;
			var t = Level.Terrain;
			foreach (var e in Ships)
				if (R(e.LaneX - e.Width / 2, t.WaterLevel - ShipCatalog.Draft(e), e.LaneX + e.Width / 2, t.WaterLevel + e.Height).HasPoint(m)) return (H.Ship, e);
			return best;
		}

		public override void _GuiInput(InputEvent ev)
		{
			if (Level == null) return;
			if (ev is InputEventMouseMotion mm)
			{
				if (_drag != H.None) { DragTo(mm.Position); AcceptEvent(); return; }
				var (k, o) = HitTest(mm.Position);
				if (k != _hover || o != _hoverObj) { _hover = k; _hoverObj = o; _readout = Describe(k, o); QueueRedraw(); }
				MouseDefaultCursorShape = k switch
				{
					H.None => CursorShape.Arrow,
					H.LeftBank or H.Bed or H.Water or H.BuildMin or H.BuildMax or H.ShipDraft => CursorShape.Vsize,
					H.Slope or H.Width => CursorShape.Hsize,
					_ => CursorShape.Move
				};
			}
			else if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
			{
				var (k, o) = HitTest(mb.Position);
				if (mb.DoubleClick && k == H.None) { AddNodeAt(mb.Position); AcceptEvent(); return; }
				Selection = k is H.Anchor or H.Ship or H.ShipDraft ? o : null;
				SelectionChanged?.Invoke();
				if (k != H.None) { _drag = k; _dragObj = o; _frozen = true; }
				QueueRedraw(); AcceptEvent();
			}
			else if (ev is InputEventMouseButton up && !up.Pressed && up.ButtonIndex == MouseButton.Left && _drag != H.None)
			{
				_drag = H.None; _dragObj = null; _frozen = false;
				UpdateView(); QueueRedraw(); Edited?.Invoke(); AcceptEvent();
			}
			else if (ev is InputEventMouseButton rb && rb.Pressed && rb.ButtonIndex == MouseButton.Right)
			{
				var (k, o) = HitTest(rb.Position);
				bool removed = o is AnchorDefinition a ? LevelEditing.RemoveAnchor(Level, a) : o is TrafficEvent s && Level.TrafficEvents.Remove(s);
				if (removed) { if (Selection == o) Selection = null; SelectionChanged?.Invoke(); Edited?.Invoke(); UpdateView(); QueueRedraw(); }
				AcceptEvent();
			}
		}

		private void AddNodeAt(Vector2 m)
		{
			var (x, y) = W(m);
			var a = LevelEditing.AddAnchor(Level, SnapNode(x, y));
			Selection = a; SelectionChanged?.Invoke(); Edited?.Invoke(); UpdateView(); QueueRedraw();
		}

		/// <summary>Grid 0.25 m; sticks to the bank top / bank wall / river bed when within 0.6 m.</summary>
		private Vec3d SnapNode(double x, double y)
		{
			var t = Level.Terrain;
			x = Snap(x, 0.25); y = Snap(y, 0.25);
			double ground = x <= 0 ? t.LeftBankHeight : x >= t.RiverWidth ? t.RightBankHeight : t.GroundYInRiver(x);
			if (Math.Abs(y - ground) < 0.6 || y < ground)
			{
				// On a sloped wall, keep the node on the wall line rather than buried in it
				y = Math.Round(ground, 2);
			}
			return new Vec3d(Math.Round(x, 2), y, 0);
		}

		private void DragTo(Vector2 m)
		{
			var l = Level; var t = l.Terrain; var (x, y) = W(m);
			switch (_drag)
			{
				case H.LeftBank: LevelEditing.SetBankHeight(l, true, Math.Clamp(Snap(y, 0.25), -20, 40)); break;
				case H.RightBank: LevelEditing.SetBankHeight(l, false, Math.Clamp(Snap(y, 0.25), -20, 40)); break;
				case H.Width: LevelEditing.SetRiverWidth(l, Math.Clamp(Snap(x + t.WallRun * (t.RightBankHeight - t.RiverBedDepth), 0.5), LevelEditing.MinRiverWidth, LevelEditing.MaxRiverWidth)); break;
				case H.Slope:
				{
					double drop = t.LeftBankHeight - t.RiverBedDepth, run = Math.Max(0, x);
					LevelEditing.SetBankSlope(l, Math.Round(run < 0.05 ? 90 : Math.Atan2(drop, run) * 180 / Math.PI));
					break;
				}
				case H.Bed: LevelEditing.SetRiverDepth(l, Snap(LevelEditing.LowBank(l) - y, 0.25)); break;
				case H.Water: LevelEditing.SetWaterDepth(l, Snap(y - t.RiverBedDepth, 0.25)); break;
				case H.BuildMin: LevelEditing.SetBuildDepth(l, Math.Round(t.RiverBedDepth - y)); break;
				case H.BuildMax: LevelEditing.SetBuildHeight(l, Math.Round(y - Math.Max(t.LeftBankHeight, t.RightBankHeight))); break;
				case H.Anchor: LevelEditing.MoveAnchor(l, (AnchorDefinition)_dragObj, SnapNode(x, y)); break;
				case H.Ship: LevelEditing.SetShipLane(l, (TrafficEvent)_dragObj, Snap(x, 0.25)); break;
				case H.ShipDraft:
				{
					var e = (TrafficEvent)_dragObj;
					LevelEditing.SetShipDraft(e, Snap(t.WaterLevel - y, 0.05));
					LevelEditing.SetShipLane(l, e, e.LaneX);
					break;
				}
			}
			_readout = Describe(_drag, _dragObj);
			QueueRedraw();
			Edited?.Invoke();
		}

		private string Describe(H k, object o)
		{
			var l = Level; var t = l.Terrain;
			return k switch
			{
				H.LeftBank => Loc.F("左岸高度 {0:0.##} m", t.LeftBankHeight),
				H.RightBank => Loc.F("右岸高度 {0:0.##} m", t.RightBankHeight),
				H.Width => Loc.F("河宽 {0:0.#} m", t.RiverWidth),
				H.Slope => Loc.F("岸壁坡度 {0:0}°", t.BankSlopeDeg),
				H.Bed => Loc.F("河道深度 {0:0.##} m", LevelEditing.RiverDepth(l)),
				H.Water => Loc.F("水深 {0:0.##} m", LevelEditing.WaterDepth(l)),
				H.BuildMin => Loc.F("可建深度：河床以下 {0:0} m", LevelEditing.BuildDepth(l)),
				H.BuildMax => Loc.F("可建高度：岸顶以上 {0:0} m", LevelEditing.BuildHeight(l)),
				H.Anchor when o is AnchorDefinition a => Loc.F("节点 {0}  x {1:0.##} m · y {2:0.##} m", a.Id, a.Position.X, a.Position.Y),
				H.Ship when o is TrafficEvent e => Loc.F("{0}  航道 x {1:0.##} m", e.Name, e.LaneX),
				H.ShipDraft when o is TrafficEvent e => Loc.F("{0}  吃水 {1:0.##} m（船高的 {2:0}%）", e.Name, ShipCatalog.Draft(e), ShipCatalog.Draft(e) / e.Height * 100),
				_ => ""
			};
		}

		// ---------------- drawing ----------------

		public override void _Draw()
		{
			DrawRect(new Rect2(Vector2.Zero, Size), UiTheme.Bg);
			if (Level == null) return;
			UpdateView();
			var t = Level.Terrain; double w = t.RiverWidth, bd = t.RiverBedDepth, run = t.WallRun;
			var font = ThemeDB.FallbackFont;

			var soil = new Color(0.45f, 0.35f, 0.24f); var road = new Color(0.25f, 0.25f, 0.28f);
			var grid = new Color(1, 1, 1, 0.04f);
			for (double gx = Math.Ceiling(_x0 / 5) * 5; gx <= _x1; gx += 5) DrawLine(P(gx, _y0), P(gx, _y1), grid);
			for (double gy = Math.Ceiling(_y0 / 5) * 5; gy <= _y1; gy += 5) DrawLine(P(_x0, gy), P(_x1, gy), grid);

			if (t.HasWater && t.WaterLevel > bd) DrawRect(R(0, bd, w, t.WaterLevel), new Color(0.2f, 0.45f, 0.75f, 0.7f));
			DrawRect(R(_x0, _y0 - 5, _x1, bd), t.DefaultSoil == "soft" ? new Color(0.55f, 0.42f, 0.27f) : new Color(0.44f, 0.46f, 0.5f));
			if (run > 0)
			{
				DrawColoredPolygon(new[] { P(0, t.LeftBankHeight), P(run * (t.LeftBankHeight - bd), bd), P(0, bd) }, soil);
				DrawColoredPolygon(new[] { P(w, t.RightBankHeight), P(w, bd), P(w - run * (t.RightBankHeight - bd), bd) }, soil);
			}
			DrawRect(R(_x0, _y0 - 5, 0, t.LeftBankHeight), soil);
			DrawRect(R(w, _y0 - 5, _x1, t.RightBankHeight), soil);
			DrawRect(R(_x0, t.LeftBankHeight - 0.15, 0, t.LeftBankHeight + 0.15), road);
			DrawRect(R(w, t.RightBankHeight - 0.15, _x1, t.RightBankHeight + 0.15), road);
			foreach (var z in t.NoBuildZones) DrawRect(R(z.X0, bd, z.X1, z.BelowY), new Color(0.3f, 0.85f, 0.4f, 0.16f));
			if (LevelEditing.NoPiers(Level)) DrawString(font, P(w / 2, bd) + new Vector2(-30, -6), Loc.T("禁止建墩"), HorizontalAlignment.Left, -1, 12, new Color(0.5f, 0.95f, 0.6f));
			foreach (var p in t.Pillars) DrawRect(R(p.X - p.Width / 2, bd, p.X + p.Width / 2, p.TopY), new Color(0.5f, 0.52f, 0.56f));

            if(Level.ProvidedInfrastructure?.Tunnel is {} route)
            {
                var nodes=route.PointNodeIds.Select(Level.ProvidedInfrastructure.FindNode).Where(n=>n!=null).ToArray();
                for(int i=0;i+1<nodes.Length;i++)DrawLine(P(nodes[i].Position.X,nodes[i].Position.Y),P(nodes[i+1].Position.X,nodes[i+1].Position.Y),new Color("45bed1"),5);
            }

			// Build limits: dashed lines (clamped to the view; the label gives the true value)
			var lim = new Color(1f, 0.6f, 0.25f, 0.55f);
			foreach (var (y, k) in new[] { (t.BuildMaxY, H.BuildMax), (t.BuildMinY, H.BuildMin) })
			{
				double cy = ClampY(y);
				for (double dx = _x0; dx < _x1; dx += 2) DrawLine(P(dx, cy), P(Math.Min(_x1, dx + 1), cy), lim, 1);
				string txt = k == H.BuildMax ? Loc.F("可建高度 +{0:0} m", LevelEditing.BuildHeight(Level)) : Loc.F("可建深度 −{0:0} m", LevelEditing.BuildDepth(Level));
				DrawString(font, P(_x0, cy) + new Vector2(6, k == H.BuildMax ? 14 : -5), txt, HorizontalAlignment.Left, -1, 11, lim);
			}

			foreach (var e in Ships)
			{
				double draft = ShipCatalog.Draft(e), hull = Math.Max(ShipCatalog.HullHeight(e), 0.3);
				bool sel = Selection == e;
				var c = new Color(1f, 0.6f, 0.2f);
				DrawRect(R(e.LaneX - e.Width / 2, t.WaterLevel - draft, e.LaneX + e.Width / 2, t.WaterLevel + hull), new Color(c, 0.55f));
				DrawRect(R(e.LaneX - e.Width / 2, t.WaterLevel - draft, e.LaneX + e.Width / 2, t.WaterLevel + e.Height), c, false, sel ? 2.5f : 1.5f);
				DrawString(font, P(e.LaneX - e.Width / 2, t.WaterLevel + e.Height) + new Vector2(0, -4), e.Name, HorizontalAlignment.Left, -1, 11, c);
			}

			foreach (var (k, o, pos) in Handles())
			{
				bool hot = (k == _hover && o == _hoverObj) || (k == _drag && o == _dragObj);
				if (k == H.Anchor)
				{
					var a = (AnchorDefinition)o;
					var c = LevelEditing.NeedsPillar(Level, a.Position) ? PillarNode : NodeColor;
					bool sel = Selection == o;
					if (sel) DrawArc(pos, 10, 0, Mathf.Tau, 24, Colors.White, 2);
					DrawCircle(pos, hot ? 7 : 5.5f, c);
					DrawString(font, pos + new Vector2(8, -7), a.Id, HorizontalAlignment.Left, -1, 12, c);
				}
				else
				{
					DrawCircle(pos, hot ? 7.5f : 6, UiTheme.Bg);
					DrawArc(pos, hot ? 7.5f : 6, 0, Mathf.Tau, 24, hot ? HandleHot : (k is H.BuildMin or H.BuildMax ? lim : Handle), 2);
					if (k != H.Ship) DrawArrows(pos, k is H.Slope or H.Width, hot ? HandleHot : Handle);
				}
			}

			DrawString(font, new Vector2(12, 20), _readout.Length > 0 ? _readout : Loc.T("拖动圆点编辑地图 · 双击空白处添加固定节点 · 右键删除节点 / 船"),
				HorizontalAlignment.Left, -1, 13, _readout.Length > 0 ? Colors.White : UiTheme.Muted);
		}

		/// <summary>Small arrows that show which way a handle moves.</summary>
		private void DrawArrows(Vector2 p, bool horizontal, Color c)
		{
			var d = horizontal ? new Vector2(1, 0) : new Vector2(0, 1); var n = new Vector2(d.Y, d.X);
			foreach (float s in new[] { -1f, 1f })
			{
				var tip = p + d * s * 13; var b = p + d * s * 9;
				DrawColoredPolygon(new[] { tip, b + n * 3, b - n * 3 }, c);
			}
		}
	}
}
