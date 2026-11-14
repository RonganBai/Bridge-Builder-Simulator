using System;
using System.IO;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

/// <summary>Earthquake weather: permanent river widening (prescribed support movement) and ground shaking.</summary>
internal static class QuakeTests
{
    public static void Run(GameDatabase db, string root, Action<string, bool, string> check)
    {
        var wc = db.Weather; var q = wc.Preset("earthquake");
        check("地震 预设：河道变宽、水平加速度、不涨水", q.IsQuake && q.QuakeWiden > 0 && q.QuakeAccel > 0 && q.WaterRise == 0, "");
        check("天气画面：暴雨下雨并变阴，正常天气晴朗", wc.Preset("rainstorm").Rain > 0.5 && wc.Preset("rainstorm").Overcast > 0.5 && wc.Normal.Rain == 0 && wc.Normal.Overcast == 0, "");

        // Solver: a single beam between two fixed supports, the right one moved by d → axial force EA·d/L
        var model = StructuralModel.Build(GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root, "Data/ReferenceDesigns/level1_reference.json"))), db.GetLevel("level1"), db);
        var solver = new StructureSolver(); var outA = new SolveOutput(); var outB = new SolveOutput();
        var loads = new double[2 * model.NodeCount];
        LoadCalculator.Compute(model, Array.Empty<AxleLoad>(), 0, loads);
        solver.Solve(model, loads, outA);
        model.SupportShiftX = new double[model.NodeCount];
        var used = Enumerable.Range(0, model.MemberCount).SelectMany(k => new[] { model.A[k], model.B[k] }).ToHashSet();
        int right = Enumerable.Range(0, model.NodeCount).Where(i => model.FixX[i] && used.Contains(i)).OrderBy(i => model.X[i]).Last();
        model.SupportShiftX[right] = 0.05;
        solver.Solve(model, loads, outB);
        check("地震 求解器：固定支点按规定位移移动", outB.Stable && Math.Abs(outB.Displacement[2 * right] - 0.05) < 1e-12, $"{outB.Displacement[2 * right]}");
        double dN = Enumerable.Range(0, model.MemberCount).Max(k => Math.Abs(outB.AxialForce[k] - outA.AxialForce[k]));
        check("地震 支点被拉开后构件内力变化", dN > 1, $"{dN:0}");
        model.SupportShiftX = null;

        // Simulation on level 1 with a mild test-only quake (2 mm, 0.03 g) the reference bridge survives
        wc.Presets["quake_test"] = new WeatherPreset { Name = "test", Current = 0.5, QuakeWiden = 0.002, QuakeAccel = 0.03, QuakeFrequency = 1.2 };
        var lv = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(db.GetLevel("level1"), GameDatabase.JsonOptions), GameDatabase.JsonOptions);
        lv.TrafficEvents.Add(new TrafficEvent { Type = TrafficEventType.Environment, Weather = "quake_test", Time = 2, Duration = 4 });
        lv.TrafficEvents.Add(new TrafficEvent { Time = 14, VehicleId = "car" });   // keeps the test running past the quake
        var design = GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root, "Data/ReferenceDesigns/level1_reference.json")));
        var sim = new BridgeSimulation(design, lv, db); sim.Initialize();
        double peakAccel = 0, lastWiden = 0; bool shrank = false;
        while (sim.Time < 12 && sim.State == SimState.Running) { sim.Step(); peakAccel = Math.Max(peakAccel, Math.Abs(sim.GroundAccel)); shrank |= sim.QuakeWiden < lastWiden - 1e-12; lastWiden = sim.QuakeWiden; }
        check("地震 轻微地震下第一关参考桥照常运行", sim.State == SimState.Running || sim.Result.IsSuccess, sim.Result.FailureMessage ?? "");
        check("地震 测试中地面晃动", peakAccel > 0.4 * 0.03 * StructuralModel.Gravity, $"{peakAccel:0.00} m/s²");
        check("地震 结束后河道保持变宽（不会缩回）", sim.Time >= 12 && !shrank && Math.Abs(sim.QuakeWiden - 0.002) < 1e-12, $"t={sim.Time:0.0} widen={sim.QuakeWiden}");
        var calm = new BridgeSimulation(design, db.GetLevel("level1"), db).RunToEnd();
        var shaken = new BridgeSimulation(design, lv, db).RunToEnd();
        check("地震 让桥梁利用率升高", shaken.MaxUtilization > calm.MaxUtilization + 1e-3, $"{calm.MaxUtilization:0.000} → {shaken.MaxUtilization:0.000}");
        check("地震 结果可重复", new BridgeSimulation(design, lv, db).RunToEnd().MaxUtilization == shaken.MaxUtilization, "");
        // The full preset (0.3 m wider river) tears a rigid deck that is fixed on both banks
        var big = System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(db.GetLevel("level1"), GameDatabase.JsonOptions), GameDatabase.JsonOptions);
        big.TrafficEvents.Add(new TrafficEvent { Type = TrafficEventType.Environment, Weather = "earthquake", Time = 2, Duration = 4 });
        var bigRes = new BridgeSimulation(design, big, db).RunToEnd();
        check("地震 河道变宽 0.3 m 会拉断两岸固定的桥面", !bigRes.IsSuccess && bigRes.FailureMessage.Contains("拉伸"), bigRes.FailureMessage ?? "");
        wc.Presets.Remove("quake_test");
    }
}
