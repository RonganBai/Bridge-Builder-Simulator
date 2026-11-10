using System;
using System.Collections.Generic;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using Godot;

namespace BridgeArchitect3D.Traffic
{
    /// <summary>
    /// 通车测试控制器（Godot 层）：按固定步长驱动 BridgeSimulation，支持暂停 / 继续 / 1×2×4× 速度，
    /// 更新桥梁变形、受力颜色、车辆位置，生成断裂碎片，并提供失败回放与首次失效快照。
    /// </summary>
    public partial class TrafficManager : Node3D
    {
        public BridgeSimulation Sim { get; private set; }
        public bool Paused { get; set; }
        public int SpeedMultiplier { get; set; } = 1;
        public bool ShowStress { get; set; } = true;
        public float DeformationScale { get; set; } = 30f;
        public int SelectedMember { get; set; } = -1;
        public bool IsReplaying => _replayMode != ReplayMode.None;

        public event Action<SimulationResult> Finished;
        public event Action<StructureEvent> MemberBrokenVisual;

        private enum ReplayMode { None, Replay, Snapshot }
        private ReplayMode _replayMode;
        private double _replayTime;

        private BridgeRenderer _renderer;
        private readonly List<VehicleController> _vehicles = new();
        private Node3D _debris;
        private double _acc;
        private bool _finishedRaised;
        private float[] _util = Array.Empty<float>();
        private readonly Queue<StructureEvent> _pendingBreaks = new();

        public void Init(BridgeRenderer renderer) { _renderer = renderer; }

        /// <summary>开始（或重新开始）测试。使用方案副本，不修改原始方案。</summary>
        public void Begin(BridgeDesign design, LevelDefinition level, GameDatabase db)
        {
            Clear();
            _debris = new Node3D { Name = "Debris" };
            AddChild(_debris);
            _renderer.Rebuild(design);
            Sim = new BridgeSimulation(design, level, db);
            Sim.MemberBroken += e => _pendingBreaks.Enqueue(e);
            Sim.MemberCracked += e => _pendingCracks.Enqueue(e);
            Sim.Initialize();
            _util = new float[Sim.Model.MemberCount];
            _shipZ = new double[Sim.Ships.Count];
            Paused = false; _acc = 0; _finishedRaised = false; _replayMode = ReplayMode.None;
            FlushBreaks();
            UpdateVisuals();
        }

        /// <summary>结束测试并清除车辆与碎片。</summary>
        public void Clear()
        {
            foreach (var v in _vehicles) v.QueueFree();
            foreach (var sh in _ships) sh.QueueFree();
            _ships.Clear();
            _vehicles.Clear();
            _debris?.QueueFree(); _debris = null;
            _pendingBreaks.Clear();
            Sim = null;
            _replayMode = ReplayMode.None;
        }

        public override void _Process(double delta)
        {
            if (Sim == null) return;
            if (_replayMode != ReplayMode.None) { ProcessReplay(delta); return; }
            if (Sim.State == SimState.Running && !Paused)
            {
                _acc += delta * SpeedMultiplier;
                int steps = 0;
                while (_acc >= BridgeSimulation.FixedDt && steps < 16 && Sim.State == SimState.Running)
                {
                    Sim.Step(); _acc -= BridgeSimulation.FixedDt; steps++;
                }
                if (steps >= 16) _acc = 0;   // 防止卡顿后积累过多步数
                FlushBreaks();
                UpdateVisuals();
            }
            if (Sim.State != SimState.Running && !_finishedRaised)
            {
                _finishedRaised = true;
                if (Sim.State == SimState.Failed) DropVehiclesOnBrokenDeck();
                UpdateVisuals();
                Finished?.Invoke(Sim.Result);
            }
        }

        private readonly Queue<StructureEvent> _pendingCracks = new();
        public event Action<StructureEvent> MemberCrackedVisual;

        private void FlushBreaks()
        {
            while (_pendingCracks.Count > 0) MemberCrackedVisual?.Invoke(_pendingCracks.Dequeue());
            while (_pendingBreaks.Count > 0)
            {
                var e = _pendingBreaks.Dequeue();
                SpawnDebris(e.MemberId);
                MemberBrokenVisual?.Invoke(e);
            }
        }

        public void UpdateVisuals()
        {
            if (Sim?.Model == null) return;
            var m = Sim.Model;
            for (int k = 0; k < m.MemberCount; k++) _util[k] = (float)Sim.Status[k].Utilization;
            for (int i = 0; i < Sim.Ships.Count && i < _shipZ.Length; i++)
                _shipZ[i] = Sim.Ships[i].Spawned && !Sim.Ships[i].Done ? Sim.Ships[i].Z : double.NaN;
            ApplyState(Sim.Output.Displacement, Sim.KinematicDisplacement, m.Broken, _util, Sim.Vehicles.Count, i => Sim.Vehicles[i].FrontS, i => !Sim.Vehicles[i].Finished, _shipZ);
        }

        private double[] _display = Array.Empty<double>();
        private double[] _shipZ = Array.Empty<double>();
        private readonly List<ShipController> _ships = new();

