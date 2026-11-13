using System;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// 立柱规则（建造校验与结构模型共用）：
    /// - 混凝土柱（BaseRule.Land）：较低一端必须落在陆地上，该端成为固定基础（约束 X、Y 与转角，接近刚性节点）；
    /// - 钢筋混凝土柱（BaseRule.Anchor）：必须有一端为地图锚点，该锚点变为刚接基础（额外约束转角）；
    /// - 倾角超过 MaxTiltDegrees 的立柱在测试开始时倒塌。
    /// </summary>
    public static class ColumnRules
    {
        /// <summary>构件轴线偏离竖直方向的角度（度）。</summary>
        public static double TiltDegrees(Vec3d a, Vec3d b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return 0;
            return Math.Acos(Math.Min(1, Math.Abs(dy) / len)) * 180 / Math.PI;
        }

        /// <summary>
        /// 求立柱的基础端：返回 true 表示 a 端为基础，false 表示 b 端；不满足基础要求时 reason 非空。
        /// </summary>
        public static bool FindBase(MaterialDefinition mat, BridgeNode a, BridgeNode b, LevelDefinition level, out string reason)
        {
            reason = null;
            if (mat.BaseRule == BaseRule.Anchor)
            {
                bool aa = a.IsAnchor || a.IsTunnelSupport, ba = b.IsAnchor || b.IsTunnelSupport;
                if (aa && (!ba || a.Position.Y <= b.Position.Y)) return true;
                if (ba) return false;
                reason = Loc.F("{0}只能建在地图锚点上", (mat.Name));
                return true;
            }
            bool aLow = a.Position.Y <= b.Position.Y;
            var low = aLow ? a : b;
            if (mat.BaseRule == BaseRule.Pier && !(low.PierBase || low.PierPart))
                reason = Loc.F("{0}只能用桥墩工具从河床立起", (mat.Name));
            if (mat.BaseRule == BaseRule.Land && !low.IsTunnelSupport && !level.IsOnLand(low.Position.X, low.Position.Y))
                reason = Loc.F("{0}的底端必须落在陆地上（两岸或岩墩）", (mat.Name));
            return aLow;
        }

        public static bool TiltExceeded(MaterialDefinition mat, Vec3d a, Vec3d b) =>
            mat.MaxTiltDegrees > 0 && TiltDegrees(a, b) > mat.MaxTiltDegrees + 1e-6;
    }
}
