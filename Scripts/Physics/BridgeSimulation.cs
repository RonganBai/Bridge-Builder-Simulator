using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Traffic;

namespace BridgeArchitect3D.Physics
{
    public enum SimState { NotStarted, Running, Succeeded, Failed }

    /// <summary>运行中的车辆状态。</summary>
    public class VehicleState
    {
        public VehicleDefinition Def;
        public double FrontS;          // 车头轴在路径上的弧长 m
        public bool Finished;
        public bool Fallen;
        /// <summary>Train left the rails (derailment animation, then the test fails).</summary>
        public bool Derailed;
        /// <summary>Rail drop under each axle at the previous step (NaN = axle not on the bridge).</summary>
        public double[] RailPrev;
        /// <summary>所在道路层与行驶路径。</summary>
        public int Lane;
        public RoadPath Path;
        /// <summary>在断口处被卡住的累计时间 s。</summary>
        public double StuckTime;
        public double FinishTime;
        /// <summary>正在液压活动段前停车等候（液压装置动作期间，不算卡住）。</summary>
        public bool WaitingForBridge;
        public double RearOffset => Def.Axles.Count == 0 ? 0 : Def.Axles.Min(a => a.Offset);
        /// <summary>Train offsets use the locomotive nose; other vehicles use the leading axle.</summary>
        public double FrontOverhang => Def.Kind == "train" ? 0.2 : Math.Max(0, (Def.Length + RearOffset) / 2);
        public double RearOverhang => Def.Kind == "train" ? Math.Max(0, Def.Length + RearOffset) : FrontOverhang;
        /// <summary>Arc length of the front / rear bumper.</summary>
        public double BodyFrontS => FrontS + FrontOverhang;
        public double BodyRearS => FrontS + RearOffset - RearOverhang;
    }

    /// <summary>船只状态：沿河道（Z 方向）匀速穿过桥位。</summary>
    public class ShipState
    {
        public TrafficEvent Ev;
        public double Z;              // 船中心 Z 坐标 m
        public bool Spawned, Done;
        public const double StartZ = -45, EndZ = 45, BridgeHalfDepth = 3;

        /// <summary>船体与桥位（|z| ≤ BridgeHalfDepth）重叠的时间窗口。</summary>
        public double WindowStart => Ev.Time + (-BridgeHalfDepth - Ev.Length / 2 - StartZ) / Ev.Speed;
        public double WindowEnd => Ev.Time + (BridgeHalfDepth + Ev.Length / 2 - StartZ) / Ev.Speed;
        public bool InBridgeZone => Spawned && !Done && Math.Abs(Z) - Ev.Length / 2 < BridgeHalfDepth;
        /// <summary>The whole hull has left the bridge (a movable bridge may close).</summary>
        public bool PastBridge => Z - Ev.Length / 2 > BridgeHalfDepth;
        /// <summary>Holding in front of a movable bridge that is not open yet.</summary>
        public bool Waiting;
    }

    /// <summary>结构事件（断裂等），用于界面提示。</summary>
    public struct StructureEvent
    {
        public double Time;
        public int MemberId;
        public FailureReason Mode;
        public string Text;
    }

    /// <summary>历史帧（用于失败回放与首次失效快照）。数组预分配，循环复用。</summary>
    public class SimFrame
    {
        public double Time;
        public double[] Displacement;
        public double[] Kinematic;
        /// <summary>Movable-bridge status line at this frame (the geometry itself is in Displacement / Kinematic).</summary>
        public string BridgeState;
        public double[] ShipZ;
        public bool[] Broken;
        public float[] Utilization;
        public double[] VehicleS;
        public bool[] VehicleActive;
        public void CopyFrom(SimFrame o)
        {
            Time = o.Time;
            BridgeState = o.BridgeState;
            Array.Copy(o.Displacement, Displacement, Displacement.Length);
            Array.Copy(o.Kinematic, Kinematic, Kinematic.Length);
            Array.Copy(o.ShipZ, ShipZ, ShipZ.Length);
            Array.Copy(o.Broken, Broken, Broken.Length);
            Array.Copy(o.Utilization, Utilization, Utilization.Length);
            Array.Copy(o.VehicleS, VehicleS, VehicleS.Length);
            Array.Copy(o.VehicleActive, VehicleActive, VehicleActive.Length);
        }
    }

    /// <summary>
    /// 通车测试模拟（纯逻辑，不依赖 Godot）。
    /// 每个固定时间步：推进车辆 → 车轴载荷分配到桥面节点 → 求解结构 → 检测失效 → 断裂后重新求解。
    /// 模拟只操作方案副本，原始建造方案不会被修改。固定步长 + 无随机数，保证结果可重复。
    /// </summary>
    public class BridgeSimulation
    {
        public const double FixedDt = 1.0 / 60.0;
        public const double StuckLimit = 3.0;     // 车辆被卡住多久判定失败 s
        private const double HistoryInterval = 0.1;
        private const int HistoryFrames = 60;      // 约 6 秒回放
        private const double MaxTestTime = 240;

        public BridgeDesign Design { get; }          // 测试副本
        public LevelDefinition Level { get; }
        public GameDatabase Db { get; }
        public StructuralModel Model { get; private set; }
        public RoadPath Path { get; private set; }
        /// <summary>各层道路的行驶路径（索引 = 道路层号顺序）。</summary>
        public readonly List<RoadPath> Paths = new();
        public RoadPath PathFor(int lane) => Paths.FirstOrDefault(p => p.Lane == lane) ?? Path;
        public SolveOutput Output { get; } = new();
        public MemberStatus[] Status { get; private set; } = Array.Empty<MemberStatus>();
        public SimState State { get; private set; } = SimState.NotStarted;
        public SimulationResult Result { get; } = new();
        public double Time { get; private set; }
        public readonly List<VehicleState> Vehicles = new();
        public readonly List<StructureEvent> Events = new();
        public int BrokenCount { get; private set; }
        public double CurrentMaxUtilization { get; private set; }
        public SimFrame FailureSnapshot { get; private set; }
        public readonly List<ShipState> Ships = new();
        /// <summary>液压杆当前收缩程度 0~1（船只通行时自动收缩，通过后恢复）。</summary>
        public double HydraulicFactor { get; private set; }
        /// <summary>仅由液压杆伸缩引起的节点位移（显示时不放大）。</summary>
        public double[] KinematicDisplacement { get; private set; } = Array.Empty<double>();
        public const double HydraulicRampTime = 4.0;   // 液压杆收缩 / 恢复用时 s
        /// <summary>
        /// Movable-bridge mechanism (bascule leaves, deck locks). When it is active the legacy automatic
        /// hydraulic retraction is disabled: leaves move as rigid bodies, never by straining members.
        /// </summary>
        public BasculeSystem Bascule { get; } = new();
        /// <summary>Displacement shown on screen = mechanism motion (exact) + elastic displacement.</summary>
        public double[] DisplayDisplacement { get; private set; } = Array.Empty<double>();
        /// <summary>Mechanisms use true-scale elasticity as well as true-scale kinematics, including replays.</summary>
        public void ComposeDisplayFrame(double[] total, double[] kinematic, double requestedScale, double[] destination)
        {
            double scale = Bascule.Active ? 1 : Math.Clamp(requestedScale, 1, 100);
            for (int i = 0; i < total.Length; i++)
            {
                double motion = kinematic != null && i < kinematic.Length ? kinematic[i] : 0;
                destination[i] = motion + (total[i] - motion) * scale;
            }
        }
        private double[] _mechKin = Array.Empty<double>();
        private double[] _prestrainKin = Array.Empty<double>();
        /// <summary>Distance a ship keeps from the bridge while waiting for it to open, m.</summary>
        public const double ShipHoldMargin = 4.0;

