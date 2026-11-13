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
            public Material BaseMaterial;
            public MeshInstance3D Front, Back, Tie;
            /// <summary>Second end of a released deck seam (model node -Id); shown only while the seam is split.</summary>
            public MeshInstance3D TwinFront, TwinBack;
            public Vector3 Base;
        }

        private class MemberVisual
        {
            public int Id; public int A, B;
            public MaterialDefinition Mat;
            /// <summary>Material last applied to the parts (skips re-assigning the same material every frame).</summary>
            public Material Applied;
            public float Barrel = 0.5f;   // hydraulic cylinder length / built length
            public MeshInstance3D[] Parts;
            /// <summary>部件的固定材质（液压杆内杆镀铬、桥面标线）；为 null 的部件随状态着色。</summary>
            public Material[] Special;
            public float RestLength;
            /// <summary>预应力梁上拱程度 0~1（1 = 空载时的最大上拱，加载后趋平）。</summary>
            public float Bow = 1;
            /// <summary>旧桥锈蚀 / 老化的原有构件（显示为风化外观）。</summary>
            public bool Aged;
            public bool Hidden;
            /// <summary>Cross beam between the truss planes at the midpoint (members >= 5 m).</summary>
            public bool Cross;
            /// <summary>Cross beam positions (fractions from A to B); the last CrossT.Count parts are the beams.</summary>
            public System.Collections.Generic.List<double> CrossT = new();
            public System.Collections.Generic.List<bool> CrossRed = new();
            /// <summary>Cable self-weight sag as a fraction of the span.</summary>
            public float Sag;
        }

        private const int CableSegments = 6;

        /// <summary>Is this member cross beam above the road and lower than the level height limit?</summary>
        private bool CrossBlocksTraffic(BridgeDesign design, Vec3d a, Vec3d b, double t)
        {
            double mx = a.X + (b.X - a.X) * t, my = a.Y + (b.Y - a.Y) * t;
            var y = HeightClearance.DeckYAt(design, _mm.Db, mx, my);
            if (y == null) return false;
            double h = my - y.Value;
            return h > 0.05 && h < HeightClearance.Limit(Level, _mm.Db);
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
            // Node model radius equals the snap / pick radius
            float nr = (float)DesignEditor.NodeSnapRadius;
            _nodeMesh = new SphereMesh { Radius = nr, Height = nr * 2, RadialSegments = 12, Rings = 6 };
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
                var mesh = n.IsAnchor ? (Mesh)_anchorMesh : n.IsRigidSplit ? new BoxMesh { Size = new Vector3(0.24f,0.24f,0.24f) } : _nodeMesh;
                var mat = n.IsAnchor ? _mm.Anchor : n.IsRigidSplit ? new StandardMaterial3D { AlbedoColor = new Color(0.2f,0.8f,1f) } : _mm.Node;
                v.BaseMaterial=mat;
                v.Front = NewMesh(mesh, mat); v.Back = NewMesh(mesh, mat);
                v.Tie = NewMesh(_unitBox, _mm.CrossTie);
                // No cross beam inside the vehicle height envelope above the road (or at cable / hydraulic-only nodes)
                v.Tie.Visible = HeightClearance.NeedsCrossTie(design, _mm.Db, Level, n);
                _nodes[n.Id] = v; _nodeList.Add(v);
                if (n.PierBase) AddFoundation(n);
                if (n.IsTunnelSupport) NewMesh(new BoxMesh { Size = new Vector3(1.6f, 0.4f, 5.6f) }, _mm.Foundation).Position = ToV(n.Position) + new Vector3(0, -0.2f, 0);
            }
            AddTunnel(design);
            BuildClearanceMarks(design);
            var sleeves = DesignEditor.SleeveFractions(design, Level, _mm.Db);
            foreach (var m in design.Members)
            {
                var mat = _mm.Db.GetMaterial(m.MaterialId);
                if (mat == null || !_nodes.ContainsKey(m.StartNodeId) || !_nodes.ContainsKey(m.EndNodeId)) continue;
                var v = new MemberVisual { Id = m.Id, A = m.StartNodeId, B = m.EndNodeId, Mat = mat, RestLength = (float)m.Length, Aged = m.Existing && m.Condition < 0.999 };
                if (mat.Extendable)
                {
                    // Yellow cylinder = shortest length: L*(1-stroke) when retracting, the built length when extending
                    v.Barrel = sleeves.TryGetValue(m.Id, out var f) ? (float)f : (float)(1 - mat.RetractRatio);
                }
                var sm = _mm.ForMaterial(mat.MaterialId);
                // 桥面：路面 + 中线标线；立柱 / 梁：两侧桁架面各 1 根；液压杆：两侧各 外筒 + 内杆（双节套筒）
                // 预应力梁：每侧 3 段折线以显示上拱
                {
                    var na = design.FindNode(m.StartNodeId); var nb = design.FindNode(m.EndNodeId);
                    v.CrossT = CrossBeams.Fractions(mat, m.Length, na.Position.Y, nb.Position.Y);
                    v.Cross = v.CrossT.Count > 0;
                    v.CrossRed = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(v.CrossT, t => CrossBlocksTraffic(design, na.Position, nb.Position, t)));
                }
                // Flexible cables sag slightly under their own weight; heavier cables sag more
                if (mat.IsCable) v.Sag = Mathf.Clamp(0.008f * Mathf.Sqrt((float)mat.MassPerMeter / 6.28f), 0.004f, 0.03f);
                int count = mat.IsDeck ? (mat.CarriesTrains ? 4 : 2) : mat.Extendable ? 4 : mat.PrestressLoad > 0 ? 6 : mat.IsCable ? 2 * CableSegments : mat.IsPier ? 3 : 2;
                int firstCross = count;
                count += v.CrossT.Count;
                v.Parts = new MeshInstance3D[count];
                v.Special = new Material[count];
                for (int i = 0; i < count; i++)
                {
                    if (mat.Extendable && i % 2 == 1) v.Special[i] = _mm.Chrome;
                    if (mat.IsDeck && i == 1) v.Special[i] = mat.CarriesCars ? _mm.LaneMark : _mm.CrossTie;
                    if (mat.IsDeck && i >= 2) v.Special[i] = _mm.Chrome;   // steel rails
                    // Cross beam keeps its own colour; red when it sits inside the vehicle height and will block traffic
                    bool crossPart = i >= firstCross;
                    if (crossPart) v.Special[i] = v.CrossRed[i - firstCross] ? _mm.ForUtilization(1.2) : _mm.CrossTie;
                    v.Parts[i] = NewMesh(crossPart ? _unitBox : PartMesh(mat, i), v.Special[i] ?? sm);
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

        // ---------------- Underwater tunnel ----------------
        private readonly System.Collections.Generic.List<(MeshInstance3D shell, StandardMaterial3D mat, Vector3 basePos)> _tubes = new();
        private TunnelGeometry _tubeGeo;
        private bool _cutaway;
        private readonly List<(MeshInstance3D mesh, Mesh full, Mesh section)> _tubeSections = new();
        private readonly List<MeshInstance3D> _tubeFrontDetails = new();
        private readonly List<(int element, MeshInstance3D mesh, Vector3 top, Vector3 anchor)> _tubeCableLines = new();

        public void SetCutaway(bool on)
        {
            _cutaway = on;
            foreach (var part in _tubeSections) part.mesh.Mesh = on ? part.section : part.full;
            foreach (var part in _tubeFrontDetails) part.Visible = !on;
        }

        private MeshInstance3D TubeLining(Node3D parent, string shape, float length, float width, float height, float wall, Material material, float x = 0)
        {
            var full = TunnelVisualMesh.Lining(shape, length, width, height, wall, false);
            var section = TunnelVisualMesh.Lining(shape, length, width, height, wall, true);
            var mesh = new MeshInstance3D { Mesh = _cutaway ? section : full, MaterialOverride = material, Position = new Vector3(x,0,0) };
            parent.AddChild(mesh); _tubeSections.Add((mesh, full, section)); return mesh;
        }

        private static MeshInstance3D TubeBox(Node3D parent, string name, Vector3 position, Vector3 size, Material material)
        {
            var mesh = new MeshInstance3D { Name = name, Mesh = new BoxMesh { Size = size }, MaterialOverride = material, Position = position };
            parent.AddChild(mesh); return mesh;
        }
        /// <summary>Settlement is drawn this many times larger so it can be seen.</summary>
        public const float TunnelSettleScale = 10f;
        public static readonly Color[] RiskColors = { new("#4caf50"), new("#e8c33a"), new("#ef8a2e"), new("#e04436") };

        /// <summary>Distinct hollow tube families. Utilization colours the joint rims while the lining retains its material identity.</summary>
        private void AddTunnel(BridgeDesign design)
        {
            _tubes.Clear(); _tubeSections.Clear(); _tubeFrontDetails.Clear(); _tubeCableLines.Clear();
            _tubeGeo = TunnelGeometry.Build(design, _mm.Db);
            var g = _tubeGeo;
            if (g == null) return;
            float h = (float)g.Type.TubeHeight, w = (float)g.Type.TubeWidth, fl = (float)g.Type.FloorHeight;
            var angles = new float[g.Count];
            for (int i = 0; i < g.Count; i++) angles[i] = Mathf.Atan2((float)(g.Y1[i] - g.Y0[i]), (float)(g.X1[i] - g.X0[i]));
            for (int i = 0; i < g.Count; i++)
            {
                var a = new Vector3((float)g.X0[i], (float)g.Y0[i], 0); var b = new Vector3((float)g.X1[i], (float)g.Y1[i], 0);
                var d = b - a; float len = d.Length(); float ang = Mathf.Atan2(d.Y, d.X);
                var up = new Vector3(-Mathf.Sin(ang), Mathf.Cos(ang), 0);
                var centre = (a + b) / 2 + up * (h / 2);
                string shape = g.Type.IsShield ? "shield" : g.Type.IsFloating ? "floating" : "immersed";
                var gc = Color.FromHtml(g.Grade[i].Color ?? "#9a9a92");
                var solid = new StandardMaterial3D { AlbedoColor = gc, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                var mat = new StandardMaterial3D { AlbedoColor = RiskColors[0], Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                float wall = g.GradeId[i] == "heavy" ? 0.42f : g.GradeId[i] == "reinforced" ? 0.34f : 0.27f;
                var shell = TubeLining(_container, shape, Mathf.Max(len,0.1f), w, h, wall, solid);
                shell.Name = "TunnelBody_" + shape; shell.Position = centre; shell.Rotation = new Vector3(0,0,ang);
                var accent = new StandardMaterial3D { AlbedoColor = new Color(shape == "immersed" ? "c3934c" : shape == "shield" ? "8b7760" : "468f9b"), Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                TubeLining(shell,shape,0.18f,w+0.06f,h+0.06f,wall+0.03f,mat,-len/2+0.09f).Name = "OpenJointRim";
                TubeLining(shell,shape,0.1f,w+0.03f,h+0.03f,wall+0.02f,accent,len/2-0.05f);
                var floor = TunnelVisualMesh.Floor(shape,len,w,h,wall,fl,out float floorWidth);
                shell.AddChild(new MeshInstance3D {Name="FloorSlab",Mesh=floor,MaterialOverride=solid});
                var road = new StandardMaterial3D {AlbedoColor = new Color("3c4851"),Roughness=0.95f};
                float roadWidth = Mathf.Min(4.4f,floorWidth-0.4f);
                // Separate the pavement and paint from the slab to prevent coplanar depth flicker.
                TubeBox(shell,"RoadSurface",new Vector3(0,-h/2+fl+0.005f,0),new Vector3(len,0.05f,roadWidth),road);
                var paint = new StandardMaterial3D {AlbedoColor=new Color("e5dcb5"),Roughness=0.8f};
                for(float x=-len/2+0.8f;x<len/2;x+=2.4f)
                    TubeBox(shell,"LaneMark",new Vector3(x,-h/2+fl+0.031f,0),new Vector3(Mathf.Min(1.1f,len/2-x),0.002f,0.09f),paint);
                if(g.Type.IsShield)
                {
                    for(float x=-len/2+1.6f;x<len/2-0.2f;x+=1.6f)
                        TubeLining(shell,shape,0.075f,w+0.02f,h+0.02f,wall+0.02f,accent,x).Name = "SegmentRing";
                }
                else if(g.Type.IsFloating)
                {
                    float podHeight = 0.35f + Mathf.Clamp((float)(g.Buoy[i].Ratio-1)*1.4f,0,0.6f);
                    for(int side=-1;side<=1;side+=2)
                    {
                        var pod = TubeBox(shell,"BuoyancyChamber",new Vector3(0,h*0.22f,side*(w/2-0.36f)),new Vector3(len*0.72f,podHeight,0.65f),accent);
                        if(side>0)_tubeFrontDetails.Add(pod);
                    }
                    TubeLining(shell,shape,0.26f,w+0.12f,h+0.12f,wall+0.08f,accent).Name = "AnchorCollar";
                }
                else
                {
                    TubeBox(shell,"InspectionWalkway",new Vector3(0,-h/2+fl+0.12f,-w/2+0.85f),new Vector3(len,0.24f,0.85f),solid);
                    TubeBox(shell,"ServiceDuct",new Vector3(0,-h/2+fl+1.1f,-w/2+wall+0.1f),new Vector3(len,0.18f,0.18f),accent);
                }
                var lampMat = new StandardMaterial3D {AlbedoColor=new Color("efe3b8"),EmissionEnabled=true,Emission=new Color("e2d6ac"),EmissionEnergyMultiplier=0.4f};
                for(float x=-len/2+1.2f;x<len/2-0.3f;x+=3.2f)
                    TubeBox(shell,"InteriorLight",new Vector3(x,h*0.26f,-w*0.29f),new Vector3(0.65f,0.1f,0.16f),lampMat);
                // Miter both ends on the bisector of the joint angle: neighbouring elements meet without gap or overlap
                float t0 = i > 0 ? Mathf.Tan((angles[i] - angles[i - 1]) / 2) : 0, t1 = i + 1 < g.Count ? Mathf.Tan((angles[i + 1] - angles[i]) / 2) : 0;
                if (Mathf.Abs(t0) > 1e-5f || Mathf.Abs(t1) > 1e-5f) MiterTube(shell, Vector3.Zero, len, h, t0, t1);
                if (g.Type.IsFloating) AddCables(g, i, a, b, w);
                else if (g.Reinforced[i] && g.Type.IsShield)
                    NewMesh(new BoxMesh { Size = new Vector3(len, 0.6f, w + 2f) }, _mm.Foundation).Position = (a + b) / 2 - new Vector3(0, 0.3f, 0);
                _tubes.Add((shell, mat, centre));
            }
            AddTunnelAccess(g);
            SetCutaway(_cutaway);
            // Static check (normal water level, no vehicles) so the build view shows the risk colours at once
            if (_mm.Db.TunnelConfig != null)
                ShowTunnel(Physics.TunnelAnalysis.Evaluate(g, Level, _mm.Db.TunnelConfig, Physics.TunnelAnalysis.StaticEnvironment(Level, _mm.Db.TunnelConfig)), false);
        }

        /// <summary>
        /// Shears every mesh of an element (local x along the axis, y up, origin at the centre, bottom at -h/2) so
        /// its end faces lie on the bisector planes through the bottom joint points: x += (y + h/2)·((1−u)·t0 − u·t1),
        /// u = position along the element, t = tan(half the angle change at that joint).
        /// </summary>
        private void MiterTube(Node3D node, Vector3 offset, float len, float h, float t0, float t1)
        {
            Mesh Shear(Mesh m, Vector3 p)
            {
                if (m == null) return null;
                var am = new ArrayMesh();
                for (int s = 0; s < m.GetSurfaceCount(); s++)
                {
                    var arr = m.SurfaceGetArrays(s);
                    var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    for (int k = 0; k < verts.Length; k++)
                    {
                        var v = verts[k] + p;
                        float u = Mathf.Clamp((v.X + len / 2) / len, 0, 1);
                        verts[k].X += (v.Y + h / 2) * ((1 - u) * t0 - u * t1);
                    }
                    arr[(int)Mesh.ArrayType.Vertex] = verts;
                    am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
                    var sm = m.SurfaceGetMaterial(s); if (sm != null) am.SurfaceSetMaterial(s, sm);
                }
                return am;
            }
            if (node is MeshInstance3D mi)
            {
                int idx = _tubeSections.FindIndex(t => t.mesh == mi);
                if (idx >= 0) { var t = _tubeSections[idx]; _tubeSections[idx] = (mi, Shear(t.full, offset), Shear(t.section, offset)); mi.Mesh = _cutaway ? _tubeSections[idx].section : _tubeSections[idx].full; }
                else mi.Mesh = Shear(mi.Mesh, offset);
            }
            foreach (var child in node.GetChildren())
                if (child is Node3D c) MiterTube(c, offset + (node == null ? Vector3.Zero : c.Position), len, h, t0, t1);
        }

        /// <summary>Floating tube: anchor cables from the tube bottom to the bed, splayed sideways in pairs, plus anchor blocks.</summary>
        private void AddCables(TunnelGeometry g, int i, Vector3 a, Vector3 b, float w)
        {
            int n = g.CableCount[i];
            float bed = (float)TunnelRules.BedY(Level.Terrain);
            var cmat = new StandardMaterial3D { AlbedoColor = Color.FromHtml(g.Cable[i].Color ?? "#3a3f45"), Roughness = 0.6f, Metallic = 0.4f };
            float tan = Mathf.Tan((float)g.Type.CableAngleDeg * Mathf.Pi / 180);
            for (int k = 0; k < n; k++)
            {
                float f = (k / 2 + 0.5f) / Mathf.Ceil(n / 2f);
                var direction = (b-a).Normalized();var up = new Vector3(-direction.Y,direction.X,0);
                var top = a.Lerp(b, f) + up*(float)g.Type.TubeHeight*0.26f;
                float side = k % 2 == 0 ? -1 : 1;
                var t3 = new Vector3(top.X, top.Y, side * w * 0.45f);
                var b3 = new Vector3(top.X, bed, side * (w / 2 + (top.Y - bed) * tan));
                var line = Line(t3, b3, 0.12f, cmat);
                line.Name = "TunnelAnchorCable";
                _tubeCableLines.Add((i,line,t3,b3));
                var anchorMat = g.Reinforced[i] ? _mm.Foundation : cmat;
                NewMesh(new BoxMesh { Size = new Vector3(1.2f, 0.6f, 1.2f) }, anchorMat).Position = b3 + new Vector3(0, 0.3f, 0);
            }
        }

        /// <summary>Thin box from a to b in any direction.</summary>
        private MeshInstance3D Line(Vector3 a, Vector3 b, float thick, Material mat)
        {
            var mi = NewMesh(new BoxMesh {Size=Vector3.One},mat);
            SetCable(mi,a,b,thick);return mi;
        }

        private static void SetCable(MeshInstance3D mi, Vector3 a, Vector3 b, float thick)
        {
            var d = b-a;float len=d.Length();
            if (len < 1e-4f) return;
            var y = d / len;
            var x = Mathf.Abs(y.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : y.Cross(Vector3.Up).Normalized();
            var z = x.Cross(y).Normalized();
            mi.Transform = new Transform3D(new Basis(x*thick, y*len, z*thick), (a+b)/2);
        }

        /// <summary>Colour every element by its utilization band; while testing also sink it by its settlement.</summary>
        public void ShowTunnel(Physics.TunnelState st, bool testing, bool flooded = false)
        {
            if (st == null || _mm.Db.TunnelConfig == null) return;
            for (int i = 0; i < _tubes.Count && i < st.Count; i++)
            {
                var (shell, mat, basePos) = _tubes[i];
                int band = flooded ? 3 : TunnelRules.RiskBand(_mm.Db.TunnelConfig, st.Util[i]);
                mat.AlbedoColor = RiskColors[band];
                // Floating tubes also sway sideways with the current (drawn at true scale)
                float sway = testing && i < st.Sway.Length ? (float)st.Sway[i] : 0;
                shell.Position = basePos - new Vector3(0, testing ? (float)st.Settle[i] * TunnelSettleScale : 0, -sway);
            }
            foreach(var cable in _tubeCableLines)
            {
                var tube = _tubes[cable.element];
                SetCable(cable.mesh,cable.top+tube.shell.Position-tube.basePos,cable.anchor,0.12f);
            }
        }

        private MeshInstance3D NewMesh(Mesh mesh, Material mat)
        {
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
            _container.AddChild(mi);
            return mi;
        }

        /// <summary>Thin dashed orange line at road + height limit over every deck span, labelled once.</summary>
        private void BuildClearanceMarks(BridgeDesign design)
        {
            double lim = HeightClearance.Limit(Level, _mm.Db);
            var mat = World.TerrainBuilder.Unshaded(new Color(1f, 0.55f, 0.15f, 0.55f));
            bool labelled = false;
            foreach (var m in design.Members)
            {
                if (_mm.Db.GetMaterial(m.MaterialId)?.IsDeck != true) continue;
                var a = design.FindNode(m.StartNodeId); var b = design.FindNode(m.EndNodeId);
                if (a == null || b == null) continue;
                var pa = ToV(a.Position) + new Vector3(0, (float)lim + DeckSurfaceOffset, 0); var pb = ToV(b.Position) + new Vector3(0, (float)lim + DeckSurfaceOffset, 0);
                for (float t = 0; t < 1; t += 0.2f)
                {
                    var seg = NewMesh(_unitBox, mat); seg.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                    SetBeam(seg, pa.Lerp(pb, t), pa.Lerp(pb, t + 0.12f), 0.04f, 0.04f);
                }
                if (!labelled)
                {
                    labelled = true;
                    _container.AddChild(new Label3D { Text = Loc.F("限高 {0:0.0} m", (lim)), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 32, PixelSize = 0.01f,
                        Modulate = new Color(1f, 0.65f, 0.3f), Position = pa.Lerp(pb, 0.5f) + new Vector3(0, 0.3f, BuildPlane.FrontZ) });
                }
            }
        }

        // ---------------- Member profiles ----------------
        // Unit meshes: x = along the member (-0.5..0.5), y = in-plane thickness, z = out-of-plane depth.
        // SetBeam scales them like the unit box, so every material keeps its own cross-section shape.
        private static readonly System.Collections.Generic.Dictionary<string, Mesh> Profiles = new();

        private Mesh PartMesh(MaterialDefinition mat, int part)
        {
            if (mat.IsDeck && part >= 1) return _unitBox;                      // lane marking / sleeper strip / rails
            // Pier: capsule (stadium) plan = middle slab + a round column at each truss plane
            if (mat.IsPier) return part == 0 ? _unitBox : (Profiles.TryGetValue("round", out var rm) ? rm : Profiles["round"] = BuildProfile("round"));
            string kind = mat.Extendable ? "round"
                : mat.Look == "igirder" ? "ibeam" : mat.Look == "box" ? "boxgirder"
                : mat.MaterialId switch
                {
                    "steel" => "ibeam",
                    "cable" or "cable_composite" => "round",
                    "concrete" or "pier_concrete" => "round",
                    "rc_column" => "octagon",
                    "deck_steel" or "deck_frp" => "ribbed",
                    "deck_wood" => "planks",
                    _ => "box",
                };
            if (kind == "box") return _unitBox;
            if (!Profiles.TryGetValue(kind, out var mesh)) Profiles[kind] = mesh = BuildProfile(kind);
            return mesh;
        }

        private static Mesh BuildProfile(string kind)
        {
            if (kind == "round") return Prism(16, 0.5f);
            if (kind == "octagon") return Prism(8, 0.5f / Mathf.Cos(Mathf.Pi / 8));
            var boxes = new System.Collections.Generic.List<(Vector3 c, Vector3 s)>();
            switch (kind)
            {
                case "ibeam":      // flanges top / bottom, thin web
                    boxes.Add((new Vector3(0, 0.42f, 0), new Vector3(1, 0.16f, 1)));
                    boxes.Add((new Vector3(0, -0.42f, 0), new Vector3(1, 0.16f, 1)));
                    boxes.Add((Vector3.Zero, new Vector3(1, 0.7f, 0.22f)));
                    break;
                case "boxgirder":  // hollow box: top, bottom and two webs
                    boxes.Add((new Vector3(0, 0.43f, 0), new Vector3(1, 0.14f, 1)));
                    boxes.Add((new Vector3(0, -0.43f, 0), new Vector3(1, 0.14f, 0.8f)));
                    boxes.Add((new Vector3(0, 0, 0.36f), new Vector3(1, 0.74f, 0.12f)));
                    boxes.Add((new Vector3(0, 0, -0.36f), new Vector3(1, 0.74f, 0.12f)));
                    break;
                case "ribbed":     // deck plate with longitudinal ribs underneath
                    boxes.Add((new Vector3(0, 0.25f, 0), new Vector3(1, 0.5f, 1)));
                    foreach (float z in new[] { -0.38f, -0.13f, 0.13f, 0.38f }) boxes.Add((new Vector3(0, -0.25f, z), new Vector3(1, 0.5f, 0.07f)));
                    break;
                case "planks":     // planks across the deck with small gaps, on two stringers
                    for (int i = 0; i < 10; i++) boxes.Add((new Vector3(-0.45f + i * 0.1f, 0.2f, 0), new Vector3(0.085f, 0.6f, 1)));
                    boxes.Add((new Vector3(0, -0.3f, 0.3f), new Vector3(1, 0.4f, 0.12f)));
                    boxes.Add((new Vector3(0, -0.3f, -0.3f), new Vector3(1, 0.4f, 0.12f)));
                    break;
            }
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var (c, s) in boxes)
            {
                var bm = new SurfaceTool(); bm.CreateFrom(new BoxMesh { Size = s }, 0); bm.Index();
                st.AppendFrom(bm.Commit(), 0, new Transform3D(Basis.Identity, c));
            }
            st.Index();
            return st.Commit();
        }

        /// <summary>Regular prism along x (round bar / cable / cylinder when sides = 16, octagon when 8).</summary>
        private static Mesh Prism(int sides, float r)
        {
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 n)
            {
                // Both windings with the outward normal: whichever faces the camera is drawn, lit correctly
                foreach (var (p, q) in new[] { (b, c), (c, b) })
                {
                    st.SetNormal(n); st.SetUV(new Vector2(a.X + 0.5f, a.Y)); st.AddVertex(a);
                    st.SetNormal(n); st.SetUV(new Vector2(p.X + 0.5f, p.Y)); st.AddVertex(p);
                    st.SetNormal(n); st.SetUV(new Vector2(q.X + 0.5f, q.Y)); st.AddVertex(q);
                }
            }
            for (int i = 0; i < sides; i++)
            {
                float a0 = Mathf.Tau * i / sides + Mathf.Pi / sides, a1 = Mathf.Tau * (i + 1) / sides + Mathf.Pi / sides;
                var o0 = new Vector3(0, Mathf.Cos(a0) * r, Mathf.Sin(a0) * r); var o1 = new Vector3(0, Mathf.Cos(a1) * r, Mathf.Sin(a1) * r);
                var n = sides >= 12 ? (o0 + o1).Normalized() : (o0 + o1).Normalized();
                Vector3 l0 = o0 + Vector3.Left * 0.5f, l1 = o1 + Vector3.Left * 0.5f, r0 = o0 + Vector3.Right * 0.5f, r1 = o1 + Vector3.Right * 0.5f;
                Tri(l0, r0, r1, n); Tri(l0, r1, l1, n);
                Tri(new Vector3(-0.5f, 0, 0), l0, l1, Vector3.Left);
                Tri(new Vector3(0.5f, 0, 0), r1, r0, Vector3.Right);
            }
            return st.Commit();
        }

        /// <summary>Level of the bridge (height clearance); null uses the default 4.5 m.</summary>
        public LevelDefinition Level { get; set; }

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
                // A split seam has two model nodes; draw the other end too so both deck heads keep a node model
                bool twin = model != null && displacement != null && v.Id > 0 && model.NodeIndex.TryGetValue(-v.Id, out int j);
                if (twin)
                {
                    if (v.TwinFront == null) { v.TwinFront = NewMesh(v.Front.Mesh, v.BaseMaterial); v.TwinBack = NewMesh(v.Front.Mesh, v.BaseMaterial); }
                    model.NodeIndex.TryGetValue(-v.Id, out int tj);
                    var q = ModelPos(model, tj, displacement, scale);
                    v.TwinFront.Position = new Vector3(q.X, q.Y, zf); v.TwinBack.Position = new Vector3(q.X, q.Y, -zf);
                    v.TwinFront.MaterialOverride = v.Front.MaterialOverride; v.TwinBack.MaterialOverride = v.Back.MaterialOverride;
                }
                if (v.TwinFront != null) { v.TwinFront.Visible = twin; v.TwinBack.Visible = twin; }
            }
            foreach (var m in _memberList)
            {
                // Use the model's member ends: on a released deck seam the two sides are separate end points
                Vector3 a, b;
                if (model != null && displacement != null && model.MemberIndex.TryGetValue(m.Id, out int mk))
                { a = ModelPos(model, model.A[mk], displacement, scale); b = ModelPos(model, model.B[mk], displacement, scale); }
                else { a = NodePos(_nodes[m.A], model, displacement, scale); b = NodePos(_nodes[m.B], model, displacement, scale); }
                if (m.Mat.IsDeck)
                {
                    float th = (float)m.Mat.VisualThickness;
                    var off = new Vector3(0, DeckSurfaceOffset - th / 2, 0);
                    SetBeam(m.Parts[0], a + off, b + off, th, (float)m.Mat.DeckWidth);
                    // 路面中线标线（略高于桥面顶面），用来与梁、索区分
                    var dir = (b - a).Normalized();
                    var top = new Vector3(0, DeckSurfaceOffset + 0.01f, 0);
                    if (m.Mat.CarriesCars) SetBeam(m.Parts[1], a + top + dir * 0.3f, b + top - dir * 0.3f, 0.02f, 0.14f);
                    else SetBeam(m.Parts[1], a + top, b + top, 0.02f, 2.6f);   // rail-only deck: dark sleeper bed
                    if (m.Mat.CarriesTrains)
                        for (int r = 0; r < 2; r++)
                        {
                            var rz = new Vector3(0, DeckSurfaceOffset + (float)RailTrack.Height / 2, (r == 0 ? 1 : -1) * (float)(RailTrack.Gauge / 2));
                            SetBeam(m.Parts[2 + r], a + rz, b + rz, (float)RailTrack.Height, (float)RailTrack.Width);
                        }
                }
                else if (m.Mat.Extendable)
                {
                    // 液压杆：外筒（粗，固定长度，从起点出发）+ 内杆（细，从终点伸入外筒），伸缩时内杆露出长度变化
                    float th = (float)m.Mat.VisualThickness;
                    var dir = (b - a).Normalized();
                    // Telescoping visual stays connected across the entire 50%-150% travel envelope.
                    float cyl = Mathf.Min(m.RestLength * m.Barrel,(b-a).Length()*0.98f);
                    float overlap = Mathf.Min(m.RestLength*0.05f,cyl*0.15f);
                    for (int side = 0; side < 2; side++)
                    {
                        var zf = new Vector3(0, 0, side == 0 ? BuildPlane.TrussOffset : -BuildPlane.TrussOffset);
                        SetBeam(m.Parts[side * 2], a + zf, a + zf + dir * cyl, th, th);
                        SetBeam(m.Parts[side * 2 + 1], a + zf + dir * (cyl-overlap), b + zf, th * 0.45f, th * 0.45f);
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
                else if (m.Mat.IsPier)
                {
                    // Single capsule-shaped pier under both truss planes (plan view: rounded ends across the road)
                    float th = (float)m.Mat.VisualThickness;
                    float zo = BuildPlane.TrussOffset;
                    SetBeam(m.Parts[0], a, b, th, 2 * zo);
                    SetBeam(m.Parts[1], a + new Vector3(0, 0, zo), b + new Vector3(0, 0, zo), th, th);
                    SetBeam(m.Parts[2], a - new Vector3(0, 0, zo), b - new Vector3(0, 0, zo), th, th);
                }
                else if (m.Mat.IsCable)
                {
                    // Cable: parabolic sag under self weight (only the horizontal span sags)
                    float th = (float)m.Mat.VisualThickness;
                    var d = b - a; float len = d.Length();
                    float sag = len * m.Sag * (len > 1e-4f ? Mathf.Abs(d.X) / len : 0);
                    for (int side = 0; side < 2; side++)
                    {
                        var zf = new Vector3(0, 0, side == 0 ? BuildPlane.TrussOffset : -BuildPlane.TrussOffset);
                        for (int i = 0; i < CableSegments; i++)
                        {
                            float t0 = i / (float)CableSegments, t1 = (i + 1) / (float)CableSegments;
                            var p0 = a + d * t0 + Vector3.Down * (4 * sag * t0 * (1 - t0));
                            var p1 = a + d * t1 + Vector3.Down * (4 * sag * t1 * (1 - t1));
                            SetBeam(m.Parts[side * CableSegments + i], p0 + zf, p1 + zf, th, th);
                        }
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
                if (m.Cross)
                {
                    // Wider than the member so the beam end shows as a block on the member in side view
                    float th = Mathf.Max((float)m.Mat.VisualThickness * 0.9f, 0.15f);
                    var zf = new Vector3(0, 0, BuildPlane.TrussOffset);
                    int first = m.Parts.Length - m.CrossT.Count;
                    for (int c = 0; c < m.CrossT.Count; c++)
                    {
                        var pos = a + (b - a) * (float)m.CrossT[c];
                        // Runs along Z, which SetBeam cannot orient (its frame is built from Z), so place it directly
                        m.Parts[first + c].Transform = new Transform3D(new Basis(new Vector3(th, 0, 0), new Vector3(0, th, 0), new Vector3(0, 0, 2 * zf.Z)), pos);
                    }
                }
            }
        }

        private static Vector3 ModelPos(StructuralModel model, int i, double[] u, float scale) =>
            new((float)(model.X0[i] + u[2 * i] * scale), (float)(model.Y0[i] + u[2 * i + 1] * scale), 0);

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
            // A member drawn right-to-left would get its local "up" pointing down (deck top face upside down,
            // so neighbouring deck segments looked flipped); turn it 180 degrees about its axis instead
            if (y.Y < -1e-6f) { y = -y; z = -z; }
            mi.Transform = new Transform3D(new Basis(x * len, y * thickness, z * depth), (a + b) * 0.5f);
        }

        // ---------------- 颜色 ----------------
        /// <summary>建造模式着色：材料颜色 + 选中/悬停高亮；可选叠加静力受力颜色。</summary>
        public void ColorForBuild(ICollection<int> selNodes, ICollection<int> selMembers, int hoverNode, int hoverMember, StructuralModel model, MemberStatus[] status)
        {
            foreach (var v in _nodeList)
            {
                Material mat = selNodes.Contains(v.Id) ? _mm.NodeSelected : v.Id == hoverNode ? _mm.Hover : v.BaseMaterial;
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
                Material mat = v.BaseMaterial;
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
            if (ReferenceEquals(m.Applied, mat)) return;
            m.Applied = mat;
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
        public int PickNode(Vector3 origin, Vector3 dir, float radius = 0.12f)
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

        /// <summary>Attract scenes show bridge geometry without construction handles.</summary>
        public void SetConnectionMarkersVisible(bool visible)
        {
            foreach(var node in _nodeList)
            {
                node.Front.Visible=visible;node.Back.Visible=visible;
                if(node.TwinFront!=null){node.TwinFront.Visible=visible;node.TwinBack.Visible=visible;}
            }
        }

        public void SetVisibleAll(bool v) { if (_container != null) _container.Visible = v; }
    }
}
