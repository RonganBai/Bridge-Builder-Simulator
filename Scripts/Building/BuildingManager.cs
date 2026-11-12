using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.World;
using Godot;

namespace BridgeArchitect3D.Building
{
    /// <summary>工具：选择、建造（材料由 ToolMaterial 指定）、删除。节点只能由构件端点产生，没有单独创建节点的工具。</summary>
    public enum BuildTool { Select, Build, Delete }

    /// <summary>
    /// 建造模式输入与交互：节点创建/选择/拖动、材料连接（带实时预览）、删除，以及建造期的静力受力显示。
    /// 所有数据修改都委托给 DesignEditor（规则与预算在那里统一校验）。
    /// </summary>
    public partial class BuildingManager : Node3D
    {
        private const float NodeSnapRadius = (float)DesignEditor.NodeSnapRadius;   // snap / pick radius m (hold Ctrl: no node snap)
        private const float MemberPickRadius = 0.35f;

        public DesignEditor Editor { get; private set; }
        public BuildTool Tool { get; private set; } = BuildTool.Select;
        /// <summary>建造工具当前使用的材料 Id。</summary>
        public string ToolMaterial { get; private set; }
        public bool Enabled { get; set; } = true;
        public bool StressView { get; private set; }
        public int SelectedNodeId { get; private set; } = -1;
        public int SelectedMemberId { get; private set; } = -1;
        /// <summary>建造模式下仅自重的静力分析（用于受力显示与属性面板）。</summary>
        public BridgeSimulation StaticSim { get; private set; }

        public event Action SelectionChanged;
        public event Action<string, bool> Message;           // 文本, 是否为错误
        public event Action<string, Vector2, bool> Preview;   // 文本, 屏幕位置, 是否允许
        public event Action ToolChanged;

        private Camera3D _camera;
        private BridgeRenderer _renderer;
        private MaterialManager _mm;
        private int _hoverNode = -1, _hoverMember = -1;
        private int _chainStart = -1;
        private int _dragNode = -1;
        private Vector2 _lastMouse;
        private MeshInstance3D _previewBeam, _cursor, _grid, _ring;
        private bool _showGrid = true;

