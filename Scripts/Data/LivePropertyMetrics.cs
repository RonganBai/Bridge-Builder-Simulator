using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Physics;

namespace BridgeArchitect3D.Data
{
    public sealed class LivePropertyMetric
    {
        public string Label { get; init; }
        public string Readout { get; init; }
        public double Ratio { get; init; }
        public bool Risk { get; init; }
        /// <summary>Section title row (no bar).</summary>
        public bool Header { get; init; }
        public static LivePropertyMetric Section(string label)=>new(){Label=Loc.T(label),Readout="",Header=true};
        public double Fill=>double.IsFinite(Ratio)?Math.Clamp(Ratio,0,1):Ratio>0?1:0;
        public int Band=>!Risk?0:Ratio>=1?3:Ratio>=.9?2:Ratio>=.7?1:0;
        public static LivePropertyMetric Make(string label,double value,double maximum,string readout,bool risk=false)=>new(){Label=Loc.T(label),Ratio=maximum>0?value/maximum:value>0?9.99:0,Readout=readout,Risk=risk};
    }
    public static class LivePropertyMetrics
    {
        /// <summary>
        /// Bank lifts built from ordinary members: machine (drive, counterweight), member count, then the worst case
        /// "a road vehicle of the level alone at its stop" over both lifts, with each part's utilization.
        /// </summary>
        public static List<LivePropertyMetric> Lift(LiftDesign lift,LiftLayout[] layouts,LiftCheck worst,double vehicle,GameDatabase db)
        {
            var rows=new List<LivePropertyMetric>();if(lift==null||layouts==null||layouts.Length==0)return rows;
            var c=LiftStructure.Cfg(db);var drive=c.Drives.GetValueOrDefault(lift.Drive??"")??c.Drives.Values.First();
            rows.Add(LivePropertyMetric.Section(Loc.T("电梯结构属性")));
            foreach(var L in layouts)
                rows.Add(LivePropertyMetric.Make(LiftStructure.Name(L),L.Problem==null?0:1,1,L.Problem??Loc.F("{0} 根构件（平台 {1} 根，吊索 {2} 根），井壁拉结 {3} 处",(L.Members.Count),(L.Members.Count(L.IsPlatformMember)),(L.Hoists.Count),(L.Ties.Count)),L.Problem!=null));
            bool mechanical=layouts.Any(l=>l.Mechanical);
            if(mechanical)
            {
                var rods=layouts.SelectMany(l=>l.Members).Where(m=>db.GetMaterial(m.MaterialId)?.Extendable==true).ToArray();
                double force=rods.Sum(m=>db.GetMaterial(m.MaterialId).ActuatorForce);
                rows.Add(LivePropertyMetric.Make(Loc.T("已安装液压缸额定力"),force,Math.Max(1,force),Loc.F("{0} 根缸 · 合计 {1:0} kN；受实际行程与单缸拉压力限制", (rods.Length), (force/1000))));
                if(worst!=null)rows.Add(LivePropertyMetric.Make(Loc.T("实际悬挂配重"),worst.CounterweightForce/9810,100,Loc.F("{0:0} 吨；配重块通过钢索施加载荷", (worst.CounterweightForce/9810))));
            }
            else
            {
            rows.Add(LivePropertyMetric.Make(Loc.T("驱动能力"),drive.Force,c.Drives.Values.Max(v=>v.Force),Loc.F("{0}：{1:0} kN（点吊索顶端的滑轮节点更换）",(drive.Name),(drive.Force/1000))));
            rows.Add(LivePropertyMetric.Make(Loc.T("配重"),lift.Counterweight,c.MaxCounterweight,Loc.F("{0:0} 吨，挂在滑轮上，经柱子传到井底",(lift.Counterweight))));
            }
            if(worst==null)return rows;
            rows.Add(LivePropertyMetric.Section(Loc.T("受力与利用率")));
            rows.Add(LivePropertyMetric.Make(Loc.T("工况：本关车辆单独停在平台上"),vehicle,vehicle,Loc.F("最重 {0:0.0} 吨 + 平台自重 {1:0.0} 吨",(vehicle/9810),(worst.PlatformWeight/9810))));
            rows.Add(LivePropertyMetric.Make(Loc.T("平台构件"),worst.PlatformUtil,1,$"{worst.PlatformUtil:0%}",true));
            rows.Add(LivePropertyMetric.Make(Loc.T("吊索"),worst.RopeUtil,1,Loc.F("总拉力 {0:0} kN",(worst.RopePull/1000)),true));
            rows.Add(LivePropertyMetric.Make(Loc.T("井道柱子与拉结"),worst.ShaftUtil,1,$"{worst.ShaftUtil:0%}",true));
            rows.Add(LivePropertyMetric.Make(mechanical?Loc.T("液压缸利用率"):Loc.T("驱动不平衡力"),worst.DriveUtil,1,Loc.F("{0:0} kN",(worst.DriveForce/1000)),true));
            rows.Add(LivePropertyMetric.Make(mechanical?Loc.T("固定支点总竖向反力"):Loc.T("井底基础反力"),worst.BaseReaction,worst.BaseReaction,Loc.F("{0:0} kN",(worst.BaseReaction/1000))));
            rows.Add(new LivePropertyMetric{Label=Loc.F("控制项：{0}",(worst.Governing)),Ratio=worst.Util,Readout=worst.Where,Risk=true});
            return rows;
        }

