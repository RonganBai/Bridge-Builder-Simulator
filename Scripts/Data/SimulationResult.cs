using System.Collections.Generic;

namespace BridgeArchitect3D.Data
{
    public enum FailureReason
    {
        None,
        RoadNotConnected,
        TensionFailure,
        CompressionFailure,
        DeckBendingFailure,
        ShipCollision,
        FloodScour,
        AnchorOverload,
        DeckSpanTooLong,
        DeckDeflectionExceeded,
        StructureUnstable,
        VehicleBlocked,
        ProgressiveCollapse,
        OverBudget,
        Derailed,
        WrongSurface,
        /// <summary>A tunnel element was overstressed (material strength); the name is kept for old saves and tests.</summary>
        TunnelFlooded,
        /// <summary>A bank lift platform carried more than it can.</summary>
        LiftOverload,
    }

    /// <summary>The four phases of every test: static (no traffic, no weather), vehicles, environment (weather events), result.</summary>
    public enum TestPhase { Static, Vehicles, Environment, Result }

    public class VehicleResult
    {
        public string VehicleId { get; set; }
        public string Name { get; set; }
        public bool Passed { get; set; }
        public double FinishTime { get; set; }
    }

    /// <summary>一次通车测试的结果。</summary>
    public class SimulationResult
    {
        public bool IsSuccess { get; set; }
        public FailureReason FailureReason { get; set; } = FailureReason.None;
        public string FailureMessage { get; set; } = "";
        public double FailureTime { get; set; }
        public List<int> InvolvedMemberIds { get; set; } = new();
        public List<string> Suggestions { get; set; } = new();
        /// <summary>一句话说明发生了什么（例如“轿车在桥中间压断了桥面”）。</summary>
        public string Headline { get; set; } = "";
        /// <summary>针对出问题位置的一条具体修改建议。</summary>
        public string TopSuggestion { get; set; } = "";
        /// <summary>出问题的位置（X 坐标 m），用于定位。</summary>
        public double ProblemX { get; set; } = double.NaN;
        public double MaxUtilization { get; set; }
        // ---- Phase-4 result evaluation ----
        /// <summary>Phase in which the test failed (Result when it passed).</summary>
        public TestPhase FailurePhase { get; set; } = TestPhase.Result;
        /// <summary>Peak utilisation in each phase (Static, Vehicles, Environment); -1 = the phase did not happen.</summary>
        public double[] PhaseMaxUtil { get; set; } = { -1, -1, -1 };
        /// <summary>Weakest place over the whole test: member id (or -1) and a readable description.</summary>
        public int WeakestMemberId { get; set; } = -1;
        public string WeakestText { get; set; } = "";
        public double WeakestX { get; set; } = double.NaN;
        /// <summary>Largest elastic deck sag (m) and largest tunnel displacement (settlement / sway, m).</summary>
        public double MaxDeckSag { get; set; }
        public double MaxTunnelMove { get; set; }
        /// <summary>Safety margin during weather events: capacity / peak demand − 1 (NaN = no weather event).</summary>
        public double WeatherMargin { get; set; } = double.NaN;
        public List<VehicleResult> VehicleResults { get; set; } = new();
        public double TotalCost { get; set; }
        public double Budget { get; set; }
        public double CompletionTime { get; set; }
        public int ShipsPassed { get; set; }
        /// <summary>测试中出现裂缝的构件数。</summary>
        public int CrackedMembers { get; set; }
        public int TotalShips { get; set; }

        // ---- 评分（上不封顶）：预算分 + 桥面完整度分 + 受力利用分 ----
        /// <summary>通过后桥面完整度（未断裂桥面长度 / 桥面总长度，0~1）。</summary>
        public double DeckIntegrity { get; set; }
        /// <summary>预算分 = 1000 × 提供预算 / 使用预算（用得越少越高，不封顶）。</summary>
        public int BudgetScore { get; set; }
        /// <summary>Time score: ideal finish time (every vehicle / ship at full speed, no waiting) / actual time.</summary>
        public int TimeScore { get; set; }
        /// <summary>Ideal finish time in s (0 = unknown, the time score is then full).</summary>
        public double ReferenceTime { get; set; }
        /// <summary>完整度分 = 1000 × 桥面完整度。</summary>
        public int IntegrityScore { get; set; }
        /// <summary>受力分 = 1000 × 最大受力利用率（材料用得越充分越高）。</summary>
        public int StressScore { get; set; }
        /// <summary>安全冗余 = 承载力 / 峰值需求 − 1（上限 150%）。</summary>
        public double SafetyMargin { get; set; }
        /// <summary>受力分是否取自安全冗余（否则取自利用率）。</summary>
        public bool StressUsesMargin { get; set; }
        public int Score { get; set; }
        // ---- Engineering Score (version 2, uncapped; see EngineeringScore) ----
        public int ScoreVersion { get; set; }
        public int BaseScore { get; set; }
        public double ReferenceCost { get; set; }
        public double RiverWidth { get; set; }
        /// <summary>Riverbed / ground disturbance length, m (piers, immersed tube, anchors, bored tube).</summary>
        public double Disturbance { get; set; }
        public double EconomyFactor { get; set; }
        public double SafetyFactor { get; set; }
        public double WeatherFactor { get; set; }
        public double TrafficFactor { get; set; }
        public double EnvironmentFactor { get; set; }
        public int EconomyPoints { get; set; }
        public int SafetyPoints { get; set; }
        public int WeatherPoints { get; set; }
        public int TrafficPoints { get; set; }
        public int EnvironmentPoints { get; set; }
        public double AchievementMultiplier { get; set; } = 1;
        /// <summary>Engineering contract of the tested design (null = none) and whether its condition was met.</summary>
        public string Contract { get; set; }
        public bool ContractMet { get; set; }
        public string ContractNote { get; set; } = "";
        public List<AchievementResult> Achievements { get; set; } = new();

        public int PassedVehicles
        {
            get { int c = 0; foreach (var v in VehicleResults) if (v.Passed) c++; return c; }
        }
    }
}
