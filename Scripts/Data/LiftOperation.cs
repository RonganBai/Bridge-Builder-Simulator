using System;

namespace BridgeArchitect3D.Data
{
    /// <summary>The same parking rule is used by structural planning and live dispatch.</summary>
    public static class LiftOperation
    {
        public const string Shared="shared",SingleCentered="single_centered";
        public static bool Valid(string policy)=>policy is Shared or SingleCentered;
        public static bool Centered(string policy)=>policy==SingleCentered;
        public static string Name(string policy)=>Centered(policy)?Loc.T("单车居中"):Loc.T("多车合乘");
        public static double Stop(VehicleDefinition vehicle,double length,double margin,string policy)
        {
            double front=VehicleBody.Front(vehicle),rear=VehicleBody.Rear(vehicle);
            return Centered(policy)?(length-front-rear)/2:length-margin-front;
        }
        public static bool Fits(VehicleDefinition vehicle,double length,double margin)=>VehicleBody.Front(vehicle)-VehicleBody.Rear(vehicle)<=length-2*margin+1e-6;
    }
}
