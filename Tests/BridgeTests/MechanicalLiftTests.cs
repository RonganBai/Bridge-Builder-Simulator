using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;

internal static class MechanicalLiftTests
{
    public static void Run(GameDatabase db,Action<string,bool,string> check)
    {
        string root=Directory.GetCurrentDirectory();
        while(!Directory.Exists(Path.Combine(root,"Data/Materials"))&&Directory.GetParent(root)!=null)root=Directory.GetParent(root).FullName;
        foreach(string id in new[]{"level13","level14","level15"})
        {
            var provided=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/LiftChallenges",id+"_provided.json")));
            var challenge=LiftChallengeRules.Prepare(db.GetLevel(id),provided,db);LiftMechanics.Enable(challenge,db);
            var reference=LiftMechanics.Reference(provided,challenge,db);
            var finished=new BridgeSimulation(reference,challenge,db).RunToEnd();
            check("Mechanical lift: "+id+" physical cylinder reference completes",finished.IsSuccess,finished.FailureMessage);
        }
        var source=CustomLevelBuilder.Build(new CustomLevelSpec{Name="机构电梯测试",RiverWidth=20},db);
        CustomLiftLevels.Generate(source,db,"tunnel_immersed",true,out var level,out var error);
        var e=new DesignEditor(null,level,db);
        check("Mechanical lift: starts with sparse mounts and no automatic platform or motor",level.MechanicalLift && e.Design.Nodes.Count(n=>n.LiftRole?.StartsWith("mount")==true)==4 && !e.Design.Members.Any(m=>!m.Provided),error);
        var invalid=new DesignEditor(null,level,db);var mountPoint=invalid.Design.Nodes.First(n=>n.LiftRole=="mount0");
        var loose=invalid.AddMemberToNewNode(mountPoint.Id,mountPoint.Position.X+1,mountPoint.Position.Y-1,"cable");
        int looseEnd=invalid.Design.FindMember(loose.CreatedId).EndNodeId;
        var badPulley=invalid.InstallDevice(looseEnd,"fixed_pulley");
        check("Devices: pulley on an unsupported rope endpoint is rejected with feedback",!badPulley.Ok&&badPulley.Message.Contains("稳定"),badPulley.Message);
        check("Devices: counterweight cannot be attached to a fixed support",!invalid.InstallDevice(mountPoint.Id,"counterweight").Ok,"");
        var blank=new BridgeSimulation(e.Design,level,db);blank.Initialize();
        check("Mechanical lift: unfinished design stays stopped",blank.State==SimState.Failed && blank.Vehicles.Count==0,blank.Result.FailureMessage);
        foreach(var l in e.Lifts())
        {
            var road=e.AddMember(l.Deck0.Id,l.Deck1.Id,"deck_steel");
            check("Mechanical lift: platform can be built from its connection points",road.Ok,road.Message);
            foreach(var (role,deck) in new[]{("mount0",l.Deck0),("mount1",l.Deck1)})
            {
                var mount=e.Design.Nodes.Single(n=>n.LiftBank==l.Bank&&n.LiftRole==role);
                var rod=e.AddMember(mount.Id,deck.Id,"hydraulic_lift");
                check("Mechanical lift: physical hydraulic rod can be installed",rod.Ok,rod.Message);
                if(rod.Ok)e.Design.FindMember(rod.CreatedId).Stroke=-.95;
            }
        }
        check("Mechanical lift: valid hydraulic layout has full travel",e.Lifts().All(l=>l.Problem==null),LiftMechanics.Summary(e.Design,level,db));
        var sim=new BridgeSimulation(e.Design,level,db);var result=sim.RunToEnd();
        check("Mechanical lift: player-built cylinders carry cars through both lifts",result.IsSuccess,result.FailureMessage);
        var ropeEditor=new DesignEditor(null,level,db);
        foreach(var l in ropeEditor.Lifts())
        {
            ropeEditor.AddMember(l.Deck0.Id,l.Deck1.Id,"deck_steel");
            var left=ropeEditor.Design.Nodes.Single(n=>n.LiftBank==l.Bank&&n.LiftRole=="mount0");
            var right=ropeEditor.Design.Nodes.Single(n=>n.LiftBank==l.Bank&&n.LiftRole=="mount1");
            ropeEditor.AddMember(right.Id,l.Deck1.Id,"hydraulic_lift");
            double x=l.Shaft.X1+ropeEditor.LiftCfgOf.ColumnOffset,y=right.Position.Y;
            var extension=ropeEditor.AddMemberToNewNode(right.Id,x,y,"steel");
            var over=ropeEditor.Design.Members.First(m=>m.Id==extension.CreatedId).EndNodeId;
            ropeEditor.InstallDevice(left.Id,"fixed_pulley");ropeEditor.InstallDevice(over,"fixed_pulley");
            ropeEditor.AddMember(l.Deck0.Id,left.Id,"cable_composite");
            ropeEditor.AddMember(left.Id,over,"cable_composite");
            var tail=ropeEditor.AddMemberToNewNode(over,x,y-1,"cable_composite");
            int weight=ropeEditor.Design.FindMember(tail.CreatedId).EndNodeId;
            ropeEditor.InstallDevice(weight,"counterweight");
            var pit=l.Pits.OrderBy(n=>n.Position.X).Last();
            var drive=ropeEditor.AddMember(pit.Id,weight,"hydraulic_lift");
            if(drive.Ok)ropeEditor.Design.FindMember(drive.CreatedId).Stroke=-.95;
        }
        check("Mechanical lift: pulley and counterweight circuit has a valid drive path",ropeEditor.Lifts().All(l=>l.Problem==null),LiftMechanics.Summary(ropeEditor.Design,level,db));
        var ropeSim=new BridgeSimulation(ropeEditor.Design,level,db);var ropeResult=ropeSim.RunToEnd();
        if(!ropeResult.IsSuccess)foreach(var ev in ropeSim.Events.Take(8))Console.WriteLine("ROPE "+ev.MemberId+" "+ev.Text);
        check("Mechanical lift: cylinders drive redirected ropes and counterweights through both lifts",ropeResult.IsSuccess,ropeResult.FailureMessage);
        var branchLift=ropeEditor.Lifts()[0];var branchPulley=ropeEditor.Design.Nodes.First(n=>n.Pulley&&branchLift.Nodes.Contains(n.Id));
        var branch=ropeEditor.AddMember(branchLift.Deck1.Id,branchPulley.Id,"cable");
        check("Devices: a third rope branch is rejected with feedback",!branch.Ok&&branch.Message.Contains("两段"),branch.Message);
        var l0=ropeEditor.Lifts()[0];var plan=LiftMechanics.Plan(ropeEditor.Design,level,db,l0);
        check("Mechanical lift: counterweight moves opposite to the platform",ropeEditor.Design.Nodes.Where(n=>n.CounterweightTonnes>0 && l0.Nodes.Contains(n.Id)).All(n=>plan.Motion.GetValueOrDefault(n.Id)==-1),"");
        foreach(var rod in e.Design.Members.Where(m=>db.GetMaterial(m.MaterialId)?.Extendable==true))rod.Stroke=-.1;
        check("Mechanical lift: insufficient stroke is explicit",e.Lifts().All(l=>l.Problem?.Contains("行程") == true),LiftMechanics.Summary(e.Design,level,db));
        // An ideal fixed pulley redirects tension. One hanging load and a fixed rope end share one rope.
        var d=new BridgeDesign();
        d.Nodes.Add(new BridgeNode{Id=1,Position=new Vec3d(0,4),IsAnchor=true,AnchorId="p1",SupportType=SupportType.Pinned,Pulley=true});
        d.Nodes.Add(new BridgeNode{Id=2,Position=new Vec3d(0,0),CounterweightTonnes=5});
        d.Nodes.Add(new BridgeNode{Id=3,Position=new Vec3d(4,4),IsAnchor=true,AnchorId="p2",SupportType=SupportType.Pinned});
        d.Members.Add(new BridgeMember{Id=1,StartNodeId=2,EndNodeId=1,MaterialId="cable",Length=4});
        d.Members.Add(new BridgeMember{Id=2,StartNodeId=1,EndNodeId=3,MaterialId="cable",Length=4});
        var model=StructuralModel.Build(d,new LevelDefinition{Anchors=new(){new AnchorDefinition{Id="p1",SupportType=SupportType.Pinned},new AnchorDefinition{Id="p2",SupportType=SupportType.Pinned}}},db);var load=new double[model.NodeCount*2];LoadCalculator.Compute(model,Array.Empty<AxleLoad>(),0,load);
        var output=new SolveOutput();new StructureSolver().Solve(model,load,output);
        check("Pulley: redirected rope has equal tension on both segments",output.Stable && Math.Abs(output.AxialForce[0]-output.AxialForce[1])<.001 && output.AxialForce[0]>49000,output.Message);
        check("Pulley: support receives both vertical and horizontal rope reactions",Math.Abs(output.Reaction[0])>49000 && output.Reaction[1]>49000,"");
        var copy=GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(d));
        check("Devices: save round-trip retains pulley, mass and price",copy.Nodes[0].Pulley && copy.Nodes[1].CounterweightTonnes==5 && copy.TotalCost()==d.TotalCost() && d.TotalCost()==3100,"");
        var stable=e.Design.Nodes.First(n=>n.LiftRole=="mount0");
        double cost=e.UsedCost;var install=e.InstallDevice(stable.Id,"fixed_pulley");
        check("Devices: mounting charges actual cost and supports undo",install.Ok && Math.Abs(e.UsedCost-cost-1800)<.01 && e.Undo() && !e.Design.FindNode(stable.Id).Pulley,install.Message);
    }
}
