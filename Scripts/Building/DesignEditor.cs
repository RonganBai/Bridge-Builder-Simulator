using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

namespace BridgeArchitect3D.Building
{
    /// <summary>建造操作的结果：是否成功以及失败原因（直接显示给玩家）。</summary>
    public readonly struct BuildResult
    {
        public readonly bool Ok;
        public readonly string Message;
        public readonly int CreatedId;
        public BuildResult(bool ok, string msg, int id = 0) { Ok = ok; Message = msg; CreatedId = id; }
        public static BuildResult Fail(string msg) => new(false, msg);
        public static BuildResult Success(int id = 0, string msg = "") => new(true, msg, id);
    }

    /// <summary>构件预览信息（连接预览时显示）。</summary>
    public struct MemberPreview
    {
        public string MaterialName;
        public double Length;
        public double Cost;
        public bool Allowed;
        public string Reason;
        /// <summary>允许建造但需要提醒的问题（例如立柱倾角过大）。</summary>
        public string Warning;
    }

    /// <summary>
    /// 建造规则与编辑操作（不依赖 Godot，可单独测试）。
    /// 负责：位置约束、重复检测、长度限制、预算同步、撤销/重做。
    /// 所有修改都通过本类完成，保证预算始终与构件数据一致。
    /// </summary>
    public partial class DesignEditor
    {
        public const double GridSize = 0.25;
        /// <summary>Node snap / pick radius in metres; node models are drawn with the same radius.</summary>
        public const double NodeSnapRadius = 0.25;          // 网格吸附间距 m
        public const double MinNodeSpacing = 0.25;   // 节点最小间距 m（防止重复节点）
        public const double MinMemberLength = 0.25;   // 最短构件 m
        private const int MaxHistory = 200;

        public BridgeDesign Design { get; private set; }
        public LevelDefinition Level { get; }
        public GameDatabase Db { get; }

        private readonly List<BridgeDesign> _undo = new();
        private readonly List<BridgeDesign> _redo = new();

        /// <summary>方案发生任何变化时触发（UI 与渲染据此刷新）。</summary>
        public event Action Changed;

        private bool _previewing;

        public DesignEditor(BridgeDesign design, LevelDefinition level, GameDatabase db, bool migrate = true)
        {
            Level = level; Db = db;
            Design = design ?? BridgeDesign.CreateForLevel(level);
            LiftChallengeRules.Ensure(Design,Level,Db);
            if(Level.MechanicalLift)LiftMechanics.AddMounts(Design,Level,Db);
            CrossBeams.SplitExistingBlocking(Design, Level, Db);
            RecalculateAll();
            if (!migrate) return;
            MigrateStructuralTunnel();
            MigrateLiftStructure();
        }

        // ---------------- Bank lifts: ordinary members in the shafts ----------------

        /// <summary>
        /// Older saves have no lift members (Lift == null or Lift.Built == null). Once, the fixed lift nodes are created
        /// and the cheapest template that carries every road vehicle of the level is generated as ordinary members.
        /// Afterwards Built = 1 and this changes nothing.
        /// </summary>
        public void MigrateLiftStructure()
        {
            var r = Design.Tunnel;
            if (r == null || r.Lift?.Built != null || TunnelGeo == null) return;
            if(Level.MechanicalLift){SyncLifts(TunnelGeo);LiftMechanics.AddMounts(Design,Level,Db);r.Lift.Built=1;return;}
            RegenerateTunnel();
            var t = Level.ProvidedInfrastructure!=null?LiftCfgOf.Templates["light"].Clone():LiftTemplateForLevel();
            GenerateLifts(t);
            RegenerateTunnel();
        }

        public LiftConfig LiftCfgOf => LiftStructure.Cfg(Db);

        /// <summary>The two lifts as found in the design (empty without a tunnel).</summary>
        public LiftLayout[] Lifts() => TunnelGeo == null ? Array.Empty<LiftLayout>() : LiftStructure.Analyze(Design, Level, Db);

        /// <summary>Travel heights of both bank lifts, m.</summary>
        public double[] LiftTravels() => TunnelGeo == null ? Array.Empty<double>() : TunnelRules.Shafts(Level, TunnelGeo).Select(s => s.Height).ToArray();

        /// <summary>Heaviest road vehicle of the level, N.</summary>
        public double LiftLoadForLevel() => LevelRoadVehicles().Select(v => v.Mass * StructuralModel.Gravity).DefaultIfEmpty(0).Max();

        /// <summary>Road vehicles of the level (distinct).</summary>
        public IEnumerable<VehicleDefinition> LevelRoadVehicles() => Level.TrafficEvents
            .Where(e => e.Type == TrafficEventType.Vehicle && Db.Vehicles.TryGetValue(e.VehicleId ?? "", out var v) && !RailTrack.IsTrain(v))
            .Select(e => e.VehicleId).Distinct().Select(id => Db.Vehicles[id]);

        /// <summary>Worst utilization of the current lifts over every road vehicle of the level standing alone on the platform.</summary>
        public double LiftWorstUtil(out LiftCheck worst)
        {
            worst = null; double u = 0; var c = LiftCfgOf;
            foreach (var L in Lifts())
                foreach (var axles in LevelRoadVehicles().Select(v => LiftStructure.VehicleAxles(v, L.Shaft, TunnelRules.LiftCfg.Margin,Design.Tunnel?.Lift?.BoardingPolicy??LiftOperation.Shared)).Append(Array.Empty<(double, double)>()))
                {
                    var k = LiftStructure.Check(Design, Level, Db, L, axles);
                    if (worst == null || k.Util > u) { u = k.Util; worst = k; }
                }
            return u;
        }

        /// <summary>Cheapest template whose generated members carry every road vehicle of the level (heaviest template if none does).</summary>
        public LiftDesign LiftTemplateForLevel()
        {
            var c = LiftCfgOf; LiftDesign best = null; double bestCost = double.MaxValue, bestUtil = double.MaxValue; LiftDesign safest = null;
            foreach (var t in c.Templates.Values)
            {
                var trial = new DesignEditor(Design.Clone(), Level, Db, migrate: false);
                trial.GenerateLifts(t); trial.RegenerateTunnel();
                double u = trial.LiftWorstUtil(out _), cost = trial.UsedCost;
                if (u <= 1 && cost < bestCost) { best = t; bestCost = cost; }
                if (u < bestUtil) { bestUtil = u; safest = t; }
            }
            return (best ?? safest ?? c.Templates.Values.First()).Clone();
        }

        private string AllowedOr(string id, params string[] fallback)
        {
            if (id != null && Level.AllowedMaterials.Contains(id)) return id;
            var mat = Db.GetMaterial(id);
            foreach (var f in fallback) if (Level.AllowedMaterials.Contains(f)) return f;
            // Same component type, any allowed one
            var same = Level.AllowedMaterials.Select(Db.GetMaterial).FirstOrDefault(m => m != null && mat != null && m.ComponentType == mat.ComponentType && !m.IsTunnel && !m.IsLift && !m.IsPier);
            return same?.MaterialId ?? id;
        }

        /// <summary>Removes the members / free nodes of every lift and generates template t in both shafts (no undo step).</summary>
        private void GenerateLifts(LiftDesign t)
        {
            var r = Design.Tunnel; var g = TunnelGeo;
            if (r == null || g == null) return;
            SyncLifts(g);
            foreach (var L in Lifts())
            {
                var free = L.Nodes.Where(id => !Design.FindNode(id).IsTunnelNode).ToHashSet();
                Design.Members.RemoveAll(m => L.Members.Contains(m));
                Design.Nodes.RemoveAll(n => free.Contains(n.Id) && !Design.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id));
                var pits = L.Pits.OrderBy(p => p.Position.X).ToList();
                string Allowed(string id) => Db.GetMaterial(id)?.ComponentType switch
                {
                    ComponentType.Deck => AllowedOr(id, "deck_steel", "road"),
                    ComponentType.Cable => AllowedOr(id, "cable_composite", "cable"),
                    ComponentType.Column => AllowedOr(id, "concrete", "steel"),
                    _ => AllowedOr(id, "steel")
                };
                var (nodes, members) = LiftStructure.Template(t, L.Shaft, L.Deck0, L.Deck1, pits[0], pits[^1], Db, () => Design.NextNodeId++, Allowed);
                Design.Nodes.AddRange(nodes);
                foreach (var (a, b, mid) in members)
                {
                    var na = Design.FindNode(a); var nb = Design.FindNode(b); var mat = Db.GetMaterial(mid);
                    double len = Vec3d.Distance(na.Position, nb.Position);
                    Design.Members.Add(new BridgeMember { Id = Design.NextMemberId++, StartNodeId = a, EndNodeId = b, MaterialId = mid, Length = len, Cost = len * mat.CostPerMeter, CostStroke = DefaultCostStroke(mat) });
                }
            }
            string policy=r.Lift?.BoardingPolicy;
            r.Lift = t.Clone(); r.Lift.Built = 1;
            if(LiftOperation.Valid(policy))r.Lift.BoardingPolicy=policy;
        }