        public static List<LivePropertyMetric> Member(BridgeMember member,GameDatabase db,StructuralModel model,MemberStatus[] statuses,bool stable)
        {
            var rows=new List<LivePropertyMetric>();var mat=db.GetMaterial(member.MaterialId);if(mat==null)return rows;
            var catalog=db.Materials.Values.Where(m=>!m.IsTunnel && !m.IsLift).ToArray();
            double Max(Func<MaterialDefinition,double> get)=>catalog.Select(get).DefaultIfEmpty(1).Max();
            void Add(string label,double value,double max,string text,bool risk=false)=>rows.Add(LivePropertyMetric.Make(label,value,max,text,risk));
            double condition=member.Existing?Math.Clamp(member.Condition,0,1):1;
            double tension=mat.TensionCapacity*CrossBeams.TensionFactor(mat,member.Length)*condition;
            double compression=mat.IsCable?0:mat.CompressionCapacity(member.Length)*condition,bending=mat.IsCable || mat.PinnedEnds && !mat.IsDeck?0:mat.BendingCapacity*condition;
            Add(Loc.T("长度 / 长度上限"),member.Length,mat.MaximumLength,$"{member.Length:0.00} / {mat.MaximumLength:0.##} m",true);
            Add(Loc.T("构件质量"),mat.MassPerMeter*member.Length,Max(m=>m.MassPerMeter)*member.Length,Loc.F("{0:0.##} 吨", (mat.MassPerMeter*member.Length/1000)));
            Add(Loc.T("构件费用"),member.EffectiveCost(),Max(m=>m.CostPerMeter)*member.Length,$"${member.EffectiveCost():0}");
            Add(Loc.T("抗拉承载力"),tension,Max(m=>m.TensionCapacity*CrossBeams.TensionFactor(m,member.Length)), Loc.F("{0:0.##} 吨", (MaterialRatings.Tonnes(tension))));
            Add(Loc.T("抗压承载力"),compression,Max(m=>m.CompressionCapacity(member.Length)),mat.IsCable?Loc.T("不承受压力"): Loc.F("{0:0.##} 吨", (MaterialRatings.Tonnes(compression))));
            Add(Loc.T("抗弯承载力"),bending,Max(m=>m.BendingCapacity),mat.IsCable || mat.PinnedEnds?Loc.T("铰接，仅承受轴力"): Loc.F("{0:0.##} 吨·m", (MaterialRatings.Tonnes(bending))));
            if(mat.Extendable)
            {
                Add(Loc.T("最大收缩行程"),mat.RetractRatio,1,$"{mat.RetractRatio:0%}");
                Add(Loc.T("最大伸长行程"),mat.ExtendRatio,1,$"{mat.ExtendRatio:0%}");
                Add(Loc.T("液压驱动力"),mat.ActuatorForce,Max(m=>m.ActuatorForce),Loc.F("{0:0.##} 吨", (MaterialRatings.Tonnes(mat.ActuatorForce))));
            }
            if(mat.IsColumn)Add(Loc.T("允许倾角"),mat.MaxTiltDegrees,90,$"{mat.MaxTiltDegrees:0.#}°");
            if(mat.IsDeck)Add(Loc.T("桥面宽度"),mat.DeckWidth,Max(m=>m.DeckWidth),$"{mat.DeckWidth:0.##} m");
            if(member.Existing)Add(Loc.T("旧构件完好度"),condition,1,$"{condition:0%}");
            if(!stable || model==null || statuses==null || !model.MemberIndex.TryGetValue(member.Id,out int k) || k>=statuses.Length)return rows;
            if(model.Broken[k]){Add(Loc.T("构件已断裂"),1,1,Loc.T("已失去承载能力"),true);return rows;}
            var s=statuses[k];double cap=s.Mode==MemberMode.Compression?compression:tension;
            Add(s.Mode==MemberMode.Compression?Loc.T("受压 / 抗压上限"):Loc.T("受拉 / 抗拉上限"),Math.Abs(s.Force),cap,Loc.F("{0:0.##} / {1:0.##} 吨", (MaterialRatings.Tonnes(Math.Abs(s.Force))), (MaterialRatings.Tonnes(cap))),true);
            if(!mat.IsCable && !mat.PinnedEnds && bending>0 || mat.IsDeck && bending>0)
                Add(Loc.T("弯矩 / 抗弯上限"),Math.Abs(s.Moment),bending,Loc.F("{0:0.##} / {1:0.##} 吨·m", (MaterialRatings.Tonnes(Math.Abs(s.Moment))), (MaterialRatings.Tonnes(bending))),true);
            Add(Loc.T("综合承载利用率"),s.Utilization,1,$"{s.Utilization*100:0.#}%"+(s.Mode==MemberMode.Slack?" · "+Loc.T("松弛"):""),true);
            return rows;
        }
        public static List<LivePropertyMetric> Tunnel(TunnelGeometry g,int i,TunnelState state,TunnelConfig cfg,LevelDefinition level=null)
        {
            var rows=new List<LivePropertyMetric>();if(g==null || i<0 || i>=g.Count)return rows;
            var grade=g.Grade[i];var type=g.Type;bool structural=TunnelStructure.IsStructural(type);
            void Add(string label,double value,double max,string text,bool risk=false)=>rows.Add(LivePropertyMetric.Make(label,value,max,text,risk));
            // Material / section properties (blue bars: relative to the other grades)
            rows.Add(LivePropertyMetric.Section(Loc.T("材料与截面属性")));
            Add(Loc.T("管节长度"),g.Len[i],Math.Max(cfg.ElementLength,g.Len[i]),$"{g.Len[i]:0.##} m");
            Add(Loc.T("管节价格"),grade.CostPerMeter,type.Grades.Values.Max(v=>v.CostPerMeter),$"${grade.CostPerMeter:0}/m");
            Add(Loc.T("抗水压强度"),grade.PressureCapacity,type.Grades.Values.Max(v=>v.PressureCapacity),$"{grade.PressureCapacity/1000:0.#} kPa");
            if(structural)
            {
                double mc=TunnelStructure.JointMomentCapacity(type,grade);
                Add(Loc.T("接缝抗弯能力"),mc,type.Grades.Values.Max(v=>TunnelStructure.JointMomentCapacity(type,v)),$"{mc/1e6:0.#} MN·m");
            }
            Add(Loc.T("管节质量"),grade.MassPerMeter,type.Grades.Values.Max(v=>v.MassPerMeter),Loc.F("{0:0.##} 吨/m", (grade.MassPerMeter/1000)));
            if(structural && level!=null)
            {
                double disp=TunnelStructure.DisplacedMass(g,level,cfg,i),net=TunnelStructure.NetMass(g,level,cfg,i);
                if(type.IsFloating)
                {
                    Add(Loc.T("浮力 / 自重比"),g.Buoy[i].Ratio,type.Buoyancy.Values.Max(v=>v.Ratio),$"{g.Buoy[i].Name} ×{g.Buoy[i].Ratio:0.00}");
                    double ballast=TunnelStructure.Ballast(g,level,cfg,i);
                    Add(Loc.T("配重"),ballast,Math.Max(1,disp),Loc.F("{0:0.##} 吨/m", (ballast/1000)));
                    Add(Loc.T("配重附加费用"),g.Buoy[i].CostPerMeter,type.Buoyancy.Values.Max(v=>v.CostPerMeter),$"${g.Buoy[i].CostPerMeter:0}/m");
                }
                Add(Loc.T("排开水（浮力）"),disp,Math.Max(1,disp),Loc.F("{0:0.##} 吨/m", (disp/1000)));
                Add(net>=0?Loc.T("净重（向下）"):Loc.T("净上浮力（向上）"),Math.Abs(net),Math.Max(1,disp*0.4),Loc.F("{0:0.##} 吨/m · {1:0.#} 吨/节", (Math.Abs(net)/1000), (Math.Abs(net)*g.Len[i]/1000)));
            }
            // Load utilization (coloured bars: share of the limit)
            if(state!=null && i<state.Count)
            {
                rows.Add(LivePropertyMetric.Section(Loc.T("受力与利用率")));
                double pressure=state.Pressure[i]+(type.IsShield?state.EarthPressure[i]:0);
                Add(type.IsShield?Loc.T("外压 / 管片强度"):Loc.T("水压 / 管节强度"),pressure,grade.PressureCapacity,$"{pressure/1000:0.#} / {grade.PressureCapacity/1000:0.#} kPa",true);
                if(type.IsShield)
                {
                    Add(Loc.T("地下水压"),state.Pressure[i],grade.PressureCapacity,$"{state.Pressure[i]/1000:0.#} kPa");
                    Add(Loc.T("土压力"),state.EarthPressure[i],grade.PressureCapacity,$"{state.EarthPressure[i]/1000:0.#} kPa");
                }
                if(structural)
                {
                    double mc=TunnelStructure.JointMomentCapacity(type,grade);
                    Add(Loc.T("接缝弯矩 / 抗弯能力"),Math.Abs(state.Moment[i]),mc,$"{Math.Abs(state.Moment[i])/1e6:0.##} / {mc/1e6:0.#} MN·m",true);
                    Add(Loc.T("接缝转角"),Math.Abs(state.JointRotationNow[i]),grade.JointRotation,$"{Math.Abs(state.JointRotationNow[i])*180/Math.PI:0.###}° / {grade.JointRotation*180/Math.PI:0.##}°",true);
                    Add(state.Settle[i]>=0?Loc.T("管身下沉"):Loc.T("管身上浮"),Math.Abs(state.Settle[i]),Math.Max(0.01,type.MaxDisplacement),$"{Math.Abs(state.Settle[i])*100:0.#} cm");
                    if(type.IsFloating)Add(Loc.T("横向摆动（简化估算，仅供参考）"),state.Sway[i],Math.Max(1e-6,type.MaxDisplacement),$"{state.Sway[i]*100:0.#} cm");
                }
                Add(Loc.T("综合强度利用率"),state.Util[i],1,$"{state.Util[i]*100:0.#}%"+(structural?" · "+(state.BendUtil[i]>state.PressureUtil[i]?Loc.T("控制：接缝弯矩"):Loc.T("控制：水压")):""),true);
            }
            var lift=TunnelRules.LiftCfg;double capacity=TunnelRules.LiftCapacity(g.LiftLength);
            Add(Loc.T("电梯平台长度"),g.LiftLength,lift.MaxLength,$"{g.LiftLength:0.#} / {lift.MaxLength:0.#} m");
            Add(Loc.T("电梯额定载重"),capacity,TunnelRules.LiftCapacity(lift.MaxLength),Loc.F("{0:0.#} 吨", (capacity/9810)));
            Add(Loc.T("电梯升降速度"),lift.Speed,5,$"{lift.Speed:0.##} m/s");
            return rows;
        }
    }
    public static class TunnelBallastLayout
    {
        public static int Layers(double ratio)=>ratio<1.1?3:ratio<1.28?2:1;
        public static int Columns(double length)=>Math.Clamp((int)Math.Floor(length/2.2),1,16);
    }
}
