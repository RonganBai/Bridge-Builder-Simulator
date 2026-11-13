using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

namespace BridgeArchitect3D.Traffic
{
    /// <summary>
    /// 车辆行驶路径：左岸地面 → 桥面 → 右岸地面。
    /// 路径按几何生成：车轮下方有桥面且高度能衔接，车辆就继续前进，不要求桥面段首尾共用节点
    /// （交叠或贴合的桥面段都能通行；不超过 GapTolerance 的小缝隙车辆可以直接驶过）。
    /// 未建成或断裂的桥面不会出现在路径中，车辆不能沿空中直线穿越。
    /// </summary>
    public class RoadPath
    {
        public const double ApproachLength = 25;   // 两岸引道长度 m
        public const double GapTolerance = 0.5;    // 可直接驶过的水平缝隙 m
        public const double StepTolerance = 0.6;   // 可衔接的高度差 m
        /// <summary>坡度比较容差（避免网格节点“刚好 15%”因浮点误差被判超限）。</summary>
        public const double SlopeTolerance = 0.005;
        /// <summary>Steepest grade any vehicle can climb: 30° (tan 30° ≈ 57.7%). Above the suggested grade vehicles slow down.</summary>
        public const double MaxClimbGrade = 0.57735026918962573;

        // 路径点：位置 = 节点 A、B 位移按 T 插值（A=-1 表示地面固定点）
        private readonly List<int> _pa = new(), _pb = new();
        private readonly List<double> _pt = new();
        public readonly List<double> PointX = new(), PointY = new();
        /// <summary>每段对应的桥面构件下标（-1 表示地面或缝隙过渡）。</summary>
        public readonly List<int> SegmentMember = new();
        private readonly List<double> _segT0 = new(), _segT1 = new();
        /// <summary>每段起点的累计弧长。</summary>
        public readonly List<double> SegmentStartS = new();
        public double TotalLength;
        public int[] RoadNodeIndices = Array.Empty<int>();
        public List<int> DeckMemberIds = new();
        /// <summary>坡度超过建议值的桥面（车辆会减速爬坡）。</summary>
        public List<int> SteepMemberIds = new();

        /// <summary>某段的坡度（上坡为正）。</summary>
        public double SegmentSlope(int seg)
        {
            double dx = PointX[seg + 1] - PointX[seg];
            return Math.Abs(dx) < 1e-9 ? 0 : (PointY[seg + 1] - PointY[seg]) / dx;
        }

        /// <summary>路径是否连通到右岸出口。不连通时路径终点就是断口，车辆驶过即坠落。</summary>
        public bool Complete { get; private set; }
        /// <summary>道路层号。</summary>
        public int Lane { get; private set; }

