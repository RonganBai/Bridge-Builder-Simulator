using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    /// <summary>Result of one lift structure check (one lift, one set of axles on the platform).</summary>
    public class LiftCheck
    {
        public bool Stable = true;
        /// <summary>Overall utilization = the largest of the parts below.</summary>
        public double Util, PlatformUtil, RopeUtil, ShaftUtil, DriveUtil;
        /// <summary>Total rope pull N, out-of-balance drive force N, shaft floor reaction N.</summary>
        public double RopePull, DriveForce, BaseReaction;
        /// <summary>Self-weight of the platform N, counterweight N, vehicle load N.</summary>
        public double PlatformWeight, CounterweightForce, VehicleLoad;
        /// <summary>Governing part, where it is, why it fails and how to fix it.</summary>
        public string Governing = "", Where = "", Why = "", Fix = "";
        /// <summary>Member id → utilization of every lift member (for colouring / the property panel).</summary>
        public Dictionary<int, double> MemberUtil = new();
    }

    /// <summary>One bank lift found in the design: its fixed nodes, its members and what they do.</summary>
    public sealed class LiftLayout
    {
        public bool Mechanical;
        public int Bank;
        public bool Left => Bank == 1;
        public TunnelRules.LiftShaft Shaft;
        public BridgeNode Deck0, Deck1;
        public readonly List<BridgeNode> Pits = new();
        /// <summary>Every node of this lift (fixed nodes and the free nodes inside the shaft).</summary>
        public readonly HashSet<int> Nodes = new();
        /// <summary>Nodes that move with the cab: everything reached from the platform end through non-cable members.</summary>
        public readonly HashSet<int> Platform = new();
        public readonly List<BridgeMember> Members = new(), Hoists = new(), DeckPath = new();
        /// <summary>Top node of each hoist rope (sheave) → number of ropes over it.</summary>
        public readonly Dictionary<int, int> Sheaves = new();
        /// <summary>Nodes held sideways by the shaft end walls (lining ties).</summary>
        public readonly HashSet<int> Ties = new();
        /// <summary>Why this lift cannot run (null = it can).</summary>
        public string Problem;
        public bool IsPlatformMember(BridgeMember m) => Platform.Contains(m.StartNodeId) && Platform.Contains(m.EndNodeId);
    }

    /// <summary>
    /// Bank lifts as player structure. Each shaft has fixed nodes created with the tunnel: the two platform ends at the
    /// lower landing (deck0 / deck1) and two shaft floor bases (pit). Everything else is ordinary members built with the
    /// normal tools inside the shaft:
    /// - the platform = all members reached from the platform ends without passing a cable; it must carry a deck from
    ///   one end to the other (vehicles drive on it) and must not be fixed to the shaft (it has to travel);
    /// - hoist ropes = cables from the platform to the rest of the shaft structure; their top nodes are the sheaves,
    ///   where the counterweight hangs (its weight is a load on the sheave nodes and goes down the shaft structure);
    /// - shaft structure = columns / frames standing on the pit bases (fixed) and tied sideways to the end walls
    ///   wherever a node lies on a wall face;
    /// - the drive holds |rope pull − counterweight| (TunnelRoute.Lift.Drive).
    /// The members are part of the ordinary structural model (same solver, same failures). The platform is analysed
    /// at the lower landing (longest ropes); while it travels it is drawn moved, the model does not move it.
    /// </summary>
    public static class LiftStructure
    {
        public static LiftConfig Cfg(GameDatabase db) => db?.TunnelConfig?.Lift ?? new LiftConfig();

        public static MaterialDefinition Mat(GameDatabase db, string id) => id != null && db.Materials.TryGetValue(id, out var m) ? m : null;

        /// <summary>Space of one shaft where nodes belong to that lift: between the end walls, pit floor to head.</summary>
        public static bool InZone(LiftConfig c, TunnelRules.LiftShaft s, double x, double y) =>
            x >= s.X0 - TunnelRules.LiftEndWallOffset - 0.35 && x <= s.X1 + TunnelRules.LiftEndWallOffset + 0.35 &&
            y >= s.Bottom - c.Pit - 0.05 && y <= s.Top + c.HeadRoom + 3;

        public static double PitY(LiftConfig c, TunnelRules.LiftShaft s) => s.Bottom - c.Pit;
        public static double HeadY(LiftConfig c, TunnelRules.LiftShaft s) => s.Top + c.HeadRoom;

        /// <summary>Inner faces of the two end walls.</summary>
        public static (double left, double right) WallFaces(TunnelRules.LiftShaft s) =>
            (s.X0 - TunnelRules.LiftEndWallOffset + TunnelRules.LiftEndWallThickness / 2, s.X1 + TunnelRules.LiftEndWallOffset - TunnelRules.LiftEndWallThickness / 2);

        public static LiftLayout[] Analyze(BridgeDesign d, LevelDefinition level, GameDatabase db)
        {
            var g = TunnelGeometry.Build(d, db);
            if (g == null) return Array.Empty<LiftLayout>();
            return Analyze(d, level, db, TunnelRules.Shafts(level, g));
        }

        public static LiftLayout[] Analyze(BridgeDesign d, LevelDefinition level, GameDatabase db, TunnelRules.LiftShaft[] shafts)
        {
            var c = Cfg(db); var result = new List<LiftLayout>();
            for (int bank = 1; bank <= 2 && bank <= shafts.Length; bank++)
            {
                var L = new LiftLayout { Bank = bank, Shaft = shafts[bank - 1], Mechanical=level.MechanicalLift };
                foreach (var n in d.Nodes)
                {
                    bool own = n.LiftBank == bank || n.LiftBank == 0 && !n.IsAnchor && !n.TunnelPoint && n.TunnelJoint == 0 && !n.PierBase
                        && InZone(c, L.Shaft, n.Position.X, n.Position.Y);
                    if (!own) continue;
                    L.Nodes.Add(n.Id);
                    if (n.LiftBank == bank && n.LiftRole == "deck0") L.Deck0 = n;
                    if (n.LiftBank == bank && n.LiftRole == "deck1") L.Deck1 = n;
                    if (n.LiftBank == bank && n.LiftRole == "pit") L.Pits.Add(n);
                }
                foreach (var m in d.Members) if (L.Nodes.Contains(m.StartNodeId) && L.Nodes.Contains(m.EndNodeId)) L.Members.Add(m);
                bool IsCable(BridgeMember m) => Mat(db, m.MaterialId)?.IsCable == true;
                if (L.Deck0 == null || L.Deck1 == null) { L.Problem = Loc.T("电梯平台端点缺失（重建电梯结构即可恢复）"); result.Add(L); continue; }
                // Platform: reached from the platform ends without passing a cable
                var q = new Queue<int>(); q.Enqueue(L.Deck0.Id); q.Enqueue(L.Deck1.Id);
                while (q.Count > 0)
                {
                    int cur = q.Dequeue(); if (!L.Platform.Add(cur)) continue;
                    foreach (var m in L.Members) if (!IsCable(m) && !(level.MechanicalLift && Mat(db,m.MaterialId)?.Extendable==true) && (m.StartNodeId == cur || m.EndNodeId == cur)) q.Enqueue(m.Other(cur));
                }
                var (wl, wr) = WallFaces(L.Shaft);
                foreach (int id in L.Nodes)
                {
                    if (L.Platform.Contains(id)) continue;
                    var n = d.FindNode(id);
                    if (!level.MechanicalLift && (Math.Abs(n.Position.X - wl) <= c.TieTolerance || Math.Abs(n.Position.X - wr) <= c.TieTolerance)) L.Ties.Add(id);
                }
                foreach (var m in L.Members)
                {
                    if (!IsCable(m)) continue;
                    bool a = L.Platform.Contains(m.StartNodeId), b = L.Platform.Contains(m.EndNodeId);
                    if (a == b) continue;
                    L.Hoists.Add(m);
                    int top = a ? m.EndNodeId : m.StartNodeId;
                    L.Sheaves[top] = L.Sheaves.GetValueOrDefault(top) + 1;
                }
                // Deck path from one platform end to the other (vehicles drive on it)
                var prev = new Dictionary<int, BridgeMember>(); var seen = new HashSet<int> { L.Deck0.Id }; var bq = new Queue<int>(); bq.Enqueue(L.Deck0.Id);
                while (bq.Count > 0 && !seen.Contains(L.Deck1.Id))
                {
                    int cur = bq.Dequeue();
                    foreach (var m in L.Members)
                    {
                        if (Mat(db, m.MaterialId)?.IsDeck != true || (m.StartNodeId != cur && m.EndNodeId != cur)) continue;
                        int o = m.Other(cur); if (!seen.Add(o)) continue;
                        prev[o] = m; bq.Enqueue(o);
                    }
                }
                if (seen.Contains(L.Deck1.Id))
                    for (int cur = L.Deck1.Id; cur != L.Deck0.Id; cur = prev[cur].Other(cur)) L.DeckPath.Insert(0, prev[cur]);
                if (L.DeckPath.Count == 0) L.Problem = Loc.T("平台桥面没有从平台一端铺到另一端：车辆开不上去");
                else if (L.Platform.Any(id => L.Pits.Any(p => p.Id == id) || L.Ties.Contains(id)))
                    L.Problem = Loc.T("平台和井道结构刚性连在一起（没有经过钢索）：平台升降不了。平台只能用钢索吊在井道结构上");
                else if (level.MechanicalLift) L.Problem=LiftMechanics.Plan(d,level,db,L).Problem;
                else if (L.Hoists.Count == 0) L.Problem = Loc.T("平台没有吊索：从平台节点拉钢索到井道顶部的结构上");
                result.Add(L);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Lift supports and counterweight in a model built from the design: pit bases fixed (X, Y, rotation), the
        /// platform end deck0 held sideways by the guide rail, nodes on an end wall face tied sideways, and the
        /// counterweight hung on the sheaves (split by the number of ropes over each).
        /// </summary>
        public static void ApplySupports(StructuralModel m, IEnumerable<LiftLayout> lifts, double counterweightT)
        {
            foreach (var L in lifts)
            {
                // The shaft is excavated: a column ending inside it stands on nothing (only the pit bases carry)
                foreach (int id in L.Nodes)
                    if (m.NodeIndex.TryGetValue(id, out int f) && m.IsFoundation[f])
                    { m.FixX[f] = false; m.FixY[f] = false; m.FixRot[f] = false; m.IsFoundation[f] = false; }
                foreach (var p in L.Pits)
                    if (m.NodeIndex.TryGetValue(p.Id, out int i))
                    {
                        m.FixX[i] = true; m.FixY[i] = true; m.FixRot[i] = true; m.IsAnchor[i] = true; m.AnchorMaxLoad[i] = 0;
                        m.AnchorIds[i] = Loc.F("{0}井底基础", (L.Left ? Loc.T("左岸电梯") : Loc.T("右岸电梯")));
                    }
                if (L.Deck0 != null && m.NodeIndex.TryGetValue(L.Deck0.Id, out int d0)) m.FixX[d0] = true;
                foreach (int t in L.Ties) if (m.NodeIndex.TryGetValue(t, out int ti)) m.FixX[ti] = true;
                int ropes = L.Sheaves.Values.Sum();
                if (ropes == 0) continue;
                double w = counterweightT * 1000 * StructuralModel.Gravity;
                foreach (var (id, n) in L.Sheaves) if (m.NodeIndex.TryGetValue(id, out int si)) m.NodeLoadY[si] -= w * n / ropes;
            }
        }

        /// <summary>Axle at x (m along the shaft) on the platform deck: the deck member under it.</summary>
        public static bool AxleOnPlatform(StructuralModel m, BridgeDesign d, LiftLayout L, double x, double force, out AxleLoad axle)
        {
            axle = default;
            foreach (var mem in L.DeckPath)
            {
                var a = d.FindNode(mem.StartNodeId); var b = d.FindNode(mem.EndNodeId);
                double xa = a.Position.X, xb = b.Position.X;
                if (x < Math.Min(xa, xb) - 1e-6 || x > Math.Max(xa, xb) + 1e-6) continue;
                if (!m.MemberIndex.TryGetValue(mem.Id, out int k)) return false;
                axle = new AxleLoad { MemberIndex = k, T = Math.Abs(xb - xa) > 1e-9 ? Math.Clamp((x - xa) / (xb - xa), 0, 1) : 0.5, Force = force };
                return true;
            }
            return false;
        }

        public static string Name(LiftLayout L) => L.Left ? Loc.T("左岸电梯") : Loc.T("右岸电梯");

        /// <summary>What a lift member does (for failure text).</summary>
        public static string Role(LiftLayout L, BridgeMember m) =>
            L.Hoists.Contains(m) ? Loc.T("吊索") : L.IsPlatformMember(m) ? Loc.T("平台构件") : Loc.T("井道结构");

        public static string FixFor(LiftLayout L, BridgeMember m) =>
            L.Hoists.Contains(m) ? Loc.T("换更粗的钢索（复合钢索），或在平台上多加吊点（从平台其他节点再拉钢索到井道结构）")
            : L.IsPlatformMember(m) ? Loc.T("像桥一样加强平台：缩短节间、加桁架腹杆或下弦，或换更强的桥面")
            : Loc.T("加强井道结构：换更强的柱子、把柱子节点靠在井壁上（侧向拉结），或减小配重");

        /// <summary>
        /// Checks one lift alone (its own members only) carrying the given axles (x along the shaft, m; force N, down).
        /// Same solver, same member checks as the full model; used to decide shared rides and for the build panel.
        /// </summary>
        public static LiftCheck Check(BridgeDesign design, LevelDefinition level, GameDatabase db, LiftLayout L, IReadOnlyList<(double x, double force)> axles)
        {
            if(!level.MechanicalLift)return CheckAtReference(design,level,db,L,axles);
            var plan=LiftMechanics.Plan(design,level,db,L);
            if(plan.Problem!=null)return new LiftCheck{Stable=false,Util=99,Where=Name(L),Why=plan.Problem,Fix=Loc.T("调整液压缸行程、稳定支点与滑轮绳路")};
            LiftCheck worst=null;
            foreach(double fraction in new[]{0d,.5,1d})
            {
                var moved=design.Clone();
                foreach(var node in moved.Nodes)if(plan.Motion.TryGetValue(node.Id,out double ratio))node.Position=new Vec3d(node.Position.X,node.Position.Y+fraction*(L.Shaft.Top-L.Shaft.Bottom)*ratio);
                var checkedLoad=CheckAtReference(moved,level,db,L,axles);
                if(worst==null||checkedLoad.Util>worst.Util)worst=checkedLoad;
            }
            return worst;
        }
        private static LiftCheck CheckAtReference(BridgeDesign design, LevelDefinition level, GameDatabase db, LiftLayout L, IReadOnlyList<(double x, double force)> axles)
        {
            var c = Cfg(db); var lift = design.Tunnel?.Lift ?? c.Templates.Values.First();
            var r = new LiftCheck();
            if (L.Problem != null) { r.Stable = false; r.Util = 99; r.Governing = Loc.T("电梯结构不完整"); r.Where = Name(L); r.Why = L.Problem; r.Fix = Loc.T("按提示补全电梯结构，或在隧道属性里重建电梯结构"); return r; }
            var sub = new BridgeDesign { Nodes = design.Nodes.Where(n => L.Nodes.Contains(n.Id)).Select(n => n.Clone()).ToList(), Members = L.Members.Select(x => x.Clone()).ToList() };
            var m = StructuralModel.Build(sub, level, db);
            if(level.MechanicalLift)foreach(var member in sub.Members)
                if(m.MemberIndex.TryGetValue(member.Id,out int k)&&m.Mat[k].Extendable)m.Mass[k]=member.Length*m.Mat[k].MassPerMeter;
            ApplySupports(m, new[] { L }, level.MechanicalLift?0:lift.Counterweight);
            var al = new List<AxleLoad>(); double vehicle = 0;
            foreach (var (x, f) in axles)
                if (AxleOnPlatform(m, sub, L, x, f, out var a)) { al.Add(a); vehicle += f; }
            var loads = new double[2 * m.NodeCount];
            LoadCalculator.Compute(m, al.ToArray(), al.Count, loads);
            var o = new SolveOutput(); o.EnsureSize(m.NodeCount, m.MemberCount);
            new StructureSolver().Solve(m, loads, o);   // one solver per check: checks also run on worker threads
            double cw = (level.MechanicalLift?0:lift.Counterweight) * 1000 * StructuralModel.Gravity;
            r.CounterweightForce = cw; r.VehicleLoad = vehicle;
            r.PlatformWeight = L.Members.Where(L.IsPlatformMember).Sum(x => m.Mass[m.MemberIndex[x.Id]]) * StructuralModel.Gravity;
            if (!o.Stable)
            {
                r.Stable = false; r.Util = 99; r.Governing = Loc.T("结构失稳"); r.Where = Name(L);
                r.Why = Loc.T("电梯结构不稳定（有节点没有被约束住）"); r.Fix = Loc.T("检查平台是否被吊索吊住、井道柱子是否站在井底基础上并靠在井壁上");
                return r;
            }
            var st = new MemberStatus[m.MemberCount];
            FailureDetector.EvaluateMembers(m, o, al.ToArray(), al.Count, st);
            BridgeMember worst = null; double wu = 0;
            foreach (var mem in L.Members)
            {
                int k = m.MemberIndex[mem.Id]; double u = st[k].Utilization;
                r.MemberUtil[mem.Id] = u;
                if (L.Hoists.Contains(mem)) r.RopeUtil = Math.Max(r.RopeUtil, u);
                else if (L.IsPlatformMember(mem)) r.PlatformUtil = Math.Max(r.PlatformUtil, u);
                else r.ShaftUtil = Math.Max(r.ShaftUtil, u);
                if (u > wu) { wu = u; worst = mem; }
            }
            r.RopePull = L.Hoists.Sum(h => Math.Max(0, o.AxialForce[m.MemberIndex[h.Id]]));
            r.DriveForce = Math.Abs(r.RopePull - cw);
            if(level.MechanicalLift)
            {
                r.CounterweightForce=sub.Nodes.Sum(n=>n.CounterweightTonnes)*1000*StructuralModel.Gravity;
                r.DriveForce=L.Members.Where(mem=>db.GetMaterial(mem.MaterialId)?.Extendable==true).Sum(mem=>Math.Abs(o.AxialForce[m.MemberIndex[mem.Id]]));
            }
            var drive = c.Drives.GetValueOrDefault(lift.Drive ?? "") ?? c.Drives.Values.First();
            r.DriveUtil = level.MechanicalLift?LiftMechanics.DriveUtil(m,o,L):r.DriveForce / Math.Max(1, drive.Force);
            r.BaseReaction = L.Pits.Where(p => m.NodeIndex.ContainsKey(p.Id)).Sum(p => o.Reaction[2 * m.NodeIndex[p.Id] + 1]);
            if(level.MechanicalLift)r.BaseReaction=Enumerable.Range(0,m.NodeCount).Where(i=>m.IsAnchor[i]).Sum(i=>o.Reaction[2*i+1]);
            r.Util = Math.Max(wu, r.DriveUtil);
            if(level.MechanicalLift)
            {
                foreach(int id in L.Nodes)
                    if(m.NodeIndex.TryGetValue(id,out int i)&&m.IsAnchor[i]&&m.AnchorMaxLoad[i]>0)
                    {
                        double reaction=Math.Sqrt(o.Reaction[2*i]*o.Reaction[2*i]+o.Reaction[2*i+1]*o.Reaction[2*i+1]);
                        double u=reaction/m.AnchorMaxLoad[i];
                        if(u>r.Util){r.Util=u;r.Governing=Loc.T("固定支点承载");r.Where=Loc.F("{0}固定支点 #{1}", (Name(L)), (id));r.Why=Loc.F("支反力 {0:0} kN，承载 {1:0} kN", (reaction/1000), (m.AnchorMaxLoad[i]/1000));r.Fix=Loc.T("分散滑轮和液压缸安装位置，或减少配重");return r;}
                    }
            }
            if (r.DriveUtil >= wu)
            {
                if(level.MechanicalLift){r.Governing=Loc.T("液压驱动");r.Where=Name(L);r.Why=Loc.F("液压缸拉压力利用率 {0:0}%", (r.DriveUtil*100));r.Fix=Loc.T("增加液压缸数量或改进平台与配重布置");return r;}
                r.Governing = Loc.T("驱动能力"); r.Where = Loc.F("{0}的驱动机（{1}）", (Name(L)), (drive.Name));
                r.Why = Loc.F("吊索拉力 {0:0} kN − 配重 {1:0} kN = 不平衡力 {2:0} kN，驱动只能拉住 {3:0} kN", (r.RopePull / 1000), (cw / 1000), (r.DriveForce / 1000), (drive.Force / 1000));
                r.Fix = r.RopePull > cw ? Loc.T("选中吊索顶端的滑轮节点，加大配重或换更强的驱动") : Loc.T("配重太重了：选中滑轮节点减小配重，或换更强的驱动");
            }
            else if (worst != null)
            {
                int k = m.MemberIndex[worst.Id];
                r.Governing = Role(L, worst);
                r.Where = Loc.F("{0}的{1} #{2}（{3}）", (Name(L)), (Role(L, worst)), (worst.Id), (m.Mat[k].Name));
                r.Why = st[k].Governing == FailureReason.DeckBendingFailure ? Loc.F("弯矩 {0:0} kN·m，抗弯 {1:0} kN·m", (st[k].Moment / 1000), (m.Mat[k].BendingCapacity / 1000))
                    : st[k].Force >= 0 ? Loc.F("拉力 {0:0} kN，承载 {1:0} kN", (st[k].Force / 1000), (m.Mat[k].TensionCapacity / 1000))
                    : Loc.F("压力 {0:0} kN，{1:0.0} m 长的承压 {2:0} kN", (-st[k].Force / 1000), (m.Length[k]), (m.Mat[k].CompressionCapacity(m.Length[k]) / 1000));
                r.Fix = FixFor(L, worst);
            }
            return r;
        }

        /// <summary>
        /// Axles of a vehicle standing alone at the front stop of the platform (where the simulation parks a single
        /// rider: body front <paramref name="margin"/> before the far end), x along the shaft from X0.
        /// </summary>
        public static (double x, double force)[] VehicleAxles(VehicleDefinition v, TunnelRules.LiftShaft s, double margin,string policy=LiftOperation.Shared)
        {
            double front = Math.Max(0, (v.Length + VehicleBody.RearAxle(v)) / 2);
            double stop = s.X0+LiftOperation.Stop(v,s.X1-s.X0,margin,policy);
            return v.Axles.Select(a => (stop + a.Offset, v.Mass * Math.Max(1,v.Units) * a.LoadFraction * StructuralModel.Gravity)).ToArray();
        }

        /// <summary>Extra lift cost per lift on top of its members: drive, counterweight, lining ties, shaft excavation.</summary>
        public static double MachineCost(LiftDesign lift, LiftLayout L, GameDatabase db)
        {
            if(LiftMechanics.IsMechanical(L))return 0;
            var c = Cfg(db); lift ??= c.Templates.Values.First();
            var drive = c.Drives.GetValueOrDefault(lift.Drive ?? "") ?? c.Drives.Values.First();
            return Math.Round(drive.Cost + lift.Counterweight * c.CounterweightCostPerTonne + L.Ties.Count * c.BraceCost + Math.Max(0, L.Shaft.Height) * c.ShaftCostPerMeter);
        }

        /// <summary>
        /// Ordinary members of a lift template for one shaft (fixed nodes given): a platform deck from deck0 to deck1
        /// with an optional truss below it, a hoist rope from each platform end to a sheave on a column top, and two
        /// columns from the pit bases up to the sheaves along the end walls. Returns (nodes, members) to add.
        /// </summary>
        public static (List<BridgeNode> nodes, List<(int a, int b, string mat)> members) Template(LiftDesign t, TunnelRules.LiftShaft s,
            BridgeNode deck0, BridgeNode deck1, BridgeNode pit0, BridgeNode pit1, GameDatabase db, Func<int> nextId, Func<string, string> allowed)
        {
            var c = Cfg(db); var nodes = new List<BridgeNode>(); var mem = new List<(int, int, string)>();
            BridgeNode Node(double x, double y) { var n = new BridgeNode { Id = nextId(), Position = new Vec3d(Math.Round(x, 3), Math.Round(y, 3)) }; nodes.Add(n); return n; }
            string deck = allowed(t.Deck), web = allowed(t.Web ?? "steel"), diag = allowed(t.Diagonal ?? "cable_composite"), chord = allowed(t.Chord ?? "cable_composite"), hoist = allowed(t.Hoist), col = allowed(t.Column);
            double y = s.Bottom, x0 = s.X0, x1 = s.X1, len = x1 - x0;
            double deckMax = Math.Max(1, Mat(db, deck)?.MaximumLength ?? 5);
            int panels = Math.Max(1, (int)Math.Ceiling(len / Math.Min(Math.Max(1, t.Panel), deckMax) - 1e-9));
            var top = new List<BridgeNode> { deck0 };
            for (int i = 1; i < panels; i++) top.Add(Node(x0 + len * i / panels, y));
            top.Add(deck1);
            for (int i = 0; i < panels; i++) mem.Add((top[i].Id, top[i + 1].Id, deck));
            double depth = Math.Min(t.TrussDepth, c.Pit - 0.4);
            if (depth > 0.3 && panels >= 2)
            {
                // Under-deck truss: bottom chord under the interior panel points, end diagonals up to the platform ends
                var bot = new List<BridgeNode>();
                for (int i = 1; i < panels; i++) bot.Add(Node(top[i].Position.X, y - depth));
                for (int i = 0; i + 1 < bot.Count; i++) mem.Add((bot[i].Id, bot[i + 1].Id, chord));
                for (int i = 0; i < bot.Count; i++) mem.Add((top[i + 1].Id, bot[i].Id, web));
                mem.Add((top[0].Id, bot[0].Id, diag)); mem.Add((top[^1].Id, bot[^1].Id, diag));
                // Pratt layout: diagonals run from the top near a hoist down toward mid-span, so a hanging
                // platform keeps them in tension (cables work); the posts take the compression
                for (int i = 0; i + 1 < bot.Count; i++)
                    mem.Add(i < bot.Count / 2 ? (top[i + 1].Id, bot[i + 1].Id, diag) : (bot[i].Id, top[i + 2].Id, diag));
            }
            // Columns along the end walls from the pit bases up to the sheaves, in segments no longer than the material allows
            // Segments shorter than a cross-beam member (no beam across the road at the upper landing) and short enough
            // to buckle late; each segment node sits against the end wall, where the lining ties it sideways
            double head = HeadY(c, s), colMax = Math.Min(c.ColumnSegment, Math.Max(1, Mat(db, col)?.MaximumLength ?? 8));
            foreach (var (pit, deckEnd) in new[] { (pit0, deck0), (pit1, deck1) })
            {
                double h = head - pit.Position.Y; int segs = Math.Max(1, (int)Math.Ceiling(h / colMax - 1e-9));
                var prevNode = pit;
                for (int k = 1; k <= segs; k++)
                {
                    var n = Node(pit.Position.X, pit.Position.Y + h * k / segs);
                    mem.Add((prevNode.Id, n.Id, col)); prevNode = n;
                }
                mem.Add((deckEnd.Id, prevNode.Id, hoist));   // hoist rope: platform end up to the sheave on the column top
            }
            return (nodes, mem);
        }
    }
}
