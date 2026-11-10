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

        public static GameDatabase FromJson(string materialsJson, string vehiclesJson, string levelsJson)
        {
            var db = new GameDatabase();
            var mats = JsonSerializer.Deserialize<MaterialFile>(materialsJson, JsonOptions) ?? throw new Exception("材料数据为空");
            foreach (var m in mats.Materials) db.Materials[m.MaterialId] = m;
            var veh = JsonSerializer.Deserialize<VehicleFile>(vehiclesJson, JsonOptions) ?? throw new Exception("车辆数据为空");
            foreach (var v in veh.Vehicles) db.Vehicles[v.Id] = v;
            var lv = JsonSerializer.Deserialize<LevelFile>(levelsJson, JsonOptions) ?? throw new Exception("关卡数据为空");
            db.Levels.AddRange(lv.Levels);
            db.Validate();
            return db;
        }

        /// <summary>数据一致性检查，避免关卡引用不存在的材料或车辆。</summary>
        private void Validate()
        {
            foreach (var l in Levels)
            {
                foreach (var id in l.AllowedMaterials)
                    if (!Materials.ContainsKey(id)) throw new Exception($"关卡 {l.Id} 引用了不存在的材料 {id}");
                foreach (var e in l.TrafficEvents)
                    if (e.Type == TrafficEventType.Vehicle && !Vehicles.ContainsKey(e.VehicleId))
                        throw new Exception($"关卡 {l.Id} 引用了不存在的车辆 {e.VehicleId}");
                if (!l.Anchors.Any(a => a.IsRoadStart) || !l.Anchors.Any(a => a.IsRoadEnd))
                    throw new Exception($"关卡 {l.Id} 缺少道路起点/终点锚点");
            }
        }

        public MaterialDefinition GetMaterial(string id) => id != null && Materials.TryGetValue(id, out var m) ? m : null;
        public LevelDefinition GetLevel(string id) => Levels.FirstOrDefault(l => l.Id == id);
        public int LevelIndex(string id) => Levels.FindIndex(l => l.Id == id);

        public static string SerializeDesign(BridgeDesign d) => JsonSerializer.Serialize(d, JsonOptions);
        public static BridgeDesign DeserializeDesign(string json) => JsonSerializer.Deserialize<BridgeDesign>(json, JsonOptions);
    }
}
