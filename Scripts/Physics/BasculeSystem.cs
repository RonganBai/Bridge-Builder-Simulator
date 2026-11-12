using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Traffic;

namespace BridgeArchitect3D.Physics
{
    /// <summary>
    /// Operating states of a movable bridge. The order follows the real procedure:
    /// closed &amp; locked → stop traffic and wait until the movable span is empty → unlock → open → hold open
    /// (ships pass) → close → check alignment and lock → traffic resumes.
    /// </summary>
    public enum BridgeOpState { Closed, StoppingTraffic, Unlocking, Opening, Open, Closing, Locking, Blocked }

    /// <summary>One hydraulic driving a leaf (indices refer to the structural model).</summary>
    public class ActuatorRuntime
    {
        public int MemberId, Member, LeafNode, FixedNode;
        public double L0, LMin, LMax;  // as built / shortest / longest
        public double ForceLimit;      // push / pull limit, N
        public double Length, Force;   // current length; last solved axial force (tension positive)
        public double ReachAngle;      // largest angle this actuator's stroke allows
    }

    /// <summary>Runtime data of one leaf.</summary>
    public class LeafRuntime
    {
        public LeafGroupPlan Plan;
        public int Id => Plan.Id;
        public int Pivot, Tip;
        public int[] Nodes = Array.Empty<int>();
        public int[] Members = Array.Empty<int>();
        public readonly List<ActuatorRuntime> Actuators = new();
        public double OpenAngleDeg => Plan.OpenAngleDeg;
        public double Speed => Plan.SpeedDegPerSec;
        public int Direction => Plan.Direction;
        public double PreviousAngle;
        public double Angle;           // current opening angle, degrees
        public double ReachAngle;      // smallest reach of all its actuators (≤ target means a stroke fault)
        public bool Stalled;
    }

    /// <summary>
    /// Bascule (drawbridge) mechanism. Leaves rotate as rigid bodies about their pivots, so the motion is exact
    /// (no member stretching); the structural solver then runs on the moved geometry to obtain member and
    /// actuator forces. Every actuator moves only within its stroke and below its force limit, also when several
    /// actuators drive one leaf together. Only the seams the leaves need are released; leaf-internal joints stay
    /// intact and the pivots keep supporting the leaves.
    /// </summary>
    public class BasculeSystem : IBridgeMechanism
    {
        public const double LockTime = 0.8;          // seconds to release / engage the seam connections
        public const double AlignTolerance = 0.02;   // m, seam end points must coincide within this to lock
        public const double StopMargin = 1.0;        // m between the stopped vehicle's front and the movable span

        public string Id => "bascule";
        public BasculePlan Plan { get; private set; } = new();
        public readonly List<LeafRuntime> Leaves = new();
        /// <summary>Released seams (model index pairs): rigidly connected while locked, free while open.</summary>
        public readonly List<(int a, int b)> Locks = new();
        /// <summary>Configuration problems: the bridge then never drives (explicit message instead).</summary>
        public readonly List<string> Errors = new();
        public LinkageSystem Linkage { get; private set; }
        private double _linkageDuration=4;
        public const double MaximumLinkageNodeSpeed = 3.0;
        private BasculeSystem _automatic;
        private string _configurationWarning;
        public string ConfigurationWarning => _configurationWarning;
        public bool Active => (Leaves.Count > 0 || Linkage?.Ready == true || _automatic?.Active == true) && Errors.Count == 0;
        public BridgeOpState State { get; private set; } = BridgeOpState.Closed;
        public bool Locked { get; private set; } = true;
        /// <summary>Latest operating problem (stroke / force / alignment / configuration), shown to the player.</summary>
        private string _fault;
        public string Fault { get => _fault ?? _automatic?.Fault; private set => _fault=value; }
        public string StatusText { get; private set; } = "";

        private StructuralModel _m;
        private double _stateTime;
        private readonly Dictionary<RoadPath, (double s0, double s1)> _zone = new();
        private bool[] _leafNode = Array.Empty<bool>();