        /// <summary>
        /// 由当前结构模型构建行驶路径。道路不完整时仍返回到断口为止的部分路径（Complete=false），
        /// 并通过 reason / error 给出问题；只有关卡缺少道路锚点时返回 null。
        /// </summary>
        public static RoadPath Build(StructuralModel m, LevelDefinition level, out FailureReason reason, out string error, out List<int> involved, int lane = 0)
        {
            reason = FailureReason.None; error = null; involved = new List<int>();
            int start = -1, end = -1;
            for (int i = 0; i < m.NodeCount; i++)
            {
                if (!m.IsAnchor[i]) continue;
                var def = level.Anchors.FirstOrDefault(a => a.Id == m.AnchorIds[i]);
                if (def == null || def.RoadLane != lane) continue;
                if (def.IsRoadStart) start = i;
                if (def.IsRoadEnd) end = i;
            }
            if (start < 0 || end < 0) { reason = FailureReason.RoadNotConnected; error = Loc.T("关卡缺少道路锚点"); return null; }
            string laneName = lane == 0 ? "" : Loc.T("上层");

            var p = new RoadPath { Lane = lane };
            var roadNodes = new HashSet<int> { start };
            // Main road: all vehicles start at the level's fixed ground start (shared with the tunnel route)
            p.AddPoint(-1, -1, 0, lane == 0 ? Math.Min(m.X[start] - ApproachLength, TunnelRules.GroundStartX(level)) : m.X[start] - ApproachLength, m.Y[start]);
            p.AddPoint(start, start, 0, m.X[start], m.Y[start]);
            p.AddSegment(-1, 0, 0);

            double cx = m.X[start], cy = m.Y[start];
            bool complete = false;
            for (int guard = 0; guard < m.MemberCount + 2; guard++)
            {
                if (Math.Abs(cx - m.X[end]) <= GapTolerance && Math.Abs(cy - m.Y[end]) <= StepTolerance) { complete = true; break; }

                // Follow explicit segment endpoints before a longer overlapping member can skip their joint.
                int best = -1; double bestHi = cx + 0.05;bool bestValid=false,bestStartsHere=false;
                for (int k = 0; k < m.MemberCount; k++)
                {
                    if (m.Broken[k] || !m.Mat[k].IsDeck) continue;
                    int lo = m.X[m.A[k]] <= m.X[m.B[k]] ? m.A[k] : m.B[k];
                    int hi = lo == m.A[k] ? m.B[k] : m.A[k];
                    double xl = m.X[lo], xh = m.X[hi];
                    if (xh - xl < 0.05 || xl > cx + GapTolerance || xh <= cx+0.05) continue;
                    double xe = Math.Clamp(cx, xl, xh);
                    double ye = m.Y[lo] + (m.Y[hi] - m.Y[lo]) * (xe - xl) / (xh - xl);
                    if (Math.Abs(ye - cy) > StepTolerance) continue;
                    double grade=Math.Abs((m.Y[hi]-m.Y[lo])/(xh-xl));
                    bool valid=m.Length[k]<=m.Mat[k].MaximumLength+1e-6 && grade<=MaxClimbGrade+SlopeTolerance;
                    bool startsHere=Math.Abs(xl-cx)<1e-6;
                    if(best>=0)
                    {
                        if(bestValid && !valid)continue;
                        if(bestValid==valid)
                        {
                            if(bestStartsHere && !startsHere)continue;
                            if(bestStartsHere==startsHere && (startsHere?xh>=bestHi:xh<=bestHi))continue;
                        }
                    }
                    best = k; bestHi = xh;bestValid=valid;bestStartsHere=startsHere;
                }
                if (best < 0) break;

                int k2 = best;
                int l2 = m.X[m.A[k2]] <= m.X[m.B[k2]] ? m.A[k2] : m.B[k2];
                int h2 = l2 == m.A[k2] ? m.B[k2] : m.A[k2];
                double dx = m.X[h2] - m.X[l2], dy = m.Y[h2] - m.Y[l2];
                double slope = Math.Abs(dy / dx), limit = level.Objectives.MaxDeckSlope;
                if (slope > MaxClimbGrade + SlopeTolerance)
                {
                    reason = FailureReason.VehicleBlocked; involved.Add(m.MemberIds[k2]);
                    error = Loc.F("桥面 #{0} 坡度 {1:0}% 太陡，车辆爬不上去（最多 30°≈{2:0}%，建议不超过 {3:0}%）", (m.MemberIds[k2]), (slope * 100), (MaxClimbGrade * 100), (limit * 100));
                    break;
                }
                if (slope > limit + SlopeTolerance) p.SteepMemberIds.Add(m.MemberIds[k2]);
                if (m.Length[k2] > m.Mat[k2].MaximumLength + 1e-6)
                {
                    reason = FailureReason.DeckSpanTooLong; involved.Add(m.MemberIds[k2]);
                    error = Loc.F("桥面 #{0} 跨度 {1:0.0} m 超过最大 {2:0.0} m", (m.MemberIds[k2]), (m.Length[k2]), (m.Mat[k2].MaximumLength));
                    break;
                }

                // 驶入点（沿构件 A→B 的参数）
                double xEnter = Math.Max(cx, m.X[l2]);
                double sLow = (xEnter - m.X[l2]) / dx;               // 自低端起的比例
                double tEnter = l2 == m.A[k2] ? sLow : 1 - sLow;
                double tExit = h2 == m.B[k2] ? 1 : 0;
                double yEnter = m.Y[l2] + dy * sLow;
                if (Math.Abs(xEnter - cx) > 1e-4 || Math.Abs(yEnter - cy) > 1e-4)
                {
                    // 缝隙或台阶过渡（不传递荷载）
                    p.AddPoint(m.A[k2], m.B[k2], tEnter, xEnter, yEnter);
                    p.AddSegment(-1, 0, 0);
                }
                p.AddPoint(h2, h2, 0, m.X[h2], m.Y[h2]);
                p.AddSegment(k2, tEnter, tExit);
                p.DeckMemberIds.Add(m.MemberIds[k2]);
                roadNodes.Add(l2); roadNodes.Add(h2);
                cx = m.X[h2]; cy = m.Y[h2];
            }

            if (complete)
            {
                if (Math.Abs(cx - m.X[end]) > 1e-4 || Math.Abs(cy - m.Y[end]) > 1e-4)
                {
                    p.AddPoint(end, end, 0, m.X[end], m.Y[end]);
                    p.AddSegment(-1, 0, 0);
                }
                p.AddPoint(-1, -1, 0, lane == 0 ? Math.Max(m.X[end] + ApproachLength, TunnelRules.GroundEndX(level)) : m.X[end] + ApproachLength, m.Y[end]);
                p.AddSegment(-1, 0, 0);
            }
            else if (reason == FailureReason.None)
            {
                reason = FailureReason.RoadNotConnected;
                error = p.DeckMemberIds.Count == 0 ? Loc.F("左岸{0}道路入口没有连接桥面", (laneName)) : Loc.F("{0}道路在 X = {1:0.0} m 处中断，未能到达右岸", (laneName), (cx));
            }
            p.Complete = complete;

            double s = 0;
            for (int i = 0; i < p.SegmentMember.Count; i++)
            {
                p.SegmentStartS.Add(s);
                s += Math.Sqrt(Sq(p.PointX[i + 1] - p.PointX[i]) + Sq(p.PointY[i + 1] - p.PointY[i]));
            }
            p.TotalLength = s;
            p.RoadNodeIndices = roadNodes.ToArray();
            return p;
        }

