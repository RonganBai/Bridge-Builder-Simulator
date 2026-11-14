using System;
using System.Collections.Generic;

namespace BridgeArchitect3D.Data
{
    public sealed record SceneryPlot(string Id,string Kind,double X,double Z,double HalfX,double HalfZ,int Bank);

    /// <summary>Shared footprints keep independently generated buildings, trees, hills and roads apart.</summary>
    public sealed class SceneryLayout
    {
        public const double BankLength=100,DecorationLength=45,HalfDepth=56,RoadClearance=5.5;
        public List<SceneryPlot> Reservations { get; }=new();
        public List<SceneryPlot> Buildings { get; }=new();
        public List<SceneryPlot> Hills { get; }=new();
        private readonly double _river;
        public SceneryLayout(LevelDefinition level)
        {
            _river=level.Terrain.RiverWidth;
            TryReserve(new("park","park",-11,-11.5,6,5.5,0));
            for(int bank=0;bank<2;bank++)
            {
                double X(double distance)=>bank==0?-distance:_river+distance;
                var main=new SceneryPlot($"landmark-{bank}","landmark",X(29),-12,8,6.3,bank);
                if(TryReserve(main))Buildings.Add(main);
                for(int i=0;i<3;i++)
                {
                    var plot=new SceneryPlot($"building-{bank}-{i}",i==1?"shop":i==2?"workshop":"house",X(40-i*13),-25,4.2,4,bank);
                    if(TryReserve(plot))Buildings.Add(plot);
                }
                foreach(double distance in new[]{35d,15d})
                {
                    var hill=new SceneryPlot($"hill-{bank}-{distance}","hill",X(distance),-42,8,8,bank);
                    if(TryReserve(hill))Hills.Add(hill);
                }
            }
        }
        public bool TryReserve(SceneryPlot plot,double margin=0.7)
        {
            if(plot.HalfX<=0 || plot.HalfZ<=0 || Math.Abs(plot.Z)+plot.HalfZ>HalfDepth || Math.Abs(plot.Z)-plot.HalfZ<RoadClearance)return false;
            double lo=plot.Bank==0?-BankLength:_river,hi=plot.Bank==0?0:_river+BankLength;
            if(plot.X-plot.HalfX<lo || plot.X+plot.HalfX>hi)return false;
            foreach(var other in Reservations)
                if(Overlaps(plot,other,margin))return false;
            Reservations.Add(plot);return true;
        }
        public static bool Overlaps(SceneryPlot a,SceneryPlot b,double margin=0)=>
            Math.Abs(a.X-b.X)<a.HalfX+b.HalfX+margin && Math.Abs(a.Z-b.Z)<a.HalfZ+b.HalfZ+margin;
    }
}