        public (double s0, double s1)? ZoneOn(RoadPath p)
        {
            var own=p!=null && _zone.TryGetValue(p,out var z)?((double s0,double s1)?)z:null;
            var other=_automatic?.ZoneOn(p);
            return own.HasValue && other.HasValue ? (Math.Min(own.Value.s0,other.Value.s0),Math.Max(own.Value.s1,other.Value.s1)) : own ?? other;
        }
        public double RequiredOpeningTime => 2 * LockTime + 2 * BridgeSimulation.FixedDt + Math.Max(
            Linkage?.Ready == true ? _linkageDuration * (1-Linkage.Progress) : Leaves.Select(l=>Math.Max(0,l.OpenAngleDeg-l.Angle)/Math.Max(0.1,l.Speed)).DefaultIfEmpty(0).Max(),
            _automatic?.RequiredOpeningTime ?? 0);
        public bool IsLeafNode(int nodeIndex) => (Linkage?.Ready == true && Linkage.IsMovingNode(nodeIndex)) || _automatic?.IsLeafNode(nodeIndex)==true || (nodeIndex>=0 && nodeIndex<_leafNode.Length && _leafNode[nodeIndex]);
        public bool TrafficStopped => (Active && State != BridgeOpState.Closed) || _automatic?.TrafficStopped==true;
        public bool IsOpen => Active && (Linkage?.Ready!=true && _automatic!=null || State==BridgeOpState.Open) && (_automatic==null || !_automatic.Active || _automatic.IsOpen);
        public bool ClosedAndLocked => (!Active || (State==BridgeOpState.Closed && Locked)) && (_automatic?.ClosedAndLocked ?? true);

        /// <summary>
        /// Plans the mechanism from the design and splits the released seams in the model into two coincident
        /// end points. Must run before road paths and result arrays are built (the node count grows).
        /// Seams that are not released stay single nodes; explicit locks that are not released stay rigidly joined.
        /// </summary>
        public void Prepare(StructuralModel m, BridgeDesign design, GameDatabase db, LevelDefinition level)
            {
            design=HydraulicDeckJoints.Expand(design,db);
            if ((design.Mechanisms?.Linkage?.Drivers.Count ?? 0)>0)
            {
                var settings=design.Mechanisms.Linkage;
                _linkageDuration=settings.Duration;
                _m=m;
                var selected=settings.Drivers.Select(d=>d.MemberId).ToHashSet();
                bool TrySettings(LinkageData candidate)
                {
                    Linkage?.Deactivate();
                    Linkage=new LinkageSystem();
                    return double.IsFinite(_linkageDuration) && _linkageDuration>0 &&
                        Linkage.Initialize(m,candidate) && Linkage.RestrictToDrivenMotion();
                }
                bool valid=TrySettings(settings);
                string initialFailure=Linkage.Fault;
                // Retain a valid coordinated subset when an unrelated selected actuator is locked.
                // The full set is always tried first, so genuinely cooperative drivers stay together.
                if(!valid && settings.Drivers.Count>1 && settings.Drivers.Count<=8)
                {
                    var requested=settings;
                    foreach(int mask in Enumerable.Range(1,(1<<requested.Drivers.Count)-2)
                        .OrderByDescending(mask=>Enumerable.Range(0,requested.Drivers.Count).Count(bit=>(mask&(1<<bit))!=0)))
                    {
                        var candidate=new LinkageData { Duration=requested.Duration,
                            Drivers=requested.Drivers.Where((d,index)=>(mask&(1<<index))!=0).Select(d=>d.Clone()).ToList() };
                        if(!TrySettings(candidate)) continue;
                        settings=candidate; valid=true;
                        var accepted=candidate.Drivers.Select(d=>d.MemberId).ToHashSet();
                        _configurationWarning=Loc.T("部分所选驱动无法同时动作：#")+string.Join(Loc.T("、#"),selected.Where(id=>!accepted.Contains(id)))+Loc.T("；其余有效驱动继续工作。原因：")+initialFailure;
                        break;
                    }
                }
                if(!valid)
                {
                    _configurationWarning=Loc.T("所选通用驱动暂不动作：")+(Linkage.Fault ?? Loc.T("联动时间无效"))+Loc.T("；其他有效液压杆继续自动开桥。");
                    Linkage.Deactivate(); Linkage=null;
                    PrepareWith(m,BasculePlanner.Plan(design,db,level,selected));
                    return;
                }
                var automaticPlan=BasculePlanner.Plan(design,db,level,selected);
                automaticPlan.Groups=automaticPlan.Groups.Where(g=>Linkage.CanRunAlongside(g)).ToList();
                automaticPlan.CutSeams.RemoveWhere(i=>!automaticPlan.Groups.Any(g=>
                    g.LeafKeys.Contains(automaticPlan.Seams[i].LeftKey) || g.LeafKeys.Contains(automaticPlan.Seams[i].RightKey)));
                Linkage.Deactivate();
                if(automaticPlan.Groups.Count>0)
                {
                    _automatic=new BasculeSystem();
                    _automatic.PrepareWith(m,automaticPlan);
                }
                Plan=new BasculePlan { HasHydraulics=true };
                var childLocks=_automatic==null?new HashSet<(int,int)>():_automatic.Locks.ToHashSet();
                foreach(var joint in design.Mechanisms.Locks)
                    if(m.NodeIndex.TryGetValue(joint.NodeA,out int a) && m.NodeIndex.TryGetValue(joint.NodeB,out int c) && !childLocks.Contains((a,c))) Locks.Add((a,c));
                Linkage=new LinkageSystem();
                if(!Linkage.Initialize(m,settings) || !Linkage.RestrictToDrivenMotion())
                {
                    _configurationWarning=Loc.T("通用驱动无法与其他机构同时运动：")+Linkage.Fault;
                    Linkage.Deactivate(); Linkage=null;
                }
                return;
            }
            PrepareWith(m, BasculePlanner.Plan(design, db, level));
            if (Plan.Groups.Count > 0) NavigationNote = ChannelBlocked(design, level, db, Plan);
        }