        /// <summary>"Rebuild the lift structure": regenerate both lifts from a template (null = cheapest that carries the level's vehicles).</summary>
        public BuildResult RebuildLifts(string templateId = null)
        {
            var r = Design.Tunnel;
            if (r == null || TunnelGeo == null) return BuildResult.Fail(Loc.T("没有隧道"));
            var c = LiftCfgOf;
            LiftDesign t = templateId != null && c.Templates.TryGetValue(templateId, out var tt) ? tt.Clone() : LiftTemplateForLevel();
            PushUndo();
            GenerateLifts(t);
            RegenerateTunnel();
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.F("已按{0}重建两岸电梯结构：平台、吊索和井道柱子都是普通构件，可以像建桥一样修改。", (LiftConfig.TemplateName(t.Template))) + LiftSummary());
        }

        /// <summary>Drive and counterweight of both lifts (set on a sheave node: the top of a hoist rope).</summary>
        public BuildResult SetLiftOperation(string policy)
        {
            if(Design.Tunnel?.Lift==null || !LiftOperation.Valid(policy))return BuildResult.Fail(Loc.T("无效的电梯运营方式"));
            if(Design.Tunnel.Lift.BoardingPolicy==policy)return BuildResult.Success();
            PushUndo();Design.Tunnel.Lift.BoardingPolicy=policy;RegenerateTunnel();Changed?.Invoke();
            return BuildResult.Success(0,Loc.T("已切换：")+LiftOperation.Name(policy));
        }

        public BuildResult SetLiftMachine(string drive = null, int counterweightSteps = 0)
        {
            var r = Design.Tunnel;
            if (r?.Lift == null) return BuildResult.Fail(Loc.T("没有隧道"));
            var c = LiftCfgOf;
            if(Level.MechanicalLift)return BuildResult.Fail("请安装真实液压缸和配重块；本关没有内置驱动机。");
            if (drive != null && !c.Drives.ContainsKey(drive)) return BuildResult.Fail(Loc.T("没有这种驱动"));
            PushUndo();
            if (drive != null) r.Lift.Drive = drive;
            r.Lift.Counterweight = Math.Clamp(r.Lift.Counterweight + counterweightSteps * c.CounterweightStep, 0, c.MaxCounterweight);
            RegenerateTunnel();
            Changed?.Invoke();
            return BuildResult.Success(0, LiftSummary());
        }

        /// <summary>The lift and sheave a node belongs to (sheave = top node of a hoist rope).</summary>
        public LiftLayout LiftOfNode(int nodeId, out bool sheave)
        {
            sheave = false;
            foreach (var L in Lifts()) if (L.Nodes.Contains(nodeId)) { sheave = L.Sheaves.ContainsKey(nodeId); return L; }
            return null;
        }

        /// <summary>One line on the current lifts: problems, drive, counterweight and the worst vehicle case.</summary>
        public string LiftSummary()
        {
            var r = Design.Tunnel; if (r?.Lift == null) return "";
            var c = LiftCfgOf; var lifts = Lifts();
            if(Level.MechanicalLift)return LiftMechanics.Summary(Design,Level,Db);
            var bad = lifts.FirstOrDefault(L => L.Problem != null);
            if (bad != null) return Loc.F("{0}：{1}", (LiftStructure.Name(bad)), (bad.Problem));
            double u = LiftWorstUtil(out var w);
            var drive = c.Drives.GetValueOrDefault(r.Lift.Drive ?? "") ?? c.Drives.Values.First();
            return Loc.F("电梯：{0} · 配重 {1:0} 吨 · 本关车辆单独上平台时最大利用率 {2:0}%", (drive.Name), (r.Lift.Counterweight), (u * 100))
                + (w != null && u > 1 ? Loc.F("（{0}：{1}）", (w.Where), (w.Why)) : "");
        }

        /// <summary>
        /// Keeps the fixed lift nodes where the shafts are now (platform ends at the lower landing, pit bases under the
        /// end walls) and carries the player's lift members along: when a shaft gets deeper / its platform longer, nodes in
        /// the lower half follow the landing, nodes on the outer half follow the outer end.
        /// </summary>
        private void SyncLifts(TunnelGeometry g)
        {
            if (Design.Tunnel == null || g == null) return;
            var c = LiftCfgOf; var shafts = TunnelRules.Shafts(Level, g);
            for (int bank = 1; bank <= 2; bank++)
            {
                var s = shafts[bank - 1];
                BridgeNode Fixed(string role, int index = 0) => Design.Nodes.Where(n => n.LiftBank == bank && n.LiftRole == role).OrderBy(n => n.Position.X).ElementAtOrDefault(index);
                var d0 = Fixed("deck0"); var d1 = Fixed("deck1");
                if (d0 != null && d1 != null)
                {
                    double ox0 = d0.Position.X, ox1 = d1.Position.X, ob = d0.Position.Y;
                    double dx0 = s.X0 - ox0, dx1 = s.X1 - ox1, db = s.Bottom - ob;
                    if (Math.Abs(dx0) + Math.Abs(dx1) + Math.Abs(db) > 1e-9)
                    {
                        var old = new TunnelRules.LiftShaft(s.Left, ox0, ox1, s.Top, ob);
                        double midY = (s.Top + ob) / 2;
                        foreach (var n in Design.Nodes)
                        {
                            if (n.Provided || n.LiftBank != 0 || n.IsAnchor || n.TunnelPoint || n.TunnelJoint > 0 || n.PierBase || !LiftStructure.InZone(c, old, n.Position.X, n.Position.Y)) continue;
                            // Between the platform ends: stretch proportionally (keeps the order of the nodes);
                            // beside them (shaft columns): follow the nearer end
                            double x = n.Position.X, nx = x < ox0 ? x + dx0 : x > ox1 ? x + dx1
                                : s.X0 + (x - ox0) * (s.X1 - s.X0) / Math.Max(1e-9, ox1 - ox0);
                            n.Position = new Vec3d(nx, n.Position.Y + (n.Position.Y < midY ? db : 0));
                        }
                    }
                }
                void Place(string role, int index, double x, double y)
                {
                    var n = Fixed(role, index);
                    if (n == null) { n = new BridgeNode { Id = Design.NextNodeId++, LiftBank = bank, LiftRole = role }; Design.Nodes.Add(n); }
                    n.Position = new Vec3d(x, y);
                }
                Place("deck0", 0, s.X0, s.Bottom); Place("deck1", 0, s.X1, s.Bottom);
                double pit = LiftStructure.PitY(c, s);
                Place("pit", 0, s.X0 - c.ColumnOffset, pit); Place("pit", 1, s.X1 + c.ColumnOffset, pit);
            }
        }

        private bool InLiftZone(double x, double y)
        {
            var g = TunnelGeo; if (g == null) return false;
            var c = LiftCfgOf;
            return TunnelRules.Shafts(Level, g).Any(s => LiftStructure.InZone(c, s, x, y));
        }

        /// <summary>
        /// Older saves (TunnelRoute.Structure == null): the tube had built-in supports (immersed) or a cable count per
        /// element (floating). That capacity becomes visible, costed members once: a concrete pier under every immersed
        /// joint (cheapest foundation that carries the old support load), and for floating tubes per joint
        /// ceil((cables left + cables right) / 2) cables of the same strength class down to bed anchor blocks (foundation
        /// sized for the cable's strength). Marked with Structure = 1, so running it again changes nothing.
        /// </summary>
        public void MigrateStructuralTunnel()
        {
            var r = Design.Tunnel;
            if (r == null || r.Structure != null) return;
            r.Structure = 1;
            var g = TunnelGeometry.Build(Design, Db);
            if (g == null || g.Type.IsShield) return;
            // Old per-joint capacity, read before the joints are rebuilt
            var oldSupport = new double[g.Count]; var oldCables = new int[g.Count]; var strong = new bool[g.Count];
            for (int j = 1; j < g.Count; j++)
            {
                oldSupport[j] = Math.Min(g.Grade[j - 1].SupportLoad, g.Grade[j].SupportLoad);
                if (g.Type.IsFloating)
                {
                    oldCables[j] = (int)Math.Ceiling((g.CableCount[j - 1] + g.CableCount[j]) / 2.0);
                    strong[j] = g.CableId[j - 1] == "strong" || g.CableId[j] == "strong";
                }
            }
            // Old joint nodes (on the roof) without members are rebuilt at the tube bottom
            var bare = Design.Nodes.Where(n => n.IsTunnelSupport && !n.TunnelPoint && !Design.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id)).Select(n => n.Id).ToHashSet();
            Design.Nodes.RemoveAll(n => bare.Contains(n.Id));
            foreach (var e in r.Elements) e.Reinforced = false;
            RegenerateTunnel();
            g = TunnelGeometry.Build(Design, Db);
            for (int j = 1; j < g.Count; j++)
            {
                var joint = Design.Nodes.FirstOrDefault(n => n.TunnelJoint == j);
                if (joint == null || Design.Members.Any(m => m.StartNodeId == joint.Id || m.EndNodeId == joint.Id)) continue;
                double x = joint.Position.X, bed = PierBedY(x);
                var zone = Level.Terrain.ZoneAt(x);
                if (!g.Type.IsFloating)
                {
                    var pier = Db.GetMaterial("pier_concrete");
                    if (pier == null || joint.Position.Y - bed < 0.5) continue;
                    var f = FoundationRules.Capacity(zone, FoundationType.Shallow) >= oldSupport[j] ? FoundationType.Shallow : FoundationType.Pile;
                    var baseN = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, bed), PierBase = true, Foundation = f };
                    Design.Nodes.Add(baseN);
                    var nodes = new List<BridgeNode> { baseN };
                    if (joint.Position.Y - bed > pier.MaximumLength)
                    {
                        var mid = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, Snap((bed + joint.Position.Y) / 2)), PierPart = true };
                        Design.Nodes.Add(mid); nodes.Add(mid);
                    }
                    joint.PierPart = true;
                    nodes.Add(joint);
                    for (int k = 0; k + 1 < nodes.Count; k++) AddRaw(nodes[k], nodes[k + 1], pier);
                }
                else
                {
                    var cable = Db.GetMaterial(strong[j] ? "cable_composite" : "cable") ?? Db.GetMaterial("cable");
                    if (cable == null) continue;
                    double cap = cable.TensileStrength * cable.CrossSectionArea;
                    var f = FoundationRules.Capacity(zone, FoundationType.Shallow) >= cap ? FoundationType.Shallow : FoundationType.Pile;
                    int n = oldCables[j];
                    for (int k = 0; k < n; k++)
                    {
                        // Anchors spread along the bed (two members cannot join the same two nodes)
                        double ax = x + (k - (n - 1) / 2.0) * 1.5;
                        var anchor = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(ax, PierBedY(ax)), PierBase = true, Foundation = f };
                        Design.Nodes.Add(anchor);
                        AddRaw(joint, anchor, cable);
                    }
                }
            }
            RegenerateTunnel();
            _undo.Clear(); _redo.Clear();
        }

        private void AddRaw(BridgeNode a, BridgeNode b, MaterialDefinition mat)
        {
            double len = Vec3d.Distance(a.Position, b.Position);
            Design.Members.Add(new BridgeMember { Id = Design.NextMemberId++, StartNodeId = a.Id, EndNodeId = b.Id, MaterialId = mat.MaterialId, Length = len, Cost = len * mat.CostPerMeter, CostStroke = DefaultCostStroke(mat) });
        }

        public double Budget => Level.Budget;
        public double UsedCost => Design.TotalCost();
        public double Remaining => Budget - UsedCost;
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        public static double Snap(double v) => Math.Round(v / GridSize) * GridSize;

        // ---------------- 撤销 / 重做 ----------------
        private void PushUndo()
        {
            _undo.Add(Design.Clone());
            if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
            _redo.Clear();
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            _redo.Add(Design.Clone());
            Design = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
            Changed?.Invoke();
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            _undo.Add(Design.Clone());
            Design = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
            Changed?.Invoke();
            return true;
        }

        /// <summary>整体替换方案（加载存档、重置关卡）。可撤销。</summary>
        public void ReplaceDesign(BridgeDesign d, bool undoable = true)
        {
            if (undoable) PushUndo();
            Design = d;
            LiftChallengeRules.Ensure(Design,Level,Db);
            if(Level.MechanicalLift)LiftMechanics.AddMounts(Design,Level,Db);
            RecalculateAll();
            if(Level.ProvidedInfrastructure!=null)MigrateLiftStructure();
            Changed?.Invoke();
        }

        // ---------------- 查询 ----------------
        public string DeviceProblem(int id,string material,bool remove=false)
        {
            var n=Design.FindNode(id);var mat=Db.GetMaterial(material);
            if(n==null)return "请点击已有连接节点安装；可先从支点拉一段钢索生成端点";
            if(mat?.DeviceKind==null||!Level.AllowedMaterials.Contains(material))return "本关不允许该设备";
            if(n.Provided||n.TunnelPoint||n.IsTunnelSupport)return "请选择电梯内可编辑的支点或钢索端点";
            if(remove)return null;
            if(mat.DeviceKind=="pulley")
            {
                if(n.CounterweightTonnes>0)return "配重端点不能同时安装定滑轮";
                if(!LiftMechanics.StableMount(Design,Db,id))return "安装失败：定滑轮需要稳定支点，先用梁或柱连接固定基础";
                if(Design.Members.Count(m=>Db.GetMaterial(m.MaterialId)?.IsCable==true&&(m.StartNodeId==id||m.EndNodeId==id))>2)return "定滑轮最多通过一根连续绳索（两段钢索）";
            }
            else
            {
                if(n.Pulley||LiftMechanics.StableMount(Design,Db,id))return "配重必须挂在可移动钢索端点上，不能挂在固定支点或滑轮轴上";
                if(!Design.Members.Any(m=>Db.GetMaterial(m.MaterialId)?.IsCable==true&&(m.StartNodeId==id||m.EndNodeId==id)))return "配重需要钢索连接，请先铺设钢索";
                if(Design.Members.Any(m=>(m.StartNodeId==id||m.EndNodeId==id)&&Db.GetMaterial(m.MaterialId)?.IsCable==false&&Db.GetMaterial(m.MaterialId)?.Extendable==false))return "配重需要独立悬挂端点，不能与平台或井架刚接";
                if(n.CounterweightTonnes>=100)return "该端点配重上限为 100 吨";
            }
            return null;
        }
        public BuildResult InstallDevice(int id,string material,bool remove=false)
        {
            string why=DeviceProblem(id,material,remove);if(why!=null)return BuildResult.Fail(why);
            var n=Design.FindNode(id);var kind=Db.GetMaterial(material).DeviceKind;
            if(remove && (kind=="pulley"?!n.Pulley:n.CounterweightTonnes==0))return BuildResult.Fail("该节点没有可拆除的设备");
            if(!remove&&kind=="pulley"&&n.Pulley)return BuildResult.Fail("这里已有定滑轮；连接两段钢索，Shift 点击可拆除");
            PushUndo();
            if(kind=="pulley")n.Pulley=!remove;
            else n.CounterweightTonnes=Math.Max(0,n.CounterweightTonnes+(remove?-5:5));
            Changed?.Invoke();
            return BuildResult.Success(id,kind=="pulley"?(remove?"已拆除定滑轮并退还费用":"已安装定滑轮：连接两段钢索形成连续绳路，额定轴载 600 kN，不能分支。Shift 点击可拆除"):$"节点 #{id} 配重 {n.CounterweightTonnes:0} 吨，重量与费用已更新；Shift 点击减 5 吨");
        }
        public BridgeNode NodeAt(double x, double y, double radius, int ignoreNodeId = -1)
        {
            BridgeNode best = null; double bestD = radius;
            foreach (var n in Design.Nodes)
            {
                if(n.Id==ignoreNodeId) continue;
                double d = Math.Sqrt((n.Position.X - x) * (n.Position.X - x) + (n.Position.Y - y) * (n.Position.Y - y));
                if (d <= bestD) { bestD = d; best = n; }
            }
            return best;
        }

        /// <summary>点到构件线段距离最近的构件。</summary>
        public BridgeMember MemberAt(double x, double y, double radius)
        {
            BridgeMember best = null; double bestD = radius;
            foreach (var m in Design.Members)
            {
                var a = Design.FindNode(m.StartNodeId); var b = Design.FindNode(m.EndNodeId);
                if (a == null || b == null) continue;
                double d = DistToSegment(x, y, a.Position.X, a.Position.Y, b.Position.X, b.Position.Y);
                if (d <= bestD) { bestD = d; best = m; }
            }
            return best;
        }

        public static double DistToSegment(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay; double l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
            double qx = ax + t * dx - px, qy = ay + t * dy - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }

        /// <summary>检查节点位置是否合法（建造区域、地形内部、重复节点）。</summary>
        public string CheckNodePosition(double x, double y, int ignoreNodeId = -1, bool pierPart = false, int ignoreNodeId2 = -1)
        {
            var t = Level.Terrain;
            if(Level.ProvidedInfrastructure!=null && !InLiftZone(x,y))return Loc.T(LiftChallengeRules.Locked);
            if (pierPart)
            {
                // 桥墩上的支点：可以在水面以下，但必须在河道内、河床以上
                if (x <= 0.3 || x >= t.RiverWidth - 0.3) return Loc.T("桥墩只能建在河道内");
                if (y < PierBedY(x) + 0.5) return Loc.T("支点太低（已到河床）");
                foreach (var n in Design.Nodes)
                {
                    if (n.Id == ignoreNodeId) continue;
                    double ddx = n.Position.X - x, ddy = n.Position.Y - y;
                    if (ddx * ddx + ddy * ddy < MinNodeSpacing * MinNodeSpacing) return Loc.T("该位置已有节点");
                }
                return null;
            }
            if (x < -t.BuildMarginX || x > t.RiverWidth + t.BuildMarginX) return Loc.T("超出可建造区域（水平方向）");
            if (y < t.BuildMinY || y > t.BuildMaxY) return Loc.T("超出可建造区域（竖直方向）");
            // 河道内不能在水面以下建造；两岸土体内允许放置节点——普通节点永远不是支座，
            // 只有关卡锚点提供支撑，因此不会获得“免费地面支撑”
            // Underwater nodes are allowed only to carry an immersed / floating tube: above the bed, outside the tube
            if (x > 0 && x < t.RiverWidth && y < t.WaterLevel + 0.01 && !InLiftZone(x, y))
            {
                var tg = TunnelGeo;
                if (tg == null || tg.Type.IsShield) return Loc.T("不能在水面以下建造（有沉管 / 悬浮隧道时可以在水下建支撑和锚索）");
                if (y < PierBedY(x) - 0.01) return Loc.T("节点在河床以下");
                if (InsideTube(tg, x, y)) return Loc.T("节点在隧道管身里面（请接在管底的接缝圆点上）");
            }
            foreach (var z in t.NoBuildZones)
                if (x > z.X0 + 0.01 && x < z.X1 - 0.01 && y < z.BelowY - 0.01) return Loc.F("{0}：只能利用两岸，不能在这里建桥墩或下部支撑", (z.Name));
            foreach (var n in Design.Nodes)
            {
                if (n.Id == ignoreNodeId || n.Id == ignoreNodeId2) continue;
                double dx = n.Position.X - x, dy = n.Position.Y - y;
                if (dx * dx + dy * dy < MinNodeSpacing * MinNodeSpacing) return Loc.T("该位置已有节点");
            }
            return null;
        }

        /// <summary>True when (x, y) lies inside the tube cross-section (with a small margin).</summary>
        private static bool InsideTube(TunnelGeometry g, double x, double y)
        {
            double b = g.BottomAt(x);
            return !double.IsNaN(b) && y > b + 0.1 && y < b + g.Type.TubeHeight + 0.1;
        }

        /// <summary>A free node at the river bed (immersed / floating tunnel only) becomes a bed foundation / anchor block.</summary>
        public bool IsBedPoint(double x, double y)
        {
            var t = Level.Terrain; var tg = TunnelGeo;
            return tg != null && !tg.Type.IsShield && x > 0.3 && x < t.RiverWidth - 0.3 && Math.Abs(y - PierBedY(x)) <= Physics.TunnelStructure.BedSnap;
        }

        public MemberPreview PreviewMember(int startId, int endId, string materialId)
        {
            var p = new MemberPreview();
            var mat = Db.GetMaterial(materialId);
            var a = Design.FindNode(startId); var b = Design.FindNode(endId);
            p.MaterialName = mat?.Name ?? "?";
            if (mat == null) { p.Reason = Loc.T("未知材料"); return p; }
            if (a == null || b == null) { p.Reason = Loc.T("节点不存在"); return p; }
            p.Length = Vec3d.Distance(a.Position, b.Position);
            p.Cost = p.Length * mat.CostPerMeter * StrokeFactor(mat);
            p.Reason = CheckMember(a, b, mat, p.Cost);
            p.Allowed = p.Reason == null;
            p.Warning = ColumnWarning(mat, a.Position, b.Position);
            return p;
        }

        /// <summary>限量材料是否已用完。</summary>
        public bool LimitReached(MaterialDefinition mat) =>
            Level.MaterialLimits != null && Level.MaterialLimits.TryGetValue(mat.MaterialId, out int lim) && Design.Members.Count(m => m.MaterialId == mat.MaterialId) >= lim;

        /// <summary>限量材料剩余根数（不限量返回 -1）。</summary>
        public int RemainingOf(string materialId) =>
            Level.MaterialLimits != null && Level.MaterialLimits.TryGetValue(materialId, out int lim) ? Math.Max(0, lim - Design.Members.Count(m => m.MaterialId == materialId)) : -1;

        /// <summary>立柱倾角警告（允许建造，但测试时会倒塌）。</summary>
        private static string ColumnWarning(MaterialDefinition mat, Vec3d a, Vec3d b)
        {
            if (!mat.IsColumn || !ColumnRules.TiltExceeded(mat, a, b)) return null;
            return Loc.F("倾角 {0:0}° 超过 {1:0}°，测试时会倒塌", (ColumnRules.TiltDegrees(a, b)), (mat.MaxTiltDegrees));
        }

        /// <summary>预览从节点到任意点的构件（终点尚未有节点时）。</summary>
        public MemberPreview PreviewMemberToPoint(int startId, double x, double y, string materialId)
        {
            var p = new MemberPreview();
            var mat = Db.GetMaterial(materialId);
            var a = Design.FindNode(startId);
            p.MaterialName = mat?.Name ?? "?";
            if (mat == null || a == null) { p.Reason = Loc.T("无效起点"); return p; }
            p.Length = Vec3d.Distance(a.Position, new Vec3d(x, y));
            p.Cost = p.Length * mat.CostPerMeter * StrokeFactor(mat);
            string reason = CheckNodePosition(x, y);
            if (reason == null && p.Length < MinMemberLength) reason = Loc.F("太短（至少 {0:0.#} m）", (MinMemberLength));
            if (reason == null && p.Length > mat.MaximumLength) reason = Loc.F("超长 {0:0.0} m（{1}最长 {2:0.#} m）", (p.Length - mat.MaximumLength), (mat.Name), (mat.MaximumLength));
            if (reason == null && mat.IsDeck && Math.Abs(x - a.Position.X) < 0.3) reason = Loc.T("桥面不能竖直铺设");
            if (reason == null && LimitReached(mat)) reason = Loc.F("本关{0}限量 {1} 根，已用完", (mat.Name), (Level.MaterialLimits[mat.MaterialId]));
            if (reason == null && a.TunnelPoint && !a.IsTunnelSupport) reason = Loc.T("隧道控制点只用来调整路线，构件请接在管节接缝上的支点");
            if (reason == null && a.IsTunnelSupport && mat.IsDeck) reason = Loc.T("隧道接缝圆点不能接桥面（用桥墩、柱子、梁或钢索托住 / 拉住管身）");
            if (reason == null && mat.IsColumn)
                ColumnRules.FindBase(mat, a, new BridgeNode { Id = -1, Position = new Vec3d(x, y) }, Level, out reason);
            // 预算允许超支（超支会降低最终评分），因此不阻止建造
            p.Reason = reason; p.Allowed = reason == null;
            p.Warning = ColumnWarning(mat, a.Position, new Vec3d(x, y));
            return p;
        }

        private string CheckMember(BridgeNode a, BridgeNode b, MaterialDefinition mat, double cost)
        {
            if(Level.ProvidedInfrastructure!=null && (!LiftChallengeRules.Editable(a)||!LiftChallengeRules.Editable(b)))return Loc.T(LiftChallengeRules.Locked);
            if(Level.ProvidedInfrastructure!=null && !TunnelRules.Shafts(Level,TunnelGeo).Any(shaft=>LiftStructure.InZone(LiftCfgOf,shaft,a.Position.X,a.Position.Y)&&LiftStructure.InZone(LiftCfgOf,shaft,b.Position.X,b.Position.Y)))return Loc.T("构件必须位于同一岸的电梯井内。");
            if (!Level.AllowedMaterials.Contains(mat.MaterialId)) return Loc.T("本关不允许使用该材料");
            if (LimitReached(mat)) return Loc.F("本关{0}限量 {1} 根，已用完", (mat.Name), (Level.MaterialLimits[mat.MaterialId]));
            if(!mat.IsCable&&!mat.Extendable&&(a.CounterweightTonnes>0||b.CounterweightTonnes>0))return "配重块只能连接钢索或液压缸，不能与平台刚接";
            if(mat.IsCable && new[]{a,b}.Any(n=>n.Pulley && Design.Members.Count(m=>Db.GetMaterial(m.MaterialId)?.IsCable==true&&(m.StartNodeId==n.Id||m.EndNodeId==n.Id))>=2))return "定滑轮只接受两段钢索，不能分支";
            if (a.Id == b.Id) return Loc.T("不能连接同一个节点");
            double len = Vec3d.Distance(a.Position, b.Position);
            if (len < MinMemberLength) return Loc.T("构件长度过短");
            if (len > mat.MaximumLength + 1e-9) return Loc.F("超长 {0:0.0} m（{1}最长 {2:0.#} m）", (len - mat.MaximumLength), (mat.Name), (mat.MaximumLength));
            if (Design.Members.Any(m => m.Connects(a.Id, b.Id))) return Loc.T("两节点之间已有构件");
            if (mat.IsDeck && Math.Abs(b.Position.X - a.Position.X) < 0.3) return Loc.T("桥面不能竖直铺设");
            if (a.TunnelPoint && !a.IsTunnelSupport || b.TunnelPoint && !b.IsTunnelSupport) return Loc.T("隧道控制点只用来调整路线，构件请接在管节接缝上的支点");
            if ((a.IsTunnelSupport || b.IsTunnelSupport) && mat.IsDeck) return Loc.T("隧道接缝圆点不能接桥面（用桥墩、柱子、梁或钢索托住 / 拉住管身）");
            if (a.IsTunnelSupport && b.IsTunnelSupport) return Loc.T("两个接缝圆点之间不用加构件（管身本身就是梁）");
            if (mat.IsColumn)
            {
                ColumnRules.FindBase(mat, a, b, Level, out var why);
                if (why != null) return why;
            }
            return null;
        }

        /// <summary>
        /// 调整可伸缩构件（液压杆）的长度：沿轴线移动非锚点一端（优先终点），连接的构件随之更新。
        /// </summary>
        public BuildResult ResizeMember(int memberId, double delta)
        {
            var m = Design.FindMember(memberId);
            if (m == null) return BuildResult.Fail(Loc.T("构件不存在"));
            if(m.Provided)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            var mat = Db.GetMaterial(m.MaterialId);
            if (mat == null || !mat.Extendable) return BuildResult.Fail(Loc.T("该构件不能伸缩"));
            var a = Design.FindNode(m.StartNodeId); var b = Design.FindNode(m.EndNodeId);
            var moving = !b.IsAnchor ? b : !a.IsAnchor ? a : null;
            if (moving == null) return BuildResult.Fail(Loc.T("两端都是锚点，无法伸缩"));
            var fixedEnd = moving == b ? a : b;
            double len = Vec3d.Distance(a.Position, b.Position), newLen = len + delta;
            if (newLen < MinMemberLength * 2) return BuildResult.Fail(Loc.T("已缩到最短"));
            if (newLen > mat.MaximumLength + 1e-9) return BuildResult.Fail(Loc.F("已伸到最长 {0:0.#} m", (mat.MaximumLength)));
            var dir = (moving.Position - fixedEnd.Position) * (1.0 / len);
            var p = fixedEnd.Position + dir * newLen;
            // 伸缩不吸附网格，保持轴线方向
            return MoveNode(moving.Id, p.X, p.Y);
        }

        // ---------------- 修改操作 ----------------
        public BuildResult AddNode(double x, double y)
        {
            string r = CheckNodePosition(x, y);
            if (r != null) return BuildResult.Fail(r);
            PushUndo();
            var n = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, y, 0) };
            Design.Nodes.Add(n);
            Changed?.Invoke();
            return BuildResult.Success(n.Id);
        }

        public BuildResult AddMember(int startId, int endId, string materialId)
        {
            var mat = Db.GetMaterial(materialId);
            var a = Design.FindNode(startId); var b = Design.FindNode(endId);
            if (mat == null) return BuildResult.Fail(Loc.T("未知材料"));
            if (a == null || b == null) return BuildResult.Fail(Loc.T("节点不存在"));
            double len = Vec3d.Distance(a.Position, b.Position);
            double cost = len * mat.CostPerMeter;
            string r = CheckMember(a, b, mat, cost * StrokeFactor(mat));
            if (r != null) return BuildResult.Fail(r);
            PushUndo();
            var m = new BridgeMember { Id = Design.NextMemberId++, StartNodeId = a.Id, EndNodeId = b.Id, MaterialId = mat.MaterialId, Length = len, Cost = cost, CostStroke = DefaultCostStroke(mat) };
            Design.Members.Add(m);
            Changed?.Invoke();
            return BuildResult.Success(m.Id);
        }

        /// <summary>从已有节点连接到空白位置：自动创建终点节点并连接（一次撤销即可回退两步）。</summary>
        public BuildResult AddMemberToNewNode(int startId, double x, double y, string materialId)
        {
            var p = PreviewMemberToPoint(startId, x, y, materialId);
            if (!p.Allowed) return BuildResult.Fail(p.Reason);
            var mat = Db.GetMaterial(materialId);
            if (!Level.AllowedMaterials.Contains(materialId)) return BuildResult.Fail(Loc.T("本关不允许使用该材料"));
            if (Db.GetMaterial(materialId)?.IsLift == true) return BuildResult.Fail(Loc.T("垂直电梯随隧道自动生成，不能单独建造"));
            var a = Design.FindNode(startId);
            if (mat.IsDeck && Math.Abs(x - a.Position.X) < 0.3) return BuildResult.Fail(Loc.T("桥面不能竖直铺设"));
            PushUndo();
            var n = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, y, 0) };
            if (IsBedPoint(x, y)) { n.Position = new Vec3d(x, PierBedY(x), 0); n.PierBase = true; n.Foundation = FoundationType.Shallow; }
            Design.Nodes.Add(n);
            var m = new BridgeMember { Id = Design.NextMemberId++, StartNodeId = startId, EndNodeId = n.Id, MaterialId = materialId, Length = p.Length, Cost = p.Length * mat.CostPerMeter, CostStroke = DefaultCostStroke(mat) };
            Design.Members.Add(m);
            Changed?.Invoke();
            return BuildResult.Success(m.Id);
        }

        /// <summary>移动节点：锚点不可移动；会重新计算相连构件的长度与费用，超长则拒绝（预算允许超支）。</summary>
        public BuildResult MoveNode(int nodeId, double x, double y, bool recordUndo = true)
        {
            var n = Design.FindNode(nodeId);
            if (n == null) return BuildResult.Fail(Loc.T("节点不存在"));
            if(n.Provided)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            if (n.IsAnchor) return BuildResult.Fail(Loc.T("固定锚点不能移动"));
            if (n.IsRigidSplit) return BuildResult.Fail(Loc.T("刚性拆分点不可独立移动或折叠；请移动原构件端点"));
            if (n.PierBase) return BuildResult.Fail(Loc.T("桥墩基础不能移动（可以删除桥墩后重建）"));
            if (n.IsTunnelSupport && !n.TunnelPoint) return BuildResult.Fail(Loc.T("沉管接缝圆点随管节自动生成，不能单独移动（上下拖动隧道两端可以改变埋深）"));
            if (n.IsLiftFixed)
            {
                // Only the outer platform end moves, and only sideways: it sets the platform length of both lifts
                if(n.LiftRole?.StartsWith("mount")==true)return BuildResult.Fail("井道固定支点不可移动");
                bool outer = n.LiftRole == (n.LiftBank == 1 ? "deck0" : "deck1");
                var other = Design.Nodes.FirstOrDefault(q => q.LiftBank == n.LiftBank && q.LiftRole == (n.LiftBank == 1 ? "deck1" : "deck0"));
                if (!outer || other == null) return BuildResult.Fail(Loc.T("电梯井底基础和平台内端随隧道固定，不能移动（平台外端可以左右拖动改长度）"));
                var lr = SetLiftLength(Math.Abs(other.Position.X - x), recordUndo);
                return lr.Ok ? BuildResult.Success(nodeId, lr.Message) : lr;
            }
            if (n.TunnelPoint)
            {
                double tx = x, ty = Snap(y);
                string tw = CheckTunnelPoint(n, ref tx, ref ty);
                if (tw != null) return BuildResult.Fail(tw);
                if (recordUndo) PushUndo();
                n.Position = new Vec3d(tx, ty);
                RegenerateTunnel();
                Changed?.Invoke();
                return BuildResult.Success(nodeId);
            }
            if (n.PierPart && Math.Abs(x - n.Position.X) > 1e-6) x = n.Position.X;   // 桥墩支点只能上下移动
            if (Design.Members.Any(m => m.Existing && (m.StartNodeId == nodeId || m.EndNodeId == nodeId)))
                return BuildResult.Fail(Loc.T("旧桥节点不能移动（可以拆除、修复或加固相连的旧构件）"));
            // The two nodes of a deck lock always stay together
            var lockOf = Design.EnsureMechanisms().LockOf(nodeId);
            var partner = lockOf == null ? null : Design.FindNode(lockOf.NodeA == nodeId ? lockOf.NodeB : lockOf.NodeA);
            string r = CheckNodePosition(x, y, nodeId, n.PierPart, partner?.Id ?? -1);
            if (r != null) return BuildResult.Fail(r);
            var newPos = new Vec3d(x, y, 0);
            double deltaCost = 0;
            foreach (var m in Design.Members.Where(m => m.StartNodeId == nodeId || m.EndNodeId == nodeId || (partner != null && (m.StartNodeId == partner.Id || m.EndNodeId == partner.Id))))
            {
                if (partner != null && (m.StartNodeId == partner.Id || m.EndNodeId == partner.Id) && m.Existing) return BuildResult.Fail(Loc.T("旧桥节点不能移动"));
                int movedEnd = m.StartNodeId == nodeId || m.EndNodeId == nodeId ? nodeId : partner.Id;
                var mat = Db.GetMaterial(m.MaterialId);
                var other = Design.FindNode(m.Other(movedEnd));
                double len = Vec3d.Distance(newPos, other.Position);
                if (len > mat.MaximumLength + 1e-9) return BuildResult.Fail(Loc.F("构件 #{0} 将超过最大长度", (m.Id)));
                if (len < MinMemberLength) return BuildResult.Fail(Loc.F("构件 #{0} 将过短", (m.Id)));
                if (mat.IsDeck && Math.Abs(other.Position.X - x) < 0.3) return BuildResult.Fail(Loc.T("桥面不能竖直"));
                if (mat.IsColumn)
                {
                    var moved = new BridgeNode { Id = n.Id, Position = newPos, IsAnchor = false };
                    bool startIsMoved = m.StartNodeId == movedEnd;
                    ColumnRules.FindBase(mat, startIsMoved ? moved : other, startIsMoved ? other : moved, Level, out var why);
                    if (why != null) return BuildResult.Fail(Loc.F("构件 #{0}：{1}", (m.Id), (why)));
                }
                deltaCost += (len * mat.CostPerMeter - m.Cost) * (1 + BridgeMember.StrokeCostRate * System.Math.Abs(m.CostStroke ?? 0));
            }

            if (recordUndo) PushUndo();
            n.Position = newPos;
            if (partner != null) partner.Position = newPos;
            RecalculateAll();
            Changed?.Invoke();
            return BuildResult.Success(nodeId);
        }

        public BuildResult DeleteNode(int nodeId)
        {
            var n = Design.FindNode(nodeId);
            if (n == null) return BuildResult.Fail(Loc.T("节点不存在"));
            if(n.Provided)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            if (n.IsAnchor) return BuildResult.Fail(Loc.T("固定锚点不能删除"));
            if (n.IsTunnelSupport && !n.TunnelPoint) return BuildResult.Fail(Loc.T("接缝圆点不能单独删除（删除隧道请点击管节）"));
            if (n.IsLiftFixed) return BuildResult.Fail(Loc.T("电梯平台两端和井底基础随隧道固定，不能删除（可以删除与它相连的构件）"));
            if (n.TunnelPoint)
            {
                if (Design.Tunnel == null || Design.Tunnel.PointNodeIds.Count <= 2) return DeleteTunnel();
                if (TunnelGeo != null && !TunnelGeo.Type.Straight) return BuildResult.Fail(Loc.T("管节接缝上的圆点不能删除：拖动它调整埋深，删除隧道请点击管节"));
                PushUndo();
                Design.Tunnel.PointNodeIds.Remove(n.Id);
                Design.Nodes.Remove(n);
                RegenerateTunnel();
                Changed?.Invoke();
                return BuildResult.Success(0, Loc.T("已删除隧道控制点"));
            }
            if (n.PierBase)
            {
                // 删除整座桥墩：墩身构件全部删除，中部支点若不再连接其他构件则一并删除
                var pierMembers = new List<BridgeMember>(); var frontier = new Queue<int>(); frontier.Enqueue(n.Id);
                var seen = new HashSet<int>();
                while (frontier.Count > 0)
                {
                    int c = frontier.Dequeue(); if (!seen.Add(c)) continue;
                    foreach (var m in Design.Members.Where(m => (m.StartNodeId == c || m.EndNodeId == c) && Db.GetMaterial(m.MaterialId)?.IsPier == true))
                        if (!pierMembers.Contains(m)) { pierMembers.Add(m); frontier.Enqueue(m.Other(c)); }
                }
                PushUndo();
                Design.Members.RemoveAll(m => pierMembers.Contains(m));
                Design.Nodes.Remove(n);
                Design.Nodes.RemoveAll(x => !x.IsAnchor && !x.IsTunnelNode && !Design.Members.Any(m => m.StartNodeId == x.Id || m.EndNodeId == x.Id));
                foreach (var x in Design.Nodes) if (x.PierPart && !Design.Members.Any(m => (m.StartNodeId == x.Id || m.EndNodeId == x.Id) && Db.GetMaterial(m.MaterialId)?.IsPier == true)) x.PierPart = false;
                Changed?.Invoke();
                return BuildResult.Success(0, Loc.T("已删除整座桥墩"));
            }
            var removing = Design.Members.Where(m => m.StartNodeId == nodeId || m.EndNodeId == nodeId).ToList();
            var why = CheckDemolition(removing);
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            ApplyDemolition(removing);
            Design.Members.RemoveAll(m => m.StartNodeId == nodeId || m.EndNodeId == nodeId);
            Design.Nodes.Remove(n);
            PruneMechanisms();
            Changed?.Invoke();
            return BuildResult.Success();
        }

        /// <summary>批量删除（框选）：一次撤销即可恢复。锚点会被跳过；删除节点时连同相连构件一起删除。</summary>
        public BuildResult DeleteMany(IEnumerable<int> nodeIds, IEnumerable<int> memberIds)
        {
            var nodes = new HashSet<int>(nodeIds.Where(id => Design.FindNode(id) is { IsAnchor: false, IsTunnelNode: false, Provided:false }));
            var mems = new HashSet<int>(memberIds.Where(id => Design.FindMember(id) != null));
            foreach (var m in Design.Members) if (nodes.Contains(m.StartNodeId) || nodes.Contains(m.EndNodeId)) mems.Add(m.Id);
            if (nodes.Count == 0 && mems.Count == 0) return BuildResult.Fail(Loc.T("没有可删除的对象（锚点不能删除）"));
            double refund = Design.Members.Where(m => mems.Contains(m.Id)).Sum(m => m.EffectiveCost());
            var removing = Design.Members.Where(m => mems.Contains(m.Id)).ToList();
            var why = CheckDemolition(removing);
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            ApplyDemolition(removing);
            Design.Members.RemoveAll(m => mems.Contains(m.Id));
            Design.Nodes.RemoveAll(n => nodes.Contains(n.Id));
            // 清理删除构件后留下的孤立普通节点
            Design.Nodes.RemoveAll(n => !n.IsAnchor && !n.IsTunnelNode && !Design.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id));
            PruneMechanisms();
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.F("已删除 {0} 根构件，退还 ${1:0}", (mems.Count), (refund)));
        }

        public BuildResult DeleteMember(int memberId)
        {
            var m = Design.FindMember(memberId);
            if (m == null) return BuildResult.Fail(Loc.T("构件不存在"));
            if(m.Provided)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            var why = CheckDemolition(new[] { m });
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            ApplyDemolition(new[] { m });
            Design.Members.Remove(m);   // 删除即退还费用（成本由构件列表实时求和）
            // 不保留孤立的普通节点（游戏中节点只能作为构件端点存在）
            Design.Nodes.RemoveAll(n => !n.IsAnchor && !n.IsTunnelNode && (n.Id == m.StartNodeId || n.Id == m.EndNodeId)
                                        && !Design.Members.Any(x => x.StartNodeId == n.Id || x.EndNodeId == n.Id));
            PruneMechanisms();
            Changed?.Invoke();
            return BuildResult.Success();
        }

        // ---------------- Movable bridge (bascule) and member splitting ----------------

        /// <summary>Drops locks / leaves whose nodes or members no longer exist (after deletions).</summary>
        private void PruneMechanisms()
        {
            var mech = Design.EnsureMechanisms();
            mech.Linkage?.Drivers.RemoveAll(d => Design.FindMember(d.MemberId) == null);
            mech.Locks.RemoveAll(l => Design.FindNode(l.NodeA) == null || Design.FindNode(l.NodeB) == null);
            mech.Leaves.RemoveAll(l => Design.FindMember(l.ActuatorMemberId) == null || Design.FindNode(l.PivotNodeId) == null);
        }

        /// <summary>
        /// Splits a deck node into a lockable joint: a second node is created at the same position and every
        /// member going to the right of the node is moved onto it. The two halves are held together by the lock
        /// while closed (rigid connection) and separate completely when the lock is released.
        /// </summary>
        public BuildResult CreateDeckLock(int nodeId)
        {
            var n = Design.FindNode(nodeId);
            if (n == null) return BuildResult.Fail(Loc.T("节点不存在"));
            if (n.IsRigidSplit) return BuildResult.Fail(Loc.T("刚性拆分点不可作为开合锁扣"));
            if (n.IsAnchor || n.PierBase || n.PierPart) return BuildResult.Fail(Loc.T("锚点和桥墩节点不能做成锁扣"));
            var mech = Design.EnsureMechanisms();
            if (mech.LockOf(nodeId) != null) return BuildResult.Fail(Loc.T("这个节点已经是锁扣"));
            var mems = Design.Members.Where(m => m.StartNodeId == nodeId || m.EndNodeId == nodeId).ToList();
            if (mems.Any(m => m.Existing)) return BuildResult.Fail(Loc.T("旧桥节点不能做成锁扣"));
            bool IsDeck(BridgeMember m) => Db.GetMaterial(m.MaterialId)?.IsDeck == true;
            double x = n.Position.X;
            var right = mems.Where(m => Design.FindNode(m.Other(nodeId)).Position.X > x + 1e-6).ToList();
            var left = mems.Where(m => Design.FindNode(m.Other(nodeId)).Position.X < x - 1e-6).ToList();
            if (!right.Any(IsDeck) || !left.Any(IsDeck)) return BuildResult.Fail(Loc.T("锁扣要放在左右两侧都有桥面的节点上（例如开启桥中央的桥面接缝）"));
            if (mems.Count != left.Count + right.Count) return BuildResult.Fail(Loc.T("节点上有竖直构件，无法判断它属于哪一侧"));
            PushUndo();
            var twin = new BridgeNode { Id = Design.NextNodeId++, Position = n.Position };
            Design.Nodes.Add(twin);
            foreach (var m in right)
            {
                if (m.StartNodeId == nodeId) m.StartNodeId = twin.Id; else m.EndNodeId = twin.Id;
            }
            var lk = new DeckLockData { Id = mech.NextId++, NodeA = nodeId, NodeB = twin.Id };
            mech.Locks.Add(lk);
            Changed?.Invoke();
            return BuildResult.Success(lk.Id, Loc.F("已设置中央锁扣：节点 #{0}（左侧）与 #{1}（右侧）闭合时刚性锁定，解锁后可完全分开", (nodeId), (twin.Id)));
        }

        /// <summary>Removes a deck lock and joins its two nodes back into one rigid node.</summary>
        public BuildResult RemoveDeckLock(int nodeId)
        {
            var mech = Design.EnsureMechanisms();
            var lk = mech.LockOf(nodeId);
            if (lk == null) return BuildResult.Fail(Loc.T("这个节点不是锁扣"));
            PushUndo();
            foreach (var m in Design.Members)
            {
                if (m.StartNodeId == lk.NodeB) m.StartNodeId = lk.NodeA;
                if (m.EndNodeId == lk.NodeB) m.EndNodeId = lk.NodeA;
            }
            Design.Nodes.RemoveAll(x => x.Id == lk.NodeB);
            mech.Locks.Remove(lk);
            PruneMechanisms();
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.T("已取消锁扣，两侧重新刚接"));
        }

        /// <summary>Automatic movable-bridge analysis of the current design (no changes).</summary>
        public BasculePlan PlanMechanism() => BasculePlanner.Plan(Design, Db, Level);

        /// <summary>Explains what the selected hydraulic drives (or why it drives nothing).</summary>
        /// <summary>Player-facing hydraulic control: direction (retract / extend); the stroke percentage is kept.</summary>
        public BuildResult SetHydraulicDirection(int memberId,bool extend)
        {
            var member=Design.FindMember(memberId);
            if(member==null || Db.GetMaterial(member.MaterialId)?.Extendable!=true) return BuildResult.Fail(Loc.T("请先选中液压杆"));
            var settings=Design.EnsureMechanisms().Linkage ??= new LinkageData();
            var driver=settings.Drivers.FirstOrDefault(d=>d.MemberId==memberId);
            double mag=driver!=null?System.Math.Abs(driver.Ratio):LinkageDriverData.StrokeFraction;
            double target=extend?mag:-mag;
            if(driver!=null && driver.Ratio==target) return BuildResult.Success(0,DescribeActuator(memberId));
            PushUndo();
            if(driver==null) settings.Drivers.Add(new LinkageDriverData { MemberId=memberId,Ratio=target });
            else driver.Ratio=target;
            Changed?.Invoke();
            return BuildResult.Success(0,DescribeActuator(memberId));
        }

        /// <summary>
        /// Sets the stroke of a hydraulic as a fraction of its built length (5%..95%, below 100%). Retracting, the
        /// shortest length is L*(1-stroke) and equals the drawn yellow cylinder; extending, the longest is L*(1+stroke).
        /// </summary>
        public BuildResult SetHydraulicStroke(int memberId,double stroke)
        {
            var member=Design.FindMember(memberId);
            if(member==null || Db.GetMaterial(member.MaterialId)?.Extendable!=true) return BuildResult.Fail(Loc.T("请先选中液压杆"));
            if(!double.IsFinite(stroke)) return BuildResult.Fail(Loc.T("行程必须是数字"));
            stroke=System.Math.Clamp(System.Math.Round(stroke,4),LinkageDriverData.MinStroke,LinkageDriverData.MaxStroke);
            var settings=Design.EnsureMechanisms().Linkage ??= new LinkageData();
            var driver=settings.Drivers.FirstOrDefault(d=>d.MemberId==memberId);
            double target=driver!=null && driver.Ratio>0?stroke:-stroke;
            if(driver!=null && System.Math.Abs(driver.Ratio-target)<1e-9) return BuildResult.Success(0,DescribeActuator(memberId));
            PushUndo();
            if(driver==null) settings.Drivers.Add(new LinkageDriverData { MemberId=memberId,Ratio=target });
            else driver.Ratio=target;
            Changed?.Invoke();
            string range=target<0?Loc.F("最短 {0:0.00} m（黄色外筒长度）", (member.Length*(1-stroke))):Loc.F("最长 {0:0.00} m", (member.Length*(1+stroke)));
            return BuildResult.Success(0,Loc.F("行程 {0:P0}：{1}。", (stroke), (range))+DescribeActuator(memberId));
        }

        public BuildResult ConfigureLinkage(int memberId, bool toggle, double delta=0)
        {
            var member=Design.FindMember(memberId);
            var material=member==null?null:Db.GetMaterial(member.MaterialId);
            if(material?.Extendable!=true) return BuildResult.Fail(Loc.T("请先选中液压杆"));
            var settings=Design.EnsureMechanisms().Linkage ??= new LinkageData();
            var driver=settings.Drivers.FirstOrDefault(d=>d.MemberId==memberId);
            if(!toggle && driver==null) return BuildResult.Fail(Loc.T("请先启用此杆的通用联动驱动"));
            PushUndo();
            if(toggle && driver!=null) settings.Drivers.Remove(driver);
            else if(driver==null) settings.Drivers.Add(new LinkageDriverData { MemberId=memberId, Ratio=-material.RetractRatio });
            else driver.Ratio=System.Math.Clamp(System.Math.Round(driver.Ratio+delta,4),-LinkageDriverData.MaxStroke,LinkageDriverData.MaxStroke);
            Changed?.Invoke();
            return BuildResult.Success(0, DescribeActuator(memberId));
        }

        public string DescribeActuator(int actuatorMemberId)
        {
            if(Level.MechanicalLift)
            {
                var rod=Design.FindMember(actuatorMemberId);if(rod==null)return "液压缸不存在";
                var mat=Db.GetMaterial(rod.MaterialId);var lift=Lifts().FirstOrDefault(l=>l.Members.Any(m=>m.Id==actuatorMemberId));
                string state=lift==null?"请连接电梯平台或滑轮绳路":LiftMechanics.Plan(Design,Level,Db,lift).Problem??"机构连接与行程检查通过，开始测试后校核拉压力";
                return $"液压缸 #{rod.Id}：初始 {rod.Length:0.00} m，目标 {rod.Length*(1+(rod.Stroke??-mat.RetractRatio)):0.00} m，拉压力上限 {mat.ActuatorForce/1000:0} kN。\n{state}";
            }
            var driver=Design.Mechanisms?.Linkage?.Drivers.FirstOrDefault(d=>d.MemberId==actuatorMemberId);
            if(driver!=null)
            {
                // Report the actual runtime dispatch, not just the presence of a saved checkbox.
                var model=StructuralModel.Build(Design,Level,Db);
                var controller=new BasculeSystem(); controller.Prepare(model,Design,Db,Level);
                bool available=controller.Linkage?.Ready==true && controller.Linkage.Drivers.Any(k=>model.MemberIds[k]==actuatorMemberId);
                if(available) return Loc.F("通用驱动 #{0} 预检可动作：目标长度变化 {1:P0}。等待船只和交通条件后执行；同一机构中未选择的液压杆保持原长。", (actuatorMemberId), (driver.Ratio));
                string reason=controller.ConfigurationWarning ?? controller.Fault ?? Loc.T("驱动与其他机构的运动约束冲突");
                var automatic=BasculePlanner.Plan(Design,Db,Level);
                if(automatic.Rejected.TryGetValue(actuatorMemberId,out var brace) && brace.Contains(Loc.T("锁死"))) reason+="\n"+brace;
                return Loc.F("已保存液压杆 #{0} 的目标，但此杆当前暂不动作：{1}", (actuatorMemberId), (reason));
            }
            if ((Design.Mechanisms?.Linkage?.Drivers.Count ?? 0)>0)
                return Loc.T("此杆未设置主动行程；若属于所选联动机构，将保持原长作为从动杆。独立且不冲突的桥叶仍可自动运行。");
            var plan = PlanMechanism();
            var runtimeModel=StructuralModel.Build(Design,Level,Db);
            var runtime=new BasculeSystem();runtime.Prepare(runtimeModel,Design,Db,Level);
            if(runtime.Linkage?.Ready==true && runtime.Linkage.Drivers.Any(k=>runtimeModel.MemberIds[k]==actuatorMemberId))
                return Loc.F("液压杆 #{0} 已参与联合长度求解：主桥叶、从动桥段及液压杆两端共同运动；内部支撑随桥叶保持形状，不作为固定阻挡。", (actuatorMemberId));
            if(runtime.ConfigurationWarning?.StartsWith(Loc.T("联合机构预检未通过")) == true)
                return runtime.ConfigurationWarning;
            var g = plan.GroupOf(actuatorMemberId);
            if (g == null) return plan.Rejected.TryGetValue(actuatorMemberId, out var why) ? Loc.F("不驱动桥叶：{0}", (why)) : Loc.T("不驱动桥叶");
            string mates = g.Actuators.Count > 1 ? Loc.F("，与液压杆 #{0} 共同驱动", (string.Join(Loc.T("、#"), g.Actuators.Where(a => a.MemberId != actuatorMemberId).Select(a => a.MemberId)))) : "";
            return Loc.F("驱动桥叶 #{0}：转轴节点 #{1}，{2} 个节点随桥叶转动{3}，开桥时分离 {4} 处接缝，目标开启 {5:0}°", (g.Id), (Math.Abs(g.PivotKey)), (g.LeafKeys.Count), (mates), (plan.CutSeams.Count), (g.OpenAngleDeg));
        }

        /// <summary>Stores the opening angle of the leaf this hydraulic drives (leaves themselves are found automatically).</summary>
        public BuildResult SetLeafActuator(int actuatorMemberId, double openAngleDeg = 70) => SetLeafActuator(actuatorMemberId, openAngleDeg, true);

        public BuildResult SetLeafActuator(int actuatorMemberId, double openAngleDeg, bool recordUndo)
        {
            var plan = PlanMechanism();
            var g = plan.GroupOf(actuatorMemberId);
            if (g == null) return BuildResult.Fail(plan.Rejected.TryGetValue(actuatorMemberId, out var why) ? why : Loc.T("这根液压杆不驱动桥叶"));
            var mech = Design.EnsureMechanisms();
            var old = mech.Leaves.FirstOrDefault(l => l.ActuatorMemberId == actuatorMemberId);
            if (old != null && old.PivotNodeId == Math.Abs(g.PivotKey) && Math.Abs(old.OpenAngleDeg - Math.Clamp(openAngleDeg, 5, 85)) < 1e-9)
                return BuildResult.Success(old.Id, DescribeActuator(actuatorMemberId));
            if (recordUndo) PushUndo();
            mech.Leaves.RemoveAll(l => g.Actuators.Any(a => a.MemberId == l.ActuatorMemberId));
            var leaf = new BasculeLeafData { Id = mech.NextId++, PivotNodeId = Math.Abs(g.PivotKey), ActuatorMemberId = actuatorMemberId, OpenAngleDeg = Math.Clamp(openAngleDeg, 5, 85) };
            mech.Leaves.Add(leaf);
            Changed?.Invoke();
            return BuildResult.Success(leaf.Id, DescribeActuator(actuatorMemberId));
        }

        public BuildResult RemoveLeaf(int actuatorMemberId)
        {
            var mech = Design.EnsureMechanisms();
            var leaf = mech.Leaves.FirstOrDefault(l => l.ActuatorMemberId == actuatorMemberId);
            if (leaf == null) return BuildResult.Fail(Loc.T("这根液压杆没有单独设置开启角"));
            PushUndo();
            mech.Leaves.Remove(leaf);
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.T("已恢复默认开启角 70°"));
        }

        /// <summary>Length range a hydraulic can really reach in the current structure (fractions of its built length).</summary>
        public struct HydraulicRange
        {
            public bool Valid, Leaf;
            public double BuiltLength, MinPct, MaxPct, CurrentPct;
        }

        private const double LeafAngleMin = 5, LeafAngleMax = 85, LeafAngleStep = 0.5;

        /// <summary>New hydraulics are priced for their default stroke (the material retraction).</summary>
        private static double? DefaultCostStroke(MaterialDefinition mat) => mat.Extendable ? mat.RetractRatio : null;
        private static double StrokeFactor(MaterialDefinition mat) => 1 + BridgeMember.StrokeCostRate * System.Math.Abs(DefaultCostStroke(mat) ?? 0);

        /// <summary>Price of a hydraulic for a new stroke.</summary>
        private string PriceStroke(BridgeMember member, double strokeMagnitude, out double newCost)
        {
            newCost = member.Cost * (1 + BridgeMember.StrokeCostRate * System.Math.Abs(strokeMagnitude));
            // Over budget is allowed (it only lowers the score), so the price is reported, never refused
            return null;
        }

        /// <summary>
        /// Automatic stroke limits of a hydraulic. When it drives a bascule leaf, the range is the actuator length
        /// over every leaf angle the structure allows (5..85 deg, within the +-95% stroke), so the slider can only
        /// pick lengths the mechanism can reach. Other hydraulics use the general linkage range +-95%.
        /// </summary>
        public HydraulicRange GetHydraulicRange(int memberId)
        {
            var member = Design.FindMember(memberId);
            if (member == null || Db.GetMaterial(member.MaterialId)?.Extendable != true) return default;
            var r = new HydraulicRange { Valid = true, BuiltLength = member.Length };
            if (LeafActuator(memberId, out var sys, out var lr, out var ar, out var g))
            {
                double lo = ar.L0, hi = ar.L0;
                for (double a = LeafAngleMin; a <= LeafAngleMax + 1e-9; a += LeafAngleStep)
                {
                    double len = sys.ActuatorLengthAt(lr, ar, a);
                    if (len < ar.LMin - 1e-9 || len > ar.LMax + 1e-9) break;
                    lo = System.Math.Min(lo, len); hi = System.Math.Max(hi, len);
                }
                r.Leaf = true;
                r.MinPct = lo / ar.L0 - 1; r.MaxPct = hi / ar.L0 - 1;
                r.CurrentPct = sys.ActuatorLengthAt(lr, ar, g.OpenAngleDeg) / ar.L0 - 1;
                return r;
            }
            var mat = Db.GetMaterial(member.MaterialId);
            var driver = Design.Mechanisms?.Linkage?.Drivers.FirstOrDefault(d => d.MemberId == memberId);
            r.MinPct = -System.Math.Min(mat.RetractRatio, LinkageDriverData.MaxStroke);
            r.MaxPct = System.Math.Min(mat.ExtendRatio, LinkageDriverData.MaxStroke);
            if (r.MinPct > -LinkageDriverData.MinStroke) r.MinPct = -LinkageDriverData.MaxStroke;
            if (r.MaxPct < LinkageDriverData.MinStroke) r.MaxPct = LinkageDriverData.MaxStroke;
            r.CurrentPct = driver?.Ratio ?? member.Stroke ?? -mat.RetractRatio;
            r.CurrentPct = System.Math.Clamp(r.CurrentPct, r.MinPct, r.MaxPct);
            return r;
        }

        /// <summary>
        /// Slider target: a length change (fraction of the built length). For a leaf hydraulic it becomes the leaf
        /// opening angle whose actuator length is closest; otherwise it is the linkage target ratio.
        /// </summary>
        public BuildResult SetHydraulicTarget(int memberId, double pct) => SetHydraulicTarget(memberId, pct, true);

        /// <summary>Same; recordUndo = false while the slider is being dragged (one undo step per drag).</summary>
        public BuildResult SetHydraulicTarget(int memberId, double pct, bool recordUndo)
        {
            var member = Design.FindMember(memberId);
            if (member == null || Db.GetMaterial(member.MaterialId)?.Extendable != true) return BuildResult.Fail(Loc.T("请先选中液压杆"));
            if (!double.IsFinite(pct)) return BuildResult.Fail(Loc.T("目标必须是数字"));
            if (LeafActuator(memberId, out var sys, out var lr, out var ar, out _))
            {
                double best = LeafAngleMin, err = double.MaxValue;
                for (double a = LeafAngleMin; a <= LeafAngleMax + 1e-9; a += LeafAngleStep)
                {
                    double len = sys.ActuatorLengthAt(lr, ar, a);
                    if (len < ar.LMin - 1e-9 || len > ar.LMax + 1e-9) break;
                    double e = System.Math.Abs(len / ar.L0 - 1 - pct);
                    if (e < err) { err = e; best = a; }
                }
                double got = sys.ActuatorLengthAt(lr, ar, best);
                double travel = System.Math.Abs(got / ar.L0 - 1);
                var over = PriceStroke(member, travel, out double leafCost);
                if (over != null) return BuildResult.Fail(over);
                var res = SetLeafActuator(memberId, best, recordUndo);
                if (!res.Ok) return res;
                member.CostStroke = travel;
                Changed?.Invoke();
                return BuildResult.Success(res.CreatedId, Loc.F("目标长度 {0:0.00} m（{1:+0%;-0%;0%}），桥叶开启 {2:0.#}°，费用 ${3:0}。", (got), (got / ar.L0 - 1), (best), (leafCost)) + DescribeActuator(memberId));
            }
            var range = GetHydraulicRange(memberId);
            double target = System.Math.Clamp(pct, range.MinPct, range.MaxPct);
            var linkage = Design.Mechanisms?.Linkage;
            if (linkage == null || linkage.Drivers.Count == 0)
            {
                // Ordinary hydraulic (every level): store the stroke on the member; the simulation and the yellow
                // sleeve read it directly. No linkage mode is switched on.
                if (member.Stroke.HasValue && System.Math.Abs(member.Stroke.Value - target) < 1e-9) return BuildResult.Success(0, DescribeActuator(memberId));
                var tooMuch = PriceStroke(member, target, out double strokeCost);
                if (tooMuch != null) return BuildResult.Fail(tooMuch);
                if (recordUndo) PushUndo();
                member.Stroke = target; member.CostStroke = System.Math.Abs(target);
                Changed?.Invoke();
                return BuildResult.Success(0, Loc.F("目标长度 {0:0.00} m（{1:+0%;-0%;0%}），黄色外筒 = 最短 {2:0.00} m，费用 ${3:0}", (member.Length * (1 + target)), (target), (member.Length * (1 + System.Math.Min(0, target))), (strokeCost)));
            }
            double mag = System.Math.Clamp(System.Math.Abs(target), LinkageDriverData.MinStroke, LinkageDriverData.MaxStroke);
            target = target > 0 ? mag : -mag;
            var settings = linkage;
            var driver = settings.Drivers.FirstOrDefault(d => d.MemberId == memberId);
            if (driver != null && System.Math.Abs(driver.Ratio - target) < 1e-9) return BuildResult.Success(0, DescribeActuator(memberId));
            if (recordUndo) PushUndo();
            if (driver == null) settings.Drivers.Add(new LinkageDriverData { MemberId = memberId, Ratio = target });
            else driver.Ratio = target;
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.F("目标长度 {0:0.00} m（{1:+0%;-0%}）。", (member.Length * (1 + target)), (target)) + DescribeActuator(memberId));
        }

        private bool LeafActuator(int memberId, out BasculeSystem sys, out LeafRuntime lr, out ActuatorRuntime ar, out LeafGroupPlan g)
            => LeafActuator(Design, Level, Db, PlanMechanism(), memberId, out sys, out lr, out ar, out g);

        private static bool LeafActuator(BridgeDesign design, LevelDefinition level, GameDatabase db, BasculePlan plan, int memberId,
            out BasculeSystem sys, out LeafRuntime lr, out ActuatorRuntime ar, out LeafGroupPlan g)
        {
            sys = null; lr = null; ar = null;
            g = level.MechanicalLift?null:plan.GroupOf(memberId);
            if (g == null) return false;
            var m = StructuralModel.Build(design, level, db);
            sys = new BasculeSystem();
            sys.PrepareWith(m, plan);
            sys.Initialize(System.Array.Empty<BridgeArchitect3D.Traffic.RoadPath>());
            lr = sys.Leaves.FirstOrDefault(l => l.Actuators.Any(a => a.MemberId == memberId));
            ar = lr?.Actuators.First(a => a.MemberId == memberId);
            return ar != null;
        }

        /// <summary>
        /// Yellow sleeve length of every hydraulic as a fraction of its built length = its shortest length in use:
        /// leaf hydraulics the shortest length over the opening (0 deg .. target), others 1 + min(0, stroke).
        /// </summary>
        public static Dictionary<int, double> SleeveFractions(BridgeDesign design, LevelDefinition level, GameDatabase db)
        {
            var result = new Dictionary<int, double>();
            BasculePlan plan = null;
            foreach (var m in design.Members)
            {
                var mat = db.GetMaterial(m.MaterialId);
                if (mat?.Extendable != true) continue;
                var drv = design.Mechanisms?.Linkage?.Drivers.FirstOrDefault(d => d.MemberId == m.Id);
                double s = drv?.Ratio ?? m.Stroke ?? -mat.RetractRatio;
                result[m.Id] = 1 + System.Math.Min(0, s);
            }
            if (level == null || result.Count == 0) return result;
            try { plan = BasculePlanner.Plan(design, db, level); } catch { return result; }
            foreach (var id in result.Keys.ToList())
            {
                if (plan.GroupOf(id) == null || !LeafActuator(design, level, db, plan, id, out var sys, out var lr, out var ar, out var g)) continue;
                double lo = ar.L0;
                for (double a = 0; a <= g.OpenAngleDeg + 1e-9; a += LeafAngleStep)
                {
                    double len = sys.ActuatorLengthAt(lr, ar, a);
                    if (len < ar.LMin - 1e-9 || len > ar.LMax + 1e-9) break;
                    lo = System.Math.Min(lo, len);
                }
                result[id] = lo / ar.L0;
            }
            return result;
        }

        public BuildResult SetLeafAngle(int actuatorMemberId, double openAngleDeg)
        {
            var plan = PlanMechanism();
            var g = plan.GroupOf(actuatorMemberId);
            if (g == null) return BuildResult.Fail(plan.Rejected.TryGetValue(actuatorMemberId, out var why) ? why : Loc.T("这根液压杆不驱动桥叶"));
            return SetLeafActuator(actuatorMemberId, openAngleDeg);
        }
        /// <summary>
        /// Splits a member at its midpoint into two members of the same material (one undo step). Used to give two
        /// crossing members a real shared node. Hydraulic members and mechanism actuators cannot be split.
        /// </summary>
        public BuildResult SplitMember(int memberId, bool rigid = true)
        {
            var m = Design.FindMember(memberId);
            if (m == null) return BuildResult.Fail(Loc.T("构件不存在"));
            if(m.Provided)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            var mat = Db.GetMaterial(m.MaterialId);
            if (mat == null) return BuildResult.Fail(Loc.T("材料不存在"));
            if (mat.Extendable) return BuildResult.Fail(Loc.T("液压杆不能拆分"));
            if (mat.IsPier) return BuildResult.Fail(Loc.T("桥墩不能拆分"));
            if (m.Existing) return BuildResult.Fail(Loc.T("旧桥构件不能拆分"));
            var a = Design.FindNode(m.StartNodeId); var b = Design.FindNode(m.EndNodeId);
            double len = Vec3d.Distance(a.Position, b.Position);
            if (len / 2 < MinMemberLength) return BuildResult.Fail(Loc.T("构件太短，拆分后会短于最短构件长度"));
            double mx = (a.Position.X + b.Position.X) / 2, my = (a.Position.Y + b.Position.Y) / 2;
            string why = CheckNodePosition(mx, my);
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            var mid = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(mx, my, 0), RigidEndA = a.Id, RigidEndB = b.Id };
            foreach(var candidate in new[]{a,b}.Where(n=>n.IsRigidSplit))
            {
                var start=Design.FindNode(candidate.RigidEndA);var end=Design.FindNode(candidate.RigidEndB);
                var other=candidate.Id==a.Id?b:a;
                if(start==null || end==null)continue;
                double dx=end.Position.X-start.Position.X,dy=end.Position.Y-start.Position.Y;
                double cross=(other.Position.X-start.Position.X)*dy-(other.Position.Y-start.Position.Y)*dx;
                double along=((other.Position.X-start.Position.X)*dx+(other.Position.Y-start.Position.Y)*dy)/(dx*dx+dy*dy);
                if(Math.Abs(cross)>1e-7 || along<0 || along>1)continue;
                mid.RigidEndA=start.Id;mid.RigidEndB=end.Id;break;
            }
            var rigidA=Design.FindNode(mid.RigidEndA);var rigidB=Design.FindNode(mid.RigidEndB);
            mid.RigidFraction=Vec3d.Distance(rigidA.Position,mid.Position)/Vec3d.Distance(rigidA.Position,rigidB.Position);
            if(!rigid)ReleaseRigidSpanAt(mid);
            Design.Nodes.Add(mid);
            var second = new BridgeMember { Id = Design.NextMemberId++, StartNodeId = mid.Id, EndNodeId = m.EndNodeId, MaterialId = m.MaterialId };
            m.EndNodeId = mid.Id;
            int at = Design.Members.IndexOf(m);
            Design.Members.Insert(at + 1, second);
            RecalculateAll();   // lengths and costs of both halves
            Changed?.Invoke();
            return BuildResult.Success(mid.Id, rigid ? Loc.F("已添加刚性点 #{0}：原构件保持直线、不可折叠", (mid.Id)) : Loc.F("已拆为两根独立构件，新连接点 #{0} 为普通连接点", (mid.Id)));
        }

        private void ReleaseRigidSpanAt(BridgeNode cut)
        {
            int a=cut.RigidEndA,b=cut.RigidEndB;double t=cut.RigidFraction;
            if(t>1e-9 && t<1-1e-9)
                foreach(var node in Design.Nodes.Where(n=>n.Id!=cut.Id && n.IsRigidSplit && n.RigidEndA==a && n.RigidEndB==b))
                    if(node.RigidFraction<t){node.RigidEndB=cut.Id;node.RigidFraction/=t;}
                    else {node.RigidEndA=cut.Id;node.RigidFraction=(node.RigidFraction-t)/(1-t);}
            cut.RigidEndA=0;cut.RigidEndB=0;cut.IsSplitJoint=true;
        }
        public BuildResult ConvertRigidToSplit(int nodeId)
        {
            var node=Design.FindNode(nodeId);
            if(node==null || !node.IsRigidSplit)return BuildResult.Fail(Loc.T("请选择刚性拆分点"));
            if(Design.Members.Any(m=>m.Existing && (m.StartNodeId==nodeId || m.EndNodeId==nodeId)))return BuildResult.Fail(Loc.T("旧桥节点不能改为普通连接点"));
            PushUndo();ReleaseRigidSpanAt(node);RecalculateAll();Changed?.Invoke();
            return BuildResult.Success(nodeId,Loc.T("已改为普通连接点：两侧构件独立，保留真实连接"));
        }

        // ---------------- Underwater tunnel ----------------
        public TunnelConfig TunnelCfg => Db.TunnelConfig;
        /// <summary>Current tube geometry (null = no tunnel).</summary>
        public TunnelGeometry TunnelGeo => TunnelGeometry.Build(Design, Db);

        /// <summary>Snap a tunnel x to the grid, or to a wall toe within 1 m; always inside the flat bed.</summary>
        public double SnapTunnelX(double x)
        {
            TunnelRules.FlatBed(Level.Terrain, out double b0, out double b1);
            double best = Snap(x);
            if (Math.Abs(x - b0) < 1) best = b0;
            if (Math.Abs(x - b1) < 1) best = b1;
            return Math.Clamp(best, b0, b1);
        }

        /// <summary>Why a new tunnel from x0 to x1 cannot be built (null = it can), with its starting cost.</summary>
        public string PreviewTunnel(double x0, double x1, string materialId, out double cost, out string warning)
        {
            cost = 0; warning = null;
            if (x1 < x0) (x0, x1) = (x1, x0);
            var mat = Db.GetMaterial(materialId);
            var type = TunnelCfg?.Type(mat);
            if (mat == null || !mat.IsTunnel || type == null) return Loc.T("不是隧道建材");
            if (!Level.AllowedMaterials.Contains(materialId)) return Loc.T("本关不允许使用该材料");
            if (Design.Tunnel != null) return Loc.T("已经有一条隧道：上下拖动接缝上的圆点调整埋深");
            var t = Level.Terrain;
            TunnelRules.FlatBed(t, out double b0, out double b1);
            if (x0 < b0 - 1e-6 || x1 > b1 + 1e-6) return Loc.T("水下隧道只能建在平坦的河底");
            foreach (var p in t.Pillars)
                if (!type.IsShield && p.X + p.Width / 2 > x0 && p.X - p.Width / 2 < x1) return Loc.T("岩墩处不能铺设隧道");
            // Tubes on the bed or anchored to it disturb the river bed; a bored tunnel passes under protected zones
            if (!type.IsShield)
                foreach (var z in t.NoBuildZones)
                    if (z.X1 > x0 + 0.01 && z.X0 < x1 - 0.01) return Loc.F("{0}：不能扰动河床（沉管 / 悬浮锚索不行，可以用盾构从河床下穿过）", (z.Name));
            if (type.IsFloating)
            {
                TunnelRules.BottomRange(t, type, out double flo, out double fhi);
                if (flo > fhi + 1e-6) return Loc.T("水太浅，放不下悬浮隧道（管顶覆盖水深 + 管高 + 锚索长度）");
            }
            if (x1 - x0 < TunnelRules.MinLength) return Loc.F("隧道太短（至少 {0:0} m）", (TunnelRules.MinLength));
            if (!type.IsShield)
            {
                double ob = TunnelRules.DefaultBottom(t, type);
                if (Design.Nodes.Any(n => !n.IsTunnelNode && n.Position.X > x0 && n.Position.X < x1 && n.Position.Y > ob + 0.1 && n.Position.Y < ob + type.TubeHeight + 0.1))
                    return Loc.T("已有节点在管身位置（先删掉那里的构件）");
            }
            var preview = new BridgeDesign { Tunnel = new TunnelRoute { MaterialId = materialId, LiftLength = LiftLengthForLevel() } };
            double bottom = TunnelRules.DefaultBottom(t,type);
            preview.Nodes.Add(new BridgeNode {Id=1,Position=new Vec3d(x0,bottom),TunnelPoint=true});
            preview.Nodes.Add(new BridgeNode {Id=2,Position=new Vec3d(x1,bottom),TunnelPoint=true});
            preview.Tunnel.PointNodeIds.AddRange(new[]{1,2});
            cost = TunnelRules.Cost(TunnelGeometry.Build(preview,Db),Level);
            if (!_previewing)
            {
                // Full price: build it on a copy, with the lift members the build will generate
                var trial = new DesignEditor(Design.Clone(), Level, Db, migrate: false) { _previewing = true };
                if (trial.AddTunnel(x0, x1, materialId).Ok) cost = trial.UsedCost - UsedCost;
            }
            if (x0 > b0 + TunnelRules.JoinTolerance || x1 < b1 - TunnelRules.JoinTolerance)
                warning = Loc.T("没有连到两岸墙脚：隧道要铺满整个河底才能通车");
            return null;
        }

        /// <summary>Build the tunnel: two control points on the bed, standard elements and joint supports.</summary>
        public BuildResult AddTunnel(double x0, double x1, string materialId)
        {
            if(Level.ProvidedInfrastructure!=null)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            // The tube always runs wall to wall: both ends sit against the bank walls, where the lifts are
            TunnelRules.FlatBed(Level.Terrain, out x0, out x1);
            var why = PreviewTunnel(x0, x1, materialId, out _, out _);
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            double bed = TunnelRules.DefaultBottom(Level.Terrain, TunnelCfg.Type(Db.GetMaterial(materialId)));
            var route = new TunnelRoute { MaterialId = materialId, Structure = 1 };
            foreach (double x in new[] { x0, x1 })
            {
                var n = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, bed), TunnelPoint = true };
                Design.Nodes.Add(n); route.PointNodeIds.Add(n.Id);
            }
            route.LiftLength = LiftLengthForLevel();
            Design.Tunnel = route;
            RegenerateTunnel();
            GenerateLifts(LiftTemplateForLevel());
            RegenerateTunnel();
            Changed?.Invoke();
            var g = TunnelGeo;
            return BuildResult.Success(0, Loc.F("已建造{0}（{1:0.0} m，{2} 节管节），两端贴合岸壁，两岸垂直电梯平台 {3:0} m 已用普通构件搭好（平台、吊索、井道柱子都能像桥一样修改）。拖动两端圆点只能上下移动；{4}", (Db.GetMaterial(materialId).Name), (x1 - x0), (g?.Count ?? 0), (route.LiftLength), (g?.Type.Straight == true ? Loc.T("沉管是直管，不能加控制点") : Loc.T("每个管节接缝上的圆点都能上下拖动调整埋深"))));
        }

        /// <summary>Add a control point on the route at x (then drag it to change the depth).</summary>
        public BuildResult InsertTunnelPoint(double x)
        {
            var g = TunnelGeo;
            if (g == null) return BuildResult.Fail(Loc.T("没有隧道"));
            if (g.Type.Straight) return BuildResult.Fail(Loc.T("沉管隧道是整节预制的直管，不能弯曲：只能上下拖动两端调整深度"));
            if (true) return BuildResult.Fail(Loc.T("每个管节接缝上都有圆点：直接上下拖动它调整埋深"));
            x = Snap(x);
            if (x <= g.Points[0].X + 1 || x >= g.Points[^1].X - 1) return BuildResult.Fail(Loc.T("控制点要放在隧道两端之间"));
            if (g.Points.Any(p => Math.Abs(p.X - x) < 2)) return BuildResult.Fail(Loc.T("离已有控制点太近"));
            PushUndo();
            var n = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, g.BottomAt(x)), TunnelPoint = true };
            Design.Nodes.Add(n); Design.Tunnel.PointNodeIds.Add(n.Id);
            RegenerateTunnel();
            Changed?.Invoke();
            return BuildResult.Success(n.Id, Loc.T("已增加控制点：拖动它可以改变隧道埋深"));
        }

        /// <summary>Allowed position of a dragged control point (x inside the bed between its neighbours, y in the trench range).</summary>
        private string CheckTunnelPoint(BridgeNode n, ref double x, ref double y)
        {
            var t = Level.Terrain;
            var type = TunnelCfg?.Type(Db.GetMaterial(Design.Tunnel?.MaterialId));
            if (type == null) return Loc.T("没有隧道");
            TunnelRules.FlatBed(t, out double b0, out double b1);
            x = Math.Clamp(x, b0, b1);
            // The two ends stay against the bank walls (and the lifts): they only move vertically
            var ends = Design.Tunnel.PointNodeIds.Select(id => Design.FindNode(id)).Where(o => o != null).OrderBy(o => o.Position.X).ToList();
            if (ends.Count >= 2 && n == ends[0]) x = b0;
            else if (ends.Count >= 2 && n == ends[^1]) x = b1;
            else x = n.Position.X;   // joint points stay on their joint: vertical moves only
            TunnelRules.BottomRange(t, type, out double lo, out double hi);
            if (type.IsShield)
            {
                if (y > hi + 1e-6) return Loc.T("盾构隧道必须在河床以下（管顶至少低于河床 1 m）");
                if (y < lo - 1e-6) return Loc.F("盾构隧道太深（管底最多在河床下 {0:0} m）", (type.MaxDepthBelowBed));
            }
            else if (type.IsFloating)
            {
                if (y > hi + 1e-6) return Loc.F("悬浮隧道顶部离水面至少 {0:0} m（船舶与波浪）", (type.MinCover));
                if (y < lo - 1e-6) return Loc.F("悬浮隧道底部离河床至少 {0:0} m（锚索需要长度）", (type.MinBedClearance));
            }
            else
            {
                if (y > hi + 1e-6) return Loc.T("沉管隧道只能铺在河床上或挖槽埋在河床下");
                if (y < lo - 1e-6) return Loc.F("挖槽太深（最多 {0:0} m）", (type.MaxTrenchDepth));
            }
            var others = Design.Tunnel.PointNodeIds.Where(id => id != n.Id).Select(id => Design.FindNode(id)).Where(o => o != null).Select(o => o.Position.X).ToList();
            double px = x;
            if (others.Any(o => Math.Abs(o - px) < 2)) return Loc.T("离其他控制点太近");
            // Keep the order of the points
            var sorted = Design.Tunnel.PointNodeIds.Select(id => Design.FindNode(id)).Where(o => o != null).OrderBy(o => o.Position.X).ToList();
            int k = sorted.IndexOf(n);
            if (k > 0 && px <= sorted[k - 1].Position.X || k < sorted.Count - 1 && px >= sorted[k + 1].Position.X) return Loc.T("控制点不能越过相邻控制点");
            return null;
        }

        public BuildResult SetTunnelGrade(int element, string grade)
        {
            if(Level.ProvidedInfrastructure!=null)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            var g = TunnelGeo;
            if (g == null) return BuildResult.Fail(Loc.T("没有隧道"));
            if (!g.Type.Grades.TryGetValue(grade, out var gc)) return BuildResult.Fail(Loc.T("未知管节等级"));
            PushUndo();
            for (int i = 0; i < Design.Tunnel.Elements.Count; i++) if (element < 0 || i == element) Design.Tunnel.Elements[i].Grade = grade;
            RegenerateTunnel();
            Changed?.Invoke();
            return BuildResult.Success(0, element < 0 ? Loc.F("全部管节改为{0}", (gc.Name)) : Loc.F("管节 #{0} 改为{1}", (element + 1), (gc.Name)));
        }

        public BuildResult SetTunnelReinforced(int element, bool on)
        {
            if(Level.ProvidedInfrastructure!=null)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            if (Design.Tunnel == null) return BuildResult.Fail(Loc.T("没有隧道"));
            if (TunnelGeo?.Type.IsShield != true) return BuildResult.Fail(Loc.T("沉管 / 悬浮隧道没有地基加固：管身由你建的桥墩、柱子和锚索承担，河床基础可选浅基础 / 桩基础"));
            bool floating = TunnelGeo?.Type.IsFloating == true;
            PushUndo();
            for (int i = 0; i < Design.Tunnel.Elements.Count; i++) if (element < 0 || i == element) Design.Tunnel.Elements[i].Reinforced = on;
            RegenerateTunnel();
            Changed?.Invoke();
            bool shield = TunnelGeo?.Type.IsShield == true;
            return BuildResult.Success(0, shield ? (on ? Loc.T("已注浆加固地层") : Loc.T("已取消注浆加固"))
                : floating ? (on ? Loc.T("已加固锚固基础") : Loc.T("已取消锚固加固")) : on ? Loc.T("已加固地基") : Loc.T("已取消地基加固"));
        }

        /// <summary>Floating tunnel: anchor cables of an element (or all, element -1).</summary>
        /// <summary>Old per-element cable counts (kept only so legacy data can still be edited by tools; off in the game).</summary>
        public static bool LegacyCableButtons = false;

        public BuildResult SetTunnelCables(int element, int count, string cable = null)
        {
            if(Level.ProvidedInfrastructure!=null)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            var g = TunnelGeo;
            if (g == null || !g.Type.IsFloating) return BuildResult.Fail(Loc.T("只有悬浮隧道有锚索"));
            if (!LegacyCableButtons) return BuildResult.Fail(Loc.T("锚索现在要自己建：选钢索或复合钢索，从管底接缝圆点拉到河床（自动生成锚块）、岸上锚点或其他结构"));
            if (cable != null && !g.Type.Cables.ContainsKey(cable)) return BuildResult.Fail(Loc.T("未知锚索类型"));
            count = Math.Clamp(count, 0, g.Type.MaxCableCount);
            PushUndo();
            for (int i = 0; i < Design.Tunnel.Elements.Count; i++)
                if (element < 0 || i == element) { Design.Tunnel.Elements[i].CableCount = count; if (cable != null) Design.Tunnel.Elements[i].Cable = cable; }
            RegenerateTunnel();
            Changed?.Invoke();
            string name = g.Type.Cables[cable ?? g.CableId[Math.Max(0, element)]].Name;
            return BuildResult.Success(0, Loc.F("{0}：{1} 根{2}", (element < 0 ? Loc.T("全部管节") : Loc.F("管节 #{0}", (element + 1))), (count), (name)));
        }

        /// <summary>Floating tunnel: buoyancy level (buoyancy / weight ratio, ballast adjusted to match).</summary>
        public BuildResult SetTunnelBuoyancy(int element, string level)
        {
            if(Level.ProvidedInfrastructure!=null)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            var g = TunnelGeo;
            if (g == null || !g.Type.IsFloating) return BuildResult.Fail(Loc.T("只有悬浮隧道有浮力等级"));
            if (!g.Type.Buoyancy.TryGetValue(level ?? "", out var b)) return BuildResult.Fail(Loc.T("未知浮力等级"));
            PushUndo();
            for (int i = 0; i < Design.Tunnel.Elements.Count; i++) if (element < 0 || i == element) Design.Tunnel.Elements[i].Buoyancy = level;
            RegenerateTunnel();
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.F("{0}：{1}", (element < 0 ? Loc.T("全部管节") : Loc.F("管节 #{0}", (element + 1))), (b.Name)));
        }

        /// <summary>Portal flood protection (both portals): entrance raise (m, stepped and clamped), flood gates, pumps. Null = unchanged.</summary>
        public BuildResult SetPortalProtection(double? raise = null, bool? gate = null, bool? pump = null)
        {
            if(Level.ProvidedInfrastructure!=null)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            var r = Design.Tunnel;
            if (r == null) return BuildResult.Fail(Loc.T("没有隧道"));
            var pc = TunnelRules.Weather.Portal;
            PushUndo();
            if (raise.HasValue) r.PortalRaise = Math.Clamp(Math.Round(raise.Value / pc.RaiseStep) * pc.RaiseStep, 0, pc.MaxRaise);
            if (gate.HasValue) r.FloodGate = gate.Value;
            if (pump.HasValue) r.Pump = pump.Value;
            RegenerateTunnel();
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.F("入口抬高 {0:0.0} m，防洪闸{1}，排水泵{2}", (r.PortalRaise), (r.FloodGate ? Loc.T("有") : Loc.T("无")), (r.Pump ? Loc.T("有") : Loc.T("无"))));
        }

        /// <summary>Bank lift platform length (both lifts), stepped and clamped; longer platforms carry more.</summary>
        public BuildResult SetLiftLength(double length, bool recordUndo = true)
        {
            var r = Design.Tunnel;
            if (r == null) return BuildResult.Fail(Loc.T("没有隧道"));
            var lc = TunnelRules.LiftCfg;
            double l = Math.Clamp(Math.Round(length / lc.LengthStep) * lc.LengthStep, lc.MinLength, lc.MaxLength);
            if (r.LiftLength is double cur && Math.Abs(l - cur) < 1e-9 && r.Lift?.Built != null) return BuildResult.Success(0, LiftSummary());
            if (recordUndo) PushUndo();
            r.LiftLength = l;
            RegenerateTunnel();
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.F("电梯平台 {0:0} m：能装下车身 ≤ {1:0.#} m 的车辆。", (l), (TunnelRules.LiftFits(l))) + LiftSummary());
        }

        /// <summary>Shortest platform that fits every road vehicle of the level (the load is the lift structure's job).</summary>
        public double LiftLengthForLevel()
        {
            var lc = TunnelRules.LiftCfg;
            double need = lc.MinLength;
            foreach (var e in Level.TrafficEvents)
            {
                if (e.Type != TrafficEventType.Vehicle || !Db.Vehicles.TryGetValue(e.VehicleId ?? "", out var v) || RailTrack.IsTrain(v)) continue;
                double rear = v.Axles.Count == 0 ? 0 : v.Axles.Min(a => a.Offset), over = Math.Max(0, (v.Length + rear) / 2);
                need = Math.Max(need, over * 2 - rear + 2 * lc.Margin);
            }
            return Math.Clamp(Math.Ceiling(need / lc.LengthStep - 1e-9) * lc.LengthStep, lc.MinLength, lc.MaxLength);
        }

        /// <summary>Delete the tunnel with its control points, supports and every member attached to the supports.</summary>
        public BuildResult DeleteTunnel()
        {
            if(Level.ProvidedInfrastructure!=null)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            if (Design.Tunnel == null) return BuildResult.Fail(Loc.T("没有隧道"));
            var owned = new HashSet<int>(Design.Nodes.Where(n => n.IsTunnelNode).Select(n => n.Id));
            var liftMembers = Lifts().SelectMany(L => L.Members).ToHashSet();
            var removing = Design.Members.Where(m => owned.Contains(m.StartNodeId) || owned.Contains(m.EndNodeId) || liftMembers.Contains(m)).ToList();
            var why = CheckDemolition(removing);
            if (why != null) return BuildResult.Fail(why);
            double refund = Design.Tunnel.Cost;
            PushUndo();
            ApplyDemolition(removing);
            Design.Members.RemoveAll(m => removing.Contains(m));
            Design.Nodes.RemoveAll(n => owned.Contains(n.Id));
            Design.Nodes.RemoveAll(n => !n.IsAnchor && !n.IsTunnelNode && !Design.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id));
            Design.Tunnel = null;
            PruneMechanisms();
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.F("已删除隧道，退还 ${0:0}", (refund)));
        }

        /// <summary>Tube element under a point of the build plane, or -1.</summary>
        public int TunnelElementAt(double x, double y)
        {
            var g = TunnelGeo;
            if (g == null) return -1;
            double b = g.BottomAt(x);
            if (double.IsNaN(b) || y < b - 0.3 || y > b + g.Type.TubeHeight + 0.3) return -1;
            return g.ElementAt(x);
        }

        /// <summary>
        /// Rebuild elements and joint supports after the route changed: elements keep their settings by index,
        /// supports move with their joint, surplus supports are removed with their members. Updates the price.
        /// </summary>
        /// <summary>
        /// Bendable tubes (shield, floating): the draggable points are exactly the element joints, equally spaced
        /// from wall to wall. Points of other layouts (older saves, a fresh tunnel) are resampled on the old route.
        /// </summary>
        private void SnapPointsToJoints()
        {
            var r = Design.Tunnel;
            var type = TunnelCfg?.Type(Db.GetMaterial(r.MaterialId));
            if (type == null || type.Straight) return;
            var pts = r.PointNodeIds.Select(id => Design.FindNode(id)).Where(o => o != null).OrderBy(o => o.Position.X).ToList();
            if (pts.Count < 2) return;
            var xs = TunnelRules.JointXs(pts[0].Position.X, pts[^1].Position.X, TunnelCfg.ElementLength);
            if (pts.Count == xs.Count && pts.Select((p, k) => Math.Abs(p.Position.X - xs[k])).Max() < 1e-6) return;
            double Y(double x)
            {
                for (int k = 0; k + 1 < pts.Count; k++)
                    if (x <= pts[k + 1].Position.X + 1e-9)
                    {
                        double dx = pts[k + 1].Position.X - pts[k].Position.X;
                        return dx < 1e-9 ? pts[k].Position.Y : pts[k].Position.Y + (pts[k + 1].Position.Y - pts[k].Position.Y) * (x - pts[k].Position.X) / dx;
                    }
                return pts[^1].Position.Y;
            }
            var ys = xs.Select(Y).ToList();
            var keep = new List<int>();
            for (int k = 0; k < xs.Count; k++)
            {
                // Reuse the end nodes (and any point already on a joint); create the rest
                var node = k == 0 ? pts[0] : k == xs.Count - 1 ? pts[^1] : pts.FirstOrDefault(p => Math.Abs(p.Position.X - xs[k]) < 1e-6);
                if (node == null) { node = new BridgeNode { Id = Design.NextNodeId++, TunnelPoint = true }; Design.Nodes.Add(node); }
                node.Position = new Vec3d(xs[k], ys[k]);
                keep.Add(node.Id);
            }
            var drop = r.PointNodeIds.Where(id => !keep.Contains(id)).ToHashSet();
            Design.Nodes.RemoveAll(o => drop.Contains(o.Id));
            r.PointNodeIds.Clear(); r.PointNodeIds.AddRange(keep);
        }

        private void RegenerateTunnel()
        {
            var r = Design.Tunnel;
            if (r == null) return;
            r.PointNodeIds.RemoveAll(id => Design.FindNode(id) == null);
            SnapPointsToJoints();
            var g = TunnelGeometry.Build(Design, Db);
            if (g == null) { r.Cost = 0; return; }
            while (r.Elements.Count < g.Count) r.Elements.Add(r.Elements.Count > 0 ? r.Elements[^1].Clone() : new TunnelElement());
            if (r.Elements.Count > g.Count) r.Elements.RemoveRange(g.Count, r.Elements.Count - g.Count);
            g = TunnelGeometry.Build(Design, Db);
            double L = TunnelCfg.ElementLength;
            // Connection points at the element joints, on the tube bottom. Bendable tubes (floating) use their joint
            // control points themselves; straight tubes (immersed) get a joint node per element joint.
            for (int j = 1; j < g.Count && g.Type.HasSupports; j++)
            {
                var p = new Vec3d(g.X0[j], g.Y0[j]);
                if (!g.Type.Straight)
                {
                    var pt = Design.Nodes.FirstOrDefault(q => q.TunnelPoint && Math.Abs(q.Position.X - p.X) < 1e-4);
                    if (pt != null)
                    {
                        // A separate (older) joint node is merged into the control point
                        var old = Design.Nodes.FirstOrDefault(q => q.TunnelJoint == j && !q.TunnelPoint);
                        if (old != null) { foreach (var m in Design.Members) { if (m.StartNodeId == old.Id) m.StartNodeId = pt.Id; if (m.EndNodeId == old.Id) m.EndNodeId = pt.Id; } Design.Nodes.Remove(old); }
                        foreach (var q in Design.Nodes) if (q.TunnelJoint == j && q != pt) q.TunnelJoint = 0;
                        pt.TunnelJoint = j;
                        continue;
                    }
                }
                var n = Design.Nodes.FirstOrDefault(q => q.TunnelJoint == j);
                if (n == null) Design.Nodes.Add(new BridgeNode { Id = Design.NextNodeId++, Position = p, TunnelJoint = j });
                else n.Position = p;
            }
            foreach (var q in Design.Nodes) if (q.TunnelPoint && q.TunnelJoint > 0 && (q.TunnelJoint >= g.Count || Math.Abs(q.Position.X - g.X0[q.TunnelJoint]) > 1e-4)) q.TunnelJoint = 0;
            var surplus = new HashSet<int>(Design.Nodes.Where(q => !q.TunnelPoint && (q.TunnelJoint >= g.Count || q.TunnelJoint > 0 && !g.Type.HasSupports)).Select(q => q.Id));
            if (surplus.Count > 0)
            {
                Design.Members.RemoveAll(m => surplus.Contains(m.StartNodeId) || surplus.Contains(m.EndNodeId));
                Design.Nodes.RemoveAll(q => surplus.Contains(q.Id));
                Design.Nodes.RemoveAll(q => !q.IsAnchor && !q.IsTunnelNode && !Design.Members.Any(m => m.StartNodeId == q.Id || m.EndNodeId == q.Id));
            }
            // Bank lifts: fixed nodes follow the shafts, the player's lift members follow along
            SyncLifts(g);
            foreach (var m in Design.Members)
            {
                var a = Design.FindNode(m.StartNodeId); var b = Design.FindNode(m.EndNodeId);
                if (a == null || b == null) continue;
                m.Length = Vec3d.Distance(a.Position, b.Position);
                if (!m.Existing) m.Cost = m.Length * (Db.GetMaterial(m.MaterialId)?.CostPerMeter ?? 0);
            }
            r.Cost = TunnelRules.Cost(g, Level);
            // Lining ties (nodes held by the shaft end walls) are bought per tie
            if(!Level.MechanicalLift)r.Cost += Lifts().Sum(L => L.Ties.Count) * LiftCfgOf.BraceCost;
        }

        // ---------------- 桥墩与基础 ----------------
        /// <summary>某处河床（或岩墩顶）的标高。</summary>
        public double PierBedY(double x)
        {
            var t = Level.Terrain;
            foreach (var p in t.Pillars) if (Math.Abs(x - p.X) <= p.Width / 2) return p.TopY;
            return t.GroundYInRiver(x);
        }

        /// <summary>预览桥墩：返回不能建造的原因（null 表示可以），以及高度与费用。</summary>
        public string PreviewPier(double x, double topY, string materialId, out double height, out double cost)
        {
            height = 0; cost = 0;
            var mat = Db.GetMaterial(materialId);
            if (mat == null || !mat.IsPier) return Loc.T("不是桥墩材料");
            if (!Level.AllowedMaterials.Contains(materialId)) return Loc.T("本关不允许使用该材料");
            var t = Level.Terrain;
            if (x <= 0.3 || x >= t.RiverWidth - 0.3) return Loc.T("桥墩只能建在河道内");
            foreach (var z in t.NoBuildZones)
                if (x > z.X0 + 0.01 && x < z.X1 - 0.01) return Loc.F("{0}：不能建桥墩", (z.Name));
            double bed = PierBedY(x);
            height = topY - bed;
            if (height < 1.5) return Loc.T("桥墩太矮");
            if (height > 2 * mat.MaximumLength + 1e-6) return Loc.F("桥墩太高（{0:0.0} m，最高 {1:0} m）", (height), (2 * mat.MaximumLength));
            if (Design.Nodes.Any(n => n.PierBase && Math.Abs(n.Position.X - x) < 1.5)) return Loc.T("这里已经有桥墩");
            cost = height * mat.CostPerMeter + FoundationRules.Cost(FoundationType.Shallow);
            return null;
        }

        /// <summary>
        /// 建造桥墩：从河床（或岩墩顶）立起到 topY，生成墩底基础节点、中部支点与顶部支点（两根墩身构件）。
        /// 如果顶部位置已有节点（例如桥面节点），桥墩直接顶住它。默认浅基础。
        /// </summary>
        public BuildResult AddPier(double x, double topY, string materialId)
        {
            if(Level.ProvidedInfrastructure!=null)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            var why = PreviewPier(x, topY, materialId, out double h, out _);
            if (why != null) return BuildResult.Fail(why);
            var mat = Db.GetMaterial(materialId);
            double bed = PierBedY(x);
            double midY = Snap(bed + h / 2);
            var topNode = NodeAt(x, topY, MinNodeSpacing);
            if (topNode == null && CheckNodePosition(x, topY, -1, true) != null && CheckNodePosition(x, topY) != null)
                return BuildResult.Fail(Loc.T("顶部位置不能放置支点"));
            PushUndo();
            var baseN = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, bed), PierBase = true, Foundation = FoundationType.Shallow };
            Design.Nodes.Add(baseN);
            var midN = NodeAt(x, midY, MinNodeSpacing);
            if (midN == null) { midN = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, midY), PierPart = true }; Design.Nodes.Add(midN); }
            if (topNode == null) { topNode = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, topY), PierPart = true }; Design.Nodes.Add(topNode); }
            else topNode.PierPart = true;
            foreach (var (a, b) in new[] { (baseN, midN), (midN, topNode) })
            {
                double len = Vec3d.Distance(a.Position, b.Position);
                Design.Members.Add(new BridgeMember { Id = Design.NextMemberId++, StartNodeId = a.Id, EndNodeId = b.Id, MaterialId = materialId, Length = len, Cost = len * mat.CostPerMeter });
            }
            Changed?.Invoke();
            return BuildResult.Success(baseN.Id, Loc.F("已建造{0}（高 {1:0.0} m，{2}）", (mat.Name), (h), (FoundationRules.Name(FoundationType.Shallow))));
        }

        /// <summary>切换桥墩基础类型（浅基础 / 桩基础）。</summary>
        public BuildResult SetFoundation(int baseNodeId, FoundationType type)
        {
            var n = Design.FindNode(baseNodeId);
            if(n?.Provided==true)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            if (n == null || !n.PierBase) return BuildResult.Fail(Loc.T("不是桥墩基础"));
            if (n.Foundation == type) return BuildResult.Success(n.Id);
            PushUndo();
            n.Foundation = type;
            Changed?.Invoke();
            return BuildResult.Success(n.Id, Loc.F("已改为{0}（${1:0}）", (FoundationRules.Name(type)), (FoundationRules.Cost(type))));
        }

        /// <summary>找到桥墩的墩底节点（从墩身上任一节点向下追溯）。</summary>
        public BridgeNode PierBaseOf(int nodeId)
        {
            var n = Design.FindNode(nodeId);
            for (int guard = 0; n != null && guard < 10; guard++)
            {
                if (n.PierBase) return n;
                var down = Design.Members.Where(m => (m.StartNodeId == n.Id || m.EndNodeId == n.Id) && Db.GetMaterial(m.MaterialId)?.IsPier == true)
                    .Select(m => Design.FindNode(m.Other(n.Id))).Where(o => o != null && o.Position.Y < n.Position.Y).OrderBy(o => o.Position.Y).FirstOrDefault();
                n = down;
            }
            return null;
        }

        // ---------------- 旧桥加固：拆除与修复 ----------------
        public int DemolitionsLeft => Level.MaxDemolitions < 0 ? int.MaxValue : Math.Max(0, Level.MaxDemolitions - Design.DemolishedCount);

        private string CheckDemolition(IEnumerable<BridgeMember> removing)
        {
            if(removing.Any(m=>m.Provided))return Loc.T(LiftChallengeRules.Locked);
            int c = removing.Count(m => m.Existing);
            if (c == 0 || Level.MaxDemolitions < 0) return null;
            if (c > DemolitionsLeft) return Loc.F("旧构件最多只能拆除 {0} 根（剩余 {1} 根，这次要拆 {2} 根）", (Level.MaxDemolitions), (DemolitionsLeft), (c));
            return null;
        }

        private void ApplyDemolition(IEnumerable<BridgeMember> removing)
        {
            foreach (var m in removing.Where(m => m.Existing))
            {
                Design.DemolishedCount++;
                Design.DemolitionCost += m.Length * Level.DemolitionCostPerMeter;
            }
        }

        /// <summary>修复旧构件费用（$）。</summary>
        public double RepairCostOf(BridgeMember m)
        {
            var mat = Db.GetMaterial(m.MaterialId);
            return mat == null ? 0 : m.Length * mat.CostPerMeter * Level.RepairCostRatio * (1 - m.Condition);
        }

        /// <summary>修复老化的旧构件：承载力恢复到全新状态，按损耗程度收费。</summary>
        public BuildResult RepairMember(int memberId)
        {
            var m = Design.FindMember(memberId);
            if (m == null) return BuildResult.Fail(Loc.T("构件不存在"));
            if(m.Provided)return BuildResult.Fail(Loc.T(LiftChallengeRules.Locked));
            if (!m.Existing || m.Condition >= 1 - 1e-9) return BuildResult.Fail(Loc.T("这根构件不需要修复"));
            double cost = RepairCostOf(m);
            PushUndo();
            Design.RepairCost += cost;
            m.Condition = 1;
            Changed?.Invoke();
            return BuildResult.Success(m.Id, Loc.F("已修复构件 #{0}，花费 ${1:0}", (m.Id), (cost)));
        }

        /// <summary>按当前几何重新计算所有构件长度与费用，并清理悬空引用（用于加载存档后的校验）。</summary>
        public void RecalculateAll()
        {
            Design.Members.RemoveAll(m => Design.FindNode(m.StartNodeId) == null || Design.FindNode(m.EndNodeId) == null || Db.GetMaterial(m.MaterialId) == null);
            foreach(var rigid in Design.Nodes.Where(n=>n.IsRigidSplit))
            {
                var ra=Design.FindNode(rigid.RigidEndA);var rb=Design.FindNode(rigid.RigidEndB);
                if(ra!=null && rb!=null)rigid.Position=new Vec3d(ra.Position.X*(1-rigid.RigidFraction)+rb.Position.X*rigid.RigidFraction,ra.Position.Y*(1-rigid.RigidFraction)+rb.Position.Y*rigid.RigidFraction);
            }
            foreach (var m in Design.Members)
            {
                var mat = Db.GetMaterial(m.MaterialId);
                m.Length = Vec3d.Distance(Design.FindNode(m.StartNodeId).Position, Design.FindNode(m.EndNodeId).Position);
                m.Cost = m.Existing ? 0 : m.Length * mat.CostPerMeter;   // 旧桥原有构件不计费
                m.DamageState = DamageState.Intact;
            }
            // 确保锚点与关卡定义一致（防止存档篡改锚点）
            foreach (var a in Level.Anchors.Where(a => a.Enabled))
            {
                var node = Design.Nodes.FirstOrDefault(n => n.IsAnchor && n.AnchorId == a.Id);
                if (node == null) Design.Nodes.Add(new BridgeNode { Id = Design.NextNodeId++, Position = a.Position, IsAnchor = true, AnchorId = a.Id, SupportType = a.SupportType });
                else { node.Position = a.Position; node.SupportType = a.SupportType; }
            }
            foreach (var n in Design.Nodes.Where(n => n.IsAnchor && !Level.Anchors.Any(a => a.Enabled && a.Id == n.AnchorId)).ToList())
            { n.IsAnchor = false; n.AnchorId = null; n.SupportType = SupportType.None; }
            int maxN = Design.Nodes.Count == 0 ? 0 : Design.Nodes.Max(n => n.Id);
            int maxM = Design.Members.Count == 0 ? 0 : Design.Members.Max(m => m.Id);
            Design.NextNodeId = Math.Max(Design.NextNodeId, maxN + 1);
            Design.NextMemberId = Math.Max(Design.NextMemberId, maxM + 1);
            // Tunnel sections: price from the material, unknown materials dropped with their support points
            if (Design.Tunnel != null && TunnelCfg?.Type(Db.GetMaterial(Design.Tunnel.MaterialId)) == null)
            {
                // Unknown tunnel type (tunnels.json missing or changed): drop the tunnel and its nodes
                Design.Nodes.RemoveAll(n => n.TunnelPoint || n.IsTunnelSupport);
                Design.Tunnel = null;
            }
            if (Design.Tunnel != null)
            {
                var tt = TunnelCfg.Type(Db.GetMaterial(Design.Tunnel.MaterialId));
                double bedY = TunnelRules.BedY(Level.Terrain);
                TunnelRules.FlatBed(Level.Terrain, out double fb0, out double fb1);
                TunnelRules.BottomRange(Level.Terrain, tt, out double ylo, out double yhi);
                foreach (var n in Design.Nodes.Where(n => n.TunnelPoint))
                    n.Position = new Vec3d(Math.Clamp(n.Position.X, fb0, fb1), Math.Clamp(n.Position.Y, Math.Min(ylo, yhi), yhi));
                // Straight tubes (immersed) keep only their two ends; older saves may have bent ones
                if (tt.Straight)
                {
                    var all = Design.Tunnel.PointNodeIds.Select(id => Design.FindNode(id)).Where(o => o != null).OrderBy(o => o.Position.X).ToList();
                    if (all.Count > 2)
                    {
                        var middle = all.Skip(1).Take(all.Count - 2).Select(o => o.Id).ToHashSet();
                        Design.Tunnel.PointNodeIds.RemoveAll(middle.Contains);
                        Design.Nodes.RemoveAll(o => middle.Contains(o.Id) && !Design.Members.Any(m => m.StartNodeId == o.Id || m.EndNodeId == o.Id));
                    }
                }
                // Both ends against the bank walls (older saves could end anywhere on the bed)
                var pts = Design.Tunnel.PointNodeIds.Select(id => Design.FindNode(id)).Where(o => o != null).OrderBy(o => o.Position.X).ToList();
                if (pts.Count >= 2)
                {
                    pts[0].Position = new Vec3d(fb0, pts[0].Position.Y);
                    pts[^1].Position = new Vec3d(fb1, pts[^1].Position.Y);
                }
                RegenerateTunnel();
            }
            else foreach (var n in Design.Nodes) { n.TunnelPoint = false; n.TunnelJoint = 0; }
        }

        public double CostByType(ComponentType type, string materialId = null)
        {
            double s = 0;
            foreach(var rigid in Design.Nodes.Where(n=>n.IsRigidSplit))
            {
                var ra=Design.FindNode(rigid.RigidEndA);var rb=Design.FindNode(rigid.RigidEndB);
                if(ra!=null && rb!=null)rigid.Position=new Vec3d(ra.Position.X*(1-rigid.RigidFraction)+rb.Position.X*rigid.RigidFraction,ra.Position.Y*(1-rigid.RigidFraction)+rb.Position.Y*rigid.RigidFraction);
            }
            foreach (var m in Design.Members)
            {
                var mat = Db.GetMaterial(m.MaterialId);
                if (mat.ComponentType == type && (materialId == null || mat.MaterialId == materialId)) s += m.EffectiveCost();
            }
            return s;
        }
    }
}
