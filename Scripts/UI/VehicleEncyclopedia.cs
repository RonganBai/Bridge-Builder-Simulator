using System;
using System.Linq;
using System.Collections.Generic;
using BridgeArchitect3D.Core;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Traffic;
using Godot;

namespace BridgeArchitect3D.UI
{
    /// <summary>A data-driven catalogue with its own isolated, interactive 3D viewport.</summary>
    public partial class VehicleEncyclopedia : Control
    {
        private GameDatabase _db;
        private VehicleDefinition[] _all;
        private readonly List<VehicleDefinition> _shown=new();
        private ItemList _list;
        private LineEdit _search;
        private OptionButton _category;
        private Label _title,_count;
        private RichTextLabel _details;
        private SubViewport _viewport;
        private Node3D _stage;
        private VehicleController _model;
        private Camera3D _camera;
        private CheckBox _spin,_lamps;
        private Button _variant;
        private float _angle=0.5f,_pitch=0.4f,_zoom=1;
        private Vector3 _target;
        private float _distance;
        private double _time;
        private long _trip;
        private bool _dragging,_smoke;
        private int _smokeFrames,_smokeIndex;
        private string _shots;
        public static VehicleEncyclopedia Create(Node parent,GameDatabase db)
        {
            var ui=new VehicleEncyclopedia{_db=db,Visible=false};parent.AddChild(ui);return ui;
        }
        public override void _Ready()
        {
            Theme=UiTheme.Get();SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);MouseFilter=MouseFilterEnum.Stop;ZIndex=100;
            var bg=new PanelContainer();bg.AddThemeStyleboxOverride("panel",UiTheme.Box(UiTheme.Bg,0,null,18));AddChild(bg);bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var body=new VBoxContainer();body.AddThemeConstantOverride("separation",10);bg.AddChild(body);
            var header=new HBoxContainer();body.AddChild(header);
            header.AddChild(UiTheme.MakeLabel("载具图鉴",25,UiTheme.Accent));
            _count=UiTheme.MakeLabel("",13,UiTheme.Muted);_count.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(_count);
            header.AddChild(UiTheme.MakeButton("返回主菜单",()=>Hide(),130));
            var filters=new HBoxContainer();body.AddChild(filters);
            _search=new LineEdit{PlaceholderText="搜索名称、用途或特点",SizeFlagsHorizontal=SizeFlags.ExpandFill};filters.AddChild(_search);
            _category=new OptionButton{CustomMinimumSize=new(170,0)};filters.AddChild(_category);_category.AddItem("全部分类");
            _all=_db.Vehicles.Values.ToArray();foreach(var cat in _all.Select(v=>v.Category).Distinct())_category.AddItem(cat);
            _search.TextChanged+=_=>Filter();_category.ItemSelected+=_=>Filter();
            var split=new HSplitContainer{SizeFlagsVertical=SizeFlags.ExpandFill,SplitOffsets=new int[]{245}};body.AddChild(split);
            _list=new ItemList{CustomMinimumSize=new(220,0),SizeFlagsVertical=SizeFlags.ExpandFill,AutoTranslateMode=AutoTranslateModeEnum.Disabled};split.AddChild(_list);_list.ItemSelected+=index=>Select(_shown[(int)index]);
            var right=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};right.AddThemeConstantOverride("separation",8);split.AddChild(right);
            _title=UiTheme.MakeLabel("",20);right.AddChild(_title);
            var canvas=new SubViewportContainer{Stretch=true,CustomMinimumSize=new(240,190),SizeFlagsVertical=SizeFlags.ExpandFill,SizeFlagsStretchRatio=1.2f,MouseFilter=MouseFilterEnum.Stop};right.AddChild(canvas);
            _viewport=new SubViewport{OwnWorld3D=true,TransparentBg=false,Size=new(960,460),RenderTargetUpdateMode=SubViewport.UpdateMode.WhenVisible,Msaa3D=Viewport.Msaa.Msaa2X};canvas.AddChild(_viewport);
            _stage=new Node3D();_viewport.AddChild(_stage);
            _stage.AddChild(new WorldEnvironment{Environment=new Godot.Environment{BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color("283b49"),AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=Colors.White,AmbientLightEnergy=0.72f}});
            _stage.AddChild(new DirectionalLight3D{RotationDegrees=new(-48,-30,0),LightEnergy=1.2f,ShadowEnabled=false});
            _stage.AddChild(new MeshInstance3D{Mesh=new PlaneMesh{Size=new(200,200)},Position=new(0,-0.03f,0),MaterialOverride=new StandardMaterial3D{AlbedoColor=new Color("344b55"),Roughness=1}});
            _camera=new Camera3D{Current=true,Fov=40,Far=400};_stage.AddChild(_camera);canvas.GuiInput+=PreviewInput;
            var controls=new HBoxContainer();right.AddChild(controls);
            _spin=new CheckBox{Text="自动旋转",ButtonPressed=true};controls.AddChild(_spin);
            _lamps=new CheckBox{Text="展示警灯",ButtonPressed=true};controls.AddChild(_lamps);
            _variant=UiTheme.MakeButton("切换外观",()=>_model?.SelectTractorVariant(++_trip),100);controls.AddChild(_variant);
            controls.AddChild(UiTheme.MakeButton("复位视角",()=>{_angle=.5f;_pitch=.4f;_zoom=1;Aim();},100));
            var help=UiTheme.MakeLabel("拖动模型区域旋转 · 滚轮缩放 · Esc 返回",12,UiTheme.Muted);help.ClipText=true;right.AddChild(help);
            _details=new RichTextLabel{BbcodeEnabled=true,ScrollActive=true,CustomMinimumSize=new(0,140),SizeFlagsVertical=SizeFlags.ExpandFill,SizeFlagsStretchRatio=1,SelectionEnabled=true};right.AddChild(_details);
            Filter();
        }
        public void Open()
        {
            Show();_search.GrabFocus();
        }
        private void Filter()
        {
            string query=_search.Text.Trim(),category=_category.Selected==0?null:_category.GetItemText(_category.Selected);
            _shown.Clear();_list.Clear();
            foreach(var v in _all)
                if((category==null || v.Category==category) && (query.Length==0 || (v.Name+v.Purpose+v.Features).Contains(query,StringComparison.OrdinalIgnoreCase)))
                {_shown.Add(v);_list.AddItem(v.Name);}
            _count.Text=$"   共 {_all.Length} 种载具 · 当前 {_shown.Count} 种";
            if(_shown.Count>0){_list.Select(0);Select(_shown[0]);}
            else{_title.Text="没有匹配的载具";_details.Text="试试其他名称或分类。";ClearModel();}
        }
        private void ClearModel()
        {
            if(_model!=null){_stage.RemoveChild(_model);_model.QueueFree();_model=null;}
        }
        private void Select(VehicleDefinition d)
        {
            ClearModel();_model=new VehicleController();_stage.AddChild(_model);_model.Setup(d);
            _model.PlaceAlongPath(x=>new Vector3((float)x,0,0),0);
            _title.Text=d.Name+"  ·  "+d.Category;
            _target=new(-(float)d.Wheelbase/2,Mathf.Min(2,(float)d.Height/2),0);
            _distance=Mathf.Max(5,(float)d.Length*0.8f+3);_zoom=1;_angle=d.Kind=="school_bus"?2.6f:.5f;_pitch=.4f;Aim();
            _variant.Disabled=d.Kind is not ("flatbed" or "semi");
            _lamps.Disabled=d.Kind is not ("ambulance" or "police" or "police_suv" or "fire_engine" or "utility" or "crane" or "garbage" or "pump" or "modular" or "tractor");
            string surface=RailTrack.IsTrain(d)?"铁路 / 公铁复合桥面":"公路 / 公铁复合桥面";
            string Escape(string value)=>(value??"").Replace("[","[lb]");
            var rows=new List<string>{
                $"[b]用途[/b]  {Escape(d.Purpose)}",$"[b]特点与功能[/b]  {Escape(d.Features)}","",
                $"[b]总质量[/b]  {d.Mass/1000:0.##} 吨  ({d.Mass:N0} kg)",
                $"[b]标称速度[/b]  {d.Speed:0.##} m/s  ({d.Speed*3.6:0.#} km/h)",
                $"[b]长 × 宽 × 高[/b]  {d.Length:0.##} × {d.Width:0.##} × {d.Height:0.##} m",
                $"[b]轴距 / 载荷跨度[/b]  {d.Wheelbase:0.##} m   [b]车轴 / 落点[/b]  {d.Axles.Count}",
                $"[b]跟车间距[/b]  {d.FollowGap:0.##} m   [b]坡度上限[/b]  {Math.Atan(d.MaxRampGrade)*180/Math.PI:0.#}° ({d.MaxRampGrade*100:0.#}%)",
                $"[b]通行路面[/b]  {surface}",$"[b]车身颜色[/b]  {Escape(d.Color)}",
                $"[b]车身节数 / 队伍人数[/b]  {(d.Kind is "semi" or "tanker" or "tractor" or "articulated_bus" or "car_carrier"?2:d.Units)}   [b]单节标称长度[/b]  {(d.UnitLength>0?d.UnitLength.ToString("0.##")+" m":"—")}",
                "","[b]各轴 / 落点载荷[/b]  （0 为前轴，负值向后）"};
            for(int i=0;i<d.Axles.Count;i++)
            {
                var a=d.Axles[i];double mass=d.Mass*a.LoadFraction;
                rows.Add($"{i+1:00}   位置 {a.Offset:0.##} m   ·   {a.LoadFraction*100:0.##}%   ·   {mass/1000:0.##} 吨 / {mass*9.81/1000:0.#} kN");
            }
            rows.Add("\n[color=#9daeb8]速度与轴重为游戏设定；坡度、排队和限高会影响实际通行。图鉴中的旋转、警灯和外观切换仅用于预览。[/color]");
            _details.Text=string.Join("\n",rows);_details.ScrollToLine(0);
        }
        private void PreviewInput(InputEvent e)
        {
            if(e is InputEventMouseButton button)
            {
                if(button.ButtonIndex==MouseButton.Left)_dragging=button.Pressed;
                if(button.Pressed && button.ButtonIndex==MouseButton.WheelUp)_zoom=Mathf.Clamp(_zoom*.9f,.45f,2.5f);
                if(button.Pressed && button.ButtonIndex==MouseButton.WheelDown)_zoom=Mathf.Clamp(_zoom*1.1f,.45f,2.5f);
            }
            if(e is InputEventMouseMotion motion && _dragging){_angle-=motion.Relative.X*.008f;_pitch=Mathf.Clamp(_pitch+motion.Relative.Y*.006f,.08f,1.3f);}
            Aim();
        }
        private void Aim()
        {
            if(_camera==null)return;
            float radius=_distance*_zoom;
            _camera.Position=_target+new Vector3(Mathf.Sin(_angle)*Mathf.Cos(_pitch),Mathf.Sin(_pitch),Mathf.Cos(_angle)*Mathf.Cos(_pitch))*radius;
            _camera.LookAt(_target);
        }
        public override void _UnhandledKeyInput(InputEvent e)
        {
            if(Visible && e is InputEventKey{Pressed:true,Keycode:Key.Escape}){Hide();GetViewport().SetInputAsHandled();}
        }
        public void RunSmoke(string shots=null)
        {
            _smoke=true;_shots=shots;Open();
            if(System.Array.IndexOf(OS.GetCmdlineUserArgs(),"--catalog-small")>=0){GetWindow().Mode=Window.ModeEnum.Windowed;GetWindow().Size=new(1280,720);}
            _search.Text="钢卷";Filter();
            if(!_shown.Any(v=>v.Kind=="flatbed"))throw new InvalidOperationException("Catalogue feature search failed.");
            _search.Text="__no_such_vehicle__";Filter();
            if(_shown.Count!=0 || _model!=null)throw new InvalidOperationException("Catalogue empty search failed.");
            _search.Text="";Filter();
            for(int i=1;i<_category.ItemCount;i++)
            {
                _category.Select(i);Filter();
                if(_shown.Count==0 || _shown.Any(v=>v.Category!=_category.GetItemText(i)))
                    throw new InvalidOperationException("Catalogue category filter failed.");
            }
            _category.Select(0);Filter();
            float before=_camera.Position.DistanceTo(_target);
            PreviewInput(new InputEventMouseButton{Pressed=true,ButtonIndex=MouseButton.WheelUp});
            if(_camera.Position.DistanceTo(_target)>=before)throw new InvalidOperationException("Catalogue zoom failed.");
            _zoom=1;Aim();GD.Print("Catalogue search, empty state, category filters and zoom passed.");
        }
        public override void _Process(double delta)
        {
            if(!Visible)return;_time+=delta;
            if(_spin.ButtonPressed && !_dragging){_angle+=(float)delta*.15f;Aim();}
            _model?.SetEmergencyLights(_lamps.ButtonPressed,_time);
            if(!_smoke || ++_smokeFrames%16!=0)return;
            if(_shots!=null && DisplayServer.GetName()!="headless" && (_smokeIndex==0 || _smokeIndex==_all.Length-1))
                GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_shots,"catalog-"+_smokeIndex+".png"));
            GD.Print("Catalogue verified: "+_all[_smokeIndex].Id);
            if(++_smokeIndex==_all.Length){GetTree().Quit();return;}
            _list.Select(_smokeIndex);_list.EnsureCurrentIsVisible();Select(_all[_smokeIndex]);
        }
    }
}
