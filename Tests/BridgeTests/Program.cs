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

    private static int Main()
    {
        _root = FindRoot();
        var db = GameDatabase.FromJson(
            File.ReadAllText(Path.Combine(_root, "Data/Materials/materials.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Vehicles/vehicles.json")),
            File.ReadAllText(Path.Combine(_root, "Data/Levels/levels.json")));
        Console.WriteLine($"数据加载：{db.Materials.Count} 种材料，{db.Vehicles.Count} 种车辆，{db.Levels.Count} 个关卡");

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
        }

        // 船只与液压杆：航道上方的低矮构件会被船撞；换成液压杆后船来时自动收缩让路
        {
            // 航道关的河道是禁止建墩区；这里用去掉禁建区的副本，专门测试船只净空与液压杆让路
            var l4 = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(db.GetLevel("level7"), GameDatabase.JsonOptions), GameDatabase.JsonOptions);
            l4.Terrain.NoBuildZones.Clear();
            BridgeDesign WithKeel(string mat)
            {
                var ed = new DesignEditor(LoadRef("level7"), l4, db);
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
            double maxFactor = 0, maxLift = 0;
            while (simH.State == SimState.Running)
            {
                simH.Step();
                maxFactor = Math.Max(maxFactor, simH.HydraulicFactor);
                int ki = simH.Model.NodeIndex[simH.Design.Nodes.First(n => Math.Abs(n.Position.Y + 2) < 0.01).Id];
                maxLift = Math.Max(maxLift, simH.KinematicDisplacement[2 * ki + 1]);
            }
            var rh = simH.Result;
            Check($"船 液压杆在船只通行时自动收缩（最大收缩 {maxFactor:P0}，节点抬升 {maxLift:0.00} m），船通过后恢复（结束时 {simH.HydraulicFactor:P0}）",
                maxFactor > 0.99 && maxLift > 0.5 && simH.HydraulicFactor == 0);
            Check($"船 液压杆让路后船只通过且通关（船 {rh.ShipsPassed}/{rh.TotalShips}）", rh.IsSuccess && rh.ShipsPassed == rh.TotalShips, rh.FailureMessage);
        }

        // 单段桥面承载：两端有支撑时，每种桥面在自己的最大长度内都要能承载规定车辆
        //（木桥面只要求轿车，其余桥面要求重型货车）
        foreach (var deck in db.Materials.Values.Where(m => m.IsDeck))
        {
            string veh = deck.MaterialId == "deck_wood" ? "car" : "truck";
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

        // 评分：预算 : 完整度 : 受力 = 7 : 2 : 1；受力取利用率与安全冗余（上限 150%）的较高者
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
            Check($"分 权重 7:2:1（预算分 {a.BudgetScore}、完整度分 {a.IntegrityScore}）", a.BudgetScore == 7000 && a.IntegrityScore == 2000);
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
            var crackLv = WithTraffic(db.GetLevel("level1"), "truck", "truck");
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
