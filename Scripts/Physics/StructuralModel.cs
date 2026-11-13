using System;
using System.Collections.Generic;
using System.Linq;
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

        public readonly List<(int node,int a,int b,double fraction,int[] members)> RigidSplits = new();
        public readonly HashSet<int> SplitJoints = new();
        public int MemberCount;
        public readonly List<(int[] nodes,int pivot,int[] members)> RigidBodies = new();
        public int[] MemberIds;
        public int[] A, B;
        public double[] Length, Cos, Sin, EA, EI, Mass;
        /// <summary>两端铰接（只传轴力）：钢索、液压杆。其余构件为刚接梁单元。</summary>
        public bool[] Pinned;
        public MaterialDefinition[] Mat;
        public bool[] Broken;
        public bool[] Slack;      // 钢索松弛（不参与刚度）
        /// <summary>Moment release (hinge) at end A / end B of a rigid member, e.g. a bascule leaf at its pivot.</summary>
        public bool[] ReleaseA, ReleaseB;
        /// <summary>Locked joint: node i shares the translations of node Alias[i] (-1 = none).</summary>
        public int[] Alias;
        /// <summary>The alias also shares the rotation (rigid seam: the deck stays continuous while locked).</summary>
        public bool[] AliasRot;
        /// <summary>Node positions as designed (mechanism motion changes X / Y during a test).</summary>
        public double[] X0, Y0;
        public readonly Dictionary<int, int> MemberIndex = new();

        public static StructuralModel Build(BridgeDesign design, LevelDefinition level, GameDatabase db)
        {
            design=HydraulicDeckJoints.Expand(design,db);
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
            m.ReleaseA = new bool[c]; m.ReleaseB = new bool[c];
            m.Alias = new int[n]; m.AliasRot = new bool[n];
            for (int i = 0; i < n; i++) m.Alias[i] = -1;
            m.X0 = (double[])m.X.Clone(); m.Y0 = (double[])m.Y.Clone();
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
                if(design.Nodes[a].IsSplitJoint){m.SplitJoints.Add(a);m.ReleaseA[k]=true;}
                if(design.Nodes[b].IsSplitJoint){m.SplitJoints.Add(b);m.ReleaseB[k]=true;}
            }

            foreach(var node in design.Nodes)
                if(node.IsRigidSplit && m.NodeIndex.TryGetValue(node.RigidEndA,out int ra) && m.NodeIndex.TryGetValue(node.RigidEndB,out int rb))
                {
                    int ri=m.NodeIndex[node.Id];double dx=m.X[rb]-m.X[ra],dy=m.Y[rb]-m.Y[ra];
                    double t=((m.X[ri]-m.X[ra])*dx+(m.Y[ri]-m.Y[ra])*dy)/(dx*dx+dy*dy);
                    var spanNodes=new HashSet<int>{ra,rb};
                    foreach(var part in design.Nodes)
                        if(part.IsRigidSplit && part.RigidEndA==node.RigidEndA && part.RigidEndB==node.RigidEndB)spanNodes.Add(m.NodeIndex[part.Id]);
                    var edges=new List<int>();for(int k=0;k<m.MemberCount;k++)
                        if(!m.Broken[k] && spanNodes.Contains(m.A[k]) && spanNodes.Contains(m.B[k]) && !m.Mat[k].Extendable)edges.Add(k);
                    var reached=new HashSet<int>{ra};bool changed;
                    do{changed=false;foreach(int k in edges){if(reached.Contains(m.A[k]) && reached.Add(m.B[k]))changed=true;if(reached.Contains(m.B[k]) && reached.Add(m.A[k]))changed=true;}}while(changed);
                    if(reached.Contains(rb) && reached.Contains(ri))m.RigidSplits.Add((ri,ra,rb,t,edges.ToArray()));
                }
            // 立柱：倾角过大则倒塌；否则基础端成为支座（混凝土柱落地端固接，钢筋混凝土柱的锚点端改为刚接）
            m.FixRot = new bool[n];
            m.IsFoundation = new bool[n];
            m.PierFoundation = new FoundationType[n];
            m.ScourZone = new bool[n];
            m.TunnelOf = new int[n];
            // 桥墩墩底：固定支座（约束 X、Y 与转角），承载力取决于地基（软土 / 岩石）与基础类型（浅基础 / 桩基础）
            for (int i = 0; i < n; i++)
            {
                var node = design.Nodes[i];
                if (!node.PierBase) continue;
                var zone = level.Terrain.ZoneAt(node.Position.X);
                m.FixX[i] = true; m.FixY[i] = true; m.FixRot[i] = true;
                m.IsAnchor[i] = true;
                m.AnchorIds[i] = Loc.F("桥墩 #{0} 的{1}（{2}）", (node.Id), (FoundationRules.Name(node.Foundation)), (zone.Name));
                m.AnchorMaxLoad[i] = FoundationRules.Capacity(zone, node.Foundation);
                m.PierFoundation[i] = node.Foundation;
                m.ScourZone[i] = zone.Scour;
            }
            // Underwater tunnel support points (element joints): fixed like a pier foundation; capacity from the
            // weaker of the two joined elements and the soil
            var tgeo = TunnelGeometry.Build(design, db);
            for (int i = 0; i < n && tgeo != null; i++)
            {
                var node = design.Nodes[i];
                if (!node.IsTunnelSupport || node.TunnelJoint >= tgeo.Count) continue;
                int j = node.TunnelJoint;
                var grade = tgeo.Grade[j - 1].SupportLoad <= tgeo.Grade[j].SupportLoad ? tgeo.Grade[j - 1] : tgeo.Grade[j];
                var zone = level.Terrain.ZoneAt(node.Position.X);
                m.FixX[i] = true; m.FixY[i] = true; m.FixRot[i] = true;
                m.IsAnchor[i] = true;
                m.AnchorIds[i] = Loc.F("隧道支点 #{0}（{1}，{2}）", (node.Id), (grade.Name), (zone.Name));
                m.AnchorMaxLoad[i] = TunnelRules.SupportCapacity(db.TunnelConfig, grade, zone);
                m.TunnelOf[i] = j;
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
            // All lockable seams begin closed, including static previews that do not run a controller.
            foreach(var joint in design.Mechanisms?.Locks ?? new List<DeckLockData>())
                if(m.NodeIndex.TryGetValue(joint.NodeA,out int a) && m.NodeIndex.TryGetValue(joint.NodeB,out int b) &&
                   Math.Abs(m.X[a]-m.X[b])<=BasculeSystem.AlignTolerance && Math.Abs(m.Y[a]-m.Y[b])<=BasculeSystem.AlignTolerance)
                {m.Alias[b]=a;m.AliasRot[b]=true;}
            return m;
        }

        /// <summary>
        /// Splits node i into two coincident end points: the given members move to the new node. Used for deck
        /// seams that a movable bridge releases. The new node's id is -NodeIds[i]. Returns the new index.
        /// </summary>
        public int SplitNode(int i, IEnumerable<int> memberIndices)
        {
            int j = NodeCount++;
            T[] Grow<T>(T[] a, T v) { var r = new T[NodeCount]; Array.Copy(a, r, a.Length); r[j] = v; return r; }
            NodeIds = Grow(NodeIds, -NodeIds[i]);
            X = Grow(X, X[i]); Y = Grow(Y, Y[i]); X0 = Grow(X0, X0[i]); Y0 = Grow(Y0, Y0[i]);
            FixX = Grow(FixX, false); FixY = Grow(FixY, false); IsAnchor = Grow(IsAnchor, false);
            AnchorMaxLoad = Grow(AnchorMaxLoad, 0.0); AnchorIds = Grow(AnchorIds, (string)null);
            Alias = Grow(Alias, -1); AliasRot = Grow(AliasRot, false);
            FixRot = Grow(FixRot, false); IsFoundation = Grow(IsFoundation, false);
            PierFoundation = Grow(PierFoundation, FoundationType.None); ScourZone = Grow(ScourZone, false); TunnelOf = Grow(TunnelOf, 0);
            NodeIndex[-NodeIds[i]] = j;
            foreach (int k in memberIndices)
            {
                if (A[k] == i) A[k] = j;
                if (B[k] == i) B[k] = j;
            }
            return j;
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
        /// <summary>Tunnel section id of a tunnel support node (0 = none).</summary>
        public int[] TunnelOf;

        /// <summary>节点转角被约束（立柱刚接基础）。</summary>
        public bool[] FixRot;
        /// <summary>由混凝土柱在陆地上形成的基础节点。</summary>
        public bool[] IsFoundation;
        /// <summary>因倾角过大而倒塌的立柱（构件下标）。</summary>
        public readonly List<int> TiltCollapsed = new();

        public bool IsActive(int k) => !Broken[k] && !Slack[k];

        /// <summary>Recomputes length / direction / self-weight of member k from the current node positions.</summary>
        public void UpdateMemberGeometry(int k)
        {
            double dx = X[B[k]] - X[A[k]], dy = Y[B[k]] - Y[A[k]];
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return;
            Cos[k] = dx / len; Sin[k] = dy / len;
            Length[k] = len;
            Mass[k] = Mat[k].MassPerMeter * len;
        }
    }
}
