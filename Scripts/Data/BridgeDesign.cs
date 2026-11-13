using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    public enum DamageState { Intact, Broken }

    /// <summary>桥梁节点。锚点节点由关卡生成，玩家不可移动或删除。</summary>
    public class BridgeNode
    {
        public int Id { get; set; }
        public Vec3d Position { get; set; }
        public bool IsAnchor { get; set; }
        public string AnchorId { get; set; }
        /// <summary>Legacy split provenance: an ordinary-looking connection with independent member-end rotations.</summary>
        public bool IsSplitJoint { get; set; }
        public int RigidEndA { get; set; }
        public int RigidEndB { get; set; }
        public double RigidFraction { get; set; } = 0.5;
        public bool IsRigidSplit => RigidEndA != 0 && RigidEndB != 0;
        public SupportType SupportType { get; set; } = SupportType.None;
        /// <summary>桥墩墩底（基础节点）：固定支座，承载力由地基与基础类型决定。</summary>
        public bool PierBase { get; set; }
        /// <summary>桥墩上的支点（中部 / 顶部）：可以位于水面以下。</summary>
        public bool PierPart { get; set; }
        public FoundationType Foundation { get; set; } = FoundationType.None;
        /// <summary>Control point of the underwater tunnel route (Y = tube bottom). Moved by dragging.</summary>
        public bool TunnelPoint { get; set; }
        /// <summary>Support point on the tunnel roof at element joint TunnelJoint (1-based; 0 = not a support).</summary>
        public int TunnelJoint { get; set; }
        /// <summary>Save version 4 tunnel section id (read only, cleared by TunnelRules.MigrateLegacy).</summary>
        [System.Text.Json.Serialization.JsonPropertyName("TunnelId")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int TunnelLegacyId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool IsTunnelSupport => TunnelJoint > 0;
        /// <summary>Any node owned by the tunnel (control point or support): not an ordinary member end.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool IsTunnelNode => TunnelPoint || TunnelJoint > 0;

        public BridgeNode Clone() => (BridgeNode)MemberwiseClone();
    }

    /// <summary>
    /// 桥梁构件。木梁、钢梁、钢索和道路桥面统一使用该结构，桥面由材料 ComponentType=Deck 区分
    /// （对应需求中的 BridgeDeck：DeckType=MaterialId，Width / LoadLimit 取自材料定义）。
    /// </summary>
    public class BridgeMember
    {
        public int Id { get; set; }
        public int StartNodeId { get; set; }
        public int EndNodeId { get; set; }
        public string MaterialId { get; set; }
        public double Length { get; set; }
        public double Cost { get; set; }
        public DamageState DamageState { get; set; } = DamageState.Intact;
        /// <summary>旧桥原有构件（加固关）：不计费用，节点不能移动，拆除有数量上限。</summary>
        public bool Existing { get; set; }
        /// <summary>构件状况 0~1：老化的旧构件承载力按此比例折减，修复后恢复为 1。</summary>
        public double Condition { get; set; } = 1;
        /// <summary>
        /// Hydraulic target stroke as a signed fraction of the built length (negative = retract). Null keeps the
        /// material default, so old saves behave as before. Used when the hydraulic does not drive a bascule leaf.
        /// </summary>
        public double? Stroke { get; set; }
        /// <summary>Stroke magnitude the hydraulic is priced for (null = none, old saves keep their cost).</summary>
        public double? CostStroke { get; set; }

        /// <summary>Each 100% of hydraulic stroke costs this fraction of the member price extra.</summary>
        public const double StrokeCostRate = 0.8;

        /// <summary>Price including the hydraulic stroke surcharge (Cost is the length price).</summary>
        public double EffectiveCost() => Cost * (1 + StrokeCostRate * System.Math.Abs(CostStroke ?? 0));

        public BridgeMember Clone() => (BridgeMember)MemberwiseClone();
        public bool Connects(int a, int b) => (StartNodeId == a && EndNodeId == b) || (StartNodeId == b && EndNodeId == a);
        public int Other(int nodeId) => nodeId == StartNodeId ? EndNodeId : StartNodeId;
    }

    /// <summary>
    /// A bascule leaf: a rigid group of members that rotates about one pivot node, driven by one hydraulic member.
    /// The leaf nodes/members are derived from the structure (see BasculeAnalyzer), so only the pivot and the
    /// actuator are stored. Angles are in degrees; positive opening always raises the leaf tip.
    /// </summary>
    public class BasculeLeafData
    {
        public int Id { get; set; }
        public int PivotNodeId { get; set; }
        public int ActuatorMemberId { get; set; }
        public double OpenAngleDeg { get; set; } = 70;
        public double SpeedDegPerSec { get; set; } = 8;
        public BasculeLeafData Clone() => (BasculeLeafData)MemberwiseClone();
    }

    /// <summary>
    /// A lockable deck joint: two coincident nodes (one on each side). When locked the two nodes share their
    /// translations (a pin joint); when unlocked they are independent so the leaves can separate.
    /// </summary>
    public class DeckLockData
    {
        public int Id { get; set; }
        public int NodeA { get; set; }
        public int NodeB { get; set; }
        public DeckLockData Clone() => (DeckLockData)MemberwiseClone();
    }

    /// <summary>One independently selectable length-driven actuator; ratio is relative to built length.</summary>
    public class LinkageDriverData
    {
        public int MemberId { get; set; }
        public const double StrokeFraction = 0.5;
        /// <summary>Player-adjustable stroke range (fraction of built length), always below 100%.</summary>
        public const double MinStroke = 0.05, MaxStroke = 0.95;
        public double Ratio { get; set; } = -StrokeFraction;
        public LinkageDriverData Clone() => (LinkageDriverData)MemberwiseClone();
    }

    /// <summary>Opt-in planar pin-jointed linkage settings. Empty drivers preserve legacy rigid frames.</summary>
    public class LinkageData
    {
        public double Duration { get; set; } = 4;
        public List<LinkageDriverData> Drivers { get; set; } = new();
        public LinkageData Clone() => new() { Duration=Duration, Drivers=Drivers.Select(d=>d.Clone()).ToList() };
    }

    /// <summary>Movable-bridge data saved with the design (empty for fixed bridges and for version-1 saves).</summary>
    public class MechanismData
    {
        public List<BasculeLeafData> Leaves { get; set; } = new();
        public List<DeckLockData> Locks { get; set; } = new();
        public LinkageData Linkage { get; set; } = new();
        public int NextId { get; set; } = 1;
        [System.Text.Json.Serialization.JsonIgnore] public bool IsEmpty => Leaves.Count == 0 && Locks.Count == 0 && (Linkage?.Drivers.Count ?? 0) == 0;
        public MechanismData Clone() => new()
        {
            Leaves = Leaves.Select(l => l.Clone()).ToList(), Locks = Locks.Select(l => l.Clone()).ToList(), NextId = NextId, Linkage=(Linkage ?? new LinkageData()).Clone(),
        };
        public DeckLockData LockOf(int nodeId) => Locks.FirstOrDefault(l => l.NodeA == nodeId || l.NodeB == nodeId);
    }

    /// <summary>玩家建造方案。建造与测试状态独立：测试只读取方案的副本，不会修改原始方案。</summary>
    public class BridgeDesign : IVersionStamped
    {
        /// <summary>Game version that last saved this design (null = saved before versioning, i.e. 1.0.0).
        /// Separate from Version, which is the save format.</summary>
        public string GameVersion { get; set; }
        /// <summary>Save format version. 1 = no mechanisms; 2 = bascule settings; 3 = optional pin-jointed linkage drivers;
        /// 4 = underwater tunnel sections; 5 = tunnel route with graded elements (version 4 migrates).</summary>
        public const int CurrentVersion = 5;
        public int Version { get; set; } = CurrentVersion;
        public string LevelId { get; set; }
        /// <summary>Player-given design name (named saves); empty for the auto save.</summary>
        public string Name { get; set; }
        /// <summary>Optional engineering contract (scoring.json Contracts id); null = no contract. Old saves have none.</summary>
        public string Contract { get; set; }
        public List<BridgeNode> Nodes { get; set; } = new();
        public List<BridgeMember> Members { get; set; } = new();
        public int NextNodeId { get; set; } = 1;
        public int NextMemberId { get; set; } = 1;
        /// <summary>保存时记录的已用预算，加载后会按构件重新计算校验。</summary>
        public double SavedTotalCost { get; set; }
        /// <summary>已拆除的旧构件数量与拆除费用（加固关）。</summary>
        public int DemolishedCount { get; set; }
        public double DemolitionCost { get; set; }
        /// <summary>修复旧构件的费用。</summary>
        public double RepairCost { get; set; }
        /// <summary>Movable-bridge mechanisms. Never null after loading (see EnsureMechanisms).</summary>
        public MechanismData Mechanisms { get; set; } = new();
        public MechanismData EnsureMechanisms() => Mechanisms ??= new MechanismData();
        /// <summary>Underwater tunnel sections. Never null after loading.</summary>
        /// <summary>The underwater tunnel (null = none).</summary>
        public TunnelRoute Tunnel { get; set; }
        /// <summary>Save version 4 tunnel sections (read only, migrated on load).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<TunnelSegment> Tunnels { get; set; }

        public BridgeNode FindNode(int id) => Nodes.FirstOrDefault(n => n.Id == id);
        public BridgeMember FindMember(int id) => Members.FirstOrDefault(m => m.Id == id);
        /// <summary>总花费 = 新建构件费用 + 拆除费 + 修复费（旧桥原有构件不计费）。</summary>
        public double TotalCost() => Members.Sum(m => m.EffectiveCost()) + DemolitionCost + RepairCost
                                     + Nodes.Sum(n => n.PierBase ? FoundationRules.Cost(n.Foundation) : 0)
                                     + (Tunnel?.Cost ?? 0);

        public BridgeDesign Clone()
        {
            return new BridgeDesign
            {
                Version = Version, LevelId = LevelId, NextNodeId = NextNodeId, NextMemberId = NextMemberId,
                SavedTotalCost = SavedTotalCost, DemolishedCount = DemolishedCount, DemolitionCost = DemolitionCost, RepairCost = RepairCost,
                Nodes = Nodes.Select(n => n.Clone()).ToList(),
                Members = Members.Select(m => m.Clone()).ToList(),
                Mechanisms = (Mechanisms ?? new MechanismData()).Clone(),
                Tunnel = Tunnel?.Clone(),
                Name = Name, Contract = Contract,
            };
        }

        /// <summary>根据关卡创建只含锚点的空方案。</summary>
        public static BridgeDesign CreateForLevel(LevelDefinition level)
        {
            var d = new BridgeDesign { LevelId = level.Id };
            foreach (var a in level.Anchors)
            {
                if (!a.Enabled) continue;
                d.Nodes.Add(new BridgeNode { Id = d.NextNodeId++, Position = a.Position, IsAnchor = true, AnchorId = a.Id, SupportType = a.SupportType });
            }
            return d;
        }
    }
}
