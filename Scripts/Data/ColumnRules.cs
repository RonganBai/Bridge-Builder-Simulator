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
                if (a.IsAnchor && (!b.IsAnchor || a.Position.Y <= b.Position.Y)) return true;
                if (b.IsAnchor) return false;
                reason = $"{mat.Name}只能建在地图锚点上";
                return true;
            }
            bool aLow = a.Position.Y <= b.Position.Y;
            var low = aLow ? a : b;
            if (mat.BaseRule == BaseRule.Pier && !(low.PierBase || low.PierPart))
                reason = $"{mat.Name}只能用桥墩工具从河床立起";
            if (mat.BaseRule == BaseRule.Land && !level.IsOnLand(low.Position.X, low.Position.Y))
                reason = $"{mat.Name}的底端必须落在陆地上（两岸或岩墩）";
            return aLow;
        }

        public static bool TiltExceeded(MaterialDefinition mat, Vec3d a, Vec3d b) =>
            mat.MaxTiltDegrees > 0 && TiltDegrees(a, b) > mat.MaxTiltDegrees + 1e-6;
    }
}
