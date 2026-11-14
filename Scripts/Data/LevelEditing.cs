using BridgeArchitect3D.Physics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// One set of editing operations on a <see cref="LevelDefinition"/>, shared by the main-menu level designer and
    /// the in-game level settings (they show the same form). Terrain edits keep the level consistent: bank-top
    /// anchors follow the bank height, right-bank anchors follow the river width, fixed nodes inside the river keep
    /// their rock pillar, the no-pier zone and the river-bed soil cover the whole river.
    /// </summary>
    public static class LevelEditing
    {
        private const double Eps = 1e-6, OnGround = 0.05;
        public const double MinRiverWidth = 6, MaxRiverWidth = 300;

        /// <summary>Deep copy (the editors always work on a copy until saved).</summary>
        public static LevelDefinition Copy(LevelDefinition l) => GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(l));

        // ---------------- river and banks ----------------

        public static void SetRiverWidth(LevelDefinition l, double w)
        {
            var t = l.Terrain; double old = t.RiverWidth, d = w - old;
            if (Math.Abs(d) < Eps) return;
            foreach (var a in l.Anchors)
                if (a.Position.X >= old - Eps) a.Position = new Vec3d(a.Position.X + d, a.Position.Y, a.Position.Z);
                else if (a.Position.X > Eps) a.Position = new Vec3d(a.Position.X * w / old, a.Position.Y, a.Position.Z);
            foreach (var p in t.Pillars) p.X = p.X * w / old;
            foreach (var z in t.NoBuildZones) { z.X0 = Scale(z.X0, old, w); z.X1 = Scale(z.X1, old, w); }
            foreach (var z in t.FoundationZones) { z.X0 = Scale(z.X0, old, w); z.X1 = Scale(z.X1, old, w); }
            foreach (var c in t.Strata) { c.X0 = Scale(c.X0, old, w); c.X1 = Scale(c.X1, old, w); }
            foreach (var e in l.TrafficEvents) if (e.Type == TrafficEventType.Ship) e.LaneX = Scale(e.LaneX, old, w);
            t.RiverWidth = w;
        }

        /// <summary>Positions inside the river scale with it; positions on the right bank shift with its edge.</summary>
        private static double Scale(double x, double old, double w) =>
            x >= old - Eps ? x + (w - old) : x <= Eps ? x : x * w / old;

        public static void SetBankHeight(LevelDefinition l, bool left, double h)
        {
            var t = l.Terrain; double old = left ? t.LeftBankHeight : t.RightBankHeight, d = h - old;
            if (Math.Abs(d) < Eps) return;
            foreach (var a in l.Anchors)
            {
                bool onBank = left ? a.Position.X <= Eps : a.Position.X >= t.RiverWidth - Eps;
                // Anchors on (or into) this bank move with it; raised bridge-deck anchors keep their height above it
                if (onBank && a.Position.Y >= old - 3 - Eps) a.Position = new Vec3d(a.Position.X, a.Position.Y + d, a.Position.Z);
            }
            if (left) t.LeftBankHeight = h; else t.RightBankHeight = h;
            t.BuildMaxY = Math.Max(t.BuildMaxY, Math.Max(t.LeftBankHeight, t.RightBankHeight) + 12);
        }

        public static void SetBedLevel(LevelDefinition l, double y)
        {
            var t = l.Terrain; double d = y - t.RiverBedDepth;
            t.RiverBedDepth = y;
            if (!t.HasWater) t.WaterLevel = y;
            if (t.BuildMinY > y) t.BuildMinY = y;


        }

        // ---- Depth-based terrain (what the editor shows): everything relative to the lower bank top ----
        public static double LowBank(LevelDefinition l) => Math.Min(l.Terrain.LeftBankHeight, l.Terrain.RightBankHeight);
        /// <summary>River depth: lower bank top down to the river bed.</summary>
        public static double RiverDepth(LevelDefinition l) => LowBank(l) - l.Terrain.RiverBedDepth;
        /// <summary>Water depth: river bed up to the normal water level (0 = dry).</summary>
        public static double WaterDepth(LevelDefinition l) => l.Terrain.HasWater ? l.Terrain.WaterLevel - l.Terrain.RiverBedDepth : 0;
        /// <summary>Changes the river depth, keeping the water depth where it still fits (≥ 0.5 m freeboard).</summary>
        public static void SetRiverDepth(LevelDefinition l, double depth)
        {
            double wd = WaterDepth(l);
            depth = Math.Max(1, depth);
            SetBedLevel(l, LowBank(l) - depth);
            if (l.Terrain.HasWater) SetWaterDepth(l, wd);
        }
        public static void SetWaterDepth(LevelDefinition l, double wd)
        {
            var t = l.Terrain; if (!t.HasWater) return;
            double max = Math.Max(0.5, RiverDepth(l) - 0.5);
            t.WaterLevel = t.RiverBedDepth + Math.Clamp(wd, 0.5, max);
        }
        /// <summary>Deepest build level, as metres below the river bed.</summary>
        public static double BuildDepth(LevelDefinition l) => l.Terrain.RiverBedDepth - l.Terrain.BuildMinY;
        public static void SetBuildDepth(LevelDefinition l, double d) => l.Terrain.BuildMinY = l.Terrain.RiverBedDepth - Math.Max(0, d);
        /// <summary>Highest build level, as metres above the higher bank top.</summary>
        public static double BuildHeight(LevelDefinition l) => l.Terrain.BuildMaxY - Math.Max(l.Terrain.LeftBankHeight, l.Terrain.RightBankHeight);
        public static void SetBuildHeight(LevelDefinition l, double h) => l.Terrain.BuildMaxY = Math.Max(l.Terrain.LeftBankHeight, l.Terrain.RightBankHeight) + Math.Max(5, h);

        public static void SetHasWater(LevelDefinition l, bool on)
        {
            var t = l.Terrain; if (t.HasWater == on) return;
            t.HasWater = on;
            if (!on) { t.WaterLevel = t.RiverBedDepth; l.TrafficEvents.RemoveAll(e => e.Type == TrafficEventType.Ship); }
            else if (t.WaterLevel <= t.RiverBedDepth + Eps) t.WaterLevel = Math.Min(t.LeftBankHeight, t.RightBankHeight) - Math.Min(4, Math.Max(0.5, (Math.Min(t.LeftBankHeight, t.RightBankHeight) - t.RiverBedDepth) / 2));
        }

        /// <summary>River-bed soil for the whole river: every foundation zone takes it (scour marks are kept).</summary>
        public static void SetSoil(LevelDefinition l, string soil)
        {
            var t = l.Terrain; t.DefaultSoil = soil;
            if (t.FoundationZones.Count == 0) t.FoundationZones.Add(new FoundationZone { X0 = 0, X1 = t.RiverWidth, Soil = soil });
            else foreach (var z in t.FoundationZones) z.Soil = soil;
        }

        /// <summary>Mixed soils (built-in levels with several zones) are shown as such instead of one choice.</summary>
        public static bool MixedSoil(LevelDefinition l) => l.Terrain.FoundationZones.Select(z => z.Soil).Distinct().Count() > 1;

        private static NoBuildZone RiverZone(LevelDefinition l) =>
            l.Terrain.NoBuildZones.FirstOrDefault(z => z.X0 <= Eps && z.X1 >= l.Terrain.RiverWidth - Eps);

        public static bool NoPiers(LevelDefinition l) => RiverZone(l) != null;

        public static void SetNoPiers(LevelDefinition l, bool on)
        {
            var z = RiverZone(l);
            if (on && z == null) l.Terrain.NoBuildZones.Add(new NoBuildZone { X0 = 0, X1 = l.Terrain.RiverWidth, BelowY = Math.Min(l.Terrain.LeftBankHeight, l.Terrain.RightBankHeight), Name = Loc.T("禁止建墩区") });
            else if (!on && z != null) l.Terrain.NoBuildZones.Remove(z);
        }

        // ---------------- fixed nodes ----------------

        /// <summary>Road start / end anchors define the route and are not removable.</summary>
        public static bool IsRoadAnchor(AnchorDefinition a) => a.IsRoadStart || a.IsRoadEnd;

        /// <summary>Snaps a point onto a bank: its top surface, or the wall face `along` metres below the top.</summary>
        public static Vec3d BankPoint(LevelDefinition l, bool left, bool onWall, double along)
        {
            var t = l.Terrain; double h = left ? t.LeftBankHeight : t.RightBankHeight;
            if (!onWall) return new Vec3d(left ? -along : t.RiverWidth + along, h, 0);
            double y = Math.Max(t.RiverBedDepth, h - along), run = t.WallRun * (h - y);
            return new Vec3d(left ? run : t.RiverWidth - run, y, 0);
        }

        private static bool InRiver(LevelDefinition l, double x) => x > Eps && x < l.Terrain.RiverWidth - Eps;
        private static bool OnWallOrBed(LevelDefinition l, Vec3d p) => Math.Abs(p.Y - l.Terrain.GroundYInRiver(p.X)) <= OnGround;

        /// <summary>A fixed node in the river (not on a bank wall or the bed) stands on its own rock pillar.</summary>
        public static bool NeedsPillar(LevelDefinition l, Vec3d p) => InRiver(l, p.X) && !OnWallOrBed(l, p) && p.Y > l.Terrain.RiverBedDepth;

        private static PillarConfig PillarAt(LevelDefinition l, Vec3d p) =>
            l.Terrain.Pillars.FirstOrDefault(c => Math.Abs(c.X - p.X) < 1e-3 && Math.Abs(c.TopY - p.Y) < 1e-3);

        public static string NextAnchorId(LevelDefinition l)
        {
            for (int i = 1; ; i++) if (l.Anchors.All(a => a.Id != $"C{i}")) return $"C{i}";
        }

        public static AnchorDefinition AddAnchor(LevelDefinition l, Vec3d p)
        {
            var a = new AnchorDefinition { Id = NextAnchorId(l), Position = p, SupportType = SupportType.Pinned };
            l.Anchors.Add(a);
            if (NeedsPillar(l, p)) l.Terrain.Pillars.Add(new PillarConfig { X = p.X, TopY = p.Y, Width = CustomLevelBuilder.PillarWidth });
            return a;
        }

        public static void MoveAnchor(LevelDefinition l, AnchorDefinition a, Vec3d p)
        {
            var pillar = PillarAt(l, a.Position);
            a.Position = p;
            bool need = NeedsPillar(l, p);
            if (pillar != null && need) { pillar.X = p.X; pillar.TopY = p.Y; }
            else if (pillar != null) l.Terrain.Pillars.Remove(pillar);
            else if (need) l.Terrain.Pillars.Add(new PillarConfig { X = p.X, TopY = p.Y, Width = CustomLevelBuilder.PillarWidth });
        }

        public static bool RemoveAnchor(LevelDefinition l, AnchorDefinition a)
        {
            if (IsRoadAnchor(a)) return false;
            if (PillarAt(l, a.Position) is { } pillar) l.Terrain.Pillars.Remove(pillar);
            return l.Anchors.Remove(a);
        }

        // ---------------- traffic ----------------

        public static TrafficEvent AddVehicle(LevelDefinition l, VehicleDefinition v) => Add(l, new TrafficEvent
        { Time = NextTime(l, 2), Type = RailTrack.IsTrain(v) ? TrafficEventType.Train : TrafficEventType.Vehicle, VehicleId = v.Id });

        public static TrafficEvent AddShip(LevelDefinition l, string kind)
        {
            var s = ShipCatalog.Create(kind, l.Terrain.RiverWidth / 2, NextTime(l, 1));
            s.Width = Math.Min(s.Width, l.Terrain.RiverWidth * 0.5);
            SetShipDraft(s, ShipCatalog.DefaultDraft(s));
            return Add(l, s);
        }

        public static TrafficEvent AddWeather(LevelDefinition l, string preset) => Add(l, new TrafficEvent
        { Time = NextTime(l, 1), Type = TrafficEventType.Environment, Name = Loc.T("暴雨"), Weather = preset, Duration = 10 });

        /// <summary>Changes a ship's type and takes that type's size and speed (the event keeps its time and lane).</summary>
        public static void SetShipKind(TrafficEvent e, string kind, double riverWidth)
        {
            var s = ShipCatalog.Create(kind, e.LaneX, e.Time);
            e.ShipKind = s.ShipKind; e.Name = s.Name; e.Width = Math.Min(s.Width, riverWidth * 0.5); e.Height = s.Height; e.Length = s.Length; e.Speed = s.Speed; SetShipDraft(e, ShipCatalog.DefaultDraft(e));
        }

        // ---- Ship position: anywhere on the water, draft 10%~50% of the ship's height ----
        public const double MinDraftRatio = 0.1, MaxDraftRatio = 0.5, ShipWallGap = 0.2;
        public static double MinDraft(TrafficEvent e) => e.Height * MinDraftRatio;
        public static double MaxDraft(TrafficEvent e) => e.Height * MaxDraftRatio;
        public static void SetShipDraft(TrafficEvent e, double d) => e.Draft = Math.Clamp(d, MinDraft(e), MaxDraft(e));

        /// <summary>Lane centre range where the hull (width, draft) floats clear of the bank walls; null = no room.</summary>
        public static (double Min, double Max)? ShipLaneRange(LevelDefinition l, TrafficEvent e)
        {
            var t = l.Terrain; double keel = t.WaterLevel - ShipCatalog.Draft(e), run = t.WallRun;
            if (!t.HasWater || keel < t.RiverBedDepth - Eps) return null;
            double lo = run * Math.Max(0, t.LeftBankHeight - keel) + e.Width / 2 + ShipWallGap, hi = t.RiverWidth - run * Math.Max(0, t.RightBankHeight - keel) - e.Width / 2 - ShipWallGap;
            return hi < lo - Eps ? null : (lo, Math.Max(lo, hi));
        }

        /// <summary>Moves a ship's lane, kept on the water (the closest valid spot).</summary>
        public static void SetShipLane(LevelDefinition l, TrafficEvent e, double x)
        {
            var r = ShipLaneRange(l, e);
            e.LaneX = r is { } v ? Math.Clamp(x, v.Min, v.Max) : l.Terrain.RiverWidth / 2;
        }

        /// <summary>Bank wall angle, 30°~90°.</summary>
        public static void SetBankSlope(LevelDefinition l, double deg) => l.Terrain.BankSlopeDeg = Math.Clamp(deg, 30, 90);

        private static TrafficEvent Add(LevelDefinition l, TrafficEvent e) { l.TrafficEvents.Add(e); return e; }
        private static double NextTime(LevelDefinition l, double step) =>
            l.TrafficEvents.Count == 0 ? 0 : l.TrafficEvents.Max(x => x.Time) + step;

        // ---------------- checks ----------------

        /// <summary>
        /// Everything that stops a level from being saved or played (empty = fine): the data errors of
        /// <see cref="GameDatabase.LevelError"/> plus the designer's terrain, material, traffic and fixed-node checks.
        /// </summary>
        public static List<string> Validate(LevelDefinition l, GameDatabase db)
        {
            var e = new List<string>();
            if (db.LevelError(l) is { } err) { e.Add(err); return e; }
            if(l.ProvidedInfrastructure!=null && l.ProvidedTerrainSignature!=null && l.ProvidedTerrainSignature!=CustomLiftLevels.Fingerprint(l))e.Add("地形或固定节点已变更，请在基本页点击‘生成 / 更新固定通道’后保存。");
            if(l.ProvidedInfrastructure!=null && l.TrafficEvents.Any(v=>v.VehicleId!=null && db.Vehicles.TryGetValue(v.VehicleId,out var def)&&RailTrack.IsTrain(def)) && !l.ProvidedInfrastructure.Members.Any(m=>db.GetMaterial(m.MaterialId)?.CarriesTrains==true))e.Add("当前电梯关未提供铁路通道，请移除火车或使用带固定铁路的关卡模板。");
            var t = l.Terrain;
            if (string.IsNullOrWhiteSpace(l.Name)) e.Add(Loc.T("请给关卡起个名字"));
            if (t.RiverWidth < MinRiverWidth || t.RiverWidth > MaxRiverWidth) e.Add(Loc.F("河宽应在 {0:0}~{1:0} m 之间", (MinRiverWidth), (MaxRiverWidth)));
            double low = Math.Min(t.LeftBankHeight, t.RightBankHeight);
            if (t.RiverBedDepth > low - 1) e.Add(Loc.T("河床至少比低岸低 1 m"));
            if (t.HasWater && t.WaterLevel > low - 0.5 + Eps) e.Add(Loc.T("水面至少比低岸低 0.5 m"));
            if (t.HasWater && t.WaterLevel < t.RiverBedDepth + 1 - Eps) e.Add(Loc.T("河床至少比水面低 1 m"));
            if (t.BankSlopeDeg < 30 || t.BankSlopeDeg > 90) e.Add(Loc.T("岸壁角度应在 30°~90° 之间"));
            else
            {
                double toes = t.WallRun * ((t.LeftBankHeight - t.RiverBedDepth) + (t.RightBankHeight - t.RiverBedDepth));
                if (toes > t.RiverWidth - 2) e.Add(Loc.F("岸壁太缓：两侧坡脚共伸入河道 {0:0.#} m，河底至少要留 2 m，请加大角度或河宽", (toes)));
            }
            if (l.Budget <= 0) e.Add(Loc.T("预算必须大于 0"));
            var mats = l.AllowedMaterials.Select(db.GetMaterial).Where(m => m != null).ToList();
            var vehicles = l.TrafficEvents.Where(x => x.Type is TrafficEventType.Vehicle or TrafficEventType.Train).Select(x => db.Vehicles[x.VehicleId]).ToList();
            if (mats.Count == 0) e.Add(Loc.T("请选择可用建材"));
            else if (!mats.Any(m => m.IsDeck || m.IsTunnel)) e.Add(Loc.T("至少选一种桥面，车辆才有路可走"));
            else
            {
                if (vehicles.Any(RailTrack.IsTrain) && !mats.Any(m => m.CarriesTrains)) e.Add(Loc.T("有火车：至少选一种铁轨桥面（铁轨桥面或铁轨公路复合桥面）"));
                if (vehicles.Any(v => !RailTrack.IsTrain(v)) && !mats.Any(m => m.CarriesCars || m.IsTunnel)) e.Add(Loc.T("有汽车：至少选一种公路桥面（公路桥面或铁轨公路复合桥面）"));
            }
            if (vehicles.Count == 0) e.Add(Loc.T("请至少选择一种通行车辆"));
            if (!t.HasWater && l.TrafficEvents.Any(x => x.Type == TrafficEventType.Ship)) e.Add(Loc.T("无水河道不能有船只通过"));
            if (t.HasWater)
                foreach (var s in l.TrafficEvents.Where(x => x.Type == TrafficEventType.Ship))
                {
                    double d = ShipCatalog.Draft(s);
                    if (s.Draft.HasValue && (d < MinDraft(s) - Eps || d > MaxDraft(s) + Eps)) e.Add(Loc.F("{0}：吃水应在船高的 10%~50% 之间", (s.Name)));
                    var r = ShipLaneRange(l, s);
                    if (r == null) e.Add(Loc.F("{0}：水深或水面宽度不够，船浮不起来", (s.Name)));
                    else if (s.LaneX < r.Value.Min - 0.05 || s.LaneX > r.Value.Max + 0.05) e.Add(Loc.F("{0}：船身碰到岸壁，请把船移到水面上", (s.Name)));
                }
            double top = Math.Max(t.LeftBankHeight, t.RightBankHeight) + 12;
            foreach (var a in l.Anchors.Where(a => !IsRoadAnchor(a) && a.RoadLane == 0))
            {
                var p = a.Position;
                if (p.X < -t.BuildMarginX - 8 || p.X > t.RiverWidth + t.BuildMarginX + 8) e.Add(Loc.F("固定节点 {0} 超出可建范围", (a.Id)));
                else if (p.Y < t.RiverBedDepth - Eps || p.Y > Math.Max(top, t.BuildMaxY)) e.Add(Loc.F("固定节点 {0} 的高度应在河床 {1:0.#} m 与 {2:0.#} m 之间", (a.Id), (t.RiverBedDepth), (Math.Max(top, t.BuildMaxY))));
                else if (InRiver(l, p.X) && p.Y < t.GroundYInRiver(p.X) - OnGround) e.Add(Loc.F("固定节点 {0} 埋在岸坡土体里，应放在坡面上", (a.Id)));
                else if (NoPiers(l) && NeedsPillar(l, p) && p.Y < low) e.Add(Loc.F("固定节点 {0} 在禁止建墩的河道里", (a.Id)));
            }
            return e;
        }

        // ---------------- custom level files ----------------

        /// <summary>
        /// Reads a custom level file. Current files hold a full <see cref="LevelDefinition"/>; files from the old
        /// designer hold a <see cref="CustomLevelSpec"/> and are converted (the file itself is only rewritten on save).
        /// </summary>
        public static LevelDefinition ReadCustomLevel(string json, GameDatabase db)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Terrain", out _)){var level=GameDatabase.DeserializeLevel(json);if(level.ProvidedInfrastructure!=null)LiftMechanics.Enable(level,db);return level;}
            var spec = JsonSerializer.Deserialize<CustomLevelSpec>(json, GameDatabase.JsonOptions);
            return spec == null ? null : CustomLevelBuilder.Build(spec, db);
        }

        /// <summary>Prepares a level for saving as a custom level: custom id from its name, "custom" difficulty.</summary>
        public static LevelDefinition AsCustom(LevelDefinition l)
        {
            var c = Copy(l);
            c.Id = CustomLevelBuilder.IdFor(c.Name);
            if (string.IsNullOrWhiteSpace(c.Difficulty) || !CustomLevelBuilder.IsCustom(l.Id)) c.Difficulty = Loc.T("自定义");
            return c;
        }
    }
}
