using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.World;
using BridgeArchitect3D.UI;
using Godot;

namespace BridgeArchitect3D.Building
{
    /// <summary>Screen-space route guidance, independent of zoom and terrain occlusion.</summary>
    public partial class TunnelBuildGuide : Control
    {
        private readonly List<(Vector3 world, string text, Color color)> _markers = new();
        private readonly List<Label> _labels = new();
        private readonly List<Vector3> _route = new();
        private readonly Label _instruction = new();
        private readonly HBoxContainer _actions = new();
        private readonly List<(Vector3 world,int id,bool control)> _handles = new();
        private TunnelGeometry _geometry;
        private int _hoverNode = -1, _selectedNode = -1, _hoverElement = -1;
        public event Action<string> FocusRequested;
        private Camera3D _camera;
        private bool _propertiesOpen;
        private static readonly Color Entry = new("17765f"), Exit = new("a65813"), Invalid = new("c43c3c");

        public override void _Ready()
        {
            Theme=UiTheme.Get();
            MouseFilter = MouseFilterEnum.Ignore;
            ConfigureLabel(_instruction);
            AddChild(_instruction);
            foreach(var item in new[]{(Loc.T("入口"),"entry"),(Loc.T("管体近景"),"tube"),(Loc.T("出口"),"exit"),(Loc.T("全线"),"route")})
            {
                string destination=item.Item2;
                _actions.AddChild(UiTheme.MakeButton(Loc.T(item.Item1),()=>FocusRequested?.Invoke(destination),80));
            }
            AddChild(_actions);
        }

        private static void ConfigureLabel(Label label)
        {
            label.MouseFilter = MouseFilterEnum.Ignore;
            label.AddThemeFontSizeOverride("font_size", 13);
            label.AddThemeColorOverride("font_outline_color", new Color("f7f2e8"));
            label.AddThemeConstantOverride("outline_size", 5);
        }

