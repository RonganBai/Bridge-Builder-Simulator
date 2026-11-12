using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Traffic;
using BridgeArchitect3D.World;
using Godot;

namespace BridgeArchitect3D.UI
{
    /// <summary>Independent harbor attract scene. All resets happen inside tunnels or beyond the river edge.</summary>
    public partial class MainMenuBackdrop : Node3D
    {
        private Camera3D _camera;
        private HarborScenery _harbor;
        private sealed class RoadVisual
        {
            public VehicleController Model;
            public MeshInstance3D[] Geometry;
            public float Alpha=-1;
        }
        private readonly List<RoadVisual> _traffic=new();
        private readonly List<(ShipController model,TrafficEvent definition)> _ships=new();
        private Vector3 _focus;
        private HarborTraffic _simulation;
        public double SceneTime=>_simulation?.Time??0;
        public HarborTraffic Traffic=>_simulation;
        public override void _Process(double delta){_simulation?.Advance(Math.Min(delta,0.1));UpdateScene();}
        public void Setup(GameDatabase db)
        {
            try
            {
                var source=db.GetLevel("level7")??db.Levels[0];
                var level=JsonSerializer.Deserialize<LevelDefinition>(JsonSerializer.Serialize(source,GameDatabase.JsonOptions),GameDatabase.JsonOptions);
                level.Terrain.RiverWidth=24;level.Terrain.LeftBankHeight=0;level.Terrain.RightBankHeight=0;
                level.Terrain.WaterLevel=HarborTimeline.WaterLevel;level.Terrain.RiverBedDepth=-8.5;level.TrafficEvents.Clear();
                var layout=new SceneryLayout(level);
                HarborMountains.Reserve(layout);
                HarborBankDetails.Reserve(layout);
                var neighborhood=new HarborNeighborhood();neighborhood.Plan(layout);
                TerrainBuilder.Build(this,level,layout);HideConstructionGuides(this);
                AddChild(neighborhood);neighborhood.Build();
                _harbor=new HarborScenery();AddChild(_harbor);_harbor.Build(db);
                var details=new HarborBankDetails();AddChild(details);details.Build();
                _focus=new Vector3(12,0.4f,0);_camera=new Camera3D {Current=true,Fov=48,Near=0.3f,Far=450,HOffset=-6};AddChild(_camera);
                _simulation=new HarborTraffic(db.Vehicles.Values,level.Terrain.WaterLevel);
                foreach(var definition in _simulation.Fleet)
                {
                    var vehicle=new VehicleController {Visible=false};AddChild(vehicle);vehicle.Setup(definition);
                    _traffic.Add(new RoadVisual {Model=vehicle,Geometry=Descendants(vehicle).OfType<MeshInstance3D>().ToArray()});
                }
                foreach(var type in ShipCatalog.Types)
                {
                    var definition=ShipCatalog.Create(type.Id,12);var ship=new ShipController {Visible=false};AddChild(ship);
                    ship.Setup(definition,level.Terrain.WaterLevel,24,level.Terrain.RiverBedDepth);_ships.Add((ship,definition));
                }
                _simulation.Advance(12);
                UpdateScene();
            }
            catch(Exception error){GD.PushWarning("Menu harbor unavailable: "+error);}
        }
        private static IEnumerable<Node> Descendants(Node root)
        {
            foreach(var child in root.GetChildren()){yield return child;foreach(var nested in Descendants(child))yield return nested;}
        }
        private static void HideConstructionGuides(Node root)
        {
            foreach(var child in Descendants(root))
            {
                if(child is Label3D label)label.Hide();
                if(child is MeshInstance3D mesh && (mesh.Mesh is ImmediateMesh ||
                    mesh.MaterialOverride is StandardMaterial3D {ShadingMode:BaseMaterial3D.ShadingModeEnum.Unshaded,Transparency:BaseMaterial3D.TransparencyEnum.Alpha}))mesh.Hide();
            }
        }
        private void UpdateScene()
        {
            if(_camera==null || _harbor==null || _simulation==null)return;
            float angle=0.48f+(float)_simulation.Time*0.023f;
            _camera.Position=_focus+new Vector3(Mathf.Sin(angle)*46,31,Mathf.Cos(angle)*46);_camera.LookAt(_focus);
            _harbor.SetPose(_simulation.Opening,_simulation.RoadGreen,_simulation.GateRaised,_simulation.Operation==HarborOperation.Draining);
            foreach(var vehicle in _traffic)vehicle.Model.Visible=false;
            foreach(var ship in _ships)ship.model.Visible=false;
            foreach(var car in _simulation.Cars)
            {
                var visual=_traffic.First(v=>v.Model.Def.Id==car.Definition.Id);
                visual.Model.Visible=true;visual.Model.PlaceAlongPath(x=>new Vector3((float)x,0.24f,0),car.AxleX);
                visual.Model.SetEmergencyLights(car.EmergencyLightsOn,_simulation.Time);
                float alpha=(float)HarborTimeline.VehicleOpacity(car.Front,car.Definition.Length);
                if(Math.Abs(alpha-visual.Alpha)>0.001)
                {foreach(var mesh in visual.Geometry)mesh.Transparency=1-alpha;visual.Alpha=alpha;}
            }
            foreach(var boat in _simulation.Boats)
            {
                var visual=_ships.First(s=>s.definition.ShipKind==boat.Definition.ShipKind);
                visual.model.Visible=true;visual.model.SetZ((float)boat.Z);
            }
        }
    }
}
