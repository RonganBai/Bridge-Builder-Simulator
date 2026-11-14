using BridgeArchitect3D.Data;
using System;

namespace BridgeArchitect3D.Physics
{
    /// <summary>
    /// 二维刚架 / 桁架混合刚度矩阵求解器（小变形线性分析）。
    /// - 梁、桥面、混凝土柱：刚接梁单元（轴向 EA/L + 弯曲 EI），节点可传递弯矩，
    ///   因此缺少斜撑的桥面会像悬臂梁 / 连续梁一样受弯，而不是直接成为机构；
    /// - 钢索、液压杆：两端铰接的二力杆，只传递轴力；钢索受压时自动松弛并重新求解；
    /// - 节点自由度 (ux, uy, θ)；只连接铰接杆的节点不含转角自由度；
    /// - 对称正定矩阵消元，主元接近 0 视为机构（约束不足）→ 报告失稳。
    /// 结果完全确定（无随机数），相同输入得到相同输出。
    /// </summary>
    public class StructureSolver : IStructuralSolver
    {
        private const double PivotTolerance = 1e-10;   // 相对主元容差
        private const double MaxDisplacement = 3.0;    // 位移超过 3 m 视为失稳发散
        private const int MaxCableIterations = 12;

        private double[] _k = Array.Empty<double>();
        private double[] _f = Array.Empty<double>();
        private int[] _dofMap = Array.Empty<int>();
        private int[] _degree = Array.Empty<int>();
        private bool[] _hasRigid = Array.Empty<bool>();
        private readonly double[] _kl = new double[36];
        private readonly double[] _kg = new double[36];
        private readonly int[] _idx = new int[6];

        public void Solve(StructuralModel m, double[] loads, SolveOutput o)
        {
            o.EnsureSize(m.NodeCount, m.MemberCount);
            for (int k = 0; k < m.MemberCount; k++) m.Slack[k] = false;

            for (int iter = 0; iter < MaxCableIterations; iter++)
            {
                SolveLinear(m, loads, o);
                if (!o.Stable) return;
                bool changed = false;
                foreach(var rope in m.PulleyRopes)
                {
                    double elong=rope.Extension(m,o.Displacement);
                    bool slack=elong < -1e-9;
                    foreach(int k in rope.Members)if(m.Slack[k]!=slack){m.Slack[k]=slack;changed=true;}
                }
                for (int k = 0; k < m.MemberCount; k++)
                {
                    if (m.Broken[k] || !m.Mat[k].IsCable || m.PulleyMembers.Contains(k)) continue;
                    double elong = Elongation(m, o.Displacement, k);
                    if (!m.Slack[k] && elong < 0) { m.Slack[k] = true; changed = true; }
                    else if (m.Slack[k] && elong > 1e-9) { m.Slack[k] = false; changed = true; }
                }
                if (!changed) break;
            }
            ComputeForces(m, loads, o);
        }

        private static double Elongation(StructuralModel m, double[] u, int k)
        {
            int a = m.A[k], b = m.B[k];
            return (u[2 * b] - u[2 * a]) * m.Cos[k] + (u[2 * b + 1] - u[2 * a + 1]) * m.Sin[k] - m.Prestrain[k];
        }

        /// <summary>局部坐标系下的单元刚度矩阵（6×6，自由度 u1 v1 θ1 u2 v2 θ2）。铰接杆只保留轴向项。</summary>
        private void LocalStiffness(StructuralModel m, int k)
        {
            Array.Clear(_kl, 0, 36);
            double L = m.Length[k], ea = m.EA[k] / L;
            _kl[0 * 6 + 0] = ea; _kl[0 * 6 + 3] = -ea; _kl[3 * 6 + 0] = -ea; _kl[3 * 6 + 3] = ea;
            if (m.Pinned[k]) return;
            double ei = m.EI[k];
            double k1 = 12 * ei / (L * L * L), k2 = 6 * ei / (L * L), k3 = 4 * ei / L, k4 = 2 * ei / L;
            int[] r = { 1, 2, 4, 5 };
            double[,] b =
            {
                {  k1,  k2, -k1,  k2 },
                {  k2,  k3, -k2,  k4 },
                { -k1, -k2,  k1, -k2 },
                {  k2,  k4, -k2,  k3 },
            };
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) _kl[r[i] * 6 + r[j]] = b[i, j];
            // Moment release (hinge) at one end: statically condense that rotation out of the element.
            bool relA = m.ReleaseA != null && m.ReleaseA[k], relB = m.ReleaseB != null && m.ReleaseB[k];
            if (relA && relB) { for (int i = 0; i < 36; i++) if (i != 0 && i != 3 && i != 18 && i != 21) _kl[i] = 0; return; }
            if (relA) Condense(2);
            else if (relB) Condense(5);
        }

