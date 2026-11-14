using System;
using System.Linq;
using BridgeArchitect3D.Core;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.UI;
using Godot;

namespace BridgeArchitect3D.Tools
{
    public partial class MaterialInfoPreview : Node3D
    {
        private GameDatabase _db;
        private LevelDefinition _level;
        private DesignEditor _editor;
        private BridgeRenderer _renderer;
        private GameHUD _hud;
        private Camera3D _camera;
        private LevelManager _controller;
        private int _index,_frames;
        private string _output;
        public override void _Ready()
        {
            SaveManager.WritesSuspended=true;_db=GameManager.Instance.Db;_level=_db.GetLevel("level14");
            var args=OS.GetCmdlineUserArgs();int at=Array.IndexOf(args,"--info-shots");if(at>=0 && at+1<args.Length)_output=args[at+1];
            AddChild(new WorldEnvironment{Environment=new Godot.Environment{BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color("b3b5aa"),AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=Colors.White,AmbientLightEnergy=.7f}});
            AddChild(new DirectionalLight3D{RotationDegrees=new(-40,-30,0),LightEnergy=1.1f,ShadowEnabled=false});
            _camera=new Camera3D{Current=true,Projection=Camera3D.ProjectionType.Orthogonal,Size=22,Far=500};AddChild(_camera);
            _renderer=new BridgeRenderer{Level=_level};AddChild(_renderer);_renderer.Init(new MaterialManager(_db));
            var canvas=new CanvasLayer();AddChild(canvas);_hud=new GameHUD();canvas.AddChild(_hud);_controller=new LevelManager();
            _hud.Init(_controller,"配重与实时信息预览","",_db.Materials.Values.ToArray(),_db.Materials.Values.ToArray());
            ShowSample();
        }
        private void ShowSample()
        {
            _editor=new DesignEditor(null,_level,_db);var r=_editor.AddTunnel(0,_level.Terrain.RiverWidth,"tunnel_floating");
            if(!r.Ok)throw new InvalidOperationException(r.Message);
            string buoy=new[]{"low","mid","high"}[_index%3];_editor.SetTunnelBuoyancy(-1,buoy);_editor.SetTunnelGrade(-1,"heavy");_renderer.Rebuild(_editor.Design);_renderer.SetCutaway(true);
            var g=_editor.TunnelGeo;int selected=g.Count/2;
            var st=BridgeSimulation.StaticTunnel(_editor.Design,_level,_db);
            _hud.ShowTunnelProperties(g,selected,st,_level,_db.TunnelConfig,"静态工况："+g.Buoy[selected].Name);
            var target=new Vector3((float)g.MidX(selected)-2,(float)g.Y0[selected]+1,0);
            _camera.Position=target+new Vector3(12,7,45);_camera.LookAt(target);
            int CountBlocks(Node n)=>n.GetChildren().Sum(c=>(c.Name.ToString().StartsWith("BallastBlock")?1:0)+CountBlocks(c));
            int blocks=CountBlocks(_renderer);
            int expected=Enumerable.Range(0,g.Count).Sum(i=>2*TunnelBallastLayout.Columns(g.Len[i])*TunnelBallastLayout.Layers(g.Buoy[i].Ratio));
            // QueueFree removes the previous mesh subtree at frame end; validate only after a frame has passed.
            GD.Print($"Ballast preview {buoy}: expected {expected} blocks; current tree {blocks}");
            _frames=0;
        }
        public override void _Process(double delta)
        {
            if(_output==null || ++_frames<24)return;            var g=_editor.TunnelGeo;
            int CountBlocks(Node n)=>n.GetChildren().Sum(c=>(c.Name.ToString().StartsWith("BallastBlock_")?1:0)+CountBlocks(c));
            int expected=Enumerable.Range(0,g.Count).Sum(i=>2*TunnelBallastLayout.Columns(g.Len[i])*TunnelBallastLayout.Layers(g.Buoy[i].Ratio));
            if(CountBlocks(_renderer)!=expected)throw new InvalidOperationException("Ballast visual count does not match buoyancy level.");
            GD.Print("Ballast block count verified: "+expected);
            if(DisplayServer.GetName()!="headless")GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_output,"ballast-"+_index+".png"));
            if(++_index==3){GetTree().Quit();return;}ShowSample();
        }
        public override void _ExitTree(){_controller?.Free();}
    }
}
