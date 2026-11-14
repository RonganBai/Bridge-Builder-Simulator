using System;
using System.IO;
using System.Linq;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

// 验收测试 A~H（无界面）。运行：dotnet run --project Tests/BridgeTests
// 返回码 0 表示全部通过。
internal static class Program
{
    private static int _fail;
    private static string _root;

    private static int Main(string[] args)
    {
        if(args.Contains("--ship-catalog-only"))
        {
            foreach(var type in ShipCatalog.Types.Where(t=>ExtendedShipGeometry.Supports(t.Id)))
            {
                var e=ShipCatalog.Create(type.Id,10);
                var boxes=ShipGeometry.Parts(e).ToArray();
                var solids=ShipGeometry.Solids(e).ToArray();
                bool valid=boxes.Length>5 && solids.Length>=2 && boxes.All(b=>
                    double.IsFinite(b.X+b.Y+b.Z+b.Width+b.Height+b.Length) && b.Width>0 && b.Height>0 && b.Length>0 &&
                    Math.Abs(b.X)+b.Width/2<=e.Width/2+.05 && Math.Abs(b.Z)+b.Length/2<=e.Length/2+.05 && b.Y+b.Height/2<=e.Height+.1);
                var sample=boxes[0];
                bool collision=ShipGeometry.Intersects(e,0,0,10+sample.X-.03,sample.Y,10+sample.X+.03,sample.Y,sample.Z,.04,.04);
                Check("Ship geometry: "+type.Id,valid && collision,$"parts={boxes.Length}, solids={solids.Length}, collision={collision}");
            }
            return _fail==0?0:1;
        }
        _root = FindRoot();
        var db = GameDatabase.FromJson(
            File.ReadAllText(Path.Combine(_root, "Data/Materials/materials.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Vehicles/vehicles.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Levels/levels.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Tunnels/tunnels.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Environment/weather.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Scoring/scoring.json")));
        Console.WriteLine($"数据加载：{db.Materials.Count} 种材料，{db.Vehicles.Count} 种车辆，{db.Levels.Count} 个关卡");
        if(args.Contains("--mechanical-only")){MechanicalLiftTests.Run(db,(n,ok,d)=>Check(n,ok,d));return _fail==0?0:1;}
        if(args.Contains("--custom-play"))
        {
            var lv=LevelEditing.ReadCustomLevel(File.ReadAllText(args[1]),db);
            var ed=new DesignEditor(args.Length>2?GameDatabase.DeserializeDesign(File.ReadAllText(args[2])):null,lv,db);
            Console.WriteLine("CUSTOM: "+lv.Id+" "+ed.Design.Members.Count);
            var sim=new BridgeSimulation(ed.Design,lv,db);sim.Initialize();
            for(int i=0;i<24000 && sim.State==SimState.Running;i++)sim.Step();
            Console.WriteLine("RESULT: "+sim.Result.IsSuccess+" "+sim.Result.FailureReason+" "+sim.Result.FailureMessage);
            foreach(var ev in sim.Events.Take(12))Console.WriteLine(ev.Time+" "+ev.MemberId+" "+ev.Text);
            return 0;
        }
        if(args.Contains("--gen-tube-refs")){TunnelStructureTests.Generate(db,_root,args.Contains("--write"));return 0;}
        if(args.Contains("--road-layout-only")){RoadLayoutTests.Run(db,(n,ok,d)=>Check(n,ok,d));return _fail==0?0:1;}
        if(args.Contains("--custom-lift-only")){CustomLiftTests.Run(db,(n,ok,d)=>Check(n,ok,d));return _fail==0?0:1;}
        if(args.Contains("--lift-challenge-only")){LiftChallengeTests.Run(db,_root,(n,ok,d)=>Check(n,ok,d));return _fail==0?0:1;}
        if(args.Contains("--strategy-only")){StrategyTests.Run(db,_root,(n,ok,d)=>Check(n,ok,d));return _fail==0?0:1;}
        if(args.Contains("--lift-sweep")){LiftStructureTests.Sweep(db);return 0;}
        if(args.Contains("--lift-only")){LiftStructureTests.Run(db,(n,ok,d)=>Check(n,ok,d));return _fail==0?0:1;}
        if(args.Contains("--tube-only")){TunnelStructureTests.Run(db,_root,(n,ok,d)=>Check(n,ok,d));return _fail==0?0:1;}
        if(args.Contains("--live-info-only")){LiveInfoTests.Run(db,(n,ok,d)=>Check(n,ok,d));return _fail==0?0:1;}
        if(args.Contains("--lift-batch-only")){LiftBatchTests.Run(db,_root,Check);LiftQueueTests.Run(db,_root,Check);LiftAlignTests.Run(db,_root,Check);return _fail==0?0:1;}
        // PLAYSAVE=<level>:<design.json>: run a player's save and print where it goes wrong (diagnostics only)
        if (Environment.GetEnvironmentVariable("PLAYSAVE") is { Length: > 0 } playArg)
        {
            int c = playArg.IndexOf(':'); var plv = db.GetLevel(playArg[..c]);
            var pd = GameDatabase.DeserializeDesign(File.ReadAllText(playArg[(c + 1)..]));
            var psim = new BridgeSimulation(new DesignEditor(pd, plv, db).Design, plv, db);
            psim.Initialize();
            Console.WriteLine($"PLAYSAVE tunnelPath {(psim.TunnelPath != null)} geo {(psim.TunnelGeo != null)} complete {(psim.TunnelGeo != null && TunnelRules.Complete(psim.TunnelGeo, plv.Terrain))}");
            for (int k = 0; k < 60 * 400 && psim.State == SimState.Running; k++)
            {
                psim.Step();
                if (k % 600 == 0) Console.WriteLine($"PLAYSAVE t={psim.Time:0} " + string.Join(" | ", psim.Vehicles.Select(v => $"{v.Def.Id} s={v.FrontS:0.0} lane={v.Lane} lift={v.OnLift} wait={v.WaitingAtPortal}")) + " || " + string.Join(" ", psim.Lifts.Select(l => $"A={l.A:0.0} pos={l.Pos:0.00} rider={l.Rider?.Def.Id} riding={l.Riding}")));
            }
            var pr = psim.Result;
            Console.WriteLine($"PLAYSAVE ok {pr.IsSuccess} {pr.FailureReason} {pr.FailureMessage} | {string.Join(", ", pr.VehicleResults.Select(v => v.Name + (v.Passed ? "+" : "-")))}");
            return 0;
        }

        foreach (string id in new[] { "tunnel_immersed", "tunnel_shield", "tunnel_floating" })
        {
            var material = db.GetMaterial(id); var type = db.TunnelConfig.Type(material);
            var grades = TunnelMaterialRatings.For(db.TunnelConfig, material);
            Check($"Tunnel ratings: {id} exposes every grade and valid five-cell values", grades.Count == type.Grades.Count && grades.All(g => g.Stats.All(s => s.Cells >= 0 && s.Cells <= 5 && double.IsFinite(s.Value))));
            Check($"Tunnel ratings: {id} includes supports only when supported", grades.All(g => g.Stats.Count == (type.HasSupports ? 5 : 4)));
            Check($"Tunnel ratings: {id} pressure is kPa and joint tolerance is degrees", grades.All(g => Math.Abs(g.Stats[2].Value - type.Grades[g.Id].PressureCapacity / 1000) < 1e-8 && Math.Abs(g.Stats[3].Value - type.Grades[g.Id].JointRotation * 180 / Math.PI) < 1e-8));
            var ordered = grades.OrderBy(g => g.Stats[2].Value).ToList();
            Check($"Tunnel ratings: {id} increasing capacity never loses cells", ordered.Zip(ordered.Skip(1)).All(pair => pair.First.Stats[2].Cells <= pair.Second.Stats[2].Cells));
        }
        Check("Tunnel ratings: absent configuration is safe", TunnelMaterialRatings.For(null, db.GetMaterial("tunnel_immersed")).Count == 0);
        if (args.Contains("--tunnel-guide-only")) return _fail == 0 ? 0 : 1;
        TunnelAccessTests.Run(db,_root,Check);
        QuakeTests.Run(db,_root,Check);
        if(args.Contains("--tunnel-access-only"))return _fail==0?0:1;
        LiftBatchTests.Run(db,_root,Check);
        LiftQueueTests.Run(db,_root,Check);LiftAlignTests.Run(db,_root,Check);
        LevelEditTests.Run(db,(n,ok,d)=>Check(n,ok,d));
        TunnelStructureTests.Run(db,_root,(n,ok,d)=>Check(n,ok,d));
        LiftStructureTests.Run(db,(n,ok,d)=>Check(n,ok,d));
        StrategyTests.Run(db,_root,(n,ok,d)=>Check(n,ok,d));
        LiftChallengeTests.Run(db,_root,(n,ok,d)=>Check(n,ok,d));
        MechanicalLiftTests.Run(db,(n,ok,d)=>Check(n,ok,d));
        CustomLiftTests.Run(db,(n,ok,d)=>Check(n,ok,d));

        RoadLayoutTests.Run(db,(n,ok,d)=>Check(n,ok,d));
        LiveInfoTests.Run(db,(n,ok,d)=>Check(n,ok,d));
        var diagnostic=DiagnosticTargets.Parse("液压杆 #71 暂不动作：构件 #31 附近约束冲突。\n构件 #52（钢梁）把活动段锁死：转轴节点 #31；节点 #999999999999999999。",71);
        Check("Diagnostic: blocker, source and pivot have separate identities",diagnostic.SourceMember==71 && diagnostic.BlockingMembers.SetEquals(new[]{52}) && diagnostic.Nodes.Contains(31) && diagnostic.Members.Contains(31) && !diagnostic.Members.Contains(71));
        var nodeOnly=DiagnosticTargets.Parse("转轴节点 #31，开启角 35 度，行程 25%");
        Check("Diagnostic: node numbers and engineering values do not select members",nodeOnly.Nodes.SetEquals(new[]{31}) && nodeOnly.Members.Count==0);
        Check("Diagnostic: empty message is safe",!DiagnosticTargets.Parse(null).HasTargets);
        Check("Controls: hydraulic stroke envelope is below 100% both ways (95%)",db.Materials["hydraulic"].RetractRatio==LinkageDriverData.MaxStroke && db.Materials["hydraulic"].ExtendRatio==LinkageDriverData.MaxStroke && LinkageDriverData.MaxStroke<1);
        var gridEditor=new DesignEditor(null,db.GetLevel("level1"),db);
        int gridA=gridEditor.AddNode(2,2).CreatedId;
        int gridB=gridEditor.AddNode(2.25,2).CreatedId;
        Check("Grid: quarter-meter nodes and members can be built",gridA>0 && gridB>0 && gridEditor.AddMember(gridA,gridB,"wood").Ok);
        Check("Grid: snapping supports positive and negative quarter meters",DesignEditor.Snap(0.26)==0.25 && DesignEditor.Snap(-0.26)==-0.25 && DesignEditor.Snap(0.51)==0.5);
        Check("Grid: dragging can ignore its own node during snapping",gridEditor.NodeAt(2,2,0.1,gridA)==null && gridEditor.NodeAt(2.25,2,0.1,gridA)?.Id==gridB);
        Check("Grid: quarter-meter moves commit correctly",gridEditor.MoveNode(gridB,2.5,2.25).Ok && gridEditor.Design.FindNode(gridB).Position.Y==2.25);
        var directionLevel=new LevelDefinition{Id="direction"};
        directionLevel.Anchors=new() {new AnchorDefinition{Id="A",Position=new Vec3d(0,0),SupportType=SupportType.Pinned},new AnchorDefinition{Id="B",Position=new Vec3d(4,0),SupportType=SupportType.RollerX}};
        var directionDesign=BridgeDesign.CreateForLevel(directionLevel);
        directionDesign.Members.Add(new BridgeMember{Id=1,StartNodeId=1,EndNodeId=2,MaterialId="hydraulic"});
        foreach(bool extend in new[]{false,true})
        {
            var settings=new LinkageData();settings.Drivers.Add(new LinkageDriverData{MemberId=1,Ratio=extend?0.5:-0.5});
            var model=StructuralModel.Build(directionDesign,directionLevel,db);var drive=new LinkageSystem();
            bool ok=drive.Initialize(model,settings);
            for(int step=1;step<=100 && ok;step++) ok=drive.TryMove(step/100.0);
            Check($"Controls: {(extend?"extension":"contraction")} reaches exactly fifty percent",ok && System.Math.Abs(model.Length[0]-(extend?6:2))<1e-6,drive.Fault);
        }
        var directionEditor=new DesignEditor(directionDesign,directionLevel,db);
        directionEditor.SetHydraulicDirection(1,true);
        Check("Controls: extension setting is positive fifty percent",directionEditor.Design.Mechanisms.Linkage.Drivers.Single().Ratio==0.5);
        directionEditor.SetHydraulicDirection(1,false);
        Check("Controls: contraction setting is negative fifty percent",directionEditor.Design.Mechanisms.Linkage.Drivers.Single().Ratio==-0.5);
        directionEditor.Undo();
        Check("Controls: direction change is undoable",directionEditor.Design.Mechanisms.Linkage.Drivers.Single().Ratio==0.5);
        var migrated=GameDatabase.DeserializeDesign("{\"Version\":3,\"Mechanisms\":{\"Linkage\":{\"Drivers\":[{\"MemberId\":1,\"Ratio\":-0.25},{\"MemberId\":2,\"Ratio\":0.2}]}}}");
        // Saved strokes are kept (only clamped into 5%..95%); a zero / out-of-range value is brought back into range
        Check("Controls: saved stroke and direction are kept when loading",migrated.Mechanisms.Linkage.Drivers[0].Ratio==-0.25 && migrated.Mechanisms.Linkage.Drivers[1].Ratio==0.2);
        var clamped=GameDatabase.DeserializeDesign("{\"Version\":3,\"Mechanisms\":{\"Linkage\":{\"Drivers\":[{\"MemberId\":1,\"Ratio\":-1.5},{\"MemberId\":2,\"Ratio\":0}]}}}");
        Check("Controls: out-of-range saved strokes are clamped below 100%",clamped.Mechanisms.Linkage.Drivers[0].Ratio==-0.95 && clamped.Mechanisms.Linkage.Drivers[1].Ratio==-0.5);
        // Manual stroke percentage: kept by direction changes, clamped below 100%, undoable, and really reached
        directionEditor.SetHydraulicStroke(1,0.8);
        Check("Controls: stroke can be set manually (80%)",directionEditor.Design.Mechanisms.Linkage.Drivers.Single().Ratio==0.8);
        directionEditor.SetHydraulicDirection(1,false);
        Check("Controls: changing direction keeps the stroke",directionEditor.Design.Mechanisms.Linkage.Drivers.Single().Ratio==-0.8);
        directionEditor.SetHydraulicStroke(1,1.2);
        Check("Controls: stroke stays below 100%",directionEditor.Design.Mechanisms.Linkage.Drivers.Single().Ratio==-LinkageDriverData.MaxStroke);
        directionEditor.Undo();
        Check("Controls: stroke change is undoable",directionEditor.Design.Mechanisms.Linkage.Drivers.Single().Ratio==-0.8);
        {
            var st=new LinkageData();st.Drivers.Add(new LinkageDriverData{MemberId=1,Ratio=-0.8});
            var model=StructuralModel.Build(directionDesign,directionLevel,db);var drive=new LinkageSystem();
            bool ok=drive.Initialize(model,st);
            for(int step=1;step<=100 && ok;step++) ok=drive.TryMove(step/100.0);
            Check("Controls: 80% contraction reaches the shortest length L*(1-0.8)",ok && System.Math.Abs(model.Length[0]-0.8)<1e-6,drive.Fault);
            var bad=new LinkageData();bad.Drivers.Add(new LinkageDriverData{MemberId=1,Ratio=-1.0});
            Check("Controls: a 100% stroke is rejected",!new LinkageSystem().Initialize(StructuralModel.Build(directionDesign,directionLevel,db),bad));
        }
        IndependentFrameTests.Run(db,_root,Check);
        SuvTests.Run(db,_root,Check);
        HarborTests.Run(db,Check);
        LinkageTests.Run(db, Check);
        MotionRegressionTests.Run(db, _root, Check); ShipRigidTests.Run(db, _root, Check); CoupledTests.Run(db, _root, Check); SceneJointTests.Run(db, _root, Check);
        if (args.Contains("--linkage-only")) return _fail==0 ? 0 : 1;

        // 参考方案：每关都必须能在预算内通关
        foreach (var lv in db.Levels)
        {
            var d = LoadRef(lv.Id);
            var ed = new DesignEditor(d, lv, db);
            var r = new BridgeSimulation(ed.Design, lv, db).RunToEnd();
            Check($"参考方案 {lv.Id} 通关（成本 ${ed.UsedCost:0}/{lv.Budget:0}，最大利用率 {r.MaxUtilization:P0}，得分 {r.Score}）",
                r.IsSuccess && ed.UsedCost <= lv.Budget, r.FailureMessage);
        }

        var l1 = db.GetLevel("level1");
        var l2 = db.GetLevel("level3");

        // 测试 A：基础搭桥 + 预算扣除
        {
            var ed = new DesignEditor(null, l1, db);
            var a = ed.Design.Nodes.First(n => n.AnchorId == "L1").Id;
            var b = ed.Design.Nodes.First(n => n.AnchorId == "R1").Id;
            var n1 = ed.AddNode(4, 0).CreatedId;
            var r1 = ed.AddMember(a, n1, "road");
            double expect = 4 * db.Materials["road"].CostPerMeter;
            Check("A 创建构件并扣除预算", r1.Ok && Math.Abs(ed.UsedCost - expect) < 1e-6, $"used={ed.UsedCost}");
            ed.DeleteMember(r1.CreatedId);
            Check("A 删除构件退还费用", Math.Abs(ed.UsedCost) < 1e-6);
            ed.Undo();
            Check("A 撤销恢复构件", ed.Design.Members.Count == 1);
            ed.Redo();
            Check("A 重做再次删除", ed.Design.Members.Count == 0);
            Check("A 锚点不可删除", !ed.DeleteNode(a).Ok);
            Check("A 锚点不可移动", !ed.MoveNode(b, 13, 1).Ok);
            Check("A 禁止重复节点（锚点位置已有节点）", !ed.AddNode(0, 0).Ok);
            Check("A 删除构件后不留下孤立节点", ed.Design.Nodes.All(n => n.IsAnchor || ed.Design.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id)));
            Check("A 禁止在河道水面以下建节点", !ed.AddNode(6, -5).Ok);
            Check("A 禁止超长构件", !ed.AddMember(a, b, "road").Ok);
        }

        // 测试 B：预算（允许超支，但超支只能得 1 星）
        {
            var ed = new DesignEditor(LoadRef("level1"), l1, db);
            int prev = ed.Design.Nodes.First(n => n.AnchorId == "L1").Id;
            int created = 0; BuildResult last = default;
            for (int i = 0; i < 8; i++)
            {
                last = ed.AddMemberToNewNode(prev, -2.5 - (i % 2) * 5, 9 - i * 0.5, "steel");
                if (!last.Ok) break;
                prev = ed.Design.Members.First(m => m.Id == last.CreatedId).EndNodeId; created++;
            }
            Check($"B 超出预算仍可建造（已用 ${ed.UsedCost:0} / ${l1.Budget:0}）", ed.UsedCost > l1.Budget, last.Message);
            var r = new BridgeSimulation(ed.Design, l1, db).RunToEnd();
            var refScore = new BridgeSimulation(LoadRef("level1"), l1, db).RunToEnd().Score;
            Check($"B 超预算方案可通关，但得分低于预算内方案（{r.Score} < {refScore}）", r.IsSuccess && r.Score < refScore, r.FailureMessage);
        }

