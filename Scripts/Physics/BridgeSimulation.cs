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
        /// <summary>所在道路层与行驶路径。</summary>
        public int Lane;
        public RoadPath Path;
        /// <summary>在断口处被卡住的累计时间 s。</summary>
        public double StuckTime;
        public double FinishTime;
        public double RearOffset => Def.Axles.Count == 0 ? 0 : Def.Axles.Min(a => a.Offset);
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
        public double[] ShipZ;
        public bool[] Broken;
        public float[] Utilization;
        public double[] VehicleS;
        public bool[] VehicleActive;
        public void CopyFrom(SimFrame o)
        {
            Time = o.Time;
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
                // 洪水到来：怕水的木桥墩被冲毁
                if (_floodHit.Add(e))
                {
                    for (int k = 0; k < Model.MemberCount; k++)
                    {
                        if (Model.Broken[k] || !Model.Mat[k].FloodVulnerable) continue;
                        if (Math.Min(Model.Y[Model.A[k]], Model.Y[Model.B[k]]) > floodY) continue;
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

        /// <summary>液压杆收缩程度随时间的变化：船进入桥位前 4 秒开始收缩，船完全离开后 4 秒内恢复。</summary>
        private double ComputeHydraulicFactor(double t)
        {
            double f = 0;
            foreach (var s in Ships)
            {
                double ws = s.WindowStart, we = s.WindowEnd, v;
                if (t < ws - HydraulicRampTime || t > we + HydraulicRampTime) v = 0;
                else if (t < ws) v = 1 - (ws - t) / HydraulicRampTime;
                else if (t <= we) v = 1;
                else v = 1 - (t - we) / HydraulicRampTime;
                f = Math.Max(f, v);
            }
            return f;
        }

        public int TotalVehicles => _totalVehicles;
        public int FinishedVehicles => Vehicles.Count(v => v.Finished);
        public double Progress => _totalVehicles == 0 ? 1 : (FinishedVehicles + Vehicles.Where(v => !v.Finished).Sum(VehicleProgress)) / _totalVehicles;

        private double VehicleProgress(VehicleState v)
        {
            if (v.Path == null) return 0;
            return Math.Clamp((v.FrontS + v.RearOffset) / (v.Path.DeckEndS + 8), 0, 1);
        }

        /// <summary>初始化：预算、道路连续性、结构模型以及仅自重作用下的静力校核。</summary>
        public void Initialize()
        {
            State = SimState.Running;
            Time = 0;
            Result.Budget = Level.Budget;
            Result.TotalCost = Design.TotalCost();
            Model = StructuralModel.Build(Design, Level, Db);
            Status = new MemberStatus[Model.MemberCount];
            _loads = new double[Model.NodeCount * 2];
            _kinLoads = new double[Model.NodeCount * 2];
            KinematicDisplacement = new double[Model.NodeCount * 2];
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
            // 起点被同层前车占用时，后车排队等待，避免车辆重叠
            for (int w = 0; w < _waitingSpawn.Count; w++)
            {
                var e = _waitingSpawn[w];
                var def = Db.Vehicles[e.VehicleId];
                var vs = new VehicleState { Def = def, Lane = e.Lane, Path = PathFor(e.Lane) };
                vs.FrontS = -vs.RearOffset + 1.0;
                var last = Vehicles.LastOrDefault(v => v.Lane == e.Lane && !v.Finished && !v.Fallen);
                if (last != null && last.FrontS + last.RearOffset - def.FollowGap - 1.0 < vs.FrontS) break;   // 保持出发顺序
                Vehicles.Add(vs);
                _waitingSpawn.RemoveAt(w); w--;
            }

            // 1b. 船只航行
            foreach (var s in Ships)
            {
                if (s.Done) continue;
                if (!s.Spawned) { if (Time >= s.Ev.Time) s.Spawned = true; else continue; }
                s.Z += s.Ev.Speed * FixedDt;
                if (s.Z > ShipState.EndZ) { s.Done = true; Result.ShipsPassed++; }
            }

            // 1b2. 洪水：水位上涨；木桥墩被冲毁；冲刷区里的浅基础被掏空
            UpdateFlood();

            // 1c. 液压杆按船只时间表自动收缩 / 恢复
            HydraulicFactor = ComputeHydraulicFactor(Time);
            for (int k = 0; k < Model.MemberCount; k++)
            {
                var mat = Model.Mat[k];
                Model.Prestrain[k] = mat.Extendable && mat.RetractRatio > 0 ? -mat.RetractRatio * Model.Length[k] * HydraulicFactor : 0;
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
                for (int j = vi - 1; j >= 0; j--)
                {
                    var ahead = Vehicles[j];
                    if (ahead.Finished || ahead.Fallen || ahead.Lane != v.Lane) continue;
                    double limit = ahead.FrontS + ahead.RearOffset - v.Def.FollowGap - 1.0;
                    if (next > limit) next = Math.Max(v.FrontS, limit);
                    break;
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
                if (Path.Complete && v.FrontS + v.RearOffset > Path.DeckEndS + 8)
                {
                    v.Finished = true; v.FinishTime = Time;
                }
            }

            // 3. 求解 + 失效检测
            SolveAndCheck();
            if (State != SimState.Running) return;

            // 3b. 船只净空检查
            CheckShipClearance();
            if (State != SimState.Running) return;

            // 4. 记录历史
            _historyTimer += FixedDt;
            if (_historyTimer >= HistoryInterval) { _historyTimer = 0; RecordFrame(false); }

            // 5. 完成判定
            if (_totalVehicles > 0 && Vehicles.Count == _totalVehicles && Vehicles.All(v => v.Finished)
                && Ships.All(s => s.Done) && HydraulicFactor <= 0)
            {
                // 车辆都通过了，但期间出现过致命结构问题（如桥面下沉超限）时仍判定失败
                if (HasFatalIssue) Fail(_issueReason, _issueText + "\n车辆虽已通过，但结构出现了致命问题", _issueMembers);
                else Succeed();
            }
            else if (Time > MaxTestTime) Fail(FailureReason.VehicleBlocked, "测试超时，车辆未能到达出口", null);
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
                if (Model.HasPrestrain)
                {
                    Array.Clear(_kinLoads, 0, _kinLoads.Length);
                    LoadCalculator.AddPrestrainLoads(Model, _kinLoads);
                    _solver.Solve(Model, _kinLoads, _kinOut);
                    if (_kinOut.Stable) Array.Copy(_kinOut.Displacement, KinematicDisplacement, KinematicDisplacement.Length);
                }
                else Array.Clear(KinematicDisplacement, 0, KinematicDisplacement.Length);

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
                    double s = -(Output.Displacement[2 * i + 1] - KinematicDisplacement[2 * i + 1]);
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
        private void CheckShipClearance()
        {
            foreach (var s in Ships)
            {
                if (!s.InBridgeZone) continue;
                double x0 = s.Ev.LaneX - s.Ev.Width / 2, x1 = s.Ev.LaneX + s.Ev.Width / 2;
                double y0 = Level.Terrain.WaterLevel, y1 = Level.Terrain.WaterLevel + s.Ev.Height;
                var u = Output.Displacement;
                for (int k = 0; k < Model.MemberCount; k++)
                {
                    if (Model.Broken[k]) continue;
                    int a = Model.A[k], b = Model.B[k];
                    double ax = Model.X[a] + u[2 * a], ay = Model.Y[a] + u[2 * a + 1];
                    double bx = Model.X[b] + u[2 * b], by = Model.Y[b] + u[2 * b + 1];
                    if (SegmentHitsRect(ax, ay, bx, by, x0, y0, x1, y1))
                    {
                        RecordIssue(FailureReason.ShipCollision, $"{s.Ev.Name}（高 {s.Ev.Height:0.#} m）撞上构件 #{Model.MemberIds[k]}（{Model.Mat[k].Name}）", new[] { Model.MemberIds[k] });
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

        private void Succeed()
        {
            State = SimState.Succeeded;
            Result.IsSuccess = true;
            Result.CompletionTime = Time;
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

        public const double BudgetWeight = 7, IntegrityWeight = 2, StressWeight = 1;  // 权重 7 : 2 : 1
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
            r.IntegrityScore = (int)Math.Round(1000 * IntegrityWeight * r.DeckIntegrity);
            r.StressScore = (int)Math.Round(1000 * StressWeight * Math.Max(util, r.SafetyMargin));
            r.Score = r.BudgetScore + r.IntegrityScore + r.StressScore;
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
            Array.Copy(Output.Displacement, f.Displacement, Math.Min(Output.Displacement.Length, f.Displacement.Length));
            Array.Copy(KinematicDisplacement, f.Kinematic, Math.Min(KinematicDisplacement.Length, f.Kinematic.Length));
            for (int i = 0; i < Ships.Count && i < f.ShipZ.Length; i++) f.ShipZ[i] = Ships[i].Spawned && !Ships[i].Done ? Ships[i].Z : double.NaN;
            for (int k = 0; k < Model.MemberCount; k++) { f.Broken[k] = Model.Broken[k]; f.Utilization[k] = (float)Status[k].Utilization; }
            for (int i = 0; i < f.VehicleS.Length; i++)
            {
                f.VehicleActive[i] = i < Vehicles.Count && !Vehicles[i].Finished;
                f.VehicleS[i] = i < Vehicles.Count ? Vehicles[i].FrontS : 0;
            }
        }

        private void RecordFrame(bool reset)
        {
            if (reset) { _historyHead = 0; _historyCount = 0; }
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
            sim.Model = StructuralModel.Build(sim.Design, level, db);
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
