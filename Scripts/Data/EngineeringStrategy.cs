using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;

namespace BridgeArchitect3D.Data
{
    public sealed class LiftStrategyCase
    {
        public string Name,Policy,Problem;
        public double TotalCost,EmptyUtil,LoadedUtil,PlatformUtil,RopeUtil,ShaftUtil,DriveUtil,MinRope=double.PositiveInfinity,MaxRope;
        public bool Fits=true;
        public double Util=>Math.Max(EmptyUtil,LoadedUtil);
        public bool Pass=>Fits && Problem==null && Util<=1;
    }
    public sealed class EngineeringStrategyReport
    {
        public double TotalCost,LiftMembers,LiftMachines,TubeAndEarth,Other;
        public List<LiftStrategyCase> Cases=new();
        public string Text;
    }
    /// <summary>Read-only comparisons of actual members, actual prices and actual structural checks.</summary>
    public static class EngineeringStrategy
    {
        public static LiftStrategyCase Check(DesignEditor editor,string name)
        {
            var result=new LiftStrategyCase{Name=name,TotalCost=editor.UsedCost,Policy=editor.Design.Tunnel?.Lift?.BoardingPolicy??LiftOperation.Shared};
            var cfg=editor.LiftCfgOf;var vehicles=editor.LevelRoadVehicles().ToArray();
            foreach(var layout in editor.Lifts())
            {
                if(layout.Problem!=null){result.Problem=layout.Problem;result.EmptyUtil=99;continue;}
                void Capture(LiftCheck c,bool empty)
                {
                    if(empty)result.EmptyUtil=Math.Max(result.EmptyUtil,c.Util);else result.LoadedUtil=Math.Max(result.LoadedUtil,c.Util);
                    result.PlatformUtil=Math.Max(result.PlatformUtil,c.PlatformUtil);result.RopeUtil=Math.Max(result.RopeUtil,c.RopeUtil);
                    result.ShaftUtil=Math.Max(result.ShaftUtil,c.ShaftUtil);result.DriveUtil=Math.Max(result.DriveUtil,c.DriveUtil);
                    if(c.Stable){result.MinRope=Math.Min(result.MinRope,c.RopePull);result.MaxRope=Math.Max(result.MaxRope,c.RopePull);}
                    else result.Problem=c.Why;
                }
                Capture(LiftStructure.Check(editor.Design,editor.Level,editor.Db,layout,Array.Empty<(double,double)>()),true);
                foreach(var vehicle in vehicles)
                {
                    if(!LiftOperation.Fits(vehicle,(layout.Shaft.X1-layout.Shaft.X0),cfg.Margin)){result.Fits=false;continue;}
                    Capture(LiftStructure.Check(editor.Design,editor.Level,editor.Db,layout,LiftStructure.VehicleAxles(vehicle,layout.Shaft,cfg.Margin,result.Policy)),false);
                }
            }
            if(!double.IsFinite(result.MinRope))result.MinRope=0;
            return result;
        }
        public static EngineeringStrategyReport Analyze(DesignEditor editor,int element=0)
        {
            var report=new EngineeringStrategyReport{TotalCost=editor.UsedCost};var g=editor.TunnelGeo;
            if(g==null){report.Text=Loc.T("请先建造隧道。");return report;}
            var config=editor.LiftCfgOf;var lift=editor.Design.Tunnel.Lift;var layouts=editor.Lifts();
            var ids=layouts.SelectMany(l=>l.Members).Select(m=>m.Id).ToHashSet();
            report.LiftMembers=editor.Design.Members.Where(m=>ids.Contains(m.Id)).Sum(m=>m.EffectiveCost());
            var drive=config.Drives.GetValueOrDefault(lift?.Drive??"")??config.Drives.Values.First();
            report.LiftMachines=layouts.Sum(l=>(editor.Level.ProvidedInfrastructure==null?Math.Max(0,l.Shaft.Height)*config.ShaftCostPerMeter:0)+drive.Cost+(lift?.Counterweight??0)*config.CounterweightCostPerTonne+l.Ties.Count*config.BraceCost);
            report.TubeAndEarth=editor.Design.Tunnel.Cost-report.LiftMachines;
            report.Other=report.TotalCost-report.LiftMembers-report.LiftMachines-report.TubeAndEarth;
            report.Cases.Add(Check(editor,Loc.T("当前自建结构")));
            foreach(var (id,template) in config.Templates)
            {
                var trial=new DesignEditor(editor.Design.Clone(),editor.Level,editor.Db,migrate:false);
                trial.RebuildLifts(id);report.Cases.Add(Check(trial,LiftConfig.TemplateName(id)));
            }
            var sb=new StringBuilder();
            sb.AppendLine(Loc.T("[b]结构与经济决策[/b]"));
            sb.AppendLine(Loc.F("当前总造价 ${0:N0} / 预算 ${1:N0}（余额 ${2:N0}）",report.TotalCost,editor.Budget,editor.Budget-report.TotalCost));
            sb.AppendLine(Loc.F("电梯构件 ${0:N0} · 电梯机构及约束 ${1:N0}\n管节、配重与地层施工 ${2:N0} · 桥梁、支撑、基础及其他 ${3:N0}",report.LiftMembers,report.LiftMachines,report.TubeAndEarth,report.Other));
            sb.AppendLine("\n"+Loc.T("[b]电梯方案比较[/b]"));
            sb.AppendLine(Loc.T("保持当前平台长度与运营方式，只在副本里比较模板；不会改动你的构件。"));
            foreach(var c in report.Cases)
            {
                sb.AppendLine(Loc.F("{0}：总价 ${1:N0}（较当前 {2:+$#,0;-$#,0;$0}）\n  空载 {3:0%} / 单车最不利 {4:0%} · {5}",c.Name,c.TotalCost,c.TotalCost-report.TotalCost,c.EmptyUtil,c.LoadedUtil,c.Pass?Loc.T("预检通过"):!c.Fits?Loc.T("部分车辆放不下"):c.Problem??Loc.T("结构或驱动超限")));
            }
            var best=report.Cases.Where(c=>c.Pass).OrderBy(c=>c.TotalCost).FirstOrDefault();
            sb.AppendLine(best==null?Loc.T("已比较的方案均未通过预检，需要调整长度、受力结构或驱动。"):Loc.F("已比较方案中，预检合格且造价最低的是：{0}。",best.Name));
            var current=report.Cases[0];
            sb.AppendLine(Loc.F("\n当前分项控制：平台 {0:0%} · 吊索 {1:0%} · 井架 {2:0%} · 驱动 {3:0%}",current.PlatformUtil,current.RopeUtil,current.ShaftUtil,current.DriveUtil));
            double balanced=Math.Clamp(Math.Round((current.MinRope+current.MaxRope)/2/9810/config.CounterweightStep)*config.CounterweightStep,0,config.MaxCounterweight);
            sb.AppendLine(Loc.F("空载到单车工况的吊索总拉力范围 {0:0.#}~{1:0.#} 吨；平衡驱动的配重试算约 {2:0} 吨。",current.MinRope/9810,current.MaxRope/9810,balanced));
            sb.AppendLine(Loc.T("配重只减小驱动的不平衡力，不减轻平台或吊索载荷，还会加重井架；增加后仍需重新预检。"));
            sb.AppendLine(Loc.T("单车居中：减轻偏载、便于使用较轻结构，但车辆不能合乘，通行时间可能增加。多车合乘：减少升降趟数，实际合乘载荷仍逐组检查。"));
            if(editor.Level.ProvidedInfrastructure!=null)
            {
                sb.AppendLine("\n[b]关卡提供的固定设施[/b]\n隧道、外部支撑和地面通道由关卡提供，不计入电梯设计预算，也不能移动、删除或升级。玩家负责平台、吊索、井架、驱动、配重与运营方式。");
                sb.AppendLine("\n[color=#e3c384]这里只预检空载和单车停靠，不覆盖车辆进出、合乘与整场交通；采用方案后仍需完整测试。[/color]");
                report.Text=sb.ToString();return report;
            }
            sb.AppendLine("\n"+Loc.T("[b]隧道局部升级与全线升级[/b]"));element=Math.Clamp(element,0,g.Count-1);
            foreach(var (id,grade) in g.Type.Grades)
            {
                double local=g.Len[element]*(grade.CostPerMeter-g.Grade[element].CostPerMeter);
                double all=Enumerable.Range(0,g.Count).Sum(i=>g.Len[i]*(grade.CostPerMeter-g.Grade[i].CostPerMeter));
                sb.AppendLine(Loc.F("{0}：本段改造 {1:+$#,0;-$#,0;$0} / 全线改造 {2:+$#,0;-$#,0;$0}；抗外压 {3:0} kPa",grade.Name,local,all,grade.PressureCapacity/1000));
            }
            sb.AppendLine(g.Type.IsFloating?Loc.T("悬浮：降低浮力需额外配重费用，但减少空载向上拉力；高浮力减少配重费用，但要用实际锚索及支撑承受上浮力。比较钢索跨度、方向与接缝弯矩后再选等级。"):g.Type.IsShield?Loc.T("盾构：更深的路线增加水土压力、井道高度及施工费用；管片等级和局部注浆应按沿线地层选择。"):Loc.T("沉管：增设支墩或缩短支撑跨度可降低接缝弯曲，但基础和支撑都要计价；升级管节同时增加自重，应和加支撑比较。"));
            sb.AppendLine("\n[color=#e3c384]"+Loc.T("这是空载及本关每种车辆单独停靠的静态预检，不覆盖车辆进入过程、合乘组合、天气和隧道整场结构响应。必须进行完整通车测试；各方案不自动应用。")+"[/color]");
            report.Text=sb.ToString();return report;
        }
    }
}
