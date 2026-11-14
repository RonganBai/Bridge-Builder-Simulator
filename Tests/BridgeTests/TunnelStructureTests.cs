using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

/// <summary>
/// Immersed / floating tubes carried by the player's structure: the tube is a beam chain in the ordinary solver,
/// supports and cables are ordinary members, migration turns the old built-in capacity into visible members.
/// </summary>
internal static class TunnelStructureTests
{
    public static LevelDefinition Calm(GameDatabase db, string id, params string[] vehicles)
    {
        var c = LevelEditing.Copy(db.GetLevel(id));
        c.TrafficEvents.RemoveAll(e => e.Type == TrafficEventType.Environment || e.Type == TrafficEventType.Vehicle);
        for (int i = 0; i < vehicles.Length; i++) c.TrafficEvents.Add(new TrafficEvent { Time = i * 3, VehicleId = vehicles[i] });
        return c;
    }

    /// <summary>Immersed tube with a concrete pier under the given joints (1-based; null = every joint).</summary>
    public static DesignEditor Immersed(GameDatabase db, LevelDefinition lv, string grade, IEnumerable<int> joints = null, FoundationType f = FoundationType.Shallow)
    {
        var e = new DesignEditor(null, lv, db);
        var r = e.AddTunnel(0, lv.Terrain.RiverWidth, "tunnel_immersed");
        if (!r.Ok) throw new InvalidOperationException(r.Message);
        e.SetTunnelGrade(-1, grade);
        var g = e.TunnelGeo;
        foreach (int j in joints ?? Enumerable.Range(1, g.Count - 1))
        {
            var jn = e.Design.Nodes.First(n => n.TunnelJoint == j);
            var p = e.AddPier(jn.Position.X, jn.Position.Y, "pier_concrete");
            if (!p.Ok) throw new InvalidOperationException(p.Message);
            if (f != FoundationType.Shallow) e.SetFoundation(p.CreatedId, f);
        }
        return e;
    }

    /// <summary>Floating tube with `perJoint` cables of `cable` straight down from every joint to bed anchors.</summary>
    public static DesignEditor Floating(GameDatabase db, LevelDefinition lv, string grade, string buoy, int perJoint, string cable, FoundationType f, IEnumerable<int> joints = null)
    {
        var e = new DesignEditor(null, lv, db);
        var r = e.AddTunnel(0, lv.Terrain.RiverWidth, "tunnel_floating");
        if (!r.Ok) throw new InvalidOperationException(r.Message);
        e.SetTunnelGrade(-1, grade); e.SetTunnelBuoyancy(-1, buoy);
        var g = e.TunnelGeo;
        foreach (int j in joints ?? Enumerable.Range(1, g.Count - 1))
            for (int k = 0; k < perJoint; k++)
            {
                var jn = e.Design.Nodes.First(n => n.TunnelJoint == j);
                double ax = jn.Position.X + (k - (perJoint - 1) / 2.0) * 1.5;
                var m = e.AddMemberToNewNode(jn.Id, ax, e.PierBedY(ax), cable);
                if (!m.Ok) throw new InvalidOperationException(m.Message);
                var an = e.Design.FindNode(e.Design.FindMember(m.CreatedId).EndNodeId);
                if (f != FoundationType.Shallow) e.SetFoundation(an.Id, f);
            }
        return e;
    }

    private static BridgeSimulation Static(DesignEditor e, LevelDefinition lv, GameDatabase db) => BridgeSimulation.StaticAnalysis(e.Design, lv, db);
    private static double MaxTubeMoment(BridgeSimulation s) => s.Model.TubeMember.Max(k => Math.Abs(s.Status[k].Moment));
    private static double MaxBend(BridgeSimulation s) => s.TunnelStatus.BendUtil.Max();
    private static double MaxAxial(BridgeSimulation s, Func<MaterialDefinition, bool> pick) =>
        Enumerable.Range(0, s.Model.MemberCount).Where(k => pick(s.Model.Mat[k]) && !s.Model.Mat[k].IsTunnel).Select(k => Math.Abs(s.Status[k].Force)).DefaultIfEmpty(0).Max();