        public void ShowRoute(Camera3D camera, LevelDefinition level, TunnelTypeConfig type,
            TunnelGeometry geometry, double first, double cursor, bool valid, bool propertiesOpen,
            IEnumerable<BridgeNode> nodes=null,int hoverNode=-1,int selectedNode=-1,int hoverElement=-1,bool editingTube=false,BuildTool tool=BuildTool.Select)
        {
            _camera = camera; _propertiesOpen = propertiesOpen; _markers.Clear(); _route.Clear();
            _handles.Clear();_hoverNode=hoverNode;_selectedNode=selectedNode;_hoverElement=hoverElement;
            if(nodes!=null)foreach(var node in nodes.Where(n=>n.IsTunnelNode))_handles.Add((World(node.Position.X,node.Position.Y),node.Id,node.TunnelPoint));
            Visible = type != null;
            if (!Visible) return;
            bool built = geometry != null;
            if (!built)
            {
                geometry = TunnelAccessPlan.Preview(level,type,first,cursor);
            }
            _geometry=geometry;
            string routing=built&&TunnelRules.Complete(geometry,level.Terrain)?Loc.T("主路汽车优先走可用隧道；火车走桥"):Loc.T("隧道贯通后主路汽车优先走隧道；火车走桥");
            _instruction.Text = built ? (editingTube?Loc.T("上下拖动管节接缝上的圆点调整埋深（两端贴合岸壁）；切换‘选择/拖动’后可设置管节和电梯\n")
                    :Loc.T("青色圆点：拖动（两端只能上下）　橙色方点：连接混凝土　点击管节：设置等级\n"))+Loc.T("两岸垂直电梯自动连接 · ")+routing
                : Loc.T("点击一次即建成整条隧道：两端贴合岸壁，两岸自动配垂直电梯 · Esc / 右键取消");
            if(built&&tool==BuildTool.Build&&!editingTube)
                _instruction.Text=Loc.T("当前为结构建材：橙色方点连接混凝土；切换‘选择/拖动’后可设置管节和电梯\n")+routing;
            if(built&&tool==BuildTool.Delete)
                _instruction.Text=Loc.T("删除工具：点管体拆除隧道和两岸电梯；操作可撤销\n青色圆点是路线控制点；橙色方点是结构支点");
            _instruction.AddThemeColorOverride("font_color", new Color("263645"));
            var access=new TunnelAccessPlan(level,geometry);
            foreach (var p in access.LeftRamp.Concat(access.Route.Where(p=>p.x>geometry.Points[0].X&&p.x<geometry.Points[^1].X)).Concat(access.RightRamp)) _route.Add(World(p.x, p.y));
            Mark(access.Entry.X,access.Entry.Y,Loc.F("下行电梯 {0:0} m", (geometry.LiftLength)),Entry);
            Mark(access.Exit.X,access.Exit.Y,Loc.F("上行电梯 {0:0} m", (geometry.LiftLength)),Exit);
            var a = geometry.Points[0]; var b = geometry.Points[^1];
            var cyan=new Color("1696b2");
            Mark(a.X,a.Y,built?Loc.T("左端 ↕"):Loc.T("左端贴合岸壁"),valid?cyan:Invalid);
            Mark(b.X,b.Y,built?Loc.T("右端 ↕"):Loc.T("右端贴合岸壁"),valid?cyan:Invalid);
            if(built&&(!access.LeftConnected||!access.RightConnected))
            {
                TunnelRules.FlatBed(level.Terrain,out double left,out double right);
                if(!access.LeftConnected)Mark(left,a.Y,Loc.T("左岸接入点"),cyan);
                if(!access.RightConnected)Mark(right,b.Y,Loc.T("右岸接入点"),cyan);
                _instruction.Text=Loc.T("隧道两端会自动贴合岸壁");
            }
            while (_labels.Count < _markers.Count)
            {
                var label = new Label(); ConfigureLabel(label); AddChild(label); _labels.Add(label);
            }
            for (int i = 0; i < _labels.Count; i++)
            {
                _labels[i].Visible = i < _markers.Count;
                if (i < _markers.Count) { _labels[i].Text = _markers[i].text; _labels[i].AddThemeColorOverride("font_color", _markers[i].color); }
            }
            UpdatePositions();
        }

        private static Vector3 World(double x, double y) => new((float)x, (float)y, BuildPlane.FrontZ);
        private void Mark(double x, double y, string text, Color color) => _markers.Add((World(x, y), text, color));

        public override void _Process(double delta)
        {
            if (Visible && _camera != null) UpdatePositions();
        }