        // 测试 C：结构不足（只有桥面串联、无支撑）
        {
            var ed = new DesignEditor(null, l1, db);
            int a = ed.Design.Nodes.First(n => n.AnchorId == "L1").Id;
            int b = ed.Design.Nodes.First(n => n.AnchorId == "R1").Id;
            // 12 m 只铺木桥面（4 × 3 m）、中间没有任何支撑
            int n1 = ed.AddNode(3, 0).CreatedId, n2 = ed.AddNode(6, 0).CreatedId, n3 = ed.AddNode(9, 0).CreatedId;
            ed.AddMember(a, n1, "deck_wood"); ed.AddMember(n1, n2, "deck_wood"); ed.AddMember(n2, n3, "deck_wood"); ed.AddMember(n3, b, "deck_wood");
            var r = new BridgeSimulation(ed.Design, l1, db).RunToEnd();
            var st0 = BridgeSimulation.StaticAnalysis(ed.Design, l1, db);
            Check($"C 无支撑桥面按连续梁受弯，自重下不会直接碎裂（利用率 {st0.CurrentMaxUtilization:P0}）", st0.Output.Stable && st0.CurrentMaxUtilization < 1);
            Check("C 无支撑桥面在车辆作用下弯曲破坏", !r.IsSuccess && (r.FailureReason == FailureReason.DeckBendingFailure || r.FailureReason == FailureReason.StructureUnstable), r.FailureReason + " " + r.FailureMessage);
            Check($"C 车辆实际行驶到断口后才判负（t={r.FailureTime:0.0}s）", r.FailureTime > 1.0, r.FailureMessage);

            // 道路未连通：车辆开到桥面尽头坠落
            var ed2 = new DesignEditor(null, l1, db);
            int a2 = ed2.Design.Nodes.First(n => n.AnchorId == "L1").Id;
            ed2.AddMemberToNewNode(a2, 4, 0, "road");
            var r2 = new BridgeSimulation(ed2.Design, l1, db).RunToEnd();
            Check($"C 道路不完整：车辆实际行驶后失败（{r2.FailureReason}，t={r2.FailureTime:0.0}s）",
                !r2.IsSuccess && r2.FailureTime > 1.0 && (r2.FailureReason == FailureReason.RoadNotConnected || r2.FailureReason == FailureReason.StructureUnstable), r2.FailureMessage);
        }

        // 道路断口（结构完好）：车辆被卡住 3 秒后判负
        {
            var ed = new DesignEditor(LoadRef("level1"), l1, db);
            var lastDeck = ed.Design.Members.Where(m => m.MaterialId == "road").OrderByDescending(m => ed.Design.FindNode(m.EndNodeId).Position.X).First();
            ed.DeleteMember(lastDeck.Id);
            var sim = new BridgeSimulation(ed.Design, l1, db);
            var r = sim.RunToEnd();
            var car = sim.Vehicles[0];
            Check($"C 道路断口：车辆卡住 3 秒后判负（t={r.FailureTime:0.0}s，卡住 {car.StuckTime:0.0}s，未坠落）",
                !r.IsSuccess && !car.Fallen && car.StuckTime >= BridgeSimulation.StuckLimit - 1e-6, r.FailureMessage);
        }

