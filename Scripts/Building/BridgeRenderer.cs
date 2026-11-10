using System;
using System.Collections.Generic;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.World;
using Godot;

namespace BridgeArchitect3D.Building
{
    /// <summary>
    /// 桥梁 3D 显示：节点（BridgeNode 视觉）、构件（BridgeMember 视觉）、桥面（RoadDeck 视觉）。
    /// 结构变化时整体重建；测试模式下只更新变换与材质引用，不产生新对象。
    /// 桁架在桥面两侧各显示一片（±TrussOffset），节点在两片之间以横梁相连。
    /// </summary>
    public partial class BridgeRenderer : Node3D
    {
        public const float DeckSurfaceOffset = 0.2f;  // 桥面顶面相对节点的高度（与引道路面齐平）

        private class NodeVisual
        {
            public int Id; public bool Anchor;
            public MeshInstance3D Front, Back, Tie;
            public Vector3 Base;
        }

        private class MemberVisual
        {
            public int Id; public int A, B;
            public MaterialDefinition Mat;
            public MeshInstance3D[] Parts;
            /// <summary>部件的固定材质（液压杆内杆镀铬、桥面标线）；为 null 的部件随状态着色。</summary>
            public Material[] Special;
            public float RestLength;
            /// <summary>预应力梁上拱程度 0~1（1 = 空载时的最大上拱，加载后趋平）。</summary>
            public float Bow = 1;
            /// <summary>旧桥锈蚀 / 老化的原有构件（显示为风化外观）。</summary>
            public bool Aged;
            public bool Hidden;
        }

        private MaterialManager _mm;
        private readonly Dictionary<int, NodeVisual> _nodes = new();
        private readonly Dictionary<int, MemberVisual> _members = new();
        private readonly List<MemberVisual> _memberList = new();
        private readonly List<NodeVisual> _nodeList = new();
        private SphereMesh _nodeMesh;
        private BoxMesh _anchorMesh, _unitBox;
        private Node3D _container;

        public void Init(MaterialManager mm)
        {
            _mm = mm;
            _nodeMesh = new SphereMesh { Radius = 0.22f, Height = 0.44f, RadialSegments = 12, Rings = 6 };
            _anchorMesh = new BoxMesh { Size = new Vector3(0.7f, 0.7f, 0.7f) };
            _unitBox = new BoxMesh { Size = Vector3.One };
        }

        /// <summary>根据方案重建所有视觉对象。</summary>
        public void Rebuild(BridgeDesign design)
        {
            _container?.QueueFree();
            _container = new Node3D { Name = "BridgeVisuals" };
            AddChild(_container);
            _nodes.Clear(); _members.Clear(); _memberList.Clear(); _nodeList.Clear();

            foreach (var n in design.Nodes)
            {
                var v = new NodeVisual { Id = n.Id, Anchor = n.IsAnchor, Base = ToV(n.Position) };
                var mesh = n.IsAnchor ? (Mesh)_anchorMesh : _nodeMesh;
                var mat = n.IsAnchor ? _mm.Anchor : _mm.Node;
                v.Front = NewMesh(mesh, mat); v.Back = NewMesh(mesh, mat);
                v.Tie = NewMesh(_unitBox, _mm.CrossTie);
                _nodes[n.Id] = v; _nodeList.Add(v);
                if (n.PierBase) AddFoundation(n);
            }
            foreach (var m in design.Members)
            {
                var mat = _mm.Db.GetMaterial(m.MaterialId);
                if (mat == null || !_nodes.ContainsKey(m.StartNodeId) || !_nodes.ContainsKey(m.EndNodeId)) continue;
                var v = new MemberVisual { Id = m.Id, A = m.StartNodeId, B = m.EndNodeId, Mat = mat, RestLength = (float)m.Length, Aged = m.Existing && m.Condition < 0.999 };
                var sm = _mm.ForMaterial(mat.MaterialId);
                // 桥面：路面 + 中线标线；立柱 / 梁：两侧桁架面各 1 根；液压杆：两侧各 外筒 + 内杆（双节套筒）
                // 预应力梁：每侧 3 段折线以显示上拱
                int count = mat.IsDeck ? 2 : mat.Extendable ? 4 : mat.PrestressLoad > 0 ? 6 : 2;
                v.Parts = new MeshInstance3D[count];
                v.Special = new Material[count];
                for (int i = 0; i < count; i++)
                {
                    if (mat.Extendable && i % 2 == 1) v.Special[i] = _mm.Chrome;
                    if (mat.IsDeck && i == 1) v.Special[i] = _mm.LaneMark;
                    v.Parts[i] = NewMesh(_unitBox, v.Special[i] ?? sm);
                }
                _members[m.Id] = v; _memberList.Add(v);
            }
            UpdateTransforms(null, null, 0);
        }

