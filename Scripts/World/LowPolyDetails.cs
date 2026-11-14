using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.World
{
    public static class LowPolyDetails
    {
        private static readonly BoxMesh Cube=new(){Size=Vector3.One};
        private static readonly StandardMaterial3D Paint=new(){AlbedoColor=new Color("e4e6df"),Roughness=0.55f};
        private static readonly StandardMaterial3D Ink=new(){AlbedoColor=new Color("242f38"),Roughness=0.8f};
        private static readonly StandardMaterial3D Alloy=new(){AlbedoColor=new Color("9caeb2"),Metallic=0.65f,Roughness=0.35f};
        private static readonly StandardMaterial3D Amber=new(){AlbedoColor=new Color("eea834"),Roughness=0.7f};
        public static void Box(Node3D parent,Vector3 position,Vector3 size,Material material)
            =>parent.AddChild(new MeshInstance3D{Mesh=Cube,Position=position,Scale=size,MaterialOverride=material});

        public static void DressVehicle(Node3D root,VehicleDefinition def,int unit)
        {
            float w=(float)def.Width,h=(float)def.Height,l=(float)def.Length;
            float rear=(float)def.Axles.Select(a=>a.Offset).DefaultIfEmpty(0).Min();
            float front=Math.Max(0.3f,(l+rear)/2),cx=rear/2;
            void B(float x,float y,float z,float sx,float sy,float sz,Material material)=>Box(root,new(x,y,z),new(sx,sy,sz),material);
            switch(def.Kind)
            {
                case "car":
                    root.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.RoundedBox(new Vector3(l*0.35f,0.08f,w*0.87f),0.12f),Position=new Vector3(cx-0.15f,1.47f,0),MaterialOverride=Paint});
                    B(front+0.065f,0.89f,0,0.03f,0.18f,w*0.28f,Ink);
                    B(front+0.07f,0.62f,0,0.03f,0.12f,0.42f,Paint);
                    foreach(int side in new[]{-1,1})
                    {
                        B(cx-0.12f,1.3f,side*w*0.445f,0.09f,0.42f,0.03f,Ink);
                        B(cx-0.55f,0.99f,side*w*0.503f,0.28f,0.035f,0.025f,Alloy);
                        B(cx+0.35f,0.99f,side*w*0.503f,0.25f,0.035f,0.025f,Alloy);
                        B(cx,0.58f,side*w*0.505f,l*0.66f,0.055f,0.025f,Ink);
                        B(cx-0.7f,1.28f,side*w*0.447f,0.045f,0.42f,0.025f,Paint);
                    }
                    break;
                case "truck":
                    float back=rear-front,boxLength=l-2.6f;
                    foreach(int side in new[]{-1,1})
                    {
                        B(front-1.4f,1.32f,side*w*0.505f,0.28f,0.045f,0.025f,Alloy);
                        B(front-1.9f,0.75f,side*w*0.51f,0.7f,0.12f,0.12f,Alloy);
                        B(front-2.6f-boxLength/2,1.1f,side*w*0.51f,boxLength*0.92f,0.09f,0.025f,Amber);
                        foreach(float offset in new[]{0.3f,0.7f})B(back-0.015f,h*offset,side*w*0.38f,0.035f,0.16f,0.18f,Alloy);
                        foreach(var axle in def.Axles)B((float)axle.Offset-0.5f,0.37f,side*(w/2-0.1f),0.06f,0.42f,0.38f,Ink);
                    }
                    B(back-0.02f,h*0.52f,0,0.025f,h*0.66f,0.045f,Ink);
                    for(int i=0;i<3;i++)B(front+0.065f,1.06f+i*0.13f,0,0.025f,0.035f,w*0.62f,Alloy);
                    B(front-1.2f,2.55f,0,0.45f,0.12f,0.65f,Paint);
                    break;
                case "bus":
                    for(int i=0;i<7;i++)foreach(int side in new[]{-1,1})
                        B(front-l*0.12f-i*l*0.115f,0.45f+h*0.66f,side*w*0.509f,0.07f,h*0.32f,0.025f,Paint);
                    foreach(int side in new[]{-1,1})
                    {
                        B(cx,1.08f,side*w*0.51f,l*0.98f,0.12f,0.025f,Paint);
                        for(int i=0;i<4;i++)B(rear-front+0.5f,0.95f+i*0.14f,side*w*0.51f,0.7f,0.035f,0.025f,Ink);
                    }
                    B(front+0.04f,0.45f+h*0.92f,0,0.025f,0.14f,w*0.4f,Ink);
                    B(front+0.09f,0.7f,0,0.025f,0.12f,0.55f,Paint);
                    break;
                case "train":
                    float ul=(float)(def.UnitLength>0?def.UnitLength:l/Math.Max(1,def.Units)),cl=ul-0.6f;
                    foreach(int side in new[]{-1,1})
                    {
                        B(-cl/2,1.12f,side*w*0.507f,cl*0.96f,0.15f,0.025f,Amber);
                        for(int i=0;i<(unit==0?8:6);i++)B(-3-i*(cl-4)/8,unit==0?2.45f:2.7f,side*w*0.512f,unit==0?0.11f:0.045f,unit==0?1.1f:2.25f,0.025f,unit==0?Ink:Alloy);
                        B(-0.7f,1.15f,side*w*0.51f,0.5f,0.1f,0.22f,Alloy);
                        B(-cl+0.5f,1.15f,side*w*0.51f,0.5f,0.1f,0.22f,Alloy);
                    }
                    if(unit==0){B(-4.5f,4.14f,0,2,0.12f,w*0.65f,Ink);B(-1,3.6f,0,0.06f,0.8f,w*1.02f,Paint);}
                    else B(-cl/2,4.16f,0,cl*0.88f,0.08f,w*0.8f,Alloy);
                    break;
                case "tank":
                    for(int i=0;i<5;i++)foreach(int side in new[]{-1,1})
                    {
                        B(1.5f-i*1.03f,1.05f,side*w*0.46f,0.9f,0.45f,0.07f,Alloy);
                        B(1.5f-i*1.03f,1.17f,side*w*0.475f,0.08f,0.08f,0.025f,Ink);
                    }
                    B(-1.2f,2.36f,-0.4f,0.5f,0.12f,0.55f,Ink);
                    B(-2,2.32f,0.6f,0.025f,0.38f,0.025f,Ink);
                    for(int i=0;i<4;i++)B(-2.6f+i*0.22f,1.57f,0,0.08f,0.025f,w*0.48f,Ink);
                    break;
                case "pedestrians":
                    float z=unit%2==0?-0.6f:0.6f;
                    B(-0.23f,1.2f,z,0.16f,0.43f,0.35f,Amber);
                    B(0,1.75f,z,0.25f,0.08f,0.27f,Ink);
                    foreach(int side in new[]{-1,1})B(0,1.17f,z+side*0.25f,0.13f,0.47f,0.12f,Paint);
                    break;
            }
            // Keep the model within the vehicle's physical clearance height.
            float top=0;
            foreach(var mesh in root.GetChildren().OfType<MeshInstance3D>())
            {
                var bounds=mesh.Transform*mesh.GetAabb();top=Math.Max(top,bounds.End.Y);
            }
            if(top>h)foreach(var mesh in root.GetChildren().OfType<MeshInstance3D>())
            {var t=mesh.Transform;t.Origin=new Vector3(t.Origin.X,t.Origin.Y*h/top,t.Origin.Z);t.Basis=new Basis(t.Basis.X*new Vector3(1,h/top,1),t.Basis.Y*new Vector3(1,h/top,1),t.Basis.Z*new Vector3(1,h/top,1));mesh.Transform=t;}
            Batch(root);
        }

        /// <summary>One mesh per material and unit keeps added detail from multiplying draw calls.</summary>
        public static void Batch(Node3D root)
        {
            var meshes=new List<(MeshInstance3D mesh,Transform3D transform)>();
            void Collect(Node3D node,Transform3D transform)
            {
                // Animated parts (tank tracks, road wheels: meta "animated") keep their own nodes so they can move
                foreach(var child in node.GetChildren().OfType<Node3D>().Where(c=>!c.HasMeta("animated")))
                {
                    if(child.HasMeta("vehicle_variant")){Batch(child);continue;}
                    var local=transform*child.Transform;
                    if(child is MeshInstance3D mesh && mesh.Mesh!=null)meshes.Add((mesh,local));
                    else Collect(child,local);
                }
            }
            Collect(root,Transform3D.Identity);
            // Merge vertex arrays directly in C#: the former SurfaceTool CreateFrom / Index / Commit per source mesh
            // registered a temporary mesh with the renderer each time and made the menu scene take seconds to build.
            foreach(var group in meshes.GroupBy(m=>m.mesh.MaterialOverride))
            {
                var verts=new List<Vector3>();var normals=new List<Vector3>();var uvs=new List<Vector2>();var indices=new List<int>();
                bool ok=true;
                foreach(var item in group)
                {
                    var src=Source(item.mesh.Mesh);
                    if(src==null){ok=false;break;}
                    var (v,n,uv,idx)=src.Value;int start=verts.Count;
                    var basis=item.transform.Basis;if(Math.Abs(basis.Determinant())<1e-12)continue; // degenerate (zero-size) source: nothing visible
                    var normalBasis=basis.Inverse().Transposed();
                    for(int i=0;i<v.Length;i++)
                    {
                        verts.Add(item.transform*v[i]);
                        normals.Add(n!=null&&n.Length==v.Length?(normalBasis*n[i]).Normalized():Vector3.Up);
                        uvs.Add(uv!=null&&uv.Length==v.Length?uv[i]:Vector2.Zero);
                    }
                    // A mirroring transform flips the winding; swap two corners to keep faces pointing outwards
                    bool flip=basis.Determinant()<0;
                    for(int i=0;i+2<idx.Length;i+=3)
                    {
                        indices.Add(start+idx[i]);
                        if(flip){indices.Add(start+idx[i+2]);indices.Add(start+idx[i+1]);}
                        else{indices.Add(start+idx[i+1]);indices.Add(start+idx[i+2]);}
                    }
                }
                if(!ok || verts.Count==0)continue;
                var arrays=new Godot.Collections.Array();arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex]=verts.ToArray();arrays[(int)Mesh.ArrayType.Normal]=normals.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV]=uvs.ToArray();arrays[(int)Mesh.ArrayType.Index]=indices.ToArray();
                var combined=new ArrayMesh();combined.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
                root.AddChild(new MeshInstance3D{Mesh=combined,MaterialOverride=group.Key});
                foreach(var item in group){item.mesh.GetParent().RemoveChild(item.mesh);item.mesh.QueueFree();}
            }
        }

        // Surface 0 arrays of a source mesh. Primitive meshes are generated on the CPU (GetMeshArrays) instead of
        // being read back from the renderer, and identical primitives share one cached result.
        private static readonly Dictionary<string,(Vector3[] v,Vector3[] n,Vector2[] uv,int[] idx)?> SourceCache=new();
        private static string PrimitiveKey(PrimitiveMesh p)=>p switch
        {
            BoxMesh b=>$"box{b.Size}{b.SubdivideWidth},{b.SubdivideHeight},{b.SubdivideDepth}",
            SphereMesh sp=>$"sph{sp.Radius},{sp.Height},{sp.RadialSegments},{sp.Rings},{sp.IsHemisphere}",
            CylinderMesh c=>$"cyl{c.TopRadius},{c.BottomRadius},{c.Height},{c.RadialSegments},{c.Rings},{c.CapTop},{c.CapBottom}",
            _=>null,
        };
        private static (Vector3[] v,Vector3[] n,Vector2[] uv,int[] idx)? Source(Mesh mesh)
        {
            string key=mesh is PrimitiveMesh pm?PrimitiveKey(pm):null;
            if(key!=null && SourceCache.TryGetValue(key,out var cached))return cached;
            Godot.Collections.Array arrays=null;
            if(mesh is PrimitiveMesh p)arrays=p.GetMeshArrays();
            else if(CurvedMeshes.TryGetArrays(mesh,out var built))arrays=built;
            else if(mesh.GetSurfaceCount()>0 && (mesh is not ArrayMesh am || am.SurfaceGetPrimitiveType(0)==Mesh.PrimitiveType.Triangles))arrays=mesh.SurfaceGetArrays(0);
            (Vector3[] v,Vector3[] n,Vector2[] uv,int[] idx)? result=null;
            if(arrays!=null && arrays.Count>(int)Mesh.ArrayType.Index)
            {
                var v=arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var n=arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var uv=arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                var idx=arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                if(idx==null || idx.Length==0)idx=Enumerable.Range(0,v.Length-v.Length%3).ToArray();
                result=(v,n,uv,idx);
            }
            if(key!=null)SourceCache[key]=result;
            return result;
        }
    }
}

