using System;
using Godot;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.World
{
    /// <summary>Small faceted curves preserve silhouettes without smooth high-resolution primitives.</summary>
    public static class CurvedMeshes
    {
        private static void Triangle(SurfaceTool st,Vector3 a,Vector3 b,Vector3 c)
        {
            // Godot uses clockwise front faces; keep outward normals while reversing the vertex order.
            var normal=(b-a).Cross(c-a).Normalized();st.SetNormal(normal);st.AddVertex(a);st.SetNormal(normal);st.AddVertex(c);st.SetNormal(normal);st.AddVertex(b);
        }
        public static Mesh Extrude(Vector2[] outline,float depth)
        {
            var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);int n=outline.Length;
            Vector3 P(int i,float z)=>new(outline[i].X,outline[i].Y,z);
            for(int i=0;i<n;i++)
            {
                int j=(i+1)%n;Triangle(st,P(i,-depth/2),P(j,depth/2),P(i,depth/2));Triangle(st,P(i,-depth/2),P(j,-depth/2),P(j,depth/2));
                if(i>0 && i<n-1){Triangle(st,P(0,depth/2),P(i,depth/2),P(j,depth/2));Triangle(st,P(0,-depth/2),P(j,-depth/2),P(i,-depth/2));}
            }
            st.Index();return st.Commit();
        }
        public static Mesh RoundedBox(Vector3 size,float radius)
        {
            radius=Math.Min(radius,Math.Min(size.X,size.Z)*0.45f);
            var shape=new Vector2[16];
            for(int corner=0;corner<4;corner++)for(int step=0;step<4;step++)
            {
                float angle=Mathf.Pi/2*(corner+step/3f);float cx=(corner==0 || corner==3?1:-1)*(size.X/2-radius),cz=(corner<2?1:-1)*(size.Z/2-radius);
                shape[corner*4+step]=new Vector2(cx+Mathf.Cos(angle)*radius,cz+Mathf.Sin(angle)*radius);
            }
            // The source profile is XZ; rotate extrusion depth into the vertical axis.
            var mesh=Extrude(shape,size.Y);var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);st.AppendFrom(mesh,0,new Transform3D(new Basis(Vector3.Right,Mathf.Pi/2),Vector3.Zero));st.Index();return st.Commit();
        }
        public static void Windshield(Node3D parent,float bottomX,float bottomY,float topX,float topY,float width,float bulge,Material glass)
        {
            var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 P(int row,int col){float t=col/6f;return new Vector3((row==0?bottomX:topX)+bulge*(1-Mathf.Pow(2*t-1,2)),row==0?bottomY:topY,(t-0.5f)*width);}
            for(int i=0;i<6;i++){var a=P(0,i);var b=P(0,i+1);var c=P(1,i+1);var d=P(1,i);Triangle(st,a,b,c);Triangle(st,a,c,d);Triangle(st,c,b,a);Triangle(st,d,c,a);}
            st.Index();parent.AddChild(new MeshInstance3D{Mesh=st.Commit(),MaterialOverride=glass});
        }
        public static Vector2[] Stadium(float halfStraight,float radius)
        {
            var points=new Vector2[16];
            for(int i=0;i<8;i++){float a=-Mathf.Pi/2+i*Mathf.Pi/7;points[i]=new(halfStraight+Mathf.Cos(a)*radius,Mathf.Sin(a)*radius);points[i+8]=new(-halfStraight-Mathf.Cos(a)*radius,-Mathf.Sin(a)*radius);}
            return points;
        }
        public static void Track(Node3D parent,float length,float z,float depth,Material rubber,Material tread)
        {
            var outer=Stadium(length/2-0.48f,0.48f);var inner=Stadium(length/2-0.48f,0.32f);var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 P(Vector2 p,float side)=>new(p.X-0.6f,p.Y+0.5f,z+side*depth/2);
            for(int i=0;i<outer.Length;i++)
            {
                int j=(i+1)%outer.Length;
                foreach(float side in new[]{-1f,1f}){var a=P(outer[i],side);var b=P(outer[j],side);var c=P(inner[j],side);var d=P(inner[i],side);Triangle(st,a,b,c);Triangle(st,a,c,d);Triangle(st,c,b,a);Triangle(st,d,c,a);}
                foreach(var ring in new[]{outer,inner}){Triangle(st,P(ring[i],-1),P(ring[j],-1),P(ring[j],1));Triangle(st,P(ring[i],-1),P(ring[j],1),P(ring[i],1));}
                var mid=(outer[i]+outer[j])/2;var delta=outer[j]-outer[i];
                parent.AddChild(new MeshInstance3D{Mesh=new BoxMesh{Size=new Vector3(delta.Length()*0.7f,0.055f,depth+0.04f)},Position=new(mid.X-0.6f,mid.Y+0.5f,z),RotationDegrees=new(0,0,Mathf.RadToDeg(Mathf.Atan2(delta.Y,delta.X))),MaterialOverride=tread});
            }
            st.Index();parent.AddChild(new MeshInstance3D{Mesh=st.Commit(),MaterialOverride=rubber});
        }
        public static Mesh Solid(ShipSolid solid)
        {
            var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);
            foreach(var face in solid.Faces)for(int i=1;i<face.Length-1;i++)
            {Vector3 P(int n){var p=solid.Vertices[n];return new((float)p.X,(float)p.Y,(float)p.Z);}Triangle(st,P(face[0]),P(face[i]),P(face[i+1]));Triangle(st,P(face[i+1]),P(face[i]),P(face[0]));}
            st.Index();return st.Commit();
        }
    }
}