        private readonly SolveOutput _kinOut = new();
        private double[] _kinLoads = Array.Empty<double>();
        private readonly IStructuralSolver _solver;
        private readonly TrafficEventSystem _traffic;
        private double[] _loads = Array.Empty<double>();
        private AxleLoad[] _axles = new AxleLoad[64];
        private int _axleCount;
        private SimFrame[] _history;
        private int _historyHead, _historyCount;
        private double _historyTimer;
        private StructureEvent? _firstBreak;
        private int _totalVehicles;

        public event Action<StructureEvent> MemberBroken;
        /// <summary>钢筋混凝土类构件首次开裂。</summary>
        public event Action<StructureEvent> MemberCracked;
        private readonly HashSet<int> _cracked = new();
        private readonly List<TrafficEvent> _waitingSpawn = new();

        private double[] _stroke;

        /// <summary>Per-member hydraulic stroke: the player's slider value, else the material default retraction.</summary>
        private readonly List<(int member, double x, double y)> _crossBeams = new();

        /// <summary>Cross beams (members >= 5 m) as solid obstacles at their midpoints.</summary>
        private void FillCrossBeams()
        {
            _crossBeams.Clear();
            for (int k = 0; k < Model.MemberCount; k++)
            {
                double xa = Model.X[Model.A[k]], ya = Model.Y[Model.A[k]], xb = Model.X[Model.B[k]], yb = Model.Y[Model.B[k]];
                foreach (double t in CrossBeams.Fractions(Model.Mat[k], Model.Length[k], ya, yb))
                    _crossBeams.Add((Model.MemberIds[k], xa + (xb - xa) * t, ya + (yb - ya) * t));
            }
        }

        /// <summary>Did the vehicle body move into a cross beam that is lower than its roof?</summary>
        private bool HitsCrossBeam(VehicleState v, double frontBefore, double frontAfter, out int member, out double clear)
        {
            member = -1; clear = 0;
            if (_crossBeams.Count == 0 || frontAfter <= frontBefore) return false;
            v.Path.Evaluate(frontBefore + v.FrontOverhang, null, 1, out double x0, out _);
            v.Path.Evaluate(frontAfter + v.FrontOverhang, null, 1, out double x1, out double y1);
            double lo = System.Math.Min(x0, x1), hi = System.Math.Max(x0, x1);
            foreach (var c in _crossBeams)
            {
                if (c.x < lo - 1e-9 || c.x > hi + 1e-9) continue;
                double h = c.y - y1;
                if (h > 0.05 && h < v.Def.Height) { member = c.member; clear = h; return true; }
            }
            return false;
        }

        private void FillStrokes()
        {
            _stroke = new double[Model.MemberCount];
            for (int k = 0; k < Model.MemberCount; k++)
            {
                var mat = Model.Mat[k];
                if (!mat.Extendable) continue;
                var mem = Design.FindMember(Model.MemberIds[k]);
                double s = mem?.Stroke ?? -mat.RetractRatio;
                _stroke[k] = Math.Clamp(s, -LinkageDriverData.MaxStroke, LinkageDriverData.MaxStroke);
            }
        }

        public BridgeSimulation(BridgeDesign design, LevelDefinition level, GameDatabase db, IStructuralSolver solver = null)
        {
            Design = design.Clone();
            foreach (var m in Design.Members) m.DamageState = DamageState.Intact;
            Level = level; Db = db;
            _solver = solver ?? new StructureSolver();
            _traffic = new TrafficEventSystem(level.TrafficEvents.Where(e => e.Type == TrafficEventType.Vehicle));
            _totalVehicles = _traffic.Count;
            foreach (var e in level.TrafficEvents.Where(e => e.Type == TrafficEventType.Ship).OrderBy(e => e.Time))
                Ships.Add(new ShipState { Ev = e, Z = ShipState.StartZ });
        }

        /// <summary>当前洪水水位上涨量 m（显示用）。</summary>
        public double FloodRise { get; private set; }
        public const double FloodRampTime = 2.0;
        private readonly HashSet<TrafficEvent> _floodHit = new(), _floodScoured = new();

        private void UpdateFlood()
        {
            FloodRise = 0;
            foreach (var e in Level.TrafficEvents)
            {
                if (e.Type != TrafficEventType.Environment) continue;
                double t = Time - e.Time;
                if (t < 0 || t > e.Duration + 2 * FloodRampTime) continue;
                double f = t < FloodRampTime ? t / FloodRampTime : t < FloodRampTime + e.Duration ? 1 : 1 - (t - FloodRampTime - e.Duration) / FloodRampTime;
                FloodRise = Math.Max(FloodRise, e.Height * f);
                double floodY = Level.Terrain.WaterLevel + e.Height;
                // Flood-vulnerable (wooden) piers are washed away once the rising water reaches them: when it covers
                // the member, or at the flood peak for members that stand higher than the peak
                double waterNow = Level.Terrain.WaterLevel + e.Height * f;
                if (t <= FloodRampTime + e.Duration && !_floodHit.Contains(e))
                {
                    if (t >= FloodRampTime) _floodHit.Add(e);
                    for (int k = 0; k < Model.MemberCount; k++)
                    {
                        if (Model.Broken[k] || !Model.Mat[k].FloodVulnerable) continue;
                        double lo = Math.Min(Model.Y[Model.A[k]], Model.Y[Model.B[k]]), hi = Math.Max(Model.Y[Model.A[k]], Model.Y[Model.B[k]]);
                        if (lo > floodY) continue;
                        if (waterNow < Math.Min(hi, floodY) - 1e-9) continue;
                        string text = $"构件 #{Model.MemberIds[k]}（{Model.Mat[k].Name}）被洪水冲毁";
                        if (_pendingBreakText == null && _issueReason == FailureReason.None) { _pendingBreakText = text; _pendingBreakMode = FailureReason.FloodScour; _pendingBreakId = Model.MemberIds[k]; }
                        RemoveMember(k, FailureReason.FloodScour, text, countsAsBreak: false);
                    }
                }
                // 水位达到峰值：冲刷区的浅基础被掏空，失去支撑（桩基础不受影响）
                if (t >= FloodRampTime && _floodScoured.Add(e))
                {
                    for (int i = 0; i < Model.NodeCount; i++)
                    {
                        if (!Model.ScourZone[i] || Model.PierFoundation[i] != FoundationType.Shallow) continue;
                        Model.FixX[i] = false; Model.FixY[i] = false; Model.FixRot[i] = false; Model.AnchorMaxLoad[i] = 0;
                        string text = $"冲刷掏空了{Model.AnchorIds[i]}，桥墩失去支撑";
                        if (_pendingBreakText == null && _issueReason == FailureReason.None) { _pendingBreakText = text; _pendingBreakMode = FailureReason.FloodScour; _pendingBreakId = -1; }
                        var ev = new StructureEvent { Time = Time, MemberId = -1, Mode = FailureReason.FloodScour, Text = text };
                        Events.Add(ev); MemberBroken?.Invoke(ev);
                    }
                }
            }
        }
        /// <summary>
        /// Legacy automatic retraction was removed: hydraulics only move through a valid movable-bridge mechanism,
        /// which enforces stroke and force limits. Kept so the HUD / old callers read 0.
        /// </summary>
        private double ComputeHydraulicFactor(double t) => 0;
        public int TotalVehicles => _totalVehicles;
        public int FinishedVehicles => Vehicles.Count(v => v.Finished);
        public double Progress => _totalVehicles == 0 ? 1 : (FinishedVehicles + Vehicles.Where(v => !v.Finished).Sum(VehicleProgress)) / _totalVehicles;

