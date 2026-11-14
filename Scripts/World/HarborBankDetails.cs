using System;
using System.Linq;
using Godot;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Traffic;
using BridgeArchitect3D.Building;

namespace BridgeArchitect3D.World
{
    /// <summary>Reserved riverside walks, retaining walls and small docks outside the navigation corridor.</summary>
    public partial class HarborBankDetails : Node3D
    {
        private readonly Material _stone=Mat("879891"),_paving=Mat("b0b7a8"),_joint=Mat("72817d"),_metal=Mat("3e5459"),_wood=Mat("9d805b"),_leaf=Mat("68866b"),_soil=Mat("554b3c"),_cream=Mat("d9ceb1"),_red=Mat("b97254"),_lamp=Mat("e5d5ab",true);
        private static Material Mat(string color,bool glow=false)=>new StandardMaterial3D {AlbedoColor=new Color(color),Roughness=0.8f,EmissionEnabled=glow,Emission=new Color(color),EmissionEnergyMultiplier=glow?0.4f:0};
        private static float X(int bank,float distance)=>bank==0?-distance:24+distance;
        public static void Reserve(SceneryLayout layout)
        {
            for(int bank=0;bank<2;bank++)for(int side=-1;side<=1;side+=2)
            {
                var strip=new SceneryPlot($"promenade-{bank}-{side}","promenade",X(bank,2.65f),side*25.5,2.6,18,bank);
                layout.Buildings.RemoveAll(p=>SceneryLayout.Overlaps(p,strip,0.7));
                layout.Hills.RemoveAll(p=>SceneryLayout.Overlaps(p,strip,0.7));
                layout.Reservations.RemoveAll(p=>SceneryLayout.Overlaps(p,strip,0.7));layout.Reservations.Add(strip);
            }
            // TerrainBuilder's existing park is unconditional; keep its real footprint protected.
            if(!layout.Reservations.Any(p=>p.Kind=="park"))layout.Reservations.Add(new SceneryPlot("park","park",-11.2,-11.5,5.2,5.1,0));
        }
        public void Build()
        {
            for(int bank=0;bank<2;bank++)
            {
                float dockZ=bank==0?-34:34;
                for(int side=-1;side<=1;side+=2)
                {
                    Box(new(X(bank,3.9f),0.18f,side*25.5f),new(2.7f,0.1f,36),_paving);
                    Box(new(X(bank,0.15f),-2.4f,side*25.5f),new(0.5f,5.2f,36),_stone);
                    Box(new(X(bank,0.15f),0.22f,side*25.5f),new(0.65f,0.16f,36),_cream);
                    // Shallow mortar lines break up the wall without adding navigation obstacles.
                    for(int row=0;row<6;row++)
                    {
                        float y=-0.2f-row*0.78f,face=bank==0?0.105f:23.895f;
                        Box(new(face,y,side*25.5f),new(0.018f,0.025f,36),_joint);
                        for(int block=0;block<12;block++)
                            Box(new(face,y-0.38f,side*(8.2f+block*3+(row%2)*0.7f)),new(0.018f,0.74f,0.025f),_joint);
                    }
                    for(int i=0;i<=12;i++)
                    {
                        float z=side*(7.5f+i*3);
                        Box(new(X(bank,3.9f),0.235f,z),new(2.7f,0.012f,0.025f),_joint);
                    }
                    float start=side>0?7.5f:-43.5f,end=side>0?43.5f:-7.5f;
                    if(dockZ>start && dockZ<end){RailRun(bank,start,dockZ-1);RailRun(bank,dockZ+1,end);}else RailRun(bank,start,end);
                    foreach(int z in new[]{10,26,42})Lamp(new(X(bank,0.95f),0.25f,side*z));
                    foreach(int z in new[]{18,30})Bench(bank,side*z);
                    foreach(int z in new[]{14,22,38})Planter(new(X(bank,1.3f),0.25f,side*z));
                    // Waste bins stay in the furnishing strip, leaving the paving unobstructed.
                    Box(new(X(bank,1.25f),0.63f,side*20),new(0.4f,0.75f,0.4f),_metal);
                    Box(new(X(bank,1.25f),1.02f,side*20),new(0.43f,0.05f,0.43f),_cream);
                }
                Dock(bank,dockZ);
                for(int side=-1;side<=1;side+=2)
                {
                    float portal=(float)(bank==0?HarborTimeline.LeftPortal:HarborTimeline.RightPortal);
                    float end=bank==0?-1:25,center=(portal+end)/2,length=Math.Abs(portal-end);
                    Box(new(center,0.5f,side*(RoadLayout.Width/2+.6f)),new(length,0.75f,0.38f),_stone);
                    Box(new(center,0.9f,side*(RoadLayout.Width/2+.6f)),new(length,0.08f,0.5f),_cream);
                    foreach(float t in new[]{0.15f,0.65f})Lamp(new(Mathf.Lerp(portal,end,t),0.1f,side*(RoadLayout.Width/2+1.2f)));
                }
                // Road markings belong to the approach, not the moving bridge leaf.
                float crossing=bank==0?-11:32;
                for(int i=0;i<6;i++)Box(new(crossing+i*0.5f,0.237f,0),new(0.28f,0.012f,2.8f),_cream);
            }
            Box(new(-5.65f,0.185f,-11.5f),new(0.85f,0.04f,1.4f),_paving);
            LowPolyDetails.Batch(this);
        }
        private void RailRun(int bank,float start,float end)
        {
            Box(new(X(bank,0.15f),1.07f,(start+end)/2),new(0.065f,0.06f,end-start),_metal);
            int count=(int)Math.Ceiling((end-start)/3);
            for(int i=0;i<=count;i++)Box(new(X(bank,0.15f),0.7f,Mathf.Lerp(start,end,(float)i/count)),new(0.075f,0.85f,0.075f),_metal);
        }
        private void Lamp(Vector3 foot)
        {
            Box(foot+new Vector3(0,1.65f,0),new(0.075f,3.3f,0.075f),_metal);
            Box(foot+new Vector3(0,3.32f,0),new(0.42f,0.12f,0.42f),_metal);
            Box(foot+new Vector3(0,3.23f,0),new(0.32f,0.06f,0.32f),_lamp);
        }
        private void Bench(int bank,float z)
        {
            float x=X(bank,1.55f);Box(new(x,0.68f,z),new(0.55f,0.1f,1.65f),_wood);
            Box(new(X(bank,1.82f),0.96f,z),new(0.07f,0.55f,1.65f),_wood);
            foreach(int side in new[]{-1,1})Box(new(x,0.43f,z+side*0.6f),new(0.35f,0.4f,0.065f),_metal);
        }
        private void Planter(Vector3 foot)
        {
            Box(foot+new Vector3(0,0.22f,0),new(0.8f,0.44f,1.4f),_stone);
            Box(foot+new Vector3(0,0.45f,0),new(0.65f,0.03f,1.25f),_soil);
            for(int i=-1;i<=1;i++)AddChild(new MeshInstance3D {Mesh=new SphereMesh {Radius=0.32f,Height=0.65f,RadialSegments=7,Rings=3},Position=foot+new Vector3(0,0.7f,i*0.38f),MaterialOverride=_leaf});
            foreach(int side in new[]{-1,1})for(int i=-1;i<=1;i++)
                Box(foot+new Vector3(side*0.22f,0.93f,i*0.34f),new(0.13f,0.08f,0.13f),i%2==0?_cream:_red);
        }
        private void Dock(int bank,float z)
        {
            float direction=bank==0?1:-1,edge=bank==0?0:24,deck=-3.95f;
            Box(new(X(bank,2.1f),0.19f,z),new(4.2f,0.1f,1.4f),_paving);
            for(int i=0;i<22;i++)Box(new(edge+direction*(i+0.5f)*0.28f,0.23f-i*0.2f-0.07f,z),new(0.28f,0.14f,1.35f),_wood);
            for(int side=-1;side<=1;side+=2)
            {
                Beam(new(edge,0.02f,z+side*0.58f),new(edge+direction*6.1f,deck-0.2f,z+side*0.58f),0.14f,_metal);
                Beam(new(edge,1.13f,z+side*0.68f),new(edge+direction*6.1f,deck+0.9f,z+side*0.68f),0.055f,_metal);
                for(int step=0;step<=21;step+=3)
                    Box(new(edge+direction*(step+0.5f)*0.28f,0.68f-step*0.2f,z+side*0.68f),new(0.055f,0.9f,0.055f),_metal);
            }
            for(int i=0;i<19;i++)Box(new(edge+direction*5.9f,deck-0.07f,z-3.33f+i*0.37f),new(2.6f,0.14f,0.34f),_wood);
            foreach(float distance in new[]{4.8f,7f})foreach(int side in new[]{-1,1})
            {
                float x=edge+direction*distance;
                Box(new(x,-6.3f,z+side*3),new(0.2f,4.4f,0.2f),_metal);
                Box(new(x,deck+0.45f,z+side*3),new(0.09f,0.9f,0.09f),_metal);
            }
            Beam(new(edge+direction*7,deck+0.85f,z-3),new(edge+direction*7,deck+0.85f,z+3),0.065f,_metal);
            Box(new(edge+direction*6.6f,deck+0.55f,z+2),new(0.09f,1.1f,0.09f),_metal);
            AddChild(new MeshInstance3D {Mesh=new TorusMesh {InnerRadius=0.14f,OuterRadius=0.24f,Rings=12,RingSegments=6},Position=new Vector3(edge+direction*6.6f,deck+0.75f,z+1.94f),RotationDegrees=new Vector3(90,0,0),MaterialOverride=_red});
            if(bank==0)
            {
                var profile=ShipCatalog.Create("speedboat",8);var boat=new ShipController {Scale=Vector3.One*0.55f};AddChild(boat);boat.Setup(profile,-4.8,24,-8.5,false);boat.SetZ(z);
                foreach(float offset in new[]{-1.1f,1.1f})Beam(new(7.15f,deck+0.12f,z+offset),new(7.48f,-4.5f,z+offset),0.025f,_cream);
            }
        }
        private void Box(Vector3 position,Vector3 size,Material material)=>LowPolyDetails.Box(this,position,size,material);
        private void Beam(Vector3 a,Vector3 b,float width,Material material)
        {
            var mesh=new MeshInstance3D {Mesh=new BoxMesh {Size=Vector3.One},MaterialOverride=material};AddChild(mesh);BridgeRenderer.SetBeam(mesh,a,b,width,width);
        }
    }
}