        /// <summary>桥墩基础外观：浅基础为扩大底板；桩基础为承台 + 4 根深入河床的桩。</summary>
        private void AddFoundation(BridgeNode n)
        {
            var p = ToV(n.Position);
            var pad = NewMesh(new BoxMesh { Size = new Vector3(3.2f, 0.6f, 5.6f) }, _mm.Foundation);
            pad.Position = p + new Vector3(0, -0.3f, 0);
            if (n.Foundation != FoundationType.Pile) return;
            var pile = new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.25f, Height = 6f, RadialSegments = 8 };
            foreach (var dx in new[] { -1.0f, 1.0f })
                foreach (var dz in new[] { -2.0f, 2.0f })
                    NewMesh(pile, _mm.Foundation).Position = p + new Vector3(dx, -3.6f, dz);
        }

        private MeshInstance3D NewMesh(Mesh mesh, Material mat)
        {
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
            _container.AddChild(mi);
            return mi;
        }

        public static Vector3 ToV(Vec3d p) => new((float)p.X, (float)p.Y, 0);

        /// <summary>
        /// 更新所有变换。displacement 为结构求解的真实位移，scale 为视觉放大系数（只影响显示）。
        /// </summary>
        public void UpdateTransforms(StructuralModel model, double[] displacement, float scale)
        {
            foreach (var v in _nodeList)
            {
                var p = NodePos(v, model, displacement, scale);
                float zf = BuildPlane.TrussOffset;
                v.Front.Position = new Vector3(p.X, p.Y, zf);
                v.Back.Position = new Vector3(p.X, p.Y, -zf);
                v.Tie.Transform = new Transform3D(new Basis(new Vector3(0.12f, 0, 0), new Vector3(0, 0.12f, 0), new Vector3(0, 0, 2 * zf)), p);
            }
            foreach (var m in _memberList)
            {
                var a = NodePos(_nodes[m.A], model, displacement, scale);
                var b = NodePos(_nodes[m.B], model, displacement, scale);
                if (m.Mat.IsDeck)
                {
                    float th = (float)m.Mat.VisualThickness;
                    var off = new Vector3(0, DeckSurfaceOffset - th / 2, 0);
                    SetBeam(m.Parts[0], a + off, b + off, th, (float)m.Mat.DeckWidth);
                    // 路面中线标线（略高于桥面顶面），用来与梁、索区分
                    var dir = (b - a).Normalized();
                    var top = new Vector3(0, DeckSurfaceOffset + 0.01f, 0);
                    SetBeam(m.Parts[1], a + top + dir * 0.3f, b + top - dir * 0.3f, 0.02f, 0.14f);
                }
                else if (m.Mat.Extendable)
                {
                    // 液压杆：外筒（粗，固定长度，从起点出发）+ 内杆（细，从终点伸入外筒），伸缩时内杆露出长度变化
                    float th = (float)m.Mat.VisualThickness;
                    var dir = (b - a).Normalized();
                    float cyl = m.RestLength * 0.55f;
                    for (int side = 0; side < 2; side++)
                    {
                        var zf = new Vector3(0, 0, side == 0 ? BuildPlane.TrussOffset : -BuildPlane.TrussOffset);
                        SetBeam(m.Parts[side * 2], a + zf, a + zf + dir * cyl, th, th);
                        SetBeam(m.Parts[side * 2 + 1], b + zf - dir * cyl, b + zf, th * 0.45f, th * 0.45f);
                    }
                }
                else if (m.Mat.PrestressLoad > 0)
                {
                    // 预应力梁：放置后略微上拱（跨长的 1/120），加载后逐渐趋平
                    float th = (float)m.Mat.VisualThickness;
                    var d = b - a; float len = d.Length();
                    var nrm = len > 1e-4f ? new Vector3(-d.Y, d.X, 0) / len : Vector3.Up;
                    if (nrm.Y < 0) nrm = -nrm;
                    float rise = len / 120f * Mathf.Clamp(m.Bow, 0, 1);
                    for (int side = 0; side < 2; side++)
                    {
                        var zf = new Vector3(0, 0, side == 0 ? BuildPlane.TrussOffset : -BuildPlane.TrussOffset);
                        var p1 = a + d / 3 + nrm * rise * 0.89f; var p2 = a + d * 2 / 3 + nrm * rise * 0.89f;
                        SetBeam(m.Parts[side * 3], a + zf, p1 + zf, th, th * 0.55f);
                        SetBeam(m.Parts[side * 3 + 1], p1 + zf, p2 + zf, th, th * 0.55f);
                        SetBeam(m.Parts[side * 3 + 2], p2 + zf, b + zf, th, th * 0.55f);
                    }
                }
                else if (m.Mat.IsColumn)
                {
                    // 立柱：两侧桁架面各一根粗方柱
                    float th = (float)m.Mat.VisualThickness;
                    var zf = new Vector3(0, 0, BuildPlane.TrussOffset);
                    SetBeam(m.Parts[0], a + zf, b + zf, th, th);
                    SetBeam(m.Parts[1], a - zf, b - zf, th, th);
                }
                else
                {
                    float th = (float)m.Mat.VisualThickness;
                    var zf = new Vector3(0, 0, BuildPlane.TrussOffset);
                    SetBeam(m.Parts[0], a + zf, b + zf, th, th);
                    SetBeam(m.Parts[1], a - zf, b - zf, th, th);
                }
            }
        }

