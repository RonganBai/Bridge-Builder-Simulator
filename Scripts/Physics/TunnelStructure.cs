using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    /// <summary>
    /// Immersed and floating tubes as structure. The tube is added to the ordinary structural model as a chain of
    /// beam elements (one per tube element) between its joints; both ends are pinned at the bank lift shafts. Nothing
    /// else holds it: the joints are free nodes, and the player's own members (piers / columns on the river bed,
    /// cables to bed anchors, bank anchors or other structure) are what carries the tube. Loads on the tube:
    /// - net weight per metre: immersed = element mass − displaced water; floating = (tube + ballast) − displaced
    ///   water, where the ballast is sized so that tube + ballast = buoyancy ÷ buoyancy ratio (one consistent
    ///   bookkeeping, nothing is counted twice) — a floating tube pushes up;
    /// - vehicle axles inside the tube, on the element they stand on.
    /// Player members attached to a tube joint are hinged there (support bearings).
    /// Bending stiffness of an element combines the tube body and one joint (joints are much softer); the moment
    /// capacity is the joint's, JointStiffness × the grade's JointRotation. The shell water pressure stays a
    /// separate check in <see cref="TunnelAnalysis"/>. Shield tunnels are bored in the ground and are not modelled here.
    /// Lateral (current) effects are outside this 2D model; TunnelAnalysis keeps a labelled simplified estimate.
    /// </summary>
    public static class TunnelStructure
    {
        /// <summary>Ids of the model-only tube members / end nodes (never in the player's design).</summary>
        public const int IdBase = -800000;
        /// <summary>Allowed gap between a node and the river bed to count as a bed foundation / anchor, m.</summary>
        public const double BedSnap = 0.35;
        public static int MemberId(int element) => IdBase - element;
        public static int ElementOfMemberId(int id) => id <= IdBase && id > IdBase - 10000 ? IdBase - id : -1;

        public static bool IsStructural(TunnelTypeConfig t) => t != null && !t.IsShield;

        /// <summary>Displaced water of element i at the normal water level, kg/m.</summary>
        public static double DisplacedMass(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, int i)
        {
            var t = level.Terrain;
            if (!t.HasWater) return 0;
            double submerged = Math.Clamp(t.WaterLevel - (g.Y0[i] + g.Y1[i]) / 2, 0, g.Type.TubeHeight);
            return cfg.WaterDensity * g.Type.TubeWidth * submerged;
        }

        /// <summary>Total mass of element i per metre (tube + ballast), kg/m.</summary>
        public static double TotalMass(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, int i) =>
            g.Type.IsFloating ? Math.Max(g.Grade[i].MassPerMeter, DisplacedMass(g, level, cfg, i) / Math.Max(1.0001, g.Buoy[i].Ratio)) : g.Grade[i].MassPerMeter;

        /// <summary>Ballast of a floating element, kg/m (0 for immersed tubes).</summary>
        public static double Ballast(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, int i) =>
            g.Type.IsFloating ? Math.Max(0, TotalMass(g, level, cfg, i) - g.Grade[i].MassPerMeter) : 0;

        /// <summary>Net downward mass per metre (weight − buoyancy), kg/m; negative = the element pushes up.</summary>
        public static double NetMass(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, int i) =>
            TotalMass(g, level, cfg, i) - DisplacedMass(g, level, cfg, i);

        /// <summary>Moment a joint of this grade carries before it opens, N·m.</summary>
        public static double JointMomentCapacity(TunnelTypeConfig type, TunnelGradeConfig grade) => type.JointStiffness * grade.JointRotation;

        /// <summary>Effective bending stiffness of one element: tube body and one joint in series.</summary>
        public static double ElementEI(TunnelTypeConfig type, double length)
        {
            double L = Math.Max(0.5, length);
            return L / (L / Math.Max(1, type.TubeEI) + 1 / Math.Max(1, type.JointStiffness));
        }

        private static MaterialDefinition Section(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, int i)
        {
            const double E = 30e9;
            var type = g.Type; var grade = g.Grade[i];
            double A = type.TubeEA / E, I = ElementEI(type, g.Len[i]) / E;
            return new MaterialDefinition
            {
                MaterialId = "tube", Name = Loc.F("管节 #{0}（{1}）", (i + 1), (grade.Name)), ComponentType = ComponentType.Beam,
                ElasticModulus = E, CrossSectionArea = A, SecondMomentOfArea = I, Density = NetMass(g, level, cfg, i) / A,
                // The concrete / steel body is far stronger axially than anything the joints transmit
                TensileStrength = 1.5e8 / A, CompressiveStrength = 9e8 / A, MaximumLength = 1e6,
                BendingCapacity = JointMomentCapacity(type, grade), IsTunnel = true, SideOnly = true, TunnelType = type.Placement,
                Color = grade.Color
            };
        }

        /// <summary>Adds the tube of the design (if it is immersed / floating) to the model built from it.</summary>
        public static void Append(StructuralModel m, BridgeDesign design, LevelDefinition level, GameDatabase db, TunnelGeometry g)
        {
            m.TubeMember = Array.Empty<int>(); m.TubeNode = Array.Empty<int>();
            if (g == null || !IsStructural(g.Type) || g.Count == 0 || db.TunnelConfig == null) return;
            var cfg = db.TunnelConfig;
            int n = g.Count;
            m.TubeNode = new int[n + 1]; m.TubeMember = new int[n];
            for (int j = 0; j <= n; j++)
            {
                double x = j < n ? g.X0[j] : g.X1[n - 1], y = j < n ? g.Y0[j] : g.Y1[n - 1];
                int idx = -1;
                if (j > 0 && j < n)
                {
                    var dn = design.Nodes.FirstOrDefault(q => q.TunnelJoint == j);
                    if (dn != null && m.NodeIndex.TryGetValue(dn.Id, out int di)) idx = di;
                }
                if (idx < 0) idx = AddNode(m, IdBase - j, x, y);
                // The model always follows the tube's own geometry
                m.X[idx] = x; m.Y[idx] = y; m.X0[idx] = x; m.Y0[idx] = y;
                m.TunnelOf[idx] = j;
                if (j == 0 || j == n)
                {
                    // Both ends sit in the bank lift shafts: pinned (bending is free at the shaft wall)
                    m.FixX[idx] = true; m.FixY[idx] = true; m.IsAnchor[idx] = true; m.AnchorMaxLoad[idx] = 0;
                    m.AnchorIds[idx] = j == 0 ? Loc.T("左岸电梯井（隧道端部）") : Loc.T("右岸电梯井（隧道端部）");
                }
                m.TubeNode[j] = idx;
            }
            for (int i = 0; i < n; i++)
                m.TubeMember[i] = AddMember(m, MemberId(i), m.TubeNode[i], m.TubeNode[i + 1], Section(g, level, cfg, i));
            // Player members meet the tube on bearings (hinged): supports carry force, not the tube's joint rotation
            var joints = new HashSet<int>(m.TubeNode);
            for (int k = 0; k < m.MemberCount; k++)
            {
                if (m.Mat[k].IsTunnel) continue;
                if (joints.Contains(m.A[k])) m.ReleaseA[k] = true;
                if (joints.Contains(m.B[k])) m.ReleaseB[k] = true;
            }
        }

        private static T[] Grow<T>(T[] a, int size, T v)
        {
            var r = new T[size]; Array.Copy(a, r, Math.Min(a.Length, size));
            for (int i = a.Length; i < size; i++) r[i] = v;
            return r;
        }

        internal static int AddNode(StructuralModel m, int id, double x, double y)
        {
            int j = m.NodeCount++, s = m.NodeCount;
            m.NodeIds = Grow(m.NodeIds, s, id);
            m.X = Grow(m.X, s, x); m.Y = Grow(m.Y, s, y); m.X0 = Grow(m.X0, s, x); m.Y0 = Grow(m.Y0, s, y);
            m.FixX = Grow(m.FixX, s, false); m.FixY = Grow(m.FixY, s, false); m.IsAnchor = Grow(m.IsAnchor, s, false);
            m.AnchorMaxLoad = Grow(m.AnchorMaxLoad, s, 0.0); m.AnchorIds = Grow(m.AnchorIds, s, (string)null);
            m.Alias = Grow(m.Alias, s, -1); m.AliasRot = Grow(m.AliasRot, s, false);
            m.FixRot = Grow(m.FixRot, s, false); m.IsFoundation = Grow(m.IsFoundation, s, false);
            m.PierFoundation = Grow(m.PierFoundation, s, FoundationType.None); m.ScourZone = Grow(m.ScourZone, s, false); m.TunnelOf = Grow(m.TunnelOf, s, 0); m.NodeLoadY = Grow(m.NodeLoadY, s, 0.0);
            m.NodeIndex[id] = j;
            return j;
        }

        internal static int AddMember(StructuralModel m, int id, int a, int b, MaterialDefinition mat)
        {
            int k = m.MemberCount++, s = m.MemberCount;
            double dx = m.X[b] - m.X[a], dy = m.Y[b] - m.Y[a], len = Math.Sqrt(dx * dx + dy * dy);
            m.MemberIds = Grow(m.MemberIds, s, id); m.A = Grow(m.A, s, a); m.B = Grow(m.B, s, b);
            m.Length = Grow(m.Length, s, len); m.Cos = Grow(m.Cos, s, len > 1e-9 ? dx / len : 1); m.Sin = Grow(m.Sin, s, len > 1e-9 ? dy / len : 0);
            m.EA = Grow(m.EA, s, mat.ElasticModulus * mat.CrossSectionArea); m.EI = Grow(m.EI, s, mat.ElasticModulus * mat.SecondMomentOfArea);
            m.Mass = Grow(m.Mass, s, mat.MassPerMeter * len); m.Pinned = Grow(m.Pinned, s, false);
            m.Prestrain = Grow(m.Prestrain, s, 0.0); m.Condition = Grow(m.Condition, s, 1.0); m.Existing = Grow(m.Existing, s, false);
            m.Mat = Grow(m.Mat, s, mat); m.Broken = Grow(m.Broken, s, len < 1e-6); m.Slack = Grow(m.Slack, s, false);
            m.ReleaseA = Grow(m.ReleaseA, s, false); m.ReleaseB = Grow(m.ReleaseB, s, false);
            m.MemberIndex[id] = k;
            return k;
        }

        /// <summary>Axle force x (N, down) inside the tube as a load on the element it stands on.</summary>
        public static bool AxleOnTube(StructuralModel m, TunnelGeometry g, double x, double force, out AxleLoad axle)
        {
            axle = default;
            if (m.TubeMember.Length == 0 || g == null) return false;
            int i = g.ElementAt(x);
            if (i < 0 || i >= m.TubeMember.Length) return false;
            double span = g.X1[i] - g.X0[i];
            axle = new AxleLoad { MemberIndex = m.TubeMember[i], T = span > 1e-9 ? Math.Clamp((x - g.X0[i]) / span, 0, 1) : 0.5, Force = force };
            return true;
        }

        /// <summary>
        /// Copies the solved tube structure into the tunnel state: joint moment utilization, joint rotation,
        /// deflection, and the support members carrying each joint. Util becomes max(shell pressure, bending).
        /// </summary>
        public static void Merge(StructuralModel m, SolveOutput o, MemberStatus[] status, TunnelGeometry g, TunnelState st, TunnelConfig cfg, LevelDefinition level, bool stable)
        {
            if (g == null || st.Count != g.Count || m.TubeMember.Length != g.Count) return;
            for (int i = 0; i < g.Count; i++)
            {
                int k = m.TubeMember[i];
                bool broken = m.Broken[k];
                st.BendUtil[i] = !stable || broken ? 9.99 : status[k].Utilization;
                st.Moment[i] = broken ? 0 : status[k].Moment;
                st.JointRotationNow[i] = st.Moment[i] / Math.Max(1, g.Type.JointStiffness);
                double dA = stable ? -o.Displacement[2 * m.A[k] + 1] : 0, dB = stable ? -o.Displacement[2 * m.B[k] + 1] : 0;
                st.Settle[i] = (dA + dB) / 2;
                st.Util[i] = Math.Max(st.PressureUtil[i], st.BendUtil[i]);
                st.LoadPath[i] = stable ? LoadPath(m, status, g, i) : Loc.T("结构不稳定");
            }
            st.MaxUtil = 0; st.Worst = -1;
            for (int i = 0; i < g.Count; i++) if (st.Util[i] > st.MaxUtil) { st.MaxUtil = st.Util[i]; st.Worst = i; }
            st.WorstText = st.Worst >= 0 ? TunnelAnalysis.Describe(g, cfg, level, st, st.Worst) : null;
        }

        /// <summary>
        /// Where the element's load goes: net weight / uplift and vehicles → the tube beam → its two joints → the
        /// player's members at each joint (or the bank shaft at the ends), with their forces.
        /// </summary>
        public static string LoadPath(StructuralModel m, MemberStatus[] status, TunnelGeometry g, int i)
        {
            string Joint(int j)
            {
                if (j == 0 || j == g.Count) return Loc.T("岸边电梯井（铰接）");
                var sup = SupportsOf(m, j).Where(k => !m.Broken[k]).ToList();
                if (sup.Count == 0) return Loc.T("没有支撑（只靠管身抗弯）");
                return string.Join("、", sup.Select(k => Loc.F("{0} {1:0.#} 吨{2}", (m.Mat[k].Name), (Math.Abs(status[k].Force) / 9810), (status[k].Mode == MemberMode.Slack ? Loc.T("（松弛）") : status[k].Force >= 0 ? Loc.T("拉") : Loc.T("压")))));
            }
            return Loc.F("左接缝：{0}\n右接缝：{1}", Joint(i), Joint(i + 1));
        }

        /// <summary>Player members that hold tube joint j (model member indices attached to that joint node).</summary>
        public static List<int> SupportsOf(StructuralModel m, int j)
        {
            var r = new List<int>();
            if (j < 0 || j >= m.TubeNode.Length) return r;
            int node = m.TubeNode[j];
            for (int k = 0; k < m.MemberCount; k++)
                if ((m.A[k] == node || m.B[k] == node) && !m.Mat[k].IsTunnel) r.Add(k);
            return r;
        }

        /// <summary>Failure sentence for a tube element whose joints opened (bending), with what to change.</summary>
        public static string BendingFailure(TunnelGeometry g, int i, double moment)
        {
            double cap = JointMomentCapacity(g.Type, g.Grade[i]);
            string fix = g.Type.IsFloating
                ? Loc.T("在接缝上加锚索（钢索 / 复合钢索连到河床锚块或岸上锚点），或换低一档浮力减小上浮力")
                : Loc.T("在这段管节的接缝下面加桥墩或柱子（缩短支撑间距），或升级管节等级");
            return Loc.F("隧道管节 #{0}（{1}）接缝弯矩 {2:0.0} MN·m 超过 {3:0.0} MN·m，接缝张开进水。改进：{4}", (i + 1), (g.Grade[i].Name), (moment / 1e6), (cap / 1e6), fix);
        }
    }
}