    /// <summary>Builds the level 13 / 14 reference designs under the structural tube rules (--gen-tube-refs).</summary>
    public static void Generate(GameDatabase db, string root, bool write)
    {
        var l13 = db.GetLevel("level13"); var l14 = db.GetLevel("level14");
        var e13 = new DesignEditor(null, l13, db);
        e13.AddTunnel(0, l13.Terrain.RiverWidth, "tunnel_immersed"); e13.SetTunnelGrade(-1, "reinforced");
        foreach (int j in new[] { 2, 4 })
        {
            var jn = e13.Design.Nodes.First(n => n.TunnelJoint == j);
            var p = e13.AddPier(jn.Position.X, jn.Position.Y, "pier_concrete");
            if (!l13.Terrain.ZoneAt(jn.Position.X).IsRock) e13.SetFoundation(p.CreatedId, FoundationType.Pile);
        }
        if (Environment.GetEnvironmentVariable("TUBE_SWEEP") == "1")
            foreach (var buoy in new[] { "low", "mid", "high" })
                foreach (var (n, cab) in new[] { (1, "cable"), (2, "cable"), (1, "cable_composite") })
                    foreach (var js in new[] { new[] { 2, 4, 6 }, new[] { 1, 3, 5, 7 }, new[] { 1, 2, 3, 4, 5, 6, 7 } })
                        foreach (var grade in new[] { "reinforced" })
                        {
                            var v = Floating(db, l14, grade, buoy, n, cab, FoundationType.Pile, js);
                            var rr = new BridgeSimulation(v.Design, l14, db).RunToEnd();
                            Console.WriteLine($"SWEEP {grade} {buoy} {n}x{cab} @{string.Join(",", js)}: ${v.UsedCost:0} ok={rr.IsSuccess} util={rr.MaxUtilization:0.00} move={rr.MaxTunnelMove:0.000} {rr.WeakestText} {(rr.IsSuccess ? "" : rr.FailureMessage?.Split((char)10)[0])}");
                        }
        // Low buoyancy: small uplift, one steel cable every second joint on pile anchors (the tube carries traffic in bending)
        var e14 = Floating(db, l14, "reinforced", "low", 1, "cable", FoundationType.Pile, new[] { 2, 4, 6 });
        // Shield tunnel: keep the committed tube, give it the lift template the level's vehicles need
        var l15 = db.GetLevel("level15");
        var e15 = new DesignEditor(GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root, "Data/ReferenceDesigns/legacy/level15_reference_v_before_structural_lifts.json"))), l15, db);
        e15.RebuildLifts();   // platform stays at its saved (longest) length: other tests run long convoys on it
        foreach (var e in new[] { e13, e14 }) e.SetLiftLength(e.LiftLengthForLevel());
        foreach (var (id, e, lv) in new[] { ("level13", e13, l13), ("level14", e14, l14), ("level15", e15, l15) })
        {
            Console.WriteLine($"LIFT {id}: {e.LiftSummary()} template={e.Design.Tunnel.Lift?.Template}");
            var sim = new BridgeSimulation(e.Design, lv, db); var res = sim.RunToEnd();
            Console.WriteLine($"GEN {id}: cost {e.UsedCost:0} / budget {lv.Budget} ok={res.IsSuccess} util={res.MaxUtilization:0.00} weakest={res.WeakestText} {res.FailureMessage}");
            if (write) File.WriteAllText(Path.Combine(root, $"Data/ReferenceDesigns/{id}_reference.json"), GameDatabase.SerializeDesign(e.Design));
        }
    }

    public static void Run(GameDatabase db, string root, Action<string, bool, string> check)
    {
        var l13 = db.GetLevel("level13");
        var l14 = db.GetLevel("level14");

        // ---- Immersed: the tube is a beam between the bank shafts, carried only by the player's piers ----
        var bare = Immersed(db, l13, "normal", new int[0]);
        var bareS = Static(bare, l13, db);
        check("沉管结构 管身是求解器里的梁（每节一根，两端铰接在两岸电梯井）", bareS.Model.TubeMember.Length == bare.TunnelGeo.Count && bareS.Model.FixY[bareS.Model.TubeNode[0]] && !bareS.Model.FixY[bareS.Model.TubeNode[1]], "");
        check("沉管结构 接缝圆点在管底、不是固定支座（没有隐藏支撑）",
            bare.Design.Nodes.Where(n => n.TunnelJoint > 0).All(n => Math.Abs(n.Position.Y - bare.TunnelGeo.BottomAt(n.Position.X)) < 1e-6) &&
            bareS.Model.TubeNode.Skip(1).Take(bareS.Model.TubeNode.Length - 2).All(i => !bareS.Model.FixX[i] && !bareS.Model.FixY[i] && !bareS.Model.IsAnchor[i]), "");
        check("沉管结构 只靠两端支撑的 60 m 管身静载接缝弯矩就超限", MaxBend(bareS) >= 1, $"{MaxBend(bareS):0.00}");
        var all = Immersed(db, l13, "normal"); var allS = Static(all, l13, db);
        var half = Immersed(db, l13, "normal", new[] { 2, 4 }); var halfS = Static(half, l13, db);
        check("沉管结构 每道接缝一个桥墩时静载够用", MaxBend(allS) < 1 && allS.Output.Stable, $"{MaxBend(allS):0.00}");
        check("沉管结构 支撑减少（隔一道接缝）→ 接缝弯矩和墩身压力都变大",
            MaxTubeMoment(halfS) > 1.5 * MaxTubeMoment(allS) && MaxAxial(halfS, m => m.IsPier) > 1.2 * MaxAxial(allS, m => m.IsPier),
            $"M {MaxTubeMoment(allS) / 1e6:0.00}→{MaxTubeMoment(halfS) / 1e6:0.00} MN·m, N {MaxAxial(allS, m => m.IsPier) / 1e3:0}→{MaxAxial(halfS, m => m.IsPier) / 1e3:0} kN");
        var halfHeavy = Static(Immersed(db, l13, "heavy", new[] { 2, 4 }), l13, db);
        check("沉管结构 等级越高接缝抗弯越强（同一支撑下利用率下降或自重增加都如实计入）",
            TunnelStructure.JointMomentCapacity(bare.TunnelGeo.Type, db.TunnelConfig.Types["immersed"].Grades["heavy"]) > TunnelStructure.JointMomentCapacity(bare.TunnelGeo.Type, db.TunnelConfig.Types["immersed"].Grades["normal"]) &&
            MaxTubeMoment(halfHeavy) > MaxTubeMoment(halfS), $"{MaxTubeMoment(halfHeavy) / 1e6:0.00} vs {MaxTubeMoment(halfS) / 1e6:0.00}");
        // Net weight: one bookkeeping (mass − displaced water), the weight reaches the foundations
        var g13 = all.TunnelGeo; var cfg = db.TunnelConfig;
        double net = Enumerable.Range(0, g13.Count).Sum(i => TunnelStructure.NetMass(g13, l13, cfg, i) * g13.Len[i]) * cfg.Gravity;
        double pierReact = Enumerable.Range(0, allS.Model.NodeCount).Where(i => allS.Model.PierFoundation[i] != FoundationType.None).Sum(i => allS.Output.Reaction[2 * i + 1]);
        double endReact = allS.Output.Reaction[2 * allS.Model.TubeNode[0] + 1] + allS.Output.Reaction[2 * allS.Model.TubeNode[^1] + 1];
        double pierSelf = allS.Model.Mass.Where((m, k) => allS.Model.Mat[k].IsPier).Sum() * StructuralModel.Gravity;
        check("沉管结构 管身净重（自重 − 排水）全部传到桥墩基础和两岸，不重复也不丢", Math.Abs(pierReact + endReact - net - pierSelf) < 1e-3 * (net + pierSelf), $"{(pierReact + endReact) / 1e3:0} kN vs {(net + pierSelf) / 1e3:0} kN");
        // Vehicles load the tube through the solver: a truck in the tube increases the joint moments
        var tr = Calm(db, "level13", "truck_heavy");
        var trSim = new BridgeSimulation(half.Design, tr, db); trSim.Initialize();
        double m0 = MaxTubeMoment(trSim), mPeak = 0;
        for (int s = 0; s < 60 * 120 && trSim.State == SimState.Running; s++) { trSim.Step(); mPeak = Math.Max(mPeak, MaxTubeMoment(trSim)); }
        check("沉管结构 隧道里的车轴荷载作用在管节梁上（重卡驶过时接缝弯矩上升）", mPeak > m0 * 1.1, $"{m0 / 1e6:0.00}→{mPeak / 1e6:0.00} MN·m");
        // A tube on too few supports opens its joints and floods with a message saying where and what to build
        var bareRun = new BridgeSimulation(bare.Design, Calm(db, "level13", "car"), db).RunToEnd();
        check("沉管结构 支撑不足时管节接缝张开进水，提示位置与改进办法", !bareRun.IsSuccess && bareRun.FailureReason == FailureReason.TunnelFlooded && bareRun.FailureMessage.Contains("接缝弯矩") && bareRun.FailureMessage.Contains("桥墩"), bareRun.FailureMessage);
        var traffic = Calm(db, "level13", "car", "truck", "semi_long");
        var shallowRun = new BridgeSimulation(all.Design, traffic, db).RunToEnd();
        check("沉管结构 软土上的浅基础桥墩被压坏（基础类型影响承载，提示锚点）", !shallowRun.IsSuccess && (shallowRun.FailureMessage ?? "").Contains("浅基础"), shallowRun.FailureMessage);
        var pile = Immersed(db, l13, "normal", null, FoundationType.Pile);
        var allRun = new BridgeSimulation(pile.Design, traffic, db).RunToEnd();
        check("沉管结构 每道接缝有桩基桥墩时车辆正常通过", allRun.IsSuccess, allRun.FailureMessage);
        // Underwater building is allowed only to carry the tube: above the bed, not inside the tube
        double jy = all.Design.Nodes.First(n => n.TunnelJoint == 1).Position.Y;
        check("沉管结构 有沉管时可以在水下建节点，但不能在管身里或河床下", all.CheckNodePosition(15, jy - 0.5) == null && all.CheckNodePosition(15, jy + 2) != null && all.CheckNodePosition(15, all.PierBedY(15) - 1) != null, "");
        check("沉管结构 没有隧道时仍不能在水下建造", new DesignEditor(null, l13, db).CheckNodePosition(15, l13.Terrain.WaterLevel - 2) != null, "");
        check("沉管结构 接缝圆点可以接钢梁 / 钢索，但不能接桥面", all.PreviewMemberToPoint(all.Design.Nodes.First(n => n.TunnelJoint == 3).Id, 33, jy - 0.5, "steel").Allowed && !all.PreviewMemberToPoint(all.Design.Nodes.First(n => n.TunnelJoint == 3).Id, 33, jy - 0.5, "road").Allowed, "");
        check("沉管结构 旧的地基加固按钮对沉管无效（不再有看不见的承载力）", !all.SetTunnelReinforced(-1, true).Ok, "");

        // ---- Floating: the player builds the anchor cables; buoyancy level is a real trade-off ----
        var fl = Floating(db, l14, "reinforced", "mid", 2, "cable", FoundationType.Pile);
        var flS = Static(fl, l14, db); var gf = fl.TunnelGeo;
        check("悬浮结构 接缝圆点就是可拖动的管底接缝点，可以接锚索", fl.Design.Nodes.Count(n => n.TunnelJoint > 0 && n.TunnelPoint) == gf.Count - 1, "");
        check("悬浮结构 锚索根数按钮已停用（锚索要自己建）", !fl.SetTunnelCables(-1, 3, "standard").Ok, "");
        double up = -Enumerable.Range(0, gf.Count).Sum(i => TunnelStructure.NetMass(gf, l14, cfg, i) * gf.Len[i]) * cfg.Gravity;
        double ballast = TunnelStructure.Ballast(gf, l14, cfg, 3), shell = gf.Grade[3].MassPerMeter, disp = TunnelStructure.DisplacedMass(gf, l14, cfg, 3);
        check("悬浮结构 浮力 / 自重 / 配重只算一次：管身 + 配重 = 排水 ÷ 浮力比，净上浮 = 排水 − 管身 − 配重",
            ballast >= 0 && Math.Abs((shell + ballast) * gf.Buoy[3].Ratio - disp) < 1e-6 * disp && up > 0, $"shell {shell / 1000:0} t/m ballast {ballast / 1000:0} t/m disp {disp / 1000:0} t/m");
        double cableSum = Enumerable.Range(0, flS.Model.MemberCount).Where(k => flS.Model.Mat[k].IsCable).Sum(k => flS.Status[k].Force * flS.Model.Sin[k] * (flS.Model.Y[flS.Model.A[k]] > flS.Model.Y[flS.Model.B[k]] ? 1 : -1));
        check("悬浮结构 中浮力每道接缝 2 根钢索、桩基锚块，静载安全", flS.Output.Stable && MaxBend(flS) < 1 && MaxAxial(flS, m => m.IsCable) < 0.9 * 880e3, $"bend {MaxBend(flS):0.00} cable {MaxAxial(flS, m => m.IsCable) / 1e3:0} kN");
        var hi = Static(Floating(db, l14, "reinforced", "high", 2, "cable", FoundationType.Pile), l14, db);
        var lo = Static(Floating(db, l14, "reinforced", "low", 2, "cable", FoundationType.Pile), l14, db);
        check("悬浮结构 浮力越高锚索拉力越大（低 < 中 < 高）", MaxAxial(lo, m => m.IsCable) < MaxAxial(flS, m => m.IsCable) && MaxAxial(flS, m => m.IsCable) < MaxAxial(hi, m => m.IsCable),
            $"{MaxAxial(lo, m => m.IsCable) / 1e3:0} / {MaxAxial(flS, m => m.IsCable) / 1e3:0} / {MaxAxial(hi, m => m.IsCable) / 1e3:0} kN");
        var none = Floating(db, l14, "reinforced", "mid", 0, "cable", FoundationType.Shallow);
        check("悬浮结构 没有锚索时上浮力只靠两端，接缝弯矩超限", MaxBend(Static(none, l14, db)) >= 1, $"{MaxBend(Static(none, l14, db)):0.00}");
        var oneSteel = Static(Floating(db, l14, "reinforced", "mid", 1, "cable", FoundationType.Pile), l14, db);
        var oneComp = Static(Floating(db, l14, "reinforced", "mid", 1, "cable_composite", FoundationType.Pile), l14, db);
        check("悬浮结构 每道接缝只 1 根钢索拉断；换成复合钢索就够（更强的构件有用）",
            oneSteel.CurrentMaxUtilization > 1 && oneComp.CurrentMaxUtilization < 1, $"{oneSteel.CurrentMaxUtilization:0.00} / {oneComp.CurrentMaxUtilization:0.00}");
        var shallowSoft = new BridgeSimulation(Floating(db, l14, "reinforced", "mid", 2, "cable", FoundationType.Shallow).Design, Calm(db, "level14", "car"), db).RunToEnd();
        check("悬浮结构 软土河床上浅基础锚块被拔出（失败提示锚点和承载力）", !shallowSoft.IsSuccess && (shallowSoft.FailureMessage ?? "").Contains("锚点"), shallowSoft.FailureMessage);
        // Cable direction matters: a cable at 45° carries more force for the same vertical pull
        var vert = Floating(db, l14, "reinforced", "mid", 1, "cable_composite", FoundationType.Pile, new[] { 4 });
        var slant = new DesignEditor(null, l14, db); slant.AddTunnel(0, l14.Terrain.RiverWidth, "tunnel_floating"); slant.SetTunnelGrade(-1, "reinforced");
        var j4 = slant.Design.Nodes.First(n => n.TunnelJoint == 4); double depth = j4.Position.Y - slant.PierBedY(j4.Position.X);
        slant.AddMemberToNewNode(j4.Id, j4.Position.X + depth, slant.PierBedY(j4.Position.X + depth), "cable_composite");
        double fv = MaxAxial(Static(vert, l14, db), m => m.IsCable), fs = MaxAxial(Static(slant, l14, db), m => m.IsCable);
        check("悬浮结构 锚索方向有影响：45° 斜拉比竖直拉力大", fs > 1.2 * fv, $"{fv / 1e3:0} vs {fs / 1e3:0} kN");
        // Vehicles push down on the floating tube and relieve the cables
        var fr = new BridgeSimulation(fl.Design, Calm(db, "level14", "truck_heavy"), db); fr.Initialize();
        double c0 = MaxAxial(fr, m => m.IsCable), cMin = c0;
        for (int s = 0; s < 60 * 150 && fr.State == SimState.Running; s++) { fr.Step(); cMin = Math.Min(cMin, Enumerable.Range(0, fr.Model.MemberCount).Where(k => fr.Model.Mat[k].IsCable).Min(k => fr.Status[k].Force)); }
        check("悬浮结构 车辆在管内下压，锚索拉力减小", cMin < c0 * 0.97, $"{c0 / 1e3:0}→{cMin / 1e3:0} kN");
        // Two 60 t vehicles standing in the middle tube element (the same loads for both buoyancy levels)
        double Sink(string buoy)
        {
            var e = Floating(db, l14, "reinforced", buoy, 1, "cable_composite", FoundationType.Pile, new[] { 2, 4, 6 });
            var m = StructuralModel.Build(e.Design, l14, db);
            var solver = new StructureSolver();
            double Mid(AxleLoad[] ax)
            {
                var loads = new double[m.NodeCount * 2]; var o = new SolveOutput(); o.EnsureSize(m.NodeCount, m.MemberCount);
                LoadCalculator.Compute(m, ax, ax.Length, loads); solver.Solve(m, loads, o);
                return -o.Displacement[2 * m.TubeNode[4] + 1];
            }
            int k = m.TubeMember[3];
            var two = new[] { new AxleLoad { MemberIndex = k, T = 0.3, Force = 6e5 }, new AxleLoad { MemberIndex = k, T = 0.9, Force = 6e5 } };
            return Mid(two) - Mid(new AxleLoad[0]);
        }
        double sinkLow = Sink("low"), sinkMid = Sink("mid");
        check("悬浮结构 浮力是取舍：低浮力锚索省，但重车让管身下沉更多（锚索松弛后只靠管身抗弯）", sinkLow > 1.5 * sinkMid, $"low {sinkLow:0.000} m, mid {sinkMid:0.000} m");

        // ---- Migration: old built-in capacity becomes visible members once ----
        var oldRef = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root, "Data/ReferenceDesigns/legacy/level13_reference_v_before_structural_tunnels.json")));
        var m1 = new DesignEditor(oldRef, l13, db);
        int piers1 = m1.Design.Nodes.Count(n => n.PierBase);
        var again = new DesignEditor(GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(m1.Design)), l13, db);
        check("迁移 旧沉管存档：每道接缝生成一座可见、计费的桥墩", m1.Design.Tunnel.Structure == 1 && piers1 == m1.TunnelGeo.Count - 1 && m1.Design.Members.Where(x => db.GetMaterial(x.MaterialId).IsPier).All(x => x.Cost > 0), $"{piers1}");
        check("迁移 重复载入不再生成（不重复）", again.Design.Nodes.Count == m1.Design.Nodes.Count && again.Design.Members.Count == m1.Design.Members.Count, $"{again.Design.Members.Count} vs {m1.Design.Members.Count}");
        var oldFl = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root, "Data/ReferenceDesigns/legacy/level14_reference_v_before_structural_tunnels.json")));
        var m2 = new DesignEditor(oldFl, l14, db);
        var oldG = TunnelGeometry.Build(oldFl, db);
        int expect = Enumerable.Range(1, oldG.Count - 1).Sum(j => (int)Math.Ceiling((oldG.CableCount[j - 1] + oldG.CableCount[j]) / 2.0));
        var liftM2 = m2.Lifts().SelectMany(L => L.Members).ToHashSet();
        check("迁移 旧悬浮存档：锚索根数变成真实钢索和河床锚块", m2.Design.Members.Count(x => db.GetMaterial(x.MaterialId).IsCable && !liftM2.Contains(x)) == expect && m2.Design.Nodes.Count(n => n.PierBase) == expect, $"{expect}");
        var m2b = new DesignEditor(GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(m2.Design)), l14, db);
        check("迁移 悬浮存档重复载入结果相同", m2b.Design.Members.Count == m2.Design.Members.Count && Math.Abs(m2b.UsedCost - m2.UsedCost) < 1e-6, "");
        var simDirect = new BridgeSimulation(GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root, "Data/ReferenceDesigns/legacy/level13_reference_v_before_structural_tunnels.json"))), l13, db);
        check("迁移 仿真直接读旧存档时同样迁移（没有隐藏支撑）", simDirect.Design.Tunnel.Structure == 1 && simDirect.Design.Nodes.Count(n => n.PierBase) == piers1, "");
        var oldRes = simDirect.RunToEnd();
        check("迁移 旧沉管参考方案迁移后仍能通关", oldRes.IsSuccess, oldRes.FailureMessage);
    }
}
