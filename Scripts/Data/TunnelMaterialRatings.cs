using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    public sealed record TunnelGradeRating(string Id,string Name,List<MaterialStat> Stats);

    /// <summary>Comparable five-cell ratings of tunnel grades using tunnel-specific physical quantities.</summary>
    public static class TunnelMaterialRatings
    {
        public static List<TunnelGradeRating> For(TunnelConfig config,MaterialDefinition material)
        {
            var result=new List<TunnelGradeRating>();var type=config?.Type(material);
            if(type==null)return result;
            var metrics=new List<(string label,string unit,Func<TunnelGradeConfig,double> get)>
            {
                (Loc.T("价格"),"$/m",g=>g.CostPerMeter),
                (Loc.T("自重"),Loc.T("吨/m"),g=>g.MassPerMeter/1000),
                (Loc.T("外压承载"),"kPa",g=>g.PressureCapacity/1000),
                (Loc.T("接缝容差"),"°",g=>g.JointRotation*180/Math.PI)
            };
            if(type.HasSupports)metrics.Add((Loc.T("支点承载"),Loc.T("吨"),g=>MaterialRatings.Tonnes(g.SupportLoad)));
            var all=config.Types.Values.SelectMany(t=>t.Grades.Values).ToList();
            foreach(var entry in type.Grades)
            {
                var stats=new List<MaterialStat>();
                foreach(var metric in metrics)
                {
                    double value=metric.get(entry.Value);var positives=all.Select(metric.get).Where(v=>v>0).ToArray();
                    stats.Add(new MaterialStat {Label=metric.label,Unit=metric.unit,Value=value,Cells=MaterialRatings.LogCells(value,positives.DefaultIfEmpty(0).Min(),positives.DefaultIfEmpty(0).Max())});
                }
                result.Add(new TunnelGradeRating(entry.Key,entry.Value.Name,stats));
            }
            return result;
        }
    }
}