        private double VehicleProgress(VehicleState v)
        {
            if (v.Path == null) return 0;
            return Math.Clamp(v.BodyRearS / (v.Path.DeckEndS + 8), 0, 1);
        }

        /// <summary>初始化：预算、道路连续性、结构模型以及仅自重作用下的静力校核。</summary>
        public void Initialize()
        {
            State = SimState.Running;
            Time = 0;
            Result.Budget = Level.Budget;
            Result.TotalCost = Design.TotalCost();
            Model = StructuralModel.Build(Design, Level, Db);
            // Movable bridge: plan leaves / seams and split the released seams (node count may grow)
            Bascule.Prepare(Model, Design, Db, Level);
            FillStrokes();
            FillCrossBeams();
            Status = new MemberStatus[Model.MemberCount];
            _loads = new double[Model.NodeCount * 2];
            _kinLoads = new double[Model.NodeCount * 2];
            KinematicDisplacement = new double[Model.NodeCount * 2];
            DisplayDisplacement = new double[Model.NodeCount * 2];
            _mechKin = new double[Model.NodeCount * 2];
            _prestrainKin = new double[Model.NodeCount * 2];
            Output.EnsureSize(Model.NodeCount, Model.MemberCount);
            Result.TotalShips = Ships.Count;

            _history = new SimFrame[HistoryFrames];
            for (int i = 0; i < HistoryFrames; i++) _history[i] = NewFrame();
            FailureSnapshot = NewFrame();


            // 每一层道路各建一条行驶路径（立交桥有上下两层）
            foreach (int lane in Level.Anchors.Where(a => a.IsRoadStart).Select(a => a.RoadLane).Distinct().OrderBy(x => x))
            {
                var path = RoadPath.Build(Model, Level, out var reason, out var err, out var involved, lane);
                if (path == null) { Fail(reason, err, involved); return; }
                Paths.Add(path);
                // 道路问题只记录，不立即判负：车辆照常出发，开到断口处才会被卡住 / 坠落
                if (reason != FailureReason.None) RecordIssue(reason, err, involved);
            }
            Path = Paths.FirstOrDefault();
            if (Path == null) { Fail(FailureReason.RoadNotConnected, "关卡缺少道路锚点", null); return; }

            // 倾角过大的立柱在测试开始时倒塌
            foreach (int k in Model.TiltCollapsed)
            {
                var a = Design.FindNode(Model.NodeIds[Model.A[k]]); var b = Design.FindNode(Model.NodeIds[Model.B[k]]);
                string text = $"构件 #{Model.MemberIds[k]}（{Model.Mat[k].Name}）倾角 {ColumnRules.TiltDegrees(a.Position, b.Position):0}° 超过 {Model.Mat[k].MaxTiltDegrees:0}°，倒塌";
                Model.Broken[k] = false;   // 交给 RemoveMember 统一记录
                if (_pendingBreakText == null) { _pendingBreakText = text; _pendingBreakMode = FailureReason.StructureUnstable; _pendingBreakId = Model.MemberIds[k]; }
                RemoveMember(k, FailureReason.StructureUnstable, text, countsAsBreak: false);
            }

            // Movable bridge: leaves, pivots and deck locks come from the saved mechanism data.
            Bascule.Initialize(Paths);
            if (Bascule.Errors.Count > 0)
                Events.Add(new StructureEvent { Time = 0, MemberId = -1, Mode = FailureReason.None, Text = "开启桥设置有误，桥保持闭合：" + string.Join("；", Bascule.Errors) });
            Bascule.ApplyGeometry(_mechKin);
            if (Bascule.Active)
            {
                // Static check: with the seams released the structure must still stand (leaves on pivot + actuators).
                Bascule.TrialRelease(true);
                Array.Clear(_loads, 0, _loads.Length);
                LoadCalculator.Compute(Model, _axles, 0, _loads);
                _solver.Solve(Model, _loads, Output);
                bool stable = Output.Stable;
                Bascule.TrialRelease(false);
                if (!stable)
                {
                    Bascule.Disable($"解除接缝后结构不稳定（节点 #{Math.Abs(Output.UnstableNodeId)} 附近缺少约束），开启桥不会动作。请让每片桥叶只通过一个转轴节点连着岸上结构，并用液压杆从固定结构拉住桥叶");
                    Events.Add(new StructureEvent { Time = 0, MemberId = -1, Mode = FailureReason.None, Text = "开启桥设置有误，桥保持闭合：" + Bascule.Fault });
                }
            }
            if (Ships.Count > 0 && !Bascule.Active && Bascule.Linkage == null && Bascule.Plan.Hint() is { } hint)
                Events.Add(new StructureEvent { Time = 0, MemberId = -1, Mode = FailureReason.None, Text = "提示：" + hint });
            SolveAndCheck();   // t=0：仅自重（不稳定的部分会在这里垮塌）
            RecordFrame(true);
        }
        /// <summary>首个致命问题（用于失败分析）。测试不会因记录问题而立即结束。</summary>
        private FailureReason _issueReason = FailureReason.None;
        private string _issueText;
        private readonly List<int> _issueMembers = new();
        private bool _deflectionRecorded, _collapseRecorded;

        public bool HasFatalIssue => _issueReason != FailureReason.None;

        private void RecordIssue(FailureReason reason, string text, IEnumerable<int> members)
        {
            if (_issueReason != FailureReason.None) return;
            if (_pendingBreakText != null)
            {
                // 根因是之前的构件断裂，当前问题是其后果
                _issueReason = _pendingBreakMode;
                _issueText = $"{_pendingBreakText}，随后{text}（t={Time:0.00}s）";
                if (_pendingBreakId >= 0) _issueMembers.Add(_pendingBreakId);
            }
            else
            {
                _issueReason = reason;
                _issueText = $"{text}（t={Time:0.00}s）";
            }
            if (members != null) foreach (var id in members) if (!_issueMembers.Contains(id)) _issueMembers.Add(id);
            Result.FailureTime = Time;
        }

        /// <summary>车辆坠落：测试结束，失败原因取首个致命问题。</summary>
        private void VehicleFell(VehicleState v, string where)
        {
            v.Fallen = true; _failVehicle = v;
            double t = Time;
            if (_issueReason == FailureReason.None) RecordIssue(FailureReason.VehicleBlocked, $"{v.Def.Name}{where}", null);
            Fail(_issueReason, $"{_issueText}\n结果：{v.Def.Name} 在 t={t:0.00}s {where}，坠落", _issueMembers, FailureReason.VehicleBlocked);
            Result.FailureTime = t;
        }

        private SimFrame NewFrame() => new SimFrame
        {
            Displacement = new double[Model.NodeCount * 2],
            Kinematic = new double[Model.NodeCount * 2],
            ShipZ = new double[Math.Max(1, Ships.Count)],
            Broken = new bool[Model.MemberCount],
            Utilization = new float[Model.MemberCount],
            VehicleS = new double[Math.Max(1, _totalVehicles)],
            VehicleActive = new bool[Math.Max(1, _totalVehicles)],
        };

