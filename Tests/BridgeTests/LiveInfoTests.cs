using System;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;

internal static class LiveInfoTests
{
    public static void Run(GameDatabase db,Action<string,bool,string> check)
    {
        check("Ballast: low, mid and high buoyancy have distinct decreasing block stacks",TunnelBallastLayout.Layers(1.04)==3 && TunnelBallastLayout.Layers(1.2)==2 && TunnelBallastLayout.Layers(1.35)==1,"");
        check("Ballast: short and long elements keep bounded positive block counts",TunnelBallastLayout.Columns(.2)==1 && TunnelBallastLayout.Columns(1000)==16,"");
        foreach(var material in db.Materials.Values.Where(m=>!m.IsTunnel && !m.IsLift))
        {
            var member=new BridgeMember{Id=1,MaterialId=material.MaterialId,Length=material.MaximumLength*.75,Cost=material.CostPerMeter*material.MaximumLength*.75};
            var rows=LivePropertyMetrics.Member(member,db,null,null,false);
            check($"Live bars: {material.MaterialId} has finite readings and bounded fills",rows.Count>=6 && rows.All(r=>double.IsFinite(r.Ratio) && r.Fill>=0 && r.Fill<=1 && !string.IsNullOrWhiteSpace(r.Readout)),"");
            if(material.IsCable)check($"Live bars: {material.MaterialId} has no compression capacity",rows.First(r=>r.Label==Loc.T("抗压承载力")).Fill==0,"");
        }
        var over=LivePropertyMetric.Make("test",1.7,1,"170%",true);
        check("Live bars: overlimit readings retain 170% while the bar saturates red",over.Ratio==1.7 && over.Fill==1 && over.Band==3 && over.Readout=="170%","");
        foreach(var (id,level) in new[]{("tunnel_immersed","level13"),("tunnel_floating","level14"),("tunnel_shield","level15")})
        {
            var lv=db.GetLevel(level);var editor=new DesignEditor(null,lv,db);var added=editor.AddTunnel(0,lv.Terrain.RiverWidth,id);
            check($"Live bars: {id} fixture can be built",added.Ok,added.Message);if(!added.Ok)continue;
            var g=editor.TunnelGeo;var state=TunnelAnalysis.Evaluate(g,lv,db.TunnelConfig,TunnelAnalysis.StaticEnvironment(lv,db.TunnelConfig));
            var rows=LivePropertyMetrics.Tunnel(g,0,state,db.TunnelConfig);
            check($"Live bars: {id} includes actual strength and lift metrics",rows.Any(r=>r.Label==Loc.T("综合强度利用率") && r.Risk) && rows.Any(r=>r.Label==Loc.T("电梯额定载重")) && rows.All(r=>double.IsFinite(r.Ratio)),"");
        }
    }
}
