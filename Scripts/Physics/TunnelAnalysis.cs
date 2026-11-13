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
        /// Immersed tube on the bed:
        /// - water pressure at the tube bottom p = ρ g (water surface − bottom), against the grade's shell capacity;
        /// - bearing q = (weight − buoyancy of the submerged part + vehicles) / footprint, against the soil
        ///   (× ground improvement; × scour factor in a scour zone during a flood unless embedded ≥ ScourCover);
        /// - settlement ∝ bearing utilization; joints check the rotation from differential settlement.
        /// A fully submerged tube displaces a fixed volume, so a rising flood adds pressure, not buoyancy.
        /// </summary>
        private static void Immersed(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, TunnelEnvironment env,
            IReadOnlyList<(double x, double force)> loads, TunnelState st)
        {
            var t = level.Terrain; var type = g.Type;
            double rho = cfg.WaterDensity, gr = cfg.Gravity, B = type.TubeWidth, H = type.TubeHeight, bed = TunnelRules.BedY(t);
            double water = t.HasWater ? env.WaterY : double.NegativeInfinity;
            for (int i = 0; i < g.Count; i++)
            {
                var grade = g.Grade[i];
                double bottom = g.BottomMin(i), len = Math.Max(g.Len[i], 1e-6);
                double head = Math.Max(0, water - bottom);
                st.Pressure[i] = rho * gr * head;
                st.PressureUtil[i] = st.Pressure[i] / grade.PressureCapacity;

                double submerged = Math.Clamp(water - (g.Y0[i] + g.Y1[i]) / 2, 0, H);
                double net = (grade.MassPerMeter * gr - rho * gr * B * submerged) * len;
                if (loads != null) foreach (var (x, f) in loads) if (x >= g.X0[i] - 1e-6 && x < g.X1[i] + 1e-6) net += f;
                st.Bearing[i] = Math.Max(0, net) / (B * len);

                // Weakest soil under the element
                double cap = double.MaxValue, coef = 0;
                foreach (double x in new[] { g.X0[i] + 0.01, g.MidX(i), g.X1[i] - 0.01 })
                {
                    var zone = t.ZoneAt(x); var soil = cfg.Soil(zone);
                    double c = soil.Bearing * (g.Reinforced[i] ? type.ReinforceFactor : 1) * (1 - env.Softening);
                    double embed = bed - bottom;
                    if (env.Scouring && zone.Scour && !g.Reinforced[i] && embed < type.ScourCover - 1e-9) c *= type.ScourFactor;
                    if (c < cap) { cap = c; coef = soil.SettleCoef; }
                }
                st.BearingCap[i] = cap;
                st.BearingUtil[i] = st.Bearing[i] / cap;
                double u = st.BearingUtil[i];
                st.Settle[i] = coef * u * (u > 1 ? 1 + 5 * (u - 1) : 1);
            }
            for (int j = 0; j + 1 < g.Count; j++)
            {
                double span = (g.Len[j] + g.Len[j + 1]) / 2;
                double limit = Math.Min(g.Grade[j].JointRotation, g.Grade[j + 1].JointRotation);
                st.JointUtil[j] = Math.Abs(st.Settle[j] - st.Settle[j + 1]) / Math.Max(span, 1e-6) / limit;
            }
            // Only material strength damages the tunnel: the shell against the water pressure. Bearing,
            // settlement and joints are shown for information only (no leaks, no settlement failures).
            for (int i = 0; i < g.Count; i++) st.Util[i] = st.PressureUtil[i];
        }

        /// <summary>
        /// Floating tube held in the water by anchor cables (positive buoyancy):
        /// - buoyancy B = ρ g × displaced volume (a fully submerged tube gains nothing from a rising flood, only pressure);
        ///   ballast makes the weight B / ratio, so the cables carry the net uplift B − W minus the vehicles;
        ///   vehicles heavier than the net uplift slacken the cables and sink the element;
        /// - current drag F = ½ ρ Cd H v² per metre, sideways (∝ v²);
        /// - each cable carries |(uplift, drag)| / n, the anchors in the bed the whole resultant (soil, reinforcement,
        ///   scour during a flood);
        /// - sway = drag / lateral stiffness, heave = vehicles / vertical stiffness; joints (also at both banks) check
        ///   the rotation from differential heave; shell water pressure as for the immersed tube.
        /// </summary>
        private static void Floating(TunnelGeometry g, LevelDefinition level, TunnelConfig cfg, TunnelEnvironment env,
            IReadOnlyList<(double x, double force)> loads, TunnelState st)
        {
            var t = level.Terrain; var type = g.Type;
            double rho = cfg.WaterDensity, gr = cfg.Gravity, B = type.TubeWidth, H = type.TubeHeight;
            double water = t.HasWater ? env.WaterY : double.NegativeInfinity;
            for (int i = 0; i < g.Count; i++)
            {
                double bottom = g.BottomMin(i), len = Math.Max(g.Len[i], 1e-6);
                st.Pressure[i] = rho * gr * Math.Max(0, water - bottom);
                st.PressureUtil[i] = st.Pressure[i] / g.Grade[i].PressureCapacity;

                // Buoyancy of the submerged part; ballast is designed for the normal water level (fully submerged)
                double submerged = Math.Clamp(water - (g.Y0[i] + g.Y1[i]) / 2, 0, H);
                double buoy = rho * gr * B * submerged * len;
                double weight = rho * gr * B * H * len / Math.Max(1.0001, g.Buoy[i].Ratio);
                double vehicles = 0;
                if (loads != null) foreach (var (x, f) in loads) if (x >= g.X0[i] - 1e-6 && x < g.X1[i] + 1e-6) vehicles += f;
                double uplift = buoy - weight;
                st.NetBuoyancy[i] = uplift;
                st.SlackUtil[i] = uplift > 1e-6 ? vehicles / uplift : (vehicles > 0 ? 9.99 : 0);
                double vertical = Math.Max(0, uplift - vehicles);

                double drag = 0.5 * rho * type.DragCoefficient * H * env.Current * env.Current * len;
                st.Drag[i] = drag;
                double pull = Math.Sqrt(vertical * vertical + drag * drag);
                int n = g.CableCount[i];
                st.Tension[i] = n > 0 ? pull / n : pull;
                st.CableUtil[i] = n > 0 ? st.Tension[i] / g.Cable[i].Capacity : (pull > 1e-6 ? 9.99 : 0);

                // Weakest anchor ground under the element
                double cap = double.MaxValue;
                foreach (double x in new[] { g.X0[i] + 0.01, g.MidX(i), g.X1[i] - 0.01 })
                {
                    var zone = t.ZoneAt(x);
                    double c = type.AnchorCapacity.TryGetValue(zone.IsRock ? "rock" : "soft", out var ac) ? ac : 1e12;
                    if (g.Reinforced[i]) c *= type.ReinforceFactor;
                    else if (env.Scouring && zone.Scour) c *= type.ScourFactor;
                    c *= 1 - env.Softening;
                    cap = Math.Min(cap, c);
                }
                st.AnchorPull[i] = pull; st.AnchorCap[i] = cap;
                st.AnchorUtil[i] = pull / cap;

                double kl = Math.Max(1, n) * type.CableLateralStiffness, kv = Math.Max(1, n) * type.CableVerticalStiffness;
                st.Sway[i] = n > 0 ? drag / kl : type.MaxDisplacement * 9.99;
                st.SwayUtil[i] = st.Sway[i] / type.MaxDisplacement;
                // Heave (down, from the vehicles); a slack element sinks further
                double sl = st.SlackUtil[i];
                st.Settle[i] = vehicles / kv * (sl > 1 ? 1 + 20 * (sl - 1) : 1);
            }
            for (int j = 0; j + 1 < g.Count; j++)
            {
                double span = (g.Len[j] + g.Len[j + 1]) / 2;
                double limit = Math.Min(g.Grade[j].JointRotation, g.Grade[j + 1].JointRotation);
                st.JointUtil[j] = Math.Abs(st.Settle[j] - st.Settle[j + 1]) / Math.Max(span, 1e-6) / limit;
            }
            // Both ends are fixed in the bank portals
            int last = g.Count - 1;
            st.EndJointLeft = g.Count > 0 ? st.Settle[0] / Math.Max(g.Len[0], 1e-6) / g.Grade[0].JointRotation : 0;
            st.EndJointRight = g.Count > 0 ? st.Settle[last] / Math.Max(g.Len[last], 1e-6) / g.Grade[last].JointRotation : 0;
            // Only material strength: the shell (water pressure) and the anchor cables (tension)
            for (int i = 0; i < g.Count; i++) st.Util[i] = Math.Max(st.PressureUtil[i], st.CableUtil[i]);
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

        private static string DescribeFloating(TunnelGeometry g, TunnelConfig cfg, LevelDefinition level, TunnelState st, int i)
        {
            if (st.CableUtil[i] > st.PressureUtil[i])
                return g.CableCount[i] == 0
                    ? Loc.F("管节 #{0} 没有锚索，被浮力和水流拉走", (i + 1))
                    : Loc.F("管节 #{0} 的{1}被拉断（每根 {2:0} 吨 > {3:0} 吨，共 {4} 根）", (i + 1), (g.Cable[i].Name), (st.Tension[i] / cfg.Gravity / 1000), (g.Cable[i].Capacity / cfg.Gravity / 1000), (g.CableCount[i]));
            return Loc.F("管节 #{0}（{1}）水压 {2:0} kPa 超过管壁强度 {3:0} kPa，管壁被压坏", (i + 1), (g.Grade[i].Name), (st.Pressure[i] / 1000), (g.Grade[i].PressureCapacity / 1000));
        }

        /// <summary>What governs element i, as a failure / status sentence.</summary>
        public static string Describe(TunnelGeometry g, TunnelConfig cfg, LevelDefinition level, TunnelState st, int i)
        {
            if (g.Type.IsFloating) return DescribeFloating(g, cfg, level, st, i);
            if (g.Type.IsShield)
                return Loc.F("管节 #{0}（{1}）外压 {2:0} kPa（地下水 {3:0} + 土压 {4:0}）超过管片强度 {5:0} kPa，管片被压坏", (i + 1), (g.Grade[i].Name),
                    ((st.Pressure[i] + st.EarthPressure[i]) / 1000), (st.Pressure[i] / 1000), (st.EarthPressure[i] / 1000), (g.Grade[i].PressureCapacity / 1000));
            return Loc.F("管节 #{0}（{1}）水压 {2:0} kPa 超过管壁强度 {3:0} kPa，管壁被压坏", (i + 1), (g.Grade[i].Name), (st.Pressure[i] / 1000), (g.Grade[i].PressureCapacity / 1000));
        }
    }
}
