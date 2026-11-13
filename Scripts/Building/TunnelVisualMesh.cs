using System;
using System.Collections.Generic;
using Godot;

namespace BridgeArchitect3D.Building
{
    /// <summary>Hollow low-poly linings along local X. Section meshes remove the front half and cap the lining.</summary>
    public static class TunnelVisualMesh
    {
        private static readonly Dictionary<(string,float,float,float,float,bool),ArrayMesh> Cache = new();
        private static Vector2[] Profile(string shape, float width, float height)
        {
            float w = width / 2, h = height / 2;
            if (shape == "immersed")
            {
                float c = Mathf.Min(width, height) * 0.09f;
                return new[] { new Vector2(w,h-c),new Vector2(w-c,h),new Vector2(-w+c,h),new Vector2(-w,h-c),
                    new Vector2(-w,-h+c),new Vector2(-w+c,-h),new Vector2(w-c,-h),new Vector2(w,-h+c) };
            }
            var points = new List<Vector2>();
            if (shape == "floating")
            {
                float flat = Mathf.Max(0, w - h);
                for (int side = 0; side < 2; side++)
                    for (int i = 0; i <= 12; i++)
                    {
                        float angle = (-Mathf.Pi / 2 + side * Mathf.Pi) + i * Mathf.Pi / 12;
                        points.Add(new Vector2(Mathf.Cos(angle) * (w - flat) + (side == 0 ? flat : -flat), Mathf.Sin(angle) * h));
                    }
            }
            else
                for (int i = 0; i < 24; i++) { float angle = i * Mathf.Tau / 24; points.Add(new Vector2(Mathf.Cos(angle)*w,Mathf.Sin(angle)*h)); }
            return points.ToArray();
        }

        public static ArrayMesh Lining(string shape, float length, float width, float height, float wall, bool section)
        {
            var key = (shape,length,width,height,wall,section);
            if(Cache.TryGetValue(key,out var cached))return cached;
            var outer = Profile(shape,width,height); var inner = Profile(shape,width-2*wall,height-2*wall);
            var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 V(float x, Vector2 p) => new(x,p.Y,p.X);
            void Face(params Vector3[] vertices)
            {
                var polygon = new List<Vector3>(vertices);
                if (section)
                {
                    var clipped = new List<Vector3>();
                    for (int k = 0; k < polygon.Count; k++)
                    {
                        var a = polygon[k]; var b = polygon[(k+1)%polygon.Count];
                        bool inside = a.Z <= 0.00001f, nextInside = b.Z <= 0.00001f;
                        if (inside) clipped.Add(a);
                        if (inside != nextInside) clipped.Add(a.Lerp(b, -a.Z/(b.Z-a.Z)));
                    }
                    polygon = clipped;
                }
                for (int k = 1; k+1 < polygon.Count; k++)
                {
                    if ((polygon[k]-polygon[0]).Cross(polygon[k+1]-polygon[0]).LengthSquared()<1e-10f) continue;
                    // Godot front faces use clockwise winding.
                    surface.AddVertex(polygon[0]);surface.AddVertex(polygon[k+1]);surface.AddVertex(polygon[k]);
                }
            }
            float left = -length/2, right = length/2;
            for (int i = 0; i < outer.Length; i++)
            {
                int j = (i+1)%outer.Length;
                Face(V(left,outer[i]),V(right,outer[i]),V(right,outer[j]),V(left,outer[j]));
                Face(V(left,inner[j]),V(right,inner[j]),V(right,inner[i]),V(left,inner[i]));
                Face(V(left,outer[i]),V(left,outer[j]),V(left,inner[j]),V(left,inner[i]));
                Face(V(right,outer[j]),V(right,outer[i]),V(right,inner[i]),V(right,inner[j]));
            }
            if (section)
            {
                Face(new Vector3(left,height/2-wall,0),new Vector3(right,height/2-wall,0),new Vector3(right,height/2,0),new Vector3(left,height/2,0));
                Face(new Vector3(left,-height/2,0),new Vector3(right,-height/2,0),new Vector3(right,-height/2+wall,0),new Vector3(left,-height/2+wall,0));
            }
            surface.GenerateNormals();var mesh=surface.Commit();
            if(Cache.Count>=192)Cache.Clear();Cache[key]=mesh;return mesh;
        }

        /// <summary>Concrete below the road follows the inner lining instead of protruding through a curved tube.</summary>
        public static ArrayMesh Floor(string shape, float length, float width, float height, float wall, float floorHeight, out float floorWidth)
        {
            float top = -height/2+floorHeight;
            var profile = Profile(shape,width-2*wall,height-2*wall);
            var polygon = new List<Vector2>();
            for(int i=0;i<profile.Length;i++)
            {
                var a=profile[i];var b=profile[(i+1)%profile.Length];
                bool inside=a.Y<=top,nextInside=b.Y<=top;
                if(inside)polygon.Add(a);
                if(inside!=nextInside)polygon.Add(a.Lerp(b,(top-a.Y)/(b.Y-a.Y)));
            }
            float half=0;foreach(var p in polygon)if(Mathf.Abs(p.Y-top)<0.001f)half=Mathf.Max(half,Mathf.Abs(p.X));
            floorWidth=half*2;
            var surface=new SurfaceTool();surface.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 V(float x,Vector2 p)=>new(x,p.Y,p.X);
            void Triangle(Vector3 a,Vector3 b,Vector3 c){surface.AddVertex(a);surface.AddVertex(c);surface.AddVertex(b);}
            for(int i=1;i+1<polygon.Count;i++)
            {
                Triangle(V(-length/2,polygon[0]),V(-length/2,polygon[i]),V(-length/2,polygon[i+1]));
                Triangle(V(length/2,polygon[0]),V(length/2,polygon[i+1]),V(length/2,polygon[i]));
            }
            for(int i=0;i<polygon.Count;i++)
            {
                int j=(i+1)%polygon.Count;var a=V(-length/2,polygon[i]);var b=V(length/2,polygon[i]);var c=V(length/2,polygon[j]);var d=V(-length/2,polygon[j]);
                Triangle(a,b,c);Triangle(a,c,d);
            }
            surface.GenerateNormals();return surface.Commit();
        }
    }
}
