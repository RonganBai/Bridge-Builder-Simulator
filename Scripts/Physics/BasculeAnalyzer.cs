using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    /// <summary>
    /// A deck seam: a node where deck members meet from both sides. Every seam has two independent end points
    /// (left key = node id, right key = -node id). While closed the two ends coincide and are joined by a rigid
    /// connection constraint, so the deck looks and behaves as one continuous road. Only the seams a movable leaf
    /// needs are released when the bridge opens. Explicit locks saved in version-2 designs are seams as well.
    /// </summary>
    public class SeamInfo
    {
        public int NodeId;
        public int LeftKey, RightKey;
        public bool Explicit;
        /// <summary>Members attached to the right end point (auto seams only).</summary>
        public HashSet<int> RightMembers = new();
        public double X, Y;
    }

    public class ActuatorPlan
    {
        public int MemberId;
        public int LeafEndKey, FixedEndKey;
    }

    /// <summary>One leaf: rigid body turning about one pivot, driven by one or more hydraulics working together.</summary>
    public class LeafGroupPlan
    {
        public int Id;
        public int PivotKey;
        public HashSet<int> LeafKeys = new();
        public HashSet<int> MemberIds = new();
        public List<ActuatorPlan> Actuators = new();
        public int TipKey;
        public int Direction = 1;
        public double OpenAngleDeg = 70, SpeedDegPerSec = 8;
    }

    public class BasculePlan
    {
        public List<SeamInfo> Seams = new();
        public List<LeafGroupPlan> Groups = new();
        /// <summary>Indices into Seams that are released when the bridge opens.</summary>
        public HashSet<int> CutSeams = new();
        /// <summary>Hydraulic members that do not drive a leaf, with the reason (they stay fixed-length members).</summary>
        public Dictionary<int, string> Rejected = new();
        public bool HasHydraulics;
        private Dictionary<int, int> _seamOfNode = new();

        internal void Index()
        {
            _seamOfNode = new();
            for (int i = 0; i < Seams.Count; i++) if (!Seams[i].Explicit) _seamOfNode[Seams[i].NodeId] = i;
        }

        /// <summary>Graph key of a member end (the right end point of an auto seam for members on its right).</summary>
        public int KeyOf(BridgeMember m, int nodeId) =>
            _seamOfNode.TryGetValue(nodeId, out int s) && Seams[s].RightMembers.Contains(m.Id) ? -nodeId : nodeId;

        /// <summary>
        /// Canonical node identity for a given set of released seams: the two end points of a seam that is NOT
        /// released are the same node (exactly as in the structural model, where such a seam is never split).
        /// </summary>
        public int Canon(int key, ICollection<int> cut)
        {
            for (int i = 0; i < Seams.Count; i++)
                if (Seams[i].RightKey == key && !cut.Contains(i)) return Seams[i].LeftKey;
            return key;
        }

        public int K(BridgeMember m, int nodeId, ICollection<int> cut) => Canon(KeyOf(m, nodeId), cut);

        public LeafGroupPlan GroupOf(int actuatorId) => Groups.FirstOrDefault(g => g.Actuators.Any(a => a.MemberId == actuatorId));

        /// <summary>How to configure the bridge when nothing valid was found (shown to the player), else null.</summary>
        public string Hint()
        {
            if (Groups.Count > 0 || !HasHydraulics) return null;
            string why = Rejected.Count == 0 ? "" : "\n" + string.Join("\n", Rejected.Select(r => Loc.F("液压杆 #{0}：{1}", (r.Key), (r.Value))));
            return Loc.T("没有有效的开启桥配置，液压杆不会动作（不会自动强制收缩）。开启桥的要求：每片桥叶只通过一个转轴节点连着岸上结构；") +
                   Loc.T("液压杆一端接在桥叶上、另一端接在固定结构上；桥叶与对面之间是桥面接缝（桥面接缝会自动分离）。") + why;
        }
    }

    /// <summary>
    /// Finds which hydraulic drives which leaf. Topology first, geometry second: candidate seams are ordered by
    /// how many members away they are from the actuator, then by straight distance, so a deck that merely lies
    /// close by but is not connected is never chosen. All actuators are matched together so two of them cannot
    /// claim the same side; actuators that turn the very same leaf about the same pivot work together.
    /// </summary>
    public static class BasculePlanner
    {
        private const int MaxSeamCandidates = 6;
        private const int MaxOptionsPerActuator = 4;

        private sealed class Graph
        {
            public readonly Dictionary<int, List<(int to, int member)>> Adj = new();
            public void Edge(int a, int b, int member)
            {
                if (!Adj.TryGetValue(a, out var la)) Adj[a] = la = new();
                if (!Adj.TryGetValue(b, out var lb)) Adj[b] = lb = new();
                la.Add((b, member)); lb.Add((a, member));
            }
        }

        private sealed class Option
        {
            public int Actuator, EndKey, OtherKey;
            public int[] Seams;
            public double Score;
        }

        private sealed class Ctx
        {
            public BridgeDesign D; public GameDatabase Db; public BasculePlan P;
            public readonly HashSet<int> Fixed = new();
            public (double x, double y) Pos(int key) { var n = D.FindNode(Math.Abs(key)); return (n.Position.X, n.Position.Y); }

            public Graph Build(ICollection<int> cut, ICollection<int> excluded)
            {
                var g = new Graph();
                foreach (var m in D.Members)
                {
                    if (excluded.Contains(m.Id) || m.DamageState == DamageState.Broken) continue;
                    if (D.FindNode(m.StartNodeId) == null || D.FindNode(m.EndNodeId) == null) continue;
                    g.Edge(P.K(m, m.StartNodeId, cut), P.K(m, m.EndNodeId, cut), m.Id);
                }
                return g;
            }

            public HashSet<int> Reach(Graph g, int start, ICollection<int> removed, out bool hitFixed)
            {
                hitFixed = false;
                var seen = new HashSet<int> { start };
                var q = new Queue<int>(); q.Enqueue(start);
                while (q.Count > 0)
                {
                    int cur = q.Dequeue();
                    if (Fixed.Contains(cur)) { hitFixed = true; continue; }
                    if (!g.Adj.TryGetValue(cur, out var list)) continue;
                    foreach (var (o, _) in list)
                    {
                        if (removed.Contains(o) || !seen.Add(o)) continue;
                        q.Enqueue(o);
                    }
                }
                return seen;
            }

            /// <summary>Leaf of the actuator end in graph g: the part cut off from every support by exactly one node.</summary>
            public bool Leaf(Graph g, int end, int other, out int pivot, out HashSet<int> leaf, out string err)
            {
                pivot = int.MinValue; leaf = null; err = null;
                if (Fixed.Contains(end)) { err = Loc.T("这一端在固定结构上"); return false; }
                var all = Reach(g, end, Array.Empty<int>(), out bool any);
                if (!any) { err = Loc.T("桥叶没有连到任何固定支座"); return false; }
                foreach (int v in all)
                {
                    if (v == end || Fixed.Contains(v)) continue;
                    var set = Reach(g, end, new[] { v }, out bool hit);
                    if (hit) continue;
                    if (leaf == null || set.Count > leaf.Count) { pivot = v; leaf = set; }
                }
                if (leaf == null) { err = Loc.T("桥叶除了一个转轴节点之外还连着固定结构"); return false; }
                if (leaf.Contains(other)) { err = Loc.T("液压杆两端都在桥叶上"); return false; }
                if (other == pivot) { err = Loc.T("液压杆一端在转轴上，没有力臂"); return false; }
                var blocked = new HashSet<int>(leaf) { pivot };
                Reach(g, other, blocked, out bool otherFixed);
                if (!otherFixed) { err = Loc.T("液压杆另一端没有连到固定结构"); return false; }
                return true;
            }
        }

        public static BasculePlan Plan(BridgeDesign d, GameDatabase db, LevelDefinition level = null, ISet<int> excludedDrivers = null)
        {
            d=HydraulicDeckJoints.Expand(d,db);
            var p = new BasculePlan();
            var c = new Ctx { D = d, Db = db, P = p };
            foreach (var n in d.Nodes) if (n.IsAnchor || n.PierBase) c.Fixed.Add(n.Id);
            foreach (var m in d.Members)
                if (db.GetMaterial(m.MaterialId)?.IsColumn == true) { c.Fixed.Add(m.StartNodeId); c.Fixed.Add(m.EndNodeId); }

            // Seams: explicit locks (two saved nodes) and every node where deck members meet from both sides.
            var mech = d.Mechanisms ?? new MechanismData();
            var lockNodes = new HashSet<int>();
            foreach (var lk in mech.Locks)
            {
                var a = d.FindNode(lk.NodeA); var b = d.FindNode(lk.NodeB);
                if (a == null || b == null) continue;
                lockNodes.Add(a.Id); lockNodes.Add(b.Id);
                p.Seams.Add(new SeamInfo { NodeId = a.Id, LeftKey = a.Id, RightKey = b.Id, Explicit = true, X = a.Position.X, Y = a.Position.Y });
            }
            foreach (var n in d.Nodes)
            {
                if (c.Fixed.Contains(n.Id) || n.IsRigidSplit || n.PierPart || lockNodes.Contains(n.Id)) continue;
                var mems = d.Members.Where(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id).ToList();
                if (mems.Any(m => m.Existing)) continue;   // old-bridge joints are not seams
                bool IsDeck(BridgeMember m) => db.GetMaterial(m.MaterialId)?.IsDeck == true;
                double Ox(BridgeMember m) => d.FindNode(m.Other(n.Id))?.Position.X ?? n.Position.X;
                var right = mems.Where(m => Ox(m) > n.Position.X + 1e-6).ToList();
                if (!right.Any(IsDeck) || !mems.Any(m => IsDeck(m) && Ox(m) < n.Position.X - 1e-6)) continue;
                p.Seams.Add(new SeamInfo { NodeId = n.Id, LeftKey = n.Id, RightKey = -n.Id, RightMembers = right.Select(m => m.Id).ToHashSet(), X = n.Position.X, Y = n.Position.Y });
            }
            p.Index();

            var hyd = d.Members.Where(m => db.GetMaterial(m.MaterialId)?.Extendable == true && (excludedDrivers == null || !excludedDrivers.Contains(m.Id)) && m.DamageState != DamageState.Broken
                                           && d.FindNode(m.StartNodeId) != null && d.FindNode(m.EndNodeId) != null).ToList();
            p.HasHydraulics = hyd.Count > 0;
            if (hyd.Count == 0) return p;

            // Include already separated leaves as well as leaves requiring seam releases.
            var options = new Dictionary<int, List<Option>>();
            var allHyd = hyd.Select(x => x.Id).ToHashSet();
            foreach (var h in hyd)
            {
                var list = new List<Option>();
                string firstErr = null;
                foreach (int nodeId in new[] { h.StartNodeId, h.EndNodeId })
                {
                    int end = p.KeyOf(h, nodeId), other = p.KeyOf(h, h.Other(nodeId));
                    if (c.Fixed.Contains(end)) continue;
                    var order = SeamOrder(c, h, end);
                    foreach (var subset in Subsets(order.Count))
                    foreach (var excluded in new[] { new HashSet<int> { h.Id }, allHyd })
                    {
                        var seams = subset.Select(k => order[k].i).ToArray();
                        var g = c.Build(seams, excluded);
                        if (!c.Leaf(g, p.Canon(end, seams), p.Canon(other, seams), out _, out var leaf, out var err)) { firstErr ??= err; continue; }
                        // Only deck-bearing components are bridge leaves, not loose hydraulic linkages.
                        if (!d.Members.Any(m => db.GetMaterial(m.MaterialId)?.IsDeck == true &&
                            (leaf.Contains(p.K(m, m.StartNodeId, seams)) || leaf.Contains(p.K(m, m.EndNodeId, seams))))) continue;
                        if (!seams.All(s => leaf.Contains(p.Seams[s].LeftKey) || leaf.Contains(p.Seams[s].RightKey))) continue;
                        list.Add(new Option { Actuator = h.Id, EndKey = end, OtherKey = other, Seams = seams,
                            Score = seams.Length * 1000 + subset.Sum(k => order[k].hop) * 10 + subset.Sum(k => order[k].dist) * 0.01 });
                    }
                }
                options[h.Id] = list.GroupBy(o => (o.EndKey, Cuts: string.Join(",", o.Seams.OrderBy(x => x))))
                    .Select(g => g.First()).OrderBy(o => o.Score).Take(MaxOptionsPerActuator).ToList();
                if (list.Count == 0) p.Rejected[h.Id] = Diagnose(c, h) ?? firstErr ?? Loc.T("找不到可以转动的桥叶");
            }

            // Prefer independent valid leaves, fewer releases and nearby connections; do not inflate the moving region.
            var acts = hyd.Select(h => h.Id).Where(id => options[id].Count > 0).ToList();
            var choice = new Option[acts.Count];
            var combos = new List<((int count, int size, int seams, double score) e, List<LeafGroupPlan> groups, HashSet<int> cut)>();
            void Search(int i)
            {
                if (i == acts.Count)
                {
                    var chosen = choice.Where(o => o != null).ToList();
                    if (chosen.Count == 0) return;
                    var eval = Evaluate(c, chosen, out var groups, out var cut);
                    if (eval == null) return;
                    combos.Add((eval.Value, groups, cut));
                    return;
                }
                choice[i] = null; Search(i + 1);
                foreach (var o in options[acts[i]]) { choice[i] = o; Search(i + 1); }
                choice[i] = null;
            }
            Search(0);

            // Best combination first; each one is checked on the real structural model (seams released, closed and
            // fully open). An unstable candidate does not switch the hydraulics off: the next combination is tried.
            string unstable = null;
            bool Grounded(int key)
            {
                var seen=new HashSet<int>{key};var queue=new Queue<int>();queue.Enqueue(key);
                while(queue.Count>0)
                {
                    int node=queue.Dequeue();if(c.Fixed.Contains(node))return true;
                    foreach(var member in d.Members)
                    {
                        var material=db.GetMaterial(member.MaterialId);
                        if(member.DamageState==DamageState.Broken || material.IsDeck || material.Extendable)continue;
                        if(member.StartNodeId!=node && member.EndNodeId!=node)continue;
                        int other=member.Other(node);if(seen.Add(other))queue.Enqueue(other);
                    }
                }
                return false;
            }
            // Prefer bank-supported drivers over a larger set of internal hydraulic sub-leaves.
            List<LeafGroupPlan> picked = null; HashSet<int> pickedCut = null; (int, int, int, double) pickedRank = default;
            foreach (var cand in combos.OrderByDescending(x => x.groups.Sum(g=>g.Actuators.Count(a=>Grounded(a.FixedEndKey)))).ThenByDescending(x => x.e.count).ThenBy(x => x.e.seams).ThenBy(x => x.e.score).ThenBy(x => x.e.size))
            {
                int gid = 1, defs = 0, kept = 0;
                foreach (var g in cand.groups)
                {
                    g.Id = gid++;
                    var def = mech.Leaves.FirstOrDefault(l => g.Actuators.Any(a => a.MemberId == l.ActuatorMemberId));
                    if (def != null) { g.OpenAngleDeg = def.OpenAngleDeg; g.SpeedDegPerSec = def.SpeedDegPerSec; defs++; if (def.PivotNodeId == Math.Abs(g.PivotKey)) kept++; }
                }
                var trial = new BasculePlan { Seams = p.Seams, Groups = cand.groups, CutSeams = cand.cut, HasHydraulics = true };
                trial.Index();
                double reach = 1;
                if (level != null && !BasculeSystem.CheckStable(d, level, db, trial, out var why, out reach)) { unstable ??= why; continue; }
                // Navigation clearance is independent of whether a mechanism can move.
                // A blocked channel must not suppress a mechanically valid local leaf.
                // Among stable candidates, one whose hydraulics can really open the leaf wins over one that only
                // matches the ordering above but would stall at 0 deg (e.g. pivoting about the wrong seam).
                // Rank: opens fully > fewer members left in the ship channel > opens further > original order
                int blocks = 0;
                if (level != null) BasculeSystem.ChannelBlocked(d, level, db, trial, out blocks);
                bool full = reach >= 1 - 1e-9;
                // A leaf the player already tuned keeps its pivot, so changing its angle never flips the mechanism
                var rank = (kept, full ? 1 : 0, -blocks, reach);
                if (picked == null || rank.CompareTo(pickedRank) > 0) { picked = cand.groups; pickedCut = cand.cut; pickedRank = rank; }
                if (full && blocks == 0 && kept == defs) break;
            }
            if (picked != null) { p.Groups = picked; p.CutSeams = pickedCut; }
            if (p.Groups.Count == 0 && unstable != null)
                foreach (var id in acts) p.Rejected[id] = Loc.T("没有能正常开启的桥叶组合（已逐一尝试）：") + unstable;
            // Any hydraulic left without a leaf: if one extra member is what locks its leaf, name that member.
            foreach (var h in hyd)
                if (p.GroupOf(h.Id) == null && !p.Rejected.ContainsKey(h.Id) && Diagnose(c, h) is { } brace)
                    p.Rejected[h.Id] = brace;
            foreach (var id in acts)
                if (p.GroupOf(id) == null && !p.Rejected.ContainsKey(id))
                    p.Rejected[id] = Loc.T("与其他液压杆抢同一侧桥叶，或它的固定端在另一片桥叶上；保持为固定长度构件");
            return p;
        }

        private static List<(int i, int hop, double dist)> SeamOrder(Ctx c, BridgeMember h, int end)
        {
            var p = c.P;
            var hops = Hops(c.Build(Array.Empty<int>(), new[] { h.Id }), end);
            var (ex, ey) = c.Pos(end);
            return Enumerable.Range(0, p.Seams.Count)
                .Where(i => hops.ContainsKey(p.Seams[i].LeftKey) || hops.ContainsKey(p.Seams[i].RightKey))
                .Select(i => (i, hop: Math.Min(hops.GetValueOrDefault(p.Seams[i].LeftKey, int.MaxValue), hops.GetValueOrDefault(p.Seams[i].RightKey, int.MaxValue)),
                              dist: Math.Sqrt(Math.Pow(p.Seams[i].X - ex, 2) + Math.Pow(p.Seams[i].Y - ey, 2))))
                .OrderBy(t => t.hop).ThenBy(t => t.dist).Take(MaxSeamCandidates).ToList();
        }

        private static (int count, int size, int seams, double score)? Evaluate(Ctx c, List<Option> chosen, out List<LeafGroupPlan> groups, out HashSet<int> cut)
        {
            groups = new(); cut = new HashSet<int>(chosen.SelectMany(o => o.Seams));
            var g = c.Build(cut, chosen.Select(o => o.Actuator).ToHashSet());
            foreach (var o in chosen)
            {
                int endK = c.P.Canon(o.EndKey, cut), otherK = c.P.Canon(o.OtherKey, cut);
                if (!c.Leaf(g, endK, otherK, out int pivot, out var leaf, out _)) return null;
                if (leaf.Contains(pivot)) return null;   // a node can never be both pivot and moving
                var grp = groups.FirstOrDefault(x => x.PivotKey == pivot && x.LeafKeys.SetEquals(leaf));
                if (grp == null)
                {
                    if (groups.Any(x => x.LeafKeys.Overlaps(leaf) || leaf.Contains(x.PivotKey) || x.LeafKeys.Contains(pivot))) return null;
                    groups.Add(grp = new LeafGroupPlan { PivotKey = pivot, LeafKeys = leaf });
                }
                grp.Actuators.Add(new ActuatorPlan { MemberId = o.Actuator, LeafEndKey = endK, FixedEndKey = otherK });
            }
            var allLeaf = new HashSet<int>(groups.SelectMany(x => x.LeafKeys));
            // Fixed ends of the actuators must not ride on any leaf.
            if (groups.Any(x => x.Actuators.Any(a => allLeaf.Contains(a.FixedEndKey) || a.FixedEndKey == x.PivotKey || !x.LeafKeys.Contains(a.LeafEndKey)))) return null;
            // Every released seam must border a leaf, and nothing outside the leaves may be left without support.
            foreach (int s in cut)
                if (!allLeaf.Contains(c.P.Seams[s].LeftKey) && !allLeaf.Contains(c.P.Seams[s].RightKey)) return null;
            foreach (var key in g.Adj.Keys)
            {
                if (allLeaf.Contains(key) || c.Fixed.Contains(key)) continue;
                c.Reach(g, key, allLeaf, out bool hit);
                if (!hit) return null;
            }
            foreach (var grp in groups)
            {
                var keys = new HashSet<int>(grp.LeafKeys) { grp.PivotKey };
                foreach (var m in c.D.Members)
                {
                    if (grp.Actuators.Any(a => a.MemberId == m.Id)) continue;
                    int a = c.P.K(m, m.StartNodeId, cut), b = c.P.K(m, m.EndNodeId, cut);
                    if (keys.Contains(a) && keys.Contains(b) && (grp.LeafKeys.Contains(a) || grp.LeafKeys.Contains(b))) grp.MemberIds.Add(m.Id);
                    // Any other member (including hydraulics that do not drive) from the leaf to the outside blocks the motion
                    else if (grp.LeafKeys.Contains(a) != grp.LeafKeys.Contains(b) && m.DamageState != DamageState.Broken) return null;
                }
                var (px, py) = c.Pos(grp.PivotKey); double far = -1;
                foreach (int k in grp.LeafKeys)
                {
                    var (x, y) = c.Pos(k); double dd = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                    if (dd > far) { far = dd; grp.TipKey = k; }
                }
                grp.Direction = c.Pos(grp.TipKey).x >= px ? 1 : -1;
            }
            // Size counts real design nodes: the two end points of a released seam are one place, not two
            int size = allLeaf.Select(Math.Abs).Distinct().Count();
            return (groups.Count, size, cut.Count, chosen.Sum(o => o.Score));
        }

        /// <summary>
        /// When no leaf can be freed, looks for one member (typically an extra brace) whose removal would free it,
        /// so the player is told exactly which member locks the movable part.
        /// </summary>
        private static string Diagnose(Ctx c, BridgeMember h)
        {
            var p = c.P;
            // Candidate: a member whose removal frees a leaf. Prefer a brace that runs from the leaf to the fixed
            // side (one end on the leaf, the other off it), then the largest freed leaf.
            (double rank, int size, BridgeMember m, int pivot) best = (double.MinValue, 0, null, 0);
            foreach (int nodeId in new[] { h.StartNodeId, h.EndNodeId })
            {
                int end = p.KeyOf(h, nodeId), other = p.KeyOf(h, h.Other(nodeId));
                if (c.Fixed.Contains(end)) continue;
                var order = SeamOrder(c, h, end);
                foreach (var subset in Subsets(order.Count))
                {
                    var seams = subset.Select(k => order[k].i).ToArray();
                    foreach (var m in c.D.Members)
                    {
                        if (m.Id == h.Id) continue;
                        var mat = c.Db.GetMaterial(m.MaterialId);
                        if (mat == null || mat.IsDeck || mat.Extendable) continue;   // a deck is the road itself, not a brace
                        var g = c.Build(seams, c.D.Members.Where(v=>c.Db.GetMaterial(v.MaterialId).Extendable).Select(v=>v.Id).Append(m.Id).ToArray());
                        if (!c.Leaf(g, p.Canon(end, seams), p.Canon(other, seams), out int pivot, out var leaf, out _)) continue;
                        int a = p.K(m, m.StartNodeId, seams), b = p.K(m, m.EndNodeId, seams);
                        bool ina = leaf.Contains(a), inb = leaf.Contains(b);
                        if (!(ina || inb)) continue;
                        if (!seams.All(s => leaf.Contains(p.Seams[s].LeftKey) || leaf.Contains(p.Seams[s].RightKey))) continue;
                        int outer = ina ? b : a;
                        // Rank: a brace from the leaf to the outside (not a non-driving hydraulic) first, then a pivot close to
                        // the actuator's fixed end (the shore structure it is anchored to), then fewer released seams.
                        int pivotHops = Hops(g, p.Canon(other, seams)).GetValueOrDefault(pivot, 99);
                        double rank = (ina != inb && outer != pivot ? 1e6 : 0) + (mat.Extendable ? 0 : 5e5) - pivotHops * 1e4 + leaf.Count * 10 - seams.Length;
                        if (rank > best.rank) best = (rank, leaf.Count, m, pivot);
                    }
                }
            }
            if (best.m == null) return null;
            var bm = c.Db.GetMaterial(best.m.MaterialId);
            return Loc.F("构件 #{0}（{1}）把活动段锁死了：桥叶除了转轴节点 #{2} 之外还通过它连着固定结构，液压杆不会强行驱动。删除这根构件，或把它改接到桥叶内部", (best.m.Id), (bm.Name), (Math.Abs(best.pivot)));
        }

        private static Dictionary<int, int> Hops(Graph g, int start)
        {
            var d = new Dictionary<int, int> { [start] = 0 };
            var q = new Queue<int>(); q.Enqueue(start);
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (!g.Adj.TryGetValue(cur, out var list)) continue;
                foreach (var (o, _) in list) if (!d.ContainsKey(o)) { d[o] = d[cur] + 1; q.Enqueue(o); }
            }
            return d;
        }

        /// <summary>Try no release first: a free deck end already defines a movable leaf.</summary>
        private static IEnumerable<int[]> Subsets(int n)
        {
            yield return Array.Empty<int>();
            for (int i = 0; i < n; i++) yield return new[] { i };
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) yield return new[] { i, j };
        }
    }
}

