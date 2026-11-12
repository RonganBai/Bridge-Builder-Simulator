namespace BridgeArchitect3D.Physics
{
    /// <summary>
    /// Extension point for movable-bridge mechanisms (bascule leaves, lift spans, ...).
    /// A mechanism owns its own state and modifies the structural model (geometry, joint locks, releases)
    /// before the structure is solved. BasculeSystem is the first implementation.
    /// </summary>
    public interface IBridgeMechanism
    {
        /// <summary>Mechanism id.</summary>
        string Id { get; }

        /// <summary>Called before each structural solve.</summary>
        void PreSolve(StructuralModel model, double time, double dt);
    }
}