        /// <summary>推进一个固定时间步。</summary>
        public void Step()
        {
            if (State != SimState.Running) return;
            Time += FixedDt;

            // 1. 交通事件：生成车辆（车尾位于左岸引道上）
            TrafficEvent ev;
            while ((ev = _traffic.PopDue(Time)) != null) _waitingSpawn.Add(ev);
            // Nothing on the road and no ship pending: start the next vehicle now instead of idling until its
            // scheduled time (e.g. the truck after the freight train), so the test never appears to hang
            if (_waitingSpawn.Count == 0 && _traffic.PeekNext() != null && Vehicles.All(v => v.Finished || v.Fallen) && Ships.All(s => s.Done))
                _waitingSpawn.Add(_traffic.PopNext());
            // 起点被同层前车占用时，后车排队等待，避免车辆重叠
            for (int w = 0; w < _waitingSpawn.Count; w++)
            {
                var e = _waitingSpawn[w];
                var def = Db.Vehicles[e.VehicleId];
                var vs = new VehicleState { Def = def, Lane = e.Lane, Path = PathFor(e.Lane) };
                // Keep existing short-vehicle staging; a long train enters nose-first from the left approach.
                vs.FrontS = def.Kind is "train" or "semi" ? 1.0 - vs.FrontOverhang : -vs.RearOffset + 1.0;
                var last = Vehicles.LastOrDefault(v => v.Lane == e.Lane && !v.Finished && !v.Fallen);
                if (last != null && last.BodyRearS - def.FollowGap < vs.BodyFrontS) break;
                Vehicles.Add(vs);
                _waitingSpawn.RemoveAt(w); w--;
            }

            // 1b. 船只航行
            foreach (var s in Ships)
            {
                if (s.Done) continue;
                if (!s.Spawned) { if (Time >= s.Ev.Time) s.Spawned = true; else continue; }
                double nz = s.Z + s.Ev.Speed * FixedDt;
                // A movable bridge must be fully open before the ship's bow reaches the bridge.
                s.Waiting = false;
                s.Z = nz;
                if (s.Z > ShipState.EndZ) { s.Done = true; Result.ShipsPassed++; }
            }

            // 1b2. 洪水：水位上涨；木桥墩被冲毁；冲刷区里的浅基础被掏空
            UpdateFlood();

            // 1c. 液压杆按船只时间表自动收缩 / 恢复
            HydraulicFactor = ComputeHydraulicFactor(Time);
            // 1d. Movable bridge: open while a ship needs the channel, only once no vehicle body is on the leaves
            if (Bascule.Active)
            {
                bool wantOpen = Ships.Any(s => !s.Done && !s.PastBridge && Time + Bascule.RequiredOpeningTime + TrafficClearanceTime() + 1 >= s.WindowStart);
                Bascule.Update(FixedDt, wantOpen, SpanClear());
                Bascule.ApplyGeometry(_mechKin);
            }
            for (int k = 0; k < Model.MemberCount; k++)
            {
                var mat = Model.Mat[k];
                double stroke = _stroke != null && k < _stroke.Length ? _stroke[k] : -mat.RetractRatio;
                Model.Prestrain[k] = mat.Extendable ? stroke * Model.Length[k] * HydraulicFactor : 0;
            }

            // 2. 车辆前进（同一层道路上的后车与前车保持跟车距离，形成车队）
            for (int vi = 0; vi < Vehicles.Count; vi++)
            {
                var v = Vehicles[vi];
                if (v.Finished) continue;
                var Path = v.Path;
                // 陡坡（超过建议坡度）上车辆减速爬坡：坡度每超出建议值的 1 倍，速度降到约 35%
                double speed = v.Def.Speed;
                {
                    int segNow = Path.Locate(v.FrontS, out _);
                    double up = Path.SegmentSlope(segNow), lim = Level.Objectives.MaxDeckSlope;
                    if (up > lim + RoadPath.SlopeTolerance) speed *= Math.Clamp(1 - 0.65 * (up - lim) / lim, 0.35, 1);
                }
                double next = v.FrontS + speed * FixedDt;
                // 液压装置动作期间：还没驶入活动段的车辆在活动段前停车等候，动作结束后继续通行
                // Movable bridge in operation: vehicles whose body has not reached the movable span stop
                // in front of it (front overhang included). Waiting is not a failure.
                v.WaitingForBridge = false;
                var zone = Bascule.ZoneOn(Path);
                if (zone.HasValue && Bascule.TrafficStopped)
                {
                    double stop = zone.Value.s0 - BasculeSystem.StopMargin - v.FrontOverhang;
                    if (v.FrontS <= stop + 1e-9 && next > stop) { next = Math.Max(v.FrontS, stop); v.WaitingForBridge = true; }
                }
                for (int j = vi - 1; j >= 0; j--)
                {
                    var ahead = Vehicles[j];
                    if (ahead.Finished || ahead.Fallen || ahead.Lane != v.Lane) continue;
                    double limit = ahead.BodyRearS - v.Def.FollowGap - v.FrontOverhang;
                    if (next > limit) next = Math.Max(v.FrontS, limit);
                    break;
                }
                {
                    // Trains only on rails, road vehicles only on road surface
                    int segAt = Path.Locate(next, out _);
                    int dk = segAt >= 0 && segAt < Path.SegmentMember.Count ? Path.SegmentMember[segAt] : -1;
                    if (dk >= 0 && !RailTrack.Allows(Model.Mat[dk], v.Def))
                    {
                        _failVehicle = v;
                        string why = RailTrack.IsTrain(v.Def)
                            ? $"{v.Def.Name}只能在铁轨上行驶：桥面 #{Model.MemberIds[dk]}（{Model.Mat[dk].Name}）没有铁轨，请改用铁轨桥面或铁轨公路复合桥面"
                            : $"{v.Def.Name}只能在公路路面上行驶：桥面 #{Model.MemberIds[dk]}（{Model.Mat[dk].Name}）是铁路专用，请改用公路桥面或铁轨公路复合桥面";
                        Fail(FailureReason.WrongSurface, why, new List<int> { Model.MemberIds[dk] });
                        return;
                    }
                }
                if (HitsCrossBeam(v, v.FrontS, next, out int beam, out double beamClear))
                {
                    _failVehicle = v;
                    Fail(FailureReason.VehicleBlocked, $"{v.Def.Name}（高 {v.Def.Height:0.0} m）撞上构件 #{beam} 的横梁：横梁离路面只有 {beamClear:0.0} m（普通构件 ≥ {CrossBeams.MinLength:0} m 在中点加横梁，柱每 {CrossBeams.ColumnSpacing:0} m 加一根横杆），请缩短这根构件或把它移出车辆通行高度", new List<int> { beam });
                    return;
                }
                v.FrontS = next;
                if (!Path.Complete && v.FrontS > Path.TotalLength)
                {
                    // 前方没有可衔接的桥面：车辆被卡在断口处，持续 3 秒后判定失败
                    v.FrontS = Path.TotalLength;
                    v.StuckTime += FixedDt;
                    if (v.StuckTime >= StuckLimit)
                    {
                        _failVehicle = v;
                        if (_issueReason == FailureReason.None) RecordIssue(FailureReason.VehicleBlocked, $"{v.Def.Name}无法继续行驶", null);
                        Fail(_issueReason, $"{_issueText}\n结果：{v.Def.Name} 在 X={Path.PointX[^1]:0.0} m 处被卡住 {StuckLimit:0} 秒，无法到达出口", _issueMembers, FailureReason.VehicleBlocked);
                        return;
                    }
                }
                if (Path.Complete && v.BodyRearS > Path.DeckEndS + 8)
                {
                    v.Finished = true; v.FinishTime = Time;
                }
            }

            // 3. 求解 + 失效检测
            SolveAndCheck();
            if (State != SimState.Running) return;

            // 3a. Rail sway: a train derails when the track drops or bounces too much under its wheels
            CheckRailSway();
            if (State != SimState.Running) return;

            // 3b. 船只净空检查
            CheckShipClearance();
            if (State != SimState.Running) return;

            // 4. 记录历史
            _historyTimer += FixedDt;
            if (_historyTimer >= HistoryInterval) { _historyTimer = 0; RecordFrame(false); }

            // 5. 完成判定
            if (_totalVehicles > 0 && Vehicles.Count == _totalVehicles && Vehicles.All(v => v.Finished)
                && Ships.All(s => s.Done) && HydraulicFactor <= 0 && Bascule.ClosedAndLocked)
            {
                // 车辆都通过了，但期间出现过致命结构问题（如桥面下沉超限）时仍判定失败
                if (HasFatalIssue) Fail(_issueReason, _issueText + "\n车辆虽已通过，但结构出现了致命问题", _issueMembers);
                else Succeed();
            }
            else if (Time > MaxTestTime) Fail(FailureReason.VehicleBlocked, "测试超时，车辆或船只未能通过" +
                (Bascule.Fault == null ? "" : "\n开启桥：" + Bascule.Fault), null);
        }

