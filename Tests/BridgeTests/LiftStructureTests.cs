using System;
using System.Linq;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

/// <summary>
/// Bank lifts built from ordinary members in each shaft (platform deck / truss, hoist ropes, shaft columns), solved
/// with the ordinary frame solver. Capacity follows the members the player builds, not a template value.
/// </summary>
internal static class LiftStructureTests
{
    private static DesignEditor Fresh(GameDatabase db, string template = null)
    {
        var lv = db.GetLevel("level13");
        var e = new DesignEditor(null, lv, db);
        e.AddTunnel(0, lv.Terrain.RiverWidth, "tunnel_immersed");
        if (template != null) e.RebuildLifts(template);
        return e;
    }

    public static void Sweep(GameDatabase db)
    {
        foreach (var lvId in new[] { "level13", "level14", "level15" })
        foreach (var id in LiftStructure.Cfg(db).Templates.Keys)
        {
            var lv = db.GetLevel(lvId); var e = new DesignEditor(null, lv, db);
            e.AddTunnel(0, lv.Terrain.RiverWidth, lv.AllowedMaterials.First(m => db.GetMaterial(m)?.IsTunnel == true)); e.RebuildLifts(id);
            double u = e.LiftWorstUtil(out var w);
            Console.WriteLine($"{lvId} {id}: cost ${e.UsedCost:0} members {e.Lifts().Sum(L => L.Members.Count)} worst {u:0.00} {w?.Governing} {w?.Where} {w?.Why}");
        }
    }