        /// <summary>A fixed ground route (underwater tunnel): no deck members, always complete.</summary>
        /// <summary>Lift segments of a ground route (they run backwards in x: the vehicle rides a lift, it does not drive them).</summary>
        public readonly HashSet<int> LiftSegments = new();

        public static RoadPath FromGround(IReadOnlyList<(double x, double y)> pts, int lane)
        {
            var p = new RoadPath { Lane = lane };
            for (int i = 0; i < pts.Count; i++)
            {
                p.AddPoint(-1, -1, 0, pts[i].x, pts[i].y);
                if (i > 0) { p.AddSegment(-1, 0, 0); if (pts[i].x < pts[i - 1].x - 1e-6) p.LiftSegments.Add(i - 1); }
            }
            p.Complete = true;
            double s = 0;
            for (int i = 0; i < p.SegmentMember.Count; i++)
            {
                p.SegmentStartS.Add(s);
                s += Math.Sqrt(Sq(p.PointX[i + 1] - p.PointX[i]) + Sq(p.PointY[i + 1] - p.PointY[i]));
            }
            p.TotalLength = s;
            return p;
        }

        private void AddPoint(int a, int b, double t, double x, double y) { _pa.Add(a); _pb.Add(b); _pt.Add(t); PointX.Add(x); PointY.Add(y); }
        private void AddSegment(int member, double t0, double t1) { SegmentMember.Add(member); _segT0.Add(t0); _segT1.Add(t1); }
        private static double Sq(double v) => v * v;

        /// <summary>根据弧长查找所在段与段内参数 t。</summary>
        public int Locate(double s, out double t)
        {
            s = Math.Clamp(s, 0, TotalLength);
            int seg = SegmentMember.Count - 1;
            for (int i = 0; i < SegmentMember.Count; i++)
            {
                double end = i + 1 < SegmentStartS.Count ? SegmentStartS[i + 1] : TotalLength;
                if (s <= end) { seg = i; break; }
            }
            double s0 = SegmentStartS[seg];
            double s1 = seg + 1 < SegmentStartS.Count ? SegmentStartS[seg + 1] : TotalLength;
            t = s1 - s0 > 1e-9 ? (s - s0) / (s1 - s0) : 0;
            return seg;
        }

        /// <summary>段内参数换算为所在桥面构件的 A→B 参数（用于车轴荷载分配）。</summary>
        public double MemberT(int seg, double t) => _segT0[seg] + (_segT1[seg] - _segT0[seg]) * t;

        /// <summary>
        /// 求路径上某弧长处的位置（考虑节点位移）。displacementScale 为视觉放大系数，判定逻辑只使用真实位移（scale=1）。
        /// </summary>
        public void Evaluate(double s, double[] displacement, double displacementScale, out double x, out double y)
        {
            // Long trains extend beyond the stored approach; never pile their cars onto its first point.
            if (s < 0) { x = PointX[0] + s; y = PointY[0]; return; }
            if (Complete && s > TotalLength) { x = PointX[^1] + s - TotalLength; y = PointY[^1]; return; }
            int seg = Locate(s, out double t);
            PointAt(seg, displacement, displacementScale, out double x0, out double y0);
            PointAt(seg + 1, displacement, displacementScale, out double x1, out double y1);
            x = x0 + (x1 - x0) * t; y = y0 + (y1 - y0) * t;
        }

        private void PointAt(int i, double[] u, double scale, out double x, out double y)
        {
            x = PointX[i]; y = PointY[i];
            int a = _pa[i], b = _pb[i];
            if (a < 0 || u == null || u.Length <= 2 * Math.Max(a, b) + 1) return;
            double t = _pt[i];
            x += (u[2 * a] * (1 - t) + u[2 * b] * t) * scale;
            y += (u[2 * a + 1] * (1 - t) + u[2 * b + 1] * t) * scale;
        }

        /// <summary>桥面起点 / 终点处的弧长。</summary>
        public double DeckStartS => SegmentStartS.Count > 1 ? SegmentStartS[1] : 0;
        public double DeckEndS => Complete && SegmentStartS.Count > 0 ? SegmentStartS[^1] : TotalLength;
    }
}
