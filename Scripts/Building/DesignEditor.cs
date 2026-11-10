using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;

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
    public class DesignEditor
    {
        public const double GridSize = 0.5;          // 网格吸附间距 m
        public const double MinNodeSpacing = 0.25;   // 节点最小间距 m（防止重复节点）
        public const double MinMemberLength = 0.4;   // 最短构件 m
        private const int MaxHistory = 200;

        public BridgeDesign Design { get; private set; }
        public LevelDefinition Level { get; }
        public GameDatabase Db { get; }

        private readonly List<BridgeDesign> _undo = new();
        private readonly List<BridgeDesign> _redo = new();

        /// <summary>方案发生任何变化时触发（UI 与渲染据此刷新）。</summary>
        public event Action Changed;

        public DesignEditor(BridgeDesign design, LevelDefinition level, GameDatabase db)
        {
            Level = level; Db = db;
            Design = design ?? BridgeDesign.CreateForLevel(level);
            RecalculateAll();
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
            RecalculateAll();
            Changed?.Invoke();
        }

        // ---------------- 查询 ----------------
        public BridgeNode NodeAt(double x, double y, double radius)
        {
            BridgeNode best = null; double bestD = radius;
            foreach (var n in Design.Nodes)
            {
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
        public string CheckNodePosition(double x, double y, int ignoreNodeId = -1, bool pierPart = false)
        {
            var t = Level.Terrain;
            if (pierPart)
            {
                // 桥墩上的支点：可以在水面以下，但必须在河道内、河床以上
                if (x <= 0.3 || x >= t.RiverWidth - 0.3) return "桥墩只能建在河道内";
                if (y < PierBedY(x) + 0.5) return "支点太低（已到河床）";
                foreach (var n in Design.Nodes)
                {
                    if (n.Id == ignoreNodeId) continue;
                    double ddx = n.Position.X - x, ddy = n.Position.Y - y;
                    if (ddx * ddx + ddy * ddy < MinNodeSpacing * MinNodeSpacing) return "该位置已有节点";
                }
                return null;
            }
            if (x < -t.BuildMarginX || x > t.RiverWidth + t.BuildMarginX) return "超出可建造区域（水平方向）";
            if (y < t.BuildMinY || y > t.BuildMaxY) return "超出可建造区域（竖直方向）";
            // 河道内不能在水面以下建造；两岸土体内允许放置节点——普通节点永远不是支座，
            // 只有关卡锚点提供支撑，因此不会获得“免费地面支撑”
            if (x > 0 && x < t.RiverWidth && y < t.WaterLevel + 0.01) return "不能在水面以下建造";
            foreach (var z in t.NoBuildZones)
                if (x > z.X0 + 0.01 && x < z.X1 - 0.01 && y < z.BelowY - 0.01) return $"{z.Name}：只能利用两岸，不能在这里建桥墩或下部支撑";
            foreach (var n in Design.Nodes)
            {
                if (n.Id == ignoreNodeId) continue;
                double dx = n.Position.X - x, dy = n.Position.Y - y;
                if (dx * dx + dy * dy < MinNodeSpacing * MinNodeSpacing) return "该位置已有节点";
            }
            return null;
        }

        public MemberPreview PreviewMember(int startId, int endId, string materialId)
        {
            var p = new MemberPreview();
            var mat = Db.GetMaterial(materialId);
            var a = Design.FindNode(startId); var b = Design.FindNode(endId);
            p.MaterialName = mat?.Name ?? "?";
            if (mat == null) { p.Reason = "未知材料"; return p; }
            if (a == null || b == null) { p.Reason = "节点不存在"; return p; }
            p.Length = Vec3d.Distance(a.Position, b.Position);
            p.Cost = p.Length * mat.CostPerMeter;
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
            return $"倾角 {ColumnRules.TiltDegrees(a, b):0}° 超过 {mat.MaxTiltDegrees:0}°，测试时会倒塌";
        }

        /// <summary>预览从节点到任意点的构件（终点尚未有节点时）。</summary>
        public MemberPreview PreviewMemberToPoint(int startId, double x, double y, string materialId)
        {
            var p = new MemberPreview();
            var mat = Db.GetMaterial(materialId);
            var a = Design.FindNode(startId);
            p.MaterialName = mat?.Name ?? "?";
            if (mat == null || a == null) { p.Reason = "无效起点"; return p; }
            p.Length = Vec3d.Distance(a.Position, new Vec3d(x, y));
            p.Cost = p.Length * mat.CostPerMeter;
            string reason = CheckNodePosition(x, y);
            if (reason == null && p.Length < MinMemberLength) reason = $"太短（至少 {MinMemberLength:0.#} m）";
            if (reason == null && p.Length > mat.MaximumLength) reason = $"超长 {p.Length - mat.MaximumLength:0.0} m（{mat.Name}最长 {mat.MaximumLength:0.#} m）";
            if (reason == null && mat.IsDeck && Math.Abs(x - a.Position.X) < 0.3) reason = "桥面不能竖直铺设";
            if (reason == null && LimitReached(mat)) reason = $"本关{mat.Name}限量 {Level.MaterialLimits[mat.MaterialId]} 根，已用完";
            if (reason == null && mat.IsColumn)
                ColumnRules.FindBase(mat, a, new BridgeNode { Id = -1, Position = new Vec3d(x, y) }, Level, out reason);
            // 预算允许超支（超支会降低最终评分），因此不阻止建造
            p.Reason = reason; p.Allowed = reason == null;
            p.Warning = ColumnWarning(mat, a.Position, new Vec3d(x, y));
            return p;
        }

        private string CheckMember(BridgeNode a, BridgeNode b, MaterialDefinition mat, double cost)
        {
            if (!Level.AllowedMaterials.Contains(mat.MaterialId)) return "本关不允许使用该材料";
            if (LimitReached(mat)) return $"本关{mat.Name}限量 {Level.MaterialLimits[mat.MaterialId]} 根，已用完";
            if (a.Id == b.Id) return "不能连接同一个节点";
            double len = Vec3d.Distance(a.Position, b.Position);
            if (len < MinMemberLength) return "构件长度过短";
            if (len > mat.MaximumLength + 1e-9) return $"超长 {len - mat.MaximumLength:0.0} m（{mat.Name}最长 {mat.MaximumLength:0.#} m）";
            if (Design.Members.Any(m => m.Connects(a.Id, b.Id))) return "两节点之间已有构件";
            if (mat.IsDeck && Math.Abs(b.Position.X - a.Position.X) < 0.3) return "桥面不能竖直铺设";
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
            if (m == null) return BuildResult.Fail("构件不存在");
            var mat = Db.GetMaterial(m.MaterialId);
            if (mat == null || !mat.Extendable) return BuildResult.Fail("该构件不能伸缩");
            var a = Design.FindNode(m.StartNodeId); var b = Design.FindNode(m.EndNodeId);
            var moving = !b.IsAnchor ? b : !a.IsAnchor ? a : null;
            if (moving == null) return BuildResult.Fail("两端都是锚点，无法伸缩");
            var fixedEnd = moving == b ? a : b;
            double len = Vec3d.Distance(a.Position, b.Position), newLen = len + delta;
            if (newLen < MinMemberLength * 2) return BuildResult.Fail("已缩到最短");
            if (newLen > mat.MaximumLength + 1e-9) return BuildResult.Fail($"已伸到最长 {mat.MaximumLength:0.#} m");
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
            if (mat == null) return BuildResult.Fail("未知材料");
            if (a == null || b == null) return BuildResult.Fail("节点不存在");
            double len = Vec3d.Distance(a.Position, b.Position);
            double cost = len * mat.CostPerMeter;
            string r = CheckMember(a, b, mat, cost);
            if (r != null) return BuildResult.Fail(r);
            PushUndo();
            var m = new BridgeMember { Id = Design.NextMemberId++, StartNodeId = a.Id, EndNodeId = b.Id, MaterialId = mat.MaterialId, Length = len, Cost = cost };
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
            if (!Level.AllowedMaterials.Contains(materialId)) return BuildResult.Fail("本关不允许使用该材料");
            var a = Design.FindNode(startId);
            if (mat.IsDeck && Math.Abs(x - a.Position.X) < 0.3) return BuildResult.Fail("桥面不能竖直铺设");
            PushUndo();
            var n = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, y, 0) };
            Design.Nodes.Add(n);
            var m = new BridgeMember { Id = Design.NextMemberId++, StartNodeId = startId, EndNodeId = n.Id, MaterialId = materialId, Length = p.Length, Cost = p.Cost };
            Design.Members.Add(m);
            Changed?.Invoke();
            return BuildResult.Success(m.Id);
        }

        /// <summary>移动节点：锚点不可移动；会重新计算相连构件的长度与费用，超长则拒绝（预算允许超支）。</summary>
        public BuildResult MoveNode(int nodeId, double x, double y, bool recordUndo = true)
        {
            var n = Design.FindNode(nodeId);
            if (n == null) return BuildResult.Fail("节点不存在");
            if (n.IsAnchor) return BuildResult.Fail("固定锚点不能移动");
            if (n.PierBase) return BuildResult.Fail("桥墩基础不能移动（可以删除桥墩后重建）");
            if (n.PierPart && Math.Abs(x - n.Position.X) > 1e-6) x = n.Position.X;   // 桥墩支点只能上下移动
            if (Design.Members.Any(m => m.Existing && (m.StartNodeId == nodeId || m.EndNodeId == nodeId)))
                return BuildResult.Fail("旧桥节点不能移动（可以拆除、修复或加固相连的旧构件）");
            string r = CheckNodePosition(x, y, nodeId, n.PierPart);
            if (r != null) return BuildResult.Fail(r);
            var newPos = new Vec3d(x, y, 0);
            double deltaCost = 0;
            foreach (var m in Design.Members.Where(m => m.StartNodeId == nodeId || m.EndNodeId == nodeId))
            {
                var mat = Db.GetMaterial(m.MaterialId);
                var other = Design.FindNode(m.Other(nodeId));
                double len = Vec3d.Distance(newPos, other.Position);
                if (len > mat.MaximumLength + 1e-9) return BuildResult.Fail($"构件 #{m.Id} 将超过最大长度");
                if (len < MinMemberLength) return BuildResult.Fail($"构件 #{m.Id} 将过短");
                if (mat.IsDeck && Math.Abs(other.Position.X - x) < 0.3) return BuildResult.Fail("桥面不能竖直");
                if (mat.IsColumn)
                {
                    var moved = new BridgeNode { Id = n.Id, Position = newPos, IsAnchor = false };
                    bool startIsMoved = m.StartNodeId == nodeId;
                    ColumnRules.FindBase(mat, startIsMoved ? moved : other, startIsMoved ? other : moved, Level, out var why);
                    if (why != null) return BuildResult.Fail($"构件 #{m.Id}：{why}");
                }
                deltaCost += len * mat.CostPerMeter - m.Cost;
            }

            if (recordUndo) PushUndo();
            n.Position = newPos;
            RecalculateAll();
            Changed?.Invoke();
            return BuildResult.Success(nodeId);
        }

        public BuildResult DeleteNode(int nodeId)
        {
            var n = Design.FindNode(nodeId);
            if (n == null) return BuildResult.Fail("节点不存在");
            if (n.IsAnchor) return BuildResult.Fail("固定锚点不能删除");
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
                Design.Nodes.RemoveAll(x => !x.IsAnchor && !Design.Members.Any(m => m.StartNodeId == x.Id || m.EndNodeId == x.Id));
                foreach (var x in Design.Nodes) if (x.PierPart && !Design.Members.Any(m => (m.StartNodeId == x.Id || m.EndNodeId == x.Id) && Db.GetMaterial(m.MaterialId)?.IsPier == true)) x.PierPart = false;
                Changed?.Invoke();
                return BuildResult.Success(0, "已删除整座桥墩");
            }
            var removing = Design.Members.Where(m => m.StartNodeId == nodeId || m.EndNodeId == nodeId).ToList();
            var why = CheckDemolition(removing);
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            ApplyDemolition(removing);
            Design.Members.RemoveAll(m => m.StartNodeId == nodeId || m.EndNodeId == nodeId);
            Design.Nodes.Remove(n);
            Changed?.Invoke();
            return BuildResult.Success();
        }

        /// <summary>批量删除（框选）：一次撤销即可恢复。锚点会被跳过；删除节点时连同相连构件一起删除。</summary>
        public BuildResult DeleteMany(IEnumerable<int> nodeIds, IEnumerable<int> memberIds)
        {
            var nodes = new HashSet<int>(nodeIds.Where(id => Design.FindNode(id) is { IsAnchor: false }));
            var mems = new HashSet<int>(memberIds.Where(id => Design.FindMember(id) != null));
            foreach (var m in Design.Members) if (nodes.Contains(m.StartNodeId) || nodes.Contains(m.EndNodeId)) mems.Add(m.Id);
            if (nodes.Count == 0 && mems.Count == 0) return BuildResult.Fail("没有可删除的对象（锚点不能删除）");
            double refund = Design.Members.Where(m => mems.Contains(m.Id)).Sum(m => m.Cost);
            var removing = Design.Members.Where(m => mems.Contains(m.Id)).ToList();
            var why = CheckDemolition(removing);
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            ApplyDemolition(removing);
            Design.Members.RemoveAll(m => mems.Contains(m.Id));
            Design.Nodes.RemoveAll(n => nodes.Contains(n.Id));
            // 清理删除构件后留下的孤立普通节点
            Design.Nodes.RemoveAll(n => !n.IsAnchor && !Design.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id));
            Changed?.Invoke();
            return BuildResult.Success(0, $"已删除 {mems.Count} 根构件，退还 ${refund:0}");
        }

        public BuildResult DeleteMember(int memberId)
        {
            var m = Design.FindMember(memberId);
            if (m == null) return BuildResult.Fail("构件不存在");
            var why = CheckDemolition(new[] { m });
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            ApplyDemolition(new[] { m });
            Design.Members.Remove(m);   // 删除即退还费用（成本由构件列表实时求和）
            // 不保留孤立的普通节点（游戏中节点只能作为构件端点存在）
            Design.Nodes.RemoveAll(n => !n.IsAnchor && (n.Id == m.StartNodeId || n.Id == m.EndNodeId)
                                        && !Design.Members.Any(x => x.StartNodeId == n.Id || x.EndNodeId == n.Id));
            Changed?.Invoke();
            return BuildResult.Success();
        }

        // ---------------- 桥墩与基础 ----------------
        /// <summary>某处河床（或岩墩顶）的标高。</summary>
        public double PierBedY(double x)
        {
            var t = Level.Terrain;
            foreach (var p in t.Pillars) if (Math.Abs(x - p.X) <= p.Width / 2) return p.TopY;
            return t.RiverBedDepth;
        }

        /// <summary>预览桥墩：返回不能建造的原因（null 表示可以），以及高度与费用。</summary>
        public string PreviewPier(double x, double topY, string materialId, out double height, out double cost)
        {
            height = 0; cost = 0;
            var mat = Db.GetMaterial(materialId);
            if (mat == null || !mat.IsPier) return "不是桥墩材料";
            if (!Level.AllowedMaterials.Contains(materialId)) return "本关不允许使用该材料";
            var t = Level.Terrain;
            if (x <= 0.3 || x >= t.RiverWidth - 0.3) return "桥墩只能建在河道内";
            foreach (var z in t.NoBuildZones)
                if (x > z.X0 + 0.01 && x < z.X1 - 0.01) return $"{z.Name}：不能建桥墩";
            double bed = PierBedY(x);
            height = topY - bed;
            if (height < 1.5) return "桥墩太矮";
            if (height > 2 * mat.MaximumLength + 1e-6) return $"桥墩太高（{height:0.0} m，最高 {2 * mat.MaximumLength:0} m）";
            if (Design.Nodes.Any(n => n.PierBase && Math.Abs(n.Position.X - x) < 1.5)) return "这里已经有桥墩";
            cost = height * mat.CostPerMeter + FoundationRules.Cost(FoundationType.Shallow);
            return null;
        }

        /// <summary>
        /// 建造桥墩：从河床（或岩墩顶）立起到 topY，生成墩底基础节点、中部支点与顶部支点（两根墩身构件）。
        /// 如果顶部位置已有节点（例如桥面节点），桥墩直接顶住它。默认浅基础。
        /// </summary>
        public BuildResult AddPier(double x, double topY, string materialId)
        {
            var why = PreviewPier(x, topY, materialId, out double h, out _);
            if (why != null) return BuildResult.Fail(why);
            var mat = Db.GetMaterial(materialId);
            double bed = PierBedY(x);
            double midY = Snap(bed + h / 2);
            var topNode = NodeAt(x, topY, MinNodeSpacing);
            if (topNode == null && CheckNodePosition(x, topY, -1, true) != null && CheckNodePosition(x, topY) != null)
                return BuildResult.Fail("顶部位置不能放置支点");
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
            return BuildResult.Success(baseN.Id, $"已建造{mat.Name}（高 {h:0.0} m，{FoundationRules.Name(FoundationType.Shallow)}）");
        }

        /// <summary>切换桥墩基础类型（浅基础 / 桩基础）。</summary>
        public BuildResult SetFoundation(int baseNodeId, FoundationType type)
        {
            var n = Design.FindNode(baseNodeId);
            if (n == null || !n.PierBase) return BuildResult.Fail("不是桥墩基础");
            if (n.Foundation == type) return BuildResult.Success(n.Id);
            PushUndo();
            n.Foundation = type;
            Changed?.Invoke();
            return BuildResult.Success(n.Id, $"已改为{FoundationRules.Name(type)}（${FoundationRules.Cost(type):0}）");
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
            int c = removing.Count(m => m.Existing);
            if (c == 0 || Level.MaxDemolitions < 0) return null;
            if (c > DemolitionsLeft) return $"旧构件最多只能拆除 {Level.MaxDemolitions} 根（剩余 {DemolitionsLeft} 根，这次要拆 {c} 根）";
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
            if (m == null) return BuildResult.Fail("构件不存在");
            if (!m.Existing || m.Condition >= 1 - 1e-9) return BuildResult.Fail("这根构件不需要修复");
            double cost = RepairCostOf(m);
            PushUndo();
            Design.RepairCost += cost;
            m.Condition = 1;
            Changed?.Invoke();
            return BuildResult.Success(m.Id, $"已修复构件 #{m.Id}，花费 ${cost:0}");
        }

        /// <summary>按当前几何重新计算所有构件长度与费用，并清理悬空引用（用于加载存档后的校验）。</summary>
        public void RecalculateAll()
        {
            Design.Members.RemoveAll(m => Design.FindNode(m.StartNodeId) == null || Design.FindNode(m.EndNodeId) == null || Db.GetMaterial(m.MaterialId) == null);
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
        }

        public double CostByType(ComponentType type, string materialId = null)
        {
            double s = 0;
            foreach (var m in Design.Members)
            {
                var mat = Db.GetMaterial(m.MaterialId);
                if (mat.ComponentType == type && (materialId == null || mat.MaterialId == materialId)) s += m.Cost;
            }
            return s;
        }
    }
}
