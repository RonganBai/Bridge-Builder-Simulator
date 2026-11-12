using System;

namespace BridgeArchitect3D.Data
{
    /// <summary>Shared scene dimensions and smooth interpolation; traffic timing is state-driven.</summary>
    public static class HarborTimeline
    {
        public const double SpawnX=-70,LeftPortal=-28,RightPortal=41,OpenAngle=68;
        public const double WaterLevel=-4.8,TrussDepth=1.25,TrussThickness=0.16;
        public const double ClosedNavigationBottom=-TrussDepth-TrussThickness/2;
        public static double Smooth(double t){t=Math.Clamp(t,0,1);return t*t*(3-2*t);}
        public static double VehicleOpacity(double front,double length)=>
            Smooth((front-(SpawnX+10))/25)*(1-Smooth((front-length-(RightPortal+15))/20));
    }
}
