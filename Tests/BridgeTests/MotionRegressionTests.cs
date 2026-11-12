using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.Traffic;

internal static class MotionRegressionTests
{
    public static void Run(GameDatabase db, string root, Action<string, bool, string> check)
    {
        var level8 = db.GetLevel("level8");
        var reference = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root, "Data/ReferenceDesigns/level8_reference.json")));
        var trainSim = new BridgeSimulation(reference, level8, db);
        trainSim.Initialize();
        while (trainSim.State == SimState.Running && trainSim.Vehicles.Count == 0) trainSim.Step();
        var train = trainSim.Vehicles.Single();
        var path = train.Path;
        double firstPosition = train.FrontS;
        check("Train: first appearance is entirely on the left approach", train.BodyFrontS < path.DeckStartS &&
            train.Def.Axles.All(a => train.FrontS + a.Offset < path.DeckStartS), $"nose={train.BodyFrontS}, deck={path.DeckStartS}");
        path.Evaluate(train.FrontS, null, 1, out double nose, out double ground);
        bool spaced = true;
        for (int i = 1; i < train.Def.Units; i++)
        {
            path.Evaluate(train.FrontS - i * train.Def.UnitLength, null, 1, out double x, out double y);
            spaced &= Math.Abs(nose - x - i * train.Def.UnitLength) < 1e-8 && Math.Abs(y - ground) < 1e-8;
        }
        check("Train: cars behind the path remain spaced instead of stacking at its endpoint", spaced, "");
        path.Evaluate(-60, null, 1, out double left, out _);
        path.Evaluate(path.TotalLength + 60, null, 1, out double right, out _);
        check("Train: fixed ground extends continuously beyond both approaches", Math.Abs(left - path.PointX[0] + 60) < 1e-8 &&
            Math.Abs(right - path.PointX[^1] - 60) < 1e-8, "");
        var gapDesign = reference.Clone();
        gapDesign.Members.RemoveAll(m => m.Id == path.DeckMemberIds[path.DeckMemberIds.Count / 2]);
        var gapPath = RoadPath.Build(StructuralModel.Build(gapDesign, level8, db), level8, out _, out _, out _);
        gapPath.Evaluate(gapPath.TotalLength + 60, null, 1, out double gapEnd, out _);
        check("Train: ground extrapolation does not bridge a missing road segment", !gapPath.Complete &&
            Math.Abs(gapEnd - gapPath.PointX[^1]) < 1e-8, "");
        bool monotonic = true, completedTooSoon = false, semiSpawned = false, semiSpawnSafe = true;
        while (trainSim.State == SimState.Running)
        {
            double previous = train.FrontS;
            trainSim.Step();
            if(!semiSpawned && trainSim.Vehicles.FirstOrDefault(v=>v.Def.Kind=="semi") is { } semi)
            {semiSpawned=true;semiSpawnSafe=semi.BodyFrontS<semi.Path.DeckStartS;}
            monotonic &= train.FrontS >= previous;
            completedTooSoon |= train.Finished && train.BodyRearS <= path.DeckEndS + 8;
        }
        check("Train: enters left to right and passes only after its entire body exits", monotonic && !completedTooSoon &&
            train.FrontS > firstPosition && trainSim.Result.IsSuccess, trainSim.Result.FailureMessage);
        check("Special vehicles: extra-long semi enters nose-first on the left approach",semiSpawned && semiSpawnSafe,"");

        var convoyLevel = JsonSerializer.Deserialize<LevelDefinition>(JsonSerializer.Serialize(level8, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
        convoyLevel.TrafficEvents.Clear();
        convoyLevel.TrafficEvents.Add(new TrafficEvent { VehicleId = "train" });
        convoyLevel.TrafficEvents.Add(new TrafficEvent { VehicleId = "car" });
        var convoy = new BridgeSimulation(reference, convoyLevel, db);
        convoy.Initialize();
        bool overlap = false, queued = false;
        for (int i = 0; i < 6000 && convoy.State == SimState.Running; i++)
        {
            convoy.Step();
            queued |= convoy.Vehicles.Count == 1 && convoy.Time > 1;
            if (convoy.Vehicles.Count < 2) continue;
            var ahead = convoy.Vehicles[0]; var behind = convoy.Vehicles[1];
            if (!ahead.Finished && !behind.Finished)
                overlap |= behind.BodyFrontS > ahead.BodyRearS - behind.Def.FollowGap + 1e-8;
        }
        check("Train: a following car queues behind the train's real tail", queued && !overlap && convoy.Result.IsSuccess, convoy.Result.FailureMessage);

        var mechanismLevel = new LevelDefinition { Id = "triangle-motion", Budget = 100000 };
        mechanismLevel.Anchors.Add(new AnchorDefinition { Id = "A", Position = new Vec3d(0, 0), SupportType = SupportType.Pinned });
        mechanismLevel.Anchors.Add(new AnchorDefinition { Id = "B", Position = new Vec3d(5, 0), SupportType = SupportType.Pinned });
        var mechanism = BridgeDesign.CreateForLevel(mechanismLevel);
        mechanism.Nodes.Add(new BridgeNode { Id = 3, Position = new Vec3d(2.5, Math.Sqrt(18.75)) });
        mechanism.Members.Add(new BridgeMember { Id = 1, StartNodeId = 1, EndNodeId = 2, MaterialId = "steel" });
        mechanism.Members.Add(new BridgeMember { Id = 2, StartNodeId = 2, EndNodeId = 3, MaterialId = "steel" });
        mechanism.Members.Add(new BridgeMember { Id = 3, StartNodeId = 1, EndNodeId = 3, MaterialId = "hydraulic" });
        foreach (double ratio in new[] { -0.5, 0.5 })
        {
            var settings = new LinkageData();
            settings.Drivers.Add(new LinkageDriverData { MemberId = 3, Ratio = ratio });
            var model = StructuralModel.Build(mechanism, mechanismLevel, db);
            var engine = new LinkageSystem();
            bool moved = engine.Initialize(model, settings), bounded = true;
            for (int i = 0; i < 2000 && moved && engine.Progress < 1; i++)
            {
                double x = model.X[2], y = model.Y[2];
                moved = engine.TryMoveLimited(Math.Min(1, engine.Progress + 0.02), 0.03);
                bounded &= Math.Sqrt(Math.Pow(model.X[2] - x, 2) + Math.Pow(model.Y[2] - y, 2)) <= 0.0300001;
            }
            check($"Linkage: feasible {(ratio < 0 ? "contraction" : "extension")} reaches fifty percent without changing passive lengths", moved &&
                engine.Progress == 1 && Math.Abs(model.Length[2] - 5 * (1 + ratio)) < 1e-6 &&
                Math.Abs(model.Length[0] - 5) < 1e-6 && Math.Abs(model.Length[1] - 5) < 1e-6 && model.Y[2] > 0 && bounded, engine.Fault);
            for (int i = 0; i < 2000 && moved && engine.Progress > 0; i++)
                moved = engine.TryMoveLimited(Math.Max(0, engine.Progress - 0.02), 0.03);
            check($"Linkage: {(ratio < 0 ? "contraction" : "extension")} returns to the original assembly", moved && engine.Progress == 0 &&
                Math.Abs(model.X[2] - 2.5) + Math.Abs(model.Y[2] - Math.Sqrt(18.75)) < 1e-5, engine.Fault);
        }
        var missing = mechanism.Clone(); missing.Members.RemoveAll(m => m.Id == 2);
        var missingSettings = new LinkageData(); missingSettings.Drivers.Add(new LinkageDriverData { MemberId = 3, Ratio = -0.5 });
        var free = new LinkageSystem();
        check("Linkage: a missing follower is reported as an undriven freedom rather than invented motion", !free.Initialize(
            StructuralModel.Build(missing, mechanismLevel, db), missingSettings), free.Fault);

        var actual = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root, "Tests/BridgeTests/Fixtures/level7_triangle_limit.json")));
        string unchanged = GameDatabase.SerializeDesign(actual);
        var actualLevel = db.GetLevel("level7");
        var limitModel = StructuralModel.Build(actual, actualLevel, db);
        var limitEngine = new LinkageSystem();
        bool initialized = limitEngine.Initialize(limitModel, actual.Mechanisms.Linkage);
        var originalX = (double[])limitModel.X.Clone(); var originalY = (double[])limitModel.Y.Clone();
        check("Linkage: impossible fifty-percent triangle target is rejected atomically", initialized && !limitEngine.TryMove(1) &&
            limitEngine.Progress == 0 && limitModel.X.SequenceEqual(originalX) && limitModel.Y.SequenceEqual(originalY) &&
            limitEngine.Fault.Contains("三角形无法闭合"), limitEngine.Fault);
        var sim = new BridgeSimulation(actual, actualLevel, db);
        sim.Initialize();
        var rest = (double[])sim.Model.Length.Clone();
        var previousMotion = (double[])sim.KinematicDisplacement.Clone();
        var display = new double[previousMotion.Length];
        double passiveError = 0, jump = 0, displayStretch = 0;
        bool opened = false;
        for (int frame = 0; frame < 1500 && sim.State == SimState.Running; frame++)
        {
            sim.Step();
            sim.ComposeDisplayFrame(sim.DisplayDisplacement, sim.KinematicDisplacement, 100, display);
            for (int i = 0; i < sim.Model.NodeCount; i++)
                jump = Math.Max(jump, Math.Sqrt(Math.Pow(sim.KinematicDisplacement[2 * i] - previousMotion[2 * i], 2) +
                    Math.Pow(sim.KinematicDisplacement[2 * i + 1] - previousMotion[2 * i + 1], 2)));
            for (int k = 0; k < sim.Model.MemberCount; k++)
            {
                if (sim.Model.Mat[k].Extendable || sim.Model.Broken[k]) continue;
                passiveError = Math.Max(passiveError, Math.Abs(sim.Model.Length[k] - rest[k]));
                int a = sim.Model.A[k], b = sim.Model.B[k];
                double dx = sim.Model.X0[b] + display[2 * b] - sim.Model.X0[a] - display[2 * a];
                double dy = sim.Model.Y0[b] + display[2 * b + 1] - sim.Model.Y0[a] - display[2 * a + 1];
                displayStretch = Math.Max(displayStretch, Math.Abs(Math.Sqrt(dx * dx + dy * dy) - rest[k]));
            }
            Array.Copy(sim.KinematicDisplacement, previousMotion, previousMotion.Length);
            opened |= sim.Bascule.IsOpen;
            if (sim.Bascule.State == BridgeOpState.Blocked) break;
        }
        check("Linkage: saved bridge stops before a collinear toggle without pretending to be open", sim.Bascule.State == BridgeOpState.Blocked &&
            sim.Bascule.Linkage.Progress > 0 && sim.Bascule.Linkage.Progress < 0.4 && !opened && sim.Bascule.Fault.Contains("共线死点"), sim.Bascule.Fault);
        var diagnostic = DiagnosticTargets.Parse(sim.Bascule.Fault, 137);
        check("Linkage: the dead-point diagnostic identifies both passive sides for scene colors", diagnostic.Members.Contains(104) &&
            diagnostic.Members.Contains(136), sim.Bascule.Fault);
        check("Linkage: saved bridge preserves passive lengths and limits per-frame node travel", passiveError < 1e-6 &&
            jump <= BasculeSystem.MaximumLinkageNodeSpeed * BridgeSimulation.FixedDt + 1e-6 && sim.BrokenCount == 0, $"length={passiveError:G5}, jump={jump:G5}");
        check("Display: maximum magnification cannot stretch a working mechanism's triangles", displayStretch < 0.03, $"stretch={displayStretch:G5}");
        var replay = sim.GetHistoryFrame(sim.HistoryCount - 1);
        sim.ComposeDisplayFrame(replay.Displacement, replay.Kinematic, 100, display);
        check("Display: replay preserves the same true-scale mechanism geometry", display.Zip(replay.Displacement, (a, b) => Math.Abs(a - b)).Max() < 1e-10, "");
        bool restored = true;
        for (int frame = 0; frame < 1500 && !sim.Bascule.ClosedAndLocked; frame++)
        {
            sim.Bascule.Update(BridgeSimulation.FixedDt, false, true);
            sim.Bascule.ApplyGeometry(null);
            if (sim.Bascule.Fault != null && sim.Bascule.State == BridgeOpState.Blocked && frame > 1) { restored = false; break; }
        }
        check("Linkage: a limited opening can return and lock on the original branch", restored && sim.Bascule.ClosedAndLocked &&
            Enumerable.Range(0, sim.Model.NodeCount).All(i => Math.Abs(sim.Model.X[i] - sim.Model.X0[i]) + Math.Abs(sim.Model.Y[i] - sim.Model.Y0[i]) < 1e-5), sim.Bascule.Fault);
        check("Linkage: investigating the saved bridge never changes the player's design", unchanged == GameDatabase.SerializeDesign(actual), "");
    }
}