        /// <summary>收集所有车轴荷载。若车轴位于路径的桥面段，则把荷载交给对应构件。</summary>
        private bool CollectAxles(out int blockedMember, out VehicleState blockedVehicle)
        {
            _axleCount = 0; blockedMember = -1; blockedVehicle = null;
            foreach (var v in Vehicles)
            {
                if (v.Finished) continue;
                foreach (var axle in v.Def.Axles)
                {
                    double s = v.FrontS + axle.Offset;
                    var Path = v.Path;
                    int seg = Path.Locate(s, out double t);
                    int k = Path.SegmentMember[seg];
                    if (k < 0) continue; // 在地面上
                    if (Model.Broken[k]) { blockedMember = k; blockedVehicle = v; return false; }
                    if (_axleCount >= _axles.Length) Array.Resize(ref _axles, _axles.Length * 2);
                    double tm = Path.MemberT(seg, t);   // 换算为构件 A→B 参数
                    _axles[_axleCount++] = new AxleLoad { MemberIndex = k, T = tm, Force = v.Def.Mass * axle.LoadFraction * StructuralModel.Gravity };
                }
            }
            return true;
        }

        private void SolveAndCheck()
        {
            if (!CollectAxles(out int blocked, out var blockedVehicle))
            {
                VehicleFell(blockedVehicle, $"驶上已断裂的桥面 #{Model.MemberIds[blocked]}");
                return;
            }

            // 断裂 / 垮塌后重新分配载荷并重新求解，直到结构稳定且无新的超限构件
            int guard = Model.MemberCount + Model.NodeCount + 4;
            for (int iter = 0; iter < guard; iter++)
            {
                // 仅液压杆伸缩引起的位移（用于显示时与弹性变形区分，避免被视觉放大）
                // (legacy hydraulic retraction without a mechanism: displacement caused by the forced strain only)
                if (Model.HasPrestrain)
                {
                    Array.Clear(_kinLoads, 0, _kinLoads.Length);
                    LoadCalculator.AddPrestrainLoads(Model, _kinLoads);
                    _solver.Solve(Model, _kinLoads, _kinOut);
                    if (_kinOut.Stable) Array.Copy(_kinOut.Displacement, _prestrainKin, _prestrainKin.Length);
                }
                else Array.Clear(_prestrainKin, 0, _prestrainKin.Length);
                // Exact mechanism motion is never scaled on screen, and neither is the legacy forced strain
                for (int q = 0; q < KinematicDisplacement.Length; q++) KinematicDisplacement[q] = _mechKin[q] + _prestrainKin[q];

                LoadCalculator.Compute(Model, _axles, _axleCount, _loads);
                LoadCalculator.AddPrestrainLoads(Model, _loads);
                _solver.Solve(Model, _loads, Output);
                if (!Output.Stable)
                {
                    // 失稳：失去约束的节点连同相连构件一起垮塌坠落，其余部分重新求解
                    TakeSnapshot(false);   // 失稳时当前解无效，用上一帧（垮塌前）的状态作为快照
                    Collapse(Output.UnstableNodeId, Output.Message);
                    if (State != SimState.Running) return;
                    continue;
                }
                // 液压动作期间：桥面连接处受力超过连接强度时先分离（变铰或分开），再做强度校核
                for (int q = 0; q < DisplayDisplacement.Length; q++) DisplayDisplacement[q] = _mechKin[q] + Output.Displacement[q];
                // Actuator over its force limit: the leaf stalls (this step's motion is undone) instead of
                // the actuator dragging the structure with unlimited force
                if (Bascule.CheckActuators(Output, FixedDt)) { Bascule.ApplyGeometry(_mechKin); continue; }
                FailureDetector.EvaluateMembers(Model, Output, _axles, _axleCount, Status);
                int worst = FailureDetector.WorstMember(Model, Status, out double util);
                if (worst >= 0 && util > 1.0)
                {
                    TakeSnapshot(true);    // 首次失效快照：构件已超限、但尚未断开的瞬间
                    BreakMember(worst); continue;
                }

                CurrentMaxUtilization = util;
                Result.MaxUtilization = Math.Max(Result.MaxUtilization, util);
                // 开裂提示（钢筋混凝土类）：先开裂、再破坏，反馈更有层次
                for (int ck = 0; ck < Model.MemberCount; ck++)
                {
                    if (!Status[ck].Cracked || _cracked.Contains(ck)) continue;
                    _cracked.Add(ck);
                    Result.CrackedMembers++;
                    MemberCracked?.Invoke(new StructureEvent { Time = Time, MemberId = Model.MemberIds[ck], Mode = FailureReason.None,
                        Text = $"构件 #{Model.MemberIds[ck]}（{Model.Mat[ck].Name}）出现裂缝（利用率 {Status[ck].Utilization * 100:0}%）" });
                }

                // 锚点过载：锚点失效（失去约束），随后结构重新求解，可能继续垮塌
                int an = FailureDetector.OverloadedAnchor(Model, Output, out double reaction, out _);
                if (an >= 0)
                {
                    RecordIssue(FailureReason.AnchorOverload, $"锚点 {Model.AnchorIds[an]} 支反力 {reaction / 1000:0} kN 超过承载上限 {Model.AnchorMaxLoad[an] / 1000:0} kN", null);
                    Model.FixX[an] = false; Model.FixY[an] = false; Model.AnchorMaxLoad[an] = 0;
                    continue;
                }
                break;
            }

            // 桥面下沉（记录为致命问题，但继续模拟）
            if (!_deflectionRecorded && Output.Stable)
            {
                // 只考虑弹性下沉（液压杆主动收缩造成的位移不计入）
                double sag = 0; int sagNode = -1;
                foreach (int i in Paths.SelectMany(p => p.RoadNodeIndices).Distinct())
                {
                    if (!Bascule.ClosedAndLocked && Bascule.IsLeafNode(i)) continue;   // a raised leaf is not deck sag
                    double s = -(Output.Displacement[2 * i + 1] - _prestrainKin[2 * i + 1]);
                    if (s > sag) { sag = s; sagNode = i; }
                }
                if (sag > Level.Objectives.MaxDeckDeflection)
                {
                    _deflectionRecorded = true;
                    RecordIssue(FailureReason.DeckDeflectionExceeded, $"桥面节点 #{Model.NodeIds[sagNode]} 下沉 {sag:0.00} m，超过限制 {Level.Objectives.MaxDeckDeflection:0.00} m", null);
                }
            }
        }

        /// <summary>船只在桥位时，检查航道净空：任何完好构件进入船体包络（航道宽 × 水面以上船高）即撞船。</summary>
        /// <summary>No vehicle body overlaps the movable span on any road.</summary>
        private double TrafficClearanceTime()
        {
            double seconds=0;
            foreach(var v in Vehicles.Where(v=>!v.Finished && !v.Fallen))
            {
                var zone=Bascule.ZoneOn(v.Path);
                if(zone.HasValue && v.BodyFrontS>=zone.Value.s0 && v.BodyRearS<=zone.Value.s1)
                    seconds=Math.Max(seconds,(zone.Value.s1-v.BodyRearS)/Math.Max(0.1,v.Def.Speed*0.35));
            }
            return seconds;
        }