        private static Vector3 NodePos(NodeVisual v, StructuralModel model, double[] u, float scale)
        {
            if (model == null || u == null || !model.NodeIndex.TryGetValue(v.Id, out int i)) return v.Base;
            return v.Base + new Vector3((float)(u[2 * i] * scale), (float)(u[2 * i + 1] * scale), 0);
        }

        /// <summary>把单位立方体变换为从 a 到 b 的长方体（X 轴沿构件，Z 轴为建造平面法向）。</summary>
        public static void SetBeam(MeshInstance3D mi, Vector3 a, Vector3 b, float thickness, float depth)
        {
            var d = b - a;
            float len = d.Length();
            if (len < 1e-4f) { mi.Visible = false; return; }
            var x = d / len;
            var z = Vector3.Back;
            var y = z.Cross(x).Normalized();
            mi.Transform = new Transform3D(new Basis(x * len, y * thickness, z * depth), (a + b) * 0.5f);
        }

        // ---------------- 颜色 ----------------
        /// <summary>建造模式着色：材料颜色 + 选中/悬停高亮；可选叠加静力受力颜色。</summary>
        public void ColorForBuild(ICollection<int> selNodes, ICollection<int> selMembers, int hoverNode, int hoverMember, StructuralModel model, MemberStatus[] status)
        {
            foreach (var v in _nodeList)
            {
                Material mat = selNodes.Contains(v.Id) ? _mm.NodeSelected : v.Id == hoverNode ? _mm.Hover : v.Anchor ? _mm.Anchor : _mm.Node;
                v.Front.MaterialOverride = mat; v.Back.MaterialOverride = mat;
            }
            foreach (var m in _memberList)
            {
                Material mat;
                if (selMembers.Contains(m.Id)) mat = _mm.Selected;
                else if (m.Id == hoverMember) mat = _mm.Hover;
                else if (model != null && status != null && model.MemberIndex.TryGetValue(m.Id, out int k) && k < status.Length)
                {
                    mat = status[k].Mode == MemberMode.Inactive || status[k].Mode == MemberMode.Slack ? _mm.Gray : _mm.ForUtilization(status[k].Utilization);
                    m.Bow = 1 - (float)Math.Min(1, status[k].Utilization);
                    if (m.Mat.PrestressLoad > 0) _bowDirty = true;
                }
                else mat = m.Aged ? _mm.Aged(_mm.ForMaterial(m.Mat.MaterialId)) : _mm.ForMaterial(m.Mat.MaterialId);
                SetMemberMaterial(m, mat);
                SetMemberVisible(m, true);
            }
        }

        private bool _bowDirty;

        /// <summary>建造模式下受力显示改变了预应力梁的上拱程度时，刷新一次几何。</summary>
        public void RefreshBowIfNeeded()
        {
            if (!_bowDirty) return;
            _bowDirty = false;
            UpdateTransforms(null, null, 0);
        }

        /// <summary>测试模式着色。broken / utilization 来自模拟或回放帧。</summary>
        public void ColorForTest(StructuralModel model, bool[] broken, float[] utilization, bool showStress, int selMember)
        {
            foreach (var v in _nodeList)
            {
                Material mat = v.Anchor ? _mm.Anchor : _mm.Node;
                v.Front.MaterialOverride = mat; v.Back.MaterialOverride = mat;
            }
            foreach (var m in _memberList)
            {
                if (!model.MemberIndex.TryGetValue(m.Id, out int k)) continue;
                bool isBroken = broken[k];
                SetMemberVisible(m, !isBroken);
                if (isBroken) continue;
                m.Bow = 1 - Math.Min(1f, utilization[k]);
                Material mat = m.Id == selMember ? _mm.Selected
                    : showStress ? (model.Slack[k] ? _mm.Gray : _mm.ForUtilization(utilization[k]))
                    : m.Aged ? _mm.Aged(_mm.ForMaterial(m.Mat.MaterialId)) : _mm.ForMaterial(m.Mat.MaterialId);
                SetMemberMaterial(m, mat);
            }
        }

