using System;
using System.Collections.Generic;

namespace BridgeArchitect3D.Data
{
    /// <summary>构件类型：梁、钢索（仅受拉）、桥面（可通车）、立柱（有基础与倾角限制）。</summary>
    public enum ComponentType { Beam, Cable, Deck, Column }

    /// <summary>立柱的基础要求：落在陆地上，或必须从地图锚点起建。</summary>
    public enum BaseRule { None, Land, Anchor, Pier }

    /// <summary>桥墩基础类型：浅基础（便宜，软土承载低、怕冲刷）与桩基础（昂贵，承载高、不怕冲刷）。</summary>
    public enum FoundationType { None, Shallow, Pile }

    /// <summary>支撑约束类型：铰支（固定 X、Y），滚动支座（仅固定 Y）。</summary>
    public enum SupportType { None, Pinned, RollerX }

    /// <summary>
    /// 材料定义。全部材料参数集中在 Data/Materials/materials.json，禁止在其他模块硬编码。
    /// 单位：费用 $/m，密度 kg/m3，模量与强度 Pa，截面积 m2，惯性矩 m4，抗弯承载力 N*m，长度 m。
    /// </summary>
    public class MaterialDefinition
    {
        public string MaterialId { get; set; }
        public string Name { get; set; }
        public ComponentType ComponentType { get; set; }
        public double CostPerMeter { get; set; }
        public double Density { get; set; }
        public double ElasticModulus { get; set; }
        public double CrossSectionArea { get; set; }
        /// <summary>截面惯性矩，用于欧拉屈曲校核（0 表示不校核）。</summary>
        public double SecondMomentOfArea { get; set; }
        public double TensileStrength { get; set; }
        public double CompressiveStrength { get; set; }
        public double MaximumLength { get; set; }
        /// <summary>十六进制颜色，例如 #8FA3B8。</summary>
        public string Color { get; set; }
        public double VisualThickness { get; set; }
        /// <summary>桥面宽度（仅 Deck）。</summary>
        public double DeckWidth { get; set; }
        /// <summary>抗弯承载力 N·m（刚接构件；桥面的该值即 LoadLimit）。0 表示不校核弯曲。</summary>
        public double BendingCapacity { get; set; }
        /// <summary>两端铰接（只传轴力，不传弯矩），例如液压杆。钢索总是铰接。</summary>
        public bool PinnedEnds { get; set; }
        /// <summary>立柱基础要求。</summary>
        public BaseRule BaseRule { get; set; } = BaseRule.None;
        /// <summary>立柱允许偏离竖直方向的最大角度（度）；超过则测试开始时倒塌。0 表示不限制。</summary>
        public double MaxTiltDegrees { get; set; }
        /// <summary>可伸缩（液压杆）：建造模式下可调整自身长度。</summary>
        public bool Extendable { get; set; }
        /// <summary>船只通行时液压杆自动收缩的比例（相对原长），船通过后自动恢复。</summary>
        public double RetractRatio { get; set; }
        /// <summary>说明文字（工具提示）。</summary>
        public string Description { get; set; }
        /// <summary>桥墩材料：用“桥墩工具”从河床立起，自动生成墩底基础、中部和顶部两个支点。</summary>
        public bool IsPier { get; set; }
        /// <summary>怕洪水（木桥墩）：洪水来时被冲毁。</summary>
        public bool FloodVulnerable { get; set; }
        /// <summary>开裂利用率阈值（钢筋混凝土）：超过时显示“开裂”，提示接近失效。0 表示无开裂阶段。</summary>
        public double CrackRatio { get; set; }
        /// <summary>预应力等效上托荷载 N/m（预应力混凝土梁）：抵消部分自重与车辆弯矩，并产生上拱。</summary>
        public double PrestressLoad { get; set; }
        /// <summary>外观：layered（层板纹理）、igirder（I 形梁）等，只影响显示。</summary>
        public string Look { get; set; }

        public bool IsColumn => ComponentType == ComponentType.Column;

        public bool IsDeck => ComponentType == ComponentType.Deck;
        public bool IsCable => ComponentType == ComponentType.Cable;
        /// <summary>单位长度质量 kg/m。</summary>
        public double MassPerMeter => Density * CrossSectionArea;
        /// <summary>抗拉承载力 N。</summary>
        public double TensionCapacity => TensileStrength * CrossSectionArea;

        /// <summary>抗压承载力 N：取材料压溃与欧拉屈曲（两端铰接）的较小值。钢索为 0。</summary>
        public double CompressionCapacity(double length)
        {
            if (IsCable) return 0;
            double crush = CompressiveStrength * CrossSectionArea;
            if (SecondMomentOfArea <= 0 || length <= 1e-6) return crush;
            double euler = System.Math.PI * System.Math.PI * ElasticModulus * SecondMomentOfArea / (length * length);
            return System.Math.Min(crush, euler);
        }
    }

