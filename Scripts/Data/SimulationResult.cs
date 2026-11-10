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
    }

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
        /// <summary>完整度分 = 1000 × 桥面完整度。</summary>
        public int IntegrityScore { get; set; }
        /// <summary>受力分 = 1000 × 最大受力利用率（材料用得越充分越高）。</summary>
        public int StressScore { get; set; }
        /// <summary>安全冗余 = 承载力 / 峰值需求 − 1（上限 150%）。</summary>
        public double SafetyMargin { get; set; }
        /// <summary>受力分是否取自安全冗余（否则取自利用率）。</summary>
        public bool StressUsesMargin { get; set; }
        public int Score { get; set; }

        public int PassedVehicles
        {
            get { int c = 0; foreach (var v in VehicleResults) if (v.Passed) c++; return c; }
        }
    }
}
