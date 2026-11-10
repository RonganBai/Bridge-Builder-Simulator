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
        public SupportType SupportType { get; set; } = SupportType.None;
        /// <summary>桥墩墩底（基础节点）：固定支座，承载力由地基与基础类型决定。</summary>
        public bool PierBase { get; set; }
        /// <summary>桥墩上的支点（中部 / 顶部）：可以位于水面以下。</summary>
        public bool PierPart { get; set; }
        public FoundationType Foundation { get; set; } = FoundationType.None;

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

        public BridgeMember Clone() => (BridgeMember)MemberwiseClone();
        public bool Connects(int a, int b) => (StartNodeId == a && EndNodeId == b) || (StartNodeId == b && EndNodeId == a);
        public int Other(int nodeId) => nodeId == StartNodeId ? EndNodeId : StartNodeId;
    }

    /// <summary>玩家建造方案。建造与测试状态独立：测试只读取方案的副本，不会修改原始方案。</summary>
    public class BridgeDesign
    {
        public int Version { get; set; } = 1;
        public string LevelId { get; set; }
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

        public BridgeNode FindNode(int id) => Nodes.FirstOrDefault(n => n.Id == id);
        public BridgeMember FindMember(int id) => Members.FirstOrDefault(m => m.Id == id);
        /// <summary>总花费 = 新建构件费用 + 拆除费 + 修复费（旧桥原有构件不计费）。</summary>
        public double TotalCost() => Members.Sum(m => m.Cost) + DemolitionCost + RepairCost
                                     + Nodes.Sum(n => n.PierBase ? FoundationRules.Cost(n.Foundation) : 0);

        public BridgeDesign Clone()
        {
            return new BridgeDesign
            {
                Version = Version, LevelId = LevelId, NextNodeId = NextNodeId, NextMemberId = NextMemberId,
                SavedTotalCost = SavedTotalCost, DemolishedCount = DemolishedCount, DemolitionCost = DemolitionCost, RepairCost = RepairCost,
                Nodes = Nodes.Select(n => n.Clone()).ToList(),
                Members = Members.Select(m => m.Clone()).ToList(),
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
