using System;
using System.Collections.Generic;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.World
{
    /// <summary>Reserved building and planting plots for the menu's riverside neighborhood.</summary>
    public partial class HarborNeighborhood : Node3D
    {
        public List<SceneryPlot> Buildings { get; }=new();
        public List<SceneryPlot> Planting { get; }=new();
        private readonly Material _stone=Mat("a3a89a"),_cream=Mat("e3dac3"),_trim=Mat("eee7d3"),_glass=Mat("496c76"),_wood=Mat("70594b"),_roof=Mat("586971"),_terra=Mat("ad6b50"),_sage=Mat("8aab93"),_leaf=Mat("4c765a"),_lightLeaf=Mat("739365"),_pink=Mat("cf9a91"),_soil=Mat("746b51");
        private static Material Mat(string color)=>new StandardMaterial3D {AlbedoColor=new Color(color),Roughness=0.85f};
        private static float X(int bank,float distance)=>bank==0?-distance:24+distance;

        public void Plan(SceneryLayout layout)
        {
            for(int bank=0;bank<2;bank++)
            {
                int index=0;
                foreach(var position in new[]{(14f,19f),(14f,35f),(14f,-21f),(14f,-36f),(25f,44f),(25f,-44f),(37f,44f),(37f,-44f)})
                {
                    string kind=index++%3==0?"cafe":index%3==0?"villa":"cottage";
                    float distance=bank==1 && position.Item1==14?10.8f:position.Item1;
                    var plot=new SceneryPlot($"harbor-{kind}-{bank}-{index}",kind,X(bank,distance),position.Item2,4.6,4.6,bank);
                    if(layout.TryReserve(plot))Buildings.Add(plot);
                }
                // Reserve whole crowns and shrub beds before TerrainBuilder places its trees.
                for(int row=0;row<3;row++)for(int column=0;column<11;column++)
                {
                    float z=-48+column*9.6f,dist=7.5f+row*10;
                    var plot=new SceneryPlot($"harbor-garden-{bank}-{row}-{column}","garden",X(bank,dist),z,1.55,1.55,bank);
                    if(layout.TryReserve(plot,0.35))Planting.Add(plot);
                }
            }
        }

        public void Build()
        {
            int index=0;
            foreach(var plot in Buildings)
            {
                var root=new Node3D {Name=plot.Id,Position=new((float)plot.X,0,(float)plot.Z),RotationDegrees=new(0,plot.Bank==0?90:-90,0)};AddChild(root);
                Building(root,plot.Kind,index++);LowPolyDetails.Batch(root);
            }
            index=0;
            foreach(var plot in Planting)
            {
                var root=new Node3D {Name=plot.Id,Position=new((float)plot.X,0.08f,(float)plot.Z),Scale=Vector3.One*0.67f};AddChild(root);
                Garden(root,index++);LowPolyDetails.Batch(root);
            }
        }

        private void Building(Node3D root,string kind,int index)
        {
            bool cafe=kind=="cafe",villa=kind=="villa";float height=cafe?5.3f:villa?5.8f:3.45f;
            Material paint=index%3==0?_cream:index%3==1?_sage:_terra;
            void B(float x,float y,float z,float sx,float sy,float sz,Material mat)=>LowPolyDetails.Box(root,new(x,y,z),new(sx,sy,sz),mat);
            B(0,0.13f,0,7.8f,0.26f,8,_stone);
            B(0,height/2+0.25f,0,6,height,5.3f,paint);
            B(0,0.5f,0,6.08f,0.45f,5.38f,_stone);
            B(0,height+0.24f,0,6.2f,0.18f,5.5f,_trim);
            root.AddChild(new MeshInstance3D {Mesh=CurvedMeshes.Extrude(new[]{new Vector2(-3.3f,height+0.3f),new Vector2(3.3f,height+0.3f),new Vector2(0,height+1.85f)},5.85f),MaterialOverride=_roof});
            B(-1.8f,height+1.65f,-1.25f,0.55f,1.6f,0.65f,_terra);
            B(-1.8f,height+2.49f,-1.25f,0.7f,0.13f,0.8f,_stone);
            foreach(int side in new[]{-1,1})
            {
                B(side*3.03f,height/2+0.4f,2.63f,0.14f,height-0.3f,0.12f,_trim);
                for(int floor=0;floor<(cafe||villa?2:1);floor++)
                {
                    float y=1.9f+floor*2.45f;
                    Window(root,side*1.85f,y,2.69f,1.25f,1.2f);
                    B(side*1.85f,y-0.73f,2.85f,1.5f,0.23f,0.38f,_terra);
                    for(int flower=0;flower<4;flower++)Sphere(root,new(side*1.85f-0.48f+flower*0.32f,y-0.56f,2.88f),0.16f,0.2f,flower%2==0?_leaf:_pink);
                    // Side windows are rotated as a group so frames stay on the wall surface.
                    var window=new Node3D {Position=new(side*3.04f,y,-0.4f),RotationDegrees=new(0,side*90,0)};root.AddChild(window);Window(window,0,0,0,1.15f,1.15f);
                }
            }
            B(0,1.32f,2.7f,0.98f,2.1f,0.1f,_wood);B(0,1.65f,2.765f,0.65f,1.08f,0.035f,_glass);
            B(0.33f,1.16f,2.8f,0.07f,0.13f,0.04f,_trim);
            B(0,0.33f,3,1.5f,0.17f,0.65f,_stone);
            if(cafe)
            {
                B(0,2.94f,2.77f,2.8f,0.5f,0.13f,_wood);
                for(int stripe=0;stripe<8;stripe++)
                {
                    var awning=new MeshInstance3D {Mesh=new BoxMesh {Size=new(0.68f,0.1f,1.05f)},Position=new(-2.38f+stripe*0.68f,2.65f,3.14f),RotationDegrees=new(10,0,0),MaterialOverride=stripe%2==0?_sage:_trim};root.AddChild(awning);
                    B(-2.38f+stripe*0.68f,2.51f,3.66f,0.68f,0.18f,0.055f,stripe%2==0?_sage:_trim);
                }
                foreach(int side in new[]{-1,1})
                {
                    B(side*2.55f,0.82f,3.2f,0.82f,0.1f,0.65f,_wood);B(side*2.55f,0.5f,3.2f,0.09f,0.55f,0.09f,_roof);
                    B(side*3.3f,0.55f,3.2f,0.4f,0.65f,0.45f,_sage);
                }
            }
            else if(villa)
            {
                B(0,3.02f,2.95f,2.2f,0.15f,0.65f,_stone);
                for(int rail=0;rail<6;rail++)B(-1+rail*0.4f,3.5f,3.25f,0.045f,0.9f,0.045f,_roof);
                B(0,3.97f,3.25f,2.15f,0.05f,0.05f,_roof);
            }
            // Planting stays inside the protected plot and away from the front door.
            foreach(int side in new[]{-1,1})
            {
                B(side*3.55f,0.4f,-0.6f,0.55f,0.25f,4.4f,_terra);
                for(int shrub=0;shrub<6;shrub++)Sphere(root,new(side*3.55f,0.78f,-2.3f+shrub*0.65f),0.36f,0.65f,_leaf);
            }
        }

        private void Window(Node3D root,float x,float y,float z,float width,float height)
        {
            void B(float dx,float dy,float dz,float sx,float sy,float sz,Material mat)=>LowPolyDetails.Box(root,new(x+dx,y+dy,z+dz),new(sx,sy,sz),mat);
            B(0,0,0,width+0.18f,height+0.18f,0.07f,_trim);B(0,0,0.045f,width,height,0.035f,_glass);
            B(0,0,0.075f,0.055f,height,0.025f,_trim);B(0,0,0.075f,width,0.055f,0.025f,_trim);
        }

        private void Garden(Node3D root,int index)
        {
            LowPolyDetails.Box(root,new(0,0.16f,0),new(4.1f,0.1f,4.1f),_soil);
            if(index%3!=2)
            {
                Cylinder(root,new(0,1.45f,0),0.15f,0.22f,2.65f,_wood);
                float h=index%2==0?4.4f:3.8f;
                Sphere(root,new(0,h,0),1.65f,2.6f,index%4==0?_pink:_leaf);
                Sphere(root,new(-0.75f,h-0.6f,0.45f),1.05f,1.9f,_lightLeaf);
                Sphere(root,new(0.75f,h-0.35f,-0.45f),0.95f,1.7f,_leaf);
            }
            for(int i=0;i<5;i++)
            {
                float angle=i*Mathf.Tau/5,x=Mathf.Sin(angle)*1.6f,z=Mathf.Cos(angle)*1.6f;
                Sphere(root,new(x,0.52f,z),0.42f,0.65f,i%2==0?_leaf:_lightLeaf);
                Sphere(root,new(x+0.14f,0.87f,z),0.11f,0.16f,i%2==0?_pink:_cream);
            }
        }
        private static void Sphere(Node3D root,Vector3 position,float radius,float height,Material material)=>root.AddChild(new MeshInstance3D {Mesh=new SphereMesh {Radius=radius,Height=height,RadialSegments=8,Rings=4},Position=position,MaterialOverride=material});
        private static void Cylinder(Node3D root,Vector3 position,float top,float bottom,float height,Material material)=>root.AddChild(new MeshInstance3D {Mesh=new CylinderMesh {TopRadius=top,BottomRadius=bottom,Height=height,RadialSegments=7},Position=position,MaterialOverride=material});
    }
}
