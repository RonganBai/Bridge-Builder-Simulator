using System;

namespace BridgeArchitect3D.Physics
{
    /// <summary>作用在某根桥面构件上的一个车轴集中荷载。</summary>
    public struct AxleLoad
    {
        public int MemberIndex;   // 模型中的构件下标
        public double T;          // 沿构件位置 0..1（从 A 端起）
        public double Force;      // 竖向力 N（向下为正）
    }

    /// <summary>
    /// 载荷计算：构件自重（含桥面）按质量平均分配到两端节点，车轴荷载按位置线性分配到所在桥面构件的两端节点。
    /// 断裂构件不再传递自重。
    /// </summary>
    public static class LoadCalculator
    {
        public static void Compute(StructuralModel m, AxleLoad[] axles, int axleCount, double[] loads)
        {
            Array.Clear(loads, 0, loads.Length);
            if (m.NodeLoadY != null) for (int i = 0; i < m.NodeLoadY.Length && 2 * i + 1 < loads.Length; i++) loads[2 * i + 1] += m.NodeLoadY[i];
            for (int k = 0; k < m.MemberCount; k++)
            {
                if (m.Broken[k]) continue;
                double w = m.Mass[k] * StructuralModel.Gravity * 0.5;
                loads[2 * m.A[k] + 1] -= w;
                loads[2 * m.B[k] + 1] -= w;
            }
            for (int i = 0; i < axleCount; i++)
            {
                var a = axles[i];
                if (a.MemberIndex < 0 || m.Broken[a.MemberIndex]) continue;
                loads[2 * m.A[a.MemberIndex] + 1] -= a.Force * (1 - a.T);
                loads[2 * m.B[a.MemberIndex] + 1] -= a.Force * a.T;
            }
        }

        /// <summary>
        /// 强制伸缩（液压杆）的等效节点力：F = EA/L·δ，沿构件轴线把两端推开（δ&gt;0）或拉近（δ&lt;0）。
        /// </summary>
        public static void AddPrestrainLoads(StructuralModel m, double[] loads)
        {
            for (int k = 0; k < m.MemberCount; k++)
            {
                double d = m.Prestrain[k];
                if (d == 0 || !m.IsActive(k)) continue;
                double f = m.EA[k] / m.Length[k] * d;
                loads[2 * m.A[k]] -= f * m.Cos[k]; loads[2 * m.A[k] + 1] -= f * m.Sin[k];
                loads[2 * m.B[k]] += f * m.Cos[k]; loads[2 * m.B[k] + 1] += f * m.Sin[k];
            }
        }

        /// <summary>
        /// 刚接构件沿长度的最大弯矩绝对值（N·m）：
        /// M(x) = 简支弯矩（自重 + 构件内车轴荷载，取垂直于构件的分量） + 两端弯矩的线性插值。
        /// 端弯矩来自刚架求解；荷载按线性分配到节点，因此这是适合游戏的近似算法。
        /// </summary>
        public static double MemberMaxMoment(StructuralModel m, int k, AxleLoad[] axles, int axleCount, double momentA, double momentB)
        {
            double L = m.Length[k];
            double perp = Math.Abs(m.Cos[k]);                         // 竖向荷载垂直于构件的比例
            double w = m.Mass[k] / L * StructuralModel.Gravity * perp; // N/m
            // 预应力梁：预应力等效为向上的均布荷载，抵消一部分下挠弯矩（放置后上拱的来源）
            w -= m.Mat[k].PrestressLoad * perp;
            double best = 0;
            for (int i = -3; i < axleCount; i++)
            {
                double xi;
                if (i == -3) xi = 0; else if (i == -2) xi = L; else if (i == -1) xi = L / 2;
                else { if (axles[i].MemberIndex != k) continue; xi = axles[i].T * L; }
                double xr = xi / L;
                double mom = w * xi * (L - xi) / 2.0 - momentA * (1 - xr) + momentB * xr;
                for (int j = 0; j < axleCount; j++)
                {
                    if (axles[j].MemberIndex != k) continue;
                    double xj = axles[j].T * L;
                    mom += axles[j].Force * perp * (xi <= xj ? xi * (L - xj) / L : xj * (L - xi) / L);
                }
                best = Math.Max(best, Math.Abs(mom));
            }
            return best;
        }

        /// <summary>
        /// 桥面局部弯矩：把桥面构件视为两端简支梁，计算自重均布荷载与车轴集中荷载共同作用下的最大弯矩（N·m）。
        /// </summary>
        public static double DeckMaxMoment(StructuralModel m, int k, AxleLoad[] axles, int axleCount)
        {
            double L = m.Length[k];
            double w = m.Mass[k] / L * StructuralModel.Gravity;   // N/m
            double best = w * L * L / 8.0;
            for (int i = 0; i < axleCount; i++)
            {
                if (axles[i].MemberIndex != k) continue;
                double xi = axles[i].T * L;
                double mom = w * xi * (L - xi) / 2.0;
                for (int j = 0; j < axleCount; j++)
                {
                    if (axles[j].MemberIndex != k) continue;
                    double xj = axles[j].T * L;
                    mom += axles[j].Force * (xi <= xj ? xi * (L - xj) / L : xj * (L - xi) / L);
                }
                best = Math.Max(best, mom);
            }
            return best;
        }
    }
}
