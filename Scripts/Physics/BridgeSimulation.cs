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
        /// <summary>Waiting at a tunnel portal (closed flood gate or ponding water); not a failure.</summary>
        public bool WaitingAtPortal;
        /// <summary>Riding a bank lift: vertical offset from the path (display only, the path position does not change while riding).</summary>
        public double LiftDy;
        public bool OnLift;
        /// <summary>Current speed along the path, m/s (NaN until the first step: starts at cruising speed).</summary>
        public double Speed = double.NaN;
        public double RearOffset => VehicleBody.RearAxle(Def);
        /// <summary>Train offsets use the locomotive nose; other vehicles use the leading axle.</summary>
        public double FrontOverhang => VehicleBody.FrontOverhang(Def);
        public double RearOverhang => VehicleBody.RearOverhang(Def);
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
        public double[] VehicleLiftDy;
        /// <summary>Weather look at this frame: rain 0..1, overcast 0..1, ground shaking m/s².</summary>
        public double Rain, Overcast, Shake, Widen;
        public double[] LiftPlatformDy, LiftDoorOpen;
        public bool[] VehicleActive;
        public void CopyFrom(SimFrame o)
        {
            Time = o.Time;
            BridgeState = o.BridgeState;
            Rain = o.Rain; Overcast = o.Overcast; Shake = o.Shake; Widen = o.Widen;
            Array.Copy(o.Displacement, Displacement, Displacement.Length);
            Array.Copy(o.Kinematic, Kinematic, Kinematic.Length);
            Array.Copy(o.ShipZ, ShipZ, ShipZ.Length);
            Array.Copy(o.Broken, Broken, Broken.Length);
            Array.Copy(o.Utilization, Utilization, Utilization.Length);
            Array.Copy(o.VehicleS, VehicleS, VehicleS.Length);
            Array.Copy(o.VehicleLiftDy, VehicleLiftDy, VehicleLiftDy.Length);
            if(o.LiftPlatformDy!=null&&LiftPlatformDy!=null)Array.Copy(o.LiftPlatformDy,LiftPlatformDy,Math.Min(o.LiftPlatformDy.Length,LiftPlatformDy.Length));
            if(o.LiftDoorOpen!=null&&LiftDoorOpen!=null)Array.Copy(o.LiftDoorOpen,LiftDoorOpen,Math.Min(o.LiftDoorOpen.Length,LiftDoorOpen.Length));
            Array.Copy(o.VehicleActive, VehicleActive, VehicleActive.Length);
        }
    }

    /// <summary>
    /// 通车测试模拟（纯逻辑，不依赖 Godot）。
    /// 每个固定时间步：推进车辆 → 车轴载荷分配到桥面节点 → 求解结构 → 检测失效 → 断裂后重新求解。
    /// 模拟只操作方案副本，原始建造方案不会被修改。固定步长 + 无随机数，保证结果可重复。
    /// </summary>
    public partial class BridgeSimulation
    {
        public const double FixedDt = 1.0 / 60.0;
        public const double StuckLimit = 3.0;     // 车辆被卡住多久判定失败 s
        /// <summary>Braking and pulling-away rates of vehicles (m/s²): they slow down to a stop instead of halting instantly.</summary>
        public const double VehicleBrake = 3.0, VehicleAccel = 2.0;
        private const double HistoryInterval = 0.1;
        private const int HistoryFrames = 60;      // 约 6 秒回放
        private const double MaxTestTime = 240;
        /// <summary>Timeout: 240 s, or twice the ideal finish time plus a minute on long routes (deep tunnels with
        /// long ramps, waiting at closed portals), so a slow but working design is not cut off.</summary>
        private double _timeLimit = -1;
        private double TimeLimit => _timeLimit >= 0 ? _timeLimit : _timeLimit = Math.Max(MaxTestTime, 2 * IdealFinishTime() + 60);

        public BridgeDesign Design { get; }          // 测试副本
        public LevelDefinition Level { get; }
        public GameDatabase Db { get; }
        public StructuralModel Model { get; private set; }
        public RoadPath Path { get; private set; }
        /// <summary>各层道路的行驶路径（索引 = 道路层号顺序）。</summary>
        public readonly List<RoadPath> Paths = new();
        public RoadPath PathFor(int lane) => lane == TunnelRules.Lane && TunnelPath != null ? TunnelPath : Paths.FirstOrDefault(p => p.Lane == lane) ?? Path;
        /// <summary>Vehicle route through a complete underwater tunnel (null when there is none).</summary>
        public RoadPath TunnelPath { get; private set; }
        /// <summary>The tube cracked and flooded (any element over 100%).</summary>
        public bool TunnelFlooded { get; private set; }
        public bool TunnelOpen => TunnelPath != null && !TunnelFlooded;
        /// <summary>Tube geometry and its latest check (pressure, bearing, settlement, joints) for display.</summary>
        public TunnelGeometry TunnelGeo { get; private set; }
        public TunnelState TunnelStatus { get; } = new();
        /// <summary>Peak tube utilization during the test.</summary>
        public double TunnelPeakUtil { get; private set; }
        private bool _tunnelScouring;
        private readonly List<(double x, double force)> _tunnelLoads = new();
        /// <summary>Shield: groundwater rise around each element, lagging behind the flood (per soil).</summary>
        private double[] _groundwater;
        /// <summary>Road vehicles of the main road take a complete, intact tunnel; trains always use the bridge.</summary>
        private int LaneFor(int lane, VehicleDefinition def) => lane == 0 && TunnelOpen && !RailTrack.IsTrain(def) ? TunnelRules.Lane : lane;

        /// <summary>Highest water rise any environment event of this test reaches, m.</summary>
        private double PeakFloodRise()
        {
            double m = 0;
            foreach (var e in Level.TrafficEvents)
                if (e.Type == TrafficEventType.Environment)
                    m = Math.Max(m, e.Weather == null ? e.Height : e.Rise ?? Db.Weather?.Preset(e.Weather)?.WaterRise ?? 0);
            return m;
        }

        private void FloodTunnel(string text)
        {
            if (TunnelFlooded) return;
            TunnelFlooded = true;
            RecordIssue(FailureReason.TunnelFlooded, text, null);
            var ev = new StructureEvent { Time = Time, MemberId = -1, Mode = FailureReason.TunnelFlooded, Text = text };
            Events.Add(ev); MemberBroken?.Invoke(ev);
            var trapped = Vehicles.FirstOrDefault(v => v.Lane == TunnelRules.Lane && !v.Finished && !v.Fallen);
            if (trapped != null && State == SimState.Running)
            {
                trapped.Fallen = true; _failVehicle = trapped;
                Fail(FailureReason.TunnelFlooded, Loc.F("{0}\n结果：{1} 在损坏的隧道里无法继续行驶", (_issueText), (trapped.Def.Name)), _issueMembers);
            }
        }

        /// <summary>Check the tube under the current water level, scour and vehicle axles; over 100% floods it.</summary>
        private void CheckTunnel()
        {
            if (TunnelGeo == null || Db.TunnelConfig == null) return;
            _tunnelLoads.Clear();
            double floor = TunnelGeo.Type.FloorHeight;
            foreach (var v in Vehicles)
            {
                if (v.Lane != TunnelRules.Lane || v.Finished || v.Fallen) continue;
                foreach (var axle in v.Def.Axles)
                {
                    v.Path.Evaluate(v.FrontS + axle.Offset, null, 1, out double x, out double y);
                    double b = TunnelGeo.BottomAt(x);
                    if (double.IsNaN(b) || y > b + floor + 0.05) continue;   // on a ramp inside the bank
                    _tunnelLoads.Add((x, v.Def.Mass * axle.LoadFraction * StructuralModel.Gravity));
                }
            }
            var tc = Db.TunnelConfig;
            double share0 = 1;
            if (TunnelGeo.Type.IsShield)
            {
                if (_groundwater == null || _groundwater.Length != TunnelGeo.Count) _groundwater = new double[TunnelGeo.Count];
                double dt = Time > 0 ? FixedDt : 0;
                for (int i = 0; i < _groundwater.Length; i++)
                {
                    var (share, lag) = TunnelAnalysis.GroundwaterResponse(TunnelGeo, Level, tc, i);
                    // River rise and rain soaking in both reach the tunnel through its most permeable layer, delayed
                    _groundwater[i] += (share * (FloodRise + GroundwaterDirect) - _groundwater[i]) * Math.Min(1, dt / lag);
                }
            }
            var env = new TunnelEnvironment { WaterY = Level.Terrain.WaterLevel + FloodRise, Scouring = _tunnelScouring,
                Current = Time > 0 || FloodFraction > 0 ? Current * share0 : Db.Weather.Normal.Current, GroundwaterRise = _groundwater, Softening = Softening };
            TunnelAnalysis.Evaluate(TunnelGeo, Level, Db.TunnelConfig, env, _tunnelLoads, TunnelStatus);
            TunnelPeakUtil = Math.Max(TunnelPeakUtil, TunnelStatus.MaxUtil);
            if (!TunnelFlooded)
            {
                double tu = Math.Min(1, TunnelStatus.MaxUtil);
                if (tu > Result.MaxUtilization && TunnelStatus.Worst >= 0)
                {
                    Result.WeakestMemberId = -1;
                    Result.WeakestText = Loc.F("隧道管节 #{0}", (TunnelStatus.Worst + 1));
                    Result.WeakestX = TunnelGeo.MidX(TunnelStatus.Worst);
                }
                Result.MaxUtilization = Math.Max(Result.MaxUtilization, tu);
                NotePhaseUtil(tu);
                for (int i = 0; i < TunnelStatus.Count; i++)
                    Result.MaxTunnelMove = Math.Max(Result.MaxTunnelMove, Math.Max(i < TunnelStatus.Settle.Length ? TunnelStatus.Settle[i] : 0, i < TunnelStatus.Sway.Length ? TunnelStatus.Sway[i] : 0));
            }
            if (TunnelStatus.MaxUtil >= 1 && !TunnelFlooded) FloodTunnel(TunnelStatus.WorstText);
        }

        public SolveOutput Output { get; } = new();

        /// <summary>Copies the solved tube beam (joint moments, deflection) into TunnelStatus.</summary>
        private void MergeTube()
        {
            if (TunnelGeo == null || Model.TubeMember.Length == 0 || TunnelStatus.Count != TunnelGeo.Count) return;
            TunnelStructure.Merge(Model, Output, Status, TunnelGeo, TunnelStatus, Db.TunnelConfig, Level, Output.Stable);
            if (!TunnelFlooded) TunnelPeakUtil = Math.Max(TunnelPeakUtil, Math.Min(1, TunnelStatus.MaxUtil));
            for (int i = 0; i < TunnelStatus.Count; i++) Result.MaxTunnelMove = Math.Max(Result.MaxTunnelMove, Math.Abs(TunnelStatus.Settle[i]));
        }

        /// <summary>
        /// Idle intervals only. Approaching, waiting and lifted vehicles remain visible at the selected playback rate.
        /// </summary>
        public bool QuietTraffic => State == SimState.Running && Time >= StaticPhaseTime && _axleCount == 0 && _tunnelLoads.Count == 0
            && !Vehicles.Any(v=>!v.Finished)
            && Lifts.All(l=>l.Phase==LiftPhase.Boarding&&l.Riders.Count==0)
            && WeatherName == null && HydraulicFactor <= 0 && !(Bascule.Active&&Bascule.TrafficStopped) && !Ships.Any(s => s.Spawned && !s.Done);
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
            // Older tunnel saves: their built-in tube capacity becomes visible members first (idempotent)
            if (Design.Tunnel != null && (Design.Tunnel.Structure == null || Design.Tunnel.Lift == null)) Design = new Building.DesignEditor(Design, level, db).Design;
            _solver = solver ?? new StructureSolver();
            // Phase 1 (static) comes first: every vehicle, ship and weather event starts StaticPhaseTime later
            TestEvents = level.TrafficEvents.Select(e => e.Shifted(StaticPhaseTime)).ToList();
            _traffic = new TrafficEventSystem(TestEvents.Where(e => e.Type == TrafficEventType.Vehicle));
            _totalVehicles = _traffic.Count;
            foreach (var e in TestEvents.Where(e => e.Type == TrafficEventType.Ship).OrderBy(e => e.Time))
                Ships.Add(new ShipState { Ev = e, Z = ShipState.StartZ });
        }

        /// <summary>Length of phase 1: the structure stands alone (self weight, normal weather) before any traffic.</summary>
        public const double StaticPhaseTime = 1.0;
        /// <summary>The level's events shifted behind the static phase.</summary>
        public List<TrafficEvent> TestEvents { get; }
        /// <summary>Current test phase (weather has priority over vehicles while an event is active).</summary>
        public TestPhase Phase => State is SimState.Succeeded or SimState.Failed ? TestPhase.Result
            : Time < StaticPhaseTime ? TestPhase.Static : WeatherName != null ? TestPhase.Environment : TestPhase.Vehicles;
        private TestPhase LivePhase => Time < StaticPhaseTime ? TestPhase.Static : WeatherName != null ? TestPhase.Environment : TestPhase.Vehicles;
        private void NotePhaseUtil(double u)
        {
            int p = (int)LivePhase;
            Result.PhaseMaxUtil[p] = Math.Max(Result.PhaseMaxUtil[p], u);
        }

        /// <summary>当前洪水水位上涨量 m（显示用）。</summary>
        public double FloodRise { get; private set; }
        /// <summary>How far the strongest flood is towards its peak (0..1); drives the river current.</summary>
        public double FloodFraction { get; private set; }
        /// <summary>Unified weather right now (shared by bridges and tunnels).</summary>
        public double Current { get; private set; }
        public double GroundwaterDirect { get; private set; }
        public double Softening { get; private set; }
        public double PondRate { get; private set; }
        /// <summary>Weather look (visual only): rain 0..1, overcast 0..1.</summary>
        public double Rain { get; private set; }
        public double Overcast { get; private set; }
        /// <summary>Earthquake: horizontal ground acceleration now (m/s²) and how much wider the river is now (m).</summary>
        public double GroundAccel { get; private set; }
        public double QuakeWiden { get; private set; }
        /// <summary>Name of the strongest active weather event (null = normal).</summary>
        public string WeatherName { get; private set; }
        // Tunnel portals: ponding depth (m), gate state, traffic blocked at the entrance
        public double PortalPond { get; private set; }
        public bool GateClosed { get; private set; }
        public bool PortalBlocked => GateClosed || PortalPond > Db.Weather.Portal.PondBlockDepth;
        private double[] _pierCap;
        public const double FloodRampTime = 2.0;
        private readonly HashSet<TrafficEvent> _floodHit = new(), _floodScoured = new();

        private void UpdateFlood()
        {
            var wc = Db.Weather; var normal = wc.Normal;
            FloodRise = 0; FloodFraction = 0;
            Current = normal.Current; GroundwaterDirect = normal.GroundwaterRise; Softening = normal.SoilSoftening; PondRate = normal.PondRate; WeatherName = null;
            Rain = 0; Overcast = 0; GroundAccel = 0;
            double strongest = 0, widen = 0;
            // Earthquake widening is permanent: it ramps in with the event and stays after it ends
            foreach (var e in TestEvents)
            {
                if (e.Type != TrafficEventType.Environment) continue;
                var qp = wc.PresetOf(e);
                if (qp.QuakeWiden > 0 && Time >= e.Time) widen = Math.Max(widen, qp.QuakeWiden * Math.Min(1, (Time - e.Time) / FloodRampTime));
            }
            QuakeWiden = widen;
            foreach (var e in TestEvents)
            {
                if (e.Type != TrafficEventType.Environment) continue;
                double t = Time - e.Time;
                if (t < 0 || t > e.Duration + 2 * FloodRampTime) continue;
                double f = t < FloodRampTime ? t / FloodRampTime : t < FloodRampTime + e.Duration ? 1 : 1 - (t - FloodRampTime - e.Duration) / FloodRampTime;
                var preset = wc.PresetOf(e);
                double height = wc.RiseOf(e);
                FloodRise = Math.Max(FloodRise, height * f);
                FloodFraction = Math.Max(FloodFraction, f);
                // Every weather quantity ramps with the event; the strongest event of each quantity counts
                Current = Math.Max(Current, normal.Current + (preset.Current - normal.Current) * f);
                GroundwaterDirect = Math.Max(GroundwaterDirect, preset.GroundwaterRise * f);
                Softening = Math.Max(Softening, preset.SoilSoftening * f);
                PondRate = Math.Max(PondRate, preset.PondRate * (f > 0 ? 1 : 0));
                Rain = Math.Max(Rain, preset.Rain * f); Overcast = Math.Max(Overcast, preset.Overcast * f);
                if (preset.QuakeAccel > 0)
                    GroundAccel += preset.QuakeAccel * StructuralModel.Gravity * f * Math.Sin(2 * Math.PI * preset.QuakeFrequency * t);
                if (f * (1 + preset.WaterRise) > strongest) { strongest = f * (1 + preset.WaterRise); WeatherName = e.Name ?? preset.Name; }
                double floodY = Level.Terrain.WaterLevel + height;
                // Flood-vulnerable (wooden) piers are washed away once the rising water reaches them: when it covers
                // the member, or at the flood peak for members that stand higher than the peak
                double waterNow = Level.Terrain.WaterLevel + height * f;
                if (t <= FloodRampTime + e.Duration && !_floodHit.Contains(e))
                {
                    if (t >= FloodRampTime) _floodHit.Add(e);
                    for (int k = 0; k < Model.MemberCount; k++)
                    {
                        if (Model.Broken[k] || !Model.Mat[k].FloodVulnerable) continue;
                        double lo = Math.Min(Model.Y[Model.A[k]], Model.Y[Model.B[k]]), hi = Math.Max(Model.Y[Model.A[k]], Model.Y[Model.B[k]]);
                        if (lo > floodY) continue;
                        if (waterNow < Math.Min(hi, floodY) - 1e-9) continue;
                        string text = Loc.F("构件 #{0}（{1}）被洪水冲毁", (Model.MemberIds[k]), (Model.Mat[k].Name));
                        if (_pendingBreakText == null && _issueReason == FailureReason.None) { _pendingBreakText = text; _pendingBreakMode = FailureReason.FloodScour; _pendingBreakId = Model.MemberIds[k]; }
                        RemoveMember(k, FailureReason.FloodScour, text, countsAsBreak: false);
                    }
                }
                // 水位达到峰值：冲刷区的浅基础被掏空，失去支撑（桩基础不受影响）
                if (t >= FloodRampTime && preset.Scour && _floodScoured.Add(e))
                {
                    for (int i = 0; i < Model.NodeCount; i++)
                    {
                        if (!Model.ScourZone[i] || Model.PierFoundation[i] != FoundationType.Shallow) continue;
                        Model.FixX[i] = false; Model.FixY[i] = false; Model.FixRot[i] = false; Model.AnchorMaxLoad[i] = 0;
                        string text = Loc.F("冲刷掏空了{0}，桥墩失去支撑", (Model.AnchorIds[i]));
                        if (_pendingBreakText == null && _issueReason == FailureReason.None) { _pendingBreakText = text; _pendingBreakMode = FailureReason.FloodScour; _pendingBreakId = -1; }
                        var ev = new StructureEvent { Time = Time, MemberId = -1, Mode = FailureReason.FloodScour, Text = text };
                        Events.Add(ev); MemberBroken?.Invoke(ev);
                    }
                    // Scour also reaches the bed under the tunnel (checked in CheckTunnel)
                    _tunnelScouring = true;
                }
            }
            // Softer ground lowers what pier foundations and tunnel supports carry (not the materials themselves)
            if (_pierCap != null)
                for (int i = 0; i < _pierCap.Length && i < Model.NodeCount; i++)
                    if (_pierCap[i] > 0 && Model.AnchorMaxLoad[i] > 0) Model.AnchorMaxLoad[i] = _pierCap[i] * (1 - Softening);
            ApplyQuake();
            UpdatePortals();
        }

        /// <summary>
        /// Earthquake on the structure: shaking as horizontal inertia, and the wider river as a prescribed
        /// movement of the fixed supports (right bank by the full amount, foundations in the river in proportion).
        /// </summary>
        private void ApplyQuake()
        {
            Model.GroundAccel = GroundAccel;
            if (QuakeWiden <= 0) { Model.SupportShiftX = null; return; }
            double w = Math.Max(1e-6, Level.Terrain.RiverWidth);
            if (Model.SupportShiftX == null || Model.SupportShiftX.Length != Model.NodeCount) Model.SupportShiftX = new double[Model.NodeCount];
            for (int i = 0; i < Model.NodeCount; i++)
                Model.SupportShiftX[i] = Model.FixX[i] ? QuakeWiden * Math.Clamp((i < Model.X0.Length ? Model.X0[i] : Model.X[i]) / w, 0, 1) : 0;
        }

        /// <summary>
        /// Tunnel portals: rain ponds in the ramps (pumps drain it); a flood above the entrance pours in unless
        /// flood gates hold it (closed gates stop tunnel traffic). Ponding deeper than PondBlockDepth stops vehicles
        /// at the entrance without damaging the tunnel.
        /// </summary>
        private void UpdatePortals()
        {
            // Bank lifts replaced the portals: no gate, no ponding, no water pouring in
            return;
        }

        /// <summary>Arc length on the tunnel route where vehicles wait before the left portal.</summary>
        private static double StopBefore(RoadPath p, double x)
        {
            for (int i = 0; i + 1 < p.PointX.Count; i++)
                if (p.PointX[i + 1] >= x)
                {
                    double dx = p.PointX[i + 1] - p.PointX[i], seg = Math.Sqrt(dx * dx + Math.Pow(p.PointY[i + 1] - p.PointY[i], 2));
                    double t = dx > 1e-9 ? Math.Clamp((x - p.PointX[i]) / dx, 0, 1) : 0;
                    return p.SegmentStartS[i] + seg * t;
                }
            return p.TotalLength;
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
            // Foundation capacities before any weather softening (pier bases and tunnel supports)
            _pierCap = new double[Model.NodeCount];
            for (int i = 0; i < Model.NodeCount; i++)
                // Soil softening weakens pier foundations only; tunnel supports ignore the soil
                if (Model.PierFoundation[i] != FoundationType.None) _pierCap[i] = Model.AnchorMaxLoad[i];
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


            // Underwater tunnel: checked at once (static test); a complete tube with drivable grades is a road
            TunnelGeo = TunnelGeometry.Build(Design, Db);
            CheckTunnel();
            if (TunnelGeo != null && TunnelRules.Complete(TunnelGeo, Level.Terrain))
            {
                // Ramps are built at the grade the weakest vehicle can climb; the tube itself must not be steeper
                double grade = TunnelRules.MaxGrade(TunnelGeo), lim = TunnelRules.RampGrade(Level, out var weakest);
                if (grade > lim + RoadPath.SlopeTolerance)
                    RecordIssue(FailureReason.VehicleBlocked, weakest != null
                        ? Loc.F("隧道管身坡度 {0:0.0}% 太陡，{1}最多只能爬 {2:0.0}%（上下拖动接缝圆点让路线更平缓）", (grade * 100), (weakest.Name), (lim * 100))
                        : Loc.F("隧道管身坡度 {0:0.0}% 太陡，车辆最多只能爬 {1:0.0}%（上下拖动接缝圆点让路线更平缓）", (grade * 100), (lim * 100)), null);
                else
                {
                    TunnelPath = RoadPath.FromGround(TunnelRules.Route(Level, TunnelGeo), TunnelRules.Lane);
                    BuildLifts();
                    if(State!=SimState.Running)return;
                }
            }
            // Only trains still need the main-road bridge when road vehicles can take the tunnel
            bool trainsOnMain = TestEvents.Any(e => e.Type == TrafficEventType.Vehicle && e.Lane == 0 && Db.Vehicles.TryGetValue(e.VehicleId, out var vd) && RailTrack.IsTrain(vd));

            // 每一层道路各建一条行驶路径（立交桥有上下两层）
            foreach (int lane in Level.Anchors.Where(a => a.IsRoadStart).Select(a => a.RoadLane).Distinct().OrderBy(x => x))
            {
                var path = RoadPath.Build(Model, Level, out var reason, out var err, out var involved, lane);
                if (path == null) { Fail(reason, err, involved); return; }
                Paths.Add(path);
                // 道路问题只记录，不立即判负：车辆照常出发，开到断口处才会被卡住 / 坠落
                if (reason != FailureReason.None && !(lane == 0 && TunnelOpen && !trainsOnMain)) RecordIssue(reason, err, involved);
            }
            Path = Paths.FirstOrDefault();
            if (Path == null) { Fail(FailureReason.RoadNotConnected, Loc.T("关卡缺少道路锚点"), null); return; }

            // 倾角过大的立柱在测试开始时倒塌
            foreach (int k in Model.TiltCollapsed)
            {
                var a = Design.FindNode(Model.NodeIds[Model.A[k]]); var b = Design.FindNode(Model.NodeIds[Model.B[k]]);
                string text = Loc.F("构件 #{0}（{1}）倾角 {2:0}° 超过 {3:0}°，倒塌", (Model.MemberIds[k]), (Model.Mat[k].Name), (ColumnRules.TiltDegrees(a.Position, b.Position)), (Model.Mat[k].MaxTiltDegrees));
                Model.Broken[k] = false;   // 交给 RemoveMember 统一记录
                if (_pendingBreakText == null) { _pendingBreakText = text; _pendingBreakMode = FailureReason.StructureUnstable; _pendingBreakId = Model.MemberIds[k]; }
                RemoveMember(k, FailureReason.StructureUnstable, text, countsAsBreak: false);
            }

            // Movable bridge: leaves, pivots and deck locks come from the saved mechanism data.
            Bascule.Initialize(Paths);
            if (Bascule.Errors.Count > 0)
                Events.Add(new StructureEvent { Time = 0, MemberId = -1, Mode = FailureReason.None, Text = Loc.T("开启桥设置有误，桥保持闭合：") + string.Join(Loc.T("；"), Bascule.Errors) });
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
                    Bascule.Disable(Loc.F("解除接缝后结构不稳定（节点 #{0} 附近缺少约束），开启桥不会动作。请让每片桥叶只通过一个转轴节点连着岸上结构，并用液压杆从固定结构拉住桥叶", (Math.Abs(Output.UnstableNodeId))));
                    Events.Add(new StructureEvent { Time = 0, MemberId = -1, Mode = FailureReason.None, Text = Loc.T("开启桥设置有误，桥保持闭合：") + Bascule.Fault });
                }
            }
            if (Ships.Count > 0 && !Bascule.Active && Bascule.Linkage == null && Bascule.Plan.Hint() is { } hint)
                Events.Add(new StructureEvent { Time = 0, MemberId = -1, Mode = FailureReason.None, Text = Loc.T("提示：") + hint });
            SolveAndCheck();   // t=0：仅自重（不稳定的部分会在这里垮塌）
            if(State==SimState.Running)EnsureLiftIntegrity();
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
                _issueText = Loc.F("{0}，随后{1}（t={2:0.00}s）", (_pendingBreakText), (text), (Time));
                if (_pendingBreakId >= 0) _issueMembers.Add(_pendingBreakId);
            }
            else
            {
                _issueReason = reason;
                _issueText = Loc.F("{0}（t={1:0.00}s）", (text), (Time));
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
            Fail(_issueReason, Loc.F("{0}\n结果：{1} 在 t={2:0.00}s {3}，坠落", (_issueText), (v.Def.Name), (t), (where)), _issueMembers, FailureReason.VehicleBlocked);
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
            VehicleLiftDy = new double[Math.Max(1, _totalVehicles)],
            LiftPlatformDy = new double[2],LiftDoorOpen = new double[]{1,1},
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
            if (Time >= StaticPhaseTime && _waitingSpawn.Count == 0 && _traffic.PeekNext() != null && Vehicles.All(v => v.Finished || v.Fallen) && Ships.All(s => s.Done))
                _waitingSpawn.Add(_traffic.PopNext());
            // 起点被同层前车占用时，后车排队等待，避免车辆重叠
            for (int w = 0; w < _waitingSpawn.Count; w++)
            {
                var e = _waitingSpawn[w];
                var def = Db.Vehicles[e.VehicleId];
                int vLane = LaneFor(e.Lane, def);
                var vs = new VehicleState { Def = def, Lane = vLane, Path = PathFor(vLane) };
                // Keep existing short-vehicle staging; a long train enters nose-first from the left approach.
                vs.FrontS = def.Kind is "train" or "semi" ? 1.0 - vs.FrontOverhang : -vs.RearOffset + 1.0;
                var last = Vehicles.LastOrDefault(v => v.Lane == vLane && !v.Finished && !v.Fallen);
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
            if (State != SimState.Running) return;

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
                bool liftActuator=Level.MechanicalLift && Lifts.Any(l=>l.Layout?.Members.Any(x=>x.Id==Model.MemberIds[k])==true);
                Model.Prestrain[k] = mat.Extendable && !liftActuator ? stroke * Model.Length[k] * HydraulicFactor : 0;
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
                // Speed control: vehicles brake smoothly to every stop line (movable bridge, lift, vehicle ahead)
                // and pull away gently again, instead of stopping or starting instantly. The stop lines are found by
                // probing one braking distance ahead; the actual step then never passes any of them.
                if (double.IsNaN(v.Speed)) v.Speed = speed;
                double probe = v.FrontS + v.Speed * v.Speed / (2 * VehicleBrake) + Math.Max(v.Speed, speed) * FixedDt + 0.5;
                double next = probe;
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
                // Bank lifts: wait for the platform, drive onto it, ride it (the lift moves the vehicle)
                v.WaitingAtPortal = false;
                if (v.Lane == TunnelRules.Lane && Lifts.Count > 0)
                {
                    if (v.OnLift) { v.Speed = 0; continue; }
                    if (!LiftGate(v, ref next)) return;
                }
                double stopLine = next < probe - 1e-9 ? next : double.PositiveInfinity;
                double allowed = Math.Min(speed, v.Speed + VehicleAccel * FixedDt);
                if (!double.IsInfinity(stopLine)) allowed = Math.Min(allowed, Math.Sqrt(2 * VehicleBrake * Math.Max(0, stopLine - v.FrontS)));
                double followLine = double.PositiveInfinity;
                for (int j = vi - 1; j >= 0; j--)
                {
                    var ahead = Vehicles[j];
                    if (ahead.Finished || ahead.Fallen || ahead.Lane != v.Lane) continue;
                    followLine = ahead.BodyRearS - LiftFollowingGap(v,ahead) - v.FrontOverhang;
                    double aheadSpeed = ahead.OnLift || double.IsNaN(ahead.Speed) ? 0 : ahead.Speed;
                    // Follow the vehicle ahead: brake so the gap is kept even if it stops now
                    allowed = Math.Min(allowed, Math.Sqrt(aheadSpeed * aheadSpeed + 2 * VehicleBrake * Math.Max(0, followLine - v.FrontS)));
                    break;
                }
                allowed = Math.Max(0, allowed);
                next = Math.Max(v.FrontS, Math.Min(v.FrontS + allowed * FixedDt, Math.Min(stopLine, followLine)));
                v.Speed = (next - v.FrontS) / FixedDt;
                {
                    // Trains only on rails, road vehicles only on road surface
                    int segAt = Path.Locate(next, out _);
                    int dk = segAt >= 0 && segAt < Path.SegmentMember.Count ? Path.SegmentMember[segAt] : -1;
                    if (dk >= 0 && !RailTrack.Allows(Model.Mat[dk], v.Def))
                    {
                        _failVehicle = v;
                        string why = RailTrack.IsTrain(v.Def)
                            ? Loc.F("{0}只能在铁轨上行驶：桥面 #{1}（{2}）没有铁轨，请改用铁轨桥面或铁轨公路复合桥面", (v.Def.Name), (Model.MemberIds[dk]), (Model.Mat[dk].Name))
                            : Loc.F("{0}只能在公路路面上行驶：桥面 #{1}（{2}）是铁路专用，请改用公路桥面或铁轨公路复合桥面", (v.Def.Name), (Model.MemberIds[dk]), (Model.Mat[dk].Name));
                        Fail(FailureReason.WrongSurface, why, new List<int> { Model.MemberIds[dk] });
                        return;
                    }
                }
                if (HitsCrossBeam(v, v.FrontS, next, out int beam, out double beamClear))
                {
                    _failVehicle = v;
                    Fail(FailureReason.VehicleBlocked, Loc.F("{0}（高 {1:0.0} m）撞上构件 #{2} 的横梁：横梁离路面只有 {3:0.0} m（普通构件 ≥ {4:0} m 在中点加横梁，柱每 {5:0} m 加一根横杆），请缩短这根构件或把它移出车辆通行高度", (v.Def.Name), (v.Def.Height), (beam), (beamClear), (CrossBeams.MinLength), (CrossBeams.ColumnSpacing)), new List<int> { beam });
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
                        if (_issueReason == FailureReason.None) RecordIssue(FailureReason.VehicleBlocked, Loc.F("{0}无法继续行驶", (v.Def.Name)), null);
                        Fail(_issueReason, Loc.F("{0}\n结果：{1} 在 X={2:0.0} m 处被卡住 {3:0} 秒，无法到达出口", (_issueText), (v.Def.Name), (Path.PointX[^1]), (StuckLimit)), _issueMembers, FailureReason.VehicleBlocked);
                        return;
                    }
                }
                if (Path.Complete && v.BodyRearS > Path.DeckEndS + 8)
                {
                    v.Finished = true; v.FinishTime = Time;
                }
            }

            UpdateLifts();
            CheckTunnel();
            if (State != SimState.Running) return;

            // 3. 求解 + 失效检测
            SolveAndCheck();
            if(State==SimState.Running && !EnsureLiftIntegrity())return;
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
                if (HasFatalIssue) Fail(_issueReason, _issueText + Loc.T("\n车辆虽已通过，但结构出现了致命问题"), _issueMembers);
                else Succeed();
            }
            else if (Time > TimeLimit) Fail(FailureReason.VehicleBlocked, Loc.T("测试超时，车辆或船只未能通过") +
                (Bascule.Fault == null ? "" : Loc.T("\n开启桥：") + Bascule.Fault), null);
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
            // Vehicles inside an immersed / floating tube load the tube elements (collected by CheckTunnel)
            if (!TunnelFlooded)
                foreach (var (x, f) in _tunnelLoads)
                    if (TunnelStructure.AxleOnTube(Model, TunnelGeo, x, f, out var ta) && !Model.Broken[ta.MemberIndex])
                    {
                        if (_axleCount >= _axles.Length) Array.Resize(ref _axles, _axles.Length * 2);
                        _axles[_axleCount++] = ta;
                    }
            return true;
        }

        private void SolveAndCheck()
        {
            if (!CollectAxles(out int blocked, out var blockedVehicle))
            {
                VehicleFell(blockedVehicle, Loc.F("驶上已断裂的桥面 #{0}", (Model.MemberIds[blocked])));
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
                if (worst >= 0 && util > Result.MaxUtilization)
                {
                    Result.WeakestMemberId = Model.MemberIds[worst];
                    var wm = Design.FindMember(Model.MemberIds[worst]);
                    var wa = wm != null ? Design.FindNode(wm.StartNodeId) : null; var wb = wm != null ? Design.FindNode(wm.EndNodeId) : null;
                    Result.WeakestX = wa != null && wb != null ? (wa.Position.X + wb.Position.X) / 2 : double.NaN;
                    Result.WeakestText = Model.Mat[worst].IsTunnel ? Model.Mat[worst].Name : Loc.F("构件 #{0}（{1}）", (Model.MemberIds[worst]), (wm != null && Db.Materials.TryGetValue(wm.MaterialId, out var wmat) ? wmat.Name : "?"));
                    if (Model.Mat[worst].IsTunnel && TunnelGeo != null) { int el = TunnelStructure.ElementOfMemberId(Model.MemberIds[worst]); if (el >= 0) Result.WeakestX = TunnelGeo.MidX(el); }
                }
                Result.MaxUtilization = Math.Max(Result.MaxUtilization, util);
                NotePhaseUtil(util);
                // 开裂提示（钢筋混凝土类）：先开裂、再破坏，反馈更有层次
                for (int ck = 0; ck < Model.MemberCount; ck++)
                {
                    if (!Status[ck].Cracked || _cracked.Contains(ck)) continue;
                    _cracked.Add(ck);
                    Result.CrackedMembers++;
                    MemberCracked?.Invoke(new StructureEvent { Time = Time, MemberId = Model.MemberIds[ck], Mode = FailureReason.None,
                        Text = Loc.F("构件 #{0}（{1}）出现裂缝（利用率 {2:0}%）", (Model.MemberIds[ck]), (Model.Mat[ck].Name), (Status[ck].Utilization * 100)) });
                }

                // 锚点过载：锚点失效（失去约束），随后结构重新求解，可能继续垮塌
                int an = FailureDetector.OverloadedAnchor(Model, Output, out double reaction, out _);
                if (an >= 0)
                {
                    RecordIssue(FailureReason.AnchorOverload, Loc.F("锚点 {0} 支反力 {1:0} kN 超过承载上限 {2:0} kN", (Model.AnchorIds[an]), (reaction / 1000), (Model.AnchorMaxLoad[an] / 1000)), null);
                    Model.FixX[an] = false; Model.FixY[an] = false; Model.AnchorMaxLoad[an] = 0;
                    continue;
                }
                break;
            }
            MergeTube();

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
                Result.MaxDeckSag = Math.Max(Result.MaxDeckSag, sag);
                if (sag > Level.Objectives.MaxDeckDeflection)
                {
                    _deflectionRecorded = true;
                    RecordIssue(FailureReason.DeckDeflectionExceeded, Loc.F("桥面节点 #{0} 下沉 {1:0.00} m，超过限制 {2:0.00} m", (Model.NodeIds[sagNode]), (sag), (Level.Objectives.MaxDeckDeflection)), null);
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
                        ? Loc.F("车轮下的铁轨下沉 {0:0.00} m（上限 {1:0.00} m）", (sag), (RailTrack.MaxSag))
                        : Loc.F("车轮上下晃动速度 {0:0.00} m/s（上限 {1:0.00} m/s）", (bounce), (RailTrack.MaxBounce));
                    Fail(FailureReason.Derailed, Loc.F("{0}在桥面 #{1} 上脱轨：{2}，铁路桥需要更刚、起伏更小", (v.Def.Name), (id), (what)), new List<int> { id });
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
                        string why = Bascule.Fault != null || Bascule.ConfigNote != null ? Loc.T("\n开启桥：") + string.Join(Loc.T("；"), new[] { Bascule.Fault, Bascule.ConfigNote }.Where(x => x != null))
                            : !Bascule.Active && Model.Mat[k].IsDeck ? Loc.T("\n提示：桥面净空不足，需要开启桥。") + (Bascule.Plan.Hint() ?? Loc.T("让每片桥叶只通过一个转轴节点连着岸上结构，液压杆一端接桥叶、一端接固定结构")) : "";
                        RecordIssue(FailureReason.ShipCollision, Loc.F("{0}（高 {1:0.#} m）撞上构件 #{2}（{3}）{4}", (s.Ev.Name), (s.Ev.Height), (Model.MemberIds[k]), (Model.Mat[k].Name), (why)), new[] { Model.MemberIds[k] });
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
                _pendingBreakText = message + (deck ? Loc.T("，桥面随之垮塌") : Loc.T("（局部构件垮塌）"));
                _pendingBreakMode = FailureReason.StructureUnstable; _pendingBreakId = ids[0];
            }
            foreach (int k in removed) RemoveMember(k, FailureReason.StructureUnstable, Loc.F("构件 #{0}（{1}）失去支撑而垮塌", (Model.MemberIds[k]), (Model.Mat[k].Name)), countsAsBreak: false);
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
            // A tube element that breaks (its joints open) floods the tunnel
            if (Model.Mat[k].IsTunnel && TunnelGeo != null)
            {
                int el = TunnelStructure.ElementOfMemberId(Model.MemberIds[k]);
                if (el >= 0 && el < TunnelGeo.Count) FloodTunnel(TunnelStructure.BendingFailure(TunnelGeo, el, Status.Length > k ? Status[k].Moment : 0));
            }
            if (!_collapseRecorded && _overstressBreaks > Level.Objectives.MaxBrokenMembers)
            {
                _collapseRecorded = true;
                RecordIssue(FailureReason.ProgressiveCollapse, Loc.F("已有 {0} 根构件相继失效（首个：{1}）", (BrokenCount), (_firstBreak.Value.Text)), Events.Select(x => x.MemberId));
            }
        }

        private void BreakMember(int k)
        {
            var st = Status[k];
            string modeText = st.Governing switch
            {
                FailureReason.TensionFailure => Loc.T("拉伸破坏"),
                FailureReason.CompressionFailure => Loc.T("压缩破坏/屈曲"),
                FailureReason.DeckBendingFailure => Loc.T("弯曲破坏"),
                _ => Loc.T("破坏"),
            };
            string text = Loc.F("构件 #{0}（{1}）{2}，利用率 {3:0}%", (Model.MemberIds[k]), (Model.Mat[k].Name), (modeText), (st.Utilization * 100));
            // A tube element: the flood message (where, why, what to build) is the report, not a member number
            if (Model.Mat[k].IsTunnel) { RemoveMember(k, st.Governing, Model.Mat[k].Name + " " + modeText); return; }
            // 断裂本身不立即判负：车辆能否通过由实际行驶决定（驶上断裂桥面会坠落）；
            // 首次断裂作为候选根因，若随后出现致命后果，失败报告会追溯到它。桥面断裂会降低完整度评分。
            if (_issueReason == FailureReason.None && _pendingBreakText == null)
            { _pendingBreakText = text + (Model.Mat[k].IsDeck ? Loc.T("，道路断裂") : ""); _pendingBreakMode = st.Governing; _pendingBreakId = Model.MemberIds[k]; }
            RemoveMember(k, st.Governing, text);
        }

        // 普通构件首次断裂（非致命，但若随后出现致命问题，优先作为失败根因）
        private string _pendingBreakText;
        private FailureReason _pendingBreakMode;
        private int _pendingBreakId;

        private void Fail(FailureReason reason, string message, List<int> involved, FailureReason secondary = FailureReason.None)
        {
            if (State == SimState.Failed) return;
            Result.FailurePhase = LivePhase;
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
            string where = "", what = Loc.T("构件");
            if (k >= 0)
            {
                double x = (Model.X[Model.A[k]] + Model.X[Model.B[k]]) / 2;
                r.ProblemX = x;
                double w = Level.Terrain.RiverWidth;
                where = x < w * 0.33 ? Loc.T("靠左岸处") : x > w * 0.67 ? Loc.T("靠右岸处") : Loc.T("桥中间");
                what = Model.Mat[k].Name;
            }
            string who = (_failVehicle ?? Vehicles.LastOrDefault(v => !v.Finished))?.Def.Name ?? Loc.T("车辆");
            switch (r.FailureReason)
            {
                case FailureReason.DeckBendingFailure:
                    r.Headline = Loc.F("{0}在{1}压弯折断了{2}", (who), (where), (what));
                    r.TopSuggestion = Model != null && k >= 0 && Model.Mat[k].IsDeck
                        ? Loc.T("给这段桥面的中点加一根斜撑，或从上方用钢索 / 吊杆拉住它") : Loc.T("这根构件主要在受弯：加斜杆组成三角形，让它主要承受拉压");
                    break;
                case FailureReason.TensionFailure:
                    r.Headline = Loc.F("{0}的{1}被拉断了", (where), (what)); r.TopSuggestion = Loc.T("把它换成钢梁或钢索，或把桁架加高以减小拉力"); break;
                case FailureReason.CompressionFailure:
                    r.Headline = Loc.F("{0}的{1}被压弯（屈曲）了", (where), (what)); r.TopSuggestion = Loc.T("缩短这根受压杆：在它中间加一个节点并连一根斜撑，或改用钢梁"); break;
                case FailureReason.StructureUnstable:
                    r.Headline = Loc.F("{0}的结构缺少支撑而垮塌", (where)); r.TopSuggestion = Loc.T("给这里的节点再连一根不同方向的构件，组成三角形"); break;
                case FailureReason.RoadNotConnected:
                    r.Headline = Loc.F("{0}开到桥面尽头过不去了", (who)); r.TopSuggestion = Loc.T("把桥面一直铺到右岸的“终点”锚点，中间不要留断口"); break;
                case FailureReason.Derailed:
                    r.Headline = Loc.F("{0}在{1}脱轨了", (who), (where)); r.TopSuggestion = Loc.T("铁路桥要更刚：加高桁架、加密桥面下的支点，减小列车经过时铁轨的下沉与起伏"); break;
                case FailureReason.WrongSurface:
                    r.Headline = Loc.F("{0}驶上了不能通行的路面", (who)); r.TopSuggestion = Loc.T("火车走铁轨桥面，汽车走公路桥面；两种都要过就用铁轨公路复合桥面"); break;
                case FailureReason.VehicleBlocked:
                    r.Headline = Loc.F("{0}无法继续行驶", (who)); r.TopSuggestion = Loc.T("检查桥面是否连续、坡度是否过大"); break;
                case FailureReason.ShipCollision:
                    r.Headline = Loc.F("船撞上了{0}的{1}", (where), (what)); r.TopSuggestion = Loc.T("把航道（橙色框）里的构件移走或抬高到船高以上，或改用液压杆让路"); break;
                case FailureReason.AnchorOverload:
                    if (TunnelGeo != null && !TunnelGeo.Type.IsShield && r.FailureMessage.Contains(Loc.T("桥墩")))
                    { r.Headline = Loc.T("托住 / 拉住隧道的河床基础承受不住"); r.TopSuggestion = Loc.T("选中失效的基础改用桩基础，或在旁边多加一根支撑 / 锚索分担"); }
                    else if (r.FailureMessage.Contains(Loc.T("桥墩")))
                    { r.Headline = Loc.T("桥墩下的地基承受不住，基础失效了"); r.TopSuggestion = Loc.T("软土上的桥墩改用桩基础，或把桥墩建在岩石区"); }
                    else { r.Headline = Loc.T("一个锚点承受不住，被拉脱了"); r.TopSuggestion = Loc.T("把载荷分给更多锚点，或减轻桥的自重"); }
                    break;
                case FailureReason.TunnelFlooded:
                    r.Headline = Loc.T("隧道管节强度不够，被压坏了");
                    r.TopSuggestion = r.FailureMessage.Contains(Loc.T("接缝弯矩"))
                        ? (TunnelGeo?.Type.IsFloating == true ? Loc.T("在出问题的接缝圆点加锚索（或换复合钢索），或换低一档浮力；升级管节等级可以提高接缝抗弯") : Loc.T("在出问题的接缝下面加桥墩 / 柱子，缩短支撑间距；升级管节等级可以提高接缝抗弯"))
                        : Loc.T("选中变红的管节升级为加强 / 重型（强度更高）；也可以把隧道抬浅一些减小水压");
                    break;
                case FailureReason.LiftOverload:
                    r.Headline = Loc.T("电梯平台承受不住，被压坏了"); r.TopSuggestion = Loc.T("选中隧道，在属性栏把电梯平台加长：平台越长越结实"); break;
                case FailureReason.FloodScour:
                    r.Headline = Loc.T("洪水冲坏了桥墩"); r.TopSuggestion = Loc.T("冲刷区的桥墩改用桩基础或移到岩石区；洪水关不要用木桥墩"); break;
                case FailureReason.DeckDeflectionExceeded:
                    r.Headline = Loc.F("桥面{0}下沉太多", (where)); r.TopSuggestion = Loc.T("加高桁架或增加支点，提高桥的刚度"); break;
                case FailureReason.ProgressiveCollapse:
                    r.Headline = Loc.F("{0}的构件接连断裂", (where)); r.TopSuggestion = Loc.T("先加强最先断裂的那根构件，并增加冗余斜撑"); break;
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
            // Conservative serial reference includes door operation and unloading. Pooled trips can finish sooner.
            var lifts = Lifts.OrderBy(l => l.A).ToList();
            var liftFree = new double[lifts.Count];
            foreach (var e in TestEvents.OrderBy(ev => ev.Time))
            {
                if (e.Type == TrafficEventType.Vehicle && Db.Vehicles.TryGetValue(e.VehicleId, out var def) && def.Speed > 0)
                {
                    var path = PathFor(LaneFor(e.Lane, def));
                    if (path == null) continue;
                    if (path != TunnelPath || lifts.Count == 0) { t = Math.Max(t, e.Time + (path.DeckEndS + 8 + def.Length) / def.Speed); continue; }
                    double clock = e.Time, s = -def.Length;
                    for (int k = 0; k < lifts.Count; k++)
                    {
                        var l = lifts[k]; double ride = l.Height / l.Speed;
                        clock += Math.Max(0, l.A - s) / def.Speed;
                        double start = Math.Max(clock, liftFree[k]);
                        clock = start + l.BoardingSeconds + 2*l.DoorSeconds + ride;
                        double clear=(l.Length+def.Length+TunnelRules.LiftCfg.Margin)/def.Speed;
                        liftFree[k] = clock + clear + 2*l.DoorSeconds + ride;
                        s = l.A + l.Shift;
                    }
                    t = Math.Max(t, clock + Math.Max(0, path.DeckEndS + 8 - s) / def.Speed);
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
            double wu = Result.PhaseMaxUtil[(int)TestPhase.Environment];
            if (wu >= 0) Result.WeatherMargin = wu > 1e-9 ? 1 / wu - 1 : 9.99;
            FillVehicleResults();
            double total = 0, intact = 0;
            for (int k = 0; k < Model.MemberCount; k++)
            {
                if (!Model.Mat[k].IsDeck) continue;
                total += Model.Length[k];
                if (!Model.Broken[k]) intact += Model.Length[k];
            }
            Result.DeckIntegrity = total > 0 ? intact / total : 1;
            Result.ReferenceCost = Level.Objectives.ReferenceCost;
            Result.RiverWidth = Level.Terrain.RiverWidth;
            Result.Disturbance = Disturbance();
            Result.Contract = EngineeringScore.Config.Contract(Design.Contract) != null ? Design.Contract : null;
            ComputeScore(Result);
        }

        /// <summary>Riverbed / ground disturbance length (m) for the environment factor.</summary>
        private double Disturbance()
        {
            var c = EngineeringScore.Config; double d = 0;
            for (int i = 0; i < Model.NodeCount; i++) if (Model.PierFoundation[i] != FoundationType.None) d += c.PierDisturbance;
            if (TunnelGeo != null)
            {
                double len = 0; for (int i = 0; i < TunnelGeo.Count; i++) len += TunnelGeo.Len[i];
                if (TunnelGeo.Type.IsFloating) { }   // its bed anchor blocks are foundations, counted above
                else if (TunnelGeo.Type.IsShield) d += len * c.ShieldDisturbance;
                else d += len * c.ImmersedDisturbance;
            }
            return d;
        }

        /// <summary>Engineering Score base (uncapped, only for passed tests); achievements are added by EngineeringScore.EvaluateAchievements.</summary>
        public static void ComputeScore(SimulationResult r) => EngineeringScore.ComputeBase(r);
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
            f.Rain = Rain; f.Overcast = Overcast; f.Shake = GroundAccel; f.Widen = QuakeWiden;
            Array.Copy(DisplayDisplacement, f.Displacement, Math.Min(DisplayDisplacement.Length, f.Displacement.Length));
            f.BridgeState = Bascule.StatusText;
            for(int i=0;i<f.LiftPlatformDy.Length;i++)
            {f.LiftPlatformDy[i]=i<Lifts.Count?Lifts[i].Dy:0;f.LiftDoorOpen[i]=i<Lifts.Count?Lifts[i].DoorOpen:1;}
            Array.Copy(KinematicDisplacement, f.Kinematic, Math.Min(KinematicDisplacement.Length, f.Kinematic.Length));
            for (int i = 0; i < Ships.Count && i < f.ShipZ.Length; i++) f.ShipZ[i] = Ships[i].Spawned && !Ships[i].Done ? Ships[i].Z : double.NaN;
            for (int k = 0; k < Model.MemberCount; k++) { f.Broken[k] = Model.Broken[k]; f.Utilization[k] = (float)Status[k].Utilization; }
            for (int i = 0; i < f.VehicleS.Length; i++)
            {
                f.VehicleActive[i] = i < Vehicles.Count && !Vehicles[i].Finished;
                f.VehicleS[i] = i < Vehicles.Count ? Vehicles[i].FrontS : 0;
                f.VehicleLiftDy[i] = i < Vehicles.Count ? Vehicles[i].LiftDy : 0;
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
            // Build-mode tunnel state: shell pressure (static water) merged with the tube beam on its supports
            sim.TunnelGeo = TunnelGeometry.Build(sim.Design, db);
            if (sim.TunnelGeo != null && db.TunnelConfig != null)
            {
                TunnelAnalysis.Evaluate(sim.TunnelGeo, level, db.TunnelConfig, TunnelAnalysis.StaticEnvironment(level, db.TunnelConfig), null, sim.TunnelStatus);
                if (sim.Model.TubeMember.Length > 0) TunnelStructure.Merge(sim.Model, sim.Output, sim.Status, sim.TunnelGeo, sim.TunnelStatus, db.TunnelConfig, level, sim.Output.Stable);
            }
            return sim;
        }

        /// <summary>Build-mode tunnel state of a design: shell pressure + the tube beam on the player's supports.</summary>
        public static TunnelState StaticTunnel(BridgeDesign design, LevelDefinition level, GameDatabase db) =>
            StaticAnalysis(design, level, db).TunnelStatus;

        /// <summary>无界面快速运行整个测试（用于自动化测试与参考方案校验）。</summary>
        public SimulationResult RunToEnd()
        {
            if (State == SimState.NotStarted) Initialize();
            while (State == SimState.Running) Step();
            return Result;
        }
    }
}
