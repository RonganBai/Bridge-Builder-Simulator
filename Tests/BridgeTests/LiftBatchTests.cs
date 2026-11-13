using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

internal static class LiftBatchTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        BridgeDesign Reference()=>GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/ReferenceDesigns/level13_reference.json")));
        LevelDefinition Level(int count)
        {
            var level=GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(db.GetLevel("level13")));
            level.TrafficEvents=Enumerable.Range(0,count).Select(i=>new TrafficEvent {Type=TrafficEventType.Vehicle,VehicleId="car",Time=i*0.25}).ToList();
            return level;
        }
        (BridgeSimulation simulation,int max,bool safe,bool mixed) Exercise(double length,int count,string[] vehicles=null)
        {
            var level=Level(count);
            if(vehicles!=null)level.TrafficEvents=vehicles.Select((id,i)=>new TrafficEvent {Type=TrafficEventType.Vehicle,VehicleId=id,Time=i*0.25}).ToList();
            var editor=new DesignEditor(Reference(),level,db);editor.SetLiftLength(length);
            var simulation=new BridgeSimulation(editor.Design,level,db);simulation.Initialize();int max=0;bool safe=true,mixed=false;
            while(simulation.State==SimState.Running&&simulation.Time<300)
            {
                simulation.Step();
                foreach(var lift in simulation.Lifts)
                {
                    max=Math.Max(max,lift.Riders.Count);
                    safe&=lift.OccupiedLength<=lift.Length-2*TunnelRules.LiftCfg.Margin+1e-6&&lift.LoadForce<=lift.Capacity+1e-6;
                    if(lift.Riding)
                    {
                        mixed|=lift.Riders.Select(v=>v.Def.Length).Distinct().Count()>1;
                        safe&=lift.DoorOpen==0&&lift.Riders.All(v=>v.OnLift&&Math.Abs(v.LiftDy-lift.Dy)<1e-8&&v.BodyRearS>=lift.EntryS+TunnelRules.LiftCfg.Margin-1e-6&&v.BodyFrontS<=lift.A-TunnelRules.LiftCfg.Margin+1e-6);
                        for(int i=1;i<lift.Riders.Count;i++)safe&=lift.Riders[i-1].BodyRearS-lift.Riders[i].BodyFrontS>=lift.Gap-1e-6;
                    }
                }
                if(simulation.Vehicles.Any(v=>!v.Finished))safe&=!simulation.QuietTraffic;
            }
            return(simulation,max,safe,mixed);
        }
        var pooled=Exercise(16,8);
        check("Lift pooling: multiple cars share a trip",pooled.max>=2,$"max={pooled.max}");
        check("Lift pooling: every car completes both lifts",pooled.simulation.Result.IsSuccess&&pooled.simulation.Result.VehicleResults.Count==8,pooled.simulation.Result.FailureMessage??$"time={pooled.simulation.Time}");
        check("Lift pooling: body limits, load, gaps, doors and riding offsets remain valid",pooled.safe,"");
        var single=Exercise(6,3);var batch=Exercise(16,3);
        check("Lift pooling: a short platform still operates one car at a time",single.max==1&&single.simulation.Result.IsSuccess,single.simulation.Result.FailureMessage??"");
        check("Lift pooling: grouping improves multi-car completion time",batch.simulation.Result.IsSuccess&&batch.simulation.Time<single.simulation.Time,$"batch={batch.simulation.Time:0.0}, single={single.simulation.Time:0.0}");
        var sizes=db.Vehicles.Values.Where(v=>!RailTrack.IsTrain(v)&&v.Length>0&&v.Length<8&&v.Units==1).OrderBy(v=>v.Length).Select(v=>v.Id).Take(3).ToArray();
        var varied=Exercise(26,6,sizes.Concat(sizes).ToArray());
        check("Lift pooling: different vehicle lengths can share a trip",varied.mixed&&varied.max>=2,"");
        check("Lift pooling: mixed-size traffic preserves all load and clearance limits",varied.safe&&varied.simulation.Result.IsSuccess,varied.simulation.Result.FailureMessage??"");

        var testLevel=Level(1);var testEditor=new DesignEditor(Reference(),testLevel,db);testEditor.SetLiftLength(16);
        var gate=typeof(BridgeSimulation).GetMethod("LiftGate",BindingFlags.NonPublic|BindingFlags.Instance);
        VehicleState Car(double mass,double length=4)
        {
            var definition=JsonSerializer.Deserialize<VehicleDefinition>(JsonSerializer.Serialize(db.Vehicles["car"]));definition.Mass=mass;definition.Length=length;
            return new VehicleState {Def=definition,Lane=TunnelRules.Lane};
        }
        BridgeSimulation Fresh(){var s=new BridgeSimulation(testEditor.Design,testLevel,db);s.Initialize();return s;}
        bool Admit(BridgeSimulation simulation,VehicleState car)
        {
            var lift=simulation.Lifts[0];car.FrontS=lift.EntryS-0.5-car.FrontOverhang-0.05;
            var args=new object[]{car,car.FrontS+0.1};return (bool)gate.Invoke(simulation,args);
        }
        var capacity=Fresh();var heavy1=Car(40000);var heavy2=Car(40000);
        Admit(capacity,heavy1);Admit(capacity,heavy2);
        check("Lift pooling: combined overload waits for another trip",capacity.Lifts[0].Riders.Count==1&&heavy2.WaitingAtPortal&&capacity.State==SimState.Running,"");
        var oversized=Fresh();Admit(oversized,Car(1000,18));
        check("Lift pooling: an individually oversized vehicle is rejected",oversized.Result.FailureReason==FailureReason.VehicleBlocked,oversized.Result.FailureMessage);
        var overweight=Fresh();Admit(overweight,Car(70000));
        check("Lift pooling: an individually overweight vehicle is rejected",overweight.Result.FailureReason==FailureReason.LiftOverload,overweight.Result.FailureMessage);

        var idle=Fresh();typeof(BridgeSimulation).GetProperty("Time").SetValue(idle,2.0);
        check("Playback: an empty scene may fast-forward",idle.QuietTraffic,"");
        var approaching=Car(1000);approaching.Path=idle.TunnelPath;idle.Vehicles.Add(approaching);
        check("Playback: approach-road vehicles prevent automatic fast-forward",!idle.QuietTraffic,"");
        approaching.WaitingAtPortal=true;check("Playback: queued cars prevent automatic fast-forward",!idle.QuietTraffic,"");
        approaching.OnLift=true;check("Playback: lifted cars prevent automatic fast-forward",!idle.QuietTraffic,"");
        idle.Vehicles.Clear();idle.Lifts[0].Phase=BridgeSimulation.LiftPhase.Returning;
        check("Playback: an empty returning platform also stays at the selected rate",!idle.QuietTraffic,"");
        bool replay=false;
        for(int i=0;i<pooled.simulation.HistoryCount;i++)
        {
            var frame=pooled.simulation.GetHistoryFrame(i);
            if(frame.LiftPlatformDy==null||!frame.LiftPlatformDy.Any(v=>Math.Abs(v)>0.1))continue;
            replay=true;check("Lift replay: platform positions and door states are recorded",frame.LiftDoorOpen?.Length==2&&frame.LiftPlatformDy.Length==2,"");
            pooled.simulation.FailureSnapshot.CopyFrom(frame);
            check("Lift replay: snapshot copies retain mechanical state",pooled.simulation.FailureSnapshot.LiftPlatformDy.SequenceEqual(frame.LiftPlatformDy)&&pooled.simulation.FailureSnapshot.LiftDoorOpen.SequenceEqual(frame.LiftDoorOpen),"");break;
        }
        check("Lift replay: the recording includes a moving platform",replay,"");
        foreach(string id in new[]{"level13","level14","level15"})
        {
            var level=db.GetLevel(id);var design=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/ReferenceDesigns",id+"_reference.json")));
            var result=new BridgeSimulation(new DesignEditor(design,level,db).Design,level,db).RunToEnd();
            check("Lift regression: "+id+" reference still completes",result.IsSuccess,result.FailureMessage??"");
        }
    }
}
