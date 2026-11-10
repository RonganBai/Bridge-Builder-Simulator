using BridgeArchitect3D.Physics;

namespace BridgeArchitect3D.Building
{
    /// <summary>
    /// BridgeMechanismSystem 扩展接口（初版未实现具体机构）。
    /// 后续的铰链、液压杆、电机、活动桥面可实现此接口：在每个模拟步前修改结构模型
    /// （例如改变构件长度 / 节点约束），由 BridgeSimulation 统一调用，无需改动建造系统。
    /// </summary>
    public interface IBridgeMechanism
    {
        /// <summary>机构唯一 Id（对应存档中的机构数据）。</summary>
        string Id { get; }

        /// <summary>在结构求解前更新模型状态。</summary>
        void PreSolve(StructuralModel model, double time, double dt);
    }
}
