using System.Linq;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// Columns (ComponentType Column, piers excluded) instead get one cross bar every 10 m measured up from
    /// their lower end (a column of 10 m or less has none).
    /// Member cross beams: a building member at least 5 m long automatically gets a cross beam at its midpoint
    /// that ties the two truss planes together. It adds some tension capacity and is a solid body: a vehicle
    /// whose height reaches it above the road hits it. Shorter members, decks, piers and side-only (cable /
    /// hydraulic) members get none.
    /// </summary>
    public static class CrossBeams
    {
        public const double MinLength = 5.0;        // m
        public const double TensionBonus = 0.10;    // +10% tension capacity

        public const double ColumnSpacing = 10.0;   // m between column cross bars

        private static bool Eligible(MaterialDefinition mat) =>
            mat != null && !mat.IsDeck && !mat.IsPier && !mat.SideOnly && !mat.IsCable && !mat.Extendable;

        /// <summary>Number of cross beams on a member of this material and length.</summary>
        public static int Count(MaterialDefinition mat, double length)
        {
            if (!Eligible(mat)) return 0;
            if (mat.IsColumn) return System.Math.Max(0, (int)System.Math.Ceiling(length / ColumnSpacing - 1e-9) - 1);
            return length >= MinLength - 1e-9 ? 1 : 0;
        }

        public static bool Has(MaterialDefinition mat, double length) => Count(mat, length) > 0;

        /// <summary>
        /// Cross beam positions as fractions from the start node to the end node. Columns count 10 m steps up
        /// from their lower end (yStart / yEnd are the end heights); other members have one at the midpoint.
        /// </summary>
        public static System.Collections.Generic.List<double> Fractions(MaterialDefinition mat, double length, double yStart, double yEnd)
        {
            var list = new System.Collections.Generic.List<double>();
            int n = Count(mat, length);
            if (n == 0) return list;
            if (!mat.IsColumn) { list.Add(0.5); return list; }
            for (int k = 1; k <= n; k++)
            {
                double t = k * ColumnSpacing / length;
                list.Add(yStart <= yEnd ? t : 1 - t);
            }
            return list;
        }

        /// <summary>Short player-facing rule text.</summary>
        public static string RuleText(MaterialDefinition mat) => mat != null && mat.IsColumn
            ? $"柱每 {ColumnSpacing:0} m 自动加一根横杆" : $"≥ {MinLength:0} m 自动在中点加横梁";

        public static double TensionFactor(MaterialDefinition mat, double length) => Has(mat, length) ? 1 + TensionBonus : 1;

        /// <summary>Is the cross beam of this member above the road and lower than the given vehicle height?</summary>
        public static bool Blocks(BridgeDesign d, GameDatabase db, BridgeMember m, double vehicleHeight)
        {
            var mat = db.GetMaterial(m.MaterialId);
            if (!Has(mat, m.Length)) return false;
            var na = d.FindNode(m.StartNodeId); var nb = d.FindNode(m.EndNodeId);
            if (na == null || nb == null) return false;
            foreach (double t in Fractions(mat, m.Length, na.Position.Y, nb.Position.Y))
            {
                double mx = na.Position.X + (nb.Position.X - na.Position.X) * t, my = na.Position.Y + (nb.Position.Y - na.Position.Y) * t;
                var y = HeightClearance.DeckYAt(d, db, mx, my);
                if (y == null) continue;
                double h = my - y.Value;
                if (h > 0.05 && h < vehicleHeight) return true;
            }
            return false;
        }

        /// <summary>
        /// Old-bridge members (Existing) cannot be moved and only a few may be demolished, so an existing member
        /// whose cross beam would block traffic is treated as two halves joined rigidly at its midpoint (a braced
        /// old member): no cross beam, same stiffness and strength as the single bar. Applied in memory when a
        /// design is opened; save files are not rewritten. Returns how many members were split.
        /// </summary>
        public static int SplitExistingBlocking(BridgeDesign d, LevelDefinition level, GameDatabase db)
        {
            if (level == null || d == null) return 0;
            double tallest = level.TrafficEvents
                .Where(e => e.Type == TrafficEventType.Vehicle && e.VehicleId != null && db.Vehicles.ContainsKey(e.VehicleId))
                .Select(e => db.Vehicles[e.VehicleId].Height).DefaultIfEmpty(0).Max();
            int count = 0;
            for (int guard = 0; guard < 256; guard++)
            {
                var m = d.Members.FirstOrDefault(x => x.Existing && Blocks(d, db, x, tallest));
                if (m == null) break;
                var a = d.FindNode(m.StartNodeId); var b = d.FindNode(m.EndNodeId);
                var n = new BridgeNode { Id = d.NextNodeId++, Position = new Vec3d((a.Position.X + b.Position.X) / 2, (a.Position.Y + b.Position.Y) / 2, 0),
                    RigidEndA = a.Id, RigidEndB = b.Id, RigidFraction = 0.5 };
                d.Nodes.Add(n);
                var m2 = m.Clone(); m2.Id = d.NextMemberId++; m2.StartNodeId = n.Id; m2.Length = m.Length / 2;
                m.EndNodeId = n.Id; m.Length /= 2;
                d.Members.Add(m2);
                count++;
            }
            return count;
        }
    }
}
