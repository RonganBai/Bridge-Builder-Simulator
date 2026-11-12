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

        /// <summary>Benchmark mode: every write is skipped (reported as success) so player files are never changed.</summary>
        public static bool WritesSuspended { get; set; }

        public static bool SaveDesign(BridgeDesign design, out string error)
        {
            error = null;
            if (WritesSuspended) return true;
            try
            {
                DirAccess.MakeDirRecursiveAbsolute(SaveDir);
                design.SavedTotalCost = design.TotalCost();
                using var f = Godot.FileAccess.Open(DesignPath(design.LevelId), Godot.FileAccess.ModeFlags.Write);
                if (f == null) { error = Loc.T("无法写入存档：") + Godot.FileAccess.GetOpenError(); return false; }
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
                if (!HasSave(levelId)) { error = Loc.T("没有找到该关卡的存档"); return null; }
                var d = GameDatabase.DeserializeDesign(Godot.FileAccess.GetFileAsString(DesignPath(levelId)));
                if (d == null || d.LevelId != levelId) { error = Loc.T("存档与关卡不匹配"); return null; }
                return d;
            }
            catch (Exception e) { error = Loc.T("存档损坏：") + e.Message; return null; }
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

        // ---------------- Named designs (several saves per level, never overwriting the auto save) ----------------
        private static string NamedDir(string levelId) => $"{SaveDir}/{levelId}_named";
        private static string NamedPath(string levelId, string name) => $"{NamedDir(levelId)}/{CustomLevelBuilder.SafeName(name)}.json";

        public static bool SaveNamedDesign(BridgeDesign design, string name, out string error)
        {
            error = null;
            if (WritesSuspended) return true;
            if (string.IsNullOrWhiteSpace(name)) { error = Loc.T("请输入方案名称"); return false; }
            try
            {
                DirAccess.MakeDirRecursiveAbsolute(NamedDir(design.LevelId));
                design.SavedTotalCost = design.TotalCost();
                design.Name = name.Trim();
                using var f = Godot.FileAccess.Open(NamedPath(design.LevelId, name), Godot.FileAccess.ModeFlags.Write);
                if (f == null) { error = Loc.T("无法写入存档：") + Godot.FileAccess.GetOpenError(); return false; }
                f.StoreString(GameDatabase.SerializeDesign(design));
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        /// <summary>Names of the saved designs of a level (display name stored in the file, file name as fallback).</summary>
        public static System.Collections.Generic.List<string> ListNamedDesigns(string levelId)
        {
            var names = new System.Collections.Generic.List<string>();
            var dir = DirAccess.Open(NamedDir(levelId));
            if (dir == null) return names;
            foreach (var file in dir.GetFiles())
            {
                if (!file.EndsWith(".json")) continue;
                string stem = file.Substring(0, file.Length - 5);
                try { var d = GameDatabase.DeserializeDesign(Godot.FileAccess.GetFileAsString($"{NamedDir(levelId)}/{file}")); names.Add(string.IsNullOrWhiteSpace(d?.Name) ? stem : d.Name); }
                catch { names.Add(stem); }
            }
            names.Sort(StringComparer.CurrentCulture);
            return names;
        }

        public static BridgeDesign LoadNamedDesign(string levelId, string name, out string error)
        {
            error = null;
            string path = NamedPath(levelId, name);
            try
            {
                if (!Godot.FileAccess.FileExists(path)) { error = Loc.T("找不到方案：") + name; return null; }
                var d = GameDatabase.DeserializeDesign(Godot.FileAccess.GetFileAsString(path));
                if (d == null || d.LevelId != levelId) { error = Loc.T("方案与关卡不匹配"); return null; }
                return d;
            }
            catch (Exception e) { error = Loc.T("方案损坏：") + e.Message; return null; }
        }

        // ---------------- Custom levels (user://custom_levels) ----------------
        private const string CustomDir = "user://custom_levels";

        public static bool SaveCustomLevel(CustomLevelSpec spec, out string error)
        {
            error = null;
            if (WritesSuspended) return true;
            try
            {
                DirAccess.MakeDirRecursiveAbsolute(CustomDir);
                spec.Id = CustomLevelBuilder.IdFor(spec.Name);
                using var f = Godot.FileAccess.Open($"{CustomDir}/{spec.Id}.json", Godot.FileAccess.ModeFlags.Write);
                if (f == null) { error = Loc.T("无法写入关卡：") + Godot.FileAccess.GetOpenError(); return false; }
                f.StoreString(JsonSerializer.Serialize(spec, GameDatabase.JsonOptions));
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        public static CustomLevelSpec LoadCustomLevel(string id) => ReadUserJson<CustomLevelSpec>($"{CustomDir}/{id}.json");

        public static System.Collections.Generic.List<CustomLevelSpec> ListCustomLevels()
        {
            var list = new System.Collections.Generic.List<CustomLevelSpec>();
            var dir = DirAccess.Open(CustomDir);
            if (dir == null) return list;
            foreach (var file in dir.GetFiles())
                if (file.EndsWith(".json") && ReadUserJson<CustomLevelSpec>($"{CustomDir}/{file}") is { } s) list.Add(s);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            return list;
        }

        /// <summary>Moves a custom level file to user://custom_levels/deleted (recoverable, not destroyed).</summary>
        public static bool DeleteCustomLevel(string id)
        {
            if (WritesSuspended) return false;
            string src = $"{CustomDir}/{id}.json";
            if (!Godot.FileAccess.FileExists(src)) return false;
            DirAccess.MakeDirRecursiveAbsolute($"{CustomDir}/deleted");
            return DirAccess.RenameAbsolute(src, $"{CustomDir}/deleted/{id}_{DateTime.Now:yyyyMMddHHmmss}.json") == Error.Ok;
        }

        public static T ReadUserJson<T>(string path) where T : class
        {
            try
            {
                if (!Godot.FileAccess.FileExists(path)) return null;
                return JsonSerializer.Deserialize<T>(Godot.FileAccess.GetFileAsString(path), GameDatabase.JsonOptions);
            }
            catch (Exception e) { GD.PushWarning(Loc.F("读取 {0} 失败：{1}", (path), (e.Message))); return null; }
        }

        public static void WriteUserJson<T>(string path, T data)
        {
            if (WritesSuspended) return;
            try
            {
                using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
                f?.StoreString(JsonSerializer.Serialize(data, GameDatabase.JsonOptions));
            }
            catch (Exception e) { GD.PushWarning(Loc.F("写入 {0} 失败：{1}", (path), (e.Message))); }
        }
    }
}
