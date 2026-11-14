using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    // Rendering and collision consume the same solids and fittings.
    public static class ExtendedShipGeometry
    {
        public static bool Supports(string kind) => kind is "tourboat" or "catamaran" or "car_ferry" or "container" or "sand_barge" or "tow_barge" or "fishing" or "yacht" or "fireboat" or "folding_sail";
        private static ShipSolid Transform(ShipSolid solid,double sx,double sz,double x,double z)
            => new(solid.Vertices.Select(v=>new Vec3d(v.X*sx+x,v.Y,v.Z*sz+z)).ToArray(),solid.Faces,solid.Material);
        public static IEnumerable<ShipSolid> Solids(TrafficEvent e)
        {
            foreach(bool deck in new[]{false,true})
            {
                var hull=ShipSolid.Hull(e,deck);
                if(e.ShipKind=="catamaran")
                    foreach(int side in new[]{-1,1})yield return Transform(hull,.25,1,side*e.Width*.36,0);
                else if(e.ShipKind=="tow_barge")
                {
                    yield return Transform(hull,.65,.25,0,-e.Length*.375);
                    yield return Transform(hull,1,.65,0,e.Length*.175);
                }
                else yield return hull;
            }
        }
        public static IEnumerable<ShipBox> Parts(TrafficEvent e)
        {
            double w=e.Width,l=e.Length,h=e.Height,d=ShipCatalog.HullHeight(e);
            string kind=e.ShipKind;
            var parts=new List<ShipBox>();
            void B(double x,double y,double z,double width,double height,double length,int mat)
                =>parts.Add(new(x,y,z,width,height,length,mat));
            void Cabin(double z,double length,double width,double top)
            {
                double bottom=d+.08,wall=top-bottom;
                B(0,bottom+wall/2,z,width,wall,length,1);
                foreach(int side in new[]{-1,1})
                {
                    for(int i=0;i<4;i++)B(side*(width/2+.012),top-wall*.35,z-length*.36+i*length*.24,.018,wall*.40,length*.17,2);
                }
                B(0,top-wall*.35,z+length/2+.014,width*.85,wall*.40,.018,2);
                B(0,top+.045,z,width+.12,.09,length+.16,1);
            }
            if(kind is "tourboat" or "catamaran" or "yacht")
            {
                if(kind=="catamaran")B(0,d-.02,0,w*.88,.16,l*.82,1);
                Cabin(-l*.10,l*.59,w*.77,h*.70);
                if(kind is "catamaran" or "yacht")Cabin(-l*.17,l*.27,w*.53,h-.12);
                else
                {
                    B(0,h-.08,-l*.1,w*.84,.12,l*.65,1);
                    for(int i=0;i<5;i++)foreach(int side in new[]{-1,1})B(side*w*.22,d+.28,-l*.28+i*l*.11,w*.18,.34,l*.055,4);
                }
            }
            else if(kind=="car_ferry")
            {
                B(w*.34,d+.18,-l*.03,w*.22,.36,l*.76,1);
                Cabin(-l*.28,l*.18,w*.7,h-.15);
                foreach(int side in new[]{-1,1})B(0,d+.08,side*l*.44,w*.73,.16,l*.10,5);
                for(int i=0;i<3;i++)
                {
                    double z=-l*.12+i*l*.16;
                    B(-w*.16,d+.39,z,w*.24,.60,l*.13,i%2==0?3:7);
                    B(-w*.16,d+.82,z,w*.20,.30,l*.065,2);
                }
            }
            else if(kind=="container")
            {
                Cabin(-l*.35,l*.19,w*.83,h-.13);
                for(int row=0;row<3;row++)for(int column=0;column<2;column++)for(int tier=0;tier<2;tier++)
                {
                    double x=(column-.5)*w*.43,z=-l*.13+row*l*.19,y=d+.66+tier*1.32;
                    B(x,y,z,w*.40,1.26,l*.175,(row+column+tier)%2==0?3:7);
                    foreach(int side in new[]{-1,1})for(int rib=0;rib<4;rib++)B(x+side*w*.203,y,z-l*.066+rib*l*.044,.018,1.17,.035,5);
                }
            }
            else if(kind is "sand_barge" or "tow_barge")
            {
                double z=kind=="tow_barge"?l*.175:0,span=kind=="tow_barge"?l*.58:l*.78;
                foreach(int side in new[]{-1,1})B(side*w*.41,d+.30,z,w*.08,.60,span,0);
                foreach(int side in new[]{-1,1})B(0,d+.30,z+side*span/2,w*.86,.60,.12,0);
                for(int i=0;i<5;i++)B(0,d+.17+i%2*.07,z-span*.36+i*span*.18,w*.70,.32+i%2*.14,span*.20,4);
                if(kind=="tow_barge")
                {
                    Cabin(-l*.37,l*.16,w*.45,h-.3);
                    B(0,d+.12,-l*.19,.08,.08,l*.07,5);
                }
                else Cabin(-l*.36,l*.11,w*.35,h-.12);
            }
            else if(kind is "fishing" or "fireboat")
            {
                Cabin(-l*.16,l*.32,w*.64,h*.68);
                B(0,(d+h)/2,-l*.12,.075,h-d,.075,5);
                B(0,h-.07,-l*.12,w*.65,.07,.07,5);
                if(kind=="fishing")
                {
                    foreach(int side in new[]{-1,1})B(side*w*.32,d+.7,l*.20,.07,1.4,.07,5);
                    B(0,d+1.38,l*.20,w*.66,.08,.08,5);
                    for(int i=0;i<4;i++)B((i%2-.5)*w*.3,d+.18,l*(.08+i/2*.14),w*.24,.36,l*.10,3);
                }
                else foreach(int side in new[]{-1,1})
                {
                    B(side*w*.25,d+.45,l*.22,.20,.9,.20,7);
                    B(side*w*.25,d+.88,l*.27,.16,.16,l*.16,5);
                    B(side*w*.2,h*.70,-l*.16,.20,.16,.25,side<0?7:2);
                }
            }
            else if(kind=="folding_sail")
            {
                Cabin(-l*.12,l*.38,w*.60,d+.65);
                B(0,d+1.05,0,.12,.12,l*.78,5);
                B(0,d+1.19,-l*.04,w*.28,.18,l*.60,6);
                B(0,d+1.37,-l*.10,.09,.50,.09,5);
                B(0,h-.04,-l*.1,.12,.08,.12,8);
            }
            foreach(var detail in ShipProfileGeometry.Details(e))parts.Add(detail);
            return parts;
        }
    }
}
