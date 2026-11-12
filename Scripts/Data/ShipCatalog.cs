using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    public sealed record ShipType(string Id,string Name,double Width,double Height,double Length,double Speed);
    /// <summary>Small river craft dimensions in meters and cruise speed in meters per second.</summary>
    public static class ShipCatalog
    {
        public static IReadOnlyList<ShipType> Types { get; }=new[]
        {
            new ShipType("cargo","内河货轮",4.4,4.5,18,2.4),
            new ShipType("sailboat","帆船",2.8,8.5,8,2.8),
            new ShipType("speedboat","快艇",2.1,1.6,6,5.5),
            new ShipType("tugboat","拖轮",3.3,3.8,9,2.6)
        };
        public static string Kind(TrafficEvent e)=>Types.Any(t=>t.Id==e.ShipKind)?e.ShipKind:"cargo";
        public static TrafficEvent Create(string kind,double laneX,double time=0)
        {
            var type=Types.FirstOrDefault(t=>t.Id==kind)??Types[0];
            return new TrafficEvent {Type=TrafficEventType.Ship,ShipKind=type.Id,Name=type.Name,Width=type.Width,Height=type.Height,Length=type.Length,Speed=type.Speed,LaneX=laneX,Time=time};
        }
        public static double HullHeight(TrafficEvent e)=>e.Height*(Kind(e)=="cargo"?0.4:Kind(e)=="sailboat"?0.075:Kind(e)=="speedboat"?0.28:0.25);
        public static double Draft(TrafficEvent e)=>Kind(e)=="cargo"?0.6:Kind(e)=="speedboat"?0.22:0.38;
    }
}