        private bool SpanClear()
        {
            foreach (var v in Vehicles)
            {
                if (v.Finished || v.Fallen || v.Path == null) continue;
                var z = Bascule.ZoneOn(v.Path);
                if (z.HasValue && v.BodyFrontS > z.Value.s0 - 0.05 && v.BodyRearS < z.Value.s1 + 0.05) return false;
            }
            return true;
        }

        /// <summary>Largest elastic rail drop (m) and wheel vertical speed (m/s) seen under any train so far.</summary>
        public double MaxRailSag { get; private set; }
        public double MaxRailBounce { get; private set; }

        private void CheckRailSway()
        {
            if (!Output.Stable || !Bascule.ClosedAndLocked) return;
            foreach (var v in Vehicles)
            {
                if (!RailTrack.IsTrain(v.Def) || v.Finished || v.Fallen || v.Path == null) continue;
                var axles = v.Def.Axles;
                if (v.RailPrev == null || v.RailPrev.Length != axles.Count) { v.RailPrev = new double[axles.Count]; Array.Fill(v.RailPrev, double.NaN); }
                for (int i = 0; i < axles.Count; i++)
                {
                    double s = v.FrontS + axles[i].Offset;
                    int seg = s >= 0 && s <= v.Path.TotalLength ? v.Path.Locate(s, out _) : -1;
                    if (seg < 0 || v.Path.SegmentMember[seg] < 0) { v.RailPrev[i] = double.NaN; continue; }
                    v.Path.Evaluate(s, Output.Displacement, 1, out _, out double yd);
                    v.Path.Evaluate(s, _prestrainKin, 1, out _, out double y0);
                    double sag = y0 - yd;
                    double bounce = double.IsNaN(v.RailPrev[i]) ? 0 : Math.Abs(sag - v.RailPrev[i]) / FixedDt;
                    v.RailPrev[i] = sag;
                    MaxRailSag = Math.Max(MaxRailSag, sag); MaxRailBounce = Math.Max(MaxRailBounce, bounce);
                    if (sag <= RailTrack.MaxSag && bounce <= RailTrack.MaxBounce) continue;
                    v.Derailed = true; _failVehicle = v;
                    int id = Model.MemberIds[v.Path.SegmentMember[seg]];
                    string what = sag > RailTrack.MaxSag
                        ? $"车轮下的铁轨下沉 {sag:0.00} m（上限 {RailTrack.MaxSag:0.00} m）"
                        : $"车轮上下晃动速度 {bounce:0.00} m/s（上限 {RailTrack.MaxBounce:0.00} m/s）";
                    Fail(FailureReason.Derailed, $"{v.Def.Name}在桥面 #{id} 上脱轨：{what}，铁路桥需要更刚、起伏更小", new List<int> { id });
                    return;
                }
            }
        }

        private void CheckShipClearance()
        {
            foreach (var s in Ships)
            {
                if (!s.Spawned || s.Done) continue;
                double x0 = s.Ev.LaneX - s.Ev.Width / 2, x1 = s.Ev.LaneX + s.Ev.Width / 2;
                double y0 = Level.Terrain.WaterLevel, y1 = Level.Terrain.WaterLevel + s.Ev.Height;
                var u = Output.Displacement;
                for (int k = 0; k < Model.MemberCount; k++)
                {
                    if (Model.Broken[k]) continue;
                    int a = Model.A[k], b = Model.B[k];
                    double ax = Model.X[a] + u[2 * a], ay = Model.Y[a] + u[2 * a + 1];
                    double bx = Model.X[b] + u[2 * b], by = Model.Y[b] + u[2 * b + 1];
                    // The member is a band of its visual thickness, not a line
                    var mat = Model.Mat[k];
                    double offset = mat.IsDeck ? 0.2 - mat.VisualThickness / 2 : 0;
                    bool Hit(double z, double depth) => ShipGeometry.Intersects(s.Ev, Level.Terrain.WaterLevel, s.Z, ax, ay + offset, bx, by + offset, z, mat.VisualThickness, depth);
                    if (mat.IsDeck ? Hit(0, mat.DeckWidth) : Hit(-2.2, mat.VisualThickness) || Hit(2.2, mat.VisualThickness))
                    {
                        string why = Bascule.Fault != null || Bascule.ConfigNote != null ? "\n开启桥：" + string.Join("；", new[] { Bascule.Fault, Bascule.ConfigNote }.Where(x => x != null))
                            : !Bascule.Active && Model.Mat[k].IsDeck ? "\n提示：桥面净空不足，需要开启桥。" + (Bascule.Plan.Hint() ?? "让每片桥叶只通过一个转轴节点连着岸上结构，液压杆一端接桥叶、一端接固定结构") : "";
                        RecordIssue(FailureReason.ShipCollision, $"{s.Ev.Name}（高 {s.Ev.Height:0.#} m）撞上构件 #{Model.MemberIds[k]}（{Model.Mat[k].Name}）{why}", new[] { Model.MemberIds[k] });
                        Fail(_issueReason, _issueText, _issueMembers);
                        return;
                    }
                }
            }
        }

        /// <summary>线段与轴对齐矩形是否相交（Liang–Barsky 裁剪）。</summary>
        private static bool SegmentHitsRect(double ax, double ay, double bx, double by, double x0, double y0, double x1, double y1)
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

        /// <summary>结构失稳时，让失去约束的节点及其相连构件垮塌（从求解中移除）。</summary>
        private void Collapse(int nodeId, string message)
        {
            var removed = new List<int>();
            bool hasNode = Model.NodeIndex.TryGetValue(nodeId, out int ni);
            for (int k = 0; k < Model.MemberCount; k++)
            {
                if (Model.Broken[k]) continue;
                if (hasNode && Model.A[k] != ni && Model.B[k] != ni) continue;
                removed.Add(k);
            }
            if (removed.Count == 0)
            {
                // 无法定位失稳节点：剩余结构整体垮塌
                for (int k = 0; k < Model.MemberCount; k++) if (!Model.Broken[k]) removed.Add(k);
            }
            var ids = removed.Select(k => Model.MemberIds[k]).ToList();
            // 只有桥面随之垮塌才是致命问题；不影响道路的局部构件掉落只作为结构事件（车辆能否通过由实际行驶决定）
            if (_pendingBreakText == null && _issueReason == FailureReason.None && ids.Count > 0)
            {
                bool deck = removed.Any(k => Model.Mat[k].IsDeck);
                _pendingBreakText = message + (deck ? "，桥面随之垮塌" : "（局部构件垮塌）");
                _pendingBreakMode = FailureReason.StructureUnstable; _pendingBreakId = ids[0];
            }
            foreach (int k in removed) RemoveMember(k, FailureReason.StructureUnstable, $"构件 #{Model.MemberIds[k]}（{Model.Mat[k].Name}）失去支撑而垮塌", countsAsBreak: false);
            if (removed.Count == 0) Fail(FailureReason.StructureUnstable, message, null);
        }

        private int _overstressBreaks;

