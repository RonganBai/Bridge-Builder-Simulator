using System;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    public enum MemberMode { Inactive, Tension, Compression, Slack }

    /// <summary>每根构件的评估结果。</summary>
    public struct MemberStatus
    {
        public double Force;          // 轴力 N（拉为正）
        public double Utilization;    // 承载利用率（轴力、桥面弯矩取大值）
        public MemberMode Mode;
        public FailureReason Governing; // 控制失效模式
        public double Moment;         // 构件最大弯矩 N·m
        public bool Cracked;          // 钢筋混凝土类构件已开裂（利用率超过开裂阈值）
    }

    /// <summary>
    /// 失效检测：计算构件利用率（拉 / 压 / 屈曲 / 桥面抗弯），检测锚点过载与桥面下沉。
    /// </summary>
    public static class FailureDetector
    {
        public static void EvaluateMembers(StructuralModel m, SolveOutput o, AxleLoad[] axles, int axleCount, MemberStatus[] status)
        {
            for (int k = 0; k < m.MemberCount; k++)
            {
                ref var s = ref status[k];
                s.Moment = 0; s.Governing = FailureReason.None; s.Cracked = false;
                if (m.Broken[k]) { s.Force = 0; s.Utilization = 0; s.Mode = MemberMode.Inactive; continue; }
                if (m.Slack[k]) { s.Force = 0; s.Utilization = 0; s.Mode = MemberMode.Slack; continue; }
                var mat = m.Mat[k];
                double f = o.AxialForce[k];
                s.Force = f;
                if (f >= 0)
                {
                    s.Mode = MemberMode.Tension;
                    double cap = mat.TensionCapacity * CrossBeams.TensionFactor(mat, m.Length[k]);
                    s.Utilization = cap > 0 ? f / cap : 99;
                    s.Governing = FailureReason.TensionFailure;
                }
                else
                {
                    s.Mode = MemberMode.Compression;
                    double cap = mat.CompressionCapacity(m.Length[k]);
                    s.Utilization = cap > 0 ? -f / cap : 99;
                    s.Governing = FailureReason.CompressionFailure;
                }
                if ((!m.Pinned[k] || mat.IsDeck) && mat.BendingCapacity > 0)
                {
                    // 刚接构件：轴力与弯矩线性叠加的相互作用校核 N/Nu + M/Mu ≤ 1
                    s.Moment = LoadCalculator.MemberMaxMoment(m, k, axles, axleCount, o.MomentA[k], o.MomentB[k]);
                    double ub = s.Moment / mat.BendingCapacity;
                    if (ub > s.Utilization) s.Governing = FailureReason.DeckBendingFailure;
                    s.Utilization += ub;
                }
                // 老化的旧构件：承载力按状况折减（利用率相应放大）
                if (m.Condition != null && m.Condition[k] < 1) s.Utilization /= m.Condition[k];
                s.Cracked = mat.CrackRatio > 0 && s.Utilization >= mat.CrackRatio;
            }
            foreach(var bearing in m.PulleyBearings)
            {
                double x=0,y=0;
                for(int k=0;k<m.MemberCount;k++)
                {
                    if(m.Broken[k]||m.Slack[k]||!m.Mat[k].IsCable)continue;
                    int sign=m.A[k]==bearing.Key?1:m.B[k]==bearing.Key?-1:0;
                    double tension=Math.Max(0,o.AxialForce[k]);x+=sign*tension*m.Cos[k];y+=sign*tension*m.Sin[k];
                }
                double u=Math.Sqrt(x*x+y*y)/bearing.Value;
                for(int k=0;k<m.MemberCount;k++)
                    if(!m.Broken[k]&&m.Mat[k].IsCable&&(m.A[k]==bearing.Key||m.B[k]==bearing.Key)&&u>status[k].Utilization)
                    {status[k].Utilization=u;status[k].Governing=FailureReason.AnchorOverload;}
            }
        }

        /// <summary>返回利用率最高的构件下标及其利用率。</summary>
        public static int WorstMember(StructuralModel m, MemberStatus[] status, out double util)
        {
            int worst = -1; util = 0;
            for (int k = 0; k < m.MemberCount; k++)
                if (!m.Broken[k] && status[k].Utilization > util) { util = status[k].Utilization; worst = k; }
            return worst;
        }

        /// <summary>检查锚点支反力，返回过载锚点的节点下标（-1 表示正常）。</summary>
        public static int OverloadedAnchor(StructuralModel m, SolveOutput o, out double reaction, out double ratio)
        {
            reaction = 0; ratio = 0; int worst = -1;
            for (int i = 0; i < m.NodeCount; i++)
            {
                if (!m.IsAnchor[i] || m.AnchorMaxLoad[i] <= 0) continue;
                double r = Math.Sqrt(o.Reaction[2 * i] * o.Reaction[2 * i] + o.Reaction[2 * i + 1] * o.Reaction[2 * i + 1]);
                double q = r / m.AnchorMaxLoad[i];
                if (q > ratio) { ratio = q; reaction = r; worst = i; }
            }
            return ratio > 1 ? worst : -1;
        }

        /// <summary>道路路径节点的最大下沉量（m，向下为正）。</summary>
        public static double MaxDeckSag(SolveOutput o, int[] roadNodeIndices, out int nodeIndex)
        {
            double best = 0; nodeIndex = -1;
            foreach (int i in roadNodeIndices)
            {
                double sag = -o.Displacement[2 * i + 1];
                if (sag > best) { best = sag; nodeIndex = i; }
            }
            return best;
        }

        public static string ReasonText(FailureReason r) => r switch
        {
            FailureReason.RoadNotConnected => Loc.T("道路没有连接两岸"),
            FailureReason.TensionFailure => Loc.T("构件超过抗拉强度（拉伸破坏）"),
            FailureReason.CompressionFailure => Loc.T("构件超过抗压强度或发生屈曲（压缩破坏）"),
            FailureReason.DeckBendingFailure => Loc.T("构件抗弯不足（弯曲破坏）"),
            FailureReason.AnchorOverload => Loc.T("固定锚点过载"),
            FailureReason.ShipCollision => Loc.T("船只撞上桥梁（净空不足）"),
            FailureReason.FloodScour => Loc.T("洪水冲毁桥墩或掏空基础"),
            FailureReason.DeckSpanTooLong => Loc.T("桥面跨度过大"),
            FailureReason.DeckDeflectionExceeded => Loc.T("桥面下沉超过限制"),
            FailureReason.StructureUnstable => Loc.T("结构约束不足 / 失稳"),
            FailureReason.VehicleBlocked => Loc.T("车辆无法继续行驶"),
            FailureReason.ProgressiveCollapse => Loc.T("结构发生连续失效"),
            FailureReason.OverBudget => Loc.T("建造成本超出预算"),
            FailureReason.Derailed => Loc.T("列车脱轨（铁轨上下晃动太大）"),
            FailureReason.WrongSurface => Loc.T("车辆驶上了不能通行的路面"),
            FailureReason.TunnelFlooded => Loc.T("隧道管节强度不够被压坏"),
            FailureReason.LiftOverload => Loc.T("电梯平台超载"),
            _ => Loc.T("无"),
        };

        public static string[] SuggestionsFor(FailureReason r) => r switch
        {
            FailureReason.RoadNotConnected => new[] { Loc.T("使用“道路桥面”工具从左岸道路锚点连续铺设到右岸道路锚点"), Loc.T("桥面段必须首尾相接且自左向右前进") },
            FailureReason.TensionFailure => new[] { Loc.T("把受拉构件换成钢梁或钢索"), Loc.T("增加桁架高度以降低弦杆拉力"), Loc.T("增加斜杆分担载荷") },
            FailureReason.CompressionFailure => new[] { Loc.T("缩短受压构件（长杆容易屈曲）"), Loc.T("受压构件改用钢梁"), Loc.T("增加节点把长压杆分段") },
            FailureReason.DeckBendingFailure => new[] { Loc.T("构件以受弯为主时承载力很低：用斜杆组成三角形，让构件主要承受轴力"), Loc.T("在桥面下方用支柱或上方用钢索/吊杆增加支点，缩短受弯跨度"), Loc.T("重型货车需要更密的桥面支点") },
            FailureReason.FloodScour => new[] { Loc.T("冲刷区里的桥墩改用桩基础，或把桥墩移到岩石区"), Loc.T("洪水关卡不要用木桥墩"), Loc.T("也可以加大跨径，避免在冲刷区建墩") },
            FailureReason.ShipCollision => new[] { Loc.T("航道上方（虚线标出的区域）不要放置低于船高的构件"), Loc.T("可以用液压杆做可开启的结构：船只到来时液压杆会自动收缩，船过后恢复") },
            FailureReason.AnchorOverload => new[] { Loc.T("利用更多锚点分散支反力"), Loc.T("减轻结构自重") },
            FailureReason.DeckSpanTooLong => new[] { Loc.T("单段桥面不能超过材料最大长度") },
            FailureReason.DeckDeflectionExceeded => new[] { Loc.T("提高结构刚度：加高桁架、改用钢材"), Loc.T("增加支撑以减小跨度") },
            FailureReason.StructureUnstable => new[] { Loc.T("每个节点至少需要两根不共线的构件支撑"), Loc.T("用三角形单元组成桁架，避免四边形机构"), Loc.T("桥面节点需要桁架或钢索支撑，不能只靠桥面串联") },
            FailureReason.VehicleBlocked => new[] { Loc.T("保证车辆经过的桥面完整且坡度不过大") },
            FailureReason.Derailed => new[] { Loc.T("铁路桥要很刚：加高桁架、缩短桥面支点间距，减小列车经过时的下沉和起伏"), Loc.T("用更刚的桥面（钢铁轨桥面）或在桥面下加支柱 / 桥墩") },
            FailureReason.WrongSurface => new[] { Loc.T("火车只能走铁轨桥面或铁轨公路复合桥面"), Loc.T("汽车只能走公路桥面或铁轨公路复合桥面；两种都要过时用复合桥面") },
            FailureReason.TunnelFlooded => new[] { Loc.T("选中管节升级为加强 / 重型，管片强度更高"), Loc.T("悬浮隧道：增加锚索或换高强复合锚索"), Loc.T("隧道越深水压 / 土压越大：在强度够的前提下抬浅一些") },
            FailureReason.ProgressiveCollapse => new[] { Loc.T("增加冗余构件，避免单根构件破坏引起连锁失效") },
            FailureReason.OverBudget => new[] { Loc.T("删除不必要的构件，或换用便宜材料") },
            _ => Array.Empty<string>(),
        };
    }
}
