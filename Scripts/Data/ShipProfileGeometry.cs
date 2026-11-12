using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace BridgeArchitect3D.Data
{
    /// <summary>Visible fittings and convex sails/glazing are also the collision geometry.</summary>
    public static class ShipProfileGeometry
    {
        private static readonly ConcurrentDictionary<(string,double,double,double),ShipSolid[]> Cache=new();
        public static IEnumerable<ShipBox> Parts(TrafficEvent e)
        {
            double w=e.Width,l=e.Length,h=e.Height,d=ShipCatalog.HullHeight(e);string kind=ShipCatalog.Kind(e);
            if(kind=="sailboat")
            {
                yield return new(0,d+0.19,-l*0.1,w*0.66,0.36,l*0.4,1);
                yield return new(0,(d+h)/2,-l*0.05,0.085,h-d,0.085,5);
                yield return new(0,d+0.75,l*0.155,0.07,0.07,l*0.41,5);
                yield return new(0,h-0.22,-l*0.05-0.18,0.025,0.3,0.35,7);
                for(int i=1;i<5;i++)
                {
                    double fraction=i*0.18,span=l*0.395*(1-fraction);
                    yield return new(w*0.013,d+0.75+(h-0.2-d-0.75)*fraction,-l*0.035+span/2,0.018,0.018,span,1);
                }
                for(int side=-1;side<=1;side+=2)
                {
                    yield return new(side*w*0.332,d+0.2,-l*0.12,0.025,0.14,l*0.23,2);
                    yield return new(side*w*0.25,d+0.27,-l*0.3,w*0.12,0.12,l*0.15,4);
                }
                yield return new(0,d+0.38,-l*0.1,w*0.3,0.035,l*0.12,2);
                yield return new(0,-0.38,-l*0.05,w*0.05,0.85,l*0.17,5);
            }
            else if(kind=="speedboat")
            {
                yield return new(0,d+0.05,-l*0.06,w*0.82,0.1,l*0.7,1);
                for(int side=-1;side<=1;side+=2)
                {
                    yield return new(side*w*0.22,d+0.2,-l*0.15,w*0.23,0.3,l*0.2,1);
                    yield return new(side*w*0.22,d+0.45,-l*0.24,w*0.23,0.45,0.16,1);
                }
                yield return new(0,d+0.21,-l*0.42,w*0.78,0.35,0.18,0);
                yield return new(0,d*0.2,-l*0.47,w*0.22,d*1.3,0.27,5);
                yield return new(0,h*0.82,l*0.08,0.06,h*0.36,0.06,5);
                yield return new(0,h-0.025,l*0.08,0.11,0.05,0.11,8);
            }
            else if(kind=="tugboat")
            {
                yield return new(0,d+h*0.265,l*0.04,w*0.63,h*0.53,l*0.36,1);
                yield return new(0,h*0.68,l*0.08,w*0.64,h*0.14,l*0.31,2);
                yield return new(0,h*0.8,l*0.05,w*0.72,0.12,l*0.4,1);
                yield return new(0,h*0.57,-l*0.25,w*0.2,h*0.64,l*0.12,0);
                yield return new(0,h*0.905,-l*0.25,w*0.24,0.09,l*0.14,5);
                yield return new(0,h*0.87,l*0.13,0.04,h*0.26,0.04,5);
                yield return new(0,d+0.2,-l*0.1,w*0.32,0.2,0.13,5);
            }
            foreach(var part in Details(e))yield return part;
        }
        public static IEnumerable<ShipBox> Details(TrafficEvent e)
        {
            double w=e.Width,l=e.Length,d=ShipCatalog.HullHeight(e);string kind=ShipCatalog.Kind(e);
            for(int side=-1;side<=1;side+=2)
            {
                // Gunwale rails, stanchions, rubbing strips, cleats and navigation lights.
                yield return new(side*w*0.43,d+0.26,-l*0.02,0.035,0.035,l*0.68,5);
                yield return new(side*w*0.47,d*0.68,-l*0.08,w*0.03,0.07,l*0.7,2);
                for(int i=0;i<5;i++)yield return new(side*w*0.43,d+0.13,-l*0.32+i*l*0.15,0.03,0.26,0.03,5);
                for(int i=0;i<2;i++)yield return new(side*w*0.33,d+0.04,l*(-0.32+i*0.64),0.14,0.08,0.04,5);
                yield return new(side*w*0.35,d+0.29,l*0.26,0.07,0.08,0.1,side<0?7:8);
            }
            if(kind=="cargo")
            {
                double cabin=Math.Max(0.05,e.Height-d-0.5);
                for(int side=-1;side<=1;side+=2)
                {
                    for(int i=0;i<4;i++)yield return new(side*w*0.352,d+cabin*0.63,-l*0.27+i*l*0.075,0.035,cabin*0.25,l*0.045,2);
                    yield return new(side*w*0.18,e.Height-0.48,-l*0.15,w*0.25,0.035,l*0.1,1);
                    yield return new(side*w*0.18,e.Height-0.455,-l*0.15,w*0.22,0.018,l*0.085,2);
                }
                for(int i=0;i<3;i++)for(int rib=0;rib<7;rib++)
                    yield return new(-w*0.25+rib*w/12,d+0.4,l*(0.12+i*0.1)+l*0.044,0.03,0.57,0.025,5);
                yield return new(0,e.Height-0.36,-l*0.3,0.58,0.07,0.58,7);
            }
        }
        public static ShipSolid[] ExtraSolids(TrafficEvent e)=>Cache.GetOrAdd((ShipCatalog.Kind(e),e.Width,e.Height,e.Length),_=>Build(e));
        private static ShipSolid[] Build(TrafficEvent e)
        {
            double d=ShipCatalog.HullHeight(e),l=e.Length,w=e.Width,h=e.Height;
            var result=new List<ShipSolid>();
            if(ShipCatalog.Kind(e)=="sailboat")
                result.AddRange(new[]{Prism(new[]{new Vec3d(0,d+0.75,-l*0.035),new Vec3d(0,h-0.2,-l*0.035),new Vec3d(0,d+0.75,l*0.36)},w*0.012,6),
                    Prism(new[]{new Vec3d(0,d+0.6,-l*0.09),new Vec3d(0,h*0.76,-l*0.09),new Vec3d(0,d+0.6,-l*0.4)},w*0.012,6)});
            if(ShipCatalog.Kind(e)=="speedboat")
                result.Add(new ShipSolid(new[]{new Vec3d(-w*0.36,d+0.08,l*0.06),new Vec3d(w*0.36,d+0.08,l*0.06),new Vec3d(w*0.29,d+0.55,-l*0.03),new Vec3d(-w*0.29,d+0.55,-l*0.03),new Vec3d(-w*0.36,d+0.08,l*0.08),new Vec3d(w*0.36,d+0.08,l*0.08),new Vec3d(w*0.29,d+0.55,-l*0.01),new Vec3d(-w*0.29,d+0.55,-l*0.01)},new[]{new[]{0,1,2,3},new[]{7,6,5,4},new[]{0,4,5,1},new[]{1,5,6,2},new[]{2,6,7,3},new[]{3,7,4,0}},2));
            for(int side=-1;side<=1;side+=2)
            {
                if(ShipCatalog.Kind(e)=="cargo")
                {
                    double cabin=Math.Max(0.05,h-d-0.5),radius=Math.Min(0.24,cabin*0.2);
                    result.AddRange(Ring(side*(w*0.35+0.045),d+Math.Min(0.55,cabin*0.45),-l*0.29,radius,radius*0.55,0.04,7));
                }
                if(ShipCatalog.Kind(e)=="tugboat")
                    for(int i=0;i<3;i++)result.AddRange(Ring(side*w*0.46,d*0.55,-l*0.3+i*l*0.3,0.23,0.12,w*0.035,9));
            }
            return result.ToArray();
        }
        private static IEnumerable<ShipSolid> Ring(double x,double y,double z,double outer,double inner,double halfDepth,int material)
        {
            for(int i=0;i<8;i++)
            {
                double a=i*Math.PI/4,b=(i+1)*Math.PI/4;
                var yz=new[]{(y+outer*Math.Sin(a),z+outer*Math.Cos(a)),(y+outer*Math.Sin(b),z+outer*Math.Cos(b)),(y+inner*Math.Sin(b),z+inner*Math.Cos(b)),(y+inner*Math.Sin(a),z+inner*Math.Cos(a))};
                var vertices=new Vec3d[8];for(int j=0;j<4;j++){vertices[j]=new(x-halfDepth,yz[j].Item1,yz[j].Item2);vertices[j+4]=new(x+halfDepth,yz[j].Item1,yz[j].Item2);}
                yield return new(vertices,new[]{new[]{0,1,2,3},new[]{7,6,5,4},new[]{0,4,5,1},new[]{1,5,6,2},new[]{2,6,7,3},new[]{3,7,4,0}},material==7 && i%4==0?1:material);
            }
        }
        private static ShipSolid Prism(Vec3d[] triangle,double halfWidth,int material)
        {
            var vertices=new Vec3d[6];for(int i=0;i<3;i++){vertices[i]=new(-halfWidth,triangle[i].Y,triangle[i].Z);vertices[i+3]=new(halfWidth,triangle[i].Y,triangle[i].Z);}
            return new(vertices,new[]{new[]{0,2,1},new[]{3,4,5},new[]{0,1,4,3},new[]{1,2,5,4},new[]{2,0,3,5}},material);
        }
    }
}