        private void ApplyState(double[] disp, double[] kin, bool[] broken, float[] util, int vehicleCount, Func<int, double> frontS, Func<int, bool> active, double[] shipZ)
        {
            var m = Sim.Model;
            // 显示位移 = 液压杆伸缩位移（真实比例）+ 弹性变形 × 放大系数
            if (_display.Length != disp.Length) _display = new double[disp.Length];
            for (int i = 0; i < disp.Length; i++)
            {
                double kv = kin != null && i < kin.Length ? kin[i] : 0;
                _display[i] = kv + (disp[i] - kv) * DeformationScale;
            }
            disp = _display;
            _renderer.UpdateTransforms(m, disp, 1f);
            UpdateShips(shipZ);
            _renderer.ColorForTest(m, broken, util, ShowStress, SelectedMember);
            if (Sim.Path == null) return;
            while (_vehicles.Count < Math.Min(vehicleCount, Sim.Vehicles.Count))
            {
                var vc = new VehicleController();
                AddChild(vc);
                vc.Setup(Sim.Vehicles[_vehicles.Count].Def);
                _vehicles.Add(vc);
            }
            for (int i = 0; i < _vehicles.Count; i++)
            {
                var vc = _vehicles[i];
                bool act = i < vehicleCount && active(i);
                vc.Visible = act;
                if (!act) continue;
                double s = frontS(i);
                var path = Sim.Vehicles[i].Path ?? Sim.Path;
                float off = BridgeRenderer.DeckSurfaceOffset;
                var d2 = disp;
                // 多节车辆（火车）与行人队伍：每一节 / 每个人分别沿路径摆放
                vc.PlaceAlongPath(sv => { path.Evaluate(sv, d2, 1, out double x, out double y); return new Vector3((float)x, (float)y + off, 0); }, s);
            }
        }

        private void UpdateShips(double[] shipZ)
        {
            if (shipZ == null) return;
            while (_ships.Count < Sim.Ships.Count)
            {
                var sc = new ShipController();
                AddChild(sc);
                sc.Setup(Sim.Ships[_ships.Count].Ev, Sim.Level.Terrain.WaterLevel);
                _ships.Add(sc);
            }
            for (int i = 0; i < _ships.Count; i++)
            {
                double z = i < shipZ.Length ? shipZ[i] : double.NaN;
                _ships[i].Visible = !double.IsNaN(z);
                if (_ships[i].Visible) _ships[i].SetZ((float)z);
            }
        }

        private void DropVehiclesOnBrokenDeck()
        {
            for (int i = 0; i < _vehicles.Count && i < Sim.Vehicles.Count; i++)
                if (Sim.Vehicles[i].Fallen) _vehicles[i].StartFalling();
        }

        private void SpawnDebris(int memberId)
        {
            if (_debris == null) return;
            foreach (var (xf, mat) in _renderer.MemberParts(memberId))
            {
                // 碎片仅为视觉效果：断裂判定已由结构求解完成
                var scale = xf.Basis.Scale;
                var size = new Vector3(Mathf.Max(scale.X * 0.5f, 0.05f), Mathf.Max(scale.Y, 0.05f), Mathf.Max(scale.Z, 0.05f));
                var basis = xf.Basis.Orthonormalized();
                for (int half = 0; half < 2; half++)
                {
                    var body = new RigidBody3D { Mass = 50 };
                    var center = xf.Origin + basis.X * (half == 0 ? -size.X / 2 : size.X / 2);
                    body.Transform = new Transform3D(basis, center);
                    body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
                    body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat });
                    _debris.AddChild(body);
                    body.LinearVelocity = new Vector3(half == 0 ? -0.8f : 0.8f, 0.5f, 0);
                    body.AngularVelocity = new Vector3(0, 0, half == 0 ? 1.2f : -1.2f);
                    var timer = GetTree().CreateTimer(6.0);
                    timer.Timeout += () => { if (IsInstanceValid(body)) body.QueueFree(); };
                }
            }
        }

        // ---------------- 回放 ----------------
        public bool CanReplay => Sim != null && Sim.State == SimState.Failed && Sim.HistoryCount > 1;

        public void StartReplay()
        {
            if (!CanReplay) return;
            _replayMode = ReplayMode.Replay; _replayTime = 0;
            foreach (var v in _vehicles) v.ResetState();
        }

        public void ShowFailureSnapshot()
        {
            if (Sim?.FailureSnapshot == null || Sim.State != SimState.Failed) return;
            _replayMode = ReplayMode.Snapshot;
            foreach (var v in _vehicles) v.ResetState();
            ApplyFrame(Sim.FailureSnapshot);
        }

        public void StopReplay()
        {
            _replayMode = ReplayMode.None;
            UpdateVisuals();
        }

        private void ProcessReplay(double delta)
        {
            if (_replayMode == ReplayMode.Snapshot) return;
            _replayTime += delta * SpeedMultiplier;
            int idx = (int)(_replayTime / 0.1);
            if (idx >= Sim.HistoryCount) { idx = Sim.HistoryCount - 1; _replayMode = ReplayMode.Snapshot; }
            ApplyFrame(Sim.GetHistoryFrame(idx));
        }

        private void ApplyFrame(SimFrame f)
        {
            ApplyState(f.Displacement, f.Kinematic, f.Broken, f.Utilization, f.VehicleS.Length, i => f.VehicleS[i], i => f.VehicleActive[i], f.ShipZ);
        }

        public double ReplayFrameTime => Sim != null && Sim.HistoryCount > 0 ? Sim.GetHistoryFrame(Math.Min((int)(_replayTime / 0.1), Sim.HistoryCount - 1)).Time : 0;
    }
}
