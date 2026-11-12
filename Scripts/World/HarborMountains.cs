using System;
using System.Collections.Generic;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.World
{
    /// <summary>Distinct hollow mountain profiles with vegetation sampled from the actual rock faces.</summary>
    public static class HarborMountains
    {
        public const int Stations=8;
        private const int ProfilePoints=15;
        public static float Length(int direction)=>direction<0?68:80;
        private static float Width(int direction,float t)=>direction<0?18+14*Mathf.Sin(t*Mathf.Pi):19+19*Mathf.Sin(t*Mathf.Pi);

        public static void Reserve(SceneryLayout layout)
        {
            foreach(int direction in new[]{-1,1})
            {
                int bank=direction<0?0:1;float portal=(float)(bank==0?HarborTimeline.LeftPortal:HarborTimeline.RightPortal),length=Length(direction);
                for(int i=0;i<Stations;i++)
                {
                    float t=(i+0.5f)/Stations;
                    float width=Math.Max(Width(direction,i/(float)Stations),Width(direction,(i+1f)/Stations))+1;
                    var plot=new SceneryPlot($"tunnel-mountain-{bank}-{i}","mountain",portal+direction*length*t,0,length/Stations/2+1,width,bank);
                    layout.Buildings.RemoveAll(p=>SceneryLayout.Overlaps(p,plot,1));
                    layout.Hills.RemoveAll(p=>SceneryLayout.Overlaps(p,plot,1));
                    layout.Reservations.RemoveAll(p=>p.Kind!="mountain" && SceneryLayout.Overlaps(p,plot,1));
                    layout.Reservations.Add(plot);
                }
            }
        }

        public static Vector3 Point(float portal,int direction,int index,int count,int station)
        {
            float t=station/(float)Stations,radius=Width(direction,t),x=portal+direction*Length(direction)*t;
            if(station==0)x+=direction*4;
            if(index==0)return new(x,-2,-radius);
            if(index==count-1)return new(x,-2,radius);
            float a=Mathf.Pi-(index-1)*Mathf.Pi/(count-3),s=Mathf.Sin(a),z=radius*Mathf.Cos(a);
            if(station==0)x+=direction*4*s;
            // One bank has a craggy ridge; the other has broad, lower wooded shoulders.
            float peak=direction<0?15+10*Mathf.Sin(t*Mathf.Pi)+2*Mathf.Sin(t*Mathf.Pi*5):14+7*Mathf.Sin(t*Mathf.Pi);
            float asymmetry=direction<0?1+0.11f*Mathf.Cos(a)*Mathf.Sin(t*8):1+0.13f*Mathf.Cos(a);
            float y=-0.35f+(peak+0.35f)*Mathf.Pow(Math.Max(0,s),direction<0?0.83f:0.7f)*asymmetry;
            return new(x,y,z);
        }

        public static void BuildForest(Node3D parent,float portal,int direction)
        {
            var root=new Node3D {Name=direction<0?"RockyPineForest":"RollingMixedForest"};parent.AddChild(root);
            var bark=Mat("60584a");var pine=Mat("3d6455");var deep=Mat("426d58");var moss=Mat("73916a");var rock=Mat(direction<0?"78837c":"8b9180");
            var random=new Random(direction<0?8241:19573);
            var triangles=new List<(Vector3 a,Vector3 b,Vector3 c)>();
            for(int station=0;station<Stations;station++)for(int i=1;i<ProfilePoints-2;i++)
            {
                var a=Point(portal,direction,i,ProfilePoints,station);var b=Point(portal,direction,i+1,ProfilePoints,station);
                var c=Point(portal,direction,i+1,ProfilePoints,station+1);var d=Point(portal,direction,i,ProfilePoints,station+1);
                triangles.Add((a,b,c));triangles.Add((a,c,d));
            }
            // Wooded shoulders carry soil and vegetation; steep faces retain exposed bedrock.
            var ground=new SurfaceTool();ground.Begin(Mesh.PrimitiveType.Triangles);int patches=0;
            foreach(var (a,b,c) in triangles)
            {
                var normal=(b-a).Cross(c-a).Normalized();if(normal.Y<0)normal=-normal;
                float gradient=Mathf.Sqrt(normal.X*normal.X+normal.Z*normal.Z)/Math.Max(0.001f,normal.Y);
                if(gradient>1.5f || Math.Min(a.Y,Math.Min(b.Y,c.Y))<0.6f)continue;
                var offset=normal*0.025f;
                ground.AddVertex(a+offset);ground.AddVertex(c+offset);ground.AddVertex(b+offset);patches++;
            }
            if(patches>0)
            {
                ground.GenerateNormals();ground.Index();var cover=(StandardMaterial3D)Mat(direction<0?"667f69":"7c9271");cover.CullMode=BaseMaterial3D.CullModeEnum.Disabled;
                root.AddChild(new MeshInstance3D {Mesh=ground.Commit(),MaterialOverride=cover});
            }
            int trees=0;
            for(float distance=13;distance<Length(direction)-6;distance+=5.3f)for(float z=-35;z<=35;z+=5.2f)
            {
                float x=portal+direction*(distance+(float)random.NextDouble()*2),pz=z+(float)random.NextDouble()*2-1;
                if(Math.Abs(pz)<5.5f || !Sample(triangles,x,pz,out float y,out float slope) || y<1.2f || slope>1.8f)continue;
                float scale=0.8f+(float)random.NextDouble()*0.65f;
                bool conifer=direction<0 || random.NextDouble()<0.25;
                Tree(root,new(x,y-0.55f,pz),scale,conifer,bark,trees++%3==0?moss:conifer?pine:deep);
                if(trees%3==0)
                {
                    float sx=x+1.6f,sz=pz+1.1f;
                    if(Sample(triangles,sx,sz,out float sy,out float ss) && ss<2)
                        root.AddChild(new MeshInstance3D {Mesh=new SphereMesh {Radius=0.8f,Height=0.9f,RadialSegments=6,Rings=3},Position=new(sx,sy+0.12f,sz),MaterialOverride=rock});
                }
            }
            root.SetMeta("tree_count",trees);LowPolyDetails.Batch(root);
        }

        private static bool Sample(List<(Vector3 a,Vector3 b,Vector3 c)> triangles,float x,float z,out float y,out float slope)
        {
            foreach(var (a,b,c) in triangles)
            {
                float det=(b.Z-c.Z)*(a.X-c.X)+(c.X-b.X)*(a.Z-c.Z);
                if(Math.Abs(det)<0.0001f)continue;
                float u=((b.Z-c.Z)*(x-c.X)+(c.X-b.X)*(z-c.Z))/det;
                float v=((c.Z-a.Z)*(x-c.X)+(a.X-c.X)*(z-c.Z))/det;
                if(u<0 || v<0 || u+v>1)continue;
                y=a.Y*u+b.Y*v+c.Y*(1-u-v);var normal=(b-a).Cross(c-a).Normalized();
                slope=Mathf.Sqrt(normal.X*normal.X+normal.Z*normal.Z)/Math.Max(0.001f,Math.Abs(normal.Y));return true;
            }
            y=0;slope=0;return false;
        }

        private static void Tree(Node3D root,Vector3 foot,float scale,bool conifer,Material bark,Material leaf)
        {
            root.AddChild(new MeshInstance3D {Mesh=new CylinderMesh {TopRadius=0.11f*scale,BottomRadius=0.19f*scale,Height=2.7f*scale,RadialSegments=6},Position=foot+new Vector3(0,1.35f*scale,0),MaterialOverride=bark});
            for(int tier=0;tier<2;tier++)
            {
                float radius=(tier==0?1.45f:1.05f)*scale,height=(tier==0?3.2f:2.7f)*scale;
                Mesh mesh=conifer?new CylinderMesh {TopRadius=0,BottomRadius=radius,Height=height,RadialSegments=7}:new SphereMesh {Radius=radius,Height=height,RadialSegments=8,Rings=4};
                root.AddChild(new MeshInstance3D {Mesh=mesh,Position=foot+new Vector3(tier==1 && !conifer?0.35f*scale:0,(3+tier*1.4f)*scale,0),MaterialOverride=leaf});
            }
        }
        private static Material Mat(string color)=>new StandardMaterial3D {AlbedoColor=new Color(color),Roughness=0.95f};
    }
}