        private void RemoveMember(int k, FailureReason mode, string text, bool countsAsBreak = true)
        {
            Model.Broken[k] = true;
            BrokenCount++;
            if (countsAsBreak) _overstressBreaks++;
            var dm = Design.FindMember(Model.MemberIds[k]);
            if (dm != null) dm.DamageState = DamageState.Broken;
            var e = new StructureEvent { Time = Time, MemberId = Model.MemberIds[k], Mode = mode, Text = text };
            Events.Add(e);
            _firstBreak ??= e;
            MemberBroken?.Invoke(e);
            if (!_collapseRecorded && _overstressBreaks > Level.Objectives.MaxBrokenMembers)
            {
                _collapseRecorded = true;
                RecordIssue(FailureReason.ProgressiveCollapse, $"已有 {BrokenCount} 根构件相继失效（首个：{_firstBreak.Value.Text}）", Events.Select(x => x.MemberId));
            }
        }

        private void BreakMember(int k)
        {
            var st = Status[k];
            string modeText = st.Governing switch
            {
                FailureReason.TensionFailure => "拉伸破坏",
                FailureReason.CompressionFailure => "压缩破坏/屈曲",
                FailureReason.DeckBendingFailure => "弯曲破坏",
                _ => "破坏",
            };
            string text = $"构件 #{Model.MemberIds[k]}（{Model.Mat[k].Name}）{modeText}，利用率 {st.Utilization * 100:0}%";
            // 断裂本身不立即判负：车辆能否通过由实际行驶决定（驶上断裂桥面会坠落）；
            // 首次断裂作为候选根因，若随后出现致命后果，失败报告会追溯到它。桥面断裂会降低完整度评分。
            if (_issueReason == FailureReason.None && _pendingBreakText == null)
            { _pendingBreakText = text + (Model.Mat[k].IsDeck ? "，道路断裂" : ""); _pendingBreakMode = st.Governing; _pendingBreakId = Model.MemberIds[k]; }
            RemoveMember(k, st.Governing, text);
        }

        // 普通构件首次断裂（非致命，但若随后出现致命问题，优先作为失败根因）
        private string _pendingBreakText;
        private FailureReason _pendingBreakMode;
        private int _pendingBreakId;

        private void Fail(FailureReason reason, string message, List<int> involved, FailureReason secondary = FailureReason.None)
        {
            if (State == SimState.Failed) return;
            State = SimState.Failed;
            Result.IsSuccess = false;
            Result.FailureReason = reason;
            Result.FailureMessage = message;
            Result.FailureTime = Time;
            if (involved != null) Result.InvolvedMemberIds.AddRange(involved);
            Result.Suggestions.AddRange(FailureDetector.SuggestionsFor(reason));
            if (secondary != FailureReason.None) Result.Suggestions.AddRange(FailureDetector.SuggestionsFor(secondary));
            FillVehicleResults();
            BuildHeadline();
            if (_history != null) { if (!_snapshotTaken) { WriteFrame(FailureSnapshot); _snapshotTaken = true; } RecordFrame(false); }
        }

        private VehicleState _failVehicle;
        private bool _snapshotTaken;

        /// <summary>记录首次失效快照（只记录一次）。useCurrent=false 时取最近一帧历史（失稳前的状态）。</summary>
        private void TakeSnapshot(bool useCurrent)
        {
            if (_snapshotTaken || FailureSnapshot == null) return;
            _snapshotTaken = true;
            if (useCurrent) WriteFrame(FailureSnapshot);
            else if (_historyCount > 0) FailureSnapshot.CopyFrom(GetHistoryFrame(_historyCount - 1));
            else WriteFrame(FailureSnapshot);
            FailureSnapshot.Time = Time;
        }

        /// <summary>生成通俗的失败标题与一条针对位置的建议。</summary>
        private void BuildHeadline()
        {
            var r = Result;
            int k = -1;
            if (r.InvolvedMemberIds.Count > 0 && Model != null) Model.MemberIndex.TryGetValue(r.InvolvedMemberIds[0], out k);
            string where = "", what = "构件";
            if (k >= 0)
            {
                double x = (Model.X[Model.A[k]] + Model.X[Model.B[k]]) / 2;
                r.ProblemX = x;
                double w = Level.Terrain.RiverWidth;
                where = x < w * 0.33 ? "靠左岸处" : x > w * 0.67 ? "靠右岸处" : "桥中间";
                what = Model.Mat[k].Name;
            }
            string who = (_failVehicle ?? Vehicles.LastOrDefault(v => !v.Finished))?.Def.Name ?? "车辆";
            switch (r.FailureReason)
            {
                case FailureReason.DeckBendingFailure:
                    r.Headline = $"{who}在{where}压弯折断了{what}";
                    r.TopSuggestion = Model != null && k >= 0 && Model.Mat[k].IsDeck
                        ? "给这段桥面的中点加一根斜撑，或从上方用钢索 / 吊杆拉住它" : "这根构件主要在受弯：加斜杆组成三角形，让它主要承受拉压";
                    break;
                case FailureReason.TensionFailure:
                    r.Headline = $"{where}的{what}被拉断了"; r.TopSuggestion = "把它换成钢梁或钢索，或把桁架加高以减小拉力"; break;
                case FailureReason.CompressionFailure:
                    r.Headline = $"{where}的{what}被压弯（屈曲）了"; r.TopSuggestion = "缩短这根受压杆：在它中间加一个节点并连一根斜撑，或改用钢梁"; break;
                case FailureReason.StructureUnstable:
                    r.Headline = $"{where}的结构缺少支撑而垮塌"; r.TopSuggestion = "给这里的节点再连一根不同方向的构件，组成三角形"; break;
                case FailureReason.RoadNotConnected:
                    r.Headline = $"{who}开到桥面尽头过不去了"; r.TopSuggestion = "把桥面一直铺到右岸的“终点”锚点，中间不要留断口"; break;
                case FailureReason.Derailed:
                    r.Headline = $"{who}在{where}脱轨了"; r.TopSuggestion = "铁路桥要更刚：加高桁架、加密桥面下的支点，减小列车经过时铁轨的下沉与起伏"; break;
                case FailureReason.WrongSurface:
                    r.Headline = $"{who}驶上了不能通行的路面"; r.TopSuggestion = "火车走铁轨桥面，汽车走公路桥面；两种都要过就用铁轨公路复合桥面"; break;
                case FailureReason.VehicleBlocked:
                    r.Headline = $"{who}无法继续行驶"; r.TopSuggestion = "检查桥面是否连续、坡度是否过大"; break;
                case FailureReason.ShipCollision:
                    r.Headline = $"船撞上了{where}的{what}"; r.TopSuggestion = "把航道（橙色框）里的构件移走或抬高到船高以上，或改用液压杆让路"; break;
                case FailureReason.AnchorOverload:
                    if (r.FailureMessage.Contains("桥墩"))
                    { r.Headline = "桥墩下的地基承受不住，基础失效了"; r.TopSuggestion = "软土上的桥墩改用桩基础，或把桥墩建在岩石区"; }
                    else { r.Headline = "一个锚点承受不住，被拉脱了"; r.TopSuggestion = "把载荷分给更多锚点，或减轻桥的自重"; }
                    break;
                case FailureReason.FloodScour:
                    r.Headline = "洪水冲坏了桥墩"; r.TopSuggestion = "冲刷区的桥墩改用桩基础或移到岩石区；洪水关不要用木桥墩"; break;
                case FailureReason.DeckDeflectionExceeded:
                    r.Headline = $"桥面{where}下沉太多"; r.TopSuggestion = "加高桁架或增加支点，提高桥的刚度"; break;
                case FailureReason.ProgressiveCollapse:
                    r.Headline = $"{where}的构件接连断裂"; r.TopSuggestion = "先加强最先断裂的那根构件，并增加冗余斜撑"; break;
                default:
                    r.Headline = FailureDetector.ReasonText(r.FailureReason); r.TopSuggestion = r.Suggestions.FirstOrDefault() ?? ""; break;
            }
        }

