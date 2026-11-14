using System;

namespace BridgeArchitect3D.Data
{
    /// <summary>Absolute-time cyclic patterns shared by lens materials and lights.</summary>
    public static class EmergencyBeaconPattern
    {
        public static string ColorFor(string name)=>name.Contains("Amber")?"ffb52e":name.Contains("White")?"f4f5ff":name.Contains("Red")?"ef5147":"418ff0";
        public static bool IsLit(string name,double time)
        {
            if(!double.IsFinite(time))return false;
            bool amber=name.Contains("Amber");
            double period=amber?2.0:1.2;
            double phase=((time%period)+period)%period;
            if(amber)return phase<0.8;
            double offset=name.Contains("Red")?0:name.Contains("White")?0.4:0.8;
            phase=(phase-offset+period)%period;
            return phase<0.09 || phase>=0.18 && phase<0.27;
        }
    }
}
