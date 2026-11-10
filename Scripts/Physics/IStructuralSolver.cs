namespace BridgeArchitect3D.Physics
{
    /// <summary>求解输出（由调用方预分配并复用，减少 GC）。</summary>
    public class SolveOutput
    {
        public bool Stable;
        public string Message = "";
        /// <summary>导致失稳的节点 Id（-1 表示未知）。</summary>
        public int UnstableNodeId = -1;
        /// <summary>节点位移 [2i]=ux, [2i+1]=uy（m，真实计算值，未放大）。</summary>
        public double[] Displacement = System.Array.Empty<double>();
        /// <summary>构件轴力（N，拉为正，压为负）。</summary>
        public double[] AxialForce = System.Array.Empty<double>();
        /// <summary>支座反力 [2i], [2i+1]（N）。</summary>
        public double[] Reaction = System.Array.Empty<double>();

        /// <summary>节点转角（rad）。</summary>
        public double[] Rotation = System.Array.Empty<double>();
        /// <summary>构件两端弯矩（N·m，局部坐标，逆时针为正）。铰接杆为 0。</summary>
        public double[] MomentA = System.Array.Empty<double>();
        public double[] MomentB = System.Array.Empty<double>();

        public void EnsureSize(int nodes, int members)
        {
            if (Displacement.Length != nodes * 2) { Displacement = new double[nodes * 2]; Reaction = new double[nodes * 2]; Rotation = new double[nodes]; }
            if (AxialForce.Length != members) { AxialForce = new double[members]; MomentA = new double[members]; MomentB = new double[members]; }
        }
    }

    /// <summary>
    /// 结构求解器接口。当前实现为二维铰接桁架刚度矩阵法（TrussSolver2D）；
    /// 将来可以增加三维梁单元求解器而无需修改建造系统与通车测试流程。
    /// </summary>
    public interface IStructuralSolver
    {
        /// <param name="model">结构模型（断裂、松弛状态会被求解器更新：钢索松弛）。</param>
        /// <param name="loads">节点外荷载 [2i]=Fx, [2i+1]=Fy（N）。</param>
        /// <param name="output">输出缓存。</param>
        void Solve(StructuralModel model, double[] loads, SolveOutput output);
    }
}
