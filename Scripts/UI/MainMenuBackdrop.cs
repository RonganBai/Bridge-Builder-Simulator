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
        // ---- Kept between menu visits: building the harbor takes over a second ----
        private static MainMenuBackdrop _kept;
        private GameDatabase _db;
        private int _pendingFrames=-1;
        public bool IsBuilt=>_pendingFrames<0;
        public event Action Built;

        /// <summary>The kept backdrop from an earlier menu visit, or a new one that builds itself two frames after
        /// entering the tree (so the menu UI is shown first instead of a frozen window).</summary>
        public static MainMenuBackdrop Acquire(GameDatabase db)
        {
            if(_kept!=null && IsInstanceValid(_kept) && _kept._db==db){var kept=_kept;_kept=null;return kept;}
            return new MainMenuBackdrop {Name="MenuBackdrop",_db=db,_pendingFrames=2};
        }

        /// <summary>Detach the menu backdrop from a scene that is about to be replaced, so it is reused next time.</summary>
        public static void KeepFrom(Node scene)
        {
            if(scene?.GetNodeOrNull("MenuBackdrop") is not MainMenuBackdrop backdrop || !backdrop.IsBuilt)return;
            if(_kept!=null && _kept!=backdrop && IsInstanceValid(_kept))_kept.QueueFree();
            backdrop.Built=null;scene.RemoveChild(backdrop);_kept=backdrop;
        }

        public override void _EnterTree(){if(_camera!=null)_camera.Current=true;}

        public override void _Process(double delta)
        {
            if(_pendingFrames>0){if(--_pendingFrames==0){_pendingFrames=-1;Setup(_db);Built?.Invoke();}return;}
            if(_buildQueue.Count>0)_buildQueue.Dequeue()();
            _simulation?.Advance(Math.Min(delta,0.1));UpdateScene();
        }
        private readonly Queue<Action> _buildQueue=new();
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
                _simulation=new HarborTraffic(db.Vehicles.Values,level.Terrain.WaterLevel,bidirectional:true);
                // Vehicle and ship models are built one per frame after the scene is shown (they take ~0.7 s together);
                // a car or ship whose model is not built yet is simply not drawn for those few frames
                foreach(var definition in _simulation.Fleet)
                    _buildQueue.Enqueue(()=>
                    {
                        var vehicle=new VehicleController {Visible=false};AddChild(vehicle);vehicle.Setup(definition);
                        _traffic.Add(new RoadVisual {Model=vehicle,Geometry=Descendants(vehicle).OfType<MeshInstance3D>().ToArray()});
                    });
                double water=level.Terrain.WaterLevel,bed=level.Terrain.RiverBedDepth;
                foreach(var type in ShipCatalog.Types)
                    _buildQueue.Enqueue(()=>
                    {
                        var definition=ShipCatalog.Create(type.Id,12);var ship=new ShipController {Visible=false};AddChild(ship);
                        ship.Setup(definition,water,24,bed);_ships.Add((ship,definition));
                    });
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
                if(child is Label3D label && !label.HasMeta("vehicle_advertisement"))label.Hide();
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
                var visual=_traffic.FirstOrDefault(v=>v.Model.Def.Id==car.Definition.Id);if(visual==null)continue;
                visual.Model.SelectTractorVariant(car.Id);
                visual.Model.Visible=true;visual.Model.PlaceAlongPath(x=>new Vector3((float)RoadLayout.HarborX(x,car.Direction),0.24f,RoadLayout.Z(car.Direction)),car.AxleX);
                visual.Model.SetEmergencyLights(car.EmergencyLightsOn,_simulation.Time);
                float alpha=(float)HarborTimeline.VehicleOpacity(car.Front,car.Definition.Length);
                if(Math.Abs(alpha-visual.Alpha)>0.001)
                {foreach(var mesh in visual.Geometry)mesh.Transparency=1-alpha;visual.Alpha=alpha;}
            }
            foreach(var boat in _simulation.Boats)
            {
                var visual=_ships.FirstOrDefault(s=>s.definition.ShipKind==boat.Definition.ShipKind);if(visual.model==null)continue;
                visual.model.Visible=true;visual.model.SetZ((float)boat.Z);
            }
        }
    }
}
