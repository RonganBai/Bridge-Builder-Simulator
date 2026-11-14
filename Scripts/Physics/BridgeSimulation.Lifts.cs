using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    public partial class BridgeSimulation
    {
        public enum LiftPhase { Boarding, Closing, Travelling, OpeningDestination, Unloading, ClosingForReturn, Returning, OpeningHome }

        public class LiftState
        {
            public bool Down;
            public double A, Jump, Shift, Length, Height, Speed;
            /// <summary>Members of this lift (found in the design) and its last check (load of the current riders).</summary>
            public LiftLayout Layout;
            public bool LeftBank;
            public LiftCheck Check;
            internal readonly Dictionary<string,LiftCheck> Cache=new();
            public double Pos, DoorOpen=1, Gap, BoardingSeconds, DoorSeconds;
            public LiftPhase Phase=LiftPhase.Boarding;
            public readonly List<VehicleState> Riders=new();
            internal readonly Dictionary<VehicleState,double> Stops=new();
            internal double BoardWait;
            public VehicleState Rider => Riders.FirstOrDefault();
            public bool Riding => Phase==LiftPhase.Travelling;
            public bool Delivered => Phase==LiftPhase.OpeningDestination||Phase==LiftPhase.Unloading;
            public double EntryS => A-Length;
            public double ExitEndS => A+Jump+Length;
            public double Dy => (Down?-1:1)*Pos*Height;
            public double OccupiedLength => Riders.Sum(BodyLength)+Math.Max(0,Riders.Count-1)*Gap;
            public double LoadForce => Riders.Sum(Weight);
        }
        public readonly List<LiftState> Lifts=new();

        private static double BodyLength(VehicleState v)=>v.FrontOverhang-v.RearOffset+v.RearOverhang;
        private static double Weight(VehicleState v)=>v.Def.Mass*Math.Max(1,v.Def.Units)*StructuralModel.Gravity;

        private void BuildLifts()
        {
            Lifts.Clear();if(TunnelPath==null||TunnelGeo==null)return;
            var config=TunnelRules.LiftCfg;
            var layouts=LiftStructure.Analyze(Design,Level,Db);
            foreach(int k in TunnelPath.LiftSegments.OrderBy(k=>k))
            {
                double dx=TunnelPath.PointX[k+1]-TunnelPath.PointX[k],dy=TunnelPath.PointY[k+1]-TunnelPath.PointY[k];
                double jump=Math.Sqrt(dx*dx+dy*dy),length=TunnelGeo.LiftLength;
                Lifts.Add(new LiftState {Down=dy<0,A=TunnelPath.SegmentStartS[k],Jump=jump,Shift=jump+length,Length=length,Height=Math.Abs(dy),
                    Speed=Math.Max(0.1,config.Speed),Layout=layouts.ElementAtOrDefault(Lifts.Count),LeftBank=Lifts.Count==0,Gap=Math.Max(0.2,config.VehicleGap),
                    BoardingSeconds=Math.Max(0,config.BoardingSeconds),DoorSeconds=Math.Max(0.1,config.DoorSeconds)});
            }
            if(Level.MechanicalLift)foreach(var lift in Lifts)if(lift.Layout!=null)lift.Speed=LiftMechanics.Plan(Design,Level,Db,lift.Layout).Speed;
            EnsureLiftIntegrity();
        }

        // Routing is geometric; it must never grant passage through a failed physical lift.
        private bool EnsureLiftIntegrity()
        {
            foreach(var lift in Lifts)
            {
                string problem=lift.Layout?.Problem;
                var broken=lift.Layout?.Members.FirstOrDefault(m=>Model.MemberIndex.TryGetValue(m.Id,out int k)&&Model.Broken[k]);
                if(lift.Layout!=null && problem==null && broken==null)continue;
                string detail=problem??(broken!=null?Loc.F("构件 #{0} 已失效，平台或井架失去完整支撑",broken.Id):Loc.T("井道里没有电梯构件"));
                Fail(FailureReason.LiftOverload,Loc.F("{0}停止运行：{1}。请补齐平台路面、吊索和井架后重新测试。",LiftName(lift),detail),broken==null?null:new List<int>{broken.Id});
                foreach(var rider in lift.Riders)rider.Speed=0;
                return false;
            }
            return true;
        }

        /// <summary>Structure check of the lift carrying these vehicles at these stop positions (cached: deterministic).</summary>
        private LiftCheck CheckLift(LiftState lift,IReadOnlyList<(VehicleState v,double stop)> load)
        {
            string key=string.Join(";",load.Select(q=>q.v.Def.Id+"@"+q.stop.ToString("0.###")));
            if(lift.Cache.TryGetValue(key,out var hit))return hit;
            var axles=new List<(double,double)>();
            foreach(var (v,stop) in load)
                foreach(var a in v.Def.Axles)
                    axles.Add((lift.Layout.Shaft.X0+stop+a.Offset-lift.EntryS,v.Def.Mass*Math.Max(1,v.Def.Units)*a.LoadFraction*StructuralModel.Gravity));
            var c=LiftStructure.Check(Design,Level,Db,lift.Layout,axles);
            lift.Cache[key]=c;return c;
        }

        private string LiftName(LiftState lift)=>lift.LeftBank?Loc.T("左岸电梯"):Loc.T("右岸电梯");

        private bool LiftGate(VehicleState vehicle,ref double next)
        {
            double margin=TunnelRules.LiftCfg.Margin;
            foreach(var lift in Lifts)
            {
                if(vehicle.BodyRearS>lift.ExitEndS-1e-6)continue;
                if(lift.Stops.TryGetValue(vehicle,out double stop))
                {
                    if(lift.Phase==LiftPhase.Unloading)return true;
                    next=lift.Phase==LiftPhase.Boarding?Math.Max(vehicle.FrontS,Math.Min(next,stop)):vehicle.FrontS;
                    return true;
                }
                double wait=lift.EntryS-TunnelRules.LiftWaitClearance-vehicle.FrontOverhang;
                if(next<=wait)return true;
                if(lift.Phase==LiftPhase.Boarding&&lift.DoorOpen>=1-1e-9)
                {
                    double body=BodyLength(vehicle),weight=Weight(vehicle);
                    if(body>lift.Length-2*margin+1e-6)
                    {
                        _failVehicle=vehicle;
                        Fail(FailureReason.VehicleBlocked,Loc.F("{0}车身长 {1:0.0} m，电梯平台只有 {2:0.0} m（两端各留 {3:0.##} m），开不上去：在隧道属性里把电梯平台加长",vehicle.Def.Name,body,lift.Length,margin),null);
                        return false;
                    }
                    if(lift.Layout==null||lift.Layout.Problem!=null)
                    {
                        _failVehicle=vehicle;
                        Fail(FailureReason.LiftOverload,Loc.F("{0}的结构不完整：{1}。建议：用「重建电梯结构」或在井道里补上平台、吊索和柱子",LiftName(lift),lift.Layout?.Problem??Loc.T("井道里没有电梯构件")),null);
                        return false;
                    }
                    string policy=Design.Tunnel?.Lift?.BoardingPolicy??LiftOperation.Shared;
                    bool single=LiftOperation.Centered(policy);
                    double aloneStop=lift.EntryS+LiftOperation.Stop(vehicle.Def,lift.Length,margin,policy);
                    double bumper=lift.Riders.Count==0?lift.A-margin:
                        lift.Stops[lift.Riders[^1]]+lift.Riders[^1].RearOffset-lift.Riders[^1].RearOverhang-lift.Gap;
                    double candidate=single?aloneStop:bumper-vehicle.FrontOverhang;
                    // Alone (at the front of the platform) the vehicle must be carried by the lift structure
                    var alone=CheckLift(lift,new[]{(vehicle,aloneStop)});
                    if(alone.Util>1+1e-9)
                    {
                        _failVehicle=vehicle;lift.Check=alone;
                        Fail(FailureReason.LiftOverload,Loc.F("{0}（{1:0.0} 吨）开上{2}后结构超限：{3}，{4}（利用率 {5:0}%）。建议：{6}",vehicle.Def.Name,weight/9810,LiftName(lift),alone.Where,alone.Why,alone.Util*100,alone.Fix),null);
                        return false;
                    }
                    double occupied=lift.OccupiedLength+(lift.Riders.Count>0?lift.Gap:0)+body;
                    // Share the ride only while the structure still carries everyone at their stop positions
                    if((!single||lift.Riders.Count==0)&&occupied<=lift.Length-2*margin+1e-6&&(lift.Riders.Count==0||
                        CheckLift(lift,lift.Riders.Select(v=>(v,lift.Stops[v])).Append((vehicle,candidate)).ToList()).Util<=1+1e-9))
                    {
                        stop=candidate;
                        lift.Riders.Add(vehicle);lift.Stops.Add(vehicle,stop);
                        lift.Check=CheckLift(lift,lift.Riders.Select(v=>(v,lift.Stops[v])).ToList());
                        next=Math.Max(vehicle.FrontS,Math.Min(next,stop));return true;
                    }
                }
                next=Math.Max(vehicle.FrontS,Math.Min(next,wait));vehicle.WaitingAtPortal=true;return true;
            }
            return true;
        }

        private double LiftFollowingGap(VehicleState follower,VehicleState leader)
        {
            foreach(var lift in Lifts)
                if(lift.Phase==LiftPhase.Boarding&&lift.Stops.ContainsKey(follower)&&lift.Stops.ContainsKey(leader))
                    return Math.Min(follower.Def.FollowGap,lift.Gap);
            return follower.Def.FollowGap;
        }

        private void UpdateLifts()
        {
            foreach(var lift in Lifts)
            {
                double doorStep=FixedDt/lift.DoorSeconds;
                switch(lift.Phase)
                {
                    case LiftPhase.Boarding:
                        if(lift.Riders.Count==0)break;
                        if(lift.Rider.FrontS>=lift.Stops[lift.Rider]-1e-6)lift.BoardWait+=FixedDt;
                        if(lift.BoardWait>=lift.BoardingSeconds&&lift.Riders.All(v=>v.FrontS>=lift.Stops[v]-1e-6))lift.Phase=LiftPhase.Closing;
                        break;
                    case LiftPhase.Closing:
                        lift.DoorOpen=Math.Max(0,lift.DoorOpen-doorStep);
                        if(lift.DoorOpen<=0){lift.Phase=LiftPhase.Travelling;foreach(var v in lift.Riders)v.OnLift=true;}
                        break;
                    case LiftPhase.Travelling:
                        lift.Pos=Math.Min(1,lift.Pos+lift.Speed*FixedDt/Math.Max(lift.Height,1e-6));
                        foreach(var v in lift.Riders)v.LiftDy=lift.Dy;
                        if(lift.Pos>=1)
                        {
                            foreach(var v in lift.Riders){v.FrontS+=lift.Shift;v.LiftDy=0;v.OnLift=false;}
                            lift.Phase=LiftPhase.OpeningDestination;
                        }
                        break;
                    case LiftPhase.OpeningDestination:
                        lift.DoorOpen=Math.Min(1,lift.DoorOpen+doorStep);if(lift.DoorOpen>=1)lift.Phase=LiftPhase.Unloading;
                        break;
                    case LiftPhase.Unloading:
                        if(lift.Riders.All(v=>v.BodyRearS>lift.ExitEndS||v.Finished||v.Fallen))
                        {lift.Riders.Clear();lift.Stops.Clear();lift.BoardWait=0;lift.Phase=LiftPhase.ClosingForReturn;}
                        break;
                    case LiftPhase.ClosingForReturn:
                        lift.DoorOpen=Math.Max(0,lift.DoorOpen-doorStep);if(lift.DoorOpen<=0)lift.Phase=LiftPhase.Returning;
                        break;
                    case LiftPhase.Returning:
                        lift.Pos=Math.Max(0,lift.Pos-lift.Speed*FixedDt/Math.Max(lift.Height,1e-6));
                        if(lift.Pos<=0)lift.Phase=LiftPhase.OpeningHome;
                        break;
                    case LiftPhase.OpeningHome:
                        lift.DoorOpen=Math.Min(1,lift.DoorOpen+doorStep);if(lift.DoorOpen>=1)lift.Phase=LiftPhase.Boarding;
                        break;
                }
            }
        }
    }
}