        public void Init(DesignEditor editor, Camera3D camera, BridgeRenderer renderer, MaterialManager mm, bool showGrid)
        {
            Editor = editor; _camera = camera; _renderer = renderer; _mm = mm; _showGrid = showGrid;
            Editor.Changed += OnDesignChanged;
            _previewBeam = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One }, Visible = false };
            _cursor = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f }, MaterialOverride = _mm.PreviewOk, Visible = false };
            AddChild(_previewBeam); AddChild(_cursor);
            // 吸附光圈：鼠标吸附到已有节点时显示
            _ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.42f, OuterRadius = 0.55f, Rings = 24, RingSegments = 8 }, MaterialOverride = World.TerrainBuilder.Unshaded(new Color(0.4f, 1f, 0.9f, 0.9f)), Visible = false, RotationDegrees = new Vector3(90, 0, 0) };
            AddChild(_ring);
            _grid = new MeshInstance3D { Mesh = BuildGrid(editor.Level), MaterialOverride = World.TerrainBuilder.Unshaded(new Color(0.30f, 0.24f, 0.15f, 0.28f)), Visible = showGrid };
            AddChild(_grid);
            if(_grid.MaterialOverride is StandardMaterial3D gridMaterial)
            {gridMaterial.AlbedoColor=Colors.White;gridMaterial.VertexColorUseAsAlbedo=true;}
            OnDesignChanged();
        }

        /// <summary>工具栏中的材料顺序（桥面在前，支撑在后），数字键 2~9、0 按此顺序对应。</summary>
        public System.Collections.Generic.List<string> MaterialOrder =>
            Editor.Level.AllowedMaterials.Where(id => Editor.Db.GetMaterial(id)?.IsDeck == true)
                .Concat(Editor.Level.AllowedMaterials.Where(id => Editor.Db.GetMaterial(id) is { IsDeck: false })).ToList();

        public void SetShowGrid(bool v) { _showGrid = v; _grid.Visible = v && Enabled; }

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            if(!enabled) ClearDiagnostic();
            CancelChain();
            _dragNode = -1;
            _cursor.Visible = false; _previewBeam.Visible = false; _ring.Visible = false;
            _grid.Visible = enabled && _showGrid;
            Preview?.Invoke(null, Vector2.Zero, true);
            if (enabled) { _renderer.Rebuild(Editor.Design); Recolor(); }
        }

        public void SetTool(BuildTool t, string materialId = null)
        {
            Tool = t;
            ToolMaterial = t == BuildTool.Build ? materialId : null;
            CancelChain();
            ToolChanged?.Invoke();
        }

        public void SetStressView(bool on)
        {
            StressView = on;
            RefreshStatic();
            Recolor();
            SelectionChanged?.Invoke();
        }

        public void ClearSelection()
        {
            SelectedNodeId = -1; SelectedMemberId = -1;
            SelNodes.Clear(); SelMembers.Clear();
            Recolor(); SelectionChanged?.Invoke();
        }

        private void OnDesignChanged()
        {
            if (Editor.Design.FindNode(SelectedNodeId) == null) SelectedNodeId = -1;
            if (Editor.Design.FindMember(SelectedMemberId) == null) SelectedMemberId = -1;
            if (Editor.Design.FindNode(_chainStart) == null) _chainStart = -1;
            _problemMembers.Clear();   // 设计改动后不再显示上次失败的高亮
            ClearDiagnostic();
            SelNodes.RemoveWhere(id => Editor.Design.FindNode(id) == null);
            SelMembers.RemoveWhere(id => Editor.Design.FindMember(id) == null);
            _renderer.Rebuild(Editor.Design);
            RefreshStatic();
            Recolor();
            SelectionChanged?.Invoke();
        }

        private void RefreshStatic()
        {
            StaticSim = BridgeSimulation.StaticAnalysis(Editor.Design, Editor.Level, Editor.Db);
        }

        private void Recolor()
        {
            if (!Enabled) return;
            _hlNodes.Clear(); _hlMembers.Clear();
            foreach (var n in SelNodes) _hlNodes.Add(n);
            foreach (var m in SelMembers) _hlMembers.Add(m);
            if (_chainStart >= 0) _hlNodes.Add(_chainStart);
            if (SelectedNodeId >= 0) _hlNodes.Add(SelectedNodeId);
            if (SelectedMemberId >= 0) _hlMembers.Add(SelectedMemberId);
            if (_hintNode >= 0 && Editor.Design.Members.Count == 0) _hlNodes.Add(_hintNode);
            foreach (var id in _problemMembers) _hlMembers.Add(id);
            bool stress = StressView && StaticSim != null && StaticSim.Output.Stable;
            _renderer.ColorForBuild(_hlNodes, _hlMembers, _hoverNode, _hoverMember, stress ? StaticSim.Model : null, stress ? StaticSim.Status : null);
            _renderer.RefreshBowIfNeeded();
        }

        // ---------------- 引导与问题定位高亮 ----------------
        private int _hintNode = -1;
        private readonly HashSet<int> _problemMembers = new();

        /// <summary>第一步引导：高亮（发光）一个节点，直到建造出第一根构件。</summary>
        public void SetHintNode(int nodeId) { _hintNode = nodeId; Recolor(); }

        /// <summary>失败定位：高亮出问题的构件，并选中第一根以显示其属性。下一次修改设计后清除。</summary>
        public void HighlightProblem(IEnumerable<int> memberIds)
        {
            _problemMembers.Clear();
            foreach (var id in memberIds) if (Editor.Design.FindMember(id) != null) _problemMembers.Add(id);
            SelNodes.Clear(); SelMembers.Clear(); SelectedNodeId = -1;
            SelectedMemberId = _problemMembers.Count > 0 ? _problemMembers.First() : -1;
            SetTool(BuildTool.Select);
            Recolor(); SelectionChanged?.Invoke();
        }

        private Node3D _diagnosticOverlay;
        private readonly List<Vector3> _diagnosticPositions=new();
        public IEnumerable<Vector3> DiagnosticPositions => _diagnosticPositions;

        public void ClearDiagnostic()
        {
            if(_diagnosticOverlay!=null) { _diagnosticOverlay.Visible=false; _diagnosticOverlay.QueueFree(); _diagnosticOverlay=null; }
            _diagnosticPositions.Clear();
        }

        /// <summary>Non-selectable overlays preserve editing selection and remain visible through the structure.</summary>
        public void ShowDiagnostic(DiagnosticTargets targets)
        {
            ClearDiagnostic();
            _diagnosticOverlay=new Node3D(); AddChild(_diagnosticOverlay);
            void Member(int id,Color color,string role)
            {
                var member=Editor.Design.FindMember(id); if(member==null) return;
                var a=Editor.Design.FindNode(member.StartNodeId);var b=Editor.Design.FindNode(member.EndNodeId);
                if(a==null || b==null) return;
                var av=BridgeRenderer.ToV(a.Position);var bv=BridgeRenderer.ToV(b.Position);
                _diagnosticPositions.Add(av);_diagnosticPositions.Add(bv);
                var material=new StandardMaterial3D {AlbedoColor=new Color(color.R,color.G,color.B,0.55f),
                    Transparency=BaseMaterial3D.TransparencyEnum.Alpha,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=true};
                float thickness=(float)System.Math.Max(0.25,(Editor.Db.GetMaterial(member.MaterialId)?.VisualThickness ?? 0.2)*1.5);
                foreach(float side in new[]{-BuildPlane.TrussOffset,BuildPlane.TrussOffset})
                {
                    var overlay=new MeshInstance3D {Mesh=new BoxMesh {Size=Vector3.One},MaterialOverride=material};
                    _diagnosticOverlay.AddChild(overlay);
                    BridgeRenderer.SetBeam(overlay,av+new Vector3(0,0,side),bv+new Vector3(0,0,side),thickness,thickness);
                }

            }
            var affectedColor=targets.Kind==MechanismDiagnosticKind.DeadPoint ? new Color(0.85f,0.35f,1f) :
                targets.Kind==MechanismDiagnosticKind.ConstraintConflict ? new Color(1,0.2f,0.15f) : new Color(1,0.65f,0.1f);
            foreach(int id in targets.Members)
                Member(id,targets.BlockingMembers.Contains(id)?new Color(1,0.2f,0.15f):affectedColor,
                    targets.BlockingMembers.Contains(id)?"阻挡支撑":"涉及构件");
            foreach(int id in targets.Nodes)
            {
                var node=Editor.Design.FindNode(id);if(node==null) continue;
                var point=BridgeRenderer.ToV(node.Position);_diagnosticPositions.Add(point);
                var material=new StandardMaterial3D {AlbedoColor=affectedColor,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=true};
                foreach(float side in new[]{-BuildPlane.TrussOffset,BuildPlane.TrussOffset})
                    _diagnosticOverlay.AddChild(new MeshInstance3D {Mesh=new SphereMesh {Radius=0.33f,Height=0.66f},MaterialOverride=material,Position=point+new Vector3(0,0,side)});

            }
            Member(targets.SourceMember,new Color(0.1f,0.95f,1),"当前液压杆");
        }

        // ---------------- 多选 / 框选 ----------------
        /// <summary>框选得到的节点与构件集合。</summary>
        public readonly HashSet<int> SelNodes = new(), SelMembers = new();
        private readonly HashSet<int> _hlNodes = new(), _hlMembers = new();
        private bool _boxing;
        private Vector2 _boxStart;
        /// <summary>框选矩形变化（null 表示隐藏）。</summary>
        public event Action<Rect2?> BoxChanged;
        public int MultiSelectionCount => SelNodes.Count + SelMembers.Count;

        private Rect2 BoxRect() => new Rect2(new Vector2(Mathf.Min(_boxStart.X, _lastMouse.X), Mathf.Min(_boxStart.Y, _lastMouse.Y)),
                                              (_lastMouse - _boxStart).Abs());

        private void FinishBox()
        {
            _boxing = false;
            BoxChanged?.Invoke(null);
            var rect = BoxRect();
            SelNodes.Clear(); SelMembers.Clear();
            if (rect.Size.X < 4 && rect.Size.Y < 4) { Recolor(); SelectionChanged?.Invoke(); return; }  // 只是单击空白
            foreach (var (id, pos) in _renderer.NodePositions())
            {
                var n = Editor.Design.FindNode(id);
                if (n == null || n.IsAnchor || _camera.IsPositionBehind(pos)) continue;
                if (rect.HasPoint(_camera.UnprojectPosition(pos))) SelNodes.Add(id);
            }
            foreach (var (id, mid) in _renderer.MemberMidpoints())
                if (!_camera.IsPositionBehind(mid) && rect.HasPoint(_camera.UnprojectPosition(mid))) SelMembers.Add(id);
            SelectedNodeId = -1; SelectedMemberId = -1;
            Recolor(); SelectionChanged?.Invoke();
            if (MultiSelectionCount > 0)
                Message?.Invoke($"已框选 {SelMembers.Count} 根构件、{SelNodes.Count} 个节点，点击“删除所选”或按 Delete 删除", false);
        }

        /// <summary>用鼠标射线拾取：优先节点，其次构件实体。</summary>
        private void PickAtMouse(out int nodeId, out int memberId)
        {
            var from = _camera.ProjectRayOrigin(_lastMouse);
            var dir = _camera.ProjectRayNormal(_lastMouse);
            nodeId = _renderer.PickNode(from, dir, NodeSnapRadius);
            memberId = nodeId >= 0 ? -1 : _renderer.PickMember(from, dir);
        }

        // ---------------- 输入 ----------------
        public override void _UnhandledInput(InputEvent e)
        {
            if (!Enabled) return;
            if (e is InputEventMouseMotion mm)
            {
                _lastMouse = mm.Position;
                UpdateHover();
            }
            else if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                _lastMouse = mb.Position;
                if (mb.Pressed) OnLeftPress();
                else OnLeftRelease();
            }
            else if (e is InputEventMouseButton rb && rb.ButtonIndex == MouseButton.Right && rb.Pressed)
            {
                if (_chainStart >= 0) CancelChain();   // right click cancels the current connection (drag still orbits)
                // Double right click in build / delete mode returns to the select / drag tool
                if (rb.DoubleClick && (Tool == BuildTool.Build || Tool == BuildTool.Delete))
                {
                    SetTool(BuildTool.Select);
                    Message?.Invoke("已回到选择 / 拖动模式", false);
                }
            }
            else if (e is InputEventKey k && k.Pressed && !k.Echo)
            {
                if (k.CtrlPressed && k.Keycode == Key.Z) { if (Editor.Undo()) Message?.Invoke("已撤销", false); }
                else if (k.CtrlPressed && (k.Keycode == Key.Y || (k.ShiftPressed && k.Keycode == Key.Z))) { if (Editor.Redo()) Message?.Invoke("已重做", false); }
                else if (k.Keycode == Key.Escape) { CancelChain(); ClearSelection(); }
                else if (k.Keycode == Key.Delete || k.Keycode == Key.Backspace) DeleteSelected();
                else if (k.Keycode == Key.X)
                {
                    // With a multi-selection, X deletes it directly instead of switching to the delete tool
                    if (MultiSelectionCount > 0) DeleteSelected();
                    else SetTool(BuildTool.Delete);
                }
                else if (k.Keycode >= Key.Key0 && k.Keycode <= Key.Key9)
                {
                    // Number keys follow the (customizable) material hotkeys; 1 = select unless a material uses it
                    string digit = ((int)(k.Keycode - Key.Key0)).ToString();
                    var keys = (Core.GameManager.Instance?.Settings ?? new Core.GameSettings()).ResolveKeys(MaterialOrder);
                    var mat = keys.FirstOrDefault(p => p.Value == digit).Key;
                    if (mat != null) SetTool(BuildTool.Build, mat);
                    else if (digit == "1") SetTool(BuildTool.Select);
                }
            }
        }

        private bool MouseToPlane(out Vector2 p)
        {
            p = Vector2.Zero;
            if (_camera == null) return false;
            var from = _camera.ProjectRayOrigin(_lastMouse);
            var dir = _camera.ProjectRayNormal(_lastMouse);
            var hit = new Plane(new Vector3(0, 0, 1), BuildPlane.FrontZ).IntersectsRay(from, dir);
            if (hit == null) return false;
            p = new Vector2(hit.Value.X, hit.Value.Y);
            return true;
        }

        /// <summary>吸附：优先吸附到附近已有节点，否则吸附到网格。</summary>
        private Vector2 SnapPoint(Vector2 p, out BridgeNode snappedNode, int ignoreNodeId = -1)
        {
            // Default: snap to the nearest node within the radius; holding Ctrl places on the grid only
            snappedNode = Input.IsKeyPressed(Key.Ctrl) ? null : Editor.NodeAt(p.X, p.Y, NodeSnapRadius,ignoreNodeId>=0?ignoreNodeId:_dragNode);
            if (snappedNode != null) return new Vector2((float)snappedNode.Position.X, (float)snappedNode.Position.Y);
            return new Vector2((float)DesignEditor.Snap(p.X), (float)DesignEditor.Snap(p.Y));
        }

        private string MaterialForTool() => Tool == BuildTool.Build ? ToolMaterial : null;

        private void UpdateHover()
        {
            if (!MouseToPlane(out var p)) return;
            int oldN = _hoverNode, oldM = _hoverMember;
            if (_boxing) BoxChanged?.Invoke(BoxRect());
            if (Tool == BuildTool.Select || Tool == BuildTool.Delete)
            {
                // 选择 / 删除：按鼠标射线拾取 3D 构件实体
                PickAtMouse(out _hoverNode, out _hoverMember);
            }
            else
            {
                var n = Editor.NodeAt(p.X, p.Y, NodeSnapRadius);
                _hoverNode = n?.Id ?? -1; _hoverMember = -1;
            }

            var snapped = SnapPoint(p, out var sn);
            string mat = MaterialForTool();
            _previewBeam.Visible = false;
            _cursor.Visible = false;
            _ring.Visible = mat != null && sn != null && _dragNode < 0;
            if (_ring.Visible) _ring.Position = new Vector3((float)sn.Position.X, (float)sn.Position.Y, BuildPlane.FrontZ + 0.05f);
            string text = null; bool ok = true;

            if (_dragNode >= 0)
            {
                string reason = Editor.CheckNodePosition(snapped.X, snapped.Y, _dragNode);
                ok = reason == null;
                _cursor.Visible = true; _cursor.MaterialOverride = ok ? _mm.PreviewOk : _mm.PreviewBad;
                _cursor.Position = new Vector3(snapped.X, snapped.Y, BuildPlane.FrontZ);
                text = ok ? $"移动到 ({snapped.X:0.00}, {snapped.Y:0.00})" : reason;
            }
            else if (mat != null && Editor.Db.GetMaterial(mat).IsPier)
            {
                // 桥墩工具：点击河道中的位置（或桥面节点）从河床立起桥墩
                double px = sn != null ? sn.Position.X : snapped.X, py = sn != null ? sn.Position.Y : snapped.Y;
                string why = Editor.PreviewPier(px, py, mat, out double ph, out double pc);
                ok = why == null;
                _cursor.Visible = true; _cursor.MaterialOverride = ok ? _mm.PreviewOk : _mm.PreviewBad;
                _cursor.Position = new Vector3((float)px, (float)py, BuildPlane.FrontZ);
                var zone = Editor.Level.Terrain.ZoneAt(px);
                text = ok ? $"桥墩 高 {ph:0.0} m · ${pc:0}（{zone.Name}，浅基础）" + (sn != null ? "\n墩顶直接顶住这个节点" : "") : $"✗ {why}";
            }
            else if (mat != null && _chainStart >= 0)
            {
                var start = Editor.Design.FindNode(_chainStart);
                MemberPreview pv = sn != null && sn.Id != _chainStart
                    ? Editor.PreviewMember(_chainStart, sn.Id, mat)
                    : Editor.PreviewMemberToPoint(_chainStart, snapped.X, snapped.Y, mat);
                ok = pv.Allowed;
                var a = new Vector3((float)start.Position.X, (float)start.Position.Y, BuildPlane.FrontZ);
                var b = new Vector3(snapped.X, snapped.Y, BuildPlane.FrontZ);
                var md = Editor.Db.GetMaterial(mat);
                BridgeRenderer.SetBeam(_previewBeam, a, b, (float)Math.Max(md.VisualThickness, 0.1), (float)Math.Max(md.VisualThickness, 0.1));
                _previewBeam.Visible = pv.Length > 0.01;
                _previewBeam.MaterialOverride = ok ? _mm.PreviewOk : _mm.PreviewBad;
                // 默认只显示长度和费用；不能放置时写明具体原因
                // 桥面：显示坡度，超过建议值时提前提示
                string slopeTxt = "";
                if (Editor.Db.GetMaterial(mat).IsDeck && Math.Abs(b.X - a.X) > 0.05)
                {
                    double sl = Math.Abs((b.Y - a.Y) / (b.X - a.X)), lim = Editor.Level.Objectives.MaxDeckSlope;
                    slopeTxt = $" · 坡度 {sl * 100:0}%" + (sl > lim * 2 + 0.005 ? "（太陡，车辆爬不上去）" : sl > lim + 0.005 ? $"（超过建议 {lim * 100:0}%，车辆会减速）" : "");
                }
                text = ok ? $"{pv.Length:0.0} m · ${pv.Cost:0}" + slopeTxt
                            + (pv.Cost > Editor.Remaining + 1e-6 ? " · 将超预算" : "")
                            + (pv.Warning != null ? $"\n⚠ {pv.Warning}" : "")
                          : $"✗ {pv.Reason}";
            }
            else if (mat != null)
            {
                // 尚未选择起点：构件必须从已有节点（锚点或已建节点）开始
                ok = sn != null;
                _cursor.Visible = true; _cursor.MaterialOverride = ok ? _mm.PreviewOk : _mm.PreviewBad;
                _cursor.Position = new Vector3(snapped.X, snapped.Y, BuildPlane.FrontZ);
                text = ok ? $"起点：节点 #{sn.Id}" : "请从已有节点（锚点或已建节点）开始建造";
            }
            Preview?.Invoke(text, _lastMouse, ok);
            if (oldN != _hoverNode || oldM != _hoverMember) Recolor();
        }

        private void OnLeftPress()
        {
            if (!MouseToPlane(out var p)) return;
            var snapped = SnapPoint(p, out var sn);
            string mat = MaterialForTool();
            switch (Tool)
            {
                case BuildTool.Select:
                {
                    PickAtMouse(out int nid, out int mid);
                    SelNodes.Clear(); SelMembers.Clear();
                    if (nid >= 0)
                    {
                        SelectedNodeId = nid; SelectedMemberId = -1;
                        var n = Editor.Design.FindNode(nid);
                        if (n != null && !n.IsAnchor) _dragNode = nid;
                    }
                    else if (mid >= 0) { SelectedMemberId = mid; SelectedNodeId = -1; }
                    else
                    {
                        // 点在空白处：开始框选
                        SelectedNodeId = -1; SelectedMemberId = -1;
                        _boxing = true; _boxStart = _lastMouse;
                    }
                    Recolor(); SelectionChanged?.Invoke();
                    break;
                }
                case BuildTool.Delete:
                {
                    // 直接点击构件 / 节点实体即删除；点在空白处拖动可框选
                    PickAtMouse(out int nid, out int mid);
                    if (nid >= 0) { Report(Editor.DeleteNode(nid), "已删除节点及其相连构件"); break; }
                    if (mid >= 0)
                    {
                        var m = Editor.Design.FindMember(mid);
                        Report(Editor.DeleteMember(mid), $"已删除构件，退还 ${m?.EffectiveCost() ?? 0:0}");
                        break;
                    }
                    SelNodes.Clear(); SelMembers.Clear();
                    _boxing = true; _boxStart = _lastMouse;
                    break;
                }
                default:
                {
                    if (mat == null) break;
                    // 按在普通节点上：先不执行点击，等待判断是“点击”还是“长按拖动”
                    if (sn != null && !sn.IsAnchor)
                    {
                        _pressNode = sn.Id; _pressTime = 0; _pressMouse = _lastMouse;
                        break;
                    }
                    BuildClick(snapped, sn, mat);
                    break;
                }
            }
            UpdateHover();
        }

        // ---------------- 长按拖动节点 ----------------
        private const double LongPressTime = 0.35;   // 长按判定时间 s
        private const float DragStartPixels = 8f;    // 按住后移动超过该距离也视为拖动
        private int _pressNode = -1;
        private double _pressTime;
        private Vector2 _pressMouse;

        public override void _Process(double delta)
        {
            if (_pressNode < 0) return;
            if (!Enabled || !Input.IsMouseButtonPressed(MouseButton.Left)) { _pressNode = -1; return; }
            _pressTime += delta;
            if (_pressTime >= LongPressTime || _lastMouse.DistanceTo(_pressMouse) > DragStartPixels)
            {
                _dragNode = _pressNode; _pressNode = -1;
                Message?.Invoke("拖动节点：松开左键放下（连接的构件会自动更新长度和费用）", false);
                UpdateHover();
            }
        }

        /// <summary>建造工具的点击：选择起点，或连接到终点（已有节点或新位置）。</summary>
        private void BuildClick(Vector2 snapped, BridgeNode sn, string mat)
        {
            if (Editor.Db.GetMaterial(mat).IsPier)
            {
                double px = sn != null ? sn.Position.X : snapped.X, py = sn != null ? sn.Position.Y : snapped.Y;
                var pr = Editor.AddPier(px, py, mat);
                Message?.Invoke(pr.Ok ? pr.Message + "。选中墩底节点可改为桩基础" : pr.Message, !pr.Ok);
                return;
            }
            {
                {
                    if (_chainStart < 0)
                    {
                        // 选择起点：只能是已有节点（不允许凭空创建孤立节点）
                        if (sn == null) { Message?.Invoke("构件必须从已有节点（锚点或已建节点）开始", true); return; }
                        _chainStart = sn.Id;
                        Recolor();
                    }
                    else
                    {
                        BuildResult r;
                        int endNode;
                        if (sn != null)
                        {
                            if (sn.Id == _chainStart) { CancelChain(); return; }
                            r = Editor.AddMember(_chainStart, sn.Id, mat);
                            endNode = sn.Id;
                        }
                        else
                        {
                            r = Editor.AddMemberToNewNode(_chainStart, snapped.X, snapped.Y, mat);
                            endNode = r.Ok ? Editor.Design.FindMember(r.CreatedId).EndNodeId : -1;
                        }
                        if (r.Ok)
                        {
                            _chainStart = endNode;
                            var built = Editor.Design.FindMember(r.CreatedId);
                            var w = built == null ? null : Editor.PreviewMember(built.StartNodeId, built.EndNodeId, built.MaterialId).Warning;
                            Message?.Invoke(w != null ? $"已建造 {Editor.Db.GetMaterial(mat).Name}，注意：{w}" : $"已建造 {Editor.Db.GetMaterial(mat).Name}", w != null);
                            Recolor();
                        }
                        else Message?.Invoke(r.Message, true);
                    }
                }
            }
        }

        private void OnLeftRelease()
        {
            if (_boxing) { FinishBox(); return; }
            if (_pressNode >= 0)
            {
                // 短按：执行原本的点击（选为起点或连接到该节点）
                var n = Editor.Design.FindNode(_pressNode);
                _pressNode = -1;
                string mat = MaterialForTool();
                if (n != null && mat != null) BuildClick(new Vector2((float)n.Position.X, (float)n.Position.Y), n, mat);
                UpdateHover();
                return;
            }
            if (_dragNode < 0) return;
            int id = _dragNode; _dragNode = -1;
            if (MouseToPlane(out var p))
            {
                var node = Editor.Design.FindNode(id);
                var snapped = SnapPoint(p,out _,id);
                if (node != null && (Math.Abs(node.Position.X - snapped.X) > 1e-3 || Math.Abs(node.Position.Y - snapped.Y) > 1e-3))
                {
                    var r = Editor.MoveNode(id, snapped.X, snapped.Y);
                    Report(r, "节点已移动，构件长度与费用已更新");
                }
            }
            UpdateHover();
        }

        /// <summary>伸缩所选液压杆。</summary>
        public void ResizeSelected(double delta)
        {
            if (SelectedMemberId < 0) return;
            Report(Editor.ResizeMember(SelectedMemberId, delta), delta > 0 ? "液压杆已伸长" : "液压杆已缩短");
        }

        public void DeleteSelected()
        {
            if (MultiSelectionCount > 0)
            {
                var r = Editor.DeleteMany(SelNodes, SelMembers);
                SelNodes.Clear(); SelMembers.Clear();
                Report(r, r.Message);
                return;
            }
            if (SelectedMemberId >= 0) Report(Editor.DeleteMember(SelectedMemberId), "已删除构件");
            else if (SelectedNodeId >= 0) Report(Editor.DeleteNode(SelectedNodeId), "已删除节点");
        }

        private void CancelChain()
        {
            if (_chainStart < 0) return;
            _chainStart = -1;
            if (_previewBeam != null) _previewBeam.Visible = false;
            Recolor();
        }

        private void Report(BuildResult r, string okText)
        {
            if (r.Ok) { if (!string.IsNullOrEmpty(okText)) Message?.Invoke(okText, false); }
            else Message?.Invoke(r.Message, true);
        }

        private static Mesh BuildGrid(LevelDefinition level)
        {
            var t = level.Terrain;
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            float z = BuildPlane.FrontZ - 0.01f;
            float x0 = (float)-t.BuildMarginX, x1 = (float)(t.RiverWidth + t.BuildMarginX);
            float y0 = (float)t.BuildMinY, y1 = (float)t.BuildMaxY;
            for (float x = Mathf.Ceil(x0/(float)DesignEditor.GridSize)*(float)DesignEditor.GridSize; x <= x1 + 1e-3f; x += (float)DesignEditor.GridSize) { im.SurfaceSetColor(new Color(0.30f,0.24f,0.15f,Mathf.IsEqualApprox(x,Mathf.Round(x))?0.3f:0.12f)); im.SurfaceAddVertex(new Vector3(x, y0, z)); im.SurfaceAddVertex(new Vector3(x, y1, z)); }
            for (float y = Mathf.Ceil(y0/(float)DesignEditor.GridSize)*(float)DesignEditor.GridSize; y <= y1 + 1e-3f; y += (float)DesignEditor.GridSize) { im.SurfaceSetColor(new Color(0.30f,0.24f,0.15f,Mathf.IsEqualApprox(y,Mathf.Round(y))?0.3f:0.12f)); im.SurfaceAddVertex(new Vector3(x0, y, z)); im.SurfaceAddVertex(new Vector3(x1, y, z)); }
            im.SurfaceEnd();
            return im;
        }
    }
}