        /// <summary>Splits exactly the released seams; a seam that is not released stays one model node.</summary>
        public void PrepareWith(StructuralModel m, BasculePlan plan)
        {
            _m = m;
            Plan = plan;
            for (int s = 0; s < Plan.Seams.Count; s++)
            {
                var seam = Plan.Seams[s];
                if (!m.NodeIndex.TryGetValue(seam.LeftKey, out int a)) continue;
                int b;
                if (seam.Explicit)
                {
                    if (!m.NodeIndex.TryGetValue(seam.RightKey, out b)) continue;
                    if (Math.Abs(m.X[a] - m.X[b]) > AlignTolerance || Math.Abs(m.Y[a] - m.Y[b]) > AlignTolerance) { Errors.Add(Loc.F("接缝节点 #{0} / #{1} 没有对齐", (seam.LeftKey), (seam.RightKey))); continue; }
                }
                else
                {
                    if (!Plan.CutSeams.Contains(s)) continue;
                    var moved = seam.RightMembers.Where(m.MemberIndex.ContainsKey).Select(id => m.MemberIndex[id]).ToList();
                    b = m.SplitNode(a, moved);
                }
                if (Plan.CutSeams.Contains(s)) Locks.Add((a, b));
                else { m.Alias[b] = a; m.AliasRot[b] = true; }   // permanently joined explicit lock
            }
        }

