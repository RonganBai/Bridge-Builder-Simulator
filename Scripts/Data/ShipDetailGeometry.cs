using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    // Low-sided convex forms are used by both the visible mesh and the contact solver.
    public static class ShipDetailGeometry
    {
        public static bool Shaped(ShipBox b)=>
            (b.Material==1 && b.Width>.4 && b.Length>.5 && b.Height>.07) ||
            (b.Material==4 && b.Width>.8 && b.Length>.5);
        public static ShipSolid Shape(ShipBox b)
        {
            bool mound=b.Material==4;
            double bevel=Math.Min(.14,Math.Min(b.Width,b.Length)*.12);
            double hw=b.Width/2,hl=b.Length/2;
            var ring=new[]{(-hw+bevel,-hl),(hw-bevel,-hl),(hw,-hl+bevel),(hw,hl-bevel),
                (hw-bevel,hl),(-hw+bevel,hl),(-hw,hl-bevel),(-hw,-hl+bevel)};
            var vertices=new List<Vec3d>();
            // A bevelled roof and continuous shoulders replace sharp stacked boxes.
            foreach(int level in new[]{0,1,2})
            {
                double shrink=level==2?(mound?.52:.94):1;
                double y=b.Y-b.Height/2+(level==0?0:level==1?b.Height*.76:b.Height);
                foreach(var p in ring)vertices.Add(new(b.X+p.Item1*shrink,y,b.Z+p.Item2*shrink));
            }
            var faces=new List<int[]>{Enumerable.Range(0,8).Reverse().ToArray(),Enumerable.Range(16,8).ToArray()};
            for(int level=0;level<2;level++)for(int i=0;i<8;i++)
                faces.Add(new[]{level*8+i,level*8+(i+1)%8,(level+1)*8+(i+1)%8,(level+1)*8+i});
            return new(vertices.ToArray(),faces.ToArray(),b.Material);
        }
        private static ShipSolid Rod(Vec3d a,Vec3d b,double radius,int material)
        {
            double dx=b.X-a.X,dy=b.Y-a.Y,dz=b.Z-a.Z;
            double length=Math.Sqrt(dx*dx+dy*dy+dz*dz);dx/=length;dy/=length;dz/=length;
            double rx=-dz,ry=0,rz=dx,rlen=Math.Sqrt(rx*rx+rz*rz);
            if(rlen<1e-6){rx=1;rz=0;rlen=1;}rx/=rlen;rz/=rlen;
            double ux=dy*rz,uy=dz*rx-dx*rz,uz=-dy*rx;
            var v=new List<Vec3d>();
            foreach(var point in new[]{a,b})for(int i=0;i<6;i++)
            {
                double c=Math.Cos(i*Math.PI/3)*radius,s=Math.Sin(i*Math.PI/3)*radius;
                v.Add(new(point.X+rx*c+ux*s,point.Y+ry*c+uy*s,point.Z+rz*c+uz*s));
            }
            var faces=new List<int[]>{new[]{5,4,3,2,1,0},new[]{6,7,8,9,10,11}};
            for(int i=0;i<6;i++)faces.Add(new[]{i,(i+1)%6,(i+1)%6+6,i+6});
            return new(v.ToArray(),faces.ToArray(),material);
        }
        public static IEnumerable<ShipSolid> Fittings(TrafficEvent e)
        {
            double w=e.Width,l=e.Length,d=ShipCatalog.HullHeight(e),h=e.Height;
            string kind=ShipCatalog.Kind(e);
            // Bollards and a short crossbar give the bow and stern usable deck equipment.
            foreach(double z in new[]{-l*.35,l*.29})foreach(int side in new[]{-1,1})
            {
                double x=side*w*.29;
                yield return Rod(new(x,d+.03,z),new(x,d+.18,z),.045,5);
                yield return Rod(new(x-.10,d+.16,z),new(x+.10,d+.16,z),.025,5);
            }
            if(kind is "sailboat" or "folding_sail")
            {
                double top=kind=="sailboat"?h-.30:d+1.38;
                foreach(int side in new[]{-1,1})yield return Rod(new(0,top,-l*.05),new(side*w*.37,d+.12,-l*.21),.012,5);
                if(kind=="sailboat")yield return Rod(new(0,top,-l*.05),new(0,d+.10,l*.38),.012,5);
            }
            if(kind is "tugboat" or "tow_barge" or "fishing")
            {
                // Eight segments leave a genuine open centre in each rubber fender.
                for(int side=-1;side<=1;side+=2)for(int i=0;i<3;i++)for(int segment=0;segment<8;segment++)
                {
                    double z=kind=="tow_barge"?-l*.45+i*l*.065:-l*.27+i*l*.24;
                    double a=segment*Math.PI/4,b=(segment+1)*Math.PI/4;
                    yield return Rod(new(side*w*(kind=="tow_barge"?.32:.46),d*.65+.18*Math.Sin(a),z+.18*Math.Cos(a)),
                        new(side*w*(kind=="tow_barge"?.32:.46),d*.65+.18*Math.Sin(b),z+.18*Math.Cos(b)),.05,9);
                }
            }
            if(kind is "fireboat" or "fishing")
                yield return Rod(new(0,d+.12,l*.17),new(0,d+.55,l*.17),.17,5);
        }
    }
}
