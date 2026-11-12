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
    public class DesignEditor
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

        public DesignEditor(BridgeDesign design, LevelDefinition level, GameDatabase db)
        {
            Level = level; Db = db;
            Design = design ?? BridgeDesign.CreateForLevel(level);
            CrossBeams.SplitExistingBlocking(Design, Level, Db);
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
            if (x > 0 && x < t.RiverWidth && y < t.WaterLevel + 0.01) return Loc.T("不能在水面以下建造");
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
            if (reason == null && mat.IsColumn)
                ColumnRules.FindBase(mat, a, new BridgeNode { Id = -1, Position = new Vec3d(x, y) }, Level, out reason);
            // 预算允许超支（超支会降低最终评分），因此不阻止建造
            p.Reason = reason; p.Allowed = reason == null;
            p.Warning = ColumnWarning(mat, a.Position, new Vec3d(x, y));
            return p;
        }

        private string CheckMember(BridgeNode a, BridgeNode b, MaterialDefinition mat, double cost)
        {
            if (!Level.AllowedMaterials.Contains(mat.MaterialId)) return Loc.T("本关不允许使用该材料");
            if (LimitReached(mat)) return Loc.F("本关{0}限量 {1} 根，已用完", (mat.Name), (Level.MaterialLimits[mat.MaterialId]));
            if (a.Id == b.Id) return Loc.T("不能连接同一个节点");
            double len = Vec3d.Distance(a.Position, b.Position);
            if (len < MinMemberLength) return Loc.T("构件长度过短");
            if (len > mat.MaximumLength + 1e-9) return Loc.F("超长 {0:0.0} m（{1}最长 {2:0.#} m）", (len - mat.MaximumLength), (mat.Name), (mat.MaximumLength));
            if (Design.Members.Any(m => m.Connects(a.Id, b.Id))) return Loc.T("两节点之间已有构件");
            if (mat.IsDeck && Math.Abs(b.Position.X - a.Position.X) < 0.3) return Loc.T("桥面不能竖直铺设");
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
            var a = Design.FindNode(startId);
            if (mat.IsDeck && Math.Abs(x - a.Position.X) < 0.3) return BuildResult.Fail(Loc.T("桥面不能竖直铺设"));
            PushUndo();
            var n = new BridgeNode { Id = Design.NextNodeId++, Position = new Vec3d(x, y, 0) };
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
            if (n.IsAnchor) return BuildResult.Fail(Loc.T("固定锚点不能移动"));
            if (n.IsRigidSplit) return BuildResult.Fail(Loc.T("刚性拆分点不可独立移动或折叠；请移动原构件端点"));
            if (n.PierBase) return BuildResult.Fail(Loc.T("桥墩基础不能移动（可以删除桥墩后重建）"));
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
            if (n.IsAnchor) return BuildResult.Fail(Loc.T("固定锚点不能删除"));
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
            var nodes = new HashSet<int>(nodeIds.Where(id => Design.FindNode(id) is { IsAnchor: false }));
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
            Design.Nodes.RemoveAll(n => !n.IsAnchor && !Design.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id));
            PruneMechanisms();
            Changed?.Invoke();
            return BuildResult.Success(0, Loc.F("已删除 {0} 根构件，退还 ${1:0}", (mems.Count), (refund)));
        }

        public BuildResult DeleteMember(int memberId)
        {
            var m = Design.FindMember(memberId);
            if (m == null) return BuildResult.Fail(Loc.T("构件不存在"));
            var why = CheckDemolition(new[] { m });
            if (why != null) return BuildResult.Fail(why);
            PushUndo();
            ApplyDemolition(new[] { m });
            Design.Members.Remove(m);   // 删除即退还费用（成本由构件列表实时求和）
            // 不保留孤立的普通节点（游戏中节点只能作为构件端点存在）
            Design.Nodes.RemoveAll(n => !n.IsAnchor && (n.Id == m.StartNodeId || n.Id == m.EndNodeId)
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
            g = plan.GroupOf(memberId);
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