        public void Initialize(IEnumerable<RoadPath> paths)
        {
            var m = _m;
            if(Linkage != null || _automatic != null)
            {
                _automatic?.Initialize(paths);
                ConfigNote=_configurationWarning;
                ApplyLocks(true);
                foreach(var path in paths)
                {
                    double start=double.MaxValue,end=double.MinValue;
                    for(int seg=0;seg<path.SegmentMember.Count;seg++)
                    {
                        int k=path.SegmentMember[seg];
                        if(k<0 || !(IsLeafNode(m.A[k]) || IsLeafNode(m.B[k]))) continue;
                        start=Math.Min(start,path.SegmentStartS[seg]);
                        end=Math.Max(end,seg+1<path.SegmentStartS.Count?path.SegmentStartS[seg+1]:path.TotalLength);
                    }
                    if(start<end) _zone[path]=(start,end);
                }
                UpdateStatus(); return;
            }
            _leafNode = new bool[m.NodeCount];
            // A right end point that was not split (seam not released) is the original node itself
            // Planner keys are canonical: a negative key exists only for a seam that was really split
            int Idx(int key) => m.NodeIndex.TryGetValue(key, out int i) ? i : -1;
            foreach (var g in Plan.Groups)
            {
                var lr = new LeafRuntime { Plan = g, Pivot = Idx(g.PivotKey), Tip = Idx(g.TipKey) };
                lr.Nodes = g.LeafKeys.Select(Idx).Where(i => i >= 0).ToArray();
                lr.Members = g.MemberIds.Where(m.MemberIndex.ContainsKey).Select(id => m.MemberIndex[id]).ToArray();
                if (lr.Pivot < 0 || lr.Nodes.Length == 0) { Errors.Add(Loc.F("桥叶 #{0} 数据无效", (g.Id))); continue; }
                foreach (var ap in g.Actuators)
                {
                    if (!m.MemberIndex.TryGetValue(ap.MemberId, out int k)) continue;
                    var mat = m.Mat[k];
                    var ar = new ActuatorRuntime
                    {
                        MemberId = ap.MemberId, Member = k, LeafNode = Idx(ap.LeafEndKey), FixedNode = Idx(ap.FixedEndKey),
                        L0 = m.Length[k], ForceLimit = mat.ActuatorForce,
                    };
                    // Every driving hydraulic both retracts and extends: limits are its stroke envelope (< 100%),
                    // so it never ends up as a fixed-length rod just because the leaf needs the other direction.
                    ar.LMin = ar.L0 * (1 - Math.Min(mat.RetractRatio, LinkageDriverData.MaxStroke)); ar.LMax = ar.L0 * (1 + Math.Min(mat.ExtendRatio, LinkageDriverData.MaxStroke)); ar.Length = ar.L0;
                    lr.Actuators.Add(ar);
                }
                string bad = VerifyLeaf(lr, g);
                if (bad != null) { Errors.Add(Loc.F("桥叶 #{0}：{1}", (g.Id), (bad))); continue; }
                foreach (int i in lr.Nodes) _leafNode[i] = true;
                foreach (int k in lr.Members)
                {
                    if (m.A[k] == lr.Pivot) m.ReleaseA[k] = true;   // the leaf is hinged at its pivot
                    if (m.B[k] == lr.Pivot) m.ReleaseB[k] = true;
                }
                foreach (var ar in lr.Actuators) ar.ReachAngle = ReachableAngle(lr, ar);
                lr.ReachAngle = lr.Actuators.Count == 0 ? 0 : lr.Actuators.Min(a => a.ReachAngle);
                Leaves.Add(lr);
            }
            // A brace that locks a movable part: that hydraulic does not drive (named to the player); other valid
            // leaves still work.
            var lockedBy = Plan.Rejected.Where(r => r.Value.Contains(Loc.T("锁死"))).Select(r => Loc.F("液压杆 #{0} 不驱动：{1}", (r.Key), (r.Value))).ToList();
            ConfigNote = string.Join(Loc.T("；"), lockedBy.Concat(NavigationNote == null ? Array.Empty<string>() : new[] { Loc.T("净空提示（不阻止机构运动）：") + NavigationNote }).Concat(_configurationWarning==null?Array.Empty<string>():new[]{_configurationWarning}));
            if (ConfigNote.Length == 0) ConfigNote = null;
            if (Errors.Count > 0) { Fault = string.Join(Loc.T("；"), Errors); Leaves.Clear(); }
            ApplyLocks(true);
            foreach (var p in paths)
            {
                double s0 = double.MaxValue, s1 = double.MinValue;
                for (int seg = 0; seg < p.SegmentMember.Count; seg++)
                {
                    int k = p.SegmentMember[seg];
                    if (k < 0 || !Leaves.Any(l => l.Members.Contains(k))) continue;
                    double end = seg + 1 < p.SegmentStartS.Count ? p.SegmentStartS[seg + 1] : p.TotalLength;
                    s0 = Math.Min(s0, p.SegmentStartS[seg]); s1 = Math.Max(s1, end);
                }
                if (s0 < s1) _zone[p] = (s0, s1);
            }
            UpdateStatus();
        }

        public void PreSolve(StructuralModel model, double time, double dt) { }

        /// <summary>Configuration notes that do not stop the mechanism (e.g. a hydraulic locked by a brace).</summary>
        public string ConfigNote { get; private set; }
        public string NavigationNote { get; private set; }

        /// <summary>
        /// Cross-checks the planner result against the real model: unique leaf nodes, the pivot is not a moving
        /// node, every actuator runs from a leaf node to a node off the leaf, and no other member (including
        /// hydraulics that do not drive) ties the leaf to the outside.
        /// </summary>
        private string VerifyLeaf(LeafRuntime lr, LeafGroupPlan g)
        {
            var set = new HashSet<int>(lr.Nodes);
            if (set.Count != lr.Nodes.Length || lr.Nodes.Length != g.LeafKeys.Count) return Loc.T("桥叶节点映射重复或缺失");
            if (set.Contains(lr.Pivot)) return Loc.F("节点 #{0} 同时是转轴和活动节点", (_m.NodeIds[lr.Pivot]));
            if (lr.Actuators.Count != g.Actuators.Count) return Loc.T("驱动液压杆不存在");
            foreach (var ar in lr.Actuators)
            {
                int k = ar.Member;
                bool ends = (_m.A[k] == ar.LeafNode && _m.B[k] == ar.FixedNode) || (_m.B[k] == ar.LeafNode && _m.A[k] == ar.FixedNode);
                if (!ends) return Loc.F("液压杆 #{0} 的端点与识别结果不一致", (ar.MemberId));
                if (!set.Contains(ar.LeafNode)) return Loc.F("液压杆 #{0} 的活动端不在桥叶上", (ar.MemberId));
                if (set.Contains(ar.FixedNode) || ar.FixedNode == lr.Pivot) return Loc.F("液压杆 #{0} 的固定端在桥叶或转轴上", (ar.MemberId));
            }
            var own = new HashSet<int>(lr.Members); foreach (var ar in lr.Actuators) own.Add(ar.Member);
            for (int k = 0; k < _m.MemberCount; k++)
            {
                if (own.Contains(k) || _m.Broken[k]) continue;
                bool a = set.Contains(_m.A[k]), b = set.Contains(_m.B[k]);
                if (a != b) return Loc.F("构件 #{0}（{1}）把桥叶连到桥叶外，会阻碍转动", (_m.MemberIds[k]), (_m.Mat[k].Name));
                if (a && b) return Loc.F("构件 #{0} 在桥叶内部却没有随桥叶转动", (_m.MemberIds[k]));
            }
            return null;
        }