        private void Condense(int rd)
        {
            double krr = _kl[rd * 6 + rd];
            if (Math.Abs(krr) < 1e-30) return;
            for (int i = 0; i < 6; i++)
            {
                if (i == rd) continue;
                for (int j = 0; j < 6; j++)
                {
                    if (j == rd) continue;
                    _kl[i * 6 + j] -= _kl[i * 6 + rd] * _kl[rd * 6 + j] / krr;
                }
            }
            for (int i = 0; i < 6; i++) { _kl[i * 6 + rd] = 0; _kl[rd * 6 + i] = 0; }
        }

        /// <summary>k_global = Tᵀ k_local T。</summary>
        private void GlobalStiffness(StructuralModel m, int k)
        {
            LocalStiffness(m, k);
            double c = m.Cos[k], s = m.Sin[k];
            // T 为块对角 [[c s 0][-s c 0][0 0 1]]
            Span<double> t = stackalloc double[36];
            t.Clear();
            for (int blk = 0; blk < 2; blk++)
            {
                int o = blk * 3;
                t[(o + 0) * 6 + o + 0] = c; t[(o + 0) * 6 + o + 1] = s;
                t[(o + 1) * 6 + o + 0] = -s; t[(o + 1) * 6 + o + 1] = c;
                t[(o + 2) * 6 + o + 2] = 1;
            }
            Span<double> tmp = stackalloc double[36];
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++)
                {
                    double sum = 0;
                    for (int p = 0; p < 6; p++) sum += _kl[i * 6 + p] * t[p * 6 + j];
                    tmp[i * 6 + j] = sum;
                }
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++)
                {
                    double sum = 0;
                    for (int p = 0; p < 6; p++) sum += t[p * 6 + i] * tmp[p * 6 + j];
                    _kg[i * 6 + j] = sum;
                }
        }

        private void SolveLinear(StructuralModel m, double[] loads, SolveOutput o)
        {
            int n = m.NodeCount;
            if (_degree.Length < n) { _degree = new int[n]; _hasRigid = new bool[n]; }
            if (_dofMap.Length < 3 * n) _dofMap = new int[3 * n];
            Array.Clear(_degree, 0, n); Array.Clear(_hasRigid, 0, n);
            for (int k = 0; k < m.MemberCount; k++)
            {
                if (!m.IsActive(k)) continue;
                _degree[m.A[k]]++; _degree[m.B[k]]++;
                if (!m.Pinned[k])
                {
                    if (m.ReleaseA == null || !m.ReleaseA[k]) _hasRigid[m.A[k]] = true;
                    if (m.ReleaseB == null || !m.ReleaseB[k]) _hasRigid[m.B[k]] = true;
                }
            }
            // Locked joints: an aliased node shares the translations of its master node (pin joint).
            bool hasAlias = m.Alias != null;
            if (hasAlias)
                for (int i = 0; i < n; i++)
                {
                    int j = m.Alias[i];
                    if (j < 0) continue;
                    int sum = _degree[i] + _degree[j];
                    _degree[i] = sum; _degree[j] = sum;
                    // Rigid seam: both end points share one rotation
                    if (m.AliasRot != null && m.AliasRot[i]) { bool r = _hasRigid[i] || _hasRigid[j]; _hasRigid[i] = r; _hasRigid[j] = r; }
                }

            // 自由度编号：被约束 / 孤立的平动自由度、无刚接构件节点的转角自由度不参与求解
            int nf = 0;
            for (int i = 0; i < n; i++)
            {
                bool iso = _degree[i] == 0;
                bool alias = hasAlias && m.Alias[i] >= 0;
                _dofMap[3 * i] = (alias || m.FixX[i] || iso) ? -1 : nf++;
                _dofMap[3 * i + 1] = (alias || m.FixY[i] || iso) ? -1 : nf++;
                _dofMap[3 * i + 2] = (!_hasRigid[i] || iso || (alias && m.AliasRot != null && m.AliasRot[i]) || (m.FixRot != null && m.FixRot[i])) ? -1 : nf++;
            }
            if (hasAlias)
                for (int i = 0; i < n; i++)
                {
                    int j = m.Alias[i];
                    if (j < 0) continue;
                    _dofMap[3 * i] = _dofMap[3 * j];
                    _dofMap[3 * i + 1] = _dofMap[3 * j + 1];
                    if (m.AliasRot != null && m.AliasRot[i]) _dofMap[3 * i + 2] = _dofMap[3 * j + 2];
                }
            Array.Clear(o.Displacement, 0, o.Displacement.Length);
            Array.Clear(o.Rotation, 0, o.Rotation.Length);
            o.Stable = true; o.Message = ""; o.UnstableNodeId = -1;
            if (nf == 0) return;

            if (_k.Length < nf * nf) _k = new double[nf * nf];
            if (_f.Length < nf) _f = new double[nf];
            Array.Clear(_k, 0, nf * nf);
            Array.Clear(_f, 0, nf);
            var shift = m.SupportShiftX;
            if (shift != null && Array.TrueForAll(shift, v => v == 0)) shift = null;
            for (int i = 0; i < n; i++)
            {
                if (_dofMap[3 * i] >= 0) _f[_dofMap[3 * i]] += loads[2 * i];
                if (_dofMap[3 * i + 1] >= 0) _f[_dofMap[3 * i + 1]] += loads[2 * i + 1];
            }

            for (int k = 0; k < m.MemberCount; k++)
            {
                if (!m.IsActive(k)) continue;
                if(m.PulleyMembers.Contains(k))continue;
                GlobalStiffness(m, k);
                int a = m.A[k], b = m.B[k];
                for (int d = 0; d < 3; d++) { _idx[d] = _dofMap[3 * a + d]; _idx[3 + d] = _dofMap[3 * b + d]; }
                if (shift != null)
                {
                    // Prescribed support movement: move K·u of the fixed dof to the right-hand side
                    double ua = _idx[0] < 0 && a < shift.Length ? shift[a] : 0, ub = _idx[3] < 0 && b < shift.Length ? shift[b] : 0;
                    if (ua != 0 || ub != 0)
                        for (int p = 0; p < 6; p++) if (_idx[p] >= 0) _f[_idx[p]] -= _kg[p * 6 + 0] * ua + _kg[p * 6 + 3] * ub;
                }
                for (int p = 0; p < 6; p++)
                {
                    if (_idx[p] < 0) continue;
                    for (int q = 0; q < 6; q++)
                    {
                        if (_idx[q] < 0) continue;
                        _k[_idx[p] * nf + _idx[q]] += _kg[p * 6 + q];
                    }
                }
            }

            foreach(var rope in m.PulleyRopes)
            {
                if(!rope.Active(m))continue;
                var g=rope.Gradient(m);double stiffness=rope.Stiffness(m);
                foreach(var a in g)foreach(var b in g)
                {
                    int ia=_dofMap[3*(a.Key/2)+a.Key%2],ib=_dofMap[3*(b.Key/2)+b.Key%2];
                    if(ia>=0&&ib>=0)_k[ia*nf+ib]+=stiffness*a.Value*b.Value;
                }
            }
            // 平动刚度与转角刚度量级不同，分别取最大对角元作为主元容差基准
            double maxT = 0, maxR = 0;
            for (int i = 0; i < 3 * n; i++)
            {
                int d = _dofMap[i]; if (d < 0) continue;
                if (i % 3 == 2) maxR = Math.Max(maxR, _k[d * nf + d]); else maxT = Math.Max(maxT, _k[d * nf + d]);
            }
            var isRot = new bool[nf];
            for (int i = 0; i < 3 * n; i++) if (_dofMap[i] >= 0 && i % 3 == 2) isRot[_dofMap[i]] = true;

            for (int p = 0; p < nf; p++)
            {
                double piv = _k[p * nf + p];
                double tol = Math.Max((isRot[p] ? maxR : maxT) * PivotTolerance, 1e-6);
                if (piv <= tol)
                {
                    o.Stable = false;
                    o.UnstableNodeId = NodeOfDof(m, p);
                    o.Message = o.UnstableNodeId >= 0 ? Loc.F("节点 #{0} 约束不足，结构形成可动机构", (o.UnstableNodeId)) : Loc.T("结构约束不足");
                    return;
                }
                for (int r = p + 1; r < nf; r++)
                {
                    double f = _k[r * nf + p];
                    if (f == 0) continue;
                    f /= piv;
                    for (int c = p; c < nf; c++) _k[r * nf + c] -= f * _k[p * nf + c];
                    _f[r] -= f * _f[p];
                }
            }
            for (int p = nf - 1; p >= 0; p--)
            {
                double sum = _f[p];
                for (int c = p + 1; c < nf; c++) sum -= _k[p * nf + c] * _f[c];
                _f[p] = sum / _k[p * nf + p];
            }
            // Mechanism motion (bascule leaves) is applied to the geometry before solving, so only elastic
            // displacements appear here and the plain divergence limit applies.
            double limit = MaxDisplacement;
            for (int i = 0; i < n; i++)
            {
                for (int d = 0; d < 3; d++)
                {
                    int g = _dofMap[3 * i + d];
                    double v = g >= 0 ? _f[g] : (d == 0 && shift != null && i < shift.Length && m.FixX[i] ? shift[i] : 0);
                    if (double.IsNaN(v) || double.IsInfinity(v) || (d < 2 && Math.Abs(v) > limit))
                    {
                        o.Stable = false; o.UnstableNodeId = m.NodeIds[i];
                        o.Message = Loc.F("节点 #{0} 位移发散，结构失稳", (o.UnstableNodeId));
                        return;
                    }
                    if (d < 2) o.Displacement[2 * i + d] = v; else o.Rotation[i] = v;
                }
            }
        }

        private int NodeOfDof(StructuralModel m, int dof)
        {
            for (int i = 0; i < 3 * m.NodeCount; i++) if (_dofMap[i] == dof) return m.NodeIds[i / 3];
            return -1;
        }

        /// <summary>计算构件轴力、端弯矩与支座反力（R = Σ单元端力 − P）。</summary>
        private void ComputeForces(StructuralModel m, double[] loads, SolveOutput o)
        {
            if (!o.Stable) return;
            var u = o.Displacement; var th = o.Rotation;
            for (int i = 0; i < 2 * m.NodeCount; i++) o.Reaction[i] = -loads[i];
            for (int k = 0; k < m.MemberCount; k++)
            {
                o.MomentA[k] = 0; o.MomentB[k] = 0;
                if (!m.IsActive(k)) { o.AxialForce[k] = 0; continue; }
                int a = m.A[k], b = m.B[k];
                double c = m.Cos[k], s = m.Sin[k], L = m.Length[k];
                double u1 = c * u[2 * a] + s * u[2 * a + 1], v1 = -s * u[2 * a] + c * u[2 * a + 1];
                double u2 = c * u[2 * b] + s * u[2 * b + 1], v2 = -s * u[2 * b] + c * u[2 * b + 1];
                double n = m.EA[k] / L * (u2 - u1);
                if(m.PulleyMembers.Contains(k))
                    foreach(var rope in m.PulleyRopes)if(Array.IndexOf(rope.Members,k)>=0){n=rope.Stiffness(m)*rope.Extension(m,u);break;}
                double v = 0, m1 = 0, m2 = 0;
                if (!m.Pinned[k])
                {
                    // End forces from the (possibly condensed) local stiffness: released rotations carry no moment.
                    LocalStiffness(m, k);
                    double t1 = th[a], t2 = th[b];
                    double[] d = { u1, v1, t1, u2, v2, t2 };
                    double f1 = 0, f2 = 0, f5 = 0;
                    for (int j = 0; j < 6; j++) { f1 += _kl[1 * 6 + j] * d[j]; f2 += _kl[2 * 6 + j] * d[j]; f5 += _kl[5 * 6 + j] * d[j]; }
                    v = f1; m1 = f2; m2 = f5;
                }
                // 反力用 k·u 计算（loads 中已含强制伸缩等效力），构件轴力需扣除强制伸缩
                o.AxialForce[k] = n - m.EA[k] / L * m.Prestrain[k];
                o.MomentA[k] = m1; o.MomentB[k] = m2;
                // 单元在节点 a 的端力（局部）：(-N, V)；节点 b：(N, -V)，转换到整体坐标
                o.Reaction[2 * a] += c * (-n) - s * v; o.Reaction[2 * a + 1] += s * (-n) + c * v;
                o.Reaction[2 * b] += c * n - s * (-v); o.Reaction[2 * b + 1] += s * n + c * (-v);
            }
            for (int i = 0; i < m.NodeCount; i++)
                if (!m.IsAnchor[i]) { o.Reaction[2 * i] = 0; o.Reaction[2 * i + 1] = 0; }
        }
    }
}
