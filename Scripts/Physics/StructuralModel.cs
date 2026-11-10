using System;
using System.Collections.Generic;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    /// <summary>
    /// 求解器使用的结构模型（数组化，避免每帧分配）。由 BridgeDesign 构建，测试期间只修改断裂/松弛状态，
    /// 不会回写玩家的原始方案。
    /// 坐标：X 沿桥向，Y 竖直向上（m）。力：N。
    /// </summary>
    public class StructuralModel
    {
        public const double Gravity = 9.81; // m/s²

        public int NodeCount;
        public int[] NodeIds;
        public double[] X, Y;
        public bool[] FixX, FixY;
        public bool[] IsAnchor;
        public double[] AnchorMaxLoad;
        public string[] AnchorIds;
        public readonly Dictionary<int, int> NodeIndex = new();

        public int MemberCount;
        public int[] MemberIds;
        public int[] A, B;
        public double[] Length, Cos, Sin, EA, EI, Mass;
        /// <summary>两端铰接（只传轴力）：钢索、液压杆。其余构件为刚接梁单元。</summary>
        public bool[] Pinned;
        public MaterialDefinition[] Mat;
        public bool[] Broken;
        public bool[] Slack;      // 钢索松弛（不参与刚度）
        public readonly Dictionary<int, int> MemberIndex = new();

        public static StructuralModel Build(BridgeDesign design, LevelDefinition level, GameDatabase db)
        {
            var m = new StructuralModel();
            int n = design.Nodes.Count;
            m.NodeCount = n;
            m.NodeIds = new int[n]; m.X = new double[n]; m.Y = new double[n];
            m.FixX = new bool[n]; m.FixY = new bool[n]; m.IsAnchor = new bool[n];
            m.AnchorMaxLoad = new double[n]; m.AnchorIds = new string[n];
            for (int i = 0; i < n; i++)
            {
                var node = design.Nodes[i];
                m.NodeIds[i] = node.Id; m.X[i] = node.Position.X; m.Y[i] = node.Position.Y;
                m.NodeIndex[node.Id] = i;
                if (node.IsAnchor)
                {
                    AnchorDefinition def = null;
                    foreach (var a in level.Anchors) if (a.Id == node.AnchorId) def = a;
                    // 只有关卡中启用的锚点才能提供支撑，普通节点永远不能成为支座
                    if (def != null && def.Enabled)
                    {
                        m.IsAnchor[i] = true;
                        m.AnchorIds[i] = def.Id;
                        m.AnchorMaxLoad[i] = def.MaxLoad;
                        m.FixY[i] = def.SupportType == SupportType.Pinned || def.SupportType == SupportType.RollerX;
                        m.FixX[i] = def.SupportType == SupportType.Pinned;
                    }
                }
            }

            var list = new List<BridgeMember>();
            foreach (var mem in design.Members)
                if (m.NodeIndex.ContainsKey(mem.StartNodeId) && m.NodeIndex.ContainsKey(mem.EndNodeId) && db.GetMaterial(mem.MaterialId) != null)
                    list.Add(mem);
            int c = list.Count;
            m.MemberCount = c;
            m.MemberIds = new int[c]; m.A = new int[c]; m.B = new int[c];
            m.Length = new double[c]; m.Cos = new double[c]; m.Sin = new double[c]; m.EA = new double[c]; m.EI = new double[c]; m.Mass = new double[c];
            m.Pinned = new bool[c];
            m.Prestrain = new double[c];
            m.Condition = new double[c];
            m.Existing = new bool[c];
            m.Mat = new MaterialDefinition[c]; m.Broken = new bool[c]; m.Slack = new bool[c];
            for (int k = 0; k < c; k++)
            {
                var mem = list[k];
                var mat = db.GetMaterial(mem.MaterialId);
                int a = m.NodeIndex[mem.StartNodeId], b = m.NodeIndex[mem.EndNodeId];
                double dx = m.X[b] - m.X[a], dy = m.Y[b] - m.Y[a];
                double len = Math.Sqrt(dx * dx + dy * dy);
                m.MemberIds[k] = mem.Id; m.A[k] = a; m.B[k] = b; m.Mat[k] = mat;
                m.Length[k] = len;
                m.Cos[k] = len > 1e-9 ? dx / len : 1; m.Sin[k] = len > 1e-9 ? dy / len : 0;
                m.EA[k] = mat.ElasticModulus * mat.CrossSectionArea;
                m.EI[k] = mat.ElasticModulus * mat.SecondMomentOfArea;
                m.Pinned[k] = mat.IsCable || mat.PinnedEnds || m.EI[k] <= 0;
                m.Mass[k] = mat.MassPerMeter * len;
                m.Broken[k] = mem.DamageState == DamageState.Broken || len < 1e-6;
                m.Condition[k] = Math.Clamp(mem.Condition, 0.05, 1);
                m.Existing[k] = mem.Existing;
                m.MemberIndex[mem.Id] = k;
            }

            // 立柱：倾角过大则倒塌；否则基础端成为支座（混凝土柱落地端固接，钢筋混凝土柱的锚点端改为刚接）
            m.FixRot = new bool[n];
            m.IsFoundation = new bool[n];
            m.PierFoundation = new FoundationType[n];
            m.ScourZone = new bool[n];
            // 桥墩墩底：固定支座（约束 X、Y 与转角），承载力取决于地基（软土 / 岩石）与基础类型（浅基础 / 桩基础）
            for (int i = 0; i < n; i++)
            {
                var node = design.Nodes[i];
                if (!node.PierBase) continue;
                var zone = level.Terrain.ZoneAt(node.Position.X);
                m.FixX[i] = true; m.FixY[i] = true; m.FixRot[i] = true;
                m.IsAnchor[i] = true;
                m.AnchorIds[i] = $"桥墩 #{node.Id} 的{FoundationRules.Name(node.Foundation)}（{zone.Name}）";
                m.AnchorMaxLoad[i] = FoundationRules.Capacity(zone, node.Foundation);
                m.PierFoundation[i] = node.Foundation;
                m.ScourZone[i] = zone.Scour;
            }
            for (int k = 0; k < c; k++)
            {
                var mat = m.Mat[k];
                if (!mat.IsColumn || m.Broken[k]) continue;
                var na = design.FindNode(list[k].StartNodeId); var nb = design.FindNode(list[k].EndNodeId);
                if (ColumnRules.TiltExceeded(mat, na.Position, nb.Position))
                {
                    m.Broken[k] = true; m.TiltCollapsed.Add(k);
                    continue;
                }
                bool aBase = ColumnRules.FindBase(mat, na, nb, level, out var why);
                if (why != null) continue;   // 不满足基础要求（例如存档被修改）：不提供支撑
                int bi = aBase ? m.A[k] : m.B[k];
                if (mat.BaseRule == BaseRule.Land && !m.IsAnchor[bi])
                {
                    m.FixX[bi] = true; m.FixY[bi] = true; m.FixRot[bi] = true; m.IsFoundation[bi] = true;
                }
                else if (mat.BaseRule != BaseRule.None) m.FixRot[bi] = true;
            }
            return m;
        }

        /// <summary>构件强制伸缩量 m（液压杆收缩为负）。轴力 N = EA/L·(伸长 − Prestrain)。</summary>
        public double[] Prestrain;
        /// <summary>构件状况（老化折减，1 = 全新）。承载力按此比例折减。</summary>
        public double[] Condition;
        public bool[] Existing;

        public bool HasPrestrain
        {
            get { foreach (var d in Prestrain) if (d != 0) return true; return false; }
        }

        /// <summary>桥墩墩底的基础类型（非墩底为 None）。</summary>
        public FoundationType[] PierFoundation;
        /// <summary>墩底位于冲刷区（洪水时浅基础会被掏空）。</summary>
        public bool[] ScourZone;

        /// <summary>节点转角被约束（立柱刚接基础）。</summary>
        public bool[] FixRot;
        /// <summary>由混凝土柱在陆地上形成的基础节点。</summary>
        public bool[] IsFoundation;
        /// <summary>因倾角过大而倒塌的立柱（构件下标）。</summary>
        public readonly List<int> TiltCollapsed = new();

        public bool IsActive(int k) => !Broken[k] && !Slack[k];
    }
}
