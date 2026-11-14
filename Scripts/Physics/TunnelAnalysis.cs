using System;
using System.Collections.Generic;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    /// <summary>Environment seen by the tunnel at one moment.</summary>
    public struct TunnelEnvironment
    {
        /// <summary>Current water surface (normal level + flood rise), m.</summary>
        public double WaterY;
        /// <summary>A flood is scouring the bed (scour zones lose bearing under unprotected elements).</summary>
        public bool Scouring;
        /// <summary>River current, m/s.</summary>
        public double Current;
        /// <summary>Shield: groundwater rise around each element above the normal level, m (null = none).
        /// The river and the groundwater are one water body here: the groundwater level replaces the river level
        /// for a bored tunnel, so the two pressures are never added.</summary>
        public double[] GroundwaterRise;
        /// <summary>Ground softening from the weather (0..1): bearing / anchor capacity × (1 − s), required cover and earth pressure × (1 + s).</summary>
        public double Softening;
    }

    /// <summary>Per-element result of a tunnel check. Utilization ≥ 1 is a failure.</summary>
    public class TunnelState
    {
        public int Count;
        public double[] Util = Array.Empty<double>();
        /// <summary>Water pressure on the shell (Pa) and its utilization.</summary>
        public double[] Pressure = Array.Empty<double>(), PressureUtil = Array.Empty<double>();
        /// <summary>Bearing pressure on the soil (Pa), allowable bearing (Pa) and utilization.</summary>
        public double[] Bearing = Array.Empty<double>(), BearingCap = Array.Empty<double>(), BearingUtil = Array.Empty<double>();
        /// <summary>Settlement of each element, m.</summary>
        public double[] Settle = Array.Empty<double>();
        /// <summary>Tube as a beam on the player's supports: joint moment (N·m), its utilization, joint rotation (rad).</summary>
        public double[] Moment = Array.Empty<double>(), BendUtil = Array.Empty<double>(), JointRotationNow = Array.Empty<double>();
        /// <summary>Load path of each element: what holds its two joints (player members and their forces).</summary>
        public string[] LoadPath = Array.Empty<string>();
        /// <summary>Joint j between element j and j+1: rotation utilization.</summary>
        public double[] JointUtil = Array.Empty<double>();
        // Floating tunnel: net buoyancy (N, up), vehicle share of it, cable tension (N per cable), anchor pull,
        // sideways sway and vertical heave (m), and their utilizations
        public double[] NetBuoyancy = Array.Empty<double>(), SlackUtil = Array.Empty<double>();
        public double[] Tension = Array.Empty<double>(), CableUtil = Array.Empty<double>();
        public double[] AnchorPull = Array.Empty<double>(), AnchorCap = Array.Empty<double>(), AnchorUtil = Array.Empty<double>();
        public double[] Sway = Array.Empty<double>(), SwayUtil = Array.Empty<double>(), Drag = Array.Empty<double>();
        /// <summary>Joint rotation utilization where the tube meets the left / right bank (floating).</summary>
        public double EndJointLeft, EndJointRight;
        // Shield tunnel: effective earth pressure (Pa), cover above the crown and the soil's minimum (m), stability
        // utilization, groundwater rise (m) and the crown / axis soil ids
        public double[] EarthPressure = Array.Empty<double>(), Cover = Array.Empty<double>(), CoverReq = Array.Empty<double>();
        public double[] StabilityUtil = Array.Empty<double>(), Groundwater = Array.Empty<double>();
        public string[] CrownSoil = Array.Empty<string>();
        public double MaxUtil;
        public int Worst = -1;
        /// <summary>Failure text for the worst element (only meaningful when MaxUtil ≥ 1).</summary>
        public string WorstText;

        public void Resize(int n)
        {
            if (Count == n && Util.Length == n) return;
            Count = n;
            Util = new double[n]; Pressure = new double[n]; PressureUtil = new double[n];
            Bearing = new double[n]; BearingCap = new double[n]; BearingUtil = new double[n]; Settle = new double[n];
            Moment = new double[n]; BendUtil = new double[n]; JointRotationNow = new double[n]; LoadPath = new string[n];
            JointUtil = new double[Math.Max(0, n - 1)];
            NetBuoyancy = new double[n]; SlackUtil = new double[n]; Tension = new double[n]; CableUtil = new double[n];
            AnchorPull = new double[n]; AnchorCap = new double[n]; AnchorUtil = new double[n];
            Sway = new double[n]; SwayUtil = new double[n]; Drag = new double[n];
            EarthPressure = new double[n]; Cover = new double[n]; CoverReq = new double[n]; StabilityUtil = new double[n];
            Groundwater = new double[n]; CrownSoil = new string[n];
        }
    }

    /// <summary>
    /// Simplified tunnel mechanics. Each tunnel type has its own load model (water pressure, soil bearing,
    /// joints …); the loads are never merged into one number before they are compared with their own capacity.
    /// </summary>
    public static class TunnelAnalysis
    {
        /// <summary>Static case shown while building: normal water level and current, no flood, no vehicles.</summary>
        public static TunnelEnvironment StaticEnvironment(LevelDefinition level, TunnelConfig cfg) =>
            new() { WaterY = level.Terrain.WaterLevel, Current = TunnelRules.Weather.Normal.Current, Softening = TunnelRules.Weather.Normal.SoilSoftening };

        /// <summary>Check the tube. loads = vehicle axle forces (x, N) inside the tube.</summary>
        public static TunnelState Evaluate(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, TunnelEnvironment env,
            IReadOnlyList<(double x, double force)> loads = null, TunnelState state = null)
        {
            state ??= new TunnelState();
            if (g == null) { state.Resize(0); state.MaxUtil = 0; state.Worst = -1; return state; }
            state.Resize(g.Count);
            switch (g.Material.TunnelType)
            {
                case "floating": Floating(g, level, cfg, env, loads, state); break;
                case "shield": Shield(g, level, cfg, env, state); break;
                default: Immersed(g, level, cfg, env, loads, state); break;
            }
            state.MaxUtil = 0; state.Worst = -1;
            for (int i = 0; i < g.Count; i++) if (state.Util[i] > state.MaxUtil) { state.MaxUtil = state.Util[i]; state.Worst = i; }
            state.WorstText = state.Worst >= 0 ? Describe(g, cfg, level, state, state.Worst) : null;
            return state;
        }

        /// <summary>
        /// Immersed tube: only the shell is checked here, water pressure p = ρ g (water surface − bottom) against the
        /// grade's capacity. Weight, buoyancy and vehicles are carried by the tube as a beam on the player's supports
        /// (<see cref="TunnelStructure"/>); its joint bending and deflection are merged in afterwards. Bearing shows
        /// the net weight per footprint for information.
        /// </summary>
        private static void Immersed(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, TunnelEnvironment env,
            IReadOnlyList<(double x, double force)> loads, TunnelState st)
        {
            var t = level.Terrain; var type = g.Type;
            double water = t.HasWater ? env.WaterY : double.NegativeInfinity;
            for (int i = 0; i < g.Count; i++)
            {
                st.Pressure[i] = cfg.WaterDensity * cfg.Gravity * Math.Max(0, water - g.BottomMin(i));
                st.PressureUtil[i] = st.Pressure[i] / g.Grade[i].PressureCapacity;
                st.NetBuoyancy[i] = -TunnelStructure.NetMass(g, level, cfg, i) * cfg.Gravity * g.Len[i];
                st.Bearing[i] = Math.Max(0, -st.NetBuoyancy[i]) / Math.Max(1e-6, type.TubeWidth * g.Len[i]);
                st.Util[i] = st.PressureUtil[i];
            }
        }

        /// <summary>
        /// Floating tube: shell water pressure as for the immersed tube. The net uplift (buoyancy − tube − ballast,
        /// see <see cref="TunnelStructure.NetMass"/>) and the vehicles are carried by the player's cables as ordinary
        /// members of the structure. Sideways current drag ½ ρ Cd H v² is outside the 2D model: the sway shown is a
        /// labelled simplified estimate (drag / lateral stiffness of one cable line), information only.
        /// </summary>
        private static void Floating(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, TunnelEnvironment env,
            IReadOnlyList<(double x, double force)> loads, TunnelState st)
        {
            var t = level.Terrain; var type = g.Type;
            double water = t.HasWater ? env.WaterY : double.NegativeInfinity;
            for (int i = 0; i < g.Count; i++)
            {
                double len = Math.Max(g.Len[i], 1e-6);
                st.Pressure[i] = cfg.WaterDensity * cfg.Gravity * Math.Max(0, water - g.BottomMin(i));
                st.PressureUtil[i] = st.Pressure[i] / g.Grade[i].PressureCapacity;
                st.NetBuoyancy[i] = -TunnelStructure.NetMass(g, level, cfg, i) * cfg.Gravity * len;
                st.Drag[i] = 0.5 * cfg.WaterDensity * type.DragCoefficient * type.TubeHeight * env.Current * env.Current * len;
                st.Sway[i] = st.Drag[i] / Math.Max(1, type.CableLateralStiffness);
                st.SwayUtil[i] = st.Sway[i] / Math.Max(1e-6, type.MaxDisplacement);
                st.Util[i] = st.PressureUtil[i];
            }
        }

        /// <summary>
        /// Shield tunnel bored below the bed:
        /// - stability: cover above the crown against the minimum of the soil the crown lies in (grouting lowers it);
        ///   too little cover collapses the ground into the tunnel;
        /// - lining load: groundwater pressure ρ g (groundwater level − bottom) plus the effective earth pressure
        ///   K × Σ γ' h of the cover (grouting lowers K); each is reported on its own, the lining resists their sum;
        /// - the groundwater follows the river flood with the soil's permeability and delay (env.GroundwaterRise).
        /// Vehicles inside the bored tube do not load it noticeably.
        /// </summary>
        private static void Shield(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, TunnelEnvironment env, TunnelState st)
        {
            var t = level.Terrain; var type = g.Type;
            double rho = cfg.WaterDensity, gr = cfg.Gravity, H = type.TubeHeight, bed = TunnelRules.BedY(t);
            for (int i = 0; i < g.Count; i++)
            {
                double x = g.MidX(i), top = Math.Max(g.Y0[i], g.Y1[i]) + H, bottom = g.BottomMin(i);
                string crown = TunnelRules.SoilAt(t, x, top + 0.01);
                var soil = cfg.Geo(crown);
                st.CrownSoil[i] = crown;
                st.Cover[i] = bed - top;
                st.CoverReq[i] = soil.MinCover * (g.Reinforced[i] ? type.GroutCoverFactor : 1) * (1 + env.Softening);
                st.StabilityUtil[i] = st.Cover[i] <= 1e-6 ? 9.99 : st.CoverReq[i] / st.Cover[i];

                st.Groundwater[i] = env.GroundwaterRise != null && i < env.GroundwaterRise.Length ? env.GroundwaterRise[i] : 0;
                double gwY = (t.HasWater ? t.WaterLevel : bed) + st.Groundwater[i];
                st.Pressure[i] = rho * gr * Math.Max(0, gwY - bottom);
                st.EarthPressure[i] = soil.EarthK * (g.Reinforced[i] ? type.GroutEarthFactor : 1) * (1 + env.Softening) * TunnelRules.Overburden(cfg, t, x, top);
                st.PressureUtil[i] = (st.Pressure[i] + st.EarthPressure[i]) / g.Grade[i].PressureCapacity;
                // Only material strength: the lining against groundwater + earth pressure (cover is information)
                st.Util[i] = st.PressureUtil[i];
            }
        }

        /// <summary>Groundwater rise target and time constant for element i, used by the simulation: the most
        /// permeable layer the tube passes through (crown, axis or invert) lets the flood water in.</summary>
        public static (double share, double lag) GroundwaterResponse(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, int i)
        {
            double x = g.MidX(i), b = (g.Y0[i] + g.Y1[i]) / 2, h = g.Type.TubeHeight;
            GeoSoilConfig best = null;
            foreach (double y in new[] { b + 0.01, b + h / 2, b + h - 0.01 })
            {
                var s = cfg.Geo(TunnelRules.SoilAt(level.Terrain, x, y));
                if (best == null || s.Permeability > best.Permeability) best = s;
            }
            return (best.Permeability, Math.Max(0.1, best.LagTime));
        }

        /// <summary>What governs element i, as a failure / status sentence.</summary>
        public static string Describe(TunnelGeometry g, TunnelConfig cfg, LevelDefinition level, TunnelState st, int i)
        {
            if (st.BendUtil[i] > st.PressureUtil[i]) return TunnelStructure.BendingFailure(g, i, st.Moment[i]);
            if (g.Type.IsShield)
                return Loc.F("管节 #{0}（{1}）外压 {2:0} kPa（地下水 {3:0} + 土压 {4:0}）超过管片强度 {5:0} kPa，管片被压坏", (i + 1), (g.Grade[i].Name),
                    ((st.Pressure[i] + st.EarthPressure[i]) / 1000), (st.Pressure[i] / 1000), (st.EarthPressure[i] / 1000), (g.Grade[i].PressureCapacity / 1000));
            return Loc.F("管节 #{0}（{1}）水压 {2:0} kPa 超过管壁强度 {3:0} kPa，管壁被压坏", (i + 1), (g.Grade[i].Name), (st.Pressure[i] / 1000), (g.Grade[i].PressureCapacity / 1000));
        }
    }
}
