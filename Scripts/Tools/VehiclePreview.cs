using System;
using System.Linq;
using System.Text.Json;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Traffic;
using Godot;

namespace BridgeArchitect3D.Tools
{
    // Standalone visual inspection scene. F6 opens the gallery; optional --vehicle-shots saves every view.
    public partial class VehiclePreview : Node3D
    {
        private VehicleController _model;
        private Camera3D _camera;
        private VehicleDefinition[] _definitions;
        private string[] _ids={"mixer","coupe","machinery_flatbed","machinery_flatbed","machinery_flatbed","machinery_flatbed","car_carrier","fire_engine","semi_long","utility","crane","dump_loaded","dump_empty","articulated_bus","tanker","tractor_trailer","garbage","concrete_pump","modular_transport","school_bus"};
        private int _index,_frames;
        private string _output;
        public override void _Ready()
        {
            var args=OS.GetCmdlineUserArgs();int at=Array.IndexOf(args,"--vehicle-shots");
            if(at>=0 && at+1<args.Length)_output=args[at+1];
            using var json=JsonDocument.Parse(FileAccess.GetFileAsString("res://Data/Vehicles/vehicles.json"));
            _definitions=json.RootElement.GetProperty("Vehicles").EnumerateArray().Select(e=>JsonSerializer.Deserialize<VehicleDefinition>(e.GetRawText())).ToArray();
            if (Array.IndexOf(args, "--all-vehicles") >= 0)
                _ids = _definitions.Where(d => d.Kind != "pedestrians").Select(d => d.Id).ToArray();
            AddChild(new WorldEnvironment{Environment=new Godot.Environment{BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color("a9b9bd"),AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=Colors.White,AmbientLightEnergy=0.65f}});
            AddChild(new DirectionalLight3D{RotationDegrees=new(-48,-30,0),LightEnergy=1.4f,ShadowEnabled=true});
            AddChild(new MeshInstance3D{Mesh=new PlaneMesh{Size=new(100,100)},Position=new(0,-0.02f,0),MaterialOverride=new StandardMaterial3D{AlbedoColor=new Color("687d7f"),Roughness=1}});
            _camera=new Camera3D{Current=true,Fov=40};AddChild(_camera);ShowVehicle();
        }
        private void ShowVehicle()
        {
            if(_model!=null){RemoveChild(_model);_model.QueueFree();}
            var d=_definitions.First(v=>v.Id==_ids[_index]);_model=new VehicleController();AddChild(_model);_model.Setup(d);
            _model.PlaceAlongPath(x=>new Vector3((float)x,0,0),0);_model.SetEmergencyLights(true,0.04);
            foreach(var unit in _model.GetChildren().OfType<Node3D>())
            {
                if(d.Kind=="flatbed" && Array.IndexOf(OS.GetCmdlineUserArgs(), "--all-vehicles") < 0)
                {
                    string[] names={"PayloadExcavator","PayloadForklifts","PayloadSteel","PayloadTransformer"};
                    for(int i=0;i<4;i++){var n=unit.GetNodeOrNull<Node3D>(names[i]);if(n!=null)n.Visible=i==_index-2;}
                }
                if(d.Kind=="semi")for(int i=0;i<4;i++){var n=unit.GetNodeOrNull<Node3D>("Advertisement"+i);if(n!=null)n.Visible=i==0;}
            }
            if(d.Kind is "flatbed" or "semi")
            {
                var variants=_model.GetChildren().OfType<Node3D>().SelectMany(u=>u.GetChildren().OfType<Node3D>()).Where(n=>n.HasMeta("vehicle_variant")).ToArray();
                int expected=d.Kind=="flatbed"?4:6;
                if(variants.Length!=expected || variants.Any(n=>n.GetChildCount()==0))
                    throw new InvalidOperationException("Vehicle batching destroyed selectable model variants: "+d.Id);
                if(variants.Count(n=>n.Visible)!=(d.Kind=="flatbed"?1:2))
                    throw new InvalidOperationException("Multiple vehicle alternatives are visible: "+d.Id);
                var copy=new VehicleController();copy.SetupFrom(_model);
                var copied=copy.GetChildren().OfType<Node3D>().SelectMany(u=>u.GetChildren().OfType<Node3D>()).Where(n=>n.HasMeta("vehicle_variant")).ToArray();
                if(copied.Length!=expected || copied.Any(n=>n.GetChildCount()==0))
                    throw new InvalidOperationException("Vehicle template lost selectable variants: "+d.Id);
                copy.Free();
                GD.Print("Vehicle variant validation passed: "+d.Id);
            }
            var target=new Vector3(-(float)d.Wheelbase/2,1.6f,0);
            float distance=(float)d.Length*0.85f+4;
            _camera.Position=target+new Vector3(distance*0.62f,distance*0.58f,d.Kind=="school_bus"?-distance:distance);_camera.LookAt(target);
            _frames=0;
        }
        public override void _UnhandledKeyInput(InputEvent input)
        {
            if(input is InputEventKey {Pressed:true,Echo:false} key)
            {
                if(key.Keycode==Key.Right){_index=(_index+1)%_ids.Length;ShowVehicle();}
                if(key.Keycode==Key.Left){_index=(_index+_ids.Length-1)%_ids.Length;ShowVehicle();}
            }
        }
        public override void _Process(double delta)
        {
            if(_output==null || ++_frames<24)return;
            DirAccess.MakeDirRecursiveAbsolute(_output);
            string path=System.IO.Path.Combine(_output,$"{_index:D2}-{_ids[_index]}.png");
            if(DisplayServer.GetName()!="headless")
            {
                var result=GetViewport().GetTexture().GetImage().SavePng(path);
                if(result!=Error.Ok)throw new InvalidOperationException("Cannot save vehicle preview: "+result);
                GD.Print("Vehicle preview: "+path);
            }
            if(++_index==_ids.Length)GetTree().Quit();else ShowVehicle();
        }
    }
}