        // 新材料规则：立柱基础、倾角、液压杆伸缩、桥面类型
        {
            var l3 = db.GetLevel("level5");
            var ed = new DesignEditor(null, l3, db);
            int a = ed.Design.Nodes.First(n => n.AnchorId == "L1").Id;
            int p1 = ed.Design.Nodes.First(n => n.AnchorId == "P1").Id;
            int NodeAt(double x, double y) => ed.Design.Nodes.First(n => Math.Abs(n.Position.X - x) < 1e-6 && Math.Abs(n.Position.Y - y) < 1e-6).Id;
            ed.AddMemberToNewNode(a, 5, 0, "road");
            ed.AddMemberToNewNode(NodeAt(5, 0), 10, 0, "road");
            ed.AddMemberToNewNode(NodeAt(10, 0), 15, 0, "road");
            Check("新 混凝土柱底端在河中被拒绝", !ed.AddMemberToNewNode(NodeAt(10, 0), 10, -3, "concrete").Ok);
            var rc = ed.AddMemberToNewNode(NodeAt(15, 0), 14, -4, "concrete");
            Check("新 混凝土柱可建在岩墩（陆地）上", rc.Ok, rc.Message);
            Check("新 钢筋混凝土柱不在锚点上被拒绝", !ed.AddMemberToNewNode(NodeAt(10, 0), 10, -3, "rc_column").Ok);
            var rr = ed.AddMember(p1, NodeAt(15, 0), "rc_column");
            Check("新 钢筋混凝土柱可建在锚点上", rr.Ok, rr.Message);
            var model = StructuralModel.Build(ed.Design, l3, db);
            int fi = model.NodeIndex[NodeAt(14, -4)];
            Check("新 混凝土柱落地端成为固定基础", model.IsFoundation[fi] && model.FixRot[fi]);
            Check("新 钢筋混凝土柱的锚点端成为刚接", model.FixRot[model.NodeIndex[p1]]);
            var pv = ed.PreviewMemberToPoint(NodeAt(10, 0), 13.5, -4, "concrete");
            Check($"新 倾角过大的混凝土柱可建但有警告（{pv.Warning}）", pv.Allowed && pv.Warning != null, pv.Reason);
            var pv2 = ed.PreviewMemberToPoint(NodeAt(10, 0), 13, -4, "rc_column");
            Check("新 钢筋混凝土柱允许的倾角大于混凝土柱", db.Materials["rc_column"].MaxTiltDegrees > db.Materials["concrete"].MaxTiltDegrees);
            ed.AddMemberToNewNode(NodeAt(10, 0), 13.5, -4, "concrete");
            var sim = new BridgeSimulation(ed.Design, l3, db);
            sim.Initialize();
            Check("新 倾角过大的立柱在测试开始时倒塌", sim.Events.Any(e => e.Text.Contains("倾角")));

            // 液压杆伸缩
            var ed2 = new DesignEditor(null, db.GetLevel("level7"), db);
            int a2 = ed2.Design.Nodes.First(n => n.AnchorId == "L1").Id;
            var h = ed2.AddMemberToNewNode(a2, 3, 2, "hydraulic");
            double len0 = ed2.Design.FindMember(h.CreatedId).Length;
            var rs = ed2.ResizeMember(h.CreatedId, 0.5);
            Check($"新 液压杆可伸长（{len0:0.00} → {ed2.Design.FindMember(h.CreatedId).Length:0.00} m）", rs.Ok && Math.Abs(ed2.Design.FindMember(h.CreatedId).Length - len0 - 0.5) < 1e-6, rs.Message);
            // Slider on an ordinary hydraulic (any level): stores the stroke on the member, no linkage mode, sleeve follows
            {
                int hid = h.CreatedId; var hmem = ed2.Design.FindMember(hid);
                double sleeve0 = DesignEditor.SleeveFractions(ed2.Design, ed2.Level, db)[hid];
                var hr0 = ed2.GetHydraulicRange(hid);
                var t1 = ed2.SetHydraulicTarget(hid, -0.3);
                double sleeve1 = DesignEditor.SleeveFractions(ed2.Design, ed2.Level, db)[hid];
                var t2 = ed2.SetHydraulicTarget(hid, 0.4);
                double sleeve2 = DesignEditor.SleeveFractions(ed2.Design, ed2.Level, db)[hid];
                Check($"普通液压杆 滑块可控制（范围 {hr0.MinPct:P0}~{hr0.MaxPct:P0}，{t1.Message}）", hr0.Valid && !hr0.Leaf && t1.Ok && t2.Ok && hmem.Stroke == 0.4 && (ed2.Design.Mechanisms?.Linkage?.Drivers.Count ?? 0) == 0 && Math.Abs(ed2.GetHydraulicRange(hid).CurrentPct - 0.4) < 1e-9);
                Check($"普通液压杆 黄色外筒 = 最短长度（默认 {sleeve0:P0}，-30% → {sleeve1:P0}，+40% → {sleeve2:P0}）", Math.Abs(sleeve0 - (1 - db.Materials["hydraulic"].RetractRatio)) < 1e-9 && Math.Abs(sleeve1 - 0.7) < 1e-9 && Math.Abs(sleeve2 - 1) < 1e-9);
                var simS = new BridgeSimulation(ed2.Design, ed2.Level, db);
                Check("普通液压杆 旧存档没有行程字段时按材料默认", GameDatabase.DeserializeDesign("{\"Members\":[{\"Id\":1,\"MaterialId\":\"hydraulic\"}]}").Members[0].Stroke == null);
                ed2.Undo(); Check("普通液压杆 撤销恢复上一次行程", ed2.Design.FindMember(hid).Stroke == -0.3);
                ed2.Undo(); Check("普通液压杆 再撤销恢复默认", ed2.Design.FindMember(hid).Stroke == null);
            }
            // Hydraulic price grows with its stroke; new hydraulics are priced for the default stroke
            {
                var pe = new DesignEditor(null, db.GetLevel("level7"), db);
                int pa = pe.Design.Nodes.First(n => n.AnchorId == "L1").Id;
                int ph = pe.AddMemberToNewNode(pa, 3, 2, "hydraulic").CreatedId;
                var pm = pe.Design.FindMember(ph); var hmat = db.Materials["hydraulic"];
                double baseCost = pm.Length * hmat.CostPerMeter, c0 = pm.EffectiveCost();
                pe.SetHydraulicTarget(ph, -0.2); double c20 = pm.EffectiveCost();
                pe.SetHydraulicTarget(ph, 0.8); double c80 = pm.EffectiveCost();
                Check($"液压杆 行程越大越贵（20% ${c20:0} < 80% ${c80:0}，默认 95% ${c0:0}，无行程价 ${baseCost:0}）",
                    c20 < c80 && c80 < c0 + 1e-9 && c20 > baseCost && Math.Abs(pe.UsedCost - pe.Design.Members.Sum(m => m.EffectiveCost()) - pe.Design.Nodes.Sum(n => n.PierBase ? FoundationRules.Cost(n.Foundation) : 0)) < 1e-6);
                Check("液压杆 旧存档没有行程价格字段时费用不变", new BridgeMember { Cost = 1000 }.EffectiveCost() == 1000);
            }
            // Cross beams: members >= 5 m get one (+10% tension), shorter ones do not; they block vehicles
            {
                var st = db.Materials["steel"];
                Check("横梁 ≥5 m 的钢梁自动加横梁，4.9 m 不加，钢索 / 液压杆 / 桥面不加", CrossBeams.Has(st, 5) && !CrossBeams.Has(st, 4.9) && !CrossBeams.Has(db.Materials["cable"], 8) && !CrossBeams.Has(db.Materials["hydraulic"], 6) && !CrossBeams.Has(db.Materials["deck_steel"], 6));
                var col = db.Materials["rc_column"];
                var fr = CrossBeams.Fractions(col, 25, 0, 25); var frDown = CrossBeams.Fractions(col, 25, 25, 0);
                Check($"横梁 柱每 10 m 一根横杆（8 m:{CrossBeams.Count(col, 8)} 10 m:{CrossBeams.Count(col, 10)} 12 m:{CrossBeams.Count(col, 12)} 25 m:{CrossBeams.Count(col, 25)}），从柱底量起，桥墩不加",
                    col.IsColumn && CrossBeams.Count(col, 8) == 0 && CrossBeams.Count(col, 10) == 0 && CrossBeams.Count(col, 12) == 1 && CrossBeams.Count(col, 25) == 2
                    && Math.Abs(fr[0] - 0.4) < 1e-12 && Math.Abs(fr[1] - 0.8) < 1e-12 && Math.Abs(frDown[0] - 0.6) < 1e-12 && !CrossBeams.Has(db.Materials["pier_concrete"], 30));
                Check("横梁 增加 10% 抗拉", Math.Abs(CrossBeams.TensionFactor(st, 6) - 1.1) < 1e-12 && CrossBeams.TensionFactor(st, 3) == 1);
                var orig = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(_root, "Data/ReferenceDesigns/legacy/level3_reference_v_before_crossbeams.json")));
                var rOld = new BridgeSimulation(orig, db.GetLevel("level3"), db).RunToEnd();
                Check($"横梁 有体积：旧参考方案的长斜杆横梁挡住货车（{rOld.FailureMessage}）", !rOld.IsSuccess && rOld.FailureReason == FailureReason.VehicleBlocked && rOld.FailureMessage.Contains("横梁") && rOld.InvolvedMemberIds.Count == 1);
                foreach (var lv in db.Levels)
                {
                    var rd = LoadRef(lv.Id);
                    double tall = lv.TrafficEvents.Where(e => e.Type == TrafficEventType.Vehicle && e.VehicleId != null && db.Vehicles.ContainsKey(e.VehicleId)).Select(e => db.Vehicles[e.VehicleId].Height).DefaultIfEmpty(0).Max();
                    if (rd.Members.Any(m => CrossBeamFixtures.Blocks(rd, db, m, tall - 0.3))) Check($"横梁 {lv.Id} 参考方案没有挡车横梁", false);
                }
            }
            // Composite cable: pricier, longer, thicker, heavier, stronger; cables keep their weight in the model
            {
                var cb = db.Materials["cable"]; var cc = db.Materials["cable_composite"];
                Check($"复合钢索 更贵更长更粗更重拉力更大（{cc.TensionCapacity / cb.TensionCapacity:0.0} 倍）", cc.IsCable && cc.SideOnly && cc.CostPerMeter > cb.CostPerMeter && cc.MaximumLength > cb.MaximumLength && cc.VisualThickness > cb.VisualThickness && cc.MassPerMeter > cb.MassPerMeter && cc.TensionCapacity > cb.TensionCapacity * 2);
                Check("复合钢索 进阶关卡可用、前四关不开放", db.Levels.Skip(4).Any(l => l.AllowedMaterials.Contains("cable_composite")) && db.Levels.Take(4).All(l => !l.AllowedMaterials.Contains("cable_composite")));
            }
            Check("新 钢梁不可伸缩", !ed2.ResizeMember(ed2.AddMemberToNewNode(a2, -2, 2, "steel").CreatedId, 0.5).Ok);
            var hm = db.Materials["hydraulic"]; var sm = db.Materials["steel"];
            Check("新 液压杆强度略低于钢材、钢材更重", hm.TensionCapacity < sm.TensionCapacity && hm.TensionCapacity > 0.8 * sm.TensionCapacity && sm.MassPerMeter > hm.MassPerMeter);
            var cm = db.Materials["concrete"]; var rm = db.Materials["rc_column"];
            Check("新 钢筋混凝土柱比混凝土柱更重更强更贵，且比钢材贵",
                rm.MassPerMeter > cm.MassPerMeter && rm.TensionCapacity > cm.TensionCapacity && rm.CostPerMeter > cm.CostPerMeter && rm.CostPerMeter > sm.CostPerMeter && cm.CostPerMeter > db.Materials["wood"].CostPerMeter);
            var decks = db.Materials.Values.Where(x => x.IsDeck).ToList();
            Check($"新 有 {decks.Count} 种桥面，价格/重量/承重/长度各不相同",
                decks.Count >= 3 && decks.Select(x => x.CostPerMeter).Distinct().Count() == decks.Count && decks.Select(x => x.MaximumLength).Distinct().Count() == decks.Count
                && decks.Select(x => x.BendingCapacity).Distinct().Count() == decks.Count && decks.Select(x => x.MassPerMeter).Distinct().Count() == decks.Count);
            Check("新 所有材料价格互不相同", db.Materials.Values.Select(x => x.CostPerMeter).Distinct().Count() == db.Materials.Count);
            // 性能面板：每关都至少有一种允许的桥面，其“跨中集中承载（吨）”不低于本关最重车辆（多节列车按单节）
            foreach (var lv in db.Levels)
            {
                double heaviest = lv.TrafficEvents.Where(e => e.VehicleId != null && db.Vehicles.ContainsKey(e.VehicleId)).Select(e => MaterialRatings.VehicleTonnes(db.Vehicles[e.VehicleId])).DefaultIfEmpty(0).Max();
                double best = lv.AllowedMaterials.Select(id => MaterialRatings.DeckCapacityTonnes(db.GetMaterial(id))).DefaultIfEmpty(0).Max();
                Check($"面板 {lv.Id}：最强桥面可承载 {best:0} 吨 ≥ 最重车辆 {heaviest:0} 吨", best >= heaviest);
            }
            var s0 = MaterialRatings.StatsFor(db.Materials["cable"], db.Materials.Values);
            Check("面板 指标为 0~5 格，钢索抗压 0 格", s0.All(x => x.Cells >= 0 && x.Cells <= MaterialRatings.MaxCells) && s0.First(x => x.Label == "抗压").Cells == 0);
        }

        // Ships and hydraulics.
        // (a) Without a valid mechanism hydraulics never move: no automatic forced retraction, a clear hint instead.
        {
            var l4 = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(db.GetLevel("level7"), GameDatabase.JsonOptions), GameDatabase.JsonOptions);
            l4.Terrain.NoBuildZones.Clear();
            foreach (var e in l4.TrafficEvents.Where(e => e.Type == TrafficEventType.Ship)) e.Height = 4.5;
            BridgeDesign WithKeel(string mat)
            {
                var legacyRef = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(_root, "Data/ReferenceDesigns/legacy/level7_reference_v1_fixed.json")));
                CrossBeamFixtures.SplitBlocking(legacyRef, l4, db);
                var ed = new DesignEditor(legacyRef, l4, db);
                int NodeAt(double x, double y) => ed.Design.Nodes.First(n => Math.Abs(n.Position.X - x) < 0.01 && Math.Abs(n.Position.Y - y) < 0.01).Id;
                var r1 = ed.AddMemberToNewNode(NodeAt(9, 0), 12, -2, mat);
                int keel = ed.Design.FindMember(r1.CreatedId).EndNodeId;
                ed.AddMember(keel, NodeAt(15, 0), mat);
                return ed.Design;
            }
            var hit = new BridgeSimulation(WithKeel("steel"), l4, db).RunToEnd();
            Check($"船 航道上方的钢梁被船撞（{hit.FailureReason}）", !hit.IsSuccess && hit.FailureReason == FailureReason.ShipCollision, hit.FailureMessage);
            var simH = new BridgeSimulation(WithKeel("hydraulic"), l4, db);
            simH.Initialize();
            double maxLift = 0;
            int ki = simH.Model.NodeIndex[simH.Design.Nodes.First(n => Math.Abs(n.Position.Y + 2) < 0.01).Id];
            while (simH.State == SimState.Running) { simH.Step(); maxLift = Math.Max(maxLift, Math.Abs(simH.KinematicDisplacement[2 * ki + 1])); }
            Check($"船 没有有效开启机构时液压杆不动作（节点被动位移 {maxLift:0.000} m），船撞上并提示如何配置（{simH.Result.FailureReason}）",
                maxLift < 1e-9 && simH.HydraulicFactor == 0 && simH.Result.FailureReason == FailureReason.ShipCollision
                && simH.Events.Any(e => e.Text.Contains("没有有效的开启桥配置") || e.Text.Contains("开启桥设置有误")), simH.Result.FailureMessage + " | " + string.Join(" | ", simH.Events.Select(e => e.Text)));
        }

        // (b) Level 7 double-leaf bascule with automatic seams.
        {
            var l7 = db.GetLevel("level7");
            var ship7 = l7.TrafficEvents.First(e => e.Type == TrafficEventType.Ship);
            double shipTop = l7.Terrain.WaterLevel + ship7.Height;
            var deck7 = db.Materials["road"];
            Check($"第七关 净空：船顶 Y={shipTop:0.0} m 高于闭合桥面底面（Y={-deck7.VisualThickness / 2:0.00} m），不开桥就过不去", shipTop > 0);
            double rise = shipTop + deck7.VisualThickness / 2, run = ship7.LaneX - ship7.Width / 2;
            Check($"第七关 固定抬高坡度 {rise / run:P0} 超过推荐坡度，但低于 30° 爬坡硬上限", rise / run > l7.Objectives.MaxDeckSlope && rise / run < BridgeArchitect3D.Traffic.RoadPath.MaxClimbGrade);

            var refD = LoadRef("level7");
            var plan = BasculePlanner.Plan(refD, db);
            var cutX = plan.CutSeams.Select(s => plan.Seams[s].X).OrderBy(x => x).ToList();
            Check($"第七关 自动识别：{plan.Groups.Count} 片桥叶（转轴 #{string.Join("、#", plan.Groups.Select(g => Math.Abs(g.PivotKey)))}），只分离 X={string.Join("、", cutX.Select(x => x.ToString("0.#")))} 的接缝（共 {plan.Seams.Count} 处接缝）",
                plan.Groups.Count == 2 && cutX.SequenceEqual(new[] { 2.0, 12.0, 22.0 }) && plan.Seams.Count > cutX.Count, string.Join("；", plan.Rejected.Values));
            var sim = new BridgeSimulation(refD, l7, db); sim.Initialize();
            var bs = sim.Bascule;
            var centre = bs.Locks.First(lk => Math.Abs(sim.Model.X0[lk.a] - 12) < 1e-6);
            int ia = centre.a, ib = centre.b;
            double Gap() { var d = sim.DisplayDisplacement; return Math.Sqrt(Math.Pow(sim.Model.X0[ia] + d[2 * ia] - sim.Model.X0[ib] - d[2 * ib], 2) + Math.Pow(sim.Model.Y0[ia] + d[2 * ia + 1] - sim.Model.Y0[ib] - d[2 * ib + 1], 2)); }
            // Leaf body rigid: every pairwise distance between leaf nodes (and to the pivot) is preserved.
            double Shape(LeafRuntime l) { double e = 0; var ns = l.Nodes.Append(l.Pivot).ToArray();
                foreach (int p in ns) foreach (int q in ns) e = Math.Max(e, Math.Abs(Math.Sqrt(Math.Pow(sim.Model.X[p] - sim.Model.X[q], 2) + Math.Pow(sim.Model.Y[p] - sim.Model.Y[q], 2)) - Math.Sqrt(Math.Pow(sim.Model.X0[p] - sim.Model.X0[q], 2) + Math.Pow(sim.Model.Y0[p] - sim.Model.Y0[q], 2))));
                return e; }
            double maxGap = 0, maxShapeErr = 0, maxAngle = 0, vehWait = 0, shipWait = 0, maxNonLeafMove = 0;
            bool openWhenShipArrived = true, vehicleOnSpanWhileMoving = false, shipEntered = false;
            var states = new System.Collections.Generic.List<BridgeOpState>();
            var nonLeaf = Enumerable.Range(0, sim.Model.NodeCount).Where(i => !bs.IsLeafNode(i)).ToArray();
            while (sim.State == SimState.Running)
            {
                sim.Step();
                if (states.Count == 0 || states[^1] != bs.State) states.Add(bs.State);
                maxGap = Math.Max(maxGap, Gap());
                foreach (var l in bs.Leaves) maxShapeErr = Math.Max(maxShapeErr, Shape(l));
                foreach (int i in nonLeaf) maxNonLeafMove = Math.Max(maxNonLeafMove, Math.Abs(sim.Model.X[i] - sim.Model.X0[i]) + Math.Abs(sim.Model.Y[i] - sim.Model.Y0[i]));
                maxAngle = Math.Max(maxAngle, bs.Leaves.Max(l => l.Angle));
                if (sim.Vehicles.Any(v => v.WaitingForBridge)) vehWait += BridgeSimulation.FixedDt;
                if (sim.Ships.Any(s => s.Waiting)) shipWait += BridgeSimulation.FixedDt;
                foreach (var s in sim.Ships) if (s.InBridgeZone) { shipEntered = true; if (!bs.IsOpen) openWhenShipArrived = false; }
                if (bs.State is BridgeOpState.Unlocking or BridgeOpState.Opening or BridgeOpState.Open or BridgeOpState.Closing or BridgeOpState.Locking)
                    foreach (var v in sim.Vehicles)
                    {
                        var z = bs.ZoneOn(v.Path);
                        if (!v.Finished && z.HasValue && v.BodyFrontS > z.Value.s0 && v.BodyRearS < z.Value.s1) vehicleOnSpanWhileMoving = true;
                    }
            }
            var r7 = sim.Result;
            Check($"第七关 中央接缝真实分开：最大间距 {maxGap:0.00} m，桥叶最大开启 {maxAngle:0}°", maxGap > 3 && maxAngle > 60);
            Check($"第七关 整片桥叶刚体转动（内部距离最大变化 {maxShapeErr * 1000:0.000} mm），固定部分与转轴不动（{maxNonLeafMove * 1000:0.000} mm）", maxShapeErr < 1e-6 && maxNonLeafMove < 1e-9);
            Check($"第七关 状态顺序 {string.Join("→", states)}", string.Join(",", states) == "Closed,StoppingTraffic,Unlocking,Opening,Open,Closing,Locking,Closed");
            Check($"第七关 船到达桥位时桥已完全打开（船等候 {shipWait:0.0} s）", shipEntered && openWhenShipArrived);
            Check($"第七关 动作期间活动桥面上没有车辆，闭合锁定后才放行（车辆等候 {vehWait:0.0} s）", !vehicleOnSpanWhileMoving && vehWait > 1);
            Check($"第七关 参考方案通关：车辆 {r7.PassedVehicles}/{r7.VehicleResults.Count}、船 {r7.ShipsPassed}/{r7.TotalShips}，断裂 {sim.BrokenCount} 根，结束时闭合并恢复连接",
                r7.IsSuccess && sim.BrokenCount == 0 && bs.ClosedAndLocked && bs.Fault == null, r7.FailureMessage + " " + bs.Fault);
            Check($"第七关 参考方案费用 ${refD.TotalCost():0} 不超过预算 ${l7.Budget:0}", refD.TotalCost() <= l7.Budget);

            // Opening too little: the ship hits the leaves.
            var low = refD.Clone(); low.Mechanisms.Leaves.Clear();
            foreach (var g in plan.Groups) low.Mechanisms.Leaves.Add(new BasculeLeafData { ActuatorMemberId = g.Actuators[0].MemberId, PivotNodeId = Math.Abs(g.PivotKey), OpenAngleDeg = 15 });
            var rn = new BridgeSimulation(low, l7, db).RunToEnd();
            Check($"第七关 桥叶只开 15°：船撞上桥叶（{rn.FailureReason}）", !rn.IsSuccess && rn.FailureReason == FailureReason.ShipCollision, rn.FailureMessage);

            // Force limit: a weak actuator stalls with a clear fault; members do not break.
            var hyd = db.Materials["hydraulic"]; double keepForce = hyd.ActuatorForce;
            hyd.ActuatorForce = 20000;
            var simF = new BridgeSimulation(refD, l7, db); var rf = simF.RunToEnd();
            hyd.ActuatorForce = keepForce;
            Check($"第七关 液压力不足：桥叶停住并提示（{simF.Bascule.Fault}），不是构件断裂（断裂 {simF.BrokenCount}）",
                simF.Bascule.Fault != null && simF.Bascule.Fault.Contains("不足") && simF.BrokenCount == 0 && !rf.IsSuccess);

            // Stroke limit applies to every actuator: a target beyond the stroke is reported.
            var far = refD.Clone(); far.Mechanisms.Leaves.Clear();
            foreach (var g in plan.Groups) far.Mechanisms.Leaves.Add(new BasculeLeafData { ActuatorMemberId = g.Actuators[0].MemberId, PivotNodeId = Math.Abs(g.PivotKey), OpenAngleDeg = 85 });
            var simS = new BridgeSimulation(far, l7, db); simS.Initialize();
            double reach = simS.Bascule.Leaves.Min(l => l.ReachAngle);
            Check($"第七关 液压行程：参考方案液压杆收缩行程为 50%最多开到 {reach:0}°（≥ 70°）", reach >= 70);

            // An extra brace from the leaf to the fixed structure: the member is named and nothing is driven.
            var braced = new DesignEditor(refD.Clone(), l7, db);
            int D1 = braced.Design.Nodes.First(n => Math.Abs(n.Position.X - 5.5) < 1e-6 && Math.Abs(n.Position.Y) < 1e-6).Id;
            int L2 = braced.Design.Nodes.First(n => n.AnchorId == "L2").Id;
            var br = braced.AddMember(L2, D1, "steel");
            var simB = new BridgeSimulation(braced.Design, l7, db); var rb = simB.RunToEnd();
            var pb = BasculePlanner.Plan(braced.Design, db, l7);
            int leftAct = braced.Design.Members.First(m => m.MaterialId == "hydraulic" && braced.Design.FindNode(m.StartNodeId).Position.X < 12).Id;
            // The left leaf is locked by the brace (named, not driven); the right leaf alone still clears the ship.
            Check($"第七关 斜撑锁死左桥叶：左侧液压杆 #{leftAct} 不驱动并指出构件 #{br.CreatedId}，右叶单独开启（{simB.Bascule.ConfigNote}）",
                br.Ok && pb.GroupOf(leftAct) == null && pb.Groups.Count == 1 && simB.Bascule.Leaves.Count == 1 && simB.Bascule.ConfigNote != null && simB.Bascule.ConfigNote.Contains($"#{br.CreatedId}")
                , $"groups={pb.Groups.Count} ok={rb.IsSuccess} msg={rb.FailureMessage}");

            // Two hydraulics on the same leaf work together (same pivot, same leaf); each one is force-checked.
            var co = refD.Clone(); var h0 = co.Members.First(m => m.MaterialId == "hydraulic");
            co.Members.Add(new BridgeMember { Id = co.NextMemberId++, StartNodeId = h0.StartNodeId, EndNodeId = h0.EndNodeId, MaterialId = "hydraulic", Length = h0.Length, Cost = h0.Cost });
            var pc = BasculePlanner.Plan(co, db);
            Check($"第七关 同一桥叶的两根液压杆协同驱动（{pc.Groups.Count} 片桥叶，最多 {pc.Groups.Max(g => g.Actuators.Count)} 根杆）", pc.Groups.Count == 2 && pc.Groups.Any(g => g.Actuators.Count == 2));

            // Save / load keeps the mechanism settings.
            var round = GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(far));
            Check("存档 开启桥设置可保存 / 读取", round.Mechanisms.Leaves.Select(l => (l.ActuatorMemberId, l.OpenAngleDeg)).SequenceEqual(far.Mechanisms.Leaves.Select(l => (l.ActuatorMemberId, l.OpenAngleDeg))));

            // Replay frames carry the opened geometry.
            var simR = new BridgeSimulation(refD, l7, db); simR.Initialize();
            double frameLift = 0;
            while (simR.State == SimState.Running && simR.Time < 25) simR.Step();
            int ci = simR.Bascule.Locks.First(lk => Math.Abs(simR.Model.X0[lk.a] - 12) < 1e-6).a;
            for (int i = 0; i < simR.HistoryCount; i++) frameLift = Math.Max(frameLift, simR.GetHistoryFrame(i).Kinematic[2 * ci + 1]);
            Check($"回放 历史帧包含桥叶抬起的几何（叶尖最大抬升 {frameLift:0.0} m）", frameLift > 3);
        }

        // (c) The player's old level 7 save (version 1, shared centre node): loads unchanged, the mechanism is
        //     found automatically (outer hydraulics drive, inner ones stay rigid, only the centre seam opens) and
        //     the real problem (stroke) is reported instead of a fake pass.
        {
            string fx = Path.Combine(_root, "Tests/BridgeTests/Fixtures/level7_bascule.json");
            string before = File.ReadAllText(fx);
            var old = GameDatabase.DeserializeDesign(before);
            var l7 = db.GetLevel("level7");
            Check("存档 旧版存档（版本 1）可以读取，机构数据为空", old.Mechanisms != null && old.Mechanisms.IsEmpty);
            var po = BasculePlanner.Plan(old, db);
            var drivers = po.Groups.SelectMany(g => g.Actuators.Select(a => a.MemberId)).OrderBy(x => x).ToList();
            var cut = po.CutSeams.Select(s => po.Seams[s].NodeId).ToList();
            Check($"旧存档 自动匹配：驱动液压杆 #{string.Join("、#", drivers)}，转轴 #{string.Join("、#", po.Groups.Select(g => Math.Abs(g.PivotKey)))}，只分离节点 #{string.Join("、#", cut)}；其余：{string.Join("；", po.Rejected.Select(r => $"#{r.Key} {r.Value}"))}",
                drivers.Count == 2 && cut.SequenceEqual(new[] { 13 }) && po.Groups.All(g => !g.LeafKeys.Contains(g.PivotKey)));
            var rc = new BridgeSimulation(old, l7, db); var rcr = rc.RunToEnd();
            // With the automatic two-way stroke (95%) the old save opens fully and the ship really passes (collision checks unchanged)
            Check($"旧存档 自动行程上限下桥叶开到目标角、船只真实通过（{rcr.ShipsPassed}/{rcr.TotalShips}）", rcr.IsSuccess && rcr.TotalShips > 0 && rcr.ShipsPassed == rcr.TotalShips && rc.Bascule.Fault == null && rc.Bascule.Leaves.Count == 2 && rc.Bascule.Leaves.All(l => l.ReachAngle >= l.OpenAngleDeg - 1e-9), $"reason={rcr.FailureReason} msg={rcr.FailureMessage}");
            var ed = new DesignEditor(old, l7, db);
            Check($"编辑 选中液压杆显示驱动关系（{ed.DescribeActuator(23)}）", ed.DescribeActuator(23).Contains("转轴节点 #5"));
            Check("旧存档 测试不修改原文件", File.ReadAllText(fx) == before);
        }

        // (d) The current level 7 save (version 2) as a regression case: node identity, braces, retry of combinations.
        {
            string fx = Path.Combine(_root, "Tests/BridgeTests/Fixtures/level7_current.json");
            string before = File.ReadAllText(fx);
            var cur = GameDatabase.DeserializeDesign(before);
            var l7 = db.GetLevel("level7");
            var pl = BasculePlanner.Plan(cur, db, l7);
            string Desc(BasculePlan p) => $"桥叶 {p.Groups.Count}（{string.Join("；", p.Groups.Select(g => $"杆 #{string.Join("、#", g.Actuators.Select(a => a.MemberId))} 转轴 #{g.PivotKey} 节点 {string.Join(",", g.LeafKeys)}"))}），分离接缝 {string.Join(",", p.CutSeams.Select(i => p.Seams[i].NodeId))}；未驱动：{string.Join("；", p.Rejected.Select(r => $"#{r.Key} {r.Value}"))}";
            Console.WriteLine("    当前存档识别：" + Desc(pl));
            Check("当前存档 桥叶节点与转轴不重叠，规划键在模型里一一对应",
                pl.Groups.All(g => !g.LeafKeys.Contains(g.PivotKey)) && pl.Groups.SelectMany(g => g.LeafKeys).Count() == pl.Groups.SelectMany(g => g.LeafKeys).Distinct().Count());
            Check($"当前存档 斜撑 #52 / #54 锁死外侧桥叶：#43、#45 不驱动并点名斜撑",
                pl.GroupOf(43) == null && pl.GroupOf(45) == null && pl.Rejected[43].Contains("#52") && pl.Rejected[45].Contains("#54"), string.Join("；", pl.Rejected.Select(r => $"#{r.Key} {r.Value}")));
            var simC = new BridgeSimulation(cur, l7, db); simC.Initialize();
            Check($"当前存档 运行时核对通过（{string.Join("；", simC.Bascule.Errors)}）", simC.Bascule.Errors.Count == 0);
            var rcur = simC.RunToEnd();
            Check($"当前存档 不能通关且原因写明（{rcur.FailureReason}）", !rcur.IsSuccess && !string.IsNullOrEmpty(rcur.FailureMessage), rcur.FailureMessage);
            Console.WriteLine("    当前存档结果：" + rcur.FailureMessage.Replace('\n', ' ') + " | " + simC.Bascule.Fault + " | " + simC.Bascule.ConfigNote);
            // Remove the two braces (in memory only): the outer hydraulics are now matched, pivots 17 and 21.
            var nb = cur.Clone(); nb.Members.RemoveAll(m => m.Id == 52 || m.Id == 54);
            var pn = BasculePlanner.Plan(nb, db, l7);
            Console.WriteLine("    去掉斜撑后识别：" + Desc(pn));
            Check("当前存档 去掉斜撑后找到实际稳定的局部或完整桥叶",
                pn.Groups.Count > 0 && pn.Groups.All(g => !g.LeafKeys.Contains(g.PivotKey)) && BasculeSystem.CheckStable(nb, l7, db, pn, out _));
            var simN = new BridgeSimulation(nb, l7, db); var rnb = simN.RunToEnd();
            Console.WriteLine("    去掉斜撑后结果：" + (rnb.IsSuccess ? "通关" : rnb.FailureMessage.Replace('\n', ' ')) + " | " + simN.Bascule.Fault);
            Check("当前存档 去掉斜撑后运行时核对通过", simN.Bascule.Errors.Count == 0, string.Join("；", simN.Bascule.Errors));
            Check("当前存档 测试不修改原文件", File.ReadAllText(fx) == before);
        }

        // Regression: independent free deck tips need no additional seam release.
        {
            string fixture = Path.Combine(_root, "Tests/BridgeTests/Fixtures/level7_player_regression.json");
            string original = File.ReadAllText(fixture);
            var player = GameDatabase.DeserializeDesign(original);
            var level = db.GetLevel("level7");
            var plan = BasculePlanner.Plan(player, db, level);
            Check("Hydraulics: free tips select local decks 76/77 and pivots 18/20",
                plan.Groups.Count == 2 && plan.GroupOf(78)?.PivotKey == 18 && plan.GroupOf(79)?.PivotKey == 20
                && plan.GroupOf(78).MemberIds.SetEquals(new[] {76}) && plan.GroupOf(79).MemberIds.SetEquals(new[] {77})
                && plan.CutSeams.Count == 0, string.Join(";", plan.Rejected.Values));
            // Deliberately restrict this one test to exercise the partial-stroke safety boundary.
            double normalStroke=db.Materials["hydraulic"].RetractRatio;
            db.Materials["hydraulic"].RetractRatio=0.25;
            var sim = new BridgeSimulation(player, level, db); sim.Initialize();
            double lift = 0; bool partialReportedOpen = false;
            while (sim.State == SimState.Running)
            {
                sim.Step();
                foreach (var leaf in sim.Bascule.Leaves) lift = Math.Max(lift, leaf.Angle);
                if (sim.Bascule.IsOpen && sim.Bascule.Leaves.Any(l => l.Angle < l.OpenAngleDeg - 1e-6)) partialReportedOpen = true;
            }
            Check("Hydraulics: local decks lift; insufficient stroke is not a lock or a full opening",
                lift > 30 && lift < 70 && !partialReportedOpen && sim.BrokenCount == 0
                && sim.Bascule.State == BridgeOpState.Blocked && sim.Bascule.Fault?.Contains("行程不足") == true);
            Check("Hydraulics: insufficient opening cannot stop a ship and results in collision",
                sim.Ships.All(ship => !ship.Waiting) && sim.Result.FailureReason == FailureReason.ShipCollision && sim.Result.ShipsPassed == 0);
            // The navigation envelope remains blocked even though the mechanism itself can move.
            Check("Hydraulics: navigation obstruction is independent of mechanical recognition",
                plan.Groups.Count == 2 && BasculeSystem.ChannelBlocked(player, level, db, plan) != null);
            db.Materials["hydraulic"].RetractRatio=normalStroke;
            var cycle = player.Clone();
            foreach (int driver in new[] {78, 79}) cycle.Mechanisms.Leaves.Add(new BasculeLeafData { ActuatorMemberId=driver, OpenAngleDeg=30, SpeedDegPerSec=8 });
            var cs = new BridgeSimulation(cycle, level, db); cs.Initialize();
            double[] motion = new double[cs.Model.NodeCount * 2];
            for (int i=0; i<300; i++) { cs.Bascule.Update(0.05, true, true); cs.Bascule.ApplyGeometry(motion); }
            Check("Hydraulics: reachable target opens both local leaves", cs.Bascule.IsOpen && cs.Bascule.Leaves.All(l => Math.Abs(l.Angle-30)<1e-9));
            for (int i=0; i<300; i++) { cs.Bascule.Update(0.05, false, true); cs.Bascule.ApplyGeometry(motion); }
            Check("Hydraulics: local leaves close and lock without drifting", cs.Bascule.ClosedAndLocked && motion.All(v => Math.Abs(v)<1e-9));
            // Repeat the cycle to catch stale state and fault carry-over.
            for (int i=0; i<300; i++) cs.Bascule.Update(0.05, true, true);
            for (int i=0; i<300; i++) cs.Bascule.Update(0.05, false, true);
            Check("Hydraulics: repeated operation returns to locked", cs.Bascule.ClosedAndLocked);
            var one = player.Clone(); one.Members.RemoveAll(m => m.Id==79);
            Check("Hydraulics: one driver still selects its local deck", BasculePlanner.Plan(one, db, level).GroupOf(78)?.PivotKey == 18);
            // A shortened final motion step must roll back to the exact previous angle on force overload.
            foreach (var def in cycle.Mechanisms.Leaves) def.OpenAngleDeg=1;
            var fs = new BridgeSimulation(cycle, level, db); fs.Initialize();
            for (int i=0; i<4; i++) fs.Bascule.Update(1, true, true);
            var force = new SolveOutput(); force.EnsureSize(fs.Model.NodeCount, fs.Model.MemberCount);
            foreach (var leaf in fs.Bascule.Leaves) foreach (var act in leaf.Actuators) force.AxialForce[act.Member]=act.ForceLimit*2;
            bool rolledBack=fs.Bascule.CheckActuators(force,1);
            Check("Hydraulics: overload restores the exact previous angle", rolledBack && fs.Bascule.Leaves.All(l => l.Angle==l.PreviousAngle && l.Angle>=0));
            Check("Hydraulics: fixture is not mutated", File.ReadAllText(fixture)==original);
        }

        // (d) Split a member at its midpoint.
        {
            var ed = new DesignEditor(LoadRef("level1"), l1, db);
            var m = ed.Design.Members.First(x => db.Materials[x.MaterialId].IsDeck);
            double len = m.Length, cost = m.Cost; int count = ed.Design.Members.Count;
            var r = ed.SplitMember(m.Id);
            var halves = ed.Design.Members.Where(x => x.StartNodeId == r.CreatedId || x.EndNodeId == r.CreatedId).ToList();
            Check($"编辑 中点拆分构件：两段长度和 {halves.Sum(h => h.Length):0.000} m（原 {len:0.000}），费用和 ${halves.Sum(h => h.Cost):0.00}（原 ${cost:0.00}）",
                r.Ok && halves.Count == 2 && Math.Abs(halves.Sum(h => h.Length) - len) < 1e-9 && Math.Abs(halves.Sum(h => h.Cost) - cost) < 1e-6 && halves.All(h => h.MaterialId == m.MaterialId));
            ed.Undo();
            Check("编辑 撤销拆分", ed.Design.Members.Count == count && ed.Design.FindMember(m.Id).Length == len);
            var hydM = new DesignEditor(LoadRef("level7"), db.GetLevel("level7"), db);
            Check("编辑 液压杆不能拆分", !hydM.SplitMember(hydM.Design.Members.First(x => x.MaterialId == "hydraulic").Id).Ok);
        }

        // Vehicle axle load fractions must add up to the whole vehicle weight.
        foreach (var v in db.Vehicles.Values)
        {
            double sum = v.Axles.Sum(a => a.LoadFraction) * Math.Max(1, v.Units);
            Check($"车辆 {v.Name} 车轴载荷比例之和 {sum:0.000}", Math.Abs(sum - 1) < 0.01 || Math.Abs(v.Axles.Sum(a => a.LoadFraction) - 1) < 0.01);
        }

        // 单段桥面承载：两端有支撑时，每种桥面在自己的最大长度内都要能承载规定车辆
        //（木桥面只要求轿车，加劲钢桥面与预应力混凝土桥面要求坦克，其余桥面要求重型货车）
        foreach (var deck in db.Materials.Values.Where(m => m.IsDeck))
        {
            // Rail-only decks carry the freight train (road vehicles may not use them)
            string veh = !deck.CarriesCars ? "train" : deck.MaterialId == "deck_wood" ? "car" : deck.MaterialId is "deck_steel" or "deck_psc" ? "tank" : "truck";
            double L = deck.MaximumLength;
            var lv = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(l1, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
            lv.Terrain.RiverWidth = L; lv.AllowedMaterials = db.Materials.Keys.ToList();
            lv.Anchors = new() { new AnchorDefinition { Id = "A", Position = new Vec3d(0, 0), MaxLoad = 1e9, IsRoadStart = true }, new AnchorDefinition { Id = "B", Position = new Vec3d(L, 0), MaxLoad = 1e9, IsRoadEnd = true } };
            lv.TrafficEvents = new() { new TrafficEvent { Time = 0, VehicleId = veh } };
            var ed = new DesignEditor(null, lv, db);
            ed.AddMember(ed.Design.Nodes[0].Id, ed.Design.Nodes[1].Id, deck.MaterialId);
            var r = new BridgeSimulation(ed.Design, lv, db).RunToEnd();
            Check($"桥面 单段 {L:0.#} m {deck.Name}（两端支撑）承载{db.Vehicles[veh].Name}（利用率 {r.MaxUtilization:P0}）", r.IsSuccess && r.MaxUtilization < 0.9, r.FailureMessage);
        }

        // 第一关教学起始桥：只有桥面，试车会断，失败反馈能定位到出问题的构件
        {
            var start = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(_root, "Data/StartDesigns/level1_start.json")));
            var r = new BridgeSimulation(start, l1, db).RunToEnd();
            Check($"教学 第一关起始桥试车失败并给出位置（“{r.Headline}” → {r.TopSuggestion}）",
                !r.IsSuccess && r.InvolvedMemberIds.Count > 0 && !string.IsNullOrEmpty(r.Headline) && !string.IsNullOrEmpty(r.TopSuggestion), r.FailureMessage);
            foreach (var lv in db.Levels)
                Check($"教学 {lv.Id} 只开放 {lv.AllowedMaterials.Count} 种材料且有思路提示", lv.AllowedMaterials.Count(id => db.GetMaterial(id) is { IsTunnel: false, IsLift: false }) <= 9 && !string.IsNullOrEmpty(lv.ReferenceHint));
            Check("教学 前几关只开放少量材料", db.Levels.Take(4).All(l => l.AllowedMaterials.Count <= 6));
            Check("教学 船只只在航道关（第七关）出现", db.Levels.Where(l => l.TrafficEvents.Any(e => e.Type == TrafficEventType.Ship)).Select(l => l.Id).SequenceEqual(new[] { "level7" }));
        }

        // Engineering Score (version 2, uncapped): economy is nonlinear and weighs most, safety bonus is capped
        {
            SimulationResult R(double cost, double util, double t = 20, double wm = double.NaN, double integ = 1) =>
                new SimulationResult { IsSuccess = true, Budget = 12000, ReferenceCost = 10000, TotalCost = cost, MaxUtilization = util,
                    ReferenceTime = 20, CompletionTime = t, WeatherMargin = wm, DeckIntegrity = integ, RiverWidth = 30 };
            var baseR = R(10000, 0.77); BridgeSimulation.ComputeScore(baseR);
            Check($"分 参考造价、裕量 30%、不等待 → 约 1000 分（{baseR.Score}）", baseR.Score >= 950 && baseR.Score <= 1050);
            var cheap = R(5000, 0.77); BridgeSimulation.ComputeScore(cheap);
            var dear = R(20000, 0.77); BridgeSimulation.ComputeScore(dear);
            Check($"分 经济性非线性、上不封顶：造价减半经济分 ×2.8（{cheap.EconomyPoints} / {baseR.EconomyPoints}），造价翻倍只剩约 35%（{dear.EconomyPoints}）",
                Math.Abs(cheap.EconomyPoints / (double)baseR.EconomyPoints - Math.Pow(2, 1.5)) < 0.02 && Math.Abs(dear.EconomyPoints / (double)baseR.EconomyPoints - Math.Pow(0.5, 1.5)) < 0.02);
            Check("分 经济性权重最高", db.Scoring.Weights.Economy > Math.Max(db.Scoring.Weights.Safety, Math.Max(db.Scoring.Weights.Weather, Math.Max(db.Scoring.Weights.Traffic, db.Scoring.Weights.Environment))));
            var safe = R(10000, 0.3); BridgeSimulation.ComputeScore(safe);
            var safer = R(10000, 0.1); BridgeSimulation.ComputeScore(safer);
            Check($"分 安全裕量奖励到合理水平封顶（利用率 30% 与 10% 同分 {safe.SafetyPoints}）", safe.SafetyPoints == safer.SafetyPoints && safe.SafetyPoints > baseR.SafetyPoints);
            var slow = R(10000, 0.77, t: 40); BridgeSimulation.ComputeScore(slow);
            Check($"分 用时是理想时间的 2 倍 → 通行分减半（{slow.TrafficPoints} / {baseR.TrafficPoints}）", Math.Abs(slow.TrafficPoints * 2 - baseR.TrafficPoints) <= 1);
            var broken = R(10000, 0.77, integ: 0.5); BridgeSimulation.ComputeScore(broken);
            Check("分 桥面完整度按比例折算全部分项", Math.Abs(broken.BaseScore * 2 - baseR.BaseScore) <= 3);
            var fail = R(10000, 0.5); fail.IsSuccess = false; BridgeSimulation.ComputeScore(fail);
            Check("分 没通过全部测试没有正式分", fail.Score == 0);
            var disturbed = R(10000, 0.77); disturbed.Disturbance = 30; BridgeSimulation.ComputeScore(disturbed);
            Check("分 扰动整条河宽 → 环境系数降到下限", Math.Abs(disturbed.EnvironmentFactor - db.Scoring.EnvironmentFloor) < 1e-9 && baseR.EnvironmentFactor == 1);
            var weak = R(10000, 0.77, wm: 0.05); BridgeSimulation.ComputeScore(weak);
            Check("分 天气期间裕量小 → 天气分低", weak.WeatherPoints < baseR.WeatherPoints);
            foreach (var lv in db.Levels)
            {
                var rr = new BridgeSimulation(LoadRef(lv.Id), lv, db).RunToEnd();
                Check($"分 {lv.Id} 参考方案约 1000 分（{rr.Score}）", rr.IsSuccess && rr.Score >= 800 && rr.Score <= 1200 && rr.ScoreVersion == db.Scoring.Version);
            }

            // Achievements: configured per level (or chosen from its conditions), each counted once
            var l13s = db.GetLevel("level13");
            var ach = EngineeringScore.AchievementsFor(l13s);
            Check("成就 洪水关提供极端洪水成就；不用星星或收集品", ach.Contains("extreme_flood") && ach.Distinct().Count() == ach.Count);
            var s13 = new BridgeSimulation(LoadRef("level13"), l13s, db); s13.RunToEnd(); int base13 = s13.Result.BaseScore;
            EngineeringScore.EvaluateAchievements(s13);
            double mul = s13.Result.Achievements.Where(x => x.Earned).Aggregate(1.0, (m, x) => m * x.Multiplier);
            Check($"成就 获得的倍率各乘一次（{string.Join("、", s13.Result.Achievements.Select(x => x.Name + (x.Earned ? "✔" : "✘")))}，{base13} → {s13.Result.Score}）",
                s13.Result.Achievements.Count == ach.Count && s13.Result.Score == (int)Math.Round(base13 * mul));
            EngineeringScore.EvaluateAchievements(s13);
            Check("成就 重复评估不会叠加倍率", s13.Result.Score == (int)Math.Round(base13 * mul));
            var flood = s13.Result.Achievements.First(x => x.Id == "extreme_flood");
            Check($"成就 极端洪水用 ×1.4 洪水重测，只看管节强度：参考方案的加强管节扛得住（{flood.Note}）", flood.Earned, flood.Note);
            var weak13 = new DesignEditor(LoadRef("level13"), l13s, db); weak13.SetTunnelGrade(-1, "normal");
            var s13b = new BridgeSimulation(weak13.Design, l13s, db); s13b.RunToEnd();
            Check("成就 普通管节在洪水里强度不够，不能通关", !s13b.Result.IsSuccess && s13b.Result.FailureMessage.Contains("压坏"), s13b.Result.FailureMessage);
            var s1 = new BridgeSimulation(LoadRef("level1"), db.GetLevel("level1"), db); s1.RunToEnd(); EngineeringScore.EvaluateAchievements(s1);
            Check("成就 只有轿车的第一关不提供额外重载（不公平）；有重型货车的第三关提供",
                !EngineeringScore.AchievementsFor(db.GetLevel("level1")).Contains("heavy_load") && EngineeringScore.AchievementsFor(db.GetLevel("level3")).Contains("heavy_load"));
            var s3 = new BridgeSimulation(LoadRef("level3"), db.GetLevel("level3"), db); s3.RunToEnd(); EngineeringScore.EvaluateAchievements(s3);
            var heavy = s3.Result.Achievements.FirstOrDefault(x => x.Id == "heavy_load");
            Check($"成就 额外重载用追加满载重卡重测（{heavy?.Note}）", heavy != null && heavy.Note.Length > 0);
            foreach (var lv in db.Levels)
                Check($"成就 {lv.Id} 至少提供两项成就", EngineeringScore.AchievementsFor(lv).Count >= 2);
            Check("成就 第一关参考方案利用率 25% → 高可靠性", s1.Result.Achievements.Any(x => x.Id == "high_reliability" && x.Earned));

            // Records: best score, lowest cost, fastest, highest margin are kept separately per scoring version
            var rec = new LevelRecord { Version = db.Scoring.Version };
            var k1 = rec.Improve(baseR);
            var k2 = rec.Improve(cheap);
            var k3 = rec.Improve(safe);
            Check($"记录 第一次通过刷新五项；更便宜的方案只刷新最高分 / 最低造价；更安全的只刷新安全裕量（{string.Join(",", k2)} | {string.Join(",", k3)}）",
                k1.Count == 5 && k2.SequenceEqual(new[] { "score", "cost" }) && k3.SequenceEqual(new[] { "margin" }));
        }

        // Custom levels (level design mode): build, validate, save round trip, and a playable bridge
        {
            var spec = new CustomLevelSpec { Name = "测试 关卡/1", RiverWidth = 8, LeftBankHeight = 0, RightBankHeight = 1, WaterDepth = 3, BedDepth = 6, Budget = 20000,
                Materials = new() { "road", "steel" }, Vehicles = new() { "car" }, Anchors = new() { new CustomAnchorSpec { X = 4, Y = 0.5 } } };
            Check("自定义关卡 默认设置通过检查", CustomLevelBuilder.Validate(new CustomLevelSpec(), db).Count == 0);
            Check($"自定义关卡 名称转成安全 id（{CustomLevelBuilder.IdFor(spec.Name)}）", CustomLevelBuilder.IdFor(spec.Name) == "custom_测试_关卡_1" && CustomLevelBuilder.IsCustom("custom_x") && !CustomLevelBuilder.IsCustom("level1"));
            Check("自定义关卡 合法设置无错误", CustomLevelBuilder.Validate(spec, db).Count == 0, string.Join("；", CustomLevelBuilder.Validate(spec, db)));
            var lv = CustomLevelBuilder.Build(spec, db);
            Check("自定义关卡 两岸高度、河道、水面、河床按设置生成", lv.Terrain.RiverWidth == 8 && lv.Terrain.LeftBankHeight == 0 && lv.Terrain.RightBankHeight == 1 && lv.Terrain.WaterLevel == -3 && lv.Terrain.RiverBedDepth == -6);
            Check("自定义关卡 道路起终点 + 岸坡锚点 + 自定义固定节点（河中为岩墩）",
                lv.Anchors.Single(x => x.IsRoadStart).Position.X == 0 && lv.Anchors.Single(x => x.IsRoadEnd).Position.Y == 1 && lv.Anchors.Any(x => x.Id == "C1") && lv.Terrain.Pillars.Count == 1 && lv.IsOnLand(4, 0.5));
            Check("自定义关卡 只开放所选建材与车辆", lv.AllowedMaterials.SequenceEqual(new[] { "road", "steel" }) && lv.TrafficEvents.Single().VehicleId == "car");
            var bad = spec.Clone(); bad.Materials = new() { "steel" }; bad.BedDepth = 2; bad.Anchors.Add(new CustomAnchorSpec { X = -3, Y = 5 });
            var errs = CustomLevelBuilder.Validate(bad, db);
            Check($"自定义关卡 检查出缺桥面、河床太浅、节点悬空（{errs.Count} 条）", errs.Any(x => x.Contains("桥面")) && errs.Any(x => x.Contains("河床")) && errs.Any(x => x.Contains("悬在左岸")));
            var nop = spec.Clone(); nop.NoPiers = true; nop.Anchors[0].Y = -2;
            Check("自定义关卡 禁止建墩时河道里不能放固定节点", CustomLevelBuilder.Validate(nop, db).Any(x => x.Contains("禁止建墩")));
            {
                // Bank wall angle, bed material, dry gully, bank anchors
                var old = System.Text.Json.JsonSerializer.Deserialize<CustomLevelSpec>("{\"Name\":\"旧\",\"RiverWidth\":16}", GameDatabase.JsonOptions);
                Check("自定义关卡 旧关卡读取后岸壁 90°、岩石河床、有水", old.BankSlope == 90 && old.BedSoil == "rock" && old.HasWater);
                var sl = new CustomLevelSpec { RiverWidth = 20, BankSlope = 45, BedSoil = "soft", HasWater = false, BedDepth = 6 };
                sl.Anchors.Add(sl.BankPoint(true, true, 3)); sl.Anchors.Add(sl.BankPoint(false, false, 4));
                var sv = CustomLevelBuilder.Validate(sl, db);
                Check("自定义关卡 45° 岸壁、软土、无水、岸壁 / 平地固定节点合法", sv.Count == 0, string.Join("；", sv));
                var sll = CustomLevelBuilder.Build(sl, db);
                var c1 = sll.Anchors.First(x => x.Id == "C1").Position;
                Check($"自定义关卡 岸壁节点在坡面上（x {c1.X:0.##}, y {c1.Y:0.##}），不生成岩墩", System.Math.Abs(c1.X - 3) < 1e-6 && System.Math.Abs(c1.Y + 3) < 1e-6 && sll.Terrain.Pillars.Count == 0 && sll.IsOnLand(c1.X, c1.Y));
                Check("自定义关卡 无水时水面等于河床，软土河床", !sll.Terrain.HasWater && sll.Terrain.WaterLevel == sll.Terrain.RiverBedDepth && !sll.Terrain.ZoneAt(10).IsRock);
                var sed = new DesignEditor(BridgeDesign.CreateForLevel(sll), sll, db);
                Check($"自定义关卡 坡脚处桥墩从坡面起（{sed.PierBedY(2):0.#} m）", System.Math.Abs(sed.PierBedY(2) + 2) < 1e-6 && System.Math.Abs(sed.PierBedY(10) + 6) < 1e-6);
                var steep = sl.Clone(); steep.BankSlope = 30; steep.RiverWidth = 8; steep.Anchors.Clear();
                Check("自定义关卡 岸壁太缓、坡脚相撞会报错", CustomLevelBuilder.Validate(steep, db).Any(x => x.Contains("岸壁太缓")));
                var shipDry = sl.Clone(); shipDry.Ship = true;
                Check("自定义关卡 无水河道不能有船", CustomLevelBuilder.Validate(shipDry, db).Any(x => x.Contains("无水")));
                var buried = sl.Clone(); buried.Anchors.Add(new CustomAnchorSpec { X = 1, Y = -4 });
                Check("自定义关卡 埋进岸坡的固定节点会报错", CustomLevelBuilder.Validate(buried, db).Any(x => x.Contains("埋在岸坡")));
            }
            var round = System.Text.Json.JsonSerializer.Deserialize<CustomLevelSpec>(System.Text.Json.JsonSerializer.Serialize(spec, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
            Check("自定义关卡 保存读取后内容不变", round.Name == spec.Name && round.Anchors.Single().X == 4 && round.Materials.SequenceEqual(spec.Materials));
            var ed = new DesignEditor(BridgeDesign.CreateForLevel(lv), lv, db);
            int Nid(string anchor) => ed.Design.Nodes.First(n => n.AnchorId == anchor).Id;
            var r1 = ed.AddMember(Nid("L1"), Nid("C1"), "road"); var r2 = ed.AddMember(Nid("C1"), Nid("R1"), "road");
            var cr = new BridgeSimulation(ed.Design, lv, db).RunToEnd();
            Check($"自定义关卡 可以建桥并试车通过（{cr.FailureMessage}）", r1.Ok && r2.Ok && cr.IsSuccess, cr.FailureMessage);
            ed.Design.Name = "我的方案";
            Check("命名方案 名称随方案保存", GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(ed.Design)).Name == "我的方案");
        }

        // Level 10: old-bridge members that would block traffic with a cross beam are opened as two rigid halves,
        // so the level stays passable with only 4 demolitions; repairing costs the same per capacity as new steel
        {
            var l10 = db.GetLevel("level10");
            var startText = File.ReadAllText(Path.Combine(_root, "Data/StartDesigns/level10_start.json"));
            var e10 = new DesignEditor(GameDatabase.DeserializeDesign(startText), l10, db);
            double tall = l10.TrafficEvents.Where(e => e.Type == TrafficEventType.Vehicle && e.VehicleId != null).Select(e => db.Vehicles[e.VehicleId].Height).Max();
            Check("加固 旧桥构件不会因横梁挡车（打开时自动在中点刚性分段）", !e10.Design.Members.Any(m => m.Existing && CrossBeams.Blocks(e10.Design, db, m, tall)) && e10.Design.Nodes.Any(n => n.IsRigidSplit));
            Check("加固 起始桥文件未被修改", File.ReadAllText(Path.Combine(_root, "Data/StartDesigns/level10_start.json")) == startText);
            var oldSteel = e10.Design.Members.First(m => m.Existing && m.MaterialId == "steel" && m.Condition < 1);
            double perCapacity = e10.RepairCostOf(oldSteel) / (1 - oldSteel.Condition), fresh = oldSteel.Length * db.Materials["steel"].CostPerMeter;
            Check($"加固 修复与新建性价比相当（每份承载力 修复 ${perCapacity:0} / 新建 ${fresh:0}）", Math.Abs(perCapacity - fresh) < 1e-6);
        }

        // Rail decks: trains only on rails, road vehicles only on road surface; a swaying track derails the train
        {
            var l8 = db.GetLevel("level8");
            Check("铁路 新桥面：铁轨桥面只走火车、复合桥面火车汽车都能走、普通桥面只走汽车",
                db.Materials["deck_rail"].CarriesTrains && !db.Materials["deck_rail"].CarriesCars
                && db.Materials["deck_rail_road"].CarriesTrains && db.Materials["deck_rail_road"].CarriesCars
                && db.Materials["road"].CarriesCars && !db.Materials["road"].CarriesTrains && db.Materials["deck_steel"].Surface == "road");
            var refSim = new BridgeSimulation(LoadRef("level8"), l8, db); var rr = refSim.RunToEnd();
            Check($"铁路 第八关参考方案（复合桥面）列车平稳通过（铁轨最大下沉 {refSim.MaxRailSag:0.000} m、晃动 {refSim.MaxRailBounce:0.000} m/s）",
                rr.IsSuccess && refSim.MaxRailSag < RailTrack.MaxSag && refSim.MaxRailBounce < RailTrack.MaxBounce, rr.FailureMessage);
            var roadOnly = LoadRef("level8"); foreach (var m in roadOnly.Members.Where(m => m.MaterialId == "deck_rail_road")) m.MaterialId = "road";
            var ro = new BridgeSimulation(roadOnly, l8, db).RunToEnd();
            Check($"铁路 火车不能走普通公路桥面（{ro.FailureMessage.Split('\n')[0]}）", !ro.IsSuccess && ro.FailureReason == FailureReason.WrongSurface && ro.FailureMessage.Contains("货运列车"));
            var railOnly = LoadRef("level8"); foreach (var m in railOnly.Members.Where(m => m.MaterialId == "deck_rail_road")) m.MaterialId = "deck_rail";
            var rs = new BridgeSimulation(railOnly, l8, db); var ra = rs.RunToEnd();
            Check($"铁路 汽车不能走铁轨桥面，火车已先通过（{ra.FailureMessage.Split('\n')[0]}）",
                !ra.IsSuccess && ra.FailureReason == FailureReason.WrongSurface && rs.Vehicles.Any(v => v.Def.Id == "train" && v.Finished) && ra.FailureMessage.Contains("公路路面"));
            var soft = LoadRef("level8"); int n = 0;
            foreach (var m in soft.Members.Where(m => m.MaterialId == "steel")) if (n++ % 2 == 0) m.MaterialId = "cable";
            var ss = new BridgeSimulation(soft, l8, db); var sr = ss.RunToEnd();
            Check($"铁路 桥太软、铁轨晃动太大时列车脱轨并失败（{sr.Headline}）",
                !sr.IsSuccess && sr.FailureReason == FailureReason.Derailed && ss.Vehicles.Any(v => v.Derailed) && sr.InvolvedMemberIds.Count == 1, sr.FailureMessage);
            var cs = new CustomLevelSpec { Vehicles = new() { "train", "car" }, Materials = new() { "deck_rail", "steel" } };
            Check("铁路 自定义关卡有汽车却只有铁轨桥面会报错", CustomLevelBuilder.Validate(cs, db).Any(x => x.Contains("有汽车")));
            cs.Materials = new() { "road", "steel" };
            Check("铁路 自定义关卡有火车却没有铁轨桥面会报错", CustomLevelBuilder.Validate(cs, db).Any(x => x.Contains("有火车")));
            cs.Materials = new() { "deck_rail_road", "steel" };
            Check("铁路 自定义关卡用复合桥面时火车汽车都可以", CustomLevelBuilder.Validate(cs, db).Count == 0, string.Join("；", CustomLevelBuilder.Validate(cs, db)));
        }

        // Level 8: after the freight train has left, the truck starts at once; the test never idles with an empty road
        {
            var l8 = db.GetLevel("level8");
            var s8 = new BridgeSimulation(LoadRef("level8"), l8, db); s8.Initialize();
            double idle = 0, worst = 0, trainDone = -1;
            while (s8.State == SimState.Running)
            {
                s8.Step();
                if (trainDone < 0 && s8.Vehicles.Any(v => v.Def.Id == "train" && v.Finished)) trainDone = s8.Time;
                bool empty = s8.Vehicles.All(v => v.Finished || v.Fallen);
                idle = empty && s8.Time > BridgeSimulation.StaticPhaseTime ? idle + BridgeSimulation.FixedDt : 0;  // the static phase has no traffic by design worst = Math.Max(worst, idle);
            }
            Check($"火车 通过后后续车辆立即出发、测试正常结束（火车 {trainDone:0.0}s 离开，结束 {s8.Time:0.0}s，最长空等 {worst:0.00}s）", s8.State == SimState.Succeeded && trainDone > 0 && worst < 0.1);
        }

        // Player's custom level save: the deck must split at node 9 and the left leaf is lifted by hydraulic #17
        {
            string sp = Path.Combine(_root, "Tests/BridgeTests/Fixtures/custom_level_spec.json"), dp = Path.Combine(_root, "Tests/BridgeTests/Fixtures/custom_level_design.json");
            string specText = File.ReadAllText(sp), designText = File.ReadAllText(dp);
            var cl = CustomLevelBuilder.Build(System.Text.Json.JsonSerializer.Deserialize<CustomLevelSpec>(specText, GameDatabase.JsonOptions), db);
            var cd = GameDatabase.DeserializeDesign(designText);
            var cp = BasculePlanner.Plan(cd, db, cl);
            var cut = cp.CutSeams.Select(i => cp.Seams[i].LeftKey).ToList();
            var g = cp.Groups.SingleOrDefault();
            Check($"自定义存档 只在节点 9 断开（分离 {string.Join(",", cut)}）", cut.SequenceEqual(new[] { 9 }));
            Check($"自定义存档 左桥叶绕节点 8 转动，由液压杆 #17 驱动（转轴 {g?.PivotKey}）", g != null && g.PivotKey == 8 && g.Actuators.Single().MemberId == 17 && g.LeafKeys.Contains(13) && !g.LeafKeys.Contains(8));
            var csim = new BridgeSimulation(cd, cl, db); var cres = csim.RunToEnd();
            Check($"自定义存档 液压杆能把桥叶开到目标角（{csim.Bascule.StatusText}）", csim.Bascule.Fault == null && csim.Bascule.Leaves.Single().ReachAngle >= csim.Bascule.Leaves.Single().OpenAngleDeg - 1e-9);
            // The right half stays fixed inside the 6.5 m channel; the remaining collision is the design, not the mechanism
            Check($"自定义存档 剩余撞船来自右侧固定结构并被指出（{cres.FailureMessage}）", !cres.IsSuccess && cres.FailureReason == FailureReason.ShipCollision && csim.Bascule.NavigationNote != null);
            var ce = new DesignEditor(cd.Clone(), cl, db);
            var hr = ce.GetHydraulicRange(17);
            Check($"自定义存档 液压杆 #17 行程范围按结构自动计算（{hr.MinPct:P0} ~ {hr.MaxPct:P0}）", hr.Valid && hr.Leaf && hr.MinPct < -0.05 && hr.MaxPct >= 0 && hr.MinPct > -LinkageDriverData.MaxStroke - 1e-9 && hr.CurrentPct >= hr.MinPct - 1e-9 && hr.CurrentPct <= hr.MaxPct + 1e-9);
            var tr = ce.SetHydraulicTarget(17, hr.MinPct / 2);
            var leafDef = ce.Design.Mechanisms.Leaves.SingleOrDefault(l => l.ActuatorMemberId == 17);
            var hr2 = ce.GetHydraulicRange(17);
            Check($"自定义存档 滑块目标换算成开启角（{tr.Message}）", tr.Ok && leafDef != null && leafDef.OpenAngleDeg > 5 && leafDef.OpenAngleDeg < 70 && Math.Abs(hr2.CurrentPct - hr.MinPct / 2) < 0.02);
            // Dragging repeatedly must keep the pivot, so every slider value really sets that length
            {
                var se = new DesignEditor(cd.Clone(), cl, db); bool allOk = true; string log = "";
                foreach (var t in new[] { -0.2, -0.5, -0.9, -0.3 })
                {
                    var rr = se.SetHydraulicTarget(17, t); var h = se.GetHydraulicRange(17);
                    var ss = new BridgeSimulation(se.Design, cl, db); ss.RunToEnd();
                    var lf = ss.Bascule.Leaves.Single();
                    log += $"{t:P0}->{h.CurrentPct:P0}/{lf.Angle:0}° ";
                    allOk &= rr.Ok && Math.Abs(h.CurrentPct - t) < 0.02 && lf.Plan.PivotKey == 8 && Math.Abs(lf.Angle - lf.OpenAngleDeg) < 1e-6;
                }
                Check($"自定义存档 连续拖动滑块每次都改变液压杆长度且转轴不变（{log}）", allOk);
            }
            {
                var ve = new DesignEditor(cd.Clone(), cl, db); ve.SetHydraulicTarget(17, -0.5);
                double sf = DesignEditor.SleeveFractions(ve.Design, cl, db)[17];
                Check($"自定义存档 桥叶液压杆黄色外筒随滑块变为最短长度（-50% → {sf:P0}）", Math.Abs(sf - 0.5) < 0.02);
            }
            Check("自定义存档 夹具文件未被修改", File.ReadAllText(sp) == specText && File.ReadAllText(dp) == designText);
        }

        // Height clearance: no cross beam between the two side trusses inside the vehicle envelope above the road
        {
            var hl = db.GetLevel("level3");
            double lim = HeightClearance.Limit(hl, db);
            var hd = new BridgeDesign();
            void N(int id, double x, double y) => hd.Nodes.Add(new BridgeNode { Id = id, Position = new Vec3d(x, y, 0) });
            void M(int id, int a1, int b1, string mat) => hd.Members.Add(new BridgeMember { Id = id, StartNodeId = a1, EndNodeId = b1, MaterialId = mat });
            N(1, 0, 0); N(2, 10, 0); N(3, 5, 2); N(4, 5, lim + 0.5); N(5, 3, lim + 2); N(6, 5, -2);
            M(1, 1, 2, "road"); M(2, 1, 3, "steel"); M(3, 3, 2, "steel"); M(4, 3, 4, "steel"); M(5, 4, 5, "cable"); M(6, 1, 6, "steel");
            bool Tie(int id) => HeightClearance.NeedsCrossTie(hd, db, hl, hd.FindNode(id));
            Check($"限高 {lim:0.0} m 不低于本关最高车辆 + 0.3 m", lim >= HeightClearance.Default && hl.TrafficEvents.Where(e => e.VehicleId != null && db.Vehicles.ContainsKey(e.VehicleId)).All(e => db.Vehicles[e.VehicleId].Height + 0.3 <= lim + 1e-9));
            Check("限高 桥面节点与桥面下节点有横梁（横隔梁）", Tie(1) && Tie(2) && Tie(6));
            Check("限高 桥面上方限高以内的节点不设横梁", !Tie(3));
            Check("限高 高于限高的节点设顶部横梁", Tie(4));
            Check("限高 只连钢索的节点不需要横梁", !Tie(5) && db.GetMaterial("cable").SideOnly && db.GetMaterial("hydraulic").SideOnly);
        }

        // 性能基准（只打印耗时，不判定）
        {
            var l3 = db.GetLevel("level5");
            var d3 = LoadRef("level5");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int nStatic = 50;
            for (int i = 0; i < nStatic; i++) BridgeSimulation.StaticAnalysis(d3, l3, db);
            double staticMs = sw.Elapsed.TotalMilliseconds / nStatic;
            var sim = new BridgeSimulation(d3, l3, db); sim.Initialize();
            sw.Restart(); int steps = 0;
            while (sim.State == SimState.Running) { sim.Step(); steps++; }
            double stepMs = sw.Elapsed.TotalMilliseconds / Math.Max(1, steps);
            Console.WriteLine($"[性能] 第五关参考桥（{d3.Nodes.Count} 节点 / {d3.Members.Count} 构件）：建造期自重分析 {staticMs:0.00} ms/次；测试 {stepMs:0.000} ms/步，"
                              + $"1× 每帧约 {stepMs:0.00} ms，4× 每帧约 {stepMs * 4:0.00} ms（60 帧预算 16.7 ms）");
        }

        // 新车辆与新材料
        {
            var l3 = db.GetLevel("level3");
            LevelDefinition WithTraffic(LevelDefinition b, params string[] veh)
            {
                var lv = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(b, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
                lv.TrafficEvents = veh.Select((v, i) => new TrafficEvent { Time = i * 0.5, VehicleId = v }).ToList();
                lv.AllowedMaterials = db.Materials.Keys.ToList();
                return lv;
            }
            var refTruck = LoadRef("level3");
            var rTank = new BridgeSimulation(refTruck, WithTraffic(l3, "tank"), db).RunToEnd();
            var rTruck = new BridgeSimulation(refTruck, WithTraffic(l3, "truck"), db).RunToEnd();
            Check($"车 同一座货车桥：坦克（单点 60 吨）比货车危险得多（货车 {rTruck.MaxUtilization:P0}，坦克 {(rTank.IsSuccess ? rTank.MaxUtilization.ToString("P0") : "压断")}）", !rTank.IsSuccess || rTank.MaxUtilization > rTruck.MaxUtilization * 1.3);
            var rPed = new BridgeSimulation(refTruck, WithTraffic(l3, "pedestrians"), db).RunToEnd();
            Check($"车 行人队伍很轻（利用率 {rPed.MaxUtilization:P0}）", rPed.IsSuccess && rPed.MaxUtilization < rTruck.MaxUtilization);

            // 火车：长时间高负载
            var l8 = db.GetLevel("level8");
            var simT = new BridgeSimulation(LoadRef("level8"), l8, db); simT.Initialize();
            double loadedTime = 0;
            while (simT.State == SimState.Running) { simT.Step(); if (simT.CurrentMaxUtilization > 0.5) loadedTime += BridgeSimulation.FixedDt; }
            Check($"车 货运列车让桥长时间处于高负载（利用率 > 50% 持续 {loadedTime:0.0} s）且参考桥能通过", simT.Result.IsSuccess && loadedTime > 5, simT.Result.FailureMessage);

            // 车队：同一道路上后车不会穿过前车
            var simC = new BridgeSimulation(refTruck, WithTraffic(l3, "car", "car", "car", "car"), db); simC.Initialize();
            bool overlap = false;
            while (simC.State == SimState.Running)
            {
                simC.Step();
                for (int i = 1; i < simC.Vehicles.Count; i++)
                {
                    var a = simC.Vehicles[i - 1]; var b = simC.Vehicles[i];
                    if (!a.Finished && !b.Finished && b.FrontS > a.FrontS + a.RearOffset - 0.5) overlap = true;
                }
            }
            Check("车 车队保持跟车距离、不重叠", !overlap && simC.Result.IsSuccess, simC.Result.FailureMessage);

            // 立交：上层道路的车走上层桥面
            var sim9 = new BridgeSimulation(LoadRef("level9"), db.GetLevel("level9"), db); sim9.Initialize();
            Check($"立交 生成两层行驶路径（{sim9.Paths.Count} 条），上层车辆走上层", sim9.Paths.Count == 2 && sim9.PathFor(1).PointY.Max() > 5);

            // 钢筋混凝土梁：先开裂、再破坏
            var crackLv = WithTraffic(db.GetLevel("level1"), "tank", "tank");
            var edc = new DesignEditor(null, crackLv, db);
            int la = edc.Design.Nodes.First(n => n.AnchorId == "L1").Id, ra = edc.Design.Nodes.First(n => n.AnchorId == "R1").Id;
            int lb = edc.Design.Nodes.First(n => n.AnchorId == "L2").Id, rb = edc.Design.Nodes.First(n => n.AnchorId == "R2").Id;
            int m1 = edc.Design.FindMember(edc.AddMemberToNewNode(la, 4, 0, "road").CreatedId).EndNodeId;
            int m2 = edc.Design.FindMember(edc.AddMemberToNewNode(m1, 8, 0, "road").CreatedId).EndNodeId;
            edc.AddMember(m2, ra, "road");
            edc.AddMember(lb, m1, "rc_beam"); edc.AddMember(rb, m2, "rc_beam");
            var simR = new BridgeSimulation(edc.Design, crackLv, db);
            int cracks = 0; simR.MemberCracked += _ => cracks++;
            simR.RunToEnd();
            Check($"材料 钢筋混凝土梁在重载下先出现开裂提示（{cracks} 根开裂，最大利用率 {simR.Result.MaxUtilization:P0}）", cracks > 0);

            Check("材料 预应力 I 形梁有上托荷载（放置后上拱）", db.Materials["psc_i"].PrestressLoad > db.Materials["psc_i"].MassPerMeter * StructuralModel.Gravity);

            // 胶合木梁限量
            var edg = new DesignEditor(null, db.GetLevel("level2"), db);
            int g0 = edg.Design.Nodes.First(n => n.AnchorId == "L1").Id;
            var g1 = edg.AddMemberToNewNode(g0, 0, 6, "glulam"); var g2 = edg.AddMemberToNewNode(g0, 6, 6, "glulam"); var g3 = edg.AddMemberToNewNode(g0, -6, 6, "glulam");
            Check($"材料 胶合木梁限量 2 根（第 3 根被拒绝：{g3.Message}）", g1.Ok && g2.Ok && !g3.Ok);

            // 禁止建墩区
            var ed7 = new DesignEditor(null, db.GetLevel("level7"), db);
            Check("地形 禁止建墩区内不能在桥面以下放节点", ed7.CheckNodePosition(12, -2) != null && ed7.CheckNodePosition(12, 3) == null);
        }

        // 旧桥加固关
        {
            var l10 = db.GetLevel("level10");
            var old = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(_root, "Data/StartDesigns/level10_start.json")));
            var ed = new DesignEditor(old.Clone(), l10, db);
            Check($"加固 旧桥原有构件不计费（{ed.Design.Members.Count(m => m.Existing)} 根旧构件，花费 ${ed.UsedCost:0}）", ed.UsedCost == 0 && ed.Design.Members.All(m => m.Existing));
            LevelDefinition Only(params string[] veh)
            {
                var lv = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(l10, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
                lv.TrafficEvents = veh.Select((v, i) => new TrafficEvent { Time = i * 6, VehicleId = v }).ToList();
                return lv;
            }
            var rCar = new BridgeSimulation(old, Only("car"), db).RunToEnd();
            var rTruck = new BridgeSimulation(old, l10, db).RunToEnd();
            Check($"加固 旧桥能过轿车（{rCar.MaxUtilization:P0}），过不了货车（{rTruck.Headline}）", rCar.IsSuccess && !rTruck.IsSuccess, rCar.FailureMessage);
            var deckNode = ed.Design.Nodes.First(n => !n.IsAnchor && Math.Abs(n.Position.Y) < 0.01);
            Check("加固 旧桥节点不能移动", !ed.MoveNode(deckNode.Id, deckNode.Position.X, 1).Ok);
            var weakest = ed.Design.Members.First(m => m.MaterialId == "steel");
            double before = ed.UsedCost;
            var rep = ed.RepairMember(weakest.Id);
            Check($"加固 修复旧构件：状况恢复 100%，花费 ${ed.UsedCost - before:0}", rep.Ok && ed.Design.FindMember(weakest.Id).Condition == 1 && ed.UsedCost > before);
            ed.Undo();
            var oldIds = ed.Design.Members.Where(m => m.Existing && m.MaterialId == "steel").Select(m => m.Id).Take(5).ToList();
            int ok = 0; foreach (var id in oldIds) if (ed.DeleteMember(id).Ok) ok++;
            Check($"加固 旧构件最多拆除 4 根（第 5 根被拒绝），拆除费计入花费 ${ed.UsedCost:0}", ok == 4 && ed.Design.DemolishedCount == 4 && ed.UsedCost > 0);
            var refd = LoadRef("level10");
            var rr = new BridgeSimulation(refd, l10, db).RunToEnd();
            var edr = new DesignEditor(refd, l10, db);
            Check($"加固 参考方案（修复 + 斜撑）让货车通过（花费 ${edr.UsedCost:0}/{l10.Budget:0}，利用率 {rr.MaxUtilization:P0}）", rr.IsSuccess && edr.UsedCost <= l10.Budget, rr.FailureMessage);
            var json = GameDatabase.SerializeDesign(edr.Design);
            var back = new DesignEditor(GameDatabase.DeserializeDesign(json), l10, db);
            Check("加固 存档保留旧构件、状况与拆除 / 修复花费", Math.Abs(back.UsedCost - edr.UsedCost) < 1e-6 && back.Design.Members.Count(m => m.Existing) == edr.Design.Members.Count(m => m.Existing));
        }

        // 桥墩、地基与洪水
        {
            var l11 = db.GetLevel("level11"); var l12 = db.GetLevel("level12");
            var ed = new DesignEditor(null, l11, db);
            var bad = ed.AddPier(-2, 0, "pier_concrete");
            Check($"墩 桥墩只能建在河道内（{bad.Message}）", !bad.Ok);
            var pr = ed.AddPier(10, 0, "pier_concrete");
            var baseN = ed.Design.FindNode(pr.CreatedId);
            int pierNodes = ed.Design.Nodes.Count(n => n.PierBase || n.PierPart);
            Check($"墩 一次生成墩底 + 中部 + 顶部支点（{pierNodes} 个节点、{ed.Design.Members.Count} 根墩身）", pr.Ok && pierNodes == 3 && ed.Design.Members.Count == 2 && baseN.Foundation == FoundationType.Shallow);
            double c0 = ed.UsedCost; ed.SetFoundation(baseN.Id, FoundationType.Pile);
            Check($"墩 桩基础更贵（+${ed.UsedCost - c0:0}）", ed.UsedCost - c0 == FoundationRules.PileCost - FoundationRules.ShallowCost);
            var top = ed.Design.Nodes.First(n => n.PierPart && n.Position.Y > -1);
            Check("墩 顶部支点可以上下移动", ed.MoveNode(top.Id, top.Position.X, 1).Ok);
            Check("墩 墩底基础不能移动", !ed.MoveNode(baseN.Id, 11, -8).Ok);
            var rock = FoundationRules.Capacity(l11.Terrain.ZoneAt(5), FoundationType.Shallow);
            var softS = FoundationRules.Capacity(l11.Terrain.ZoneAt(30), FoundationType.Shallow);
            var softP = FoundationRules.Capacity(l11.Terrain.ZoneAt(30), FoundationType.Pile);
            Check($"地基 软土浅基础承载低（{softS / 1000:0} kN），桩基础高（{softP / 1000:0} kN），岩石浅基础高（{rock / 1000:0} kN）", softS < rock && softS < softP);
            ed.DeleteNode(baseN.Id);
            Check("墩 删除墩底节点会拆掉整座桥墩", !ed.Design.Nodes.Any(n => n.PierBase || n.PierPart) && ed.Design.Members.Count == 0);
            var r11 = new BridgeSimulation(LoadRef("level11"), l11, db).RunToEnd();
            Check($"墩 第十一关车队（空载厢车 / 货车 / 满载重卡）通过：{r11.PassedVehicles}/{r11.VehicleResults.Count}", r11.IsSuccess && r11.PassedVehicles == 4, r11.FailureMessage);
            // 把软土上的桩基础改成浅基础 → 地基承受不住
            var d11 = LoadRef("level11");
            foreach (var n in d11.Nodes.Where(n => n.PierBase)) n.Foundation = FoundationType.Shallow;
            var r11b = new BridgeSimulation(d11, l11, db).RunToEnd();
            Check($"地基 软土上用浅基础会失败（{r11b.Headline}）", !r11b.IsSuccess && r11b.FailureReason == FailureReason.AnchorOverload);
            // 洪水
            var r12 = new BridgeSimulation(LoadRef("level12"), l12, db).RunToEnd();
            Check($"洪水 岩石区混凝土墩扛住洪水，货车通过（{r12.MaxUtilization:P0}）", r12.IsSuccess, r12.FailureMessage);
            var dWood = LoadRef("level12");
            foreach (var m in dWood.Members.Where(m => m.MaterialId == "pier_concrete")) m.MaterialId = "pier_wood";
            var rW = new BridgeSimulation(dWood, l12, db).RunToEnd();
            Check($"洪水 木桥墩被洪水冲毁（{rW.Headline}）", !rW.IsSuccess && rW.FailureReason == FailureReason.FloodScour, rW.FailureMessage);
            {
                var flood = l12.TrafficEvents.First(e => e.Type == TrafficEventType.Environment);
                var dw2 = LoadRef("level12"); foreach (var m in dw2.Members.Where(m => m.MaterialId == "pier_concrete")) m.MaterialId = "pier_wood";
                var sw = new BridgeSimulation(dw2, l12, db); sw.Initialize();
                double washT = -1, riseAtWash = 0;
                while (sw.State == SimState.Running && washT < 0)
                {
                    sw.Step();
                    if (sw.Events.Any(ev => ev.Mode == FailureReason.FloodScour && ev.MemberId >= 0)) { washT = sw.Time; riseAtWash = sw.FloodRise; }
                }
                Check($"洪水 木桥墩在水位涨上来后才被冲走（洪水 {flood.Time:0}s 开始，{washT:0.00}s 冲走，此时水位已涨 {riseAtWash:0.00} m）", washT > flood.Time + 0.1 && riseAtWash > 0.1 && washT <= flood.Time + BridgeSimulation.FloodRampTime + 1e-6);
            }
            var e12 = new DesignEditor(LoadRef("level12"), l12, db);
            foreach (var b in e12.Design.Nodes.Where(n => n.PierBase).Select(n => n.Id).ToList()) e12.DeleteNode(b);
            var ps = e12.AddPier(15, 0, "pier_concrete");
            var rS = new BridgeSimulation(e12.Design, l12, db).RunToEnd();
            e12.SetFoundation(ps.CreatedId, FoundationType.Pile);
            var rP = new BridgeSimulation(e12.Design, l12, db).RunToEnd();
            Check($"洪水 冲刷区浅基础被掏空（{rS.Headline}），改桩基础后通过", !rS.IsSuccess && rP.IsSuccess, rS.FailureMessage + " / " + rP.FailureMessage);
        }
        // 坡道：网格上“刚好 15%”的坡不能被误判；15%~30° 减速爬上去；超过 30°（≈57.7%）才爬不上去
        foreach (var (rise, expectPass) in new[] { (0.5, true), (0.75, true), (1.2, true), (1.85, true), (2.2, false) })
        {
            var lv = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(db.GetLevel("level1"), GameDatabase.JsonOptions), GameDatabase.JsonOptions);
            lv.Terrain.RiverWidth = 10 / 3.0; lv.Terrain.RightBankHeight = rise; lv.AllowedMaterials = db.Materials.Keys.ToList();
            lv.Anchors = new() { new AnchorDefinition { Id = "A", Position = new Vec3d(0, 0), MaxLoad = 1e9, IsRoadStart = true }, new AnchorDefinition { Id = "B", Position = new Vec3d(10 / 3.0, rise), MaxLoad = 1e9, IsRoadEnd = true } };
            lv.TrafficEvents = new() { new TrafficEvent { Time = 0, VehicleId = "car" } };
            var ed = new DesignEditor(null, lv, db);
            ed.AddMember(ed.Design.Nodes[0].Id, ed.Design.Nodes[1].Id, "road");
            var r = new BridgeSimulation(ed.Design, lv, db).RunToEnd();
            Check($"坡道 坡度 {rise / (10 / 3.0) * 100:0.###}%：{(expectPass ? "能通过" : "爬不上去")}（{(r.IsSuccess ? "通过" : r.FailureMessage.Split('\n')[0])}）", r.IsSuccess == expectPass);
        }

        // 测试 D：载荷差异（同一桥梁，货车 vs 轿车）
        {
            var d = LoadRef("level3");
            double car = MaxUtilWith(db, l2, d, "car");
            double truck = MaxUtilWith(db, l2, d, "truck");
            Check($"D 货车受力需求高于轿车（轿车 {car:P0}，货车 {truck:P0}）", truck > car * 2);
        }

        // 测试 E：车辆通过 + 通关（第二关参考方案：轿车 + 货车）
        {
            var r = new BridgeSimulation(LoadRef("level3"), l2, db).RunToEnd();
            Check($"E 全部车辆通过（{r.PassedVehicles}/{r.VehicleResults.Count}）", r.IsSuccess && r.PassedVehicles == r.VehicleResults.Count, r.FailureMessage);
        }

        // 测试 F：失败修复——用木梁版第一关方案跑货车级别的第二关会失败；修改后重新测试成功
        {
            var ed = new DesignEditor(null, l2, db);
            int a = ed.Design.Nodes.First(n => n.AnchorId == "L1").Id;
            int b = ed.Design.Nodes.First(n => n.AnchorId == "R1").Id;
            int prev = a;
            for (int x = 4; x <= 16; x += 4) { var r0 = ed.AddMemberToNewNode(prev, x, 0, "road"); prev = ed.Design.FindMember(r0.CreatedId).EndNodeId; }
            ed.AddMember(prev, b, "road");
            var fail = new BridgeSimulation(ed.Design, l2, db).RunToEnd();
            Check("F 有缺陷的设计测试失败", !fail.IsSuccess, fail.FailureMessage);
            Check("F 测试不修改原始方案", ed.Design.Members.All(m => m.DamageState == DamageState.Intact));
            ed.ReplaceDesign(LoadRef("level3"));
            var ok = new BridgeSimulation(ed.Design, l2, db).RunToEnd();
            Check("F 修改设计后重新测试成功", ok.IsSuccess, ok.FailureMessage);
        }

        // 测试 G：保存 / 加载
        {
            var ed = new DesignEditor(LoadRef("level5"), db.GetLevel("level5"), db);
            string json = GameDatabase.SerializeDesign(ed.Design);
            var ed2 = new DesignEditor(GameDatabase.DeserializeDesign(json), db.GetLevel("level5"), db);
            Check("G 保存加载后节点/构件/预算一致",
                ed2.Design.Nodes.Count == ed.Design.Nodes.Count && ed2.Design.Members.Count == ed.Design.Members.Count
                && Math.Abs(ed2.UsedCost - ed.UsedCost) < 1e-6
                && ed2.Design.Members.All(m => ed.Design.FindMember(m.Id)?.MaterialId == m.MaterialId));
        }

        // Deck ranking (load / self weight / cost) follows the design table:
        // wood 2/1/1, grating 3/2/3, RC 4/5/2, prestressed 5/4/3, stiffened steel 5/2/4, FRP 3-5/1/5
        {
            double Cap(string id) => MaterialRatings.DeckCapacityTonnes(db.Materials[id]);
            double W(string id) => db.Materials[id].MassPerMeter; double C(string id) => db.Materials[id].CostPerMeter;
            Check("桥面 承载：木 < 格栅 < FRP / 钢筋混凝土 < 预应力 / 加劲钢", Cap("deck_wood") < Cap("deck_grating") && Cap("deck_grating") < Cap("deck_frp") && Cap("deck_grating") < Cap("road") && Cap("road") < Cap("deck_psc") && Cap("road") < Cap("deck_steel"));
            Check("桥面 自重：FRP / 木最轻，格栅 / 加劲钢次之，预应力较重，钢筋混凝土最重", W("deck_frp") < W("deck_grating") && W("deck_wood") < W("deck_grating") && W("deck_steel") < W("deck_psc") && W("deck_psc") < W("road"));
            Check("桥面 成本：木 < 钢筋混凝土 < 格栅 / 预应力 < 加劲钢 < FRP", C("deck_wood") < C("road") && C("road") < C("deck_grating") && C("road") < C("deck_psc") && C("deck_psc") < C("deck_steel") && C("deck_grating") < C("deck_steel") && C("deck_steel") < C("deck_frp"));
        }

        // Underwater tunnel (level 13: immersed tube)
        {
            var l13 = db.GetLevel("level13");
            var tcfg = db.TunnelConfig;
            LevelDefinition Copy(LevelDefinition lv, bool flood, params string[] vehicles)
            {
                var c = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(lv, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
                c.TrafficEvents.RemoveAll(e => (e.Type == TrafficEventType.Environment && !flood) || e.Type == TrafficEventType.Vehicle);
                for (int i = 0; i < vehicles.Length; i++) c.TrafficEvents.Add(new TrafficEvent { Time = i, VehicleId = vehicles[i] });
                return c;
            }
            // Full-width tube of one grade; elements whose middle lies in [r0, r1] get ground reinforcement
            DesignEditor Tube(LevelDefinition lv, string grade, double r0 = 1, double r1 = 0)
            {
                // Structural tube: piers on piles under joints 2 and 4 (20 m spans) carry it
                var e = TunnelStructureTests.Immersed(db, lv, grade, new[] { 2, 4 }, FoundationType.Pile);
                e.SetPortalProtection(raise: 1.5);
                return e;
            }
            SimulationResult Run(DesignEditor e, LevelDefinition lv) => new BridgeSimulation(e.Design, lv, db).RunToEnd();

            var refSim = new BridgeSimulation(new DesignEditor(LoadRef("level13"), l13, db).Design, l13, db);
            var refRes = refSim.RunToEnd();
            Check("隧道 参考方案的车辆都从隧道通过", refRes.IsSuccess && refSim.Vehicles.Count == 3 && refSim.Vehicles.All(v => v.Lane == TunnelRules.Lane && v.Finished), refRes.FailureMessage);
            Check("隧道 参考方案在预算内", new DesignEditor(LoadRef("level13"), l13, db).UsedCost <= l13.Budget);
            var old = GameDatabase.DeserializeDesign("{\"Version\":3,\"Nodes\":[],\"Members\":[]}");
            Check("隧道 旧存档（版本 3）可读取，没有隧道", old.Tunnel == null && old.Version == BridgeDesign.CurrentVersion);
            var v4 = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(_root, "Data/ReferenceDesigns/legacy/level13_reference_v_before_tunnel_types.json")));
            var v4ed = new DesignEditor(v4, l13, db);
            Check("隧道 旧版隧道段存档（版本 4）迁移为一条路线：钢筋混凝土段→加强、钢壳段→重型",
                v4ed.Design.Tunnel != null && v4ed.TunnelGeo is { } vg && vg.Count == 6 && vg.GradeId[0] == "reinforced" && vg.GradeId[5] == "heavy" && TunnelRules.Complete(vg, l13.Terrain));

            var ed = new DesignEditor(null, l13, db);
            var e0 = new DesignEditor(null, l13, db); var any = e0.AddTunnel(-5, 10, "tunnel_immersed");
            Check("隧道 点击一次就铺满整个河底，两端贴合岸壁", any.Ok && e0.TunnelGeo.Points[0].X == 0 && e0.TunnelGeo.Points[^1].X == 60, any.Message);
            var endId = e0.Design.Tunnel.PointNodeIds.Select(e0.Design.FindNode).OrderBy(n => n.Position.X).First().Id;
            var endMv = e0.MoveNode(endId, 12, TunnelRules.BedY(l13.Terrain) + 2.5);
            Check("隧道 两端只能沿岸壁上下移动（横向拖动被拉回岸壁）", endMv.Ok && e0.Design.FindNode(endId).Position.X == 0 && Math.Abs(e0.Design.FindNode(endId).Position.Y - (TunnelRules.BedY(l13.Terrain) + 2.5)) < 1e-6, endMv.Message);
            Check("隧道 不是隧道建材不能用隧道工具", !ed.AddTunnel(0, 10, "steel").Ok);
            var t1 = ed.AddTunnel(0, 60, "tunnel_immersed");
            var geo = ed.TunnelGeo;
            var sup = ed.Design.Nodes.Where(n => n.IsTunnelSupport).OrderBy(n => n.Position.X).ToList();
            double roof = geo.Points[0].Y;   // joint connection points sit on the tube bottom
            Check("隧道 自动生成 10 m 标准管节，每道接缝一个连接圆点（在管底，离河床 ≥ 1.5 m）", geo.Points[0].Y >= TunnelRules.BedY(l13.Terrain) + 1.5 - 1e-6 && t1.Ok && geo.Count == 6 && sup.Count == 5 && Math.Abs(sup[0].Position.X - 10) < 1e-6 && Math.Abs(sup[0].Position.Y - roof) < 1e-6, t1.Message);
            var lcfg = LiftStructure.Cfg(db); var liftD = ed.Design.Tunnel.Lift;
            double machine = TunnelRules.Shafts(l13, geo).Sum(s => s.Height * lcfg.ShaftCostPerMeter + lcfg.Drives[liftD.Drive].Cost + liftD.Counterweight * lcfg.CounterweightCostPerTonne);
            double liftMembers = ed.Lifts().SelectMany(L => L.Members).Sum(m => m.EffectiveCost()), ties = ed.Lifts().Sum(L => L.Ties.Count) * lcfg.BraceCost;
            Check("隧道 费用 = 标准管节 + 两岸电梯（平台、吊索、柱子按构件计价，另计驱动、配重、拉结、开挖）", liftD?.Built == 1 && liftMembers > 0 && geo.GradeId.All(x => x == TunnelRules.DefaultGrade) && Math.Abs(ed.UsedCost - 60 * geo.Grade[0].CostPerMeter - machine - liftMembers - ties) < 1e-6, $"{ed.UsedCost} members={liftMembers} machine={machine} ties={ties}");
            Check("隧道 只能有一条隧道", !ed.AddTunnel(10, 30, "tunnel_immersed").Ok);
            Check("隧道 支点不能移动", !ed.MoveNode(sup[0].Id, 11, roof).Ok);
            Check("隧道 接缝圆点不能接桥面", !ed.AddMemberToNewNode(sup[0].Id, 12, roof - 0.5, "road").Ok);
            var col = ed.AddPier(10, roof, "pier_concrete");
            Check("隧道 桥墩工具可以在接缝下面从河床立起桥墩（墩顶就是接缝圆点）", col.Ok && ed.Design.Members.Any(m => m.EndNodeId == sup[0].Id || m.StartNodeId == sup[0].Id), col.Message);
            Check("隧道 有沉管时水下可以加节点（河床以上、管身以外），管身里不行", roof < l13.Terrain.WaterLevel && ed.CheckNodePosition(12, roof - 0.5) == null && ed.CheckNodePosition(12, roof + 2) != null);
            var model = StructuralModel.Build(ed.Design, l13, db);
            int si = model.NodeIndex[sup[0].Id];
            Check("隧道 接缝在模型里不是固定支座（承载全靠玩家的桥墩）", !model.FixX[si] && !model.FixY[si] && !model.IsAnchor[si] && model.TubeNode.Contains(si));
            Check("隧道 支点承载力低于桥墩浅基础（岩石）", tcfg.Types.Values.SelectMany(t => t.Grades.Values).All(g => g.SupportLoad < FoundationRules.Capacity(new FoundationZone { Soil = "rock" }, FoundationType.Shallow)));
            Check("隧道 控制点不能接构件", !ed.AddMemberToNewNode(ed.Design.Tunnel.PointNodeIds[0], 0, -1, "rc_column").Ok);

            // Immersed tubes are straight: no control points; lowering one end (trench) tilts the straight tube
            double c0 = ed.UsedCost;
            var ins = ed.InsertTunnelPoint(30);
            Check("沉管 是直管，不能加控制点弯曲", !ins.Ok && ed.Design.Tunnel.PointNodeIds.Count == 2, ins.Message);
            var endL = ed.Design.Tunnel.PointNodeIds.Select(ed.Design.FindNode).OrderBy(n => n.Position.X).First();
            double ex = endL.Position.X;
            var mv = ed.MoveNode(endL.Id, ex, TunnelRules.BedY(l13.Terrain) + 3.0);
            var g2 = ed.TunnelGeo;
            double mid30 = (g2.Points[0].Y + g2.Points[^1].Y) / 2, lin = g2.Points[0].Y + (g2.Points[^1].Y - g2.Points[0].Y) * (30 - g2.Points[0].X) / (g2.Points[^1].X - g2.Points[0].X);
            Check("沉管 抬高一端后整条直管倾斜（仍是直线）、接缝圆点跟随管底", mv.Ok && g2.Points.Count == 2 && Math.Abs(g2.BottomAt(30) - lin) < 0.05 && g2.Points[0].Y > TunnelRules.BedY(l13.Terrain) + 2.9 && ed.Design.Nodes.Where(n => n.IsTunnelSupport).All(n => Math.Abs(n.Position.Y - g2.BottomAt(n.Position.X)) < 1e-6) && ed.Design.Nodes.Count(n => n.IsTunnelSupport) == g2.Count - 1, $"{mv.Message} cost={ed.UsedCost}/{c0}");
            Check("沉管 管底不能低于河床 + 1.5 m（下面要留出支撑空间），也不能挖槽", !ed.MoveNode(endL.Id, ex, TunnelRules.BedY(l13.Terrain) + 1.0).Ok || ed.Design.FindNode(endL.Id).Position.Y >= TunnelRules.BedY(l13.Terrain) + 1.5 - 1e-6);
            ed.Undo();
            Check("隧道 路线修改可撤销", ed.Design.Tunnel.PointNodeIds.Count == 2 && Math.Abs(ed.UsedCost - c0) < 1e-6);
            var c1 = ed.UsedCost; var gr = ed.SetTunnelGrade(2, "heavy");
            Check("隧道 单节升级管节等级，费用增加", gr.Ok && ed.TunnelGeo.GradeId[2] == "heavy" && ed.UsedCost > c0);
            ed.SetTunnelReinforced(2, true);
            var round = GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(ed.Design));
            var rg = TunnelGeometry.Build(round, db);
            Check("隧道 存档可保存 / 读取（路线、管节等级、接缝圆点、结构版本）", rg != null && rg.Count == 6 && rg.GradeId[2] == "heavy" && round.Nodes.Count(n => n.IsTunnelSupport) == 5 && round.Tunnel.Structure == 1);
            int before = ed.Design.Members.Count; var tunnelNodes = ed.Design.Nodes.Where(n => n.IsTunnelNode).Select(n => n.Id).ToHashSet();
            var del = ed.DeleteTunnel();
            Check("隧道 删除隧道连同接缝圆点和接在上面的构件", del.Ok && ed.Design.Tunnel == null && !ed.Design.Nodes.Any(n => n.IsTunnelNode) && !ed.Design.Members.Any(m => tunnelNodes.Contains(m.StartNodeId) || tunnelNodes.Contains(m.EndNodeId)));
            ed.Undo();
            Check("隧道 删除可撤销", ed.Design.Tunnel != null && ed.Design.Members.Count == before);

            // Load models (each has its own failure message)
            var press = Run(Tube(l13, "normal", 20, 60), l13);
            Check("隧道 洪水抬高水位，普通管节水压超限进水", !press.IsSuccess && press.FailureReason == FailureReason.TunnelFlooded && press.FailureMessage.Contains("水压"), press.FailureMessage);
            var calm = Copy(l13, false, "car", "truck", "semi_long");
            var calmNormal = Run(Tube(calm, "normal", 20, 60), calm);
            Check("隧道 没有洪水时普通管节水压够用", calmNormal.IsSuccess, calmNormal.FailureMessage);
            var joint = Run(Tube(calm, "reinforced"), calm);
            Check("隧道 软土与岩石交界不再因沉降 / 接缝漏水失败（只看材料强度）", joint.IsSuccess, joint.FailureMessage);
            var light = Copy(l13, false, "car");
            var lightRes = Run(Tube(light, "reinforced"), light);
            Check("隧道 只有轿车时软土地基不加固也能通过", lightRes.IsSuccess, lightRes.FailureMessage);
            var scour = Run(Tube(l13, "reinforced", 20, 40), l13);
            Check("隧道 洪水冲刷不再掏空地基（只看材料强度）", scour.IsSuccess, scour.FailureMessage);
            // Burying the scour-zone elements ≥ 2 m also protects them
            var deep = Tube(l13, "reinforced", 20, 40);
            deep.InsertTunnelPoint(40); deep.InsertTunnelPoint(56);
            foreach (var pid in deep.Design.Tunnel.PointNodeIds.ToList())
            {
                var pn = deep.Design.FindNode(pid);
                if (pn.Position.X > 39) deep.MoveNode(pid, pn.Position.X, TunnelRules.BedY(l13.Terrain) - 3);
            }
            var deepRes = Run(deep, l13);
            Check("隧道 冲刷区管节埋深 ≥ 2 m 时不被掏空", deepRes.IsSuccess || !deepRes.FailureMessage.Contains("岩石承受不住"), deepRes.FailureMessage);

            var part = new DesignEditor(null, l13, db); part.AddTunnel(0, 40, "tunnel_immersed"); part.SetTunnelGrade(-1, "reinforced");
            foreach (int j in new[] { 2, 4 }) { var jn = part.Design.Nodes.First(n => n.TunnelJoint == j); part.SetFoundation(part.AddPier(jn.Position.X, jn.Position.Y, "pier_concrete").CreatedId, FoundationType.Pile); }
            var partRes = Run(part, l13);
            Check("隧道 不会出现只建一半的隧道：总是贯通两岸并能通车", TunnelRules.Complete(part.TunnelGeo, l13.Terrain) && partRes.IsSuccess, partRes.FailureReason + " " + partRes.FailureMessage);
            var st = TunnelAnalysis.Evaluate(Tube(l13, "normal").TunnelGeo, l13, tcfg, TunnelAnalysis.StaticEnvironment(l13, tcfg));
            Check("隧道 风险分四档（绿 / 黄 / 橙 / 红）", TunnelRules.RiskBand(tcfg, 0.2) == 0 && TunnelRules.RiskBand(tcfg, 0.8) == 1 && TunnelRules.RiskBand(tcfg, 0.95) == 2 && TunnelRules.RiskBand(tcfg, 1.1) == 3 && st.MaxUtil < 1);
            // Unified ground road: bridge and tunnel routes start and end at the same ground points
            var refPath = refSim.TunnelPath;
            var bridgeLvl = db.GetLevel("level1");
            var b1 = new BridgeSimulation(LoadRef("level1"), bridgeLvl, db); b1.RunToEnd();
            Check("道路 隧道关的车辆从固定地面起点出发、到固定终点结束", refPath != null && Math.Abs(refPath.PointX[0] - TunnelRules.GroundStartX(l13)) < 1e-6 && Math.Abs(refPath.PointX[^1] - TunnelRules.GroundEndX(l13)) < 1e-6);
            Check("道路 没有隧道的关卡起点不变（锚点外 25 m）", Math.Abs(b1.Path.PointX[0] - (bridgeLvl.Anchors.First(a => a.IsRoadStart).Position.X - BridgeArchitect3D.Traffic.RoadPath.ApproachLength)) < 1e-6);
            var mixed = Tube(l13, "reinforced", 20, 60);
            mixed.AddMemberToNewNode(mixed.Design.Nodes.First(n => n.IsAnchor && n.Position.X < 1).Id, 5, 0, "road");
            var mixedSim = new BridgeSimulation(mixed.Design, l13, db); mixedSim.RunToEnd();
            Check("道路 同一关里桥面路线与隧道路线的起点相同", mixedSim.Path != null && Math.Abs(mixedSim.Path.PointX[0] - refPath.PointX[0]) < 1e-6);
            double maxStep = 0;
            for (int i = 1; i + 1 < refPath.PointX.Count; i++)
            {
                // Lifts are vertical rides, not road: only the drivable road is checked
                if (refPath.LiftSegments.Contains(i - 1) || refPath.LiftSegments.Contains(i) || refPath.LiftSegments.Contains(i - 2) || refPath.LiftSegments.Contains(i + 1)) continue;
                double sg1 = (refPath.PointY[i] - refPath.PointY[i - 1]) / Math.Max(1e-9, refPath.PointX[i] - refPath.PointX[i - 1]);
                double sg2 = (refPath.PointY[i + 1] - refPath.PointY[i]) / Math.Max(1e-9, refPath.PointX[i + 1] - refPath.PointX[i]);
                maxStep = Math.Max(maxStep, Math.Abs(sg2 - sg1));
            }
            Check("道路 坡道与平路之间是平滑竖曲线（相邻坡度变化 ≤ 2%）", maxStep <= 0.02 + 1e-9, $"{maxStep * 100:0.00}%");
            Check("道路 所有车辆可爬升坡度都是 30°", db.Vehicles.Values.All(v => Math.Abs(Math.Atan(v.MaxRampGrade) * 180 / Math.PI - 30) < 0.01) && Math.Abs(TunnelRules.RampGrade(l13) - BridgeArchitect3D.Traffic.RoadPath.MaxClimbGrade) < 1e-3 && Math.Abs(Math.Atan(BridgeArchitect3D.Traffic.RoadPath.MaxClimbGrade) * 180 / Math.PI - 30) < 1e-9);
            // The grade check itself still works: a level limiting tunnels to 5% rejects a 10% tube
            var l13flat = Copy(l13, false, "car"); l13flat.Objectives.TunnelMaxGrade = 0.015;
            var steep = Tube(l13flat, "reinforced", 20, 60);
            // Straight immersed tube: raise the left end to the top of its range (1.5 m over 60 m = 2.5% > 1.5% + tolerance)
            TunnelRules.BottomRange(l13.Terrain, db.TunnelConfig.Types["immersed"], out _, out double immHi);
            var spn = steep.Design.Tunnel.PointNodeIds.Select(steep.Design.FindNode).OrderBy(n => n.Position.X).First();
            var spMove = steep.MoveNode(spn.Id, spn.Position.X, immHi);
            Check("道路 沉管一端抬到最高（直管倾斜）", spMove.Ok, spMove.Message);
            var steepRes = Run(steep, l13flat);
            Check("道路 管身坡度超过车辆能力时车辆不能通行，并说明坡度", !steepRes.IsSuccess && steepRes.FailureMessage.Contains("坡度"), steepRes.FailureMessage);
            var l11 = db.GetLevel("level11");
            Check("隧道 没有开放隧道建材的关卡不能建隧道", !new DesignEditor(null, l11, db).AddTunnel(0, 10, "tunnel_immersed").Ok);
        }

        // Floating tunnel (level 14): buoyancy, anchor cables, current drag ∝ v², anchor scour
        {
            var l14 = db.GetLevel("level14");
            var tcfg = db.TunnelConfig;
            var ft = tcfg.Types["floating"];
            LevelDefinition Copy14(bool flood, params string[] vehicles)
            {
                var c = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(l14, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
                c.TrafficEvents.RemoveAll(e => (e.Type == TrafficEventType.Environment && !flood) || e.Type == TrafficEventType.Vehicle);
                for (int i = 0; i < vehicles.Length; i++) c.TrafficEvents.Add(new TrafficEvent { Time = i, VehicleId = vehicles[i] });
                return c;
            }
            DesignEditor Float(LevelDefinition lv, string grade = "reinforced", string buoy = "mid", int cables = 3, string cable = "standard", bool reinforceScour = true)
            {
                // Player-built anchor cables at every joint, straight down to pile anchor blocks
                var e = TunnelStructureTests.Floating(db, lv, grade, buoy, cables, cable == "strong" ? "cable_composite" : "cable", FoundationType.Pile);
                e.SetPortalProtection(raise: 1.5);
                return e;
            }
            SimulationResult Run14(DesignEditor e, LevelDefinition lv) => new BridgeSimulation(e.Design, lv, db).RunToEnd();
            TunnelState Static(DesignEditor e, LevelDefinition lv) => BridgeSimulation.StaticTunnel(e.Design, lv, db);

            var refSim = new BridgeSimulation(new DesignEditor(LoadRef("level14"), l14, db).Design, l14, db);
            var refRes = refSim.RunToEnd();
            Check("悬浮 参考方案的车辆都从悬浮隧道通过", refRes.IsSuccess && refSim.Vehicles.Where(v => v.Def.Kind != "train").All(v => v.Lane == TunnelRules.Lane && v.Finished), refRes.FailureMessage);

            var ed = Float(l14);
            var g = ed.TunnelGeo;
            TunnelRules.BottomRange(l14.Terrain, ft, out double lo, out double hi);
            Check("悬浮 新建管节悬在水中（顶部离水面 1 m），接缝圆点就是可拖动的控制点", g != null && Math.Abs(g.Points[0].Y - hi) < 1e-6 && hi + ft.TubeHeight <= l14.Terrain.WaterLevel - ft.MinCover + 1e-6 && ed.Design.Nodes.Where(n => n.IsTunnelSupport).All(n => n.TunnelPoint));
            int pid = ed.Design.Tunnel.PointNodeIds[0];
            Check("悬浮 控制点不能高出最小覆盖水深，也不能贴近河床", !ed.MoveNode(pid, 0, hi + 1).Ok && !ed.MoveNode(pid, 0, lo - 1).Ok);
            double rampShallow = TunnelRules.RampRun(l14, true, hi + ft.FloorHeight), rampBed = TunnelRules.RampRun(l14, true, TunnelRules.BedY(l14.Terrain) + 0.8);
            Check("悬浮 浅悬浮的坡道比铺在 30 m 深河床上短得多", rampShallow < rampBed / 2);
            var cheap = Float(l14, cables: 2); var many = Float(l14, cables: 4);
            Check("悬浮 锚索是普通构件，按根数和长度计费（4 根比 2 根贵）", many.UsedCost > cheap.UsedCost);
            var round = GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(ed.Design));
            var rg = TunnelGeometry.Build(round, db);
            Check("悬浮 存档保存浮力等级与锚索构件", rg != null && rg.BuoyancyId.All(b => b == "mid") && round.Members.Count(m => m.MaterialId == "cable") == ed.Design.Members.Count(m => m.MaterialId == "cable") && round.Members.Any(m => m.MaterialId == "cable"));

            // Buoyancy physics
            var s0 = Static(ed, l14);
            var flooded = TunnelAnalysis.Evaluate(ed.TunnelGeo, l14, tcfg, new TunnelEnvironment { WaterY = l14.Terrain.WaterLevel + 3, Current = 0.5 });
            Check("悬浮 完全浸没时水位上涨不增加浮力，只增加水压", Math.Abs(flooded.NetBuoyancy[0] - s0.NetBuoyancy[0]) < 1e-6 && flooded.Pressure[0] > s0.Pressure[0]);
            var hiB = Static(Float(l14, buoy: "high"), l14);
            Check("悬浮 浮力等级越高，净上浮力越大", hiB.NetBuoyancy[0] > s0.NetBuoyancy[0]);
            var fast = TunnelAnalysis.Evaluate(ed.TunnelGeo, l14, tcfg, new TunnelEnvironment { WaterY = l14.Terrain.WaterLevel, Current = 1.0 });
            Check("悬浮 水流横推与流速平方成正比", Math.Abs(fast.Drag[0] / s0.Drag[0] - 4) < 1e-9);

            // Failure modes (each its own message)
            var press = Run14(Float(l14, grade: "normal"), l14);
            Check("悬浮 洪水抬高水位，普通管节水压超限", !press.IsSuccess && press.FailureReason == FailureReason.TunnelFlooded && press.FailureMessage.Contains("水压"), press.FailureMessage);
            var calm = Copy14(false, "car", "bus", "semi_long");
            var slack = Run14(Float(calm, buoy: "low"), calm);
            Check("悬浮 车辆超过净浮力不再导致下沉失败（只看材料强度）", !(slack.FailureMessage ?? "").Contains("净浮力"), slack.FailureReason + " " + slack.FailureMessage);
            var snap = Run14(Float(calm, buoy: "high", cables: 1), calm);
            Check("悬浮 高浮力每道接缝只 1 根钢索，钢索被拉断", !snap.IsSuccess && (snap.FailureMessage ?? "").Contains("钢索"), snap.FailureMessage);
            var strong = Run14(Float(calm, buoy: "high", cables: 1, cable: "strong"), calm);
            Check("悬浮 高浮力换成复合钢索后每道接缝 1 根够用（无洪水）", strong.IsSuccess, strong.FailureMessage);
            var softAnchor = Run14(Float(calm, buoy: "high", cables: 2, cable: "strong"), calm);
            Check("悬浮 锚点地基不再被拔出（只看锚索强度）", !(softAnchor.FailureMessage ?? "").Contains("锚固基础"), softAnchor.FailureMessage);
            var sway = Run14(Float(l14, cables: 2), l14);
            Check("悬浮 横向摆动不再导致失败（只看材料强度）", !(sway.FailureMessage ?? "").Contains("摆动"), sway.FailureMessage);
            var scour = Run14(Float(l14, reinforceScour: false), l14);
            Check("悬浮 冲刷区锚点不再被拔出（只看材料强度）", !(scour.FailureMessage ?? "").Contains("锚固基础"), scour.FailureMessage);
            var none = Static(Float(l14, cables: 0), l14);
            Check("悬浮 没有锚索时上浮力只靠两岸，管节接缝弯矩超限（静态检查就超限）", none.MaxUtil >= 1 && none.WorstText != null && none.WorstText.Contains("接缝弯矩"));

            // Level balance: the immersed tube cannot take 30 m of water plus the flood
            var imm = TunnelStructureTests.Immersed(db, l14, "heavy", null, FoundationType.Pile); imm.SetPortalProtection(raise: 1.5);
            var immRes = Run14(imm, l14);
            Check("悬浮 第十四关：沉管即使用重型管节也扛不住 30 m 深水加洪水", !immRes.IsSuccess && immRes.FailureMessage.Contains("水压"), immRes.FailureMessage);
            var l13 = db.GetLevel("level13");
            Check("悬浮 第十三关也开放悬浮隧道（2026-11 用户要求隧道关开放全部隧道类型）", l13.AllowedMaterials.Contains("tunnel_floating"));
        }

        // Shield tunnel (level 15): geology layers, minimum cover, groundwater + earth pressure, lagged groundwater
        {
            var l15 = db.GetLevel("level15");
            var tcfg = db.TunnelConfig;
            var stype = tcfg.Types["shield"];
            DesignEditor Bore(LevelDefinition lv, double bottom, string grade = "reinforced", bool grout = false)
            {
                var e = new DesignEditor(null, lv, db);
                if (!e.AddTunnel(0, 60, "tunnel_shield").Ok) return null;
                e.SetTunnelGrade(-1, grade); e.SetPortalProtection(raise: 1.5);
                foreach (var pid in e.Design.Tunnel.PointNodeIds.ToList()) { var pn = e.Design.FindNode(pid); e.MoveNode(pid, pn.Position.X, bottom); }
                if (grout) e.SetTunnelReinforced(-1, true);
                return e;
            }
            SimulationResult Run15(DesignEditor e) => new BridgeSimulation(e.Design, l15, db).RunToEnd();
            TunnelState Static15(DesignEditor e) => TunnelAnalysis.Evaluate(e.TunnelGeo, l15, tcfg, TunnelAnalysis.StaticEnvironment(l15, tcfg));

            var refSim = new BridgeSimulation(new DesignEditor(LoadRef("level15"), l15, db).Design, l15, db);
            var refRes = refSim.RunToEnd();
            Check("盾构 参考方案的车辆都从盾构隧道通过", refRes.IsSuccess && refSim.Vehicles.All(v => v.Lane == TunnelRules.Lane && v.Finished), refRes.FailureMessage);

            var t = l15.Terrain;
            Check("盾构 地层按关卡分层：淤泥 / 砂土 / 黏土 / 岩石", TunnelRules.SoilAt(t, 30, -13) == "silt" && TunnelRules.SoilAt(t, 30, -18) == "sand" && TunnelRules.SoilAt(t, 30, -24) == "clay" && TunnelRules.SoilAt(t, 30, -29) == "rock");
            Check("盾构 最小覆土：岩石 3、黏土 6、砂土 9、淤泥 12 m（可配置）", tcfg.Geo("rock").MinCover == 3 && tcfg.Geo("clay").MinCover == 6 && tcfg.Geo("sand").MinCover == 9 && tcfg.Geo("silt").MinCover == 12);
            var ed = new DesignEditor(null, l15, db);
            var add = ed.AddTunnel(0, 60, "tunnel_shield");
            var g = ed.TunnelGeo;
            Check("盾构 新建隧道在河床以下（默认覆土 6 m），不扰动河床、没有支点", add.Ok && Math.Abs(g.Points[0].Y - (TunnelRules.BedY(t) - stype.TubeHeight - stype.DefaultCover)) < 1e-6 && !ed.Design.Nodes.Any(n => n.IsTunnelSupport), add.Message);
            int p0 = ed.Design.Tunnel.PointNodeIds[0];
            Check("盾构 控制点不能高出河床，也不能低于关卡最深处", !ed.MoveNode(p0, 0, TunnelRules.BedY(t) - 2).Ok && !ed.MoveNode(p0, 0, t.BuildMinY - 1).Ok);
            Check("盾构 生态保护区不能建沉管或悬浮隧道", !new DesignEditor(null, l15, db).AddTunnel(0, 60, "tunnel_immersed").Ok && !new DesignEditor(null, l15, db).AddTunnel(0, 60, "tunnel_floating").Ok);

            // Stability: crown in sand needs 9 m; grouting lowers it to 5.4 m
            var shallow = Bore(l15, -24.5);   // crown −17.5 in sand, cover 5.5 m
            var ss = Static15(shallow);
            Check("盾构 覆土不足不再塌方：只看管片强度，覆土只作参考", ss.StabilityUtil[0] > 1 && Math.Abs(ss.Util[0] - ss.PressureUtil[0]) < 1e-12);
            var grouted = Static15(Bore(l15, -24.5, grout: true));
            Check("盾构 注浆加固把砂土的最小覆土降到 5.4 m，同一埋深变稳定", grouted.StabilityUtil[0] < 1);
            // Lining: deeper means more groundwater and earth pressure, both reported separately
            var deep = Static15(Bore(l15, -28.5)); var deeper = Static15(Bore(l15, -30));
            Check("盾构 越深地下水压和土压越大（分别计算）", deeper.Pressure[0] > deep.Pressure[0] && deeper.EarthPressure[0] >= deep.EarthPressure[0] && deep.EarthPressure[0] > 0);
            var normalDeep = Run15(Bore(l15, -28.5, "normal"));
            Check("盾构 管顶在黏土里埋深足够，但普通管片外压超限", !normalDeep.IsSuccess && normalDeep.FailureReason == FailureReason.TunnelFlooded && normalDeep.FailureMessage.Contains("外压"), normalDeep.FailureMessage);
            Check("盾构 掘进费随地层变化：岩石最贵", tcfg.Geology.Values.All(s => s.ExcavationCost <= tcfg.Geo("rock").ExcavationCost));
            var rockCost = Bore(l15, -30).UsedCost; var clayCost = Bore(l15, -28.5).UsedCost;
            Check("盾构 进入岩石的隧道更贵（掘进 + 更长坡道）", rockCost > clayCost);
            // Groundwater follows the flood with the soil's delay: sand fast, clay slow
            var sandRun = new BridgeSimulation(Bore(l15, -26, "normal", grout: true).Design, l15, db);
            Check("盾构 浅埋注浆 + 普通管片：平时外压在承载以内", Static15(Bore(l15, -26, "normal", grout: true)).MaxUtil < 1);
            var sandRes = sandRun.RunToEnd();
            Check("盾构 洪水让砂层地下水快速上涨，浅埋注浆 + 普通管片外压超限", !sandRes.IsSuccess && sandRes.FailureMessage.Contains("外压"), sandRes.FailureMessage);
            var sandOk = Run15(Bore(l15, -26, "reinforced", grout: true));
            Check("盾构 同样浅埋注浆，加强管片能扛住洪水", sandOk.IsSuccess, sandOk.FailureMessage);
            var clayRun = new BridgeSimulation(new DesignEditor(LoadRef("level15"), l15, db).Design, l15, db);
            clayRun.RunToEnd();
            Check("盾构 黏土地下水滞后：洪水 5 m 只传过去不到一半", clayRun.TunnelStatus.Groundwater.Length > 0 && clayRun.TunnelStatus.Groundwater.Max() < 2.5);
            Check("盾构 地下水由河水连通而来，不再另加河水压力（只用地下水位）", Math.Abs(deep.Pressure[0] - tcfg.WaterDensity * tcfg.Gravity * (t.WaterLevel - (-28.5))) < 1e-6);
        }

        // Unified weather
        {
            var wc = db.Weather;
            Check("天气 三种预设与规格一致（水位 0/2/5 m，流速 0.5/0.8/1.1 m/s，软化 0/10/25%）",
                wc.Preset("normal").WaterRise == 0 && wc.Preset("rainstorm").WaterRise == 2 && wc.Preset("flood").WaterRise == 5 &&
                wc.Preset("normal").Current == 0.5 && wc.Preset("rainstorm").Current == 0.8 && wc.Preset("flood").Current == 1.1 &&
                wc.Preset("rainstorm").SoilSoftening == 0.1 && wc.Preset("flood").SoilSoftening == 0.25);
            var legacy = new TrafficEvent { Type = TrafficEventType.Environment, Height = 3 };
            Check("天气 旧洪水事件（只有 Height）按严重洪水处理、水位用 Height", wc.PresetOf(legacy).Scour && wc.RiseOf(legacy) == 3);
            Check("天气 只有严重洪水冲刷河床", !wc.Preset("rainstorm").Scour && wc.Preset("flood").Scour);

            var l13w = db.GetLevel("level13");
            DesignEditor Prot(double raise, bool gate = false, bool pump = false)
            {
                var e = new DesignEditor(LoadRef("level13"), l13w, db); e.SetPortalProtection(raise, gate, pump); return e;
            }
            var bare = new BridgeSimulation(Prot(0).Design, l13w, db).RunToEnd();
            Check("电梯 入口在岸上，洪水不会从入口倒灌：不做任何防护也通关", bare.IsSuccess, bare.FailureMessage);
            Check("电梯 旧存档里的抬高入口 / 防洪闸 / 排水泵不再计费", Math.Abs(Prot(1.5, true, true).UsedCost - Prot(0).UsedCost) < 1e-6);
            // One vehicle per trip: the next one waits in front of the lift; riders move with the platform
            var ls = new BridgeSimulation(Prot(0).Design, l13w, db); ls.Initialize();
            bool liftWait = false, rode = false; double lowest = 0;
            while (ls.State == SimState.Running && ls.Time < 900)
            {
                ls.Step(); liftWait |= ls.Vehicles.Any(v => v.WaitingAtPortal);
                foreach (var v in ls.Vehicles) if (v.OnLift) { rode = true; lowest = Math.Min(lowest, v.LiftDy); }
            }
            Check("电梯 一次运一辆车：后车在电梯前等候（不算失败）", ls.Result.IsSuccess && liftWait, ls.Result.FailureMessage);
            Check("电梯 车辆随平台整车下到隧道底，再从另一岸升上来", rode && lowest < -(ls.Lifts[0].Height - 0.5) && ls.Result.VehicleResults.All(v => v.Passed), $"{lowest:0.0} / {ls.Lifts[0].Height:0.0}");
            var lShort = Prot(0); lShort.SetLiftLength(8);
            var rs = new BridgeSimulation(lShort.Design, l13w, db).RunToEnd();
            Check("电梯 车身比平台长时开不上去，测试失败并说明", !rs.IsSuccess && rs.FailureReason == FailureReason.VehicleBlocked && rs.FailureMessage.Contains("电梯平台"), rs.FailureMessage);
            var tankLvl = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(l13w, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
            tankLvl.TrafficEvents.RemoveAll(e => e.Type == TrafficEventType.Vehicle);
            tankLvl.TrafficEvents.Add(new TrafficEvent { Time = 0, Type = TrafficEventType.Vehicle, VehicleId = "tank" });
            var te9 = new DesignEditor(LoadRef("level13"), tankLvl, db); te9.SetLiftLength(te9.LiftLengthForLevel()); te9.RebuildLifts("light");
            var rt9 = new BridgeSimulation(te9.Design, tankLvl, db).RunToEnd();
            Check("电梯 轻型电梯结构承载不够：60 吨坦克压坏平台，失败说明位置、原因和改法", !rt9.IsSuccess && rt9.FailureReason == FailureReason.LiftOverload && rt9.FailureMessage.Contains("利用率") && rt9.FailureMessage.Contains("建议"), rt9.FailureMessage);
            Check("电梯 按本关车辆自动配：平台长度只按车身长度（超长重卡需要 26 m）", new DesignEditor(null, l13w, db).LiftLengthForLevel() == 26);
            var te16 = new DesignEditor(LoadRef("level13"), tankLvl, db); te16.SetLiftLength(te16.LiftLengthForLevel()); te16.RebuildLifts();
            var rt16 = new BridgeSimulation(te16.Design, tankLvl, db).RunToEnd();
            Check("电梯 自动配的平台能运坦克", rt16.IsSuccess, rt16.FailureMessage);
            var round2 = GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(lShort.Design));
            Check("电梯 平台长度随方案保存；旧存档没有该字段时用最长平台", round2.Tunnel.LiftLength == 8 && TunnelGeometry.Build(LoadRef("level14"), db).LiftLength == TunnelRules.LiftCfg.MaxLength);

            var l15w = db.GetLevel("level15");
            var noPump = new BridgeSimulation(new DesignEditor(LoadRef("level15"), l15w, db).Design, l15w, db).RunToEnd();
            Check("电梯 第十五关开局暴雨不再让入口积水（入口在岸上）", noPump.IsSuccess, noPump.FailureMessage);

            // Softening acts on foundations, not on materials
            var s12 = new BridgeSimulation(LoadRef("level12"), db.GetLevel("level12"), db); s12.Initialize();
            int pier = -1; for (int i = 0; i < s12.Model.NodeCount; i++) if (s12.Model.PierFoundation[i] != FoundationType.None && s12.Model.AnchorMaxLoad[i] > 0) { pier = i; break; }
            double cap0 = pier >= 0 ? s12.Model.AnchorMaxLoad[pier] : 0, capMin = cap0;
            while (s12.State == SimState.Running && s12.Time < 120) { s12.Step(); if (pier >= 0) capMin = Math.Min(capMin, s12.Model.AnchorMaxLoad[pier]); }
            Check("天气 洪水软化土体：桥墩地基承载下降 25%（材料强度不变）", pier < 0 || Math.Abs(capMin - cap0 * 0.75) < cap0 * 0.01, $"{capMin}/{cap0}");
        }

        // Engineering contracts: other weights + one condition, never other physics
        {
            var l1c = db.GetLevel("level1");
            var c1 = EngineeringScore.ContractsFor(l1c); var c13 = EngineeringScore.ContractsFor(db.GetLevel("level13"));
            Check($"合同 第一关提供低成本 / 高安全 / 环保合同，没有洪水与重载就不提供极端天气 / 重型物流（{string.Join(",", c1)}）",
                c1.Contains("low_cost") && c1.Contains("high_safety") && c1.Contains("environment") && !c1.Contains("extreme_weather") && !c1.Contains("heavy_logistics"));
            Check("合同 洪水关提供极端天气合同", c13.Contains("extreme_weather"));
            SimulationResult RunC(string contract, out BridgeSimulation simOut)
            {
                var d = LoadRef("level1"); d.Contract = contract;
                simOut = new BridgeSimulation(d, l1c, db); simOut.RunToEnd(); EngineeringScore.EvaluateAchievements(simOut); return simOut.Result;
            }
            var none = RunC(null, out var sn); var safeC = RunC("high_safety", out _); var cheapC = RunC("low_cost", out var sc);
            Check("合同 物理结果不变（同一方案利用率、完成时间相同）", none.MaxUtilization == safeC.MaxUtilization && none.CompletionTime == safeC.CompletionTime && none.IsSuccess && safeC.IsSuccess);
            Check($"合同 高安全合同：利用率 25% 满足条件，按合同权重并乘奖励（{none.Score} → {safeC.Score}）", safeC.ContractMet && safeC.Score > none.Score && safeC.SafetyPoints > none.SafetyPoints);
            Check($"合同 低成本合同：参考方案造价没有低于 85%，合同未完成但仍通关（{cheapC.ContractNote}）", !cheapC.ContractMet && cheapC.IsSuccess && cheapC.Score > 0 && cheapC.ContractNote.Length > 0);
            Check("合同 每个合同单独记录（排行榜按合同区分）", EngineeringScore.RecordKey("level1", "low_cost") != EngineeringScore.RecordKey("level1", null));
            var dsave = LoadRef("level1"); dsave.Contract = "environment";
            Check("合同 方案保存合同并能读回；旧存档没有合同", GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(dsave)).Contract == "environment" && LoadRef("level1").Contract == null);
            var rec = new LevelRecord { Version = db.Scoring.Version };
            Check("记录 最低环境影响单独记录", rec.Improve(none).Contains("env"));
        }

        if (Environment.GetEnvironmentVariable("AUDIT") == "1") foreach (var lv in db.Levels) { var sw = System.Diagnostics.Stopwatch.StartNew(); var s = new BridgeSimulation(LoadRef(lv.Id), lv, db); s.RunToEnd(); double t1 = sw.Elapsed.TotalMilliseconds; EngineeringScore.EvaluateAchievements(s); var r = s.Result; Console.WriteLine($"AUDIT {lv.Id} cost {r.TotalCost / lv.Budget:P0} util {r.MaxUtilization:P0} wm {r.WeatherMargin:0.00} T {r.CompletionTime:0}/{r.ReferenceTime:0} env {r.EnvironmentFactor:0.00} base {r.BaseScore} score {r.Score} ach {string.Join(",", r.Achievements.Select(a => a.Id + (a.Earned ? "+" : "-")))} contracts {string.Join(",", EngineeringScore.ContractsFor(lv))} sim {t1:0}ms total {sw.Elapsed.TotalMilliseconds:0}ms"); }
        // Step 10 regression: approach fast-forward and background achievement evaluation
        {
            var l15q = db.GetLevel("level15");
            var sq = new BridgeSimulation(LoadRef("level15"), l15q, db); sq.Initialize();
            double quiet = 0; bool wrongQuiet = false,visibleFast=false;
            while (sq.State == SimState.Running)
            {
                sq.Step();
                if (!sq.QuietTraffic) continue;
                quiet += BridgeSimulation.FixedDt;
                if(sq.Vehicles.Any(v=>!v.Finished)||sq.Lifts.Any(l=>l.Phase!=BridgeSimulation.LiftPhase.Boarding||l.Riders.Count>0))visibleFast=true;
                if (sq.WeatherName != null || sq.Time < BridgeSimulation.StaticPhaseTime) wrongQuiet = true;
            }
            Check("Playback: active traffic and lift operations never trigger idle fast-forward",!visibleFast);
            Check("快进 静态测试、天气事件期间从不快进", !wrongQuiet);
            var plain = new BridgeSimulation(LoadRef("level15"), l15q, db).RunToEnd();
            Check("快进 只影响播放速度：判断它不改变测试结果", plain.IsSuccess == sq.Result.IsSuccess && plain.CompletionTime == sq.Result.CompletionTime && plain.MaxUtilization == sq.Result.MaxUtilization);

            string[] ids = { "level3", "level12", "level13", "level14" };
            int Eval(string id) { var s = new BridgeSimulation(LoadRef(id), db.GetLevel(id), db); s.RunToEnd(); EngineeringScore.EvaluateAchievements(s); return s.Result.Score; }
            var seq = ids.Select(Eval).ToArray();
            var par = new int[ids.Length];
            System.Threading.Tasks.Parallel.For(0, ids.Length, i => par[i] = Eval(ids[i]));
            Check($"后台 并行评估工程成就与顺序评估结果相同（{string.Join(",", seq)}）", seq.SequenceEqual(par), string.Join(",", par));
        }

        // Level settings and tunnel materials
        {
            var tunnelIds = new[] { "tunnel_immersed", "tunnel_floating", "tunnel_shield" };
            foreach (var id in new[] { "level13", "level14", "level15" })
                Check($"关卡设置 {id} 开放全部三种隧道", tunnelIds.All(t => db.GetLevel(id).AllowedMaterials.Contains(t)));
            foreach (var t in tunnelIds)
            {
                var rows = TunnelRules.MaterialTable(db.TunnelConfig, db.GetMaterial(t));
                Check($"关卡设置 {t} 有属性表（表头 + 3 个等级，每行列数一致）", rows.Count == 4 && rows.All(r => r.Length == rows[0].Length) && TunnelRules.MaterialNotes(db.TunnelConfig, db.GetMaterial(t)).Count >= 2);
            }
            bool allSame = true;
            foreach (var lv in db.Levels)
            {
                var copy = GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(lv));
                allSame &= db.LevelError(copy) == null && GameDatabase.SerializeLevel(copy) == GameDatabase.SerializeLevel(lv);
            }
            Check("关卡设置 每关的设置能完整保存并读回（JSON 往返不变）", allSame);
            var l13 = GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(db.GetLevel("level13")));
            var r13 = new BridgeSimulation(new DesignEditor(LoadRef("level13"), l13, db).Design, l13, db).RunToEnd();
            Check("关卡设置 读回的第十三关参考方案照常通关", r13.IsSuccess, r13.FailureMessage);
            l13.AllowedMaterials.Add("no_such_material");
            Check("关卡设置 引用不存在的建材会被拒绝", db.LevelError(l13) != null);
            var dry = GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(db.GetLevel("level13")));
            dry.Terrain.BuildMinY = dry.Terrain.BuildMaxY + 1;
            Check("关卡设置 最低建造标高高于最高时会被拒绝", db.LevelError(dry) != null);
            var low = GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(db.GetLevel("level13")));
            low.TrafficEvents.RemoveAll(e => e.Type == TrafficEventType.Environment);
            var rl = new BridgeSimulation(new DesignEditor(LoadRef("level13"), low, db).Design, low, db).RunToEnd();
            Check("关卡设置 去掉洪水后第十三关照常通关（改动在仿真里生效）", rl.IsSuccess, rl.FailureMessage);
            var ed13 = new DesignEditor(LoadRef("level13"), db.GetLevel("level13"), db);
            ed13.SetTunnelGrade(-1, "normal");
            var rp = new BridgeSimulation(ed13.Design, db.GetLevel("level13"), db).RunToEnd();
            Check($"关卡设置 第十三关普通管节的失败提示是强度不够（{rp.Headline}）", !rp.IsSuccess && rp.Headline.Contains("强度") && !rp.FailureMessage.Contains("进水"), rp.FailureMessage);
        }

        // Game version: one number everywhere, stamped into saves, old files still load
        {
            string Read(string rel) => File.ReadAllText(Path.Combine(_root, rel));
            string v = GameVersion.Current, v4 = v + ".0";
            Check($"版本 project.godot 的 config/version = {v}", Read("project.godot").Contains($"config/version=\"{v}\""));
            Check($"版本 导出预设 file / product version = {v4}", Read("export_presets.cfg").Contains($"application/file_version=\"{v4}\"") && Read("export_presets.cfg").Contains($"application/product_version=\"{v4}\""));
            Check($"版本 安装包 AppVersion = {v}", Read("installer/BridgeArchitect3D.iss").Contains($"#define AppVersion \"{v}\""));
            Check($"版本 CHANGELOG.md 有 {v} 的条目", Read("CHANGELOG.md").Contains($"## {v}"));
            Check("版本 比较：未记录版本 = 1.0.0，比当前旧；更新的版本能识别", GameVersion.IsOlder(null) && GameVersion.Compare(null, "1.0.0") == 0
                && GameVersion.IsNewer("99.0.0") && !GameVersion.IsOlder(v) && GameVersion.Compare("1.10.0", "1.9.0") > 0 && GameVersion.Compare("v1.1.0", "1.1.0") == 0);
            var d = LoadRef("level1");
            Check("版本 旧存档没有游戏版本字段也能读", d.GameVersion == null);
            GameVersion.Stamp(d);
            var back = GameDatabase.DeserializeDesign(GameDatabase.SerializeDesign(d));
            Check("版本 保存时写入游戏版本并能读回（与存档格式版本分开）", back.GameVersion == v && back.Version == BridgeDesign.CurrentVersion);
        }

        // Four-phase test
        {
            var l13p = db.GetLevel("level13");
            var ps = new BridgeSimulation(LoadRef("level13"), l13p, db); ps.Initialize();
            var seen = new System.Collections.Generic.List<TestPhase>(); bool carsInStatic = false;
            while (ps.State == SimState.Running)
            {
                ps.Step();
                if (seen.Count == 0 || seen[^1] != ps.Phase) seen.Add(ps.Phase);
                if (ps.Time < BridgeSimulation.StaticPhaseTime - 1e-9 && ps.Vehicles.Count > 0) carsInStatic = true;
            }
            var pr = ps.Result;
            Check("四阶段 静态测试在前，期间没有车辆和天气", seen.Count > 0 && seen[0] == TestPhase.Static && !carsInStatic && pr.PhaseMaxUtil[0] >= 0);
            Check("四阶段 依次经过车辆测试、环境测试，最后是结果评估", seen.Contains(TestPhase.Vehicles) && seen.Contains(TestPhase.Environment) && seen[^1] == TestPhase.Result, string.Join(",", seen));
            Check("四阶段 结果评估给出最薄弱位置、最大变形与天气期间安全裕量",
                pr.IsSuccess && pr.WeakestText.Length > 0 && !double.IsNaN(pr.WeakestX) && pr.MaxTunnelMove > 0 && !double.IsNaN(pr.WeatherMargin) && pr.WeatherMargin > 0,
                $"{pr.WeakestText} {pr.MaxTunnelMove} {pr.WeatherMargin}");
            var pe2 = new DesignEditor(LoadRef("level13"), l13p, db); pe2.SetTunnelGrade(-1, "normal");
            var pf = new BridgeSimulation(pe2.Design, l13p, db).RunToEnd();
            Check("四阶段 洪水把普通管节压坏，记在环境测试阶段", !pf.IsSuccess && pf.FailurePhase == TestPhase.Environment, pf.FailurePhase.ToString());
            var p1 = new BridgeSimulation(LoadRef("level1"), db.GetLevel("level1"), db).RunToEnd();
            Check("四阶段 没有天气事件的关卡：环境阶段不进行、没有天气裕量", p1.IsSuccess && p1.PhaseMaxUtil[2] < 0 && double.IsNaN(p1.WeatherMargin) && p1.MaxDeckSag > 0);
        }

        // 测试 H：模拟一致性
        {
            var r1 = new BridgeSimulation(LoadRef("level5"), db.GetLevel("level5"), db).RunToEnd();
            var r2 = new BridgeSimulation(LoadRef("level5"), db.GetLevel("level5"), db).RunToEnd();
            Check("H 相同条件结果一致", r1.IsSuccess == r2.IsSuccess && r1.MaxUtilization == r2.MaxUtilization && r1.CompletionTime == r2.CompletionTime);
        }

        Console.WriteLine(_fail == 0 ? "\n全部测试通过" : $"\n{_fail} 项测试失败");
        return _fail == 0 ? 0 : 1;
    }

    private static double MaxUtilWith(GameDatabase db, LevelDefinition baseLevel, BridgeDesign d, string vehicle)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(baseLevel, GameDatabase.JsonOptions);
        var lv = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(json, GameDatabase.JsonOptions);
        lv.TrafficEvents.Clear();
        lv.TrafficEvents.Add(new TrafficEvent { Time = 0, VehicleId = vehicle });
        return new BridgeSimulation(d, lv, db).RunToEnd().MaxUtilization;
    }

    private static BridgeDesign LoadRef(string id) =>
        GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(_root, $"Data/ReferenceDesigns/{id}_reference.json")));

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[{(ok ? "通过" : "失败")}] {name}{(ok || string.IsNullOrEmpty(detail) ? "" : "  -> " + detail)}");
        if (!ok) _fail++;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}



