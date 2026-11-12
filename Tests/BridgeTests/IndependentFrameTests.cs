using System;
using System.IO;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;

internal static class IndependentFrameTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        var d=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Tests/BridgeTests/Fixtures/level7_frame_independent.json")));
        d.Mechanisms.Linkage.Drivers.Clear();d.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData{MemberId=235,Ratio=-0.5});
        string original=GameDatabase.SerializeDesign(d);var lv=db.GetLevel("level7");
        var m=StructuralModel.Build(d,lv,db);var engine=new LinkageSystem();
        bool ok=engine.Initialize(m,d.Mechanisms.Linkage) && engine.RestrictToDrivenMotion();
        check("Frames: player's #235 initializes independently of a single-pivot leaf plan",ok,engine.Fault);
        check("Frames: passive parent is discovered without an actuator-specific body",m.RigidBodies.Any(b=>new[]{81,58,76,78}.All(id=>b.nodes.Contains(m.NodeIndex[id]))),"");
        var rest=(double[])m.Length.Clone();double error=0,jump=0;
        for(int step=0;step<3000 && ok && engine.Progress<1;step++)
        {
            var x=(double[])m.X.Clone();var y=(double[])m.Y.Clone();
            ok=engine.TryMoveLimited(Math.Min(1,engine.Progress+0.0025),0.05);
            for(int k=0;k<m.MemberCount;k++)if(k!=m.MemberIndex[235])error=Math.Max(error,Math.Abs(m.Length[k]-rest[k]));
            for(int i=0;i<m.NodeCount;i++)jump=Math.Max(jump,Math.Sqrt(Math.Pow(m.X[i]-x[i],2)+Math.Pow(m.Y[i]-y[i],2)));
        }
        check("Frames: #235 completes half-length contraction and lifts the center",ok && engine.Progress==1 && Math.Abs(m.Length[m.MemberIndex[235]]-rest[m.MemberIndex[235]]*0.5)<1e-6 && m.Y[m.NodeIndex[59]]>2.7,engine.Fault);
        check("Frames: all passive cylinders and beams keep length without node jumps",error<2e-6 && jump<=0.050001,$"error={error}, jump={jump}");
        for(int step=0;step<3000 && ok && engine.Progress>0;step++)ok=engine.TryMoveLimited(Math.Max(0,engine.Progress-0.0025),0.05);
        check("Frames: the same assembly returns to its built position",ok && engine.Progress==0 && Enumerable.Range(0,m.NodeCount).All(i=>Math.Abs(m.X[i]-m.X0[i])+Math.Abs(m.Y[i]-m.Y0[i])<1e-5),engine.Fault);
        var controller=new BasculeSystem();m=StructuralModel.Build(d,lv,db);controller.Prepare(m,d,db,lv);
        check("Frames: runtime and editor agree that #235 is available",controller.Linkage?.Ready==true && controller.Linkage.Drivers.Count==1 && new DesignEditor(d.Clone(),lv,db).DescribeActuator(235).Contains("预检可动作"),controller.ConfigurationWarning);
        var pair=d.Clone();pair.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData{MemberId=234,Ratio=-0.5});
        m=StructuralModel.Build(pair,lv,db);controller=new BasculeSystem();controller.Prepare(m,pair,db,lv);
        check("Frames: failed extra driver does not erase the valid frame on fallback",controller.Linkage?.Ready==true && controller.Linkage.Drivers.Contains(m.MemberIndex[235]) && !controller.Linkage.Drivers.Contains(m.MemberIndex[234]) && m.RigidBodies.Any(b=>b.nodes.Contains(m.NodeIndex[58]) && b.nodes.Contains(m.NodeIndex[81])),controller.ConfigurationWarning);
        check("Frames: planning and motion never modify the saved design",original==GameDatabase.SerializeDesign(d),"");

        var simple=new LevelDefinition{Id="diagnostics"};
        simple.Anchors.Add(new AnchorDefinition{Id="A",Position=new Vec3d(0,0),SupportType=SupportType.Pinned});
        simple.Anchors.Add(new AnchorDefinition{Id="B",Position=new Vec3d(4,0),SupportType=SupportType.Pinned});
        BridgeDesign New(double y){var value=BridgeDesign.CreateForLevel(simple);value.Nodes.Add(new BridgeNode{Id=3,Position=new Vec3d(2,y)});value.Members.Add(new BridgeMember{Id=1,StartNodeId=1,EndNodeId=3,MaterialId="steel"});value.Members.Add(new BridgeMember{Id=2,StartNodeId=2,EndNodeId=3,MaterialId="hydraulic"});value.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData{MemberId=2,Ratio=-0.5});return value;}
        var dead=New(0);engine=new LinkageSystem();m=StructuralModel.Build(dead,simple,db);
        check("Diagnostics: collinear assembly is a dead point with the actual free node highlighted",!engine.Initialize(m,dead.Mechanisms.Linkage) && engine.FaultKind==LinkageFaultKind.DeadPoint && engine.FaultNodes.Contains(3) && engine.FaultMembers.SetEquals(new[]{1,2}),engine.Fault);
        var free=New(2);free.Members[1].StartNodeId=3;free.Members[1].EndNodeId=4;free.Nodes.Add(new BridgeNode{Id=4,Position=new Vec3d(5,2)});
        engine=new LinkageSystem();m=StructuralModel.Build(free,simple,db);
        check("Diagnostics: a structurally free linkage is underconstrained, not a dead point",!engine.Initialize(m,free.Mechanisms.Linkage) && engine.FaultKind==LinkageFaultKind.Underconstrained && engine.FaultNodes.Contains(4),engine.Fault);
        var conflict=New(2);conflict.Members.Add(new BridgeMember{Id=3,StartNodeId=2,EndNodeId=3,MaterialId="steel"});
        engine=new LinkageSystem();m=StructuralModel.Build(conflict,simple,db);ok=engine.Initialize(m,conflict.Mechanisms.Linkage);var before=(double[])m.X.Clone();
        check("Diagnostics: incompatible lengths identify conflicting members and preserve the pose",ok && !engine.TryMove(0.1) && engine.FaultKind==LinkageFaultKind.ConstraintConflict && engine.FaultMembers.Contains(2) && engine.FaultMembers.Contains(3) && m.X.SequenceEqual(before) && engine.Progress==0,engine.Fault);
        var targets=DiagnosticTargets.Parse(engine.Fault,2);
        check("Diagnostics: actual affected members and nodes reach scene highlighting",targets.Kind==MechanismDiagnosticKind.ConstraintConflict && targets.BlockingMembers.Contains(3) && targets.Members.Contains(3) && targets.Nodes.Contains(3),engine.Fault);
        var deadTargets=DiagnosticTargets.Parse("几何死点：节点 #3；构件 #1；构件 #2",2);
        var freeTargets=DiagnosticTargets.Parse("机构欠约束：节点 #4；构件 #2",2);
        check("Diagnostics: dead-point and underconstrained colors preserve distinct categories",deadTargets.Kind==MechanismDiagnosticKind.DeadPoint && freeTargets.Kind==MechanismDiagnosticKind.Underconstrained && !deadTargets.BlockingMembers.Any() && !freeTargets.BlockingMembers.Any(),"");
    }
}
