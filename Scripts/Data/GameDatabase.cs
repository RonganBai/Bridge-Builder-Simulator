using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// 游戏静态数据库（材料、车辆、关卡）。只接收 JSON 文本，不依赖 Godot 文件系统，
    /// 因此可以在独立测试工程中复用。
    /// </summary>
    public class GameDatabase
    {
        public Dictionary<string, MaterialDefinition> Materials { get; } = new();
        public Dictionary<string, VehicleDefinition> Vehicles { get; } = new();
        public List<LevelDefinition> Levels { get; } = new();

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Underwater tunnel numbers (Data/Tunnels/tunnels.json); null = tunnels unavailable.</summary>
        public TunnelConfig TunnelConfig { get; set; }
        /// <summary>Unified weather (weather.json); built-in defaults when the file is not given.</summary>
        public WeatherConfig Weather { get; set; } = WeatherConfig.Default();
        /// <summary>Engineering Score settings (scoring.json); built-in defaults when the file is not given.</summary>
        public ScoringConfig Scoring { get; set; } = ScoringConfig.Default();

        public static GameDatabase FromJson(string materialsJson, string vehiclesJson, string levelsJson, string tunnelsJson = null, string weatherJson = null, string scoringJson = null)
        {
            var db = new GameDatabase();
            if (scoringJson != null) db.Scoring = JsonSerializer.Deserialize<ScoringConfig>(scoringJson, JsonOptions) ?? ScoringConfig.Default();
            if (weatherJson != null) db.Weather = JsonSerializer.Deserialize<WeatherConfig>(weatherJson, JsonOptions) ?? WeatherConfig.Default();
            if (tunnelsJson != null) db.TunnelConfig = JsonSerializer.Deserialize<TunnelConfig>(tunnelsJson, JsonOptions);
            TunnelRules.Bind(db);
            EngineeringScore.Bind(db);
            var mats = JsonSerializer.Deserialize<MaterialFile>(materialsJson, JsonOptions) ?? throw new Exception(Loc.T("材料数据为空"));
            foreach (var m in mats.Materials) db.Materials[m.MaterialId] = m;
            var veh = JsonSerializer.Deserialize<VehicleFile>(vehiclesJson, JsonOptions) ?? throw new Exception(Loc.T("车辆数据为空"));
            foreach (var v in veh.Vehicles) db.Vehicles[v.Id] = v;
            var lv = JsonSerializer.Deserialize<LevelFile>(levelsJson, JsonOptions) ?? throw new Exception(Loc.T("关卡数据为空"));
            db.Levels.AddRange(lv.Levels);
            db.Validate();
            return db;
        }

        /// <summary>数据一致性检查，避免关卡引用不存在的材料或车辆。</summary>
        private void Validate()
        {
            foreach (var l in Levels)
                if (LevelError(l) is { } err) throw new Exception(err);
        }

        /// <summary>Why a level definition cannot be played (null = valid). Used for built-in data and level settings.</summary>
        public string LevelError(LevelDefinition l)
        {
            if (l == null) return Loc.T("关卡数据为空");
            if (l.Terrain == null || l.Objectives == null || l.Anchors == null || l.AllowedMaterials == null || l.TrafficEvents == null)
                return Loc.F("关卡 {0} 缺少地形 / 目标 / 锚点 / 建材 / 事件", (l.Id));
            foreach (var id in l.AllowedMaterials)
                if (!Materials.ContainsKey(id)) return Loc.F("关卡 {0} 引用了不存在的材料 {1}", (l.Id), (id));
            foreach (var e in l.TrafficEvents)
                if ((e.Type == TrafficEventType.Vehicle || e.Type == TrafficEventType.Train) && (e.VehicleId == null || !Vehicles.ContainsKey(e.VehicleId)))
                    return Loc.F("关卡 {0} 引用了不存在的车辆 {1}", (l.Id), (e.VehicleId));
            if (!l.Anchors.Any(a => a.IsRoadStart) || !l.Anchors.Any(a => a.IsRoadEnd))
                return Loc.F("关卡 {0} 缺少道路起点/终点锚点", (l.Id));
            if (l.Terrain.RiverBedDepth > l.Terrain.WaterLevel && l.Terrain.HasWater)
                return Loc.T("河床不能高于水位");
            if (l.Terrain.BuildMinY >= l.Terrain.BuildMaxY) return Loc.T("最低建造标高要低于最高建造标高");
            return null;
        }

        public static string SerializeLevel(LevelDefinition l) => JsonSerializer.Serialize(l, JsonOptions);
        public static LevelDefinition DeserializeLevel(string json) => JsonSerializer.Deserialize<LevelDefinition>(json, JsonOptions);

        /// <summary>Replace a built-in level (level settings override); false when no level has that id.</summary>
        public bool ReplaceLevel(LevelDefinition l)
        {
            int i = LevelIndex(l?.Id);
            if (i < 0) return false;
            Levels[i] = l;
            return true;
        }

        public MaterialDefinition GetMaterial(string id) => id != null && Materials.TryGetValue(id, out var m) ? m : null;
        public LevelDefinition GetLevel(string id) => Levels.FirstOrDefault(l => l.Id == id);
        public int LevelIndex(string id) => Levels.FindIndex(l => l.Id == id);

        public static string SerializeDesign(BridgeDesign d) => JsonSerializer.Serialize(d, JsonOptions);
        /// <summary>
        /// Loads a design. Version-1 saves (no mechanisms) migrate in memory to the current version with an empty
        /// mechanism set; the file on disk is only rewritten when the player saves again.
        /// </summary>
        public static BridgeDesign DeserializeDesign(string json)
        {
            var d = JsonSerializer.Deserialize<BridgeDesign>(json, JsonOptions);
            if (d == null) return null;
            var mechanisms=d.EnsureMechanisms();
            TunnelRules.MigrateLegacy(d);
            if(mechanisms.Linkage?.Drivers!=null)
                foreach(var driver in mechanisms.Linkage.Drivers)
                {
                    // Keep the saved direction and stroke; only bring it into the allowed range (old saves are +-50%)
                    double mag=System.Math.Abs(driver.Ratio);
                    if(!double.IsFinite(mag) || mag<1e-9) mag=LinkageDriverData.StrokeFraction;
                    mag=System.Math.Clamp(mag,LinkageDriverData.MinStroke,LinkageDriverData.MaxStroke);
                    driver.Ratio=driver.Ratio>0?mag:-mag;
                }
            if (d.Version < BridgeDesign.CurrentVersion) d.Version = BridgeDesign.CurrentVersion;
            return d;
        }
    }
}