        /// <summary>
        /// Real structural check of a candidate plan: build the model, split the released seams, release them and
        /// solve self-weight closed and fully open. Used by the planner to reject a candidate and try the next one.
        /// </summary>
        /// <summary>
        /// With every leaf at its target angle (geometric intent, stroke not applied), does any member still cut a
        /// ship envelope (member thickness included)? Returns the reason, or null when the channel is clear or the
        /// level has no ships. A plan that cannot open the channel is not a valid "head" of a movable bridge.
        /// </summary>
        public static string ChannelBlocked(BridgeDesign d, LevelDefinition level, GameDatabase db, BasculePlan plan)
            => ChannelBlocked(d, level, db, plan, out _);

        /// <summary>Same check; also counts every member still in a ship envelope at the target angles.</summary>
        public static string ChannelBlocked(BridgeDesign d, LevelDefinition level, GameDatabase db, BasculePlan plan, out int blocking)
        {
            blocking = 0; string first = null;
            var ships = level.TrafficEvents.Where(e => e.Type == TrafficEventType.Ship).ToList();
            if (ships.Count == 0) return null;
            var m = StructuralModel.Build(d, level, db);
            var sys = new BasculeSystem();
            sys.PrepareWith(m, plan);
            sys.Initialize(Array.Empty<RoadPath>());
            if (sys.Leaves.Count == 0) { blocking = int.MaxValue; return Loc.T("没有可开启的桥叶"); }
            foreach (var l in sys.Leaves) l.Angle = l.OpenAngleDeg;
            sys.ApplyGeometry(null);
            foreach (var s in ships)
            {
                double x0 = s.LaneX - s.Width / 2, x1 = s.LaneX + s.Width / 2, y0 = level.Terrain.WaterLevel, y1 = y0 + s.Height;
                for (int k = 0; k < m.MemberCount; k++)
                {
                    if (m.Broken[k]) continue;
                    double h = m.Mat[k].VisualThickness / 2;
                    double offset=m.Mat[k].IsDeck?0.2-h:0;
                    if (ShipGeometry.Parts(s).Any(part=>ShipGeometry.Intersects(s,level.Terrain.WaterLevel,-part.Z,m.X[m.A[k]],m.Y[m.A[k]]+offset,m.X[m.B[k]],m.Y[m.B[k]]+offset,0,2*h,0)))
                    {
                        blocking++;
                        first ??= Loc.F("桥叶开到目标角度后构件 #{0}（{1}）仍挡住航道", (m.MemberIds[k]), (m.Mat[k].Name));
                    }
                }
            }
            return first;
        }

        private static bool HitsRect(double ax, double ay, double bx, double by, double x0, double y0, double x1, double y1)
        {
            double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
            bool Clip(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;
                double r = q / p;
                if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
                return true;
            }
            return Clip(-dx, ax - x0) && Clip(dx, x1 - ax) && Clip(-dy, ay - y0) && Clip(dy, y1 - ay) && t0 <= t1;
        }

        public static bool CheckStable(BridgeDesign d, LevelDefinition level, GameDatabase db, BasculePlan plan, out string why)
            => CheckStable(d, level, db, plan, out why, out _);

        /// <summary>Same check; also reports how far the worst leaf can open, as a fraction of its target angle.</summary>
        public static bool CheckStable(BridgeDesign d, LevelDefinition level, GameDatabase db, BasculePlan plan, out string why, out double reach)
        {
            why = null; reach = 0;
            var m = StructuralModel.Build(d, level, db);
            var sys = new BasculeSystem();
            sys.PrepareWith(m, plan);
            sys.Initialize(Array.Empty<RoadPath>());
            if (sys.Errors.Count > 0 || sys.Leaves.Count != plan.Groups.Count) { why = string.Join(Loc.T("；"), sys.Errors); return false; }
            reach = sys.Leaves.Count == 0 ? 0 : sys.Leaves.Min(l => Math.Min(1, l.ReachAngle / Math.Max(1e-6, l.OpenAngleDeg)));
            var solver = new StructureSolver();
            var o = new SolveOutput(); o.EnsureSize(m.NodeCount, m.MemberCount);
            var loads = new double[m.NodeCount * 2];
            sys.ApplyLocks(false);
            // Sample the entire trajectory, including intermediate singular configurations.
            for (int sample = 0; sample <= 14; sample++)
            {
                foreach (var l in sys.Leaves) l.Angle = Math.Min(l.OpenAngleDeg, l.ReachAngle) * sample / 14.0;
                sys.ApplyGeometry(null);
                Array.Clear(loads, 0, loads.Length);
                LoadCalculator.Compute(m, Array.Empty<AxleLoad>(), 0, loads);
                solver.Solve(m, loads, o);
                if (!o.Stable) { why = Loc.F("{0}后节点 #{1} 附近缺少约束", ((sample > 0 ? Loc.T("开启途中") : Loc.T("解除接缝"))), (Math.Abs(o.UnstableNodeId))); return false; }
            }
            return true;
        }

