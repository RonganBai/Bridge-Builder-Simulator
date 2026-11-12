using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;

namespace BridgeArchitect3D.Data
{
    public sealed record ShipSolid(Vec3d[] Vertices,int[][] Faces,int Material)
    {
        public double MinZ { get; }=Vertices.Min(v=>v.Z);
        public double MaxZ { get; }=Vertices.Max(v=>v.Z);
        private static readonly ConcurrentDictionary<(double,double,double,bool,string),ShipSolid> Cache=new();
        public static ShipSolid Hull(TrafficEvent e,bool deck=false)
        {
            var key=(e.Width,e.Height,e.Length,deck,ShipCatalog.Kind(e));
            if(Cache.TryGetValue(key,out var solid))return solid;
            solid=BuildHull(e,deck);Cache.TryAdd(key,solid);return solid;
        }
        private static ShipSolid BuildHull(TrafficEvent e,bool deck)
        {
            double w=e.Width/2,l=e.Length/2,h=ShipCatalog.HullHeight(e);
            var outline=new[]{new Vec3d(-w,0,-l),new Vec3d(w,0,-l),new Vec3d(w,0,l*0.4),new Vec3d(w*0.9,0,l*0.67),new Vec3d(w*0.65,0,l*0.88),new Vec3d(w*0.25,0,l),new Vec3d(-w*0.25,0,l),new Vec3d(-w*0.65,0,l*0.88),new Vec3d(-w*0.9,0,l*0.67),new Vec3d(-w,0,l*0.4)};
            int n=outline.Length;var vertices=new Vec3d[n*2];
            for(int i=0;i<n;i++){vertices[i]=new(outline[i].X*(deck?0.96:0.78),deck?h-0.005:-ShipCatalog.Draft(e),outline[i].Z*(deck?0.96:0.78));vertices[i+n]=new(outline[i].X*(deck?0.96:1),deck?h+0.045:h,outline[i].Z*(deck?0.96:1));}
            var faces=new List<int[]> {Enumerable.Range(0,n).Reverse().ToArray(),Enumerable.Range(n,n).ToArray()};
            for(int i=0;i<n;i++)faces.Add(new[]{i,(i+1)%n,(i+1)%n+n,i+n});
            return new(vertices,faces.ToArray(),deck?1:0);
        }
        private static double Dot(Vec3d a,Vec3d b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        private static Vec3d Cross(Vec3d a,Vec3d b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
        public bool Intersects(Vec3d offset,Vec3d center,Vec3d along,double halfLength,double halfThickness,double halfDepth)
        {
            Vec3d[] boxAxes={along,new(-along.Y,along.X,0),new(0,0,1)};
            bool Separates(Vec3d axis)
            {
                if(axis.Length()<1e-10)return false;
                double middle=Dot(center-offset,axis),radius=halfLength*Math.Abs(Dot(along,axis))+halfThickness*Math.Abs(Dot(boxAxes[1],axis))+halfDepth*Math.Abs(axis.Z);
                double lo=double.PositiveInfinity,hi=double.NegativeInfinity;
                foreach(var vertex in Vertices){double p=Dot(vertex,axis);lo=Math.Min(lo,p);hi=Math.Max(hi,p);}
                return middle+radius<lo || middle-radius>hi;
            }
            foreach(var axis in boxAxes)if(Separates(axis))return false;
            foreach(var face in Faces)
            {
                if(Separates(Cross(Vertices[face[1]]-Vertices[face[0]],Vertices[face[2]]-Vertices[face[0]])))return false;
                for(int i=0;i<face.Length;i++)foreach(var axis in boxAxes)
                    if(Separates(Cross(Vertices[face[(i+1)%face.Length]]-Vertices[face[i]],axis)))return false;
            }
            return true;
        }
    }
}
