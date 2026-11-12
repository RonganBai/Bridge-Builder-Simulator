using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>Side-view sketch of a custom level: banks, river bed, water, pillars, no-pier zone, ship, fixed nodes.</summary>
	public partial class LevelPreview : Control
	{
		public LevelDefinition Level { get; set; }

		public override void _Draw()
		{
			DrawRect(new Rect2(Vector2.Zero, Size), UiTheme.Bg);
			var lv = Level;
			if (lv == null) return;
			var t = lv.Terrain;
			double w = t.RiverWidth, x0 = -8, x1 = w + 8, y0 = t.RiverBedDepth - 1.5, y1 = System.Math.Max(t.LeftBankHeight, t.RightBankHeight) + 7;
			foreach (var e in lv.TrafficEvents) if (e.Type == TrafficEventType.Ship) y1 = System.Math.Max(y1, t.WaterLevel + e.Height + 2);
			float sc = (float)System.Math.Min((Size.X - 20) / (x1 - x0), (Size.Y - 20) / (y1 - y0));
			float ox = (Size.X - (float)(x1 - x0) * sc) / 2, oy = (Size.Y - (float)(y1 - y0) * sc) / 2;
			Vector2 P(double x, double y) => new(ox + (float)(x - x0) * sc, Size.Y - oy - (float)(y - y0) * sc);
			Rect2 R(double xa, double ya, double xb, double yb) { var a = P(xa, yb); var b = P(xb, ya); return new Rect2(a, b - a); }
			var soil = new Color(0.45f, 0.35f, 0.24f); var road = new Color(0.25f, 0.25f, 0.28f);
			if (t.HasWater && t.WaterLevel > t.RiverBedDepth) DrawRect(R(0, t.RiverBedDepth, w, t.WaterLevel), new Color(0.2f, 0.45f, 0.75f, 0.75f));
			DrawRect(R(x0, y0, x1, t.RiverBedDepth), t.DefaultSoil == "soft" ? new Color(0.55f, 0.42f, 0.27f) : new Color(0.44f, 0.46f, 0.5f));
			double run = t.WallRun;
			if (run > 0)
			{
				double bd = t.RiverBedDepth;
				DrawColoredPolygon(new[] { P(0, t.LeftBankHeight), P(run * (t.LeftBankHeight - bd), bd), P(0, bd) }, soil);
				DrawColoredPolygon(new[] { P(w, t.RightBankHeight), P(w, bd), P(w - run * (t.RightBankHeight - bd), bd) }, soil);
			}
			DrawRect(R(x0, y0, 0, t.LeftBankHeight), soil);
			DrawRect(R(w, y0, x1, t.RightBankHeight), soil);
			DrawRect(R(x0, t.LeftBankHeight - 0.15, 0, t.LeftBankHeight + 0.15), road);
			DrawRect(R(w, t.RightBankHeight - 0.15, x1, t.RightBankHeight + 0.15), road);
			foreach (var z in t.NoBuildZones) DrawRect(R(z.X0, t.RiverBedDepth, z.X1, z.BelowY), new Color(0.3f, 0.85f, 0.4f, 0.18f));
			foreach (var p in t.Pillars) DrawRect(R(p.X - p.Width / 2, t.RiverBedDepth, p.X + p.Width / 2, p.TopY), new Color(0.5f, 0.52f, 0.56f));
			foreach (var e in lv.TrafficEvents)
				if (e.Type == TrafficEventType.Ship)
					DrawRect(R(e.LaneX - e.Width / 2, t.WaterLevel, e.LaneX + e.Width / 2, t.WaterLevel + e.Height), new Color(1f, 0.6f, 0.2f), false, 2);
			var font = ThemeDB.FallbackFont;
			foreach (var a in lv.Anchors)
			{
				var c = a.Id.StartsWith("C") ? new Color(1f, 0.35f, 0.3f) : a.IsRoadStart || a.IsRoadEnd ? new Color(1f, 0.75f, 0.3f) : new Color(1f, 0.9f, 0.6f);
				var p = P(a.Position.X, a.Position.Y);
				DrawCircle(p, 5, c);
				DrawString(font, p + new Vector2(6, -6), a.Id, HorizontalAlignment.Left, -1, 12, c);
			}
			DrawString(font, P(w / 2, y1 - 1) - new Vector2(40, 0), $"河宽 {w:0.#} m", HorizontalAlignment.Left, -1, 13, Colors.White);
		}
	}
}
