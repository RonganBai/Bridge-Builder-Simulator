using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BridgeArchitect3D.Data
{
    /// <summary>A fixed node placed by the level designer (bank anchor, or a rock pillar top inside the river).</summary>
    public class CustomAnchorSpec
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// Player-made level, stored as this editable spec (user://custom_levels). Heights are absolute metres; the
    /// water level and river bed are given as depths below the lower bank so the river always fits.
    /// </summary>
    public class CustomLevelSpec
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; }
        public string Name { get; set; } = "我的关卡";
        public string Description { get; set; } = "";
        public double RiverWidth { get; set; } = 16;
        public double LeftBankHeight { get; set; }
        public double RightBankHeight { get; set; }
        /// <summary>Water surface below the lower bank top, m.</summary>
        public double WaterDepth { get; set; } = 4;
        /// <summary>River bed below the lower bank top, m (deeper than the water surface).</summary>
        public double BedDepth { get; set; } = 7;
        public double Budget { get; set; } = 30000;
        /// <summary>No piers in the river (navigation channel / protected zone).</summary>
        public bool NoPiers { get; set; }
        public bool Ship { get; set; }
        public double ShipHeight { get; set; } = 5;
        public string ShipKind { get; set; } = "cargo";
        public List<string> Materials { get; set; } = new() { "road", "wood", "steel" };
        public List<string> Vehicles { get; set; } = new() { "car" };
        public List<CustomAnchorSpec> Anchors { get; set; } = new();
        /// <summary>Bank wall angle, degrees from horizontal (90 = vertical; old specs load as 90).</summary>
        public double BankSlope { get; set; } = 90;
        /// <summary>River bed material: "rock" or "soft".</summary>
        public string BedSoil { get; set; } = "rock";
        /// <summary>False = dry gully without water (ships are not allowed then).</summary>
        public bool HasWater { get; set; } = true;

        /// <summary>Wall run per metre of drop, same formula as <see cref="TerrainConfig.WallRun"/>.</summary>
        public double WallRun => new TerrainConfig { BankSlopeDeg = BankSlope }.WallRun;

        /// <summary>Snaps a point onto a bank: top surface (onWall false) or wall face at height y (onWall true).</summary>
        public CustomAnchorSpec BankPoint(bool left, bool onWall, double along)
        {
            double h = left ? LeftBankHeight : RightBankHeight;
            if (!onWall) return new CustomAnchorSpec { X = left ? -along : RiverWidth + along, Y = h };
            double y = h - along, run = WallRun * along;
            return new CustomAnchorSpec { X = left ? run : RiverWidth - run, Y = y };
        }

        public CustomLevelSpec Clone()
        {
            var c = (CustomLevelSpec)MemberwiseClone();
            c.Materials = new List<string>(Materials); c.Vehicles = new List<string>(Vehicles);
            c.Anchors = Anchors.Select(a => new CustomAnchorSpec { X = a.X, Y = a.Y }).ToList();
            return c;
        }
    }

    /// <summary>Turns a <see cref="CustomLevelSpec"/> into a playable <see cref="LevelDefinition"/> and checks it.</summary>
    public static class CustomLevelBuilder
    {
        public const string IdPrefix = "custom_";
        public const double PillarWidth = 1.5;

        /// <summary>File / id safe name: letters, digits and CJK kept, everything else becomes '_'.</summary>
        public static string SafeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in (name ?? "").Trim())
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_');
            string s = sb.ToString().Trim('_');
            if (s.Length > 40) s = s.Substring(0, 40);
            return s.Length == 0 ? "unnamed" : s;
        }

        public static string IdFor(string name) => IdPrefix + SafeName(name);
        public static bool IsCustom(string levelId) => levelId != null && levelId.StartsWith(IdPrefix);

        /// <summary>Problems that stop the level from being saved or played (empty = fine).</summary>
        public static List<string> Validate(CustomLevelSpec s, GameDatabase db)
        {
            var e = new List<string>();
            if (string.IsNullOrWhiteSpace(s.Name)) e.Add("请给关卡起个名字");
            if (s.RiverWidth < 6 || s.RiverWidth > 80) e.Add("河宽应在 6~80 m 之间");
            if (Math.Abs(s.LeftBankHeight) > 20 || Math.Abs(s.RightBankHeight) > 20) e.Add("两岸高度应在 −20~20 m 之间");
            if (s.WaterDepth < 0.5) e.Add("水面至少比低岸低 0.5 m");
            if (s.BedDepth < s.WaterDepth + 1) e.Add("河床至少比水面低 1 m");
            if (s.Budget <= 0) e.Add("预算必须大于 0");
            var mats = s.Materials.Select(db.GetMaterial).ToList();
            if (mats.Count == 0 || mats.Any(m => m == null)) e.Add("请选择可用建材");
            else if (!mats.Any(m => m.IsDeck)) e.Add("至少选一种桥面，车辆才有路可走");
            else
            {
                var vdefs = s.Vehicles.Where(db.Vehicles.ContainsKey).Select(v => db.Vehicles[v]).ToList();
                if (vdefs.Any(RailTrack.IsTrain) && !mats.Any(m => m.CarriesTrains)) e.Add("有火车：至少选一种铁轨桥面（铁轨桥面或铁轨公路复合桥面）");
                if (vdefs.Any(v => !RailTrack.IsTrain(v)) && !mats.Any(m => m.CarriesCars)) e.Add("有汽车：至少选一种公路桥面（公路桥面或铁轨公路复合桥面）");
            }
            if (s.Vehicles.Count == 0 || s.Vehicles.Any(v => !db.Vehicles.ContainsKey(v))) e.Add("请至少选择一种通行车辆");
            if (s.Ship && (s.ShipHeight < 1 || s.ShipHeight > 20)) e.Add("船高应在 1~20 m 之间");
            if (s.Ship && !s.HasWater) e.Add("无水河道不能有船只通过");
            if (s.BankSlope < 30 || s.BankSlope > 90) e.Add("岸壁角度应在 30°~90° 之间");
            if (s.BedSoil != "rock" && s.BedSoil != "soft") e.Add("请选择河床材质");
            {
                double run = s.WallRun, low0 = Math.Min(s.LeftBankHeight, s.RightBankHeight);
                double toes = run * (s.LeftBankHeight - (low0 - s.BedDepth)) + run * (s.RightBankHeight - (low0 - s.BedDepth));
                if (toes > s.RiverWidth - 2) e.Add($"岸壁太缓：两侧坡脚共伸入河道 {toes:0.#} m，河底至少要留 2 m，请加大角度或河宽");
            }
            double low = Math.Min(s.LeftBankHeight, s.RightBankHeight), bed = low - s.BedDepth, top = Math.Max(s.LeftBankHeight, s.RightBankHeight) + 12;
            for (int i = 0; i < s.Anchors.Count; i++)
            {
                var a = s.Anchors[i];
                if (a.X < -8 || a.X > s.RiverWidth + 8) e.Add($"固定节点 {i + 1} 超出可建范围（x 应在 −8~{s.RiverWidth + 8:0} m）");
                else if (a.Y < bed || a.Y > top) e.Add($"固定节点 {i + 1} 的高度应在河床 {bed:0.#} m 与 {top:0.#} m 之间");
                else if (a.X <= 0 && a.Y > s.LeftBankHeight + 1e-6) e.Add($"固定节点 {i + 1} 悬在左岸上空，应放在岸面或岸坡上");
                else if (a.X >= s.RiverWidth && a.Y > s.RightBankHeight + 1e-6) e.Add($"固定节点 {i + 1} 悬在右岸上空，应放在岸面或岸坡上");
                else if (s.NoPiers && a.X > 0 && a.X < s.RiverWidth && a.Y < low && !OnWall(s, a)) e.Add($"固定节点 {i + 1} 在禁止建墩的河道里");
                else if (a.X > 0 && a.X < s.RiverWidth && a.Y < GroundY(s, a.X) - 0.05) e.Add($"固定节点 {i + 1} 埋在岸坡土体里，应放在坡面上");
            }
            return e;
        }

        private static double GroundY(CustomLevelSpec s, double x)
        {
            double low = Math.Min(s.LeftBankHeight, s.RightBankHeight);
            return new TerrainConfig { RiverWidth = s.RiverWidth, LeftBankHeight = s.LeftBankHeight, RightBankHeight = s.RightBankHeight, RiverBedDepth = low - s.BedDepth, BankSlopeDeg = s.BankSlope }.GroundYInRiver(x);
        }

        /// <summary>A river-span anchor sitting on a sloped bank wall needs no rock pillar.</summary>
        private static bool OnWall(CustomLevelSpec s, CustomAnchorSpec a) =>
            s.WallRun > 0 && Math.Abs(a.Y - GroundY(s, a.X)) <= 0.05 && a.Y > Math.Min(s.LeftBankHeight, s.RightBankHeight) - s.BedDepth + 0.05;

        public static LevelDefinition Build(CustomLevelSpec s, GameDatabase db)
        {
            double w = s.RiverWidth, lh = s.LeftBankHeight, rh = s.RightBankHeight, low = Math.Min(lh, rh);
            var lv = new LevelDefinition
            {
                Id = string.IsNullOrEmpty(s.Id) ? IdFor(s.Name) : s.Id,
                Name = s.Name,
                Difficulty = "自定义",
                Description = string.IsNullOrWhiteSpace(s.Description) ? "玩家自定义关卡" : s.Description,
                Budget = s.Budget,
                Terrain = new TerrainConfig
                {
                    RiverWidth = w, LeftBankHeight = lh, RightBankHeight = rh,
                    WaterLevel = s.HasWater ? low - s.WaterDepth : low - s.BedDepth, RiverBedDepth = low - s.BedDepth,
                    BankSlopeDeg = s.BankSlope, HasWater = s.HasWater, DefaultSoil = s.BedSoil == "soft" ? "soft" : "rock",
                    BuildMinY = low - s.BedDepth, BuildMaxY = Math.Max(lh, rh) + 12, BuildMarginX = 10,
                },
                AllowedMaterials = s.Materials.Where(id => db.GetMaterial(id) != null).Distinct().ToList(),
            };
            void Anchor(string id, double x, double y, bool start = false, bool end = false) =>
                lv.Anchors.Add(new AnchorDefinition { Id = id, Position = new Vec3d(x, y, 0), SupportType = SupportType.Pinned, IsRoadStart = start, IsRoadEnd = end });
            Anchor("L1", 0, lh, start: true); Anchor("R1", w, rh, end: true);
            double wallRun2 = lv.Terrain.WallRun * 2;
            Anchor("L2", wallRun2, lh - 2); Anchor("R2", w - wallRun2, rh - 2);
            lv.Terrain.FoundationZones.Add(new FoundationZone { X0 = 0, X1 = w, Soil = lv.Terrain.DefaultSoil });
            for (int i = 0; i < s.Anchors.Count; i++)
            {
                var a = s.Anchors[i];
                Anchor($"C{i + 1}", a.X, a.Y);
                // A fixed node inside the river stands on a rock pillar so it really is supported ground
                if (a.X > 0 && a.X < w && !OnWall(s, a)) lv.Terrain.Pillars.Add(new PillarConfig { X = a.X, TopY = a.Y, Width = PillarWidth });
            }
            if (s.NoPiers) lv.Terrain.NoBuildZones.Add(new NoBuildZone { X0 = 0, X1 = w, BelowY = low, Name = "禁止建墩区" });
            for (int i = 0; i < s.Vehicles.Count; i++)
                if (db.Vehicles.ContainsKey(s.Vehicles[i])) lv.TrafficEvents.Add(new TrafficEvent { Time = i * 2, VehicleId = s.Vehicles[i] });
            if (s.Ship && s.HasWater)
                {
                    var ship=ShipCatalog.Create(s.ShipKind,w/2,6);
                    ship.Height=s.ShipHeight;ship.Width=Math.Min(ship.Width,w*0.5);
                    if(ship.ShipKind=="cargo"){ship.Width=Math.Min(w*0.5,8);ship.Speed=4;}
                    lv.TrafficEvents.Add(ship);
                }
            return lv;
        }
    }
}
