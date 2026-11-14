using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    // ---------------- Configuration (Data/Tunnels/tunnels.json) ----------------

    /// <summary>One strength grade of a tunnel type (normal / reinforced / heavy).</summary>
    public class TunnelGradeConfig
    {
        public string Name { get; set; }
        public double CostPerMeter { get; set; }
        /// <summary>Structure mass per metre, kg/m (with ballast).</summary>
        public double MassPerMeter { get; set; }
        /// <summary>External water pressure the shell withstands, Pa.</summary>
        public double PressureCapacity { get; set; }
        /// <summary>Joint rotation (differential settlement / element length) before the joint leaks, rad.</summary>
        public double JointRotation { get; set; }
        /// <summary>Load capacity of a support point on rock, N.</summary>
        public double SupportLoad { get; set; }
        public string Color { get; set; }
    }

    public class SoilConfig
    {
        public string Name { get; set; }
        /// <summary>Allowable bearing pressure, Pa.</summary>
        public double Bearing { get; set; }
        /// <summary>Settlement at 100% bearing utilization, m (linear).</summary>
        public double SettleCoef { get; set; }
    }

    /// <summary>One ground layer below the river bed; Thickness 0 = continues downwards.</summary>
    public class GeoLayer
    {
        public string Soil { get; set; }
        public double Thickness { get; set; }
    }

    /// <summary>Layers from the bed downwards for X0..X1.</summary>
    public class GeoColumn
    {
        public double X0 { get; set; }
        public double X1 { get; set; }
        public List<GeoLayer> Layers { get; set; } = new();
    }

    /// <summary>Shield tunnel ground type (game balance values, not design standards).</summary>
    public class GeoSoilConfig
    {
        public string Name { get; set; }
        /// <summary>Minimum ground cover above the tunnel crown when the crown lies in this soil, m.</summary>
        public double MinCover { get; set; }
        /// <summary>Effective (submerged) unit weight, N/m³, and the earth pressure coefficient on the lining.</summary>
        public double Gamma { get; set; }
        public double EarthK { get; set; } = 1;
        /// <summary>Share of a flood rise that reaches the groundwater around the tunnel, and its delay (time constant), s.</summary>
        public double Permeability { get; set; }
        public double LagTime { get; set; } = 10;
        /// <summary>Boring cost per metre of tunnel through this soil.</summary>
        public double ExcavationCost { get; set; }
        public string Color { get; set; }
    }

    /// <summary>Floating tunnel buoyancy level: buoyancy / weight ratio (ballast is set to match) and its price.</summary>
    public class BuoyancyConfig
    {
        public string Name { get; set; }
        public double Ratio { get; set; } = 1.2;
        public double CostPerMeter { get; set; }
    }

    /// <summary>Floating tunnel anchor cable kind.</summary>
    public class CableConfig
    {
        public string Name { get; set; }
        /// <summary>Tension one cable carries, N.</summary>
        public double Capacity { get; set; }
        public double CostPerMeter { get; set; }
        public string Color { get; set; }
    }

    /// <summary>Bank lift: vertical transport of one vehicle between the bank road and the tube floor.</summary>
    public class LiftConfig
    {
        public string MaterialId { get; set; } = "lift";
        /// <summary>Platform length range, m. The longest fits the longest vehicle (extra-long semi, 24.5 m).</summary>
        public double MinLength { get; set; } = 6;
        public double MaxLength { get; set; } = 26;
        public double LengthStep { get; set; } = 1;
        /// <summary>Free space in front of and behind the vehicle on the platform, m.</summary>
        public double Margin { get; set; } = 0.75;
        /// <summary>Travel speed, m/s.</summary>
        public double Speed { get; set; } = 2.5;
        /// <summary>Gap between parked vehicles, boarding dwell, and full door travel time.</summary>
        public double VehicleGap { get; set; } = 0.8;
        public double BoardingSeconds { get; set; } = 2.5;
        public double DoorSeconds { get; set; } = 0.8;
        /// <summary>Platform load capacity = CapacityBase + CapacityPerMeter × length, N.</summary>
        public double CapacityBase { get; set; } = 200000;
        public double CapacityPerMeter { get; set; } = 25000;
        public double ShaftCostPerMeter { get; set; } = 300;
        public double PlatformCostPerMeter { get; set; } = 250;

        // ---- Structural lift (LiftStructure): the platform, hoists and shaft are ordinary members in each shaft ----
        /// <summary>Head room above the upper landing (sheave height), m; pit below the lower landing, m.</summary>
        public double HeadRoom { get; set; } = 7;
        public double Pit { get; set; } = 3;
        /// <summary>Default shaft columns stand this far outside the platform ends, m (in the shaft end walls).</summary>
        public double ColumnOffset { get; set; } = 0.3;
        /// <summary>A node this close to a shaft end wall face is tied to the lining (held sideways), m.</summary>
        public double TieTolerance { get; set; } = 0.2;
        /// <summary>Longest shaft-column segment a template generates, m (below CrossBeams.MinLength).</summary>
        public double ColumnSegment { get; set; } = 4.5;
        public double MaxCounterweight { get; set; } = 60;
        public double CounterweightStep { get; set; } = 5;
        /// <summary>Counterweight price per tonne; lining tie (lateral restraint) price each.</summary>
        public double CounterweightCostPerTonne { get; set; } = 60;
        public double BraceCost { get; set; } = 150;
        public Dictionary<string, LiftDriveConfig> Drives { get; set; } = new()
        {
            ["standard"] = new() { Name = Loc.T("标准驱动"), Force = 300e3, Cost = 3000 },
            ["strong"] = new() { Name = Loc.T("加强驱动"), Force = 600e3, Cost = 5500 },
            ["heavy"] = new() { Name = Loc.T("重型驱动"), Force = 1000e3, Cost = 9000 },
        };
        /// <summary>Starting layouts for "rebuild the lift structure": they only generate ordinary members, which the
        /// player then edits with the normal build tools.</summary>
        public Dictionary<string, LiftDesign> Templates { get; set; } = new()
        {
            ["light"] = new() { Template = "light", Deck = "road", TrussDepth = 1.5, Panel = 5, Web = "steel", Chord = "cable", Diagonal = "cable", Hoist = "cable", Column = "concrete", Drive = "standard", Counterweight = 5 },
            ["standard"] = new() { Template = "standard", Deck = "deck_steel", TrussDepth = 2.5, Panel = 5, Web = "steel", Chord = "cable_composite", Diagonal = "cable_composite", Hoist = "cable", Column = "concrete", Drive = "strong", Counterweight = 20 },
            ["heavy"] = new() { Template = "heavy", Deck = "deck_steel", TrussDepth = 2.5, Panel = 4, Web = "steel", Chord = "cable_composite", Diagonal = "cable_composite", Hoist = "cable_composite", Column = "concrete", Drive = "heavy", Counterweight = 30 },
        };
        public static string TemplateName(string id) => id switch { "light" => Loc.T("轻型模板"), "standard" => Loc.T("标准模板"), "heavy" => Loc.T("重型模板"), _ => Loc.T("自定义") };
    }

    public class LiftDriveConfig
    {
        public string Name { get; set; }
        /// <summary>Largest out-of-balance rope force the machine holds (|ropes − counterweight|), N.</summary>
        public double Force { get; set; }
        public double Cost { get; set; }
    }

    /// <summary>
    /// Lift settings of both bank lifts (TunnelRoute.Lift). The structure itself (platform, hoist ropes, shaft
    /// columns) is ordinary members in each shaft; this keeps the machine choice (drive, counterweight) and the
    /// template the members were first generated from (Deck … Column).
    /// </summary>
    public class LiftDesign
    {
        public string BoardingPolicy { get; set; } = LiftOperation.Shared;
        public string Template { get; set; }
        public string Deck { get; set; } = "deck_steel";
        /// <summary>Platform truss under the deck: depth (0 = deck only), panel length, web and bottom chord materials.</summary>
        public double TrussDepth { get; set; } = 2.5;
        public double Panel { get; set; } = 5;
        public string Web { get; set; } = "steel";
        public string Chord { get; set; } = "cable_composite";
        /// <summary>Platform truss diagonals (tension in a hanging platform).</summary>
        public string Diagonal { get; set; } = "cable_composite";
        public string Hoist { get; set; } = "cable";
        public string Column { get; set; } = "concrete";
        public string Drive { get; set; } = "strong";
        public double Counterweight { get; set; } = 20;
        /// <summary>Members generated in the shafts (null = older save: generated once from the values above).</summary>
        public int? Built { get; set; }
        public LiftDesign Clone() => (LiftDesign)MemberwiseClone();
    }

    public class TunnelTypeConfig
    {
        public string Name { get; set; }
        /// <summary>"bed" (laid on / in the river bed) or "water" (held in the water column by anchor cables).</summary>
        public string Placement { get; set; } = "bed";
        /// <summary>Joint support points for building on top (immersed tube only).</summary>
        public bool HasSupports { get; set; } = true;
        /// <summary>Floating: water above the tube roof (ships, waves) and clearance below the tube, m.</summary>
        public double MinCover { get; set; }
        public double MinBedClearance { get; set; }
        /// <summary>Immersed tube: highest the tube bottom may sit above the bed (room for the player's supports), m.</summary>
        public double MaxBedClearance { get; set; }
        // Longitudinal structure of the tube (immersed / floating): the tube is a chain of beam elements in the
        // structural model. EA / EI of the element body, rotational stiffness of one joint (N·m/rad); a joint
        // opens fully at the grade's JointRotation, so its moment capacity is JointStiffness × JointRotation.
        public double TubeEA { get; set; } = 9e11;
        public double TubeEI { get; set; } = 7.7e12;
        public double JointStiffness { get; set; } = 8e8;
        public Dictionary<string, BuoyancyConfig> Buoyancy { get; set; } = new();
        public string DefaultBuoyancy { get; set; }
        public Dictionary<string, CableConfig> Cables { get; set; } = new();
        public string DefaultCable { get; set; }
        public int DefaultCableCount { get; set; } = 2;
        public int MaxCableCount { get; set; } = 6;
        /// <summary>Cable angle from the vertical (the pairs splay sideways), degrees.</summary>
        public double CableAngleDeg { get; set; } = 30;
        /// <summary>Stiffness one cable adds to an element: sideways and vertical, N/m.</summary>
        public double CableLateralStiffness { get; set; }
        public double CableVerticalStiffness { get; set; }
        public double DragCoefficient { get; set; } = 1;
        /// <summary>Sideways sway at which the tube is overstressed, m.</summary>
        public double MaxDisplacement { get; set; } = 1;
        /// <summary>Pull an element's anchor group holds, by soil, N (× ReinforceFactor when the anchors are reinforced).</summary>
        public Dictionary<string, double> AnchorCapacity { get; set; } = new();
        public bool IsFloating => Placement == "water";
        /// <summary>Prefabricated straight tube (immersed): no middle control points, only the two ends move vertically.</summary>
        public bool Straight { get; set; }
        /// <summary>"ground": shield tunnel bored below the bed.</summary>
        public bool IsShield => Placement == "ground";
        /// <summary>Shield: deepest bottom below the bed, cover of a new tunnel, and what grouting (Reinforced) does.</summary>
        public double MaxDepthBelowBed { get; set; } = 40;
        public double DefaultCover { get; set; } = 6;
        public double GroutCoverFactor { get; set; } = 1;
        public double GroutEarthFactor { get; set; } = 1;
        public double TubeHeight { get; set; } = 8;
        public double TubeWidth { get; set; } = 9;
        public double FloorHeight { get; set; } = 0.8;
        /// <summary>Immersed: deepest dredged trench below the bed, m.</summary>
        public double MaxTrenchDepth { get; set; }
        public double DredgeCostPerMeterDepth { get; set; }
        /// <summary>Cut-and-cover ramp from the bank road down to the tube, per metre of run.</summary>
        public double RampCostPerMeter { get; set; }
        /// <summary>Ground improvement under an element: cost per metre and bearing multiplier (also stops scour).</summary>
        public double ReinforceCostPerMeter { get; set; }
        public double ReinforceFactor { get; set; } = 1;
        /// <summary>Bearing multiplier in a scour zone during a flood, unless the tube is embedded at least ScourCover.</summary>
        public double ScourFactor { get; set; } = 1;
        public double ScourCover { get; set; }
        public Dictionary<string, TunnelGradeConfig> Grades { get; set; } = new();
    }

    public class TunnelConfig
    {
        public double ElementLength { get; set; } = 10;
        public double WaterDensity { get; set; } = 1000;
        public double Gravity { get; set; } = 9.81;
        /// <summary>Risk colour bands: green below RiskYellow, yellow, orange from RiskOrange, red from 100%.</summary>
        public double RiskYellow { get; set; } = 0.5;
        public double RiskOrange { get; set; } = 0.75;
        public double SoftSupportFactor { get; set; } = 0.5;
        /// <summary>River current, m/s: normal and at the flood peak (drag grows with v²).</summary>
        public double CurrentNormal { get; set; } = 0.5;
        public double CurrentFlood { get; set; } = 1.1;
        public Dictionary<string, GeoSoilConfig> Geology { get; set; } = new();
        public GeoSoilConfig Geo(string id) => id != null && Geology.TryGetValue(id, out var s) ? s : new GeoSoilConfig { Name = id ?? "?", MinCover = 6, Gamma = 9000, EarthK = 1 };
        public Dictionary<string, SoilConfig> Soils { get; set; } = new();
        public Dictionary<string, TunnelTypeConfig> Types { get; set; } = new();
        /// <summary>Vertical lifts that carry vehicles between the bank road and the tube floor.</summary>
        public LiftConfig Lift { get; set; } = new();

        public SoilConfig Soil(FoundationZone z) =>
            Soils.TryGetValue(z.IsRock ? "rock" : "soft", out var s) ? s : new SoilConfig { Name = z.Name, Bearing = 1e9, SettleCoef = 0 };
        public TunnelTypeConfig Type(MaterialDefinition mat) =>
            mat?.TunnelType != null && Types.TryGetValue(mat.TunnelType, out var t) ? t : null;
    }

    // ---------------- Design data ----------------

    /// <summary>One standard-length tube element: strength grade and optional ground improvement.</summary>
    public class TunnelElement
    {
        public string Grade { get; set; } = TunnelRules.DefaultGrade;
        /// <summary>Immersed: ground improvement. Floating: reinforced anchor foundation.</summary>
        public bool Reinforced { get; set; }
        /// <summary>Floating only (null = type default): anchor cable count, cable kind, buoyancy level.</summary>
        public int? CableCount { get; set; }
        public string Cable { get; set; }
        public string Buoyancy { get; set; }
        public TunnelElement Clone() => (TunnelElement)MemberwiseClone();
    }

    /// <summary>
    /// The underwater tunnel of a design. Its route is a polyline through control nodes (BridgeNode.TunnelPoint);
    /// node Y is the tube bottom. Elements of ElementLength are laid along it; support points
    /// (BridgeNode.TunnelJoint &gt; 0) sit on the roof at the element joints.
    /// </summary>
    public class TunnelRoute
    {
        public string MaterialId { get; set; }
        public List<int> PointNodeIds { get; set; } = new();
        public List<TunnelElement> Elements { get; set; } = new();
        /// <summary>Price, kept up to date by the editor (TunnelRules.Cost).</summary>
        public double Cost { get; set; }
        /// <summary>Portal flood protection (both banks): entrance raised above the bank road (m), flood gates, drainage pumps.</summary>
        public double PortalRaise { get; set; }
        public bool FloodGate { get; set; }
        public bool Pump { get; set; }
        /// <summary>Platform length of both bank lifts, m (null = the longest platform, fits every vehicle).
        /// PortalRaise / FloodGate / Pump are kept only so old saves load; lifts replaced the ramps and portals.</summary>
        public double? LiftLength { get; set; }
        /// <summary>Structural tube version: null = older save whose tube had built-in supports / cable counts;
        /// 1 = the tube is carried by the player's members (old capacity migrated into visible piers / cables once).</summary>
        public int? Structure { get; set; }
        /// <summary>Structure of both lifts (null = older save: its old platform capacity is migrated into a template once).</summary>
        public LiftDesign Lift { get; set; }
        public TunnelRoute Clone() => new() { MaterialId = MaterialId, PointNodeIds = PointNodeIds.ToList(), Elements = Elements.Select(e => e.Clone()).ToList(), Cost = Cost,
            PortalRaise = PortalRaise, FloodGate = FloodGate, Pump = Pump, LiftLength = LiftLength, Structure = Structure, Lift = Lift?.Clone() };
    }

    /// <summary>Save version 4 tunnel section (read only, migrated to <see cref="TunnelRoute"/> on load).</summary>
    public class TunnelSegment
    {
        public int Id { get; set; }
        public string MaterialId { get; set; }
        public double X0 { get; set; }
        public double X1 { get; set; }
        public double Cost { get; set; }
    }

    // ---------------- Geometry ----------------

    /// <summary>Tube geometry derived from a design: control polyline and the standard elements along it.</summary>
    public class TunnelGeometry
    {
        public MaterialDefinition Material;
        public TunnelTypeConfig Type;
        public readonly List<Vec3d> Points = new();
        public double TotalLength;
        public int Count => X0.Count;
        // Per element: ends (bottom of the tube), length, grade, ground improvement
        public readonly List<double> X0 = new(), Y0 = new(), X1 = new(), Y1 = new(), Len = new();
        public readonly List<TunnelGradeConfig> Grade = new();
        public readonly List<string> GradeId = new();
        public readonly List<bool> Reinforced = new();
        // Floating only
        public readonly List<int> CableCount = new();
        public readonly List<string> CableId = new(), BuoyancyId = new();
        public readonly List<CableConfig> Cable = new();
        public readonly List<BuoyancyConfig> Buoy = new();
        /// <summary>Portal flood protection copied from the route.</summary>
        public double PortalRaise;
        public bool FloodGate, Pump;
        /// <summary>Platform length of the bank lifts, m, and their structure.</summary>
        public double LiftLength;
        public LiftDesign Lift;

        public double MidX(int i) => (X0[i] + X1[i]) / 2;
        public double BottomMin(int i) => Math.Min(Y0[i], Y1[i]);

        /// <summary>Point on the polyline at arc length s (bottom of the tube).</summary>
        public Vec3d At(double s)
        {
            s = Math.Clamp(s, 0, TotalLength);
            for (int k = 0; k + 1 < Points.Count; k++)
            {
                double l = Vec3d.Distance(Points[k], Points[k + 1]);
                if (s <= l + 1e-9 || k + 2 == Points.Count)
                {
                    double t = l > 1e-9 ? Math.Clamp(s / l, 0, 1) : 0;
                    return new Vec3d(Points[k].X + (Points[k + 1].X - Points[k].X) * t, Points[k].Y + (Points[k + 1].Y - Points[k].Y) * t);
                }
                s -= l;
            }
            return Points.Count > 0 ? Points[^1] : new Vec3d(0, 0);
        }

        /// <summary>Tube bottom height at x (polyline interpolation), or NaN outside the tube.</summary>
        public double BottomAt(double x)
        {
            for (int k = 0; k + 1 < Points.Count; k++)
                if (x >= Points[k].X - 1e-9 && x <= Points[k + 1].X + 1e-9)
                {
                    double dx = Points[k + 1].X - Points[k].X;
                    return dx < 1e-9 ? Points[k].Y : Points[k].Y + (Points[k + 1].Y - Points[k].Y) * (x - Points[k].X) / dx;
                }
            return double.NaN;
        }

        public int ElementAt(double x)
        {
            for (int i = 0; i < Count; i++) if (x >= X0[i] - 1e-6 && x <= X1[i] + 1e-6) return i;
            return -1;
        }

        public static TunnelGeometry Build(BridgeDesign d, GameDatabase db)
        {
            var r = d?.Tunnel;
            if (r == null || db?.TunnelConfig == null) return null;
            var mat = db.GetMaterial(r.MaterialId);
            var type = db.TunnelConfig.Type(mat);
            if (type == null || type.Grades.Count == 0) return null;
            // Lifts replaced ramps and portals: raise / gate / pump of old saves no longer do anything
            var lc = db.TunnelConfig.Lift ?? new LiftConfig();
            var g = new TunnelGeometry { Material = mat, Type = type, LiftLength = Math.Clamp(r.LiftLength ?? lc.MaxLength, lc.MinLength, lc.MaxLength), Lift = r.Lift };
            foreach (int id in r.PointNodeIds) { var pn = d.FindNode(id); if (pn != null) g.Points.Add(pn.Position); }
            g.Points.Sort((a, b) => a.X.CompareTo(b.X));
            if (g.Points.Count < 2) return null;
            if (!type.Straight)
            {
                // Bendable tubes: points are the element joints (older saves / reference files are resampled here too)
                var xs = TunnelRules.JointXs(g.Points[0].X, g.Points[^1].X, db.TunnelConfig.ElementLength);
                if (xs.Count != g.Points.Count || xs.Where((x, k) => Math.Abs(x - g.Points[k].X) > 1e-6).Any())
                {
                    var joints = xs.Select(x => new Vec3d(x, g.BottomAt(x))).ToList();
                    g.Points.Clear(); g.Points.AddRange(joints);
                }
            }
            for (int k = 0; k + 1 < g.Points.Count; k++) g.TotalLength += Vec3d.Distance(g.Points[k], g.Points[k + 1]);
            double L = db.TunnelConfig.ElementLength;
            // Straight tubes are cut every L along the line; bendable tubes have one element per pair of
            // neighbouring points, so every draggable point sits exactly on an element joint
            int n = type.Straight ? TunnelRules.ElementCount(g.TotalLength, L) : g.Points.Count - 1;
            for (int i = 0; i < n; i++)
            {
                Vec3d a, b; double len;
                if (type.Straight) { double s1 = Math.Min((i + 1) * L, g.TotalLength); a = g.At(i * L); b = g.At(s1); len = s1 - i * L; }
                else { a = g.Points[i]; b = g.Points[i + 1]; len = Vec3d.Distance(a, b); }
                g.X0.Add(a.X); g.Y0.Add(a.Y); g.X1.Add(b.X); g.Y1.Add(b.Y);
                g.Len.Add(len);
                var e = i < r.Elements.Count ? r.Elements[i] : new TunnelElement();
                string gid = type.Grades.ContainsKey(e.Grade ?? "") ? e.Grade : type.Grades.Keys.First();
                g.GradeId.Add(gid); g.Grade.Add(type.Grades[gid]); g.Reinforced.Add(e.Reinforced);
                if (type.IsFloating)
                {
                    string cid = e.Cable != null && type.Cables.ContainsKey(e.Cable) ? e.Cable : type.DefaultCable ?? type.Cables.Keys.FirstOrDefault();
                    string bid = e.Buoyancy != null && type.Buoyancy.ContainsKey(e.Buoyancy) ? e.Buoyancy : type.DefaultBuoyancy ?? type.Buoyancy.Keys.FirstOrDefault();
                    g.CableCount.Add(Math.Clamp(e.CableCount ?? type.DefaultCableCount, 0, type.MaxCableCount));
                    g.CableId.Add(cid); g.Cable.Add(cid == null ? new CableConfig { Name = "-", Capacity = 1 } : type.Cables[cid]);
                    g.BuoyancyId.Add(bid); g.Buoy.Add(bid == null ? new BuoyancyConfig { Name = "-" } : type.Buoyancy[bid]);
                }
            }
            return g;
        }
    }

    // ---------------- Rules ----------------

    /// <summary>
    /// Underwater tunnel rules shared by the editor, the structural model, the analysis and the simulation.
    /// When the tube covers the whole flat bed, road vehicles drive through it: approach ramps run inside both
    /// banks from a portal on the bank surface down to the tube floor.
    /// </summary>
    public static class TunnelRules
    {
        public const string DefaultGrade = "normal";
        public const double MinLength = 2;
        /// <summary>The tube must reach this close to both wall toes to connect the banks.</summary>
        public const double JoinTolerance = 0.5;
        /// <summary>Road lane id of the tunnel route.</summary>
        public const int Lane = 100;
        /// <summary>Flat ground road before the first ramp / bridge end and after the last, m.</summary>
        public const double GroundApproach = 25;
        /// <summary>Default ramp grade when a level has no vehicles with a known limit.</summary>
        public const double DefaultRampGrade = 0.06;
        /// <summary>Length of the vertical curve that smooths every grade change, m (shorter when segments are short).</summary>
        public const double VerticalCurve = 20;

        private static GameDatabase _db;
        /// <summary>Called by GameDatabase so ramp grades can read the vehicle and tunnel data.</summary>
        public static void Bind(GameDatabase db) => _db = db;

        private static bool HasTunnel(LevelDefinition level) =>
            _db?.TunnelConfig != null && level.AllowedMaterials.Any(id => _db.GetMaterial(id) is { IsTunnel: true } m && _db.TunnelConfig.Type(m) != null);

        /// <summary>Ramp / tube grade every vehicle of the level can climb: the level override, else the weakest vehicle.</summary>
        public static double RampGrade(LevelDefinition level) => RampGrade(level, out _);

        public static double RampGrade(LevelDefinition level, out VehicleDefinition limiting)
        {
            limiting = null;
            if (level.Objectives.TunnelMaxGrade > 0) return level.Objectives.TunnelMaxGrade;
            double g = double.MaxValue;
            foreach (var e in level.TrafficEvents)
            {
                if (e.Type != TrafficEventType.Vehicle || _db == null || !_db.Vehicles.TryGetValue(e.VehicleId ?? "", out var v) || v.Kind == "train") continue;
                if (v.MaxRampGrade < g) { g = v.MaxRampGrade; limiting = v; }
            }
            return g == double.MaxValue ? DefaultRampGrade : g;
        }

        /// <summary>Horizontal run of the ramp from the bank road down to a floor at floorY.</summary>
        public static double RampRun(LevelDefinition level, bool left, double floorY, double raise = 0)
        {
            BankRoad(level, out double yl, out double yr);
            return Math.Max(0, (left ? yl : yr) + raise - floorY) / RampGrade(level);
        }

        /// <summary>Entrance height of a portal: the bank road plus the raise.</summary>
        public static double EntranceY(LevelDefinition level, bool left, double raise)
        {
            BankRoad(level, out double yl, out double yr);
            return (left ? yl : yr) + raise;
        }

        public static WeatherConfig Weather => _db?.Weather ?? WeatherConfig.Default();

        /// <summary>Fixed ground start / end of the main road (same for the bridge and the tunnel route), so every
        /// vehicle starts and finishes at the same place whichever crossing it takes.</summary>
        public static double GroundStartX(LevelDefinition level) => GroundEnd(level, true);
        public static double GroundEndX(LevelDefinition level) => GroundEnd(level, false);

        private static double GroundEnd(LevelDefinition level, bool left)
        {
            var t = level.Terrain;
            var a = level.Anchors.FirstOrDefault(an => left ? an.IsRoadStart && an.RoadLane == 0 : an.IsRoadEnd && an.RoadLane == 0);
            double x = a?.Position.X ?? (left ? 0 : t.RiverWidth);
            double edge = left ? Math.Min(x, 0) : Math.Max(x, t.RiverWidth);
            if (HasTunnel(level))
            {
                // Room for the longest lift platform behind each wall (lifts replaced the long ramps)
                double run = LiftCfg.MaxLength + 2;
                FlatBed(t, out double b0, out double b1);
                edge = left ? Math.Min(edge, b0 - run) : Math.Max(edge, b1 + run);
            }
            return left ? edge - GroundApproach : edge + GroundApproach;
        }

        /// <summary>Ground length beyond each bank the scenery must cover (the bank is extended for long ramps).</summary>
        public static double BankExtent(LevelDefinition level) =>
            Math.Max(SceneryLayout.BankLength, Math.Max(-GroundStartX(level), GroundEndX(level) - level.Terrain.RiverWidth) + 10);

        public static int ElementCount(double total, double elementLength) =>
            total <= 1e-6 ? 0 : Math.Max(1, (int)Math.Ceiling(total / elementLength - 1e-6));

        public static double BedY(TerrainConfig t) => t.RiverBedDepth;

        /// <summary>Flat bed between the toes of both bank walls.</summary>
        public static void FlatBed(TerrainConfig t, out double x0, out double x1)
        {
            double run = t.WallRun;
            x0 = Math.Max(0, (t.LeftBankHeight - t.RiverBedDepth) * run);
            x1 = t.RiverWidth - Math.Max(0, (t.RightBankHeight - t.RiverBedDepth) * run);
        }

        public static double WaterDepth(TerrainConfig t) => t.HasWater ? Math.Max(0, t.WaterLevel - t.RiverBedDepth) : 0;

        public static bool Complete(TunnelGeometry g, TerrainConfig t)
        {
            if (g == null) return false;
            FlatBed(t, out double b0, out double b1);
            return g.Points[0].X <= b0 + JoinTolerance && g.Points[^1].X >= b1 - JoinTolerance;
        }

        /// <summary>Joint x positions of a bendable tube from wall to wall: equal spacing, no longer than one element.</summary>
        public static List<double> JointXs(double x0, double x1, double elementLength)
        {
            int n = Math.Max(1, (int)Math.Ceiling((x1 - x0) / Math.Max(1, elementLength) - 1e-9));
            var xs = new List<double>();
            for (int k = 0; k <= n; k++) xs.Add(k == n ? x1 : x0 + (x1 - x0) * k / n);
            return xs;
        }

        public static double SupportCapacity(TunnelConfig cfg, TunnelGradeConfig grade, FoundationZone zone) =>
            grade.SupportLoad;   // tunnels ignore the soil: only the element grade sets it

        /// <summary>Price: elements by grade, ground improvement, dredging of the trench below the bed and both ramps.</summary>
        public static double Cost(TunnelGeometry g, LevelDefinition level)
        {
            if (g == null) return 0;
            if(level.MechanicalLift)return 0;
            var t = level.Terrain;
            // Both bank lifts: shaft height and platform length
            double c = 0;
            // Lifts: every platform, rope, column, restraint, drive and counterweight at its material price
            // Lift members are ordinary members (priced as members); here: drive, counterweight and shaft excavation per lift
            var lc = LiftCfg;
            foreach (var s in Shafts(level, g))
            {
                var drive = g.Lift == null ? null : lc.Drives.GetValueOrDefault(g.Lift.Drive ?? "");
                c += (level.ProvidedInfrastructure==null?Math.Max(0, s.Top - s.Bottom) * lc.ShaftCostPerMeter:0) + (drive?.Cost ?? 0) + (g.Lift?.Counterweight ?? 0) * lc.CounterweightCostPerTonne;
            }
            if(level.ProvidedInfrastructure!=null)return c;
            double bed = BedY(t);
            for (int i = 0; i < g.Count; i++)
            {
                double trench = Math.Max(0, bed - (g.Y0[i] + g.Y1[i]) / 2);
                c += g.Len[i] * (g.Grade[i].CostPerMeter + (g.Reinforced[i] && g.Type.IsShield ? g.Type.ReinforceCostPerMeter : 0) + trench * g.Type.DredgeCostPerMeterDepth);
                // Anchor cables of floating tubes are ordinary members now (priced as members); only the buoyancy / ballast level
                if (g.Type.IsFloating) c += g.Len[i] * g.Buoy[i].CostPerMeter;
                if (g.Type.IsShield && _db?.TunnelConfig != null)
                    c += g.Len[i] * _db.TunnelConfig.Geo(SoilAt(t, g.MidX(i), (g.Y0[i] + g.Y1[i]) / 2 + g.Type.TubeHeight / 2)).ExcavationCost;
            }
            return c;
        }

        /// <summary>Length of one anchor cable of element i (tube bottom to bed, inclined at CableAngleDeg).</summary>
        public static double CableLength(TunnelGeometry g, TerrainConfig t, int i) =>
            Math.Max(0, (g.Y0[i] + g.Y1[i]) / 2 - BedY(t)) / Math.Cos(g.Type.CableAngleDeg * Math.PI / 180);

        /// <summary>Allowed height range of the tube bottom (control points) for this type.</summary>
        public static void BottomRange(TerrainConfig t, TunnelTypeConfig type, out double lo, out double hi)
        {
            double bed = BedY(t);
            if (type.IsFloating) { lo = bed + type.MinBedClearance; hi = t.WaterLevel - type.MinCover - type.TubeHeight; }
            else if (type.IsShield) { lo = bed - type.MaxDepthBelowBed; hi = bed - type.TubeHeight - 1; }
            else { lo = bed + type.MinBedClearance; hi = Math.Max(lo, Math.Min(bed + type.MaxBedClearance, (t.HasWater ? t.WaterLevel : bed + 99) - type.MinCover - type.TubeHeight)); }
            // Never deeper than the level's build area
            lo = Math.Max(lo, t.BuildMinY);
        }

        /// <summary>Where a new tube is laid: on the bed, or floating as shallow as allowed.</summary>
        public static double DefaultBottom(TerrainConfig t, TunnelTypeConfig type)
        {
            BottomRange(t, type, out double lo, out double hi);
            return type.IsFloating ? Math.Max(lo, hi) : type.IsShield ? Math.Max(lo, bed() - type.TubeHeight - type.DefaultCover) : lo;
            double bed() => BedY(t);
        }

        /// <summary>Ground type at (x, y) below the bed: the level's strata, else rock / clay from the foundation zones.</summary>
        public static string SoilAt(TerrainConfig t, double x, double y)
        {
            var col = t.Strata?.FirstOrDefault(c => x >= c.X0 - 1e-9 && x <= c.X1 + 1e-9);
            if (col == null || col.Layers.Count == 0) return t.ZoneAt(x).IsRock ? "rock" : "clay";
            double depth = BedY(t) - y, top = 0;
            foreach (var l in col.Layers)
            {
                if (l.Thickness <= 0 || depth < top + l.Thickness) return l.Soil;
                top += l.Thickness;
            }
            return col.Layers[^1].Soil;
        }

        /// <summary>Effective weight of the ground between the bed and height y at x, Pa (Σ γ' × thickness).</summary>
        public static double Overburden(TunnelConfig cfg, TerrainConfig t, double x, double y)
        {
            double bed = BedY(t), depth = Math.Max(0, bed - y), sum = 0;
            const double step = 0.25;
            for (double d = 0; d < depth - 1e-9; d += step)
                sum += cfg.Geo(SoilAt(t, x, bed - d - Math.Min(step, depth - d) / 2)).Gamma * Math.Min(step, depth - d);
            return sum;
        }

        /// <summary>Risk band: 0 green, 1 yellow, 2 orange, 3 red (failure).</summary>
        public static int RiskBand(TunnelConfig cfg, double u) => u >= 1 ? 3 : u >= cfg.RiskOrange ? 2 : u >= cfg.RiskYellow ? 1 : 0;

        /// <summary>Road surface height at both banks (lane-0 road anchors).</summary>
        private static void BankRoad(LevelDefinition level, out double yl, out double yr)
        {
            var s = level.Anchors.FirstOrDefault(a => a.IsRoadStart && a.RoadLane == 0);
            var e = level.Anchors.FirstOrDefault(a => a.IsRoadEnd && a.RoadLane == 0);
            yl = s?.Position.Y ?? level.Terrain.LeftBankHeight;
            yr = e?.Position.Y ?? level.Terrain.RightBankHeight;
        }

        /// <summary>Portal x on the bank surface for a tube whose floor at the bank end is at floorY.</summary>
        public static double PortalX(LevelDefinition level, bool left, double floorY, double endX, double raise = 0)
        {
            double run = RampRun(level, left, floorY, raise);
            return left ? endX - run : endX + run;
        }

        /// <summary>Material panel text: the grades of this tunnel type with their limits.</summary>
        /// <summary>Property table of a tunnel material: header row, then one row per grade.</summary>
        public static List<string[]> MaterialTable(TunnelConfig cfg, MaterialDefinition m)
        {
            var t = cfg?.Type(m);
            var rows = new List<string[]>();
            if (t == null) return rows;
            bool sup = t.HasSupports;
            var head = new List<string> { Loc.T("等级"), Loc.T("价格 $/m"), Loc.T("承压 MPa"), Loc.T("自重 吨/m") };
            if (sup) { head.Add(Loc.T("接缝转角 rad")); head.Add(Loc.T("接缝抗弯 MN·m")); }
            rows.Add(head.ToArray());
            foreach (var g in t.Grades.Values)
            {
                var r = new List<string> { g.Name, g.CostPerMeter.ToString("0"), (g.PressureCapacity / 1e6).ToString("0.00"), (g.MassPerMeter / 1000).ToString("0.#") };
                if (sup) { r.Add(g.JointRotation.ToString("0.###")); r.Add((t.JointStiffness * g.JointRotation / 1e6).ToString("0.#")); }
                rows.Add(r.ToArray());
            }
            return rows;
        }

        /// <summary>Type-wide properties shown under the table (size, ramps, options).</summary>
        public static List<string> MaterialNotes(TunnelConfig cfg, MaterialDefinition m)
        {
            var t = cfg?.Type(m);
            var n = new List<string>();
            if (t == null) return n;
            n.Add(Loc.F("管身 {0:0.#} × {1:0.#} m，每节 {2:0} m；两端自动贴合岸壁，两岸自动配垂直电梯", (t.TubeWidth), (t.TubeHeight), (cfg.ElementLength)));
            n.Add(t.IsShield ? Loc.T("只会因为材料强度不够而损坏：管片承压（水压 + 土压）")
                : Loc.T("管身是结构里的连续梁，两端铰接在两岸电梯井，中间全靠你建的结构承担：管壁承压（水压）与接缝弯矩都要校核；支撑 / 锚索按普通构件校核，河床基础按浅基础 / 桩基础承载"));
            if (t.IsFloating)
            {
                n.Add(Loc.T("浮力等级：") + string.Join(" · ", t.Buoyancy.Values.Select(b => Loc.F("{0} ×{1:0.00}（+${2:0}/m）", (b.Name), (b.Ratio), (b.CostPerMeter)))));
                n.Add(Loc.T("锚索自己建：选钢索或复合钢索，从管底接缝圆点拉到河床（自动生成锚块，可选浅基础 / 桩基础）、岸上锚点或其他结构；锚索只受拉，长度、角度、截面都计入。浮力越高锚索拉力越大；浮力越低，车辆越容易把管身压沉（锚索松弛后只靠管身抗弯）"));
                n.Add(Loc.T("横向水流摆动是简化估算，只显示，不参与判定"));
                n.Add(Loc.F("水面以下至少 {0:0.#} m，离河床至少 {1:0.#} m；不能在上面建桥", (t.MinCover), (t.MinBedClearance)));
            }
            else if (t.IsShield)
            {
                n.Add(Loc.T("最小覆土 / 掘进费：") + string.Join(" · ", cfg.Geology.Values.Select(s => Loc.F("{0} {1:0} m ${2:0}/m", (s.Name), (s.MinCover), (s.ExcavationCost)))));
                n.Add(Loc.F("注浆加固 ${0:0}/m：所需覆土 ×{1:0.##}，土压 ×{2:0.##}；不扰动河床", (t.ReinforceCostPerMeter), (t.GroutCoverFactor), (t.GroutEarthFactor)));
            }
            else
            {
                n.Add(Loc.F("整节预制直管，不能弯曲：只能上下拖动两端；管底离河床 {0:0.#}~{1:0.#} m", (t.MinBedClearance), (t.MaxBedClearance)));
                n.Add(Loc.T("基础价最便宜，但升级为加强 / 重型管节比其他隧道贵得多"));
                n.Add(Loc.T("管底每道接缝有一个连接圆点：用桥墩工具在下面立桥墩，或接柱子 / 梁 / 钢索；支撑越密接缝弯矩越小，软土上浅基础承载低、要用桩基础"));
            }
            return n;
        }

        public static string MaterialSummary(TunnelConfig cfg, MaterialDefinition m)
        {
            var t = cfg?.Type(m);
            if (t == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var g in t.Grades.Values)
                sb.Append(t.IsShield ? Loc.F("{0}：${1:0}/m · 外压 {2:0} kPa\n", (g.Name), (g.CostPerMeter), (g.PressureCapacity / 1000))
                    : Loc.F("{0}：${1:0}/m · 水压 {2:0} kPa · 接缝抗弯 {3:0.#} MN·m · {4:0} 吨/m\n", (g.Name), (g.CostPerMeter), (g.PressureCapacity / 1000), (t.JointStiffness * g.JointRotation / 1e6), (g.MassPerMeter / 1000)));
            if (t.IsShield) sb.Append(Loc.F("注浆加固 ${0:0}/m（土压 ×{1:0.##}）", (t.ReinforceCostPerMeter), (t.GroutEarthFactor)));
            else sb.Append(Loc.T("管身靠你建的支撑 / 锚索承担：支撑越密接缝弯矩越小，河床基础的土质决定承载"));
            return sb.ToString();
        }

        /// <summary>Portal x for a standard tube on the flat bed (terrain decoration, before anything is built).</summary>
        public static double DefaultPortalX(LevelDefinition level, TunnelTypeConfig type, bool left)
        {
            FlatBed(level.Terrain, out double b0, out double b1);
            return PortalX(level, left, (type == null ? BedY(level.Terrain) : DefaultBottom(level.Terrain, type)) + (type?.FloorHeight ?? 0.8), left ? b0 : b1);
        }

        /// <summary>
        /// Vehicle route: bank road → left lift (platform on the bank behind the wall, down to the tube floor) →
        /// tube floor → right lift (up) → bank road. Each lift is one segment that runs backwards in x (from the
        /// platform's far end at one level to its near end at the other); RoadPath marks it as a lift segment and the
        /// simulation moves the whole vehicle vertically and then shifts it by (segment length + platform length).
        /// </summary>
        public static List<(double x, double y)> Route(LevelDefinition level, TunnelGeometry g)
        {
            var sh = Shafts(level, g);
            var l = sh[0]; var rt = sh[1];
            double f = g.Type.FloorHeight;
            var tube = SmoothGrades(g.Points.Select(p => (x: p.X, y: p.Y + f)).ToList());
            var r = new List<(double x, double y)> { (Math.Min(GroundStartX(level), l.X0 - 2), l.Top), (l.X1, l.Top), (l.X0, l.Bottom) };
            foreach (var p in tube) if (p.x > r[^1].x + 1e-6) r.Add(p);
            if (r[^1].x < rt.X0 - 1e-6) r.Add((rt.X0, rt.Bottom));
            r.Add((rt.X1, rt.Bottom));
            r.Add((rt.X0, rt.Top));
            r.Add((Math.Max(GroundEndX(level), rt.X1 + 2), rt.Top));
            return r;
        }

        /// <summary>One bank lift: platform from X0 to X1 (on the bank side of the wall), bank road Top, tube floor Bottom.</summary>
        public readonly struct LiftShaft
        {
            public readonly bool Left;
            public readonly double X0, X1, Top, Bottom;
            public LiftShaft(bool left, double x0, double x1, double top, double bottom) { Left = left; X0 = x0; X1 = x1; Top = top; Bottom = bottom; }
            public double Height => Top - Bottom;
            public double CenterX => (X0 + X1) / 2;
        }

        /// <summary>Both lifts: the tube ends sit against the bank walls, the lift shafts right behind them.</summary>
        public static LiftShaft[] Shafts(LevelDefinition level, TunnelGeometry g)
        {
            BankRoad(level, out double yl, out double yr);
            FlatBed(level.Terrain, out double b0, out double b1);
            double f = g.Type.FloorHeight, L = g.LiftLength;
            return new[]
            {
                new LiftShaft(true, b0 - L, b0, yl, g.Points[0].Y + f),
                new LiftShaft(false, b1, b1 + L, yr, g.Points[^1].Y + f),
            };
        }

        public static LiftConfig LiftCfg => _db?.TunnelConfig?.Lift ?? new LiftConfig();
        /// <summary>Shaft end walls stand this far outside the platform ends (wall centre, m); see BridgeRenderer.AddEnclosure.</summary>
        public const double LiftEndWallOffset = 0.45, LiftEndWallThickness = 0.25;
        /// <summary>A vehicle waiting for an absent platform stops with its body front this far before the platform:
        /// clear of the landing door and the shaft end wall (wall face at 0.575 m), so it never pokes into the shaft.</summary>
        public const double LiftWaitClearance = 1.2;

        /// <summary>Price of one lift: shaft per metre of height, platform per metre of length.</summary>
        public static double LiftCost(double height, double length) =>
            Math.Max(0, height) * LiftCfg.ShaftCostPerMeter + length * LiftCfg.PlatformCostPerMeter;

        /// <summary>Old (pre-structural) platform capacity, N: used only to migrate older saves into a lift structure.</summary>
        public static double LiftCapacity(double length) => LiftCfg.CapacityBase + LiftCfg.CapacityPerMeter * length;

        /// <summary>Longest vehicle body that fits a platform of this length.</summary>
        public static double LiftFits(double length) => length - 2 * LiftCfg.Margin;

        /// <summary>Lift centres (left, right) of the current tube.</summary>
        public static (double left, double right) Portals(LevelDefinition level, TunnelGeometry g)
        {
            var s = Shafts(level, g);
            return (s[0].CenterX, s[1].CenterX);
        }

        /// <summary>Replace every grade change by a parabolic vertical curve so wheels never jump at a kink.</summary>
        public static List<(double x, double y)> SmoothGrades(List<(double x, double y)> pts)
        {
            if (pts.Count < 3) return pts;
            var r = new List<(double x, double y)> { pts[0] };
            for (int i = 1; i + 1 < pts.Count; i++)
            {
                var (x0, y0) = pts[i - 1]; var (x1, y1) = pts[i]; var (x2, y2) = pts[i + 1];
                double d1 = x1 - x0, d2 = x2 - x1;
                if (d1 < 1e-6 || d2 < 1e-6) { r.Add(pts[i]); continue; }
                double g1 = (y1 - y0) / d1, g2 = (y2 - y1) / d2;
                double len = Math.Min(VerticalCurve, 0.45 * Math.Min(d1, d2));
                if (Math.Abs(g2 - g1) < 1e-5 || len < 0.2) { r.Add(pts[i]); continue; }
                double ax = x1 - len / 2, ay = y1 - g1 * len / 2;
                const int steps = 8;
                for (int k = 0; k <= steps; k++)
                {
                    double u = len * k / steps;
                    r.Add((ax + u, ay + g1 * u + (g2 - g1) / (2 * len) * u * u));
                }
            }
            r.Add(pts[^1]);
            return r;
        }

        /// <summary>Steepest grade of the tube itself (ramps are built at the allowed slope).</summary>
        public static double MaxGrade(TunnelGeometry g)
        {
            double m = 0;
            for (int k = 0; k + 1 < g.Points.Count; k++)
            {
                double dx = g.Points[k + 1].X - g.Points[k].X;
                if (dx > 1e-6) m = Math.Max(m, Math.Abs(g.Points[k + 1].Y - g.Points[k].Y) / dx);
            }
            return m;
        }

        /// <summary>
        /// Migrate version-4 tunnel sections (one material per section, 5 m tube on the bed) into one route with
        /// graded elements. The old support nodes become ordinary nodes; the editor regenerates joint supports.
        /// </summary>
        public static void MigrateLegacy(BridgeDesign d)
        {
            if (d.Tunnels == null || d.Tunnels.Count == 0) { d.Tunnels = null; return; }
            var segs = d.Tunnels.OrderBy(s => s.X0).ToList();
            d.Tunnels = null;
            var oldSupports = d.Nodes.Where(n => n.TunnelLegacyId != 0).ToList();
            foreach (var n in oldSupports) n.TunnelLegacyId = 0;
            if (d.Tunnel != null) return;
            double bed = oldSupports.Count > 0 ? oldSupports[0].Position.Y - 5 : double.NaN;
            var route = new TunnelRoute { MaterialId = "tunnel_immersed" };
            double x0 = segs[0].X0, x1 = segs.Max(s => s.X1);
            foreach (double x in new[] { x0, x1 })
            {
                var n = new BridgeNode { Id = d.NextNodeId++, Position = new Vec3d(x, double.IsNaN(bed) ? 0 : bed), TunnelPoint = true };
                d.Nodes.Add(n); route.PointNodeIds.Add(n.Id);
            }
            string GradeOf(string mat) => mat == "tunnel_steel" ? "heavy" : mat == "tunnel_rc" ? "reinforced" : "normal";
            for (double x = x0; x < x1 - 1e-6; x += 10)
            {
                var s = segs.FirstOrDefault(q => x + 5 >= q.X0 && x + 5 <= q.X1) ?? segs[0];
                route.Elements.Add(new TunnelElement { Grade = GradeOf(s.MaterialId) });
            }
            d.Tunnel = route;
        }
    }
}
