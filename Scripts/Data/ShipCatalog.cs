using System;
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
            new ShipType("cargo",Loc.T("内河货轮"),4.4,4.5,18,2.4),
            new ShipType("sailboat",Loc.T("帆船"),2.8,8.5,8,2.8),
            new ShipType("speedboat",Loc.T("快艇"),2.1,1.6,6,5.5),
            new ShipType("tugboat",Loc.T("拖轮"),3.3,3.8,9,2.6),
            new ShipType("tourboat",Loc.T("观光船"),3.4,2.6,12,3.2),
            new ShipType("catamaran",Loc.T("双体渡轮"),6,4.8,16,3.8),
            new ShipType("car_ferry",Loc.T("汽车渡船"),6.4,3.6,20,2.2),
            new ShipType("container",Loc.T("集装箱货轮"),5.8,7.2,28,1.8),
            new ShipType("sand_barge",Loc.T("运砂驳船"),5.2,2.2,22,1.5),
            new ShipType("tow_barge",Loc.T("拖轮船队"),4.8,4.2,32,1.6),
            new ShipType("fishing",Loc.T("渔船"),3.6,5.2,11,2.7),
            new ShipType("yacht",Loc.T("游艇"),3.8,3.8,12,4.2),
            new ShipType("fireboat",Loc.T("消防船"),3.8,4.2,12,4.6),
            new ShipType("folding_sail",Loc.T("降桅帆船"),2.8,2.4,8,2.5)
        };
        public static string Kind(TrafficEvent e)=>Types.Any(t=>t.Id==e.ShipKind)?e.ShipKind:"cargo";
        public static TrafficEvent Create(string kind,double laneX,double time=0)
        {
            var type=Types.FirstOrDefault(t=>t.Id==kind)??Types[0];
            return new TrafficEvent {Type=TrafficEventType.Ship,ShipKind=type.Id,Name=type.Name,Width=type.Width,Height=type.Height,Length=type.Length,Speed=type.Speed,LaneX=laneX,Time=time};
        }
        public static double HullHeight(TrafficEvent e)=>ExtendedShipGeometry.Supports(Kind(e)) ? Math.Min(0.85,e.Height*.28) : e.Height*(Kind(e)=="cargo"?0.4:Kind(e)=="sailboat"?0.075:Kind(e)=="speedboat"?0.28:0.25);
        public static double Draft(TrafficEvent e)=>e.Draft??DefaultDraft(e);
        public static double DefaultDraft(TrafficEvent e)=>Kind(e)=="cargo"?0.6:Kind(e)=="speedboat"?0.22:0.38;
    }
}