        /// <summary>
        /// Finish time if every vehicle drove its whole path at its own speed from its event time and every ship
        /// sailed straight through: the lower bound used by the time score.
        /// </summary>
        private double IdealFinishTime()
        {
            double t = 0;
            foreach (var e in Level.TrafficEvents)
            {
                if (e.Type == TrafficEventType.Vehicle && Db.Vehicles.TryGetValue(e.VehicleId, out var def) && def.Speed > 0)
                {
                    var path = PathFor(e.Lane);
                    if (path != null) t = Math.Max(t, e.Time + (path.DeckEndS + 8 + def.Length) / def.Speed);
                }
                else if (e.Type == TrafficEventType.Ship && e.Speed > 0) t = Math.Max(t, e.Time + (ShipState.EndZ - ShipState.StartZ) / e.Speed);
            }
            return t;
        }

        private void Succeed()
        {
            State = SimState.Succeeded;
            Result.IsSuccess = true;
            Result.CompletionTime = Time;
            Result.ReferenceTime = IdealFinishTime();
            FillVehicleResults();
            double total = 0, intact = 0;
            for (int k = 0; k < Model.MemberCount; k++)
            {
                if (!Model.Mat[k].IsDeck) continue;
                total += Model.Length[k];
                if (!Model.Broken[k]) intact += Model.Length[k];
            }
            Result.DeckIntegrity = total > 0 ? intact / total : 1;
            ComputeScore(Result);
        }

        public const double BudgetWeight = 6, TimeWeight = 2, IntegrityWeight = 1, StressWeight = 1;  // budget : time : integrity : stress = 6 : 2 : 1 : 1
        public const double MaxSafetyMargin = 1.5;   // 安全冗余加分上限 150%

        /// <summary>
        /// 最终评分（上不封顶，通关后才计算），三项权重 7 : 2 : 1：
        /// 预算分 = 7000 × 提供预算 / 使用预算（用得越少越高，不封顶）；
        /// 完整度分 = 2000 × 通过后桥面完整度；
        /// 受力分 = 1000 × max(最大受力利用率, 安全冗余)，
        ///   利用率 = 峰值需求 / 承载力（≤ 100%），安全冗余 = 承载力 / 峰值需求 − 1（上限 150%），取两者中得分高的一项。
        /// </summary>
        public static void ComputeScore(SimulationResult r)
        {
            if (!r.IsSuccess) { r.Score = 0; return; }
            double util = Math.Clamp(r.MaxUtilization, 0, 1);
            r.SafetyMargin = util > 1e-9 ? Math.Min(1 / util - 1, MaxSafetyMargin) : MaxSafetyMargin;
            r.StressUsesMargin = r.SafetyMargin > util;
            r.BudgetScore = (int)Math.Round(1000 * BudgetWeight * r.Budget / Math.Max(r.TotalCost, 1));
            // Time: ideal / actual finish time (waiting at a closed bridge, slow climbs or queues cost points)
            double tr = r.ReferenceTime > 0 && r.CompletionTime > 0 ? Math.Min(1, r.ReferenceTime / r.CompletionTime) : 1;
            r.TimeScore = (int)Math.Round(1000 * TimeWeight * tr);
            r.IntegrityScore = (int)Math.Round(1000 * IntegrityWeight * r.DeckIntegrity);
            r.StressScore = (int)Math.Round(1000 * StressWeight * Math.Max(util, r.SafetyMargin));
            r.Score = r.BudgetScore + r.TimeScore + r.IntegrityScore + r.StressScore;
        }

        private void FillVehicleResults()
        {
            Result.VehicleResults.Clear();
            foreach (var e in _traffic.Events)
            {
                var def = Db.Vehicles[e.VehicleId];
                Result.VehicleResults.Add(new VehicleResult { VehicleId = def.Id, Name = def.Name });
            }
            for (int i = 0; i < Vehicles.Count && i < Result.VehicleResults.Count; i++)
            {
                Result.VehicleResults[i].Passed = Vehicles[i].Finished;
                Result.VehicleResults[i].FinishTime = Vehicles[i].FinishTime;
            }
        }

        // ---------------- 历史与回放 ----------------
        private void WriteFrame(SimFrame f)
        {
            f.Time = Time;
            Array.Copy(DisplayDisplacement, f.Displacement, Math.Min(DisplayDisplacement.Length, f.Displacement.Length));
            f.BridgeState = Bascule.StatusText;
            Array.Copy(KinematicDisplacement, f.Kinematic, Math.Min(KinematicDisplacement.Length, f.Kinematic.Length));
            for (int i = 0; i < Ships.Count && i < f.ShipZ.Length; i++) f.ShipZ[i] = Ships[i].Spawned && !Ships[i].Done ? Ships[i].Z : double.NaN;
            for (int k = 0; k < Model.MemberCount; k++) { f.Broken[k] = Model.Broken[k]; f.Utilization[k] = (float)Status[k].Utilization; }
            for (int i = 0; i < f.VehicleS.Length; i++)
            {
                f.VehicleActive[i] = i < Vehicles.Count && !Vehicles[i].Finished;
                f.VehicleS[i] = i < Vehicles.Count ? Vehicles[i].FrontS : 0;
            }
        }

        // Whole-test timeline (0.1 s frames) for scrubbing with the progress bar
        private const int TimelineCap = 18000;
        private readonly List<SimFrame> _timeline = new();
        public int TimelineCount => _timeline.Count;
        public SimFrame GetTimelineFrame(int i) => _timeline[i];

        private void RecordFrame(bool reset)
        {
            if (reset) { _historyHead = 0; _historyCount = 0; _timeline.Clear(); }
            if (_timeline.Count < TimelineCap) { var t = NewFrame(); WriteFrame(t); _timeline.Add(t); }
            WriteFrame(_history[_historyHead]);
            _historyHead = (_historyHead + 1) % HistoryFrames;
            _historyCount = Math.Min(_historyCount + 1, HistoryFrames);
        }

        public int HistoryCount => _historyCount;

        /// <summary>按时间顺序取历史帧（0 为最早）。</summary>
        public SimFrame GetHistoryFrame(int i)
        {
            int start = (_historyHead - _historyCount + HistoryFrames) % HistoryFrames;
            return _history[(start + i) % HistoryFrames];
        }

        /// <summary>静态分析：不放车辆，只计算自重；用于建造模式下的受力显示。</summary>
        public static BridgeSimulation StaticAnalysis(BridgeDesign design, LevelDefinition level, GameDatabase db)
        {
            var sim = new BridgeSimulation(design, level, db);
            if ((design.Mechanisms?.Linkage?.Drivers.Count ?? 0)>0)
            {
                sim.Initialize();
                return sim;
            }
            sim.Model = StructuralModel.Build(sim.Design, level, db);
            sim.FillStrokes();
            sim.Status = new MemberStatus[sim.Model.MemberCount];
            sim._loads = new double[sim.Model.NodeCount * 2];
            sim._axleCount = 0;
            LoadCalculator.Compute(sim.Model, sim._axles, 0, sim._loads);
            sim._solver.Solve(sim.Model, sim._loads, sim.Output);
            if (sim.Output.Stable)
            {
                FailureDetector.EvaluateMembers(sim.Model, sim.Output, sim._axles, 0, sim.Status);
                FailureDetector.WorstMember(sim.Model, sim.Status, out double u);
                sim.CurrentMaxUtilization = u;
            }
            return sim;
        }

        /// <summary>无界面快速运行整个测试（用于自动化测试与参考方案校验）。</summary>
        public SimulationResult RunToEnd()
        {
            if (State == SimState.NotStarted) Initialize();
            while (State == SimState.Running) Step();
            return Result;
        }
    }
}
