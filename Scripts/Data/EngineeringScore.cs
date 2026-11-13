using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Physics;

namespace BridgeArchitect3D.Data
{
    public class ScoreWeights
    {
        public double Economy { get; set; } = 0.45;
        public double Safety { get; set; } = 0.2;
        public double Weather { get; set; } = 0.15;
        public double Traffic { get; set; } = 0.15;
        public double Environment { get; set; } = 0.05;
        public double Sum => Economy + Safety + Weather + Traffic + Environment;
    }

    public class AchievementConfig
    {
        public string Name { get; set; }
        public double Multiplier { get; set; } = 1.1;
    }

    /// <summary>Optional engineering contract: other weights plus one extra condition; never changes the physics.</summary>
    public class ContractConfig
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>Total × Bonus when the condition is met.</summary>
        public double Bonus { get; set; } = 1.2;
        public ScoreWeights Weights { get; set; }
        /// <summary>Conditions (0 / null = not used): cost ≤ reference × ratio, margin ≥, achievement earned, environment factor ≥.</summary>
        public double MaxCostRatio { get; set; }
        public double MinMargin { get; set; }
        public string RequireAchievement { get; set; }
        public double MinEnvironment { get; set; }
    }

    /// <summary>Engineering Score settings (Data/Scoring/scoring.json); every number is a game balance value.</summary>
    public class ScoringConfig
    {
        public int Version { get; set; } = 2;
        public ScoreWeights Weights { get; set; } = new();
        public double ReferenceCostRatio { get; set; } = 0.9;
        public double EconomyExponent { get; set; } = 1.5;
        public double SafetyTarget { get; set; } = 0.3;
        public double SafetyCap { get; set; } = 1.25;
        public double EnvironmentFloor { get; set; } = 0.2;
        public double PierDisturbance { get; set; } = 6;
        public double ImmersedDisturbance { get; set; } = 1;
        public double CableDisturbance { get; set; } = 0.5;
        public double ShieldDisturbance { get; set; }
        public Dictionary<string, AchievementConfig> Achievements { get; set; } = new();
        public double ExtremeFloodFactor { get; set; } = 1.4;
        public string HeavyVehicle { get; set; } = "truck_heavy";
        public double LowDisturbanceMin { get; set; } = 0.8;
        public double HighReliabilityMaxUtil { get; set; } = 0.7;
        /// <summary>heavy_load is only offered when the level already has a road vehicle of at least this share of
        /// the heavy vehicle's mass (a light-traffic level is not built for it, so the challenge would not be fair).</summary>
        public double HeavyLoadMinMassRatio { get; set; } = 0.5;
        public Dictionary<string, ContractConfig> Contracts { get; set; } = new();
        public ContractConfig Contract(string id) => id != null && Contracts.TryGetValue(id, out var c) ? c : null;

        public static ScoringConfig Default() => new()
        {
            Achievements = new()
            {
                ["extreme_flood"] = new() { Name = Loc.T("极端洪水"), Multiplier = 1.15 },
                ["heavy_load"] = new() { Name = Loc.T("额外重载"), Multiplier = 1.1 },
                ["channel_protection"] = new() { Name = Loc.T("航道保护"), Multiplier = 1.1 },
                ["low_disturbance"] = new() { Name = Loc.T("低环境扰动"), Multiplier = 1.05 },
                ["high_reliability"] = new() { Name = Loc.T("高可靠性"), Multiplier = 1.1 },
            },
        };
    }

    /// <summary>One engineering achievement of a finished test.</summary>
    public class AchievementResult
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double Multiplier { get; set; }
        public bool Earned { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Uncapped Engineering Score (replaces the old 6:2:1:1 score in every level, bridges and tunnels alike).
    /// Base = 1000 × Σ weight × factor × deck integrity; the reference design lands near 1000. Earned
    /// achievements (configured per level, each counted once) multiply the base. Only a test that passed every
    /// vehicle and weather check gets an official score; the scoring never changes the physics result.
    /// </summary>
    public static class EngineeringScore
    {
        private static GameDatabase _db;
        public static void Bind(GameDatabase db) => _db = db;
        public static ScoringConfig Config => _db?.Scoring ?? ScoringConfig.Default();

        public static double Margin(double util) => util > 1e-9 ? 1 / Math.Clamp(util, 1e-9, 1) - 1 : 9.99;

        /// <summary>Base score and the five factors from the measured test data (no achievements).</summary>
        public static void ComputeBase(SimulationResult r)
        {
            var c = Config;
            r.ScoreVersion = c.Version;
            r.Achievements.Clear();
            if (!r.IsSuccess) { r.Score = 0; r.BaseScore = 0; return; }
            double refCost = r.ReferenceCost > 0 ? r.ReferenceCost : r.Budget * c.ReferenceCostRatio;
            r.EconomyFactor = refCost > 0 ? Math.Pow(refCost / Math.Max(r.TotalCost, 1), c.EconomyExponent) : 1;
            r.SafetyMargin = Margin(r.MaxUtilization);
            r.SafetyFactor = Math.Min(c.SafetyCap, r.SafetyMargin / c.SafetyTarget);
            r.WeatherFactor = double.IsNaN(r.WeatherMargin) ? r.SafetyFactor : Math.Min(c.SafetyCap, r.WeatherMargin / c.SafetyTarget);
            r.TrafficFactor = r.ReferenceTime > 0 && r.CompletionTime > 0 ? Math.Min(1, r.ReferenceTime / r.CompletionTime) : 1;
            r.EnvironmentFactor = r.RiverWidth > 0 ? Math.Max(c.EnvironmentFloor, 1 - r.Disturbance / r.RiverWidth) : 1;
            var w = c.Contract(r.Contract)?.Weights ?? c.Weights; double integrity = Math.Clamp(r.DeckIntegrity, 0, 1);
            double k = 1000 * integrity / Math.Max(w.Sum, 1e-9);
            r.EconomyPoints = (int)Math.Round(k * w.Economy * r.EconomyFactor);
            r.SafetyPoints = (int)Math.Round(k * w.Safety * r.SafetyFactor);
            r.WeatherPoints = (int)Math.Round(k * w.Weather * r.WeatherFactor);
            r.TrafficPoints = (int)Math.Round(k * w.Traffic * r.TrafficFactor);
            r.EnvironmentPoints = (int)Math.Round(k * w.Environment * r.EnvironmentFactor);
            r.BaseScore = r.EconomyPoints + r.SafetyPoints + r.WeatherPoints + r.TrafficPoints + r.EnvironmentPoints;
            r.Score = r.BaseScore;
        }

        /// <summary>Contracts a level offers: every contract whose required achievement (if any) the level offers.</summary>
        public static List<string> ContractsFor(LevelDefinition level)
        {
            var c = Config; var ach = AchievementsFor(level); var list = new List<string>();
            foreach (var (id, cc) in c.Contracts)
                if (cc.RequireAchievement == null || ach.Contains(cc.RequireAchievement)) list.Add(id);
            return list;
        }

        /// <summary>Achievements a level offers: Objectives.Achievements, or chosen from the level's conditions.</summary>
        public static List<string> AchievementsFor(LevelDefinition level)
        {
            if (level.Objectives.Achievements is { Count: > 0 } set) return set.ToList();
            var c = Config; var vehicles = _db?.Vehicles;
            bool flood = level.TrafficEvents.Any(e => e.Type == TrafficEventType.Environment);
            bool ships = level.TrafficEvents.Any(e => e.Type == TrafficEventType.Ship);
            double heaviest = 0, heavy = 0;
            if (vehicles != null)
            {
                foreach (var e in level.TrafficEvents)
                    if (e.Type == TrafficEventType.Vehicle && e.VehicleId != null && vehicles.TryGetValue(e.VehicleId, out var v) && !RailTrack.IsTrain(v))
                        heaviest = Math.Max(heaviest, v.Mass);
                if (vehicles.TryGetValue(c.HeavyVehicle, out var hv)) heavy = hv.Mass;
            }
            var list = new List<string>();
            if (flood) list.Add("extreme_flood");
            if (ships) list.Add("channel_protection");
            if (heavy > 0 && heaviest >= c.HeavyLoadMinMassRatio * heavy) list.Add("heavy_load");
            list.Add("low_disturbance");
            list.Add("high_reliability");
            return list;
        }

        /// <summary>
        /// Check the level's achievements for a passed test (extra re-tests for extreme flood and extra heavy load)
        /// and apply their multipliers once each. Call after the main test passed.
        /// </summary>
        public static void EvaluateAchievements(BridgeSimulation sim)
        {
            var r = sim.Result; var c = Config;
            if (!r.IsSuccess) return;
            if (r.BaseScore <= 0) ComputeBase(r);
            r.Achievements.Clear();
            double mult = 1;
            var contract = c.Contract(r.Contract);
            var ids = AchievementsFor(sim.Level).ToList();
            // A contract's required achievement is always checked (and counts once like any other)
            if (contract?.RequireAchievement != null && !ids.Contains(contract.RequireAchievement)) ids.Add(contract.RequireAchievement);
            foreach (var id in ids.Distinct())
            {
                if (!c.Achievements.TryGetValue(id, out var ac)) continue;
                var a = new AchievementResult { Id = id, Name = ac.Name, Multiplier = ac.Multiplier };
                switch (id)
                {
                    case "extreme_flood":
                    {
                        var lv = CloneLevel(sim.Level);
                        foreach (var e in lv.TrafficEvents.Where(e => e.Type == TrafficEventType.Environment))
                        {
                            double rise = sim.Db.Weather.RiseOf(e);
                            e.Weather ??= "flood"; e.Rise = rise * c.ExtremeFloodFactor;
                        }
                        var res = new BridgeSimulation(sim.Design, lv, sim.Db).RunToEnd();
                        a.Earned = res.IsSuccess;
                        a.Note = res.IsSuccess ? Loc.F("洪水水位 ×{0:0.0} 仍然通过", (c.ExtremeFloodFactor)) : Loc.F("洪水水位 ×{0:0.0} 时失败：{1}", (c.ExtremeFloodFactor), (FirstLine(res.FailureMessage)));
                        break;
                    }
                    case "heavy_load":
                    {
                        if (!sim.Db.Vehicles.TryGetValue(c.HeavyVehicle, out var hv)) continue;
                        var lv = CloneLevel(sim.Level);
                        double last = lv.TrafficEvents.Where(e => e.Type == TrafficEventType.Vehicle).Select(e => e.Time).DefaultIfEmpty(0).Max();
                        lv.TrafficEvents.Add(new TrafficEvent { Time = last + 2, VehicleId = hv.Id });
                        var res = new BridgeSimulation(sim.Design, lv, sim.Db).RunToEnd();
                        a.Earned = res.IsSuccess;
                        a.Note = res.IsSuccess ? Loc.F("追加一辆{0}仍然通过", (hv.Name)) : Loc.F("追加{0}时失败：{1}", (hv.Name), (FirstLine(res.FailureMessage)));
                        break;
                    }
                    case "channel_protection":
                        a.Earned = sim.Ships.Count > 0 && sim.Ships.All(s => s.Done) && sim.Bascule.Leaves.Count == 0;
                        a.Note = a.Earned ? Loc.T("船只全部通过，航道始终畅通（不用开桥）") : Loc.T("船只要靠开启桥才能通过");
                        break;
                    case "low_disturbance":
                        a.Earned = r.EnvironmentFactor >= c.LowDisturbanceMin - 1e-9;
                        a.Note = Loc.F("环境系数 {0:0.00}（至少 {1:0.00}）", (r.EnvironmentFactor), (c.LowDisturbanceMin));
                        break;
                    case "high_reliability":
                        a.Earned = r.MaxUtilization <= c.HighReliabilityMaxUtil + 1e-9;
                        a.Note = Loc.F("最大利用率 {0:0}%（不超过 {1:0}%）", (r.MaxUtilization * 100), (c.HighReliabilityMaxUtil * 100));
                        break;
                    default: continue;
                }
                if (a.Earned) mult *= a.Multiplier;
                r.Achievements.Add(a);
            }
            r.AchievementMultiplier = mult;
            r.ContractMet = false; r.ContractNote = "";
            if (contract != null)
            {
                double refCost = r.ReferenceCost > 0 ? r.ReferenceCost : r.Budget * c.ReferenceCostRatio;
                var miss = new List<string>();
                if (contract.MaxCostRatio > 0 && r.TotalCost > refCost * contract.MaxCostRatio + 1e-6)
                    miss.Add(Loc.F("造价 ${0:N0} 超过 ${1:N0}", (r.TotalCost), (refCost * contract.MaxCostRatio)));
                if (contract.MinMargin > 0 && r.SafetyMargin < contract.MinMargin - 1e-9)
                    miss.Add(Loc.F("安全裕量 {0:0}% 不到 {1:0}%", (r.SafetyMargin * 100), (contract.MinMargin * 100)));
                if (contract.RequireAchievement != null && !r.Achievements.Any(a => a.Id == contract.RequireAchievement && a.Earned))
                    miss.Add(Loc.F("没有拿到“{0}”", (c.Achievements.TryGetValue(contract.RequireAchievement, out var ra) ? ra.Name : contract.RequireAchievement)));
                if (contract.MinEnvironment > 0 && r.EnvironmentFactor < contract.MinEnvironment - 1e-9)
                    miss.Add(Loc.F("环境系数 {0:0.00} 不到 {1:0.00}", (r.EnvironmentFactor), (contract.MinEnvironment)));
                r.ContractMet = miss.Count == 0;
                r.ContractNote = r.ContractMet ? Loc.F("{0}完成 ×{1:0.00}", (contract.Name), (contract.Bonus)) : Loc.F("{0}未完成：{1}", (contract.Name), (string.Join(Loc.T("；"), miss)));
                if (r.ContractMet) mult *= contract.Bonus;
            }
            r.Score = (int)Math.Round(r.BaseScore * mult);
        }

        /// <summary>Record key of a level: one board per contract (no contract = the level id).</summary>
        public static string RecordKey(string levelId, string contract) => contract == null ? levelId : levelId + "#" + contract;

        private static string FirstLine(string s) => string.IsNullOrEmpty(s) ? "" : s.Split('\n')[0];

        private static LevelDefinition CloneLevel(LevelDefinition l) =>
            System.Text.Json.JsonSerializer.Deserialize<LevelDefinition>(System.Text.Json.JsonSerializer.Serialize(l, GameDatabase.JsonOptions), GameDatabase.JsonOptions);
    }

    /// <summary>Best designs of one level for one scoring version (multi-dimension local leaderboard).</summary>
    public class LevelRecord
    {
        public int Version { get; set; }
        public int BestScore { get; set; }
        public double LowestCost { get; set; }
        public double FastestTime { get; set; }
        public double BestMargin { get; set; } = -1;
        public double LowestDisturbance { get; set; } = -1;

        /// <summary>Kinds this result improves: "score", "cost", "time", "margin".</summary>
        public List<string> Improve(SimulationResult r)
        {
            var kinds = new List<string>();
            if (!r.IsSuccess) return kinds;
            if (r.Score > BestScore) { BestScore = r.Score; kinds.Add("score"); }
            if (LowestCost <= 0 || r.TotalCost < LowestCost - 1e-6) { LowestCost = r.TotalCost; kinds.Add("cost"); }
            if (FastestTime <= 0 || r.CompletionTime < FastestTime - 1e-6) { FastestTime = r.CompletionTime; kinds.Add("time"); }
            if (r.SafetyMargin > BestMargin + 1e-9) { BestMargin = r.SafetyMargin; kinds.Add("margin"); }
            if (LowestDisturbance < 0 || r.Disturbance < LowestDisturbance - 1e-6) { LowestDisturbance = r.Disturbance; kinds.Add("env"); }
            return kinds;
        }

        public static string KindName(string kind) => kind switch
        {
            "score" => Loc.T("★ 最高综合分"), "cost" => Loc.T("★ 最低造价"), "time" => Loc.T("★ 最快通行"), "env" => Loc.T("★ 最低环境影响"), _ => Loc.T("★ 最高安全裕量"),
        };
    }
}
