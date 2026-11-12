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
        _root = FindRoot();
        var db = GameDatabase.FromJson(
            File.ReadAllText(Path.Combine(_root, "Data/Materials/materials.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Vehicles/vehicles.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Levels/levels.json")));
        Console.WriteLine($"数据加载：{db.Materials.Count} 种材料，{db.Vehicles.Count} 种车辆，{db.Levels.Count} 个关卡");

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
            Check($"第七关 固定桥面抬高到船顶以上需要坡度 {rise / run:P0}（> 30% 车辆爬不上去）", rise / run > 0.30);

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
        //（木桥面只要求轿车，钢桥面要求坦克，其余桥面要求重型货车）
        foreach (var deck in db.Materials.Values.Where(m => m.IsDeck))
        {
            // Rail-only decks carry the freight train (road vehicles may not use them)
            string veh = !deck.CarriesCars ? "train" : deck.MaterialId == "deck_wood" ? "car" : deck.MaterialId == "deck_steel" ? "tank" : "truck";
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
                Check($"教学 {lv.Id} 只开放 {lv.AllowedMaterials.Count} 种材料且有思路提示", lv.AllowedMaterials.Count <= 9 && !string.IsNullOrEmpty(lv.ReferenceHint));
            Check("教学 前几关只开放少量材料", db.Levels.Take(4).All(l => l.AllowedMaterials.Count <= 6));
            Check("教学 船只只在航道关（第七关）出现", db.Levels.Where(l => l.TrafficEvents.Any(e => e.Type == TrafficEventType.Ship)).Select(l => l.Id).SequenceEqual(new[] { "level7" }));
        }

        // 评分：预算 : 时间 : 完整度 : 受力 = 6 : 2 : 1 : 1；受力取利用率与安全冗余（上限 150%）的较高者
        {
            var a = new SimulationResult { IsSuccess = true, Budget = 10000, TotalCost = 10000, DeckIntegrity = 1, MaxUtilization = 0.3 };
            BridgeSimulation.ComputeScore(a);
            var b = new SimulationResult { IsSuccess = true, Budget = 10000, TotalCost = 10000, DeckIntegrity = 1, MaxUtilization = 0.9 };
            BridgeSimulation.ComputeScore(b);
            var c = new SimulationResult { IsSuccess = true, Budget = 10000, TotalCost = 10000, DeckIntegrity = 1, MaxUtilization = 0.5 };
            BridgeSimulation.ComputeScore(c);
            Check($"分 利用率 30% → 安全冗余封顶 150%（受力分 {a.StressScore}）", a.StressUsesMargin && a.StressScore == 1500);
            Check($"分 利用率 90% → 取利用率（受力分 {b.StressScore}）", !b.StressUsesMargin && b.StressScore == 900);
            Check($"分 利用率 50% → 冗余 100%（受力分 {c.StressScore}）", c.StressScore == 1000);
            Check($"分 权重 6:2:1:1（预算分 {a.BudgetScore}、时间分 {a.TimeScore}、完整度分 {a.IntegrityScore}）", a.BudgetScore == 6000 && a.TimeScore == 2000 && a.IntegrityScore == 1000);
            var slow = new SimulationResult { IsSuccess = true, Budget = 10000, TotalCost = 10000, DeckIntegrity = 1, MaxUtilization = 0.9, ReferenceTime = 20, CompletionTime = 40 };
            BridgeSimulation.ComputeScore(slow);
            Check($"分 用时是理想时间的 2 倍 → 时间分减半（{slow.TimeScore}）", slow.TimeScore == 1000);
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
                idle = empty ? idle + BridgeSimulation.FixedDt : 0; worst = Math.Max(worst, idle);
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
        // 坡道：网格上“刚好 15%”的坡不能被误判；稍陡的坡减速爬上去；过陡才爬不上去
        foreach (var (rise, expectPass) in new[] { (0.5, true), (0.75, true), (1.2, false) })
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



