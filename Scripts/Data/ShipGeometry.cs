using System;
using System.Collections.Generic;

namespace BridgeArchitect3D.Data
{
    public readonly record struct ShipBox(double X, double Y, double Z, double Width, double Height, double Length, int Material);
    public static class ShipGeometry
    {
        public static IEnumerable<ShipBox> Parts(TrafficEvent e)
        {
            if(ShipCatalog.Kind(e)!="cargo")
            {foreach(var part in ShipProfileGeometry.Parts(e))yield return part;yield break;}
            double h=e.Height,w=e.Width,l=e.Length,hull=h*0.4,cabin=Math.Max(0.05,h-hull-0.5);
            // The curved hull and deck are shared convex solids rather than bounding boxes.
            yield return new(0,hull+cabin/2,-l*0.15,w*0.7,cabin,l*0.35,1);
            yield return new(0,hull+cabin*0.75,-l*0.15+l*0.175+0.01,w*0.6,cabin*0.25,0.05,2);
            yield return new(0,h-0.25,-l*0.3,0.5,0.5,0.5,2);
            // Deck furniture shares its exact boxes with the renderer and collision solver.
            for(int side=-1;side<=1;side+=2)
            {
                yield return new(side*w*0.45,hull+0.3,-l*0.05,w*0.025,0.055,l*0.72,1);
                for(int i=0;i<5;i++)yield return new(side*w*0.45,hull+0.15,-l*0.32+i*l*0.16,w*0.025,0.3,w*0.025,1);
                yield return new(side*w*0.352,hull+cabin*0.62,-l*0.15,0.025,cabin*0.24,l*0.26,2);
            }
            for(int i=0;i<3;i++)
            {
                yield return new(0,hull+0.35,l*(0.12+i*0.1),w*0.55,0.7,l*0.085,3);
                yield return new(0,hull+0.72,l*(0.12+i*0.1),w*0.56,0.04,l*0.09,1);
            }
            yield return new(0,h-0.25,-l*0.3,0.52,0.12,0.52,1);
            foreach(var part in ShipProfileGeometry.Details(e))yield return part;
        }

        public static IEnumerable<ShipSolid> Solids(TrafficEvent e)
        {
            yield return ShipSolid.Hull(e);yield return ShipSolid.Hull(e,true);
            foreach(var solid in ShipProfileGeometry.ExtraSolids(e))yield return solid;
        }

        // Exact separating-axis test for an XY-oriented beam prism and each rendered ship box.
        public static bool Intersects(TrafficEvent e,double water,double shipZ,double ax,double ay,double bx,double by,double beamZ,double thickness,double depth)
        {
            if(Math.Abs(shipZ-beamZ)>(e.Length+depth)/2)return false;
            double dx=bx-ax,dy=by-ay,length=Math.Sqrt(dx*dx+dy*dy);
            if(length<1e-9)return false;
            double ux=dx/length,uy=dy/length;
            var offset=new Vec3d(e.LaneX,water,shipZ);var center=new Vec3d((ax+bx)/2,(ay+by)/2,beamZ);var along=new Vec3d(ux,uy,0);
            foreach(var solid in Solids(e))
                if(shipZ+solid.MinZ<=beamZ+depth/2 && shipZ+solid.MaxZ>=beamZ-depth/2 &&
                   solid.Intersects(offset,center,along,length/2,thickness/2,depth/2))return true;
            foreach(var p in Parts(e))
            {
                if(Math.Abs(shipZ+p.Z-beamZ)>(p.Length+depth)/2)continue;
                double cx=e.LaneX+p.X-(ax+bx)/2,cy=water+p.Y-(ay+by)/2;
                double hw=p.Width/2,hh=p.Height/2,hl=length/2,ht=thickness/2;
                if(Math.Abs(cx)>hw+Math.Abs(ux)*hl+Math.Abs(uy)*ht)continue;
                if(Math.Abs(cy)>hh+Math.Abs(uy)*hl+Math.Abs(ux)*ht)continue;
                if(Math.Abs(cx*ux+cy*uy)>hl+hw*Math.Abs(ux)+hh*Math.Abs(uy))continue;
                if(Math.Abs(-cx*uy+cy*ux)>ht+hw*Math.Abs(uy)+hh*Math.Abs(ux))continue;
                return true;
            }
            return false;
        }
    }
}
