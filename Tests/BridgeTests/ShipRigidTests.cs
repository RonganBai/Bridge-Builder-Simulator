using System;
using System.IO;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.Building;

internal static class ShipRigidTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        var ev=new TrafficEvent{Width=4,Height=4.5,Length=12};
        check("Ship: empty space above the bow is not a full-height collision box",!ShipGeometry.Intersects(ev,0,0,-1,4.4,1,4.4,4,0.1,0.1),"");
        check("Ship: the rendered chimney collides at its actual longitudinal position",ShipGeometry.Intersects(ev,0,0,-1,4.4,1,4.4,-3.6,0.1,0.1),"");
        check("Ship: hull below the waterline is part of collision geometry",ShipGeometry.Intersects(ev,0,0,-1,-0.4,1,-0.4,0,0.1,0.1),"");
        check("Ship: rounded bow excludes empty outer corners",!ShipGeometry.Intersects(ev,0,0,1.6,1.7,1.9,1.7,5.8,0.04,0.04),"");
        check("Ship: rounded bow center still collides",ShipGeometry.Intersects(ev,0,0,-0.2,1.7,0.2,1.7,5.8,0.04,0.04),"");
        var reference=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/ReferenceDesigns/level7_reference.json")));
        var sim=new BridgeSimulation(reference,db.GetLevel("level7"),db);sim.Initialize();
        bool speed=true,early=false,waiting=false;
        while(sim.State==SimState.Running)
        {
            var previous=sim.Ships.Select(s=>(s.Z,s.Spawned,s.Done)).ToArray();sim.Step();
            for(int i=0;i<sim.Ships.Count;i++)
            {
                var s=sim.Ships[i];waiting|=s.Waiting;
                if(previous[i].Spawned && !previous[i].Done)speed&=Math.Abs(s.Z-previous[i].Z-s.Ev.Speed*BridgeSimulation.FixedDt)<1e-8;
                early|=sim.Bascule.TrafficStopped && sim.Time<s.WindowStart-sim.Bascule.RequiredOpeningTime;
            }
        }
        check("Ship: reference bridge schedules ahead while ships maintain constant speed",speed && early && !waiting && sim.Result.IsSuccess,sim.Result.FailureMessage);
        var lv=new LevelDefinition{Id="rigid",Budget=100000};
        lv.Terrain=new TerrainConfig{RiverWidth=4,BuildMinY=-5,BuildMaxY=10,WaterLevel=-5};
        lv.Anchors.Add(new AnchorDefinition{Id="A",Position=new Vec3d(0,0),SupportType=SupportType.Pinned});
        lv.Anchors.Add(new AnchorDefinition{Id="B",Position=new Vec3d(4,0),SupportType=SupportType.Pinned});
        lv.AllowedMaterials.AddRange(new[]{"steel","road","hydraulic"});
        var d=BridgeDesign.CreateForLevel(lv);
        d.Nodes.Add(new BridgeNode{Id=3,Position=new Vec3d(0,3)});d.Nodes.Add(new BridgeNode{Id=4,Position=new Vec3d(4,3)});d.Nodes.Add(new BridgeNode{Id=5,Position=new Vec3d(2,5)});d.NextNodeId=6;
        void Add(int id,int a,int b,string mat)=>d.Members.Add(new BridgeMember{Id=id,StartNodeId=a,EndNodeId=b,MaterialId=mat});
        Add(1,1,3,"steel");Add(2,3,4,"road");Add(3,4,2,"steel");Add(4,3,5,"steel");Add(5,4,5,"steel");Add(6,1,4,"hydraulic");d.NextMemberId=7;
        d.Mechanisms.Linkage.Drivers.Add(new LinkageDriverData{MemberId=6,Ratio=-0.2});
        var editor=new DesignEditor(d,lv,db);var split=editor.SplitMember(2);
        check("Rigid: subdivision records a non-folding joint and explains it",split.Ok && editor.Design.FindNode(split.CreatedId).IsRigidSplit && split.Message.Contains("刚性"),split.Message);
        var m=StructuralModel.Build(editor.Design,lv,db);var engine=new LinkageSystem();bool moved=engine.Initialize(m,editor.Design.Mechanisms.Linkage) && engine.RestrictToDrivenMotion();
        double error=0;
        for(int step=1;step<=100 && moved;step++)
        {
            moved=engine.TryMove(step/100.0);int n=m.NodeIndex[split.CreatedId],a=m.NodeIndex[3],b=m.NodeIndex[4];
            error=Math.Max(error,Math.Abs(m.X[n]-(m.X[a]+m.X[b])/2)+Math.Abs(m.Y[n]-(m.Y[a]+m.Y[b])/2));
        }
        check("Rigid: subdivided member follows the linkage without introducing a hinge",moved && error<1e-6,engine.Fault);
        int half=m.MemberIndex[7];m.Broken[half]=true;var before=(double[])m.X.Clone();
        check("Rigid: a broken half cannot leave an invisible rigid follower",!engine.TryMove(0.9) && m.X.SequenceEqual(before),engine.Fault);
        engine.Deactivate();check("Rigid: deactivation restores original end releases",m.ReleaseA.All(v=>!v) && m.ReleaseB.All(v=>!v),"");
        var loaded=GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(editor.Design));
        check("Rigid: node type persists through cloning and save/load",loaded.FindNode(split.CreatedId).IsRigidSplit && editor.Design.Clone().FindNode(split.CreatedId).IsRigidSplit,"");
        check("Rigid: independent dragging cannot fold the original member",!editor.MoveNode(split.CreatedId,2,4).Ok,"");
        editor.Undo();check("Rigid: subdivision is one undo step",editor.Design.FindNode(split.CreatedId)==null,"");
        var saved=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Tests/BridgeTests/Fixtures/level7_opposite_driver.json")));
        var plan=BasculePlanner.Plan(saved,db,db.GetLevel("level7"));
        check("Hydraulics: bank-supported main driver wins over internal sub-leaf actuators",plan.GroupOf(150)!=null && !plan.Rejected.ContainsKey(150),string.Join(";",plan.Rejected.Values));
        check("Hydraulics: opposite hydraulics are never diagnosed as a rigid brace",plan.Rejected.Values.All(v=>!v.Contains("（液压杆）把活动段锁死")),string.Join(";",plan.Rejected.Values));
    }
}
