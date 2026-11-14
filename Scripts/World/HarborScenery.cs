using System;
using System.Collections.Generic;
using Godot;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.World
{
    /// <summary>Hollow rock tunnels and a two-leaf attract bridge; no player physics is changed.</summary>
    public partial class HarborScenery : Node3D
    {
        private Node3D _left,_right;
        private readonly List<Node3D> _gates=new();
        private readonly List<MeshInstance3D> _signals=new();
        private readonly List<(MeshInstance3D barrel,MeshInstance3D rod,Vector3 fixedEnd,Node3D leaf,Vector3 tip)> _actuators=new();
        private readonly Material _steel=Mat("557e84"),_deck=Mat("424d57"),_rail=Mat("bcc3bc"),_stone=Mat("89928b"),_dark=Mat("263139");
        private readonly Material _red=Mat("ce6e57",true),_green=Mat("79bea1",true),_amber=Mat("dfb963",true);
        private static StandardMaterial3D Mat(string color,bool glow=false)=>new()
        {AlbedoColor=new Color(color),Roughness=0.86f,EmissionEnabled=glow,Emission=new Color(color),EmissionEnergyMultiplier=glow?0.7f:0};

        public void Build(GameDatabase db=null)
        {
            Tunnel((float)HarborTimeline.LeftPortal,-1);Tunnel((float)HarborTimeline.RightPortal,1);
            for(int side=0;side<2;side++)
            {
                float x=side==0?4:20;
                CapsulePier(x,db);
                Box(this,new Vector3(side==0?2:22,0.1f,0),new Vector3(4,0.24f,RoadLayout.Width),_deck);
                var leaf=new Node3D {Position=new Vector3(x,0,0)};AddChild(leaf);
                if(side==0)_left=leaf;else _right=leaf;
                float direction=side==0?1:-1;
                Box(leaf,new Vector3(direction*4,0.1f,0),new Vector3(8,0.24f,RoadLayout.Width),_deck);
                Box(leaf,new Vector3(direction*4,0.233f,0),new Vector3(7.6f,0.02f,0.1f),Mat("ded8b4"));
                for(int truss=-1;truss<=1;truss+=2)
                {
                    float z=truss*RoadLayout.TrussOffset;
                    for(int i=0;i<4;i++)
                    {
                        var a=new Vector3(direction*i*2,0,z);var b=new Vector3(direction*(i*2+1),-(float)HarborTimeline.TrussDepth,z);var c=new Vector3(direction*(i*2+2),0,z);
                        Beam(leaf,a,b,(float)HarborTimeline.TrussThickness,_steel);Beam(leaf,b,c,(float)HarborTimeline.TrussThickness,_steel);
                        if(i<3)Beam(leaf,b,new Vector3(direction*(i*2+3),-(float)HarborTimeline.TrussDepth,z),0.15f,_steel);
                        Beam(leaf,new Vector3(direction*i*2,0.25f,z),new Vector3(direction*i*2,0.95f,z),0.055f,_rail);
                    }
                    Beam(leaf,new Vector3(0,0.95f,z),new Vector3(direction*8,0.95f,z),0.06f,_rail);
                    Beam(leaf,new Vector3(direction*8,0.25f,z),new Vector3(direction*8,0.95f,z),0.055f,_rail);
                    Beam(this,new Vector3(side==0?0:20,0.95f,z),new Vector3(side==0?4:24,0.95f,z),0.06f,_rail);
                    var fixedEnd=new Vector3(side==0?3:21,5,z);
                    Beam(this,new Vector3(x,-0.2f,z),fixedEnd,0.4f,_stone);
                    Beam(this,new Vector3(side==0?0:24,-0.2f,z),fixedEnd,0.32f,_stone);
                    var barrel=Box(this,Vector3.Zero,Vector3.One,Mat("c4ad58"));var rod=Box(this,Vector3.Zero,Vector3.One,Mat("c1ccd0"));
                    _actuators.Add((barrel,rod,fixedEnd,leaf,new Vector3(direction*3,-(float)HarborTimeline.TrussDepth,z)));
                }
                Gate(side==0?-4:28);
            }
            SetPose(0,true);
        }
        private void CapsulePier(float x,GameDatabase db)
        {
            // Match the game's pier: a slab joining two round ends at the truss planes.
            var definition=db?.Materials.GetValueOrDefault("pier_concrete");
            float thickness=(float)(definition?.VisualThickness??1.4),offset=BuildPlane.TrussOffset;
            var concrete=Mat(definition?.Color??"b9b9b2");float bottom=-8.5f,top=-0.02f,height=top-bottom;
            var root=new Node3D {Name=x<12?"LeftConcreteCapsulePier":"RightConcreteCapsulePier"};AddChild(root);
            Box(root,new(x,(top+bottom)/2,0),new(thickness,height,2*offset),concrete);
            foreach(int side in new[]{-1,1})root.AddChild(new MeshInstance3D
            {
                Mesh=new CylinderMesh {TopRadius=thickness/2,BottomRadius=thickness/2,Height=height,RadialSegments=16,Rings=1},
                Position=new(x,(top+bottom)/2,side*offset),MaterialOverride=concrete
            });
            LowPolyDetails.Batch(root);
        }
        public void SetPose(double opening,bool roadGreen,double gateRaised=1,bool amber=false)
        {
            float angle=Mathf.DegToRad((float)(HarborTimeline.OpenAngle*opening));
            _left.Rotation=new Vector3(0,0,angle);_right.Rotation=new Vector3(0,0,-angle);
            foreach(var actuator in _actuators)
            {
                var end=actuator.leaf.Position+actuator.leaf.Basis*actuator.tip;var delta=end-actuator.fixedEnd;
                var middle=actuator.fixedEnd+delta.Normalized()*Mathf.Min(3.7f,delta.Length()*0.98f);
                SetBeam(actuator.barrel,actuator.fixedEnd,middle,0.18f);SetBeam(actuator.rod,middle,end,0.09f);
            }
            foreach(var gate in _gates)gate.Rotation=new Vector3((float)gateRaised*Mathf.Pi/2*(float)gate.GetMeta("side"),0,0);
            foreach(var signal in _signals)signal.MaterialOverride=roadGreen?_green:amber?_amber:_red;
        }
        private void Gate(float x)
        {
            float side=x<12?1:-1;
            Box(this,new Vector3(x,0.95f,side*4.65f),new Vector3(0.35f,1.6f,0.35f),_dark);
            var arm=new Node3D {Position=new Vector3(x,1.4f,side*4.3f)};AddChild(arm);arm.SetMeta("side",side);_gates.Add(arm);
            for(int i=0;i<23;i++)Box(arm,new Vector3(0,0,-side*i*0.38f),new Vector3(0.1f,0.13f,0.38f),i%2==0?_rail:_red);
            Beam(this,new Vector3(x,0,side*4.9f),new Vector3(x,2.9f,side*4.9f),0.11f,_dark);
            Box(this,new Vector3(x,2.8f,side*4.9f),new Vector3(0.22f,0.55f,0.3f),_dark);
            _signals.Add(Box(this,new Vector3(x-side*0.13f,2.8f,side*4.9f),new Vector3(0.035f,0.23f,0.22f),_green));
        }
        private void Tunnel(float portal,int direction)
        {
            const int arc=12;const float shoulder=3.1f,innerRadius=RoadLayout.Width/2+.8f;
            float length=HarborMountains.Length(direction);
            var inner=new List<Vector2>{new(-innerRadius,0.15f)};
            for(int i=0;i<=arc;i++){float a=Mathf.Pi-i*Mathf.Pi/arc;inner.Add(new Vector2(innerRadius*Mathf.Cos(a),shoulder+innerRadius*Mathf.Sin(a)));}
            inner.Add(new Vector2(innerRadius,0.15f));int count=inner.Count;
            var rock=new SurfaceTool();rock.Begin(Mesh.PrimitiveType.Triangles);
            var lining=new SurfaceTool();lining.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 Inner(int i,float x)=>new(x,inner[i].Y,inner[i].X);
            Vector3 Outer(int i,int station)
                =>HarborMountains.Point(portal,direction,i,count,station);
            for(int i=0;i<count;i++)
            {
                int j=(i+1)%count;
                for(int station=0;station<HarborMountains.Stations;station++)Quad(rock,Outer(i,station),Outer(j,station),Outer(j,station+1),Outer(i,station+1));
                Quad(rock,Inner(i,portal),Inner(j,portal),Outer(j,0),Outer(i,0));
                Quad(rock,Inner(i,portal+direction*length),Outer(i,HarborMountains.Stations),Outer(j,HarborMountains.Stations),Inner(j,portal+direction*length));
                Quad(lining,Inner(i,portal),Inner(i,portal+direction*length),Inner(j,portal+direction*length),Inner(j,portal));
            }
            rock.GenerateNormals();rock.Index();lining.GenerateNormals();lining.Index();
            var stone=(StandardMaterial3D)Mat("778178");stone.CullMode=BaseMaterial3D.CullModeEnum.Disabled;
            var wall=(StandardMaterial3D)Mat("3e4947");wall.CullMode=BaseMaterial3D.CullModeEnum.Disabled;
            AddChild(new MeshInstance3D {Mesh=rock.Commit(),MaterialOverride=stone});AddChild(new MeshInstance3D {Mesh=lining.Commit(),MaterialOverride=wall});
            HarborMountains.BuildForest(this,portal,direction);
            // A real annular portal follows the hollow bore; no opaque mesh spans the road.
            for(int i=1;i<count-2;i++)
            {
                var a=Inner(i,portal-direction*0.08f);var b=Inner(i+1,portal-direction*0.08f);
                Beam(this,a,b,0.5f,_stone);
            }
            for(int side=-1;side<=1;side+=2)
            {
                Box(this,new Vector3(portal,1.55f,side*innerRadius),new Vector3(0.65f,3.1f,0.5f),_stone);
                for(int i=0;i<3;i++)Box(this,new Vector3(portal-direction*0.35f,0.6f+i*0.4f,side*(innerRadius-.26f)),new Vector3(0.16f,0.12f,0.05f),i%2==0?_rail:_red);
            }
            // The existing approach road runs through the bore; duplicating it would z-fight.
            for(int i=0;i<9;i++)
            {
                float x=portal+direction*(4+i*8);
                Box(this,new Vector3(x,5.8f,0),new Vector3(1.4f,0.06f,0.22f),Mat("e2c991",true));
                AddChild(new OmniLight3D {Position=new Vector3(x,5,0),LightColor=new Color("e7d5b1"),LightEnergy=0.65f,OmniRange=7,ShadowEnabled=false});
            }
        }
        private static void Quad(SurfaceTool tool,Vector3 a,Vector3 b,Vector3 c,Vector3 d)
        {tool.AddVertex(a);tool.AddVertex(c);tool.AddVertex(b);tool.AddVertex(a);tool.AddVertex(d);tool.AddVertex(c);}
        private static MeshInstance3D Box(Node3D parent,Vector3 position,Vector3 size,Material material)
        {
            var mesh=new MeshInstance3D {Position=position,Mesh=new BoxMesh {Size=size},MaterialOverride=material};parent.AddChild(mesh);return mesh;
        }
        private static void SetBeam(MeshInstance3D mesh,Vector3 a,Vector3 b,float width)=>Building.BridgeRenderer.SetBeam(mesh,a,b,width,width);
        private static void Beam(Node3D parent,Vector3 a,Vector3 b,float width,Material material)=>SetBeam(Box(parent,Vector3.Zero,Vector3.One,material),a,b,width);
    }
}
