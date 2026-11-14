using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;

internal static class StrategyTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        var level=GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(db.GetLevel("level13")));
        level.TrafficEvents=Enumerable.Range(0,3).Select(i=>new TrafficEvent{Type=TrafficEventType.Vehicle,VehicleId="car",Time=i*.25}).ToList();
        var reference=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/ReferenceDesigns/level13_reference.json")));
        var editor=new DesignEditor(reference,level,db);editor.SetLiftLength(16);editor.RebuildLifts("standard");
        string original=JsonSerializer.Serialize(editor.Design,GameDatabase.JsonOptions);
        var report=EngineeringStrategy.Analyze(editor);
        check("Strategy: analysis does not change the source design",original==JsonSerializer.Serialize(editor.Design,GameDatabase.JsonOptions),"");
        check("Strategy: cost breakdown accounts for the actual design once",Math.Abs(report.TotalCost-(report.LiftMembers+report.LiftMachines+report.TubeAndEarth+report.Other))<.01 && Math.Abs(report.TotalCost-editor.UsedCost)<.01 && report.Other>=-.01,report.Text);
        check("Strategy: compares current members and all configured templates",report.Cases.Count==db.TunnelConfig.Lift.Templates.Count+1 && report.Cases.All(c=>double.IsFinite(c.Util) && c.TotalCost>0),"");
        var old=JsonSerializer.Deserialize<LiftDesign>("{}");
        check("Strategy: old saves retain shared dispatch by default",old.BoardingPolicy==LiftOperation.Shared,"");
        editor.SetLiftOperation(LiftOperation.SingleCentered);
        check("Strategy: selected policy is saved with the design",JsonSerializer.Deserialize<BridgeDesign>(JsonSerializer.Serialize(editor.Design,GameDatabase.JsonOptions),GameDatabase.JsonOptions).Tunnel.Lift.BoardingPolicy==LiftOperation.SingleCentered,"");
        var vehicle=db.Vehicles["car"];double stop=LiftOperation.Stop(vehicle,16,.75,LiftOperation.SingleCentered);
        check("Strategy: centered parking uses the body, not just the axle midpoint",Math.Abs((stop+VehicleBody.Front(vehicle)+stop+VehicleBody.Rear(vehicle))/2-8)<1e-9,"");
        var layout=editor.Lifts()[0];
        var front=LiftStructure.Check(editor.Design,level,db,layout,LiftStructure.VehicleAxles(vehicle,layout.Shaft,.75,LiftOperation.Shared));
        var middle=LiftStructure.Check(editor.Design,level,db,layout,LiftStructure.VehicleAxles(vehicle,layout.Shaft,.75,LiftOperation.SingleCentered));
        check("Strategy: centered load reduces the peak hoist demand on the same structure",middle.RopeUtil<front.RopeUtil,$"front={front.RopeUtil}, center={middle.RopeUtil}");
        editor.RebuildLifts("standard");
        check("Strategy: rebuilding members preserves the operating decision",editor.Design.Tunnel.Lift.BoardingPolicy==LiftOperation.SingleCentered,"");
        (bool pass,int riders,double time,bool centered) Run(string policy)
        {
            var trial=new DesignEditor(editor.Design.Clone(),level,db);trial.SetLiftOperation(policy);
            var sim=new BridgeSimulation(trial.Design,level,db);sim.Initialize();int max=0;bool correct=true;
            while(sim.State==SimState.Running && sim.Time<300)
            {
                sim.Step();foreach(var lift in sim.Lifts)
                {
                    max=Math.Max(max,lift.Riders.Count);
                    if(policy==LiftOperation.SingleCentered && lift.Riding && lift.Rider!=null)
                        correct&=Math.Abs((lift.Rider.BodyFrontS+lift.Rider.BodyRearS)/2-(lift.EntryS+lift.Length/2))<1e-5;
                }
            }
            return(sim.Result.IsSuccess,max,sim.Time,correct);
        }
        var single=Run(LiftOperation.SingleCentered);var shared=Run(LiftOperation.Shared);
        check("Strategy: single dispatch crosses both lifts with one centered vehicle",single.pass && single.riders==1 && single.centered,$"pass={single.pass} max={single.riders}");
        check("Strategy: shared dispatch preserves batching and its time advantage",shared.pass && shared.riders>1 && shared.time<single.time,$"shared={shared.time}, single={single.time}, riders={shared.riders}");
        editor.SetLiftOperation(LiftOperation.Shared);editor.SetLiftOperation(LiftOperation.SingleCentered);editor.Undo();
        check("Strategy: operating decisions are undoable",editor.Design.Tunnel.Lift.BoardingPolicy==LiftOperation.Shared,"");
        check("Strategy: invalid policies do not mutate the design",!editor.SetLiftOperation("invalid").Ok && editor.Design.Tunnel.Lift.BoardingPolicy==LiftOperation.Shared,"");
    }
}
