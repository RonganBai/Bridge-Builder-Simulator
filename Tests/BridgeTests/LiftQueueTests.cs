using System;
using System.IO;
using System.Linq;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

/// <summary>Vehicles queued for a lift must hold their position: no creeping into the shaft, no overlapping.</summary>
internal static class LiftQueueTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        foreach(string id in new[]{"level13","level14","level15"})foreach(double liftLen in new[]{0.0,8,12,26})
        {
            var level=GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(db.GetLevel(id)));
            string[] mix={"car","semi_long","motorcycle","car","suv","pickup","semi_long","ambulance","car","suv_large","truck","semi_long","car","van"};
            level.TrafficEvents=level.TrafficEvents.Where(e=>e.Type!=TrafficEventType.Vehicle&&e.Type!=TrafficEventType.Train).ToList();
            for(int i=0;i<mix.Length;i++)if(db.Vehicles.ContainsKey(mix[i]))level.TrafficEvents.Add(new TrafficEvent{Type=TrafficEventType.Vehicle,VehicleId=mix[i],Time=i*0.3});
            var design=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/ReferenceDesigns",id+"_reference.json")));
            var ed=new DesignEditor(design,level,db);if(liftLen>0)ed.SetLiftLength(liftLen);var sim=new BridgeSimulation(ed.Design,level,db);sim.Initialize();
            string creep=null,overlap=null,jerk=null;var lastSpeed=new System.Collections.Generic.Dictionary<VehicleState,double>();var held=new System.Collections.Generic.Dictionary<VehicleState,(double s,bool arrived)>();
            while(sim.State==SimState.Running&&sim.Time<400)
            {
                sim.Step();
                foreach(var v in sim.Vehicles)
                {
                    if(v.Finished||v.Fallen||v.OnLift||v.Lane!=TunnelRules.Lane)continue;
                    foreach(var lift in sim.Lifts)
                    {
                        if(v.BodyRearS>lift.ExitEndS-1e-6)continue;
                        bool rider=lift.Riders.Contains(v);
                        bool open=lift.Phase==BridgeSimulation.LiftPhase.Boarding&&lift.DoorOpen>=1-1e-9;
                        // Not on this platform: the body front must stay in front of the entry
                        if(!rider&&v.BodyFrontS>lift.EntryS-(TunnelRules.LiftEndWallOffset+TunnelRules.LiftEndWallThickness/2)-0.2+1e-6&&creep==null)
                            creep=$"{v.Def.Name} t={sim.Time:0.00} front={v.BodyFrontS:0.00} entry={lift.EntryS:0.00} phase={lift.Phase}";
                        // A rider whose platform is away must not move
                        if(rider&&!open&&lift.Phase!=BridgeSimulation.LiftPhase.Unloading)
                        {
                            bool arrived=lift.Phase==BridgeSimulation.LiftPhase.OpeningDestination;
                            if(held.TryGetValue(v,out var h)&&h.arrived==arrived&&Math.Abs(h.s-v.FrontS)>1e-9&&creep==null)creep=$"rider {v.Def.Name} moved while {lift.Phase} t={sim.Time:0.00}";
                            held[v]=(v.FrontS,arrived);
                        }
                        else held.Remove(v);
                        break;
                    }
                }
                foreach(var v in sim.Vehicles)
                {
                    if(v.Finished||v.Fallen||v.OnLift||double.IsNaN(v.Speed)){lastSpeed.Remove(v);continue;}
                    // Smooth stops: never faster than about twice the braking rate (following a braking vehicle), never instant
                    if(lastSpeed.TryGetValue(v,out double was)&&was>0.2&&was-v.Speed>BridgeSimulation.VehicleBrake*BridgeSimulation.FixedDt*2.2+1e-6&&jerk==null)   // the last crawl onto the line may snap
                        jerk=$"{v.Def.Name} t={sim.Time:0.00} {was:0.00}->{v.Speed:0.00} m/s";
                    lastSpeed[v]=v.Speed;
                }
                var lane=sim.Vehicles.Where(v=>!v.Finished&&!v.Fallen&&!v.OnLift&&v.Lane==TunnelRules.Lane&&v.Path!=null).ToList();
                for(int i=1;i<lane.Count&&overlap==null;i++)
                    if(lane[i].BodyFrontS>lane[i-1].BodyRearS+1e-6&&lane[i].BodyRearS<lane[i-1].BodyFrontS)
                        overlap=$"{lane[i].Def.Name} into {lane[i-1].Def.Name} t={sim.Time:0.00}";
            }
            check("Lift queue: "+id+" L"+liftLen+" waiting vehicles stay clear of the shaft wall and door",creep==null,creep??"");
            check("Lift queue: "+id+" L"+liftLen+" queued vehicles never overlap",overlap==null,overlap??"");
            check("Lift queue: "+id+" L"+liftLen+" vehicles brake gradually to their stop lines",jerk==null,jerk??"");
            bool fits=liftLen==0||TunnelRules.LiftFits(liftLen)>=db.Vehicles["semi_long"].Length;
            check("Lift queue: "+id+" L"+liftLen+(fits?" mixed convoy completes":" too-short platform stops the semi"),fits?sim.Result.IsSuccess:sim.Result.FailureReason==FailureReason.VehicleBlocked,sim.Result.FailureMessage??$"t={sim.Time}");
        }
    }
}
internal static class LiftAlignTests
{
    /// <summary>The simulated boarding / exit points of each lift must sit at the drawn landing doors, even when a tube end is a little off the wall.</summary>
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        foreach(string id in new[]{"level13","level14","level15"})
        foreach(double shift in new[]{0.0,-0.4,0.4})
        {
            var level=db.GetLevel(id);
            var design=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/ReferenceDesigns",id+"_reference.json")));
            var tube=design.Nodes.Where(n=>n.TunnelPoint).OrderBy(n=>n.Position.X).ToList();
            if(tube.Count>=2){var a=tube[0].Position;tube[0].Position=new Vec3d(a.X+shift,a.Y,a.Z);var b=tube[^1].Position;tube[^1].Position=new Vec3d(b.X-shift,b.Y,b.Z);}
            var sim=new BridgeSimulation(new DesignEditor(design,level,db).Design,level,db);sim.Initialize();
            if(sim.TunnelGeo==null||sim.Lifts.Count!=2){check($"Lift align: {id} shift {shift} builds two lifts",false,$"lifts={sim.Lifts.Count}");continue;}
            var shafts=TunnelRules.Shafts(level,sim.TunnelGeo);string bad=null;
            for(int i=0;i<2;i++)
            {
                var lift=sim.Lifts[i];var sh=shafts[i];
                sim.TunnelPath.Evaluate(lift.EntryS,null,1,out double ex,out double ey);
                sim.TunnelPath.Evaluate(lift.A,null,1,out double ax,out double ay);
                sim.TunnelPath.Evaluate(lift.ExitEndS,null,1,out double xx,out double xy);
                double entryY=sh.Left?sh.Top:sh.Bottom,exitY=sh.Left?sh.Bottom:sh.Top;
                if(Math.Abs(ex-sh.X0)>1e-6||Math.Abs(ey-entryY)>1e-6||Math.Abs(ax-sh.X1)>1e-6||Math.Abs(ay-entryY)>1e-6||Math.Abs(xx-sh.X1)>1e-6||Math.Abs(xy-exitY)>1e-6)
                    bad=$"lift {i}: entry ({ex:0.00},{ey:0.00}) end ({ax:0.00},{ay:0.00}) exit ({xx:0.00},{xy:0.00}) shaft x {sh.X0:0.00}..{sh.X1:0.00} y {sh.Bottom:0.00}..{sh.Top:0.00}";
            }
            check($"Lift align: {id} tube end offset {shift:+0.0;-0.0;0} m keeps boarding and exit at the landing doors",bad==null,bad??"");
        }
    }
}
