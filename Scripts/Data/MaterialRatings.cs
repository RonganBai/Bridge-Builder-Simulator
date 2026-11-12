using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    /// <summary>建材性能面板的一项指标。</summary>
    public struct MaterialStat
    {
        public string Label;   // 指标名
        public double Value;   // 数值（已换成面板单位）
        public string Unit;    // 面板单位
        public int Cells;      // 进度条格数 0~5
    }

    /// <summary>
    /// 建材性能评级（纯逻辑，不依赖 Godot）：把材料数据换算成玩家容易比较的单位（力一律用“吨”），
    /// 并按全部材料的取值范围换算成 5 格进度条。
    /// </summary>
    public static class MaterialRatings
    {
        public const int MaxCells = 5;
        private const double G = 9.81;

        /// <summary>牛顿 → 吨（1 吨 = 9.81 kN）。</summary>
        public static double Tonnes(double newton) => newton / G / 1000;

        /// <summary>
        /// 桥面承载力（吨）：单段桥面取最大长度、两端支撑，整车重量集中作用在跨中时能承受的车重。
        /// 已扣除桥面自重；实际车辆的轴重是分散的，所以这是偏保守的估计。
        /// </summary>
        public static double DeckCapacityTonnes(MaterialDefinition m)
        {
            if (m == null || !m.IsDeck || m.BendingCapacity <= 0 || m.MaximumLength <= 0) return 0;
            double L = m.MaximumLength;
            double selfMoment = m.MassPerMeter * G * L * L / 8;
            return Math.Max(0, Tonnes((m.BendingCapacity - selfMoment) * 4 / L));
        }

        /// <summary>车辆对桥面最集中的重量（吨）：多节列车按单节计算。</summary>
        public static double VehicleTonnes(VehicleDefinition v) => v == null ? 0 : v.Mass / 1000 / Math.Max(1, v.Units);

        /// <summary>原始指标（面板单位）。</summary>
        public static (string label, string unit, Func<MaterialDefinition, double> get)[] Metrics { get; } =
        {
            (Loc.T("价格"), "$/m", m => m.CostPerMeter),
            (Loc.T("自重"), "kg/m", m => m.MassPerMeter),
            (Loc.T("抗拉"), Loc.T("吨"), m => Tonnes(m.TensionCapacity)),
            (Loc.T("抗压"), Loc.T("吨"), m => Tonnes(m.CompressionCapacity(m.MaximumLength))),
            (Loc.T("抗弯"), Loc.T("吨·m"), m => Tonnes(m.BendingCapacity)),
            (Loc.T("最大长度"), "m", m => m.MaximumLength),
        };

        /// <summary>
        /// 计算一种材料的全部指标。格数按 catalog（全部材料）的范围取对数刻度：最小值 1 格、最大值 5 格，0 为 0 格。
        /// 抗压按最大长度计（含屈曲），钢索为 0。
        /// </summary>
        public static List<MaterialStat> StatsFor(MaterialDefinition m, IEnumerable<MaterialDefinition> catalog)
        {
            var all = catalog.ToList();
            var list = new List<MaterialStat>();
            foreach (var (label, unit, get) in Metrics)
            {
                double v = get(m);
                var positive = all.Select(get).Where(x => x > 0).ToList();
                double min = positive.Count > 0 ? positive.Min() : 0, max = positive.Count > 0 ? positive.Max() : 0;
                list.Add(new MaterialStat { Label = label, Unit = unit, Value = v, Cells = LogCells(v, min, max) });
            }
            return list;
        }

        /// <summary>对数刻度格数：v ≤ 0 为 0 格；min → 1 格，max → 5 格。</summary>
        public static int LogCells(double v, double min, double max)
        {
            if (v <= 0) return 0;
            if (max <= min || min <= 0) return MaxCells;
            double t = Math.Log(v / min) / Math.Log(max / min);
            return Math.Clamp(1 + (int)Math.Round(t * (MaxCells - 1)), 1, MaxCells);
        }

        /// <summary>线性刻度格数（用于车重与桥面承载对比）：向上取整，非零至少 1 格。</summary>
        public static int LinearCells(double v, double max)
        {
            if (v <= 0 || max <= 0) return 0;
            return Math.Clamp((int)Math.Ceiling(v / max * MaxCells - 1e-9), 1, MaxCells);
        }
    }
}
