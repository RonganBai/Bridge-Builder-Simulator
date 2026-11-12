using System;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// Vehicle height clearance above the road. The bridge is built as two side trusses with the road between
    /// them; a cross beam (tie between the two truss planes) is only placed where it cannot hit traffic:
    /// at or below the deck (floor beams) or higher than the clearance above the road (overhead bracing).
    /// Nodes in the traffic envelope get no cross beam, and side-only materials (cables, hydraulics) never need one.
    /// </summary>
    public static class HeightClearance
    {
        public const double Default = 4.5;      // m, standard road height limit
        public const double Margin = 0.3;       // m above the tallest vehicle of the level

        /// <summary>Height limit of a level: 4.5 m, or more when a vehicle of the level is taller.</summary>
        public static double Limit(LevelDefinition level, GameDatabase db)
        {
            double tallest = 0;
            if (level != null && db != null)
                foreach (var e in level.TrafficEvents)
                    if (e.Type == TrafficEventType.Vehicle && e.VehicleId != null && db.Vehicles.TryGetValue(e.VehicleId, out var v))
                        tallest = Math.Max(tallest, v.Height);
            return Math.Max(Default, tallest + Margin);
        }

        /// <summary>Road surface height under x: the highest deck member below y that spans x (null = no deck).</summary>
        public static double? DeckYAt(BridgeDesign d, GameDatabase db, double x, double belowY = double.PositiveInfinity)
        {
            double? best = null;
            foreach (var m in d.Members)
            {
                if (db.GetMaterial(m.MaterialId)?.IsDeck != true) continue;
                var a = d.FindNode(m.StartNodeId); var b = d.FindNode(m.EndNodeId);
                if (a == null || b == null) continue;
                double x0 = Math.Min(a.Position.X, b.Position.X), x1 = Math.Max(a.Position.X, b.Position.X);
                if (x < x0 - 1e-6 || x > x1 + 1e-6 || x1 - x0 < 1e-9) continue;
                double t = (x - a.Position.X) / (b.Position.X - a.Position.X);
                double y = a.Position.Y + (b.Position.Y - a.Position.Y) * t;
                if (y <= belowY + 1e-6 && (best == null || y > best)) best = y;
            }
            return best;
        }

        /// <summary>Height of a node above the road surface under it (null when it is not above a deck).</summary>
        public static double? HeightAboveDeck(BridgeDesign d, GameDatabase db, BridgeNode n)
        {
            if (d.Members.Any(m => (m.StartNodeId == n.Id || m.EndNodeId == n.Id) && db.GetMaterial(m.MaterialId)?.IsDeck == true)) return null;
            var y = DeckYAt(d, db, n.Position.X, n.Position.Y);
            if (y == null || n.Position.Y <= y.Value + 1e-6) return null;
            return n.Position.Y - y.Value;
        }

        /// <summary>Does this node get a cross beam between the two truss planes?</summary>
        public static bool NeedsCrossTie(BridgeDesign d, GameDatabase db, LevelDefinition level, BridgeNode n)
        {
            var mats = d.Members.Where(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id).Select(m => db.GetMaterial(m.MaterialId)).Where(m => m != null).ToList();
            if (mats.Count > 0 && mats.All(m => m.SideOnly)) return false;      // cables / hydraulics need no cross beam
            var h = HeightAboveDeck(d, db, n);
            return h == null || h.Value >= Limit(level, db);
        }
    }
}
