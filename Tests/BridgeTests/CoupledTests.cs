using System;
using System.IO;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.Building;

internal static class CoupledTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        var d=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Tests/BridgeTests/Fixtures/level7_joint_driver.json")));
        d=CrossBeamFixtures.SplitBlocking(d,db.GetLevel("level7"),db);
        int[] ids={150,152,155,156};
        foreach(int id in ids)d.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData{MemberId=id,Ratio=-0.5});
        string original=GameDatabase.SerializeDesign(d);var level=db.GetLevel("level7");
        var sim=new BridgeSimulation(d,level,db);sim.Initialize();
        check("Coupled: both bank drivers and moving-end internal drivers share one solver",sim.Bascule.Linkage?.Ready==true &&
            ids.All(id=>sim.Bascule.Linkage.Drivers.Contains(sim.Model.MemberIndex[id])),sim.Bascule.Fault);
        var rest=(double[])sim.Model.Length.Clone();var minimum=(double[])rest.Clone();double passiveError=0,peak=0;bool bothEnds=false,opened=false;
        for(int frame=0;frame<6000 && sim.State==SimState.Running;frame++)
        {
            sim.Step();opened|=sim.Bascule.IsOpen;
            peak=Math.Max(peak,sim.Bascule.Linkage.Progress);
            int k=sim.Model.MemberIndex[155],a=sim.Model.A[k],b=sim.Model.B[k];
            bothEnds|=Math.Abs(sim.Model.X[a]-sim.Model.X0[a])+Math.Abs(sim.Model.Y[a]-sim.Model.Y0[a])>0.05 &&
                Math.Abs(sim.Model.X[b]-sim.Model.X0[b])+Math.Abs(sim.Model.Y[b]-sim.Model.Y0[b])>0.05;
            for(int i=0;i<sim.Model.MemberCount;i++)
            {
                minimum[i]=Math.Min(minimum[i],sim.Model.Length[i]);
                if(!sim.Model.Mat[i].Extendable)passiveError=Math.Max(passiveError,Math.Abs(sim.Model.Length[i]-rest[i]));
            }
        }
        check("Coupled: all four cylinders follow the same commanded progress without faking unreachable travel",peak>0.5 && ids.All(id=>Math.Abs(minimum[sim.Model.MemberIndex[id]]-(1-0.5*peak)*rest[sim.Model.MemberIndex[id]])<2e-6),sim.Bascule.Fault);
        check("Coupled: an internal cylinder can contract while both endpoints move",bothEnds,"");
        check("Coupled: internal supports retain length and are never deleted or fractured",passiveError<2e-6 && sim.BrokenCount==0 && !sim.Model.Broken[sim.Model.MemberIndex[104]],$"error={passiveError}");
        check("Coupled: real traffic passes and bridge returns to its locked geometry",!opened && peak<1 && sim.Result.IsSuccess && sim.Bascule.ClosedAndLocked &&
            Enumerable.Range(0,sim.Model.NodeCount).All(i=>Math.Abs(sim.Model.X[i]-sim.Model.X0[i])+Math.Abs(sim.Model.Y[i]-sim.Model.Y0[i])<1e-5),sim.Result.FailureMessage);
        var editor=new DesignEditor(d.Clone(),level,db);string description=editor.DescribeActuator(155);
        check("Coupled: diagnostic uses runtime coordination instead of a false brace deletion advice",description.Contains("预检可动作") && !description.Contains("锁死") && !description.Contains("删除"),description);
        editor.Design.Mechanisms.Linkage.Drivers.Clear();
        editor.SetHydraulicDirection(155,false);
        var selected=new BridgeSimulation(editor.Design,level,db);selected.Initialize();
        check("Coupled: selecting one cylinder never recruits unselected supporting drivers",selected.Bascule.Linkage?.Ready==true &&
            selected.Bascule.Linkage.Drivers.Count==1 && selected.Bascule.Linkage.Drivers.Contains(selected.Model.MemberIndex[155]),selected.Bascule.ConfigurationWarning);
        check("Coupled: automatic coordination never edits the saved design",original==GameDatabase.SerializeDesign(d),"");
        var lv=new LevelDefinition{Id="serial"};
        lv.Anchors.Add(new AnchorDefinition{Id="O",Position=new Vec3d(0,0),SupportType=SupportType.Pinned});
        lv.Anchors.Add(new AnchorDefinition{Id="F",Position=new Vec3d(-3,3),SupportType=SupportType.Pinned});
        var serial=BridgeDesign.CreateForLevel(lv);
        serial.Nodes.Add(new BridgeNode{Id=3,Position=new Vec3d(3,0)});serial.Nodes.Add(new BridgeNode{Id=4,Position=new Vec3d(0,3)});serial.Nodes.Add(new BridgeNode{Id=5,Position=new Vec3d(6,0)});
        void Add(int id,int a,int b,string material)=>serial.Members.Add(new BridgeMember{Id=id,StartNodeId=a,EndNodeId=b,MaterialId=material});
        Add(1,1,3,"steel");Add(2,3,4,"steel");Add(3,4,1,"steel");Add(4,3,5,"road");Add(5,2,3,"hydraulic");Add(6,4,5,"hydraulic");
        var sm=StructuralModel.Build(serial,lv,db);sm.RigidBodies.Add((new[]{0,2,3},0,new[]{0,1,2}));
        var settings=new LinkageData();settings.Drivers.Add(new LinkageDriverData{MemberId=5,Ratio=-0.5});settings.Drivers.Add(new LinkageDriverData{MemberId=6,Ratio=-0.5});
        var solver=new LinkageSystem();bool success=solver.Initialize(sm,settings) && solver.RestrictToDrivenMotion();
        for(int frame=0;frame<3000 && success && solver.Progress<1;frame++)success=solver.TryMoveLimited(Math.Min(1,solver.Progress+0.002),0.03);
        check("Coupled: a feasible serial moving-end mechanism completes both fifty-percent strokes",success && solver.Progress==1 && Math.Abs(sm.Length[4]-Math.Sqrt(45)*0.5)<1e-6 && Math.Abs(sm.Length[5]-Math.Sqrt(45)*0.5)<1e-6,solver.Fault);
        for(int frame=0;frame<3000 && success && solver.Progress>0;frame++)success=solver.TryMoveLimited(Math.Max(0,solver.Progress-0.002),0.03);
        check("Coupled: serial moving-end mechanism returns on the same assembly branch",success && solver.Progress==0 && Enumerable.Range(0,sm.NodeCount).All(i=>Math.Abs(sm.X[i]-sm.X0[i])+Math.Abs(sm.Y[i]-sm.Y0[i])<1e-5),solver.Fault);
        sm.Broken[0]=true;var previous=(double[])sm.X.Clone();
        check("Coupled: fractured parent cannot be replaced by an invisible rigid body",!solver.TryMove(0.01) && sm.X.SequenceEqual(previous),solver.Fault);
        editor.SetHydraulicDirection(155,true);var directed=CoupledHydraulics.Find(HydraulicDeckJoints.Expand(editor.Design,db),db,level);
        check("Coupled: explicit selection preserves an explicitly selected extension direction",directed.Drivers.Single(driver=>driver.MemberId==155).Ratio==0.5,"");
    }
}
