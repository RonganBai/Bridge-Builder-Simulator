using System;
using System.IO;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.Traffic;

internal static class SceneJointTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        foreach(var level in db.Levels)
        {
            var layout=new SceneryLayout(level);
            check($"Scenery: {level.Id} allocates eight separate building plots",layout.Buildings.Count==8 && layout.Hills.Count==4,"");
            bool overlap=false;
            for(int i=0;i<layout.Reservations.Count;i++)for(int j=i+1;j<layout.Reservations.Count;j++)overlap|=SceneryLayout.Overlaps(layout.Reservations[i],layout.Reservations[j],0.5);
            check($"Scenery: {level.Id} building, park and hill footprints never overlap",!overlap && layout.Buildings.All(p=>Math.Abs(p.Z)-p.HalfZ>=SceneryLayout.RoadClearance),"");
        }
        var sample=new SceneryLayout(db.GetLevel("level1"));
        check("Scenery: approaches support the entire longest consist when it first appears",SceneryLayout.BankLength>=RoadPath.ApproachLength+db.Vehicles.Values.Max(v=>v.Length)+10,"");
        check("Scenery: placement rejects roads and existing buildings",!sample.TryReserve(new("bad","tree",-29,-12,2,2,0)) && !sample.TryReserve(new("road","tree",-30,0,1,1,0)),"");
        var lv=new LevelDefinition{Id="split",Budget=100000,Terrain=new TerrainConfig{RiverWidth=6,BuildMinY=-4,BuildMaxY=10,WaterLevel=-4}};
        lv.Anchors.Add(new AnchorDefinition{Id="L",Position=new Vec3d(0,0),SupportType=SupportType.Pinned,IsRoadStart=true});
        lv.Anchors.Add(new AnchorDefinition{Id="R",Position=new Vec3d(6,0),SupportType=SupportType.Pinned,IsRoadEnd=true});
        lv.AllowedMaterials.Add("road");
        BridgeDesign Original(){var d=BridgeDesign.CreateForLevel(lv);d.Members.Add(new BridgeMember{Id=1,StartNodeId=1,EndNodeId=2,MaterialId="road"});d.NextMemberId=2;return d;}
        var ed=new DesignEditor(Original(),lv,db);double cost=ed.UsedCost;var r=ed.SplitMember(1,false);
        check("Split: normal mode creates a new independent connection and two three-meter members",r.Ok && ed.Design.FindNode(r.CreatedId).IsSplitJoint && !ed.Design.FindNode(r.CreatedId).IsRigidSplit && ed.Design.Members.Count==2 && ed.Design.Members.All(m=>Math.Abs(m.Length-3)<1e-9),r.Message);
        var model=StructuralModel.Build(ed.Design,lv,db);var path=RoadPath.Build(model,lv,out var reason,out _,out _);
        check("Road: two three-meter decks stay separate in the drive path",reason==FailureReason.None && path.Complete && path.DeckMemberIds.Count==2 && model.Length.All(l=>l==3),reason.ToString());
        check("Split: normal joint has real moment releases and no hidden rigid parent",model.RigidSplits.Count==0 && model.SplitJoints.Count==1 && Enumerable.Range(0,model.MemberCount).All(k=>model.A[k]==model.NodeIndex[r.CreatedId]?model.ReleaseA[k]:model.ReleaseB[k]),"");
        check("Split: splitting preserves total cost",Math.Abs(ed.UsedCost-cost)<1e-8,"");
        check("Split: ordinary connection can move independently",ed.MoveNode(r.CreatedId,3,0.5).Ok,"");
        var saved=GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(ed.Design));
        check("Split: ordinary joint type survives cloning and saving",saved.FindNode(r.CreatedId).IsSplitJoint && saved.Clone().FindNode(r.CreatedId).IsSplitJoint,"");
        ed.Undo();ed.Undo();check("Split: undo restores the unsplit original",ed.Design.Members.Count==1 && ed.Design.FindNode(r.CreatedId)==null,"");
        var rigid=new DesignEditor(Original(),lv,db);var blue=rigid.SplitMember(1,true);rigid.SplitMember(1,true);
        check("Split: rigid mode retains the parent span",rigid.Design.FindNode(blue.CreatedId).IsRigidSplit && !rigid.Design.FindNode(blue.CreatedId).IsSplitJoint,"");
        var converted=rigid.ConvertRigidToSplit(blue.CreatedId);var cm=StructuralModel.Build(rigid.Design,lv,db);
        check("Split: converting an old rigid point partitions the remaining rigid span",converted.Ok && rigid.Design.FindNode(blue.CreatedId).IsSplitJoint && rigid.Design.Nodes.Where(n=>n.IsRigidSplit).All(n=>n.RigidEndB==blue.CreatedId || n.RigidEndA==blue.CreatedId),converted.Message);
        rigid.Undo();check("Split: conversion is undoable",rigid.Design.FindNode(blue.CreatedId).IsRigidSplit,"");
        var overlapDesign=ed.Design.Clone();ed.Redo();overlapDesign=ed.Design.Clone();
        overlapDesign.Members.Add(new BridgeMember{Id=99,StartNodeId=1,EndNodeId=2,MaterialId="road"});
        var om=StructuralModel.Build(overlapDesign,lv,db);var route=RoadPath.Build(om,lv,out var or,out _,out _);
        check("Road: an overlapping invalid six-meter member cannot hide the valid three-meter route",route.Complete && or==FailureReason.None && !route.DeckMemberIds.Contains(99),or.ToString());
        overlapDesign.Members.Add(new BridgeMember{Id=100,StartNodeId=1,EndNodeId=2,MaterialId="deck_steel"});
        om=StructuralModel.Build(overlapDesign,lv,db);route=RoadPath.Build(om,lv,out or,out _,out _);
        check("Road: even a legal long overlap cannot replace two connected three-meter members",route.Complete && or==FailureReason.None && route.DeckMemberIds.Count==2 && !route.DeckMemberIds.Contains(100),or.ToString());
        var actual=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Tests/BridgeTests/Fixtures/level7_three_meter_decks.json")));
        var am=StructuralModel.Build(actual,db.GetLevel("level7"),db);RoadPath.Build(am,db.GetLevel("level7"),out var ar,out _,out _);
        check("Road: player's saved three-meter pieces are never reported as a six-meter member",ar!=FailureReason.DeckSpanTooLong && Enumerable.Range(0,am.MemberCount).Where(k=>am.Mat[k].IsDeck).All(k=>Math.Abs(am.Length[k]-3)<1e-8),ar.ToString());
    }
}
