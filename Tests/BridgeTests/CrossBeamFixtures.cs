using System.Linq;
using BridgeArchitect3D.Data;

/// <summary>
/// Old fixtures were drawn before member cross beams existed. A member >= 5 m whose cross beam sits inside the
/// vehicle height envelope is split at its midpoint (two shorter members, no cross beam), the same change the
/// reference designs received. The fixture files themselves stay untouched.
/// </summary>
internal static class CrossBeamFixtures
{
    public static BridgeDesign SplitBlocking(BridgeDesign d, LevelDefinition level, GameDatabase db)
    {
        double tallest = level.TrafficEvents
            .Where(e => e.Type == TrafficEventType.Vehicle && e.VehicleId != null && db.Vehicles.ContainsKey(e.VehicleId))
            .Select(e => db.Vehicles[e.VehicleId].Height).DefaultIfEmpty(0).Max();
        for (int guard = 0; guard < 64; guard++)
        {
            var m = d.Members.FirstOrDefault(x => Blocks(d, db, x, tallest));
            if (m == null) break;
            var mat = db.GetMaterial(m.MaterialId);
            var a = d.FindNode(m.StartNodeId).Position; var b = d.FindNode(m.EndNodeId).Position;
            var n = new BridgeNode { Id = d.NextNodeId++, Position = new Vec3d((a.X + b.X) / 2, (a.Y + b.Y) / 2, 0), RigidEndA = m.StartNodeId, RigidEndB = m.EndNodeId, RigidFraction = 0.5 };
            d.Nodes.Add(n);
            double half = m.Length / 2;
            var m2 = m.Clone(); m2.Id = d.NextMemberId++; m2.StartNodeId = n.Id; m2.Length = half; m2.Cost = m.Existing ? 0 : half * mat.CostPerMeter;
            m.EndNodeId = n.Id; m.Length = half; m.Cost = m.Existing ? 0 : half * mat.CostPerMeter;
            d.Members.Add(m2);
        }
        return d;
    }

    public static bool Blocks(BridgeDesign d, GameDatabase db, BridgeMember m, double tallest)
    {
        return CrossBeams.Blocks(d, db, m, tallest + 0.3);
    }
}
