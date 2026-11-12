using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.World;
using BridgeArchitect3D.Building;
using Godot;

namespace BridgeArchitect3D.Traffic
{
    /// <summary>Two SUV silhouettes with closed wheel wells, tapered glazing and axle-aligned wheels.</summary>
    public static class SuvVehicleFactory
    {
        private static readonly Material Rubber=Material("222b30",0.9f),Trim=Material("35434a",0.8f),Glass=Material("426675",0.18f,0.25f),Metal=Material("a6b4b7",0.3f,0.65f);
        private static readonly Material Head=Material("e8e8d3",0.35f),Tail=Material("ba504b",0.45f),Plate=Material("d9ded8",0.7f);
        private static StandardMaterial3D Material(string color,float roughness,float metallic=0)=>new(){AlbedoColor=new Color(color),Roughness=roughness,Metallic=metallic};

        public static void Build(VehicleDefinition definition,Func<float,float,Node3D> unit,Material paint)
        {
            bool large=definition.Length>=5;
            float l=(float)definition.Length,w=(float)definition.Width,h=(float)definition.Height,rear=(float)definition.Axles.Min(a=>a.Offset);
            float front=(l+rear)/2,back=front-l,half=w*0.45f,r=large?0.405f:0.345f,arch=r+0.055f,bottom=r+0.035f,belt=h*0.60f,roof=h-0.07f;
            float frontBase=front-l*(large?0.27f:0.26f),rearBase=back+(large?0.13f:0.20f);
            float roofFront=frontBase-(large?0.48f:0.50f),roofRear=back+(large?0.35f:0.45f),roofHalf=w*0.39f;
            var n=unit(0,rear);n.Name=large?"LargeSUV":"CompactSUV";
            var outline=new List<Vector2>{new(front-0.03f,bottom+0.05f),new(front-0.025f,h*0.43f),new(front-0.045f,h*0.49f),new(front-0.08f,h*0.535f),new(front-0.16f,h*0.56f),new(frontBase,belt),new(back+0.1f,belt),new(back+0.015f,h*0.51f),new(back+0.015f,bottom)};
            void Arch(float x)
            {
                outline.Add(new(x-arch,bottom));
                for(int i=0;i<=10;i++){float angle=Mathf.Pi-i*Mathf.Pi/10;outline.Add(new(x+arch*Mathf.Cos(angle),r+arch*Mathf.Sin(angle)));}
                outline.Add(new(x+arch,bottom));
            }
            Arch(rear);Arch(0);
            float BodyWidth(Vector2 p)
            {
                float nose=Mathf.Clamp((p.X-(front-0.28f))/0.28f,0,1),tail=Mathf.Clamp((back+0.12f-p.X)/0.12f,0,1);
                return half*(1-(large?0.035f:0.09f)*nose-0.06f*tail);
            }
            AddMesh(n,"ClosedBody",Prism(outline.ToArray(),BodyWidth,new[]{back+0.12f,front-0.28f}),paint);
            Box(n,"Underbody",new(rear/2,large?0.30f:0.25f,0),new(-rear+0.35f,0.14f,w*0.56f),Trim);
            var cabin=new[]{new Vector3(rearBase,belt,-half),new Vector3(frontBase,belt,-half),new Vector3(roofFront,roof,-roofHalf),new Vector3(roofRear,roof,-roofHalf),new Vector3(rearBase,belt,half),new Vector3(frontBase,belt,half),new Vector3(roofFront,roof,roofHalf),new Vector3(roofRear,roof,roofHalf)};
            AddMesh(n,"ClosedCabin",Solid(cabin,new[]{new[]{0,3,2,1},new[]{4,5,6,7},new[]{1,2,6,5},new[]{0,4,7,3},new[]{3,7,6,2},new[]{0,1,5,4}}),paint);
            float Half(float y)=>Mathf.Lerp(half,roofHalf,(y-belt)/(roof-belt));
            float Front(float y)=>Mathf.Lerp(frontBase,roofFront,(y-belt)/(roof-belt));
            float Rear(float y)=>Mathf.Lerp(rearBase,roofRear,(y-belt)/(roof-belt));
            float low=belt+0.075f,high=roof-0.075f,span=roofFront-roofRear;
            float b=roofFront-span*(large?0.29f:0.32f),c=roofRear+span*(large?0.30f:0.25f);
            foreach(int side in new[]{-1,1})
            {
                void Window(string name,float leftLow,float rightLow,float rightHigh,float leftHigh,Material material)
                {
                    var points=new[]{new Vector3(leftLow,low,side*(Half(low)-0.003f)),new Vector3(rightLow,low,side*(Half(low)-0.003f)),new Vector3(rightHigh,high,side*(Half(high)-0.003f)),new Vector3(leftHigh,high,side*(Half(high)-0.003f))};
                    var vertices=points.Concat(points.Select(p=>p+new Vector3(0,0,side*0.012f))).ToArray();
                    var faces=new[]{new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7}};
                    if(side<0)faces=faces.Select(face=>face.AsEnumerable().Reverse().ToArray()).ToArray();
                    AddMesh(n,name+side,Solid(vertices,faces),material);
                }
                Window("FrontSideGlass",b+0.045f,Front(low)-0.06f,Front(high)-0.06f,b+0.045f,Glass);
                Window("RearDoorGlass",c+0.045f,b-0.045f,b-0.045f,c+0.045f,Glass);
                Window("QuarterGlass",Rear(low)+0.06f,c-0.05f,c-0.05f,Rear(high)+0.06f,Glass);
                Window("BPillar",b-0.035f,b+0.035f,b+0.035f,b-0.035f,Trim);
                foreach(float axle in definition.Axles.Select(a=>(float)a.Offset))
                {
                    Wheel(n,axle,r,side*(half-(large?0.265f:0.225f)*0.42f),large?0.265f:0.225f,side);
                    WheelArch(n,axle,r,arch,side*half,0.045f);
                }
                foreach(float seam in new[]{b,c})
                {
                    float floor=bottom+0.025f;
                    foreach(float axle in new[]{0,rear})if(Math.Abs(seam-axle)<arch)
                        floor=Math.Max(floor,r+Mathf.Sqrt(arch*arch-(seam-axle)*(seam-axle))+0.025f);
                    Box(n,"DoorSeam",new(seam,(floor+belt)/2,side*(half+0.004f)),new(0.012f,belt-floor,0.012f),Trim);
                    Box(n,"DoorHandle",new(seam+0.20f,belt-0.09f,side*(half+0.008f)),new(large?0.18f:0.15f,0.028f,0.024f),Metal);
                }
                Box(n,"Rocker",new(rear/2,bottom+0.025f,side*(half+0.006f)),new(-rear-arch*1.8f,0.08f,0.035f),Trim);
                float mirrorY=belt+0.09f,mirrorZ=w*0.475f,bodyZ=Half(mirrorY);
                Box(n,"MirrorMount",new(frontBase-0.12f,mirrorY,side*(mirrorZ+bodyZ)/2),new(0.09f,0.045f,mirrorZ-bodyZ),Trim);
                Round(n,"Mirror",new(frontBase-0.12f,mirrorY,side*mirrorZ),new(0.22f,0.12f,w*0.048f),0.035f,paint);
                Box(n,"MirrorGlass",new(frontBase-0.225f,mirrorY,side*mirrorZ),new(0.012f,0.085f,w*0.037f),Glass);
                Box(n,"HeadlampHousing",new(front-0.075f,h*0.51f,side*w*0.30f),new(0.13f,h*(large?0.11f:0.065f),w*0.19f),Trim);
                Box(n,"Headlamp",new(front-0.014f,h*0.51f,side*w*0.30f),new(0.015f,h*(large?0.075f:0.043f),w*0.16f),Head);
                Box(n,"TailHousing",new(back+0.075f,belt-0.14f,side*w*0.33f),new(0.14f,large?0.28f:0.23f,w*0.14f),Trim);
                Box(n,"TailLight",new(back+0.009f,belt-0.14f,side*w*0.33f),new(0.012f,large?0.24f:0.17f,w*0.11f),Tail);
                if(!large)Box(n,"TailSignature",new(back+0.007f,belt-0.065f,side*w*0.30f),new(0.012f,0.035f,w*0.17f),Tail);
                float railZ=side*w*0.30f;
                foreach(float t in new[]{0.15f,0.5f,0.85f})Box(n,"RoofRailFoot",new(Mathf.Lerp(roofRear,roofFront,t),roof+0.015f,railZ),new(0.09f,0.035f,0.055f),Trim);
                Beam(n,"RoofRail",new(roofRear+0.15f,roof+0.04f,railZ),new(roofFront-0.15f,roof+0.04f,railZ),0.045f,large?Metal:Trim);
            }
            Windshield(n,"FrontWindshield",low,high,y=>Front(y),Half,w*0.025f,1);
            Windshield(n,"RearWindshield",low,high,y=>Rear(y),Half,w*0.018f,-1);
            Round(n,"FrontBumper",new(front-0.05f,h*0.32f,0),new(0.09f,h*0.11f,half*1.95f),0.025f,Trim);
            Round(n,"RearBumper",new(back+0.05f,h*0.32f,0),new(0.09f,h*0.11f,half*1.95f),0.025f,Trim);
            Box(n,"Grille",new(front-0.04f,h*0.39f,0),new(0.075f,h*0.16f,w*(large?0.46f:0.38f)),Trim);
            if(large)for(int i=-3;i<=3;i++)Box(n,"GrilleBar",new(front-0.005f,h*0.39f,i*w*0.055f),new(0.006f,h*0.13f,0.022f),Metal);
            else for(int i=-1;i<=1;i++)Box(n,"GrilleBar",new(front-0.005f,h*0.39f+i*0.055f,0),new(0.006f,0.014f,w*0.34f),Metal);
            Box(n,"FrontPlate",new(front-0.005f,h*0.30f,0),new(0.006f,0.105f,0.42f),Plate);
            Box(n,"RearPlate",new(back+0.005f,h*0.43f,0),new(0.006f,0.105f,0.42f),Plate);
            Box(n,"TailgateHandle",new(back+0.012f,belt-0.13f,0),new(0.018f,0.028f,0.20f),Metal);
            Box(n,"RoofAntenna",new(roofRear+0.18f,roof+0.025f,0),new(0.13f,0.045f,0.04f),Trim);
        }
        private static void Windshield(Node3D root,string name,float low,float high,Func<float,float> boundary,Func<float,float> half,float bulge,int facing)
        {
            var tool=new SurfaceTool();tool.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 Point(int row,int col)
            {
                float y=Mathf.Lerp(low,high,row/3f),t=col/8f;
                return new(boundary(y)+facing*(0.012f+bulge*(1-Mathf.Pow(2*t-1,2))),y,(t-0.5f)*(half(y)*2-0.09f));
            }
            for(int row=0;row<3;row++)for(int col=0;col<8;col++)
            {
                var a=Point(row,col);var b=Point(row,col+1);var c=Point(row+1,col+1);var d=Point(row+1,col);
                if(facing>0){Triangle(tool,a,c,b);Triangle(tool,a,d,c);}else{Triangle(tool,a,b,c);Triangle(tool,a,c,d);}
            }
            tool.Index();AddMesh(root,name,tool.Commit(),Glass);
        }
        private static void Wheel(Node3D root,float axle,float radius,float z,float width,int side)
        {
            const int segments=16;var tool=new SurfaceTool();tool.Begin(Mesh.PrimitiveType.Triangles);
            float[] offsets={-width/2,-width*0.36f,width*0.36f,width/2};float[] radii={radius*0.91f,radius,radius,radius*0.91f};
            Vector3 P(int ring,int i){float angle=i*Mathf.Tau/segments;return new(axle+radii[ring]*Mathf.Cos(angle),radius+radii[ring]*Mathf.Sin(angle),z+offsets[ring]);}
            for(int i=0;i<segments;i++)
            {
                for(int ring=0;ring<3;ring++){Triangle(tool,P(ring,i),P(ring,i+1),P(ring+1,i+1));Triangle(tool,P(ring,i),P(ring+1,i+1),P(ring+1,i));}
                Triangle(tool,new(axle,radius,z-width/2),P(0,i+1),P(0,i));Triangle(tool,new(axle,radius,z+width/2),P(3,i),P(3,i+1));
            }
            tool.Index();AddMesh(root,"Tyre",tool.Commit(),Rubber);
            var center=new Vector3(axle,radius,z+side*(width/2+0.004f));
            var hub=new MeshInstance3D {Name="Rim",Mesh=new CylinderMesh {TopRadius=radius*0.58f,BottomRadius=radius*0.58f,Height=0.012f,RadialSegments=12,Rings=1},Position=center,RotationDegrees=new Vector3(90,0,0),MaterialOverride=Metal};root.AddChild(hub);
            for(int i=0;i<5;i++)
            {
                float angle=i*Mathf.Tau/5;var spoke=Box(root,"SpokeRecess",center+new Vector3(Mathf.Cos(angle)*radius*0.30f,Mathf.Sin(angle)*radius*0.30f,side*0.01f),new(radius*0.40f,0.035f,0.006f),Rubber);spoke.Rotation=new Vector3(0,0,angle);
            }
        }
        private static void WheelArch(Node3D root,float axle,float y,float radius,float z,float width)
        {
            var tool=new SurfaceTool();tool.Begin(Mesh.PrimitiveType.Triangles);
            for(int i=0;i<10;i++)
            {
                float a=i*Mathf.Pi/10,b=(i+1)*Mathf.Pi/10;
                Vector3 P(float angle,float r,float dz)=>new(axle+Mathf.Cos(angle)*r,y+Mathf.Sin(angle)*r,z+dz);
                var vertices=new[]{P(a,radius,-width/2),P(a,radius+0.045f,-width/2),P(b,radius+0.045f,-width/2),P(b,radius,-width/2),P(a,radius,width/2),P(a,radius+0.045f,width/2),P(b,radius+0.045f,width/2),P(b,radius,width/2)};
                Faces(tool,vertices,new[]{new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7}});
            }
            tool.Index();AddMesh(root,"WheelArch",tool.Commit(),Trim);
        }
        private static Mesh Prism(Vector2[] polygon,Func<Vector2,float> halfWidth,float[] cuts)
        {
            if(Geometry2D.IsPolygonClockwise(polygon))Array.Reverse(polygon);
            // Split chamfer bands before triangulating. Otherwise non-planar cap triangles
            // would stretch from the nose across an entire door and produce false creases.
            var expanded=new List<Vector2>();
            for(int i=0;i<polygon.Length;i++)
            {
                var a=polygon[i];var b=polygon[(i+1)%polygon.Length];expanded.Add(a);
                if(Math.Abs(b.X-a.X)<1e-6)continue;
                foreach(float t in cuts.Select(x=>(x-a.X)/(b.X-a.X)).Where(t=>t>1e-6 && t<1-1e-6).OrderBy(t=>t))expanded.Add(a.Lerp(b,t));
            }
            polygon=expanded.ToArray();
            var tool=new SurfaceTool();tool.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 P(int i,float side)=>new(polygon[i].X,polygon[i].Y,side*halfWidth(polygon[i]));
            var boundaries=new[]{polygon.Min(p=>p.X)}.Concat(cuts).Append(polygon.Max(p=>p.X)).OrderBy(x=>x).ToArray();
            for(int band=0;band<boundaries.Length-1;band++)
            {
                var cap=Clip(Clip(polygon,boundaries[band],true),boundaries[band+1],false);
                if(cap.Length<3)continue;var triangles=Geometry2D.TriangulatePolygon(cap);
                if(triangles.Length!=(cap.Length-2)*3)throw new InvalidOperationException("SUV body profile cannot be triangulated.");
                Vector3 C(int i,float side)=>new(cap[i].X,cap[i].Y,side*halfWidth(cap[i]));
                for(int i=0;i<triangles.Length;i+=3)
                {Triangle(tool,C(triangles[i],1),C(triangles[i+1],1),C(triangles[i+2],1));Triangle(tool,C(triangles[i],-1),C(triangles[i+2],-1),C(triangles[i+1],-1));}
            }
            for(int i=0;i<polygon.Length;i++){int j=(i+1)%polygon.Length;Triangle(tool,P(i,-1),P(j,-1),P(j,1));Triangle(tool,P(i,-1),P(j,1),P(i,1));}
            tool.Index();return tool.Commit();
        }
        private static Vector2[] Clip(Vector2[] polygon,float boundary,bool greater)
        {
            var result=new List<Vector2>();
            for(int i=0;i<polygon.Length;i++)
            {
                var a=polygon[i];var b=polygon[(i+1)%polygon.Length];bool ia=greater?a.X>=boundary-1e-6:a.X<=boundary+1e-6,ib=greater?b.X>=boundary-1e-6:b.X<=boundary+1e-6;
                if(ia)result.Add(a);
                if(ia!=ib && Math.Abs(b.X-a.X)>1e-6)result.Add(a.Lerp(b,(boundary-a.X)/(b.X-a.X)));
            }
            var clean=new List<Vector2>();foreach(var p in result)if(clean.Count==0 || clean[^1].DistanceSquaredTo(p)>1e-10)clean.Add(p);
            if(clean.Count>1 && clean[0].DistanceSquaredTo(clean[^1])<1e-10)clean.RemoveAt(clean.Count-1);return clean.ToArray();
        }
        private static Mesh Solid(Vector3[] vertices,int[][] faces){var tool=new SurfaceTool();tool.Begin(Mesh.PrimitiveType.Triangles);Faces(tool,vertices,faces);tool.Index();return tool.Commit();}
        private static void Faces(SurfaceTool tool,Vector3[] vertices,int[][] faces)
        {foreach(var face in faces)for(int i=1;i<face.Length-1;i++)Triangle(tool,vertices[face[0]],vertices[face[i]],vertices[face[i+1]]);}
        private static void Triangle(SurfaceTool tool,Vector3 a,Vector3 b,Vector3 c)
        {
            var normal=(b-a).Cross(c-a).Normalized();tool.SetNormal(normal);tool.AddVertex(a);tool.SetNormal(normal);tool.AddVertex(c);tool.SetNormal(normal);tool.AddVertex(b);
        }
        private static void AddMesh(Node3D root,string name,Mesh mesh,Material material)=>root.AddChild(new MeshInstance3D {Name=name,Mesh=mesh,MaterialOverride=material});
        private static MeshInstance3D Box(Node3D root,string name,Vector3 position,Vector3 size,Material material)
        {var mesh=new MeshInstance3D {Name=name,Position=position,Mesh=new BoxMesh {Size=size},MaterialOverride=material};root.AddChild(mesh);return mesh;}
        private static void Round(Node3D root,string name,Vector3 position,Vector3 size,float radius,Material material)=>root.AddChild(new MeshInstance3D {Name=name,Position=position,Mesh=CurvedMeshes.RoundedBox(size,radius),MaterialOverride=material});
        private static void Beam(Node3D root,string name,Vector3 a,Vector3 b,float width,Material material)=>BridgeRenderer.SetBeam(Box(root,name,Vector3.Zero,Vector3.One,material),a,b,width,width);
    }
}
