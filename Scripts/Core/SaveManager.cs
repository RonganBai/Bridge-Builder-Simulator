using System;
using System.Text.Json;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.Core
{
    /// <summary>JSON 存档：玩家建造方案、进度与设置均保存在 user:// 目录。</summary>
    public static class SaveManager
    {
        private const string SaveDir = "user://saves";

        public static string DesignPath(string levelId) => $"{SaveDir}/{levelId}.json";

        public static bool HasSave(string levelId) => Godot.FileAccess.FileExists(DesignPath(levelId));

        public static bool SaveDesign(BridgeDesign design, out string error)
        {
            error = null;
            try
            {
                DirAccess.MakeDirRecursiveAbsolute(SaveDir);
                design.SavedTotalCost = design.TotalCost();
                using var f = Godot.FileAccess.Open(DesignPath(design.LevelId), Godot.FileAccess.ModeFlags.Write);
                if (f == null) { error = "无法写入存档：" + Godot.FileAccess.GetOpenError(); return false; }
                f.StoreString(GameDatabase.SerializeDesign(design));
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        public static BridgeDesign LoadDesign(string levelId, out string error)
        {
            error = null;
            try
            {
                if (!HasSave(levelId)) { error = "没有找到该关卡的存档"; return null; }
                var d = GameDatabase.DeserializeDesign(Godot.FileAccess.GetFileAsString(DesignPath(levelId)));
                if (d == null || d.LevelId != levelId) { error = "存档与关卡不匹配"; return null; }
                return d;
            }
            catch (Exception e) { error = "存档损坏：" + e.Message; return null; }
        }

        /// <summary>读取参考方案（res://Data/ReferenceDesigns）。</summary>
        public static BridgeDesign LoadReference(string levelId)
        {
            string path = $"res://Data/ReferenceDesigns/{levelId}_reference.json";
            if (!Godot.FileAccess.FileExists(path)) return null;
            try { return GameDatabase.DeserializeDesign(Godot.FileAccess.GetFileAsString(path)); }
            catch { return null; }
        }

        /// <summary>读取关卡起始设计（res://Data/StartDesigns）。</summary>
        public static BridgeDesign LoadStartDesign(string name)
        {
            string path = $"res://Data/StartDesigns/{name}.json";
            if (!Godot.FileAccess.FileExists(path)) return null;
            try { return GameDatabase.DeserializeDesign(Godot.FileAccess.GetFileAsString(path)); }
            catch { return null; }
        }

        public static T ReadUserJson<T>(string path) where T : class
        {
            try
            {
                if (!Godot.FileAccess.FileExists(path)) return null;
                return JsonSerializer.Deserialize<T>(Godot.FileAccess.GetFileAsString(path), GameDatabase.JsonOptions);
            }
            catch (Exception e) { GD.PushWarning($"读取 {path} 失败：{e.Message}"); return null; }
        }

        public static void WriteUserJson<T>(string path, T data)
        {
            try
            {
                using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
                f?.StoreString(JsonSerializer.Serialize(data, GameDatabase.JsonOptions));
            }
            catch (Exception e) { GD.PushWarning($"写入 {path} 失败：{e.Message}"); }
        }
    }
}
