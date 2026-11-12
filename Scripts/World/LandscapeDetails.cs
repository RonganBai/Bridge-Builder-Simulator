using BridgeArchitect3D.Data;
using Godot;
using System.Linq;

namespace BridgeArchitect3D.World
{
    public static class LandscapeDetails
    {
        public static void Build(Node3D root,LevelDefinition level,SceneryLayout layout)
        {
            var profile=LevelArtProfile.For(level.Id);
            var walls=TerrainBuilder.Mat(new Color("d8d0ba"));var roof=TerrainBuilder.Mat(new Color(profile.Accent));
            var stone=TerrainBuilder.Mat(new Color("81918f"));var dark=TerrainBuilder.Mat(new Color("3f575e"));
            var foliage=TerrainBuilder.Mat(new Color(profile.Foliage));var pale=TerrainBuilder.Mat(new Color("e6e8db"));
            foreach(var plot in layout.Buildings)
            {
                int bank=plot.Bank;float x=(float)plot.X,y=(float)(bank==0?level.Terrain.LeftBankHeight:level.Terrain.RightBankHeight);
                var landmark=new Node3D{Name=plot.Id,Position=new Vector3(x,y,(float)plot.Z)};root.AddChild(landmark);
                void B(float px,float py,float pz,float sx,float sy,float sz,Material mat)=>LowPolyDetails.Box(landmark,new(px,py,pz),new(sx,sy,sz),mat);
                if(plot.Kind!="landmark")
                {
                    BuildNeighborhood(landmark,profile,plot.Kind,walls,roof,dark,pale);
                    LowPolyDetails.Batch(landmark);AddCollisions(landmark);continue;
                }
                switch(profile.Landmark)
                {
                    case "city":
                        for(int i=0;i<3;i++)
                        {
                            float h=5+i*1.7f;B(i*4-4,h/2,0,3.2f,h,4,stone);B(i*4-4,h+0.12f,0,3.4f,0.25f,4.2f,pale);
                            for(int floor=0;floor<3+i;floor++)for(int window=0;window<2;window++)B(i*4-4.7f+window*1.4f,1+floor*1.3f,2.02f,0.65f,0.65f,0.03f,dark);
                        }
                        break;
                    case "freight":case "harbor":
                        for(int i=0;i<4;i++)
                        {
                            B(i*2.2f-3.3f,1,0,2,2,5,i%2==0?roof:stone);
                            for(int rib=0;rib<5;rib++)B(i*2.2f-3.3f,1,-2+rib,2.04f,1.7f,0.035f,dark);
                            if(i%2==0){B(i*2.2f-3.3f,3,0,2,2,5,stone);for(int rib=0;rib<5;rib++)B(i*2.2f-3.3f,3,-2+rib,2.04f,1.7f,0.035f,dark);}
                        }
                        if(profile.Landmark=="harbor"){B(-6,3,0,0.3f,6,0.3f,roof);B(-3,5.8f,0,6,0.25f,0.25f,roof);B(0,4.5f,0,0.04f,2.5f,0.04f,dark);}
                        else{B(0,4.3f,-3.5f,10,0.2f,0.25f,stone);B(-4.5f,2.1f,-3.5f,0.2f,4.2f,0.2f,stone);B(4.5f,2.1f,-3.5f,0.2f,4.2f,0.2f,stone);}
                        break;
                    case "wetland":case "rainforest":
                        B(0,0.4f,0,10,0.18f,1.6f,roof);
                        for(int i=0;i<5;i++)foreach(int side in new[]{-1,1})B(i*2-4,0.155f,side*0.55f,0.12f,0.31f,0.12f,stone);
                        for(int i=0;i<8;i++){B(i*1.3f-4.5f,0.8f,1.6f,0.05f,1.6f,0.05f,foliage);B(i*1.3f-4.5f,1.55f,1.6f,0.13f,0.45f,0.13f,roof);}
                        B(3,2,-2.8f,3,4,3,stone);B(3,4.1f,-2.8f,3.5f,0.25f,3.5f,roof);
                        break;
                    case "canyon":
                        for(int i=0;i<4;i++)
                            landmark.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=1+i*0.2f,BottomRadius=1.8f,Height=3+i,RadialSegments=5,Rings=1},MaterialOverride=roof,Position=new(i*4-6,1.5f+i*0.5f,0)});
                        break;
                    default:
                        bool barn=profile.Landmark=="farm";float width=barn?7:4;
                        B(0,1.6f,0,width,3.2f,4.5f,walls);
                        AddRoof(landmark,width+0.6f,4.9f,3.2f,4.35f,roof);
                        B(0,0.9f,2.27f,barn?2.2f:0.8f,1.8f,0.05f,roof);
                        foreach(int side in new[]{-1,1}){float windowX=side*(barn?2.3f:1.4f);B(windowX,1.9f,2.28f,0.8f,0.85f,0.04f,dark);B(windowX,1.9f,2.31f,0.05f,0.85f,0.025f,pale);}
                        if(profile.Landmark=="alpine" || profile.Landmark=="oldtown")B(-1,4,0,0.5f,1.8f,0.6f,stone);
                        for(int i=0;i<5;i++){B(i*1.5f-3,0.65f,4.5f,0.09f,1.3f,0.09f,roof);B(i*1.5f-3,0.7f,4.5f,1.5f,0.09f,0.07f,roof);}
                        if(profile.Landmark=="orchard" || profile.Landmark=="birch")
                            for(int i=0;i<3;i++){B(i*2.8f-2.8f,1,-5,0.18f,2,0.18f,pale);landmark.AddChild(new MeshInstance3D{Mesh=new SphereMesh{Radius=1.1f,Height=1.8f,RadialSegments=7,Rings=3},MaterialOverride=foliage,Position=new(i*2.8f-2.8f,2.7f,-5)});}
                        break;
                }
                LowPolyDetails.Batch(landmark);
                AddCollisions(landmark);
            }
        }

        private static void AddRoof(Node3D root,float width,float depth,float baseY,float topY,Material material)
        {
            var shape=new[]{new Vector2(-depth/2,baseY),new Vector2(depth/2,baseY),new Vector2(0,topY)};
            root.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(shape,width),RotationDegrees=new Vector3(0,90,0),MaterialOverride=material});
        }
        private static void AddCollisions(Node3D root)
        {
            bool first=true;var bounds=new Aabb();
            foreach(var mesh in root.GetChildren().OfType<MeshInstance3D>())
            {var current=mesh.Transform*mesh.GetAabb();bounds=first?current:bounds.Merge(current);first=false;}
            root.SetMeta("local_bounds",bounds);
            var body=new StaticBody3D{Name="BuildingCollision"};root.AddChild(body);
            foreach(var mesh in root.GetChildren().OfType<MeshInstance3D>())
                body.AddChild(new CollisionShape3D{Shape=mesh.Mesh.CreateTrimeshShape(),Transform=mesh.Transform});
        }
        private static void BuildNeighborhood(Node3D root,LevelArtProfile profile,string kind,Material walls,Material roof,Material glass,Material pale)
        {
            bool shop=kind=="shop",workshop=kind=="workshop";float width=workshop?6.8f:5.8f,depth=5,height=shop?5.6f:3.6f;
            void B(Vector3 p,Vector3 size,Material m)=>LowPolyDetails.Box(root,p,size,m);
            B(new Vector3(0,height/2,0),new Vector3(width,height,depth),walls);
            if(shop || profile.Landmark=="city")B(new Vector3(0,height+0.16f,0),new Vector3(width+0.25f,0.32f,depth+0.25f),roof);
            else AddRoof(root,width+0.5f,depth+0.5f,height,height+1.25f,roof);
            B(new Vector3(0,0.1f,depth/2+0.25f),new Vector3(1.5f,0.2f,0.6f),pale);
            B(new Vector3(0,1,depth/2+0.025f),new Vector3(workshop?2.4f:0.9f,2,0.05f),roof);
            for(int floor=0;floor<(shop?2:1);floor++)foreach(int side in new[]{-1,1})
            {
                float x=side*(width/2-1.15f),y=1.7f+floor*2.5f;
                B(new Vector3(x,y,depth/2+0.03f),new Vector3(1.2f,1,0.045f),glass);
                B(new Vector3(x,y,depth/2+0.06f),new Vector3(0.06f,1,0.025f),pale);
                B(new Vector3(x,y-0.54f,depth/2+0.08f),new Vector3(1.35f,0.1f,0.18f),roof);
                B(new Vector3(side*(width/2+0.025f),y,0),new Vector3(0.045f,1,1.2f),glass);
            }
            if(shop)
            {
                var canopy=new MeshInstance3D{Mesh=new BoxMesh{Size=new Vector3(width-0.6f,0.12f,1)},Position=new Vector3(0,2.8f,depth/2+0.4f),RotationDegrees=new Vector3(9,0,0),MaterialOverride=roof};root.AddChild(canopy);
                B(new Vector3(0,3.4f,depth/2+0.04f),new Vector3(1.2f,0.32f,0.05f),roof);
            }
            if(workshop)for(int i=0;i<6;i++)B(new Vector3(0,0.5f+i*0.25f,depth/2+0.055f),new Vector3(2.3f,0.025f,0.02f),pale);
        }
    }
}
