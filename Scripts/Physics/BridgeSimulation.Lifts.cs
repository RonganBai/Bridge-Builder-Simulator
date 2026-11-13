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
            public double A, Jump, Shift, Length, Height, Speed, Capacity;
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
            foreach(int k in TunnelPath.LiftSegments.OrderBy(k=>k))
            {
                double dx=TunnelPath.PointX[k+1]-TunnelPath.PointX[k],dy=TunnelPath.PointY[k+1]-TunnelPath.PointY[k];
                double jump=Math.Sqrt(dx*dx+dy*dy),length=TunnelGeo.LiftLength;
                Lifts.Add(new LiftState {Down=dy<0,A=TunnelPath.SegmentStartS[k],Jump=jump,Shift=jump+length,Length=length,Height=Math.Abs(dy),
                    Speed=Math.Max(0.1,config.Speed),Capacity=TunnelRules.LiftCapacity(length),Gap=Math.Max(0.2,config.VehicleGap),
                    BoardingSeconds=Math.Max(0,config.BoardingSeconds),DoorSeconds=Math.Max(0.1,config.DoorSeconds)});
            }
        }

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
                double wait=lift.EntryS-0.5-vehicle.FrontOverhang;
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
                    if(weight>lift.Capacity+1e-6)
                    {
                        _failVehicle=vehicle;
                        Fail(FailureReason.LiftOverload,Loc.F("{0}（{1:0.0} 吨）超过电梯平台承载 {2:0.0} 吨：把电梯平台加长",vehicle.Def.Name,weight/9810,lift.Capacity/9810),null);
                        return false;
                    }
                    double occupied=lift.OccupiedLength+(lift.Riders.Count>0?lift.Gap:0)+body;
                    if(occupied<=lift.Length-2*margin+1e-6&&lift.LoadForce+weight<=lift.Capacity+1e-6)
                    {
                        double bumper=lift.Riders.Count==0?lift.A-margin:
                            lift.Stops[lift.Riders[^1]]+lift.Riders[^1].RearOffset-lift.Riders[^1].RearOverhang-lift.Gap;
                        stop=bumper-vehicle.FrontOverhang;
                        lift.Riders.Add(vehicle);lift.Stops.Add(vehicle,stop);
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