        /// <summary>Releases (true) or restores (false) the seams, for the stability check before the first opening.</summary>
        public void TrialRelease(bool released) { ApplyLocks(!released); _automatic?.TrialRelease(released); }

        /// <summary>Turns the mechanism off with an explicit reason; the structure then stays exactly as built.</summary>
        public void Disable(string reason)
        {
            Linkage?.Deactivate();
            _automatic?.Disable(reason);
            Errors.Add(reason);
            Fault = string.Join(Loc.T("；"), Errors);
            foreach (var l in Leaves)
                foreach (int k in l.Members) { _m.ReleaseA[k] = false; _m.ReleaseB[k] = false; }
            Leaves.Clear();
            _zone.Clear();
            Array.Clear(_leafNode, 0, _leafNode.Length);
            ApplyLocks(true);
            UpdateStatus();
        }

        /// <summary>Position of a leaf node rotated by the leaf angle (exact rigid rotation about the pivot).</summary>
        private (double x, double y) Rotated(LeafRuntime l, int node, double angleDeg)
        {
            double a = l.Direction * angleDeg * Math.PI / 180;
            double px = _m.X0[l.Pivot], py = _m.Y0[l.Pivot];
            double dx = _m.X0[node] - px, dy = _m.Y0[node] - py;
            return (px + dx * Math.Cos(a) - dy * Math.Sin(a), py + dx * Math.Sin(a) + dy * Math.Cos(a));
        }