    public static void Run(GameDatabase db, Action<string, bool, string> check)
    {
        var e = Fresh(db, "standard");
        var lifts = e.Lifts();
        check("lift: a new tunnel gets lift members in both shafts", lifts.Length == 2 && lifts.All(L => L.Problem == null && L.Members.Count > 4 && L.Hoists.Count == 2 && L.DeckPath.Count > 0),
            string.Join(" | ", lifts.Select(L => L.Problem ?? L.Members.Count.ToString())));
        check("lift: four fixed nodes per bank (two platform ends, two pit bases)", Enumerable.Range(1, 2).All(b => e.Design.Nodes.Count(n => n.LiftBank == b && n.IsLiftFixed) == 4), "");
        var L0 = lifts[0]; var s = L0.Shaft;
        const double W = 300e3;

        // Off-centre load: the same axle pair near one end pulls that end's hoist harder than a centred pair
        double mid = (s.X0 + s.X1) / 2;
        var centred = LiftStructure.Check(e.Design, e.Level, db, L0, new[] { (mid - 1.5, W / 2), (mid + 1.5, W / 2) });
        var nearEnd = LiftStructure.Check(e.Design, e.Level, db, L0, new[] { (s.X0 + 1, W / 2), (s.X0 + 4, W / 2) });
        var hoist0 = L0.Hoists.First(h => h.StartNodeId == L0.Deck0.Id || h.EndNodeId == L0.Deck0.Id);
        check("lift: off-centre load raises the near hoist force", nearEnd.MemberUtil[hoist0.Id] > centred.MemberUtil[hoist0.Id] * 1.2,
            $"{nearEnd.MemberUtil[hoist0.Id]:0.00} vs {centred.MemberUtil[hoist0.Id]:0.00}");
        check("lift: total rope pull equals platform + vehicle weight", Math.Abs(centred.RopePull - (centred.PlatformWeight + W)) < 0.05 * (centred.PlatformWeight + W),
            $"{centred.RopePull / 1e3:0} vs {(centred.PlatformWeight + W) / 1e3:0} kN");
        check("lift: deterministic check", LiftStructure.Check(e.Design, e.Level, db, L0, new[] { (mid - 1.5, W / 2), (mid + 1.5, W / 2) }).Util == centred.Util, "");

        // Changing members changes the result: stronger hoist ropes lower rope utilization
        var strong = e.Design.Clone();
        foreach (var h in L0.Hoists) strong.FindMember(h.Id).MaterialId = "cable_composite";
        var sL = LiftStructure.Analyze(strong, e.Level, db)[0];
        var sc = LiftStructure.Check(strong, e.Level, db, sL, new[] { (mid - 1.5, W / 2), (mid + 1.5, W / 2) });
        check("lift: stronger hoist ropes lower rope utilization", sc.RopeUtil < centred.RopeUtil * 0.9, $"{sc.RopeUtil:0.00} vs {centred.RopeUtil:0.00}");
        // Heavier template generates different, stronger members
        var light = Fresh(db, "light"); var heavy = Fresh(db, "heavy");
        double ul = LiftStructure.Check(light.Design, light.Level, db, light.Lifts()[0], new[] { (mid - 1.5, W / 2), (mid + 1.5, W / 2) }).Util;
        double uh = LiftStructure.Check(heavy.Design, heavy.Level, db, heavy.Lifts()[0], new[] { (mid - 1.5, W / 2), (mid + 1.5, W / 2) }).Util;
        check("lift: heavy template members carry more than light ones", uh < ul, $"{uh:0.00} vs {ul:0.00}");
        check("lift: heavy template costs more", heavy.UsedCost > light.UsedCost, $"{heavy.UsedCost:0} vs {light.UsedCost:0}");

        // Counterweight goes through the columns to the pit bases
        var cw = Fresh(db, "standard"); double before = LiftStructure.Check(cw.Design, cw.Level, db, cw.Lifts()[0], Array.Empty<(double, double)>()).BaseReaction;
        cw.SetLiftMachine(null, 4);
        double after = LiftStructure.Check(cw.Design, cw.Level, db, cw.Lifts()[0], Array.Empty<(double, double)>()).BaseReaction;
        double add = 4 * LiftStructure.Cfg(db).CounterweightStep * 1000 * StructuralModel.Gravity;
        check("lift: counterweight weight reaches the pit bases", Math.Abs(after - before - add) < 0.02 * add, $"{(after - before) / 1e3:0} vs {add / 1e3:0} kN cw={cw.Design.Tunnel.Lift.Counterweight} sheaves={string.Join(",", cw.Lifts()[0].Sheaves)}");

        // Broken structure: removing both hoists of a lift is reported, and the test run fails at that lift
        var cut = Fresh(db, "standard");
        foreach (var h in cut.Lifts()[0].Hoists.ToList()) cut.DeleteMember(h.Id);
        check("lift: a platform without hoists is reported", cut.Lifts()[0].Problem != null, cut.Lifts()[0].Problem ?? "");
        var rc = new BridgeSimulation(cut.Design, cut.Level, db).RunToEnd();
        check("lift: a broken lift fails the test with the reason", !rc.IsSuccess && rc.FailureMessage.Contains("左岸电梯"), rc.FailureMessage);

        // Fixed nodes: inner platform end and pit bases cannot move or be deleted; the outer end changes the length
        var fx = Fresh(db, "standard"); var F = fx.Lifts()[0];
        check("lift: pit bases cannot be deleted or moved", !fx.DeleteNode(F.Pits[0].Id).Ok && !fx.MoveNode(F.Pits[0].Id, F.Pits[0].Position.X + 1, F.Pits[0].Position.Y).Ok, "");
        double len0 = F.Shaft.X1 - F.Shaft.X0;
        var mv = fx.MoveNode(F.Deck0.Id, F.Deck0.Position.X + 2, F.Deck0.Position.Y);
        var F2 = fx.Lifts()[0];
        check("lift: dragging the outer platform end shortens both platforms and keeps the members", mv.Ok && Math.Abs(F2.Shaft.X1 - F2.Shaft.X0 - (len0 - 2)) < 1e-6
            && fx.Lifts().All(L => L.Problem == null) && Math.Abs(F2.Deck1.Position.X - F2.Deck0.Position.X - (len0 - 2)) < 1e-6,
            $"{mv.Ok} {len0}→{F2.Shaft.X1 - F2.Shaft.X0} deck {F2.Deck1.Position.X - F2.Deck0.Position.X} {string.Join(",", fx.Lifts().Select(L => L.Problem))}");

        // Tunnel edits move the lift: a shallower tube moves the lower landing and the player's lift members with it
        var dp = Fresh(db, "standard"); var D = dp.Lifts()[0];
        int count0 = D.Members.Count; double y0 = D.Deck0.Position.Y;
        var pt = dp.Design.Nodes.Where(n => n.TunnelPoint).OrderBy(n => n.Position.X).First();
        var down = dp.MoveNode(pt.Id, pt.Position.X, pt.Position.Y + 1);
        var D2 = dp.Lifts()[0];
        check("lift: a shallower tunnel moves the platform and its members up", down.Ok && D2.Deck0.Position.Y > y0 + 0.5 && D2.Members.Count == count0 && D2.Problem == null
            && D2.Members.All(m => Math.Abs(m.Length - Vec3d.Distance(dp.Design.FindNode(m.StartNodeId).Position, dp.Design.FindNode(m.EndNodeId).Position)) < 1e-9),
            $"{down.Message} {y0:0.00}→{D2.Deck0.Position.Y:0.00} {count0}→{D2.Members.Count} {D2.Problem}");

        // Migration of an older save (no lift members): generated once, repeated loads change nothing
        var old = Fresh(db, "standard").Design.Clone();
        var liftMembers = LiftStructure.Analyze(old, db.GetLevel("level13"), db).SelectMany(L => L.Members).ToHashSet();
        old.Members.RemoveAll(m => liftMembers.Contains(m));
        old.Nodes.RemoveAll(n => n.IsLiftFixed || !n.IsAnchor && !n.IsTunnelNode && !old.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id));
        old.Tunnel.Lift = null;
        var m1 = new DesignEditor(old, db.GetLevel("level13"), db);
        int n1 = m1.Design.Members.Count; double c1 = m1.UsedCost;
        var m2 = new DesignEditor(GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(m1.Design)), db.GetLevel("level13"), db);
        check("lift: older save gets lift members once; reloading does not duplicate them", m1.Design.Tunnel.Lift?.Built == 1 && m1.Lifts().All(L => L.Problem == null)
            && m2.Design.Members.Count == n1 && Math.Abs(m2.UsedCost - c1) < 1e-6, $"{n1} vs {m2.Design.Members.Count}");
    }
}