    public class AxleDefinition
    {
        /// <summary>相对车头轴的纵向偏移（m，向后为负）。</summary>
        public double Offset { get; set; }
        /// <summary>该轴承担的整车质量比例。</summary>
        public double LoadFraction { get; set; }
    }

    public class VehicleDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>整车质量 kg。</summary>
        public double Mass { get; set; }
        /// <summary>行驶速度 m/s。</summary>
        public double Speed { get; set; }
        public double Wheelbase { get; set; }
        public double Length { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Color { get; set; }
        public List<AxleDefinition> Axles { get; set; } = new();
        /// <summary>外观类型：car / truck / bus / tank / train / pedestrians（只影响显示）。</summary>
        public string Kind { get; set; } = "car";
        /// <summary>多节车辆（火车）的节数与单节长度 m；行人队伍为人数。</summary>
        public int Units { get; set; } = 1;
        public double UnitLength { get; set; }
        /// <summary>跟车最小间距 m（同一条道路上后车不会靠前车更近）。</summary>
        public double FollowGap { get; set; } = 3;
    }

    public class AnchorDefinition
    {
        public string Id { get; set; }
        public Vec3d Position { get; set; }
        /// <summary>最大允许支反力 N。</summary>
        public double MaxLoad { get; set; }
        public bool Enabled { get; set; } = true;
        public SupportType SupportType { get; set; } = SupportType.Pinned;
        public bool IsRoadStart { get; set; }
        public bool IsRoadEnd { get; set; }
        /// <summary>道路层号（0 = 主路 / 下层，1 = 上层立交）。</summary>
        public int RoadLane { get; set; }
    }

    public class PillarConfig
    {
        public double X { get; set; }
        public double TopY { get; set; }
        public double Width { get; set; }
    }

    public class TerrainConfig
    {
        public double RiverWidth { get; set; }
        public double LeftBankHeight { get; set; }
        public double RightBankHeight { get; set; }
        public double WaterLevel { get; set; }
        public double RiverBedDepth { get; set; }
        public double BuildMinY { get; set; }
        public double BuildMaxY { get; set; }
        public double BuildMarginX { get; set; } = 3;
        public List<PillarConfig> Pillars { get; set; } = new();
        /// <summary>禁止建墩区（航道 / 生态保护区）：区内道路标高以下不能放置节点。</summary>
        public List<NoBuildZone> NoBuildZones { get; set; } = new();
        /// <summary>河床地基分区（软土 / 岩石，可带冲刷）。未覆盖的河床按 DefaultSoil 处理。</summary>
        public List<FoundationZone> FoundationZones { get; set; } = new();
        public string DefaultSoil { get; set; } = "rock";

        public FoundationZone ZoneAt(double x)
        {
            foreach (var z in FoundationZones) if (x >= z.X0 && x <= z.X1) return z;
            return new FoundationZone { X0 = x, X1 = x, Soil = DefaultSoil };
        }
    }

    /// <summary>地基分区：Soil = rock / soft；Scour = 洪水时会被冲刷掏空的区域。</summary>
    public class FoundationZone
    {
        public double X0 { get; set; }
        public double X1 { get; set; }
        public string Soil { get; set; } = "rock";
        public bool Scour { get; set; }
        public bool IsRock => Soil == "rock";
        public string Name => (IsRock ? "岩石" : "软土") + (Scour ? "·冲刷区" : "");
    }

    /// <summary>地基与基础参数（集中定义）：费用 $ / 座，承载力 N。</summary>
    public static class FoundationRules
    {
        public const double ShallowCost = 1500, PileCost = 6000;
        public static double Capacity(FoundationZone z, FoundationType f) =>
            f == FoundationType.Pile ? 4.0e6 : z.IsRock ? 3.0e6 : 4.0e5;
        public static double Cost(FoundationType f) => f == FoundationType.Pile ? PileCost : f == FoundationType.Shallow ? ShallowCost : 0;
        public static string Name(FoundationType f) => f == FoundationType.Pile ? "桩基础" : "浅基础";
    }

    public class NoBuildZone
    {
        public double X0 { get; set; }
        public double X1 { get; set; }
        /// <summary>该高度以下禁止建造（通常为两岸道路标高）。</summary>
        public double BelowY { get; set; }
        public string Name { get; set; } = "禁止建墩区";
    }

    /// <summary>交通事件类型。预留火车、船舶、环境事件扩展（TrafficEventSystem）。</summary>
    public enum TrafficEventType { Vehicle, Train, Ship, Environment }