        public double ActuatorLengthAt(LeafRuntime l, ActuatorRuntime ar, double angleDeg)
        {
            var (x, y) = Rotated(l, ar.LeafNode, angleDeg);
            double dx = x - _m.X0[ar.FixedNode], dy = y - _m.Y0[ar.FixedNode];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private double ReachableAngle(LeafRuntime l, ActuatorRuntime ar)
        {
            double reach = 0;
            for (double a = 0.25; a <= l.OpenAngleDeg + 1e-9; a += 0.25)
            {
                double len = ActuatorLengthAt(l, ar, a);
                if (len < ar.LMin - 1e-9 || len > ar.LMax + 1e-9) break;
                reach = a;
            }
            return reach;
        }

        internal void ApplyLocks(bool locked)
        {
            Locked = locked;
            foreach (var (a, b) in Locks) { _m.Alias[b] = locked ? a : -1; _m.AliasRot[b] = locked; }
        }

        /// <summary>Moves leaf nodes to the current angles and refreshes the affected member geometry.</summary>
        public void ApplyGeometry(double[] kinematic)
        {
            if(Linkage?.Ready==true || _automatic!=null)
            {
                _automatic?.ApplyGeometry(null);
                if(kinematic!=null) for(int i=0;i<_m.NodeCount;i++) {kinematic[2*i]=_m.X[i]-_m.X0[i];kinematic[2*i+1]=_m.Y[i]-_m.Y0[i];}
                return;
            }
            foreach (var l in Leaves)
            {
                foreach (int i in l.Nodes)
                {
                    var (x, y) = Rotated(l, i, l.Angle);
                    _m.X[i] = x; _m.Y[i] = y;
                }
                foreach (int k in l.Members) _m.UpdateMemberGeometry(k);
                foreach (var ar in l.Actuators) { _m.UpdateMemberGeometry(ar.Member); ar.Length = _m.Length[ar.Member]; }
            }
            if (kinematic != null)
                for (int i = 0; i < _m.NodeCount; i++) { kinematic[2 * i] = _m.X[i] - _m.X0[i]; kinematic[2 * i + 1] = _m.Y[i] - _m.Y0[i]; }
        }

        /// <param name="wantOpen">a ship needs the channel</param>
        /// <param name="spanClear">no vehicle body overlaps the movable span</param>
        public void Update(double dt, bool wantOpen, bool spanClear)
        {
            if (!Active) return;
            if(Linkage?.Ready==true || _automatic!=null)
            {
                _automatic?.Update(dt,wantOpen,spanClear); _automatic?.ApplyGeometry(null);
                if(Linkage?.Ready==true) UpdateLinkage(dt,wantOpen,spanClear); else UpdateStatus();
                return;
            }
            _stateTime += dt;
            switch (State)
            {
                case BridgeOpState.Closed:
                    if (wantOpen) Go(BridgeOpState.StoppingTraffic);
                    break;
                case BridgeOpState.StoppingTraffic:
                    if (!wantOpen) Go(BridgeOpState.Closed);
                    else if (spanClear) Go(BridgeOpState.Unlocking);
                    break;
                case BridgeOpState.Unlocking:
                    if (_stateTime >= LockTime) { ApplyLocks(false); Go(BridgeOpState.Opening); }
                    break;
                case BridgeOpState.Opening:
                {
                    foreach (var l in Leaves) l.PreviousAngle = l.Angle;
                    bool done = true;
                    foreach (var l in Leaves)
                    {
                        double target = Math.Min(l.OpenAngleDeg, l.ReachAngle);
                        if (l.Stalled || l.Angle >= target - 1e-9) continue;
                        l.Angle = Math.Min(target, l.Angle + l.Speed * dt);
                        done = false;
                    }
                    if (done)
                    {
                        foreach (var l in Leaves.Where(l => l.ReachAngle < l.OpenAngleDeg - 1e-6))
                        {
                            var ar = l.Actuators.OrderBy(a => a.ReachAngle).First();
                            SetFault(Loc.F("桥叶 #{0} 液压杆 #{1} 行程不足：最多只能开到 {2:0}°（目标 {3:0}°，液压杆可在 {4:0.00}~{5:0.00} m 之间伸缩）", (l.Id), (ar.MemberId), (ar.ReachAngle), (l.OpenAngleDeg), (ar.LMin), (ar.LMax)));
                        }
                        Go(Leaves.Any(l => l.Stalled || l.Angle < l.OpenAngleDeg - 1e-6)
                            ? BridgeOpState.Blocked : BridgeOpState.Open);
                    }
                    break;
                }
                case BridgeOpState.Blocked:
                    // Keep traffic stopped and the ship waiting; a limited opening is not fully open.
                    if (!wantOpen) { foreach (var l in Leaves) l.Stalled = false; Go(BridgeOpState.Closing); }
                    break;
                case BridgeOpState.Open:
                    if (!wantOpen) { foreach (var l in Leaves) l.Stalled = false; Go(BridgeOpState.Closing); }
                    break;
                case BridgeOpState.Closing:
                {
                    foreach (var l in Leaves) l.PreviousAngle = l.Angle;
                    bool done = true;
                    foreach (var l in Leaves)
                    {
                        if (l.Angle <= 1e-9) { l.Angle = 0; continue; }
                        if (l.Stalled) { Go(BridgeOpState.Blocked); done = false; continue; }
                        l.Angle = Math.Max(0, l.Angle - l.Speed * dt);
                        done = false;
                    }
                    if (done)
                    {
                        string bad = AlignmentProblem();
                        if (bad != null) SetFault(bad); else Go(BridgeOpState.Locking);
                    }
                    break;
                }
                case BridgeOpState.Locking:
                    if (_stateTime >= LockTime) { ApplyLocks(true); Go(BridgeOpState.Closed); }
                    break;
            }
            UpdateStatus();
        }

        private string AlignmentProblem()
        {
            foreach (var (a, b) in Locks)
            {
                double d = Math.Sqrt(Math.Pow(_m.X[a] - _m.X[b], 2) + Math.Pow(_m.Y[a] - _m.Y[b], 2));
                if (d > AlignTolerance) return Loc.F("闭合偏差 {0:0.00} m：接缝两侧没有对齐，无法恢复连接", (d));
            }
            return null;
        }

        /// <summary>
        /// Checks the solved forces of every driving actuator. If one exceeds its force limit while moving, the
        /// step is undone and the whole leaf stalls (returns true: geometry changed, solve again).
        /// </summary>
        public bool CheckActuators(SolveOutput o, double dt)
        {
            bool otherChanged=_automatic?.CheckActuators(o,dt) ?? false;
            if(otherChanged) return true;
            if(Linkage?.Ready==true && Linkage.PendingForceCheck)
            {
                if(Linkage.CheckForces(o)) return otherChanged;
                Fault=Linkage.Fault; Go(BridgeOpState.Blocked); UpdateStatus(); return true;
            }
            bool changed = false;
            bool moving = State == BridgeOpState.Opening || State == BridgeOpState.Closing;
            foreach (var l in Leaves)
            {
                foreach (var ar in l.Actuators) ar.Force = o.AxialForce[ar.Member];
                if (!moving || l.Stalled) continue;
                var over = l.Actuators.FirstOrDefault(a => Math.Abs(a.Force) > a.ForceLimit);
                if (over == null) continue;
                // Restore the actual preceding angle, including a shortened last step.
                l.Angle = l.PreviousAngle;
                l.Stalled = true; changed = true;
                SetFault(Loc.F("桥叶 #{0} 液压杆 #{1} {2}不足：需要 {3:0.0} 吨，最大 {4:0.0} 吨（停在 {5:0}°）。可以减轻桥叶、加大力臂或增加一根共同驱动的液压杆", (l.Id), (over.MemberId), ((over.Force > 0 ? Loc.T("拉力") : Loc.T("推力"))), (Math.Abs(over.Force) / 9810), (over.ForceLimit / 9810), (l.Angle)));
            }
            return changed || otherChanged;
        }

        private void SetFault(string text) { if (Fault == null) Fault = text; }

        private void Go(BridgeOpState s) { State = s; _stateTime = 0; }

        private void UpdateLinkage(double dt,bool wantOpen,bool spanClear)
        {
            _stateTime+=dt;
            switch(State)
            {
                case BridgeOpState.Closed: if(wantOpen) Go(BridgeOpState.StoppingTraffic); break;
                case BridgeOpState.StoppingTraffic:
                    if(!wantOpen) Go(BridgeOpState.Closed); else if(spanClear) Go(BridgeOpState.Unlocking); break;
                case BridgeOpState.Unlocking:
                    if(_stateTime>=LockTime) { ApplyLocks(false); Go(BridgeOpState.Opening); } break;
                case BridgeOpState.Opening:
                case BridgeOpState.Closing:
                    bool opening=State==BridgeOpState.Opening;
                    double target=Math.Clamp(Linkage.Progress+(opening?1:-1)*dt/_linkageDuration,0,1);
                    if(!Linkage.TryMoveLimited(target,MaximumLinkageNodeSpeed * dt)) { Fault=Linkage.Fault; Go(BridgeOpState.Blocked); break; }
                    if(opening && Linkage.Progress>=1) Go(BridgeOpState.Open);
                    if(!opening && Linkage.Progress<=0)
                    {
                        string bad=AlignmentProblem();
                        if(bad!=null) { Fault=bad; Go(BridgeOpState.Blocked); }
                        else Go(BridgeOpState.Locking);
                    }
                    break;
                case BridgeOpState.Open: if(!wantOpen) Go(BridgeOpState.Closing); break;
                case BridgeOpState.Blocked:
                    if(!wantOpen && Linkage.Progress>0) Go(BridgeOpState.Closing); break;
                case BridgeOpState.Locking:
                    if(_stateTime>=LockTime) { ApplyLocks(true); Fault=null; Go(BridgeOpState.Closed); } break;
            }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if(Linkage!=null || _automatic!=null)
            {
                StatusText=Loc.F("通用铰接联动：{0}，行程 {1:P0}", (State), (Linkage?.Progress ?? 0))+(Fault==null?"":Loc.T("；")+Fault)+(_automatic==null?"":Loc.T("；")+_automatic.StatusText);
                return;
            }
            double angle = Leaves.Count == 0 ? 0 : Leaves.Max(l => l.Angle);
            StatusText = State switch
            {
                BridgeOpState.Closed => Locked ? Loc.T("开启桥：闭合，接缝已连接") : Loc.T("开启桥：闭合（接缝未连接）"),
                BridgeOpState.StoppingTraffic => Loc.T("开启桥：车辆停车，等待活动桥面清空"),
                BridgeOpState.Unlocking => Loc.T("开启桥：解除活动接缝"),
                BridgeOpState.Opening => Loc.F("开启桥：正在开启 {0:0}°", (angle)),
                BridgeOpState.Open => Loc.F("开启桥：保持开启 {0:0}°，船只通行", (angle)),
                BridgeOpState.Closing => Loc.F("开启桥：正在闭合 {0:0}°", (angle)),
                BridgeOpState.Locking => Loc.T("开启桥：对齐并恢复接缝连接"),
                BridgeOpState.Blocked => Loc.F("开启桥：动作受限，停在 {0:0}°，保持禁止通行（{1}）", (angle), (Fault)),
                _ => "",
            };
        }

        public void CopyAngles(double[] dst) { for (int i = 0; i < Leaves.Count && i < dst.Length; i++) dst[i] = Leaves[i].Angle; }
    }
}
