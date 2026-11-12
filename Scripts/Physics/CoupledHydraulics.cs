using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    /// <summary>Explicit drivers share one solve; unselected cylinders remain fixed-length followers.</summary>
    public static class CoupledHydraulics
    {
        public static LinkageData Find(BridgeDesign design, GameDatabase db, LevelDefinition level) =>
            design.Mechanisms?.Linkage?.Drivers.Count > 0 ? design.Mechanisms.Linkage.Clone() : null;

        /// <summary>Compatibility entry point; frame discovery does not require a bascule plan.</summary>
        public static void PreserveParentBodies(StructuralModel model, BridgeDesign design,
            GameDatabase db, LevelDefinition level, LinkageData settings) => RigidFrameDiscovery.Discover(model);
    }
}
