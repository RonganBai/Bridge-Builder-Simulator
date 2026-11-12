using System;
using System.Linq;
using System.Collections.Generic;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Traffic;

internal static class LinkageTests
{
    public static void Run(GameDatabase db, Action<string,bool,string> check)
    {
        var level=new LevelDefinition { Id="linkage", Budget=100000 };
        level.Anchors=new List<AnchorDefinition> {
            new AnchorDefinition {Id="A",Position=new Vec3d(0,0), SupportType=SupportType.Pinned},
            new AnchorDefinition {Id="B",Position=new Vec3d(4,0), SupportType=SupportType.Pinned}
        };
        var design=BridgeDesign.CreateForLevel(level);
        design.Nodes.AddRange(new[] {
            new BridgeNode {Id=3,Position=new Vec3d(0,3)},
            new BridgeNode {Id=4,Position=new Vec3d(4,3)},
            new BridgeNode {Id=5,Position=new Vec3d(2,5)}
        });
        void Add(int id,int a,int b,string material) => design.Members.Add(new BridgeMember {Id=id,StartNodeId=a,EndNodeId=b,MaterialId=material});
        Add(1,1,3,"steel"); Add(2,3,4,"road"); Add(3,4,2,"steel");
        Add(4,3,5,"steel"); Add(5,4,5,"steel"); Add(6,1,4,"hydraulic");
        design.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData {MemberId=6,Ratio=-0.2});
        var m=StructuralModel.Build(design,level,db); var engine=new LinkageSystem();
        check("Linkage: grounded four-bar with rigid triangular coupler initializes",engine.Initialize(m,design.Mechanisms.Linkage),engine.Fault);
        var lengths=(double[])m.Length.Clone(); var masses=(double[])m.Mass.Clone();
        bool moved=true;
        for(int i=1;i<=100;i++) if(!engine.TryMove(i/100.0)){moved=false;break;}
        check("Linkage: hydraulic contraction moves both lower bars and deck",moved && m.X[2]<-0.5 && m.X[3]<3.5,engine.Fault);
        double error=Enumerable.Range(0,5).Max(k=>Math.Abs(m.Length[k]-lengths[k]));
        check("Linkage: all passive rods and rigid coupler preserve length",error<2e-6,$"error={error}");
        check("Linkage: actuator reaches commanded length and mass is conserved",Math.Abs(m.Length[5]-4)<2e-6 && m.Mass.SequenceEqual(masses),"");
        check("Linkage: anchor coordinates remain fixed",m.X[0]==0 && m.Y[0]==0 && m.X[1]==4 && m.Y[1]==0,"");
        bool closed=true;
        for(int i=99;i>=0;i--) if(!engine.TryMove(i/100.0)){closed=false;break;}
        check("Linkage: complete cycle returns to the initial assembly branch",closed && Enumerable.Range(0,m.NodeCount).All(i=>Math.Abs(m.X[i]-m.X0[i])+Math.Abs(m.Y[i]-m.Y0[i])<1e-5),engine.Fault);
        // Two identical actuators are redundant compatible constraints, not a false topology lock.
        var pair=design.Clone(); pair.Members.Add(new BridgeMember {Id=7,StartNodeId=1,EndNodeId=4,MaterialId="hydraulic"});
        pair.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData {MemberId=7,Ratio=-0.2});
        var pm=StructuralModel.Build(pair,level,db); var pe=new LinkageSystem();
        check("Linkage: compatible multiple actuators move together",pe.Initialize(pm,pair.Mechanisms.Linkage) && pe.TryMove(0.05),pe.Fault);
        pair.Mechanisms.Linkage.Drivers[1].Ratio=-0.1;
        pm=StructuralModel.Build(pair,level,db); pe=new LinkageSystem(); pe.Initialize(pm,pair.Mechanisms.Linkage);
        var before=(double[])pm.X.Clone();
        check("Linkage: incompatible drivers reject atomically",!pe.TryMove(0.1) && pm.X.SequenceEqual(before) && pe.Progress==0,pe.Fault);
        var locked=design.Clone(); locked.Members.Add(new BridgeMember {Id=7,StartNodeId=2,EndNodeId=3,MaterialId="steel"});
        var lm=StructuralModel.Build(locked,level,db);var le=new LinkageSystem();le.Initialize(lm,locked.Mechanisms.Linkage);
        check("Linkage: truly braced geometry cannot be shortened by force",!le.TryMove(0.1) && le.Progress==0,le.Fault);
        // Force overload restores the preceding accepted pose, including actuator lengths.
        engine.TryMove(0.01); var force=new SolveOutput(); force.EnsureSize(m.NodeCount,m.MemberCount);
        force.AxialForce[5]=db.Materials["hydraulic"].ActuatorForce*2;
        check("Linkage: overload rolls geometry and progress back",!engine.CheckForces(force) && engine.Progress==0 && Math.Abs(m.Length[5]-5)<2e-6,engine.Fault);
        var invalid=design.Mechanisms.Linkage.Clone(); invalid.Drivers[0].Ratio=-2;
        check("Linkage: stroke outside cylinder limits rejected",!new LinkageSystem().Initialize(StructuralModel.Build(design,level,db),invalid),"");
        var free=design.Clone(); free.Members.RemoveAll(v=>v.Id==4 || v.Id==5);free.Nodes.RemoveAll(n=>n.Id==5);
        free.Mechanisms.Linkage.Drivers[0].Ratio=0;
        var de=new LinkageSystem();free.Members.RemoveAll(v=>v.Id==3);
        check("Linkage: undriven degrees of freedom are not guessed",!de.Initialize(StructuralModel.Build(free,level,db),free.Mechanisms.Linkage),de.Fault);
        // Exercise the shared traffic and operation-state interface, without relying on a bascule pivot.
        var bs=new BasculeSystem();var bm=StructuralModel.Build(design,level,db);
        bs.Prepare(bm,design,db,level);bs.Initialize(Array.Empty<RoadPath>());
        var disp=new double[bm.NodeCount*2];bool cycle=true;
        for(int i=0;i<400;i++){bs.Update(0.025,true,true);bs.ApplyGeometry(disp);}
        cycle=bs.IsOpen && bs.Linkage.Progress==1;
        for(int i=0;i<400;i++){bs.Update(0.025,false,true);bs.ApplyGeometry(disp);}
        check("Linkage: operating state opens, closes and restores geometry",cycle && bs.ClosedAndLocked && disp.All(v=>Math.Abs(v)<1e-5),bs.Fault);
        // Inspect the actual player's saved geometry without modifying it or asserting an impossible motion.
        string playerPath=System.IO.Path.Combine(AppContext.BaseDirectory,"unused");
        var folder=new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while(folder!=null && !System.IO.File.Exists(System.IO.Path.Combine(folder.FullName,"project.godot"))) folder=folder.Parent;
        if(folder!=null)
        {
            playerPath=System.IO.Path.Combine(folder.FullName,"Tests/BridgeTests/Fixtures/level7_player_regression.json");
            var player=GameDatabase.DeserializeDesign(System.IO.File.ReadAllText(playerPath));
            foreach(var h in player.Members.Where(v=>db.GetMaterial(v.MaterialId).Extendable))
                player.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData{MemberId=h.Id,Ratio=-0.1});
            var actual=new LinkageSystem();var actualModel=StructuralModel.Build(player,db.GetLevel("level7"),db);
            bool ready=actual.Initialize(actualModel,player.Mechanisms.Linkage);
            bool canMove=ready && actual.TryMove(0.01);
            check("Linkage: actual fixed triangles are not stretched to fake motion",!canMove && actual.Progress==0 && actual.Fault!=null,actual.Fault);
        }
        var ed=new DesignEditor(design.Clone(),level,db);
        var originalRatio=ed.Design.Mechanisms.Linkage.Drivers[0].Ratio;
        ed.ConfigureLinkage(6,false,0.05); ed.Undo();
        check("Linkage: editing stroke is undoable",ed.Design.Mechanisms.Linkage.Drivers[0].Ratio==originalRatio,"");
        ed.ConfigureLinkage(6,true); ed.Undo();
        check("Linkage: switching driver mode is undoable",ed.Design.Mechanisms.Linkage.Drivers.Count==1,"");
        // End-of-stroke force checks run even if the controller has just transitioned to Open.
        bs.Update(0.1,true,true); bs.Update(0.1,true,true); bs.Update(1,true,true); bs.Update(4,true,true);
        var endForce=new SolveOutput();endForce.EnsureSize(bm.NodeCount,bm.MemberCount);
        endForce.AxialForce[bm.MemberIndex[6]]=db.Materials["hydraulic"].ActuatorForce*2;
        check("Linkage: final-step overload cannot leave the controller fully open",bs.CheckActuators(endForce,4) && !bs.IsOpen && bs.State==BridgeOpState.Blocked,bs.Fault);
        // A tilted assembly raises its deck while the lower bars and triangular coupler rotate together.
        var lifting=design.Clone();
        lifting.FindNode(3).Position=new Vec3d(2,2);
        lifting.FindNode(4).Position=new Vec3d(6,2);
        lifting.FindNode(5).Position=new Vec3d(4,4);
        lifting.Mechanisms.Linkage.Drivers[0].Ratio=-0.1;
        var liftModel=StructuralModel.Build(lifting,level,db);var liftEngine=new LinkageSystem();
        bool lifts=liftEngine.Initialize(liftModel,lifting.Mechanisms.Linkage);
        for(int i=1;i<=100 && lifts;i++) lifts=liftEngine.TryMove(i/100.0);
        check("Linkage: contraction raises the deck through coupled lower links",lifts && liftModel.Y[2]>2.5 && liftModel.Y[3]>2.5 && liftModel.Y[4]>4.5,liftEngine.Fault);
        // A per-actuator opt-in must not become a global mode switch.
        if(folder!=null)
        {
            var saved=GameDatabase.DeserializeDesign(System.IO.File.ReadAllText(System.IO.Path.Combine(folder.FullName,"Tests/BridgeTests/Fixtures/level7_mixed_regression.json")));
            foreach(int selected in new[]{71,73,78,79})
            {
                var mixed=saved.Clone();
                mixed.Mechanisms.Linkage.Drivers.Clear();
                mixed.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData{MemberId=selected,Ratio=-0.25});
                var ms=new BridgeSimulation(mixed,db.GetLevel("level7"),db);ms.Initialize();
                double left=0,right=0;
                int leftIndex=ms.Model.NodeIndex[34],rightIndex=ms.Model.NodeIndex[35];
                while(ms.State==SimState.Running && ms.Time<15)
                {
                    ms.Step();left=Math.Max(left,ms.Model.Y[leftIndex]);right=Math.Max(right,ms.Model.Y[rightIndex]);
                }
                check($"Mixed: enabling #{selected} preserves both central hydraulic motions",ms.Bascule.Active && left>0.1 && right>0.1,
                    $"left={left:0.000}, right={right:0.000}, fault={ms.Bascule.Fault}, note={ms.Bascule.ConfigNote}, errors={string.Join(";",ms.Bascule.Errors)}");
                if(selected==78 || selected==79)
                {
                    int k=ms.Model.MemberIndex[selected];
                    double original=Vec3d.Distance(mixed.FindNode(mixed.FindMember(selected).StartNodeId).Position,mixed.FindNode(mixed.FindMember(selected).EndNodeId).Position);
                    check($"Mixed: selected driver #{selected} itself contracts",ms.Model.Length[k]<original-0.01,$"length={ms.Model.Length[k]}, original={original}");
                }
                if(selected==71 || selected==73)
                    check($"Mixed: locked driver #{selected} is reported without disabling the bridge",ms.Bascule.Linkage==null && ms.Bascule.ConfigNote?.Contains("通用驱动") == true,ms.Bascule.ConfigNote);
                else
                    check($"Mixed: selected #{selected} keeps a local linkage and stationary supports rigid",ms.Bascule.Linkage?.Ready==true && !ms.Model.Pinned[ms.Model.MemberIndex[75]],ms.Bascule.Fault);
            }
        }
        if(folder!=null)
        {
            var combination=GameDatabase.DeserializeDesign(System.IO.File.ReadAllText(System.IO.Path.Combine(folder.FullName,"Tests/BridgeTests/Fixtures/level7_mixed_regression.json")));
            combination.Mechanisms.Linkage.Drivers=new List<LinkageDriverData>{
                new LinkageDriverData{MemberId=71,Ratio=-0.25},new LinkageDriverData{MemberId=78,Ratio=-0.25}};
            var both=new BridgeSimulation(combination,db.GetLevel("level7"),db);both.Initialize();
            double left=0,right=0;
            while(both.State==SimState.Running && both.Time<15)
            {both.Step();left=Math.Max(left,both.Model.Y[both.Model.NodeIndex[34]]);right=Math.Max(right,both.Model.Y[both.Model.NodeIndex[35]]);}
            check("Mixed: one locked selected driver does not disable a valid selected driver",
                both.Bascule.Linkage?.Ready==true && both.Bascule.Linkage.Drivers.Select(k=>both.Model.MemberIds[k]).SequenceEqual(new[]{78}) && left>0.1 && right>0.1,both.Bascule.ConfigNote);
        }
        if(folder!=null)
        {
            var isolated=GameDatabase.DeserializeDesign(System.IO.File.ReadAllText(System.IO.Path.Combine(folder.FullName,"Tests/BridgeTests/Fixtures/level7_mixed_regression.json")));
            isolated.Mechanisms.Linkage.Drivers=new List<LinkageDriverData>{new LinkageDriverData{MemberId=78,Ratio=-0.25}};
            var rollback=new BridgeSimulation(isolated,db.GetLevel("level7"),db);rollback.Initialize();
            var link=rollback.Bascule.Linkage;
            bool stepOk=link?.TryMove(0.05)==true;
            int unrelated=rollback.Model.NodeIndex[35];
            rollback.Model.Y[unrelated]=0.25;
            link?.Rollback();
            check("Mixed: rollback does not reset an independent automatic mechanism",stepOk && rollback.Model.Y[unrelated]==0.25,link?.Fault);
        }
        if(folder!=null)
        {
            var ui=GameDatabase.DeserializeDesign(System.IO.File.ReadAllText(System.IO.Path.Combine(folder.FullName,"Tests/BridgeTests/Fixtures/level7_mixed_regression.json")));
            ui.Mechanisms.Linkage.Drivers.Clear();
            var editor=new DesignEditor(ui,db.GetLevel("level7"),db);
            string lockedMessage=editor.ConfigureLinkage(71,true).Message;
            check("Mixed: enabling a blocked driver reports its actual non-motion and brace",lockedMessage.Contains("暂不动作") && lockedMessage.Contains("#52"),lockedMessage);
            editor.ConfigureLinkage(71,true);
            string activeMessage=editor.ConfigureLinkage(78,true).Message;
            check("Mixed: enabling a valid driver reports successful motion preflight",activeMessage.Contains("预检可动作"),activeMessage);
        }
        if(folder!=null)
        {
            string path=System.IO.Path.Combine(folder.FullName,"Tests/BridgeTests/Fixtures/level7_shared_hydraulics.json");
            string text=System.IO.File.ReadAllText(path);
            var shared=GameDatabase.DeserializeDesign(text);
            var expanded=HydraulicDeckJoints.Expand(shared,db);
            check("Seam: two hydraulics at a shared deck node create an independent opposite endpoint",
                expanded.FindNode(-36)!=null && expanded.FindMember(82).EndNodeId==36 && expanded.FindMember(83).EndNodeId==-36
                && expanded.FindMember(86).EndNodeId==36 && expanded.FindMember(87).StartNodeId==-36,"");
            check("Seam: expansion is idempotent and never edits the player's topology",
                HydraulicDeckJoints.Expand(expanded,db).Nodes.Count==expanded.Nodes.Count && shared.FindNode(-36)==null && shared.Mechanisms.Locks.Count==0,"");
            var frame=StructuralModel.Build(shared,db.GetLevel("level7"),db);
            int a=frame.NodeIndex[36],c=frame.NodeIndex[-36];
            check("Seam: the shared deck starts locked without removing any member",frame.Alias[c]==a && frame.MemberCount==shared.Members.Count,"");
            var controller=new BridgeSimulation(shared,db.GetLevel("level7"),db);controller.Initialize();
            double shortenLeft=0,shortenRight=0,gap=0;
            double left0=controller.Model.Length[controller.Model.MemberIndex[71]],right0=controller.Model.Length[controller.Model.MemberIndex[73]];
            while(controller.State==SimState.Running && controller.Time<15)
            {
                controller.Step();var model=controller.Model;
                shortenLeft=Math.Max(shortenLeft,left0-model.Length[model.MemberIndex[71]]);
                shortenRight=Math.Max(shortenRight,right0-model.Length[model.MemberIndex[73]]);
                gap=Math.Max(gap,Math.Sqrt(Math.Pow(model.X[a]-model.X[c],2)+Math.Pow(model.Y[a]-model.Y[c],2)));
            }
            check("Seam: opposite supports do not lock the two configured outer actuators",
                controller.Bascule.Linkage?.Ready==true && shortenLeft>0.05 && shortenRight>0.05 && gap>0.05,
                $"left={shortenLeft:0.000}, right={shortenRight:0.000}, gap={gap:0.000}, fault={controller.Bascule.Fault}, note={controller.Bascule.ConfigNote}");
            check("Seam: normal opening is not counted as material fracture",controller.BrokenCount==0,controller.Result.FailureMessage);
            for(int i=0;i<300;i++){controller.Bascule.Update(0.05,false,true);controller.Bascule.ApplyGeometry(null);}
            check("Seam: connected bridge closes and locks the independent endpoints again",
                controller.Bascule.ClosedAndLocked && controller.Model.Alias[c]==a &&
                Math.Abs(controller.Model.X[a]-controller.Model.X[c])+Math.Abs(controller.Model.Y[a]-controller.Model.Y[c])<1e-5,controller.Bascule.Fault);
            var leftOnly=shared.Clone();leftOnly.Mechanisms.Linkage.Drivers.RemoveAll(d=>d.MemberId!=71);
            var leftModel=StructuralModel.Build(leftOnly,db.GetLevel("level7"),db);
            var leftController=new BasculeSystem();leftController.Prepare(leftModel,leftOnly,db,db.GetLevel("level7"));
            check("Seam: opposite lower support is outside a single driver's motion constraints",
                leftController.Linkage?.Ready==true && !leftController.Linkage.IsMovingNode(leftModel.NodeIndex[21]),leftController.ConfigurationWarning);
            var sameSide=shared.Clone();sameSide.Members.RemoveAll(m=>m.Id==83);
            var duplicate=sameSide.FindMember(82).Clone();duplicate.Id=999;sameSide.Members.Add(duplicate);
            var duplicateExpanded=HydraulicDeckJoints.Expand(sameSide,db);
            check("Seam: two co-located cylinders also establish a seam without moving the opposite brace",
                duplicateExpanded.FindNode(-36)!=null && duplicateExpanded.FindMember(999).EndNodeId==36 && duplicateExpanded.FindMember(87).StartNodeId==-36,"");
            check("Seam: the regression fixture is unchanged",System.IO.File.ReadAllText(path)==text,"");
            // One cylinder is not a two-cylinder automatic release request.
            var single=shared.Clone();single.Members.RemoveAll(m=>m.Id==83);
            check("Seam: a single hydraulic does not silently split the junction",HydraulicDeckJoints.Expand(single,db).FindNode(-36)==null,"");
            // A fixed anchor must never become a releasable deck joint.
            var fixedSeam=shared.Clone();fixedSeam.FindNode(36).IsAnchor=true;
            check("Seam: fixed support nodes cannot be split by the hydraulic rule",HydraulicDeckJoints.Expand(fixedSeam,db).FindNode(-36)==null,"");
        }
        var round=GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(design));
        check("Linkage: settings serialize and clone independently",round.Mechanisms.Linkage.Drivers.Single().Ratio==design.Mechanisms.Linkage.Drivers.Single().Ratio && !ReferenceEquals(round.Mechanisms.Linkage.Drivers,design.Mechanisms.Linkage.Drivers),"");
        var old=GameDatabase.DeserializeDesign("{\"Version\":1,\"Nodes\":[],\"Members\":[]}");
        check("Linkage: legacy saves retain their existing behavior",old.Mechanisms.Linkage.Drivers.Count==0,"");
    }
}