        private void UpdatePositions()
        {
            Size = GetViewportRect().Size;
            float left = Size.X >= 1050 ? 200 : 12, right = _propertiesOpen ? 320 : 12;
            float usableRight = Mathf.Max(left + 120, Size.X - right);
            _instruction.Position = new Vector2(Mathf.Max(left, (left + usableRight - _instruction.GetMinimumSize().X) / 2), Size.Y - 143);
            _actions.Position=new Vector2(Mathf.Max(left,(left+usableRight-_actions.GetCombinedMinimumSize().X)/2),Size.Y-184);
            for (int i = 0; i < _markers.Count; i++)
            {
                var marker = _markers[i]; var label = _labels[i];
                label.Visible = !_camera.IsPositionBehind(marker.world);
                if (!label.Visible) continue;
                var p = _camera.UnprojectPosition(marker.world);
                var min = label.GetMinimumSize();
                bool off = p.X < left || p.X > usableRight;
                label.Text = off ? (p.X < left ? "← " : "→ ") + marker.text : marker.text;
                // Alternate the two fixed-end labels to prevent overlap with the selected marker.
                float lift = i == 4 ? 54 : i == 3 ? -10 : i == 2 ? 27 : 20;
                float baseY = Mathf.Clamp(p.Y, 145, Mathf.Max(145, Size.Y - 174));
                label.Position = new Vector2(Mathf.Clamp(p.X - min.X / 2, left, Mathf.Max(left, usableRight - min.X)),
                    Mathf.Clamp(baseY - lift, 115, Mathf.Max(115, Size.Y - 148)));
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (_camera == null) return;
            var bounds = new Rect2(Vector2.Zero, Size);
            for (int i = 0; i + 1 < _route.Count; i++)
            {
                if (_camera.IsPositionBehind(_route[i]) || _camera.IsPositionBehind(_route[i + 1])) continue;
                var a = _camera.UnprojectPosition(_route[i]); var b = _camera.UnprojectPosition(_route[i + 1]);
                // Avoid unbounded dashed-line tessellation for long, off-screen ramps.
                var clipped = Geometry2D.IntersectPolylineWithPolygon(new[] { a, b }, new[] { bounds.Position, new Vector2(Size.X, 0), Size, new Vector2(0, Size.Y) });
                foreach (var line in clipped)
                    for (int j = 0; j + 1 < line.Length; j++) DrawDashedLine(line[j], line[j + 1], new Color(0.1f, 0.48f, 0.55f, 0.65f), 2, 7);
                var mid = (a + b) / 2; var direction = (b - a).Normalized();
                if (bounds.HasPoint(mid) && a.DistanceTo(b) > 50)
                {
                    var normal = new Vector2(-direction.Y, direction.X);
                    DrawLine(mid - direction * 7 + normal * 4, mid, Entry, 2, true);
                    DrawLine(mid - direction * 7 - normal * 4, mid, Entry, 2, true);
                }
            }
            if(_geometry!=null&&_hoverElement>=0&&_hoverElement<_geometry.Count)
            {
                int i=_hoverElement;float h=(float)_geometry.Type.TubeHeight;
                var polygon=new[]{World(_geometry.X0[i],_geometry.Y0[i]),World(_geometry.X1[i],_geometry.Y1[i]),World(_geometry.X1[i],_geometry.Y1[i]+h),World(_geometry.X0[i],_geometry.Y0[i]+h)};
                if(polygon.All(p=>!_camera.IsPositionBehind(p)))
                {var projected=polygon.Select(_camera.UnprojectPosition).ToArray();DrawColoredPolygon(projected,new Color(0.1f,0.75f,0.9f,0.16f));DrawPolyline(projected.Append(projected[0]).ToArray(),new Color("1696b2"),2,true);}
            }
            for(int i=0;i<_markers.Count;i++)
            {
                var marker=_markers[i];
                if (_camera.IsPositionBehind(marker.world)) continue;
                var p = _camera.UnprojectPosition(marker.world);
                if (!bounds.HasPoint(p)) continue;
                if(i<2)
                {DrawPolyline(new[]{p+new Vector2(-6,5),p+new Vector2(-6,-7),p+new Vector2(6,-7),p+new Vector2(6,5)},marker.color,3,true);}
                else
                {DrawCircle(p,11,marker.color,false,2,true);DrawCircle(p,4,marker.color,true,-1,true);}
            }
            foreach(var handle in _handles)
            {
                if(_camera.IsPositionBehind(handle.world))continue;
                var p=_camera.UnprojectPosition(handle.world);if(!bounds.HasPoint(p))continue;
                var color=new Color(handle.control?"21c6e1":"e8a13e");
                float radius=handle.id==_hoverNode||handle.id==_selectedNode?13:9;
                DrawCircle(p,radius+3,new Color(0.05f,0.1f,0.15f,0.75f),true,-1,true);
                if(handle.control){DrawCircle(p,radius,color,false,2,true);DrawCircle(p,4,color,true,-1,true);}
                else{DrawRect(new Rect2(p-Vector2.One*6,Vector2.One*12),color,false,2,true);DrawCircle(p,2,color,true,-1,true);}
            }
        }
    }
}