        private void SetMemberMaterial(MemberVisual m, Material mat)
        {
            var baseMat = _mm.ForMaterial(m.Mat.MaterialId);
            // 梁 / 立柱在受力着色时使用带轮廓高光的材质，与桥面、钢索一眼区分
            if (mat is StandardMaterial3D sm && mat != baseMat && !m.Mat.IsDeck && !m.Mat.IsCable) mat = _mm.WithRim(sm);
            for (int i = 0; i < m.Parts.Length; i++)
                m.Parts[i].MaterialOverride = m.Special[i] != null && (mat == baseMat || m.Mat.IsDeck) ? m.Special[i] : mat;
        }

        // ---------------- 拾取 ----------------
        /// <summary>
        /// 用鼠标射线直接拾取构件实体（考虑两侧桁架面、桥面宽度、液压杆外筒），返回离相机最近的构件 Id，没有则 -1。
        /// </summary>
        public int PickMember(Vector3 origin, Vector3 dir, float extraRadius = 0.15f)
        {
            int best = -1; float bestT = float.MaxValue;
            foreach (var m in _memberList)
            {
                if (m.Hidden) continue;
                foreach (var p in m.Parts)
                {
                    if (!p.Visible) continue;
                    // 部件为变换后的单位立方体：把射线变换到部件局部空间后做包围盒求交
                    var inv = p.GlobalTransform.AffineInverse();
                    var lo = inv * origin; var ld = inv.Basis * dir;
                    float pad = extraRadius;
                    var sc = p.GlobalTransform.Basis.Scale;
                    var half = new Vector3(0.5f + pad / Mathf.Max(sc.X, 1e-3f), 0.5f + pad / Mathf.Max(sc.Y, 1e-3f), 0.5f + pad / Mathf.Max(sc.Z, 1e-3f));
                    if (RayBox(lo, ld, half, out float t) && t < bestT) { bestT = t; best = m.Id; }
                }
            }
            return best;
        }

        private static bool RayBox(Vector3 o, Vector3 d, Vector3 h, out float tHit)
        {
            float t0 = -1e9f, t1 = 1e9f; tHit = 0;
            for (int i = 0; i < 3; i++)
            {
                float oi = o[i], di = d[i];
                if (Mathf.Abs(di) < 1e-8f) { if (oi < -h[i] || oi > h[i]) return false; continue; }
                float a = (-h[i] - oi) / di, b = (h[i] - oi) / di;
                if (a > b) (a, b) = (b, a);
                t0 = Mathf.Max(t0, a); t1 = Mathf.Min(t1, b);
                if (t0 > t1) return false;
            }
            if (t1 < 0) return false;
            tHit = t0 >= 0 ? t0 : t1;
            return true;
        }

        /// <summary>每根构件的中点（世界坐标，用于框选）。</summary>
        public IEnumerable<(int id, Vector3 mid)> MemberMidpoints()
        {
            foreach (var m in _memberList)
                if (!m.Hidden && _nodes.TryGetValue(m.A, out var a) && _nodes.TryGetValue(m.B, out var b))
                    yield return (m.Id, (a.Front.Position + b.Front.Position) * 0.5f);
        }

        /// <summary>射线拾取节点（两侧节点球都可点中），返回离相机最近者。</summary>
        public int PickNode(Vector3 origin, Vector3 dir, float radius = 0.4f)
        {
            int best = -1; float bestT = float.MaxValue;
            foreach (var v in _nodeList)
            {
                foreach (var c in new[] { v.Front.Position, v.Back.Position })
                {
                    float t = (c - origin).Dot(dir);
                    if (t < 0) continue;
                    if ((origin + dir * t - c).Length() <= radius && t < bestT) { bestT = t; best = v.Id; }
                }
            }
            return best;
        }

        public IEnumerable<(int id, Vector3 pos)> NodePositions()
        {
            foreach (var v in _nodeList) yield return (v.Id, v.Front.Position);
        }

        private static void SetMemberVisible(MemberVisual m, bool visible)
        {
            if (m.Hidden == !visible) return;
            m.Hidden = !visible;
            foreach (var p in m.Parts) p.Visible = visible;
        }

        /// <summary>取得构件各部件的当前变换（用于生成断裂碎片）。</summary>
        public IEnumerable<(Transform3D xf, Material mat)> MemberParts(int memberId)
        {
            if (!_members.TryGetValue(memberId, out var m)) yield break;
            foreach (var p in m.Parts) yield return (p.GlobalTransform, _mm.ForMaterial(m.Mat.MaterialId));
        }

        public void SetVisibleAll(bool v) { if (_container != null) _container.Visible = v; }
    }
}