    public class TrafficEvent
    {
        public double Time { get; set; }
        public TrafficEventType Type { get; set; } = TrafficEventType.Vehicle;
        public string VehicleId { get; set; }
        /// <summary>车辆行驶的道路层号（立交桥：0 下层，1 上层）。</summary>
        public int Lane { get; set; }
        // ---- 船只事件参数（Type = Ship）：船沿河道（Z 方向）垂直穿过桥位 ----
        /// <summary>航道中心线 X 坐标 m。</summary>
        public double LaneX { get; set; }
        /// <summary>船宽 m（航道占用宽度）。</summary>
        public double Width { get; set; } = 4;
        /// <summary>船只水面以上高度 m（所需净空）。</summary>
        public double Height { get; set; } = 3;
        /// <summary>船长 m。</summary>
        public double Length { get; set; } = 10;
        /// <summary>航速 m/s。</summary>
        public double Speed { get; set; } = 4;
        public string Name { get; set; } = "货船";
        // ---- 洪水事件参数（Type = Environment）：Height 为水位上涨高度，Duration 为持续时间 ----
        public double Duration { get; set; } = 10;
    }

    public class LevelObjectives
    {
        public double MaxDeckDeflection { get; set; } = 0.3;
        public double MaxDeckSlope { get; set; } = 0.15;
        public int MaxBrokenMembers { get; set; } = 2;
        public double Star3BudgetRatio { get; set; } = 0.7;
        public double Star2BudgetRatio { get; set; } = 0.9;
        public double Star3MaxUtilization { get; set; } = 0.8;
        public double Star2MaxUtilization { get; set; } = 0.95;
    }

    public class LevelDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public double Budget { get; set; }
        public TerrainConfig Terrain { get; set; } = new();
        public List<AnchorDefinition> Anchors { get; set; } = new();
        public List<string> AllowedMaterials { get; set; } = new();
        public List<TrafficEvent> TrafficEvents { get; set; } = new();
        public LevelObjectives Objectives { get; set; } = new();
        /// <summary>起始设计（Data/StartDesigns/{名}.json），没有存档时载入，例如缺一根斜撑的教学桥。</summary>
        public string StartDesign { get; set; }
        /// <summary>载入起始设计时的提示。</summary>
        public string StartHint { get; set; }
        /// <summary>参考方案第一步：思路提示。</summary>
        public string ReferenceHint { get; set; }
        /// <summary>限量材料：材料 Id → 最多可用根数（例如大型胶合木梁）。</summary>
        public Dictionary<string, int> MaterialLimits { get; set; } = new();
        /// <summary>加固关：最多可拆除的旧构件数量（-1 表示不限）。</summary>
        public int MaxDemolitions { get; set; } = -1;
        /// <summary>拆除旧构件的费用 $/m。</summary>
        public double DemolitionCostPerMeter { get; set; } = 60;
        /// <summary>修复旧构件的费用比例（相对同材料新建费用）。</summary>
        public double RepairCostRatio { get; set; } = 0.5;

        /// <summary>关卡标签（例如“入门”“进阶”），显示在关卡选择中。</summary>
        public string Difficulty { get; set; }

        /// <summary>判断点是否位于陆地上（两岸地面及以下，或河中岩墩顶面及以下）。</summary>
        public bool IsOnLand(double x, double y)
        {
            const double eps = 0.05;
            if (x <= eps && y <= Terrain.LeftBankHeight + eps) return true;
            if (x >= Terrain.RiverWidth - eps && y <= Terrain.RightBankHeight + eps) return true;
            foreach (var p in Terrain.Pillars)
                if (Math.Abs(x - p.X) <= p.Width / 2 + eps && y <= p.TopY + eps) return true;
            return false;
        }

        /// <summary>判断点是否位于地形实体内部（两岸土体或河中岩墩），这些位置禁止创建节点。</summary>
        public bool IsInsideTerrain(double x, double y)
        {
            const double eps = 0.01;
            if (x < -eps && y < Terrain.LeftBankHeight - eps) return true;
            if (x > Terrain.RiverWidth + eps && y < Terrain.RightBankHeight - eps) return true;
            foreach (var p in Terrain.Pillars)
                if (x > p.X - p.Width / 2 + eps && x < p.X + p.Width / 2 - eps && y < p.TopY - eps) return true;
            return false;
        }
    }

    public class MaterialFile { public List<MaterialDefinition> Materials { get; set; } = new(); }
    public class VehicleFile { public List<VehicleDefinition> Vehicles { get; set; } = new(); }
    public class LevelFile { public List<LevelDefinition> Levels { get; set; } = new(); }
}
