using System;
using System.Collections.Generic;
using System.Text.Json;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.Core
{
    /// <summary>玩家设置（保存在 user://settings.json）。</summary>
    /// <summary>Window mode ids and resolution helpers.</summary>
    public static class DisplayMode
    {
        public const string Windowed = "windowed", Fullscreen = "fullscreen", Borderless = "borderless";
        public static readonly string[] Standard = { "1280x720", "1366x768", "1600x900", "1920x1080", "1980x1080", "2560x1440", "3840x2160" };

        public static Vector2I? Parse(string r)
        {
            if (string.IsNullOrEmpty(r)) return null;
            var p = r.Split('x');
            return p.Length == 2 && int.TryParse(p[0], out int w) && int.TryParse(p[1], out int h) && w >= 640 && h >= 360 ? new Vector2I(w, h) : null;
        }

        /// <summary>Standard resolutions that fit on the current screen, plus the screen's own size.</summary>
        public static List<string> Choices()
        {
            var screen = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());
            var list = new List<string>();
            foreach (var r in Standard) { var v = Parse(r).Value; if (v.X <= screen.X && v.Y <= screen.Y) list.Add(r); }
            string native = $"{screen.X}x{screen.Y}";
            if (!list.Contains(native)) list.Add(native);
            return list;
        }
    }

    public class GameSettings : IVersionStamped
    {
        /// <summary>Game version that last wrote the settings (null = before versioning).</summary>
        public string GameVersion { get; set; }
        /// <summary>视觉变形放大系数（仅影响显示，不影响判定）。</summary>
        public double DeformationScale { get; set; } = 30;
        public bool ShowGrid { get; set; } = true;
        /// <summary>Interface language: "zh" (default) or "en". Changing it restarts the game.</summary>
        public string Language { get; set; } = Loc.Chinese;
        public double CameraSensitivity { get; set; } = 1.0;
        public bool Fullscreen { get; set; }
        /// <summary>"windowed" / "fullscreen" / "borderless"; null in old settings files (then Fullscreen decides).</summary>
        public string WindowMode { get; set; }
        /// <summary>"1920x1080" etc.; null or "" = default (project window size / native screen).</summary>
        public string Resolution { get; set; }

        /// <summary>Effective window mode, migrating the old Fullscreen flag.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Mode
        {
            get => WindowMode ?? (Fullscreen ? DisplayMode.Fullscreen : DisplayMode.Windowed);
            set { WindowMode = value; Fullscreen = value != DisplayMode.Windowed; }
        }
        /// <summary>Low graphics quality for integrated GPUs: no sun shadows, no MSAA, 3D drawn at 75 % resolution
        /// (UI stays sharp). Old settings files load as false.</summary>
        public bool LowQuality { get; set; }

        // ---- Graphics (GraphicsVersion 0 = settings file from before these options; migrated once) ----
        public int GraphicsVersion { get; set; }
        /// <summary>off / fxaa / smaa / msaa2 / msaa4 / msaa8 / taa</summary>
        public string Antialiasing { get; set; } = "msaa2";
        /// <summary>off / low / high</summary>
        public string Shadows { get; set; } = "low";
        /// <summary>3D render scale 0.5..1 (upscaled with FSR on the Forward+ renderer; UI always native).</summary>
        public double RenderScale { get; set; } = 1.0;
        public double Brightness { get; set; } = 1.0;
        public double Contrast { get; set; } = 1.0;
        public double Saturation { get; set; } = 1.0;
        public bool VSync { get; set; } = true;
        /// <summary>Frame rate cap, 0 = unlimited.</summary>
        public int MaxFps { get; set; }
        /// <summary>Main menu background: "video" (pre-rendered loop) or "live" (real-time 3D scene).</summary>
        public string MenuBackground { get; set; } = "live";

        /// <summary>Old files: the former low-quality checkbox becomes the matching graphics values.</summary>
        public void MigrateGraphics()
        {
            if (GraphicsVersion >= 1) return;
            if (LowQuality) ApplyPreset("low");
            GraphicsVersion = 1;
        }

        public void ApplyPreset(string preset)
        {
            switch (preset)
            {
                case "low": Antialiasing = "off"; Shadows = "off"; RenderScale = 0.75; break;
                case "high": Antialiasing = "msaa4"; Shadows = "high"; RenderScale = 1.0; break;
                default: Antialiasing = "msaa2"; Shadows = "low"; RenderScale = 1.0; break; // medium (the default)
            }
            LowQuality = preset == "low";
        }
        /// <summary>Material performance panel also shows numbers (moved from the tool bar into settings).</summary>
        public bool ShowMaterialValues { get; set; }
        /// <summary>Custom material hotkeys: material id -> "1".."9","0" ("" = no key). Missing ids use defaults.</summary>
        public Dictionary<string, string> MaterialKeys { get; set; } = new();

        public static readonly string[] Digits = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };

        /// <summary>
        /// Hotkey of every material in tool-bar order. Custom keys win (first come, first served); the others get
        /// 2..9, 0 in order. Key 1 selects the select / drag tool unless a material uses it.
        /// </summary>
        public Dictionary<string, string> ResolveKeys(IEnumerable<string> order)
        {
            var list = new List<string>(order);
            var res = new Dictionary<string, string>(); var used = new HashSet<string>();
            foreach (var id in list)
                if (MaterialKeys != null && MaterialKeys.TryGetValue(id, out var k))
                    res[id] = !string.IsNullOrEmpty(k) && Array.IndexOf(Digits, k) >= 0 && used.Add(k) ? k : "";
            var free = new Queue<string>(new[] { "2", "3", "4", "5", "6", "7", "8", "9", "0" });
            foreach (var id in list)
            {
                if (res.ContainsKey(id)) continue;
                while (free.Count > 0 && used.Contains(free.Peek())) free.Dequeue();
                res[id] = free.Count > 0 ? free.Dequeue() : "";
                if (res[id] != "") used.Add(res[id]);
            }
            return res;
        }
    }

    /// <summary>关卡进度（保存在 user://progress.json）。</summary>
    public class GameProgress : IVersionStamped
    {
        /// <summary>Game version that last wrote the progress (null = before versioning); used to show "updated to …" once.</summary>
        public string GameVersion { get; set; }
        /// <summary>各关最高分（通关才记录）。</summary>
        public Dictionary<string, int> BestScores { get; set; } = new();
        /// <summary>Engineering Score records per level (best score, lowest cost, fastest, highest margin) of the
        /// current scoring version. Old progress files have none; BestScores still unlocks the next level.</summary>
        public Dictionary<string, LevelRecord> Records { get; set; } = new();
        public string LastLevelId { get; set; }
    }

    /// <summary>
    /// 全局游戏管理器（Autoload 单例）：加载静态数据、保存进度与设置、负责场景切换。
    /// </summary>
    public partial class GameManager : Node
    {
        [Signal] public delegate void DisplaySettingsChangedEventHandler();
        public const string MainMenuScene = "res://Scenes/MainMenu.tscn";
        public const string LevelScene = "res://Scenes/LevelScene.tscn";

        public static GameManager Instance { get; private set; }
        public GameDatabase Db { get; private set; }
        public string LoadError { get; private set; }
        public GameSettings Settings { get; private set; } = new();
        public GameProgress Progress { get; private set; } = new();

        /// <summary>即将进入的关卡。</summary>
        public string CurrentLevelId { get; set; }
        /// <summary>进入关卡时是否加载存档方案。</summary>
        public bool LoadSavedDesign { get; set; }
        /// <summary>返回主菜单时直接打开关卡选择。</summary>
        public bool ShowLevelSelectOnMenu { get; set; }
        /// <summary>Version the player came from when this run is the first of a newer version (null otherwise).</summary>
        public string UpdatedFrom { get; set; }

        public override void _Ready()
        {
            Instance = this;
            // Settings first: the language decides how texts and the game data are loaded
            var s = SaveManager.ReadUserJson<GameSettings>("user://settings.json");
            if (s != null) Settings = s;
            Loc.Language = Settings.Language == Loc.English ? Loc.English : Loc.Chinese;
            if (Loc.IsEnglish && Godot.FileAccess.FileExists(EnglishTable))
            {
                try { Loc.LoadEnglish(Godot.FileAccess.GetFileAsString(EnglishTable)); }
                catch (Exception e) { GD.PushError("English text table: " + e.Message); }
            }
            try
            {
                Db = GameDatabase.FromJson(
                    Loc.TranslateJson(ReadRes("res://Data/Materials/materials.json")),
                    Loc.TranslateJson(ReadRes("res://Data/Vehicles/vehicles.json")),
                    Loc.TranslateJson(ReadRes("res://Data/Levels/levels.json")),
                    Loc.TranslateJson(ReadRes("res://Data/Tunnels/tunnels.json")),
                    Loc.TranslateJson(ReadRes("res://Data/Environment/weather.json")),
                    Loc.TranslateJson(ReadRes("res://Data/Scoring/scoring.json")));
            }
            catch (Exception e)
            {
                LoadError = Loc.T("数据加载失败：") + e.Message;
                GD.PushError(LoadError);
            }
            if (Db != null)
            {
                // Built-in data stays available for "restore defaults"; player level settings replace it
                foreach (var l in Db.Levels) _defaultLevels[l.Id] = GameDatabase.SerializeLevel(l);
                if (!Benchmark.Requested(out _))
                    foreach (var id in System.Linq.Enumerable.ToList(_defaultLevels.Keys))
                        if (SaveManager.LoadLevelOverride(id) is { } o)
                        {
                            o.Id = id;
                            if (Db.LevelError(o) is { } err) GD.PushWarning(Loc.F("关卡设置 {0} 无效，使用内置设置：{1}", (id), (err)));
                            else Db.ReplaceLevel(o);
                        }
            }
            var p = SaveManager.ReadUserJson<GameProgress>("user://progress.json");
            if (p != null) Progress = p;
            // An existing player who ran an older version: remember it so the menu can say what changed (once)
            if (p != null && GameVersion.IsOlder(p.GameVersion)) UpdatedFrom = p.GameVersion ?? GameVersion.Unversioned;
            ApplySettings();
            if (Benchmark.Requested(out var benchLevels))
            {
                var bench = new Benchmark { Name = "Benchmark" };
                AddChild(bench);
                bench.CallDeferred(Benchmark.MethodName.Begin, benchLevels);
            }
        }

        public const string EnglishTable = "res://Data/Locale/en.json";

        /// <summary>Save the settings and restart the game so every text is created in the new language.</summary>
        public void RestartForLanguage()
        {
            SaveSettings();
            OS.SetRestartOnExit(true, OS.GetCmdlineArgs());
            GetTree().Quit();
        }

        private static string ReadRes(string path)
        {
            if (!Godot.FileAccess.FileExists(path)) throw new Exception(Loc.F("找不到文件 {0}", (path)));
            return Godot.FileAccess.GetFileAsString(path);
        }

        public void ApplySettings()
        {
            // Embedded in the editor Game tab the window belongs to the editor: only the 3D quality applies
            if (Engine.IsEmbeddedInEditor()) { ApplyQuality(); return; }
            string mode = Settings.Mode;
            var res = DisplayMode.Parse(Settings.Resolution);
            int screenId = DisplayServer.WindowGetCurrentScreen();
            if (mode == DisplayMode.Fullscreen) DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
            // Godot's "Fullscreen" mode is a borderless window covering the screen
            else if (mode == DisplayMode.Borderless) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
            else
            {
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
                if (res is { } r)
                {
                    // Windowed: the resolution is the window size (never larger than the usable screen), centred
                    var usable = DisplayServer.ScreenGetUsableRect(screenId);
                    var size = new Vector2I(Math.Min(r.X, usable.Size.X), Math.Min(r.Y, usable.Size.Y));
                    DisplayServer.WindowSetSize(size);
                    DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
                }
            }
            ApplyQuality();
        }

        public const float LowQualityScale = 0.75f;

        /// <summary>Applies the graphics quality to the window and to the sun of the current scene.</summary>
        public void ApplyQuality()
        {
            var vp = GetTree()?.Root;
            if (vp == null) return;
            var g = Settings; g.MigrateGraphics();
            string aa = g.Antialiasing ?? "msaa2";
            vp.Msaa3D = aa switch { "msaa2" => Viewport.Msaa.Msaa2X, "msaa4" => Viewport.Msaa.Msaa4X, "msaa8" => Viewport.Msaa.Msaa8X, _ => Viewport.Msaa.Disabled };
            vp.ScreenSpaceAA = aa switch { "fxaa" => Viewport.ScreenSpaceAAEnum.Fxaa, "smaa" => Viewport.ScreenSpaceAAEnum.Smaa, _ => Viewport.ScreenSpaceAAEnum.Disabled };
            vp.UseTaa = aa == "taa";
            DisplayServer.WindowSetVsyncMode(g.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = Math.Max(0, g.MaxFps);
            RenderingServer.DirectionalShadowAtlasSetSize(g.Shadows == "low" ? 2048 : 4096, true);
            RenderingServer.DirectionalSoftShadowFilterSetQuality(g.Shadows == "low" ? RenderingServer.ShadowQuality.Hard : RenderingServer.ShadowQuality.SoftLow);
            // Fullscreen / borderless: a resolution below the screen's lowers the 3D render resolution (UI stays native)
            float scale = (float)Math.Clamp(g.RenderScale, 0.5, 1.0);
            var res = DisplayMode.Parse(Settings.Resolution);
            if (Settings.Mode != DisplayMode.Windowed && res is { } r)
            {
                var screen = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());
                if (screen.Y > 0) scale *= Mathf.Clamp(r.Y / (float)screen.Y, 0.25f, 1f);
            }
            vp.Scaling3DScale = scale;
            // FSR 1 keeps edges sharper than bilinear when upscaling (Forward+ only)
            vp.Scaling3DMode = scale < 0.999f && RenderingServer.GetCurrentRenderingMethod() == "forward_plus" ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
            foreach (var n in vp.FindChildren("*", nameof(DirectionalLight3D), true, false))
                if (n is DirectionalLight3D sun) sun.ShadowEnabled = ShadowsOn;
            foreach (var n in vp.FindChildren("*", nameof(WorldEnvironment), true, false))
                if (n is WorldEnvironment we && we.Environment != null) ApplyColor(we.Environment);
            EmitSignal(SignalName.DisplaySettingsChanged);
        }

        /// <summary>Sun shadows on unless low quality is selected (used when a scene builds its light).</summary>
        public static bool ShadowsOn => (Instance?.Settings?.Shadows ?? "high") != "off";

        /// <summary>Brightness / contrast / saturation on a 3D environment (also used when a scene builds one).</summary>
        public static void ApplyColor(Godot.Environment env)
        {
            var g = Instance?.Settings;
            if (g == null || env == null) return;
            bool neutral = Math.Abs(g.Brightness - 1) < 1e-3 && Math.Abs(g.Contrast - 1) < 1e-3 && Math.Abs(g.Saturation - 1) < 1e-3;
            env.AdjustmentEnabled = !neutral;
            env.AdjustmentBrightness = (float)g.Brightness; env.AdjustmentContrast = (float)g.Contrast; env.AdjustmentSaturation = (float)g.Saturation;
        }

        public void SaveSettings() => SaveManager.WriteUserJson("user://settings.json", Settings);
        public void SaveProgress() => SaveManager.WriteUserJson("user://progress.json", Progress);

        public bool IsUnlocked(int index)
        {
            if (Db == null || index < 0 || index >= Db.Levels.Count) return false;
            if (index == 0) return true;
            return Progress.BestScores.ContainsKey(Db.Levels[index - 1].Id);
        }

        public int BestScoreFor(string levelId) => Progress.BestScores.TryGetValue(levelId, out var s) ? s : 0;

        public void RecordCompletion(string levelId, int score)
        {
            Progress.BestScores[levelId] = Math.Max(BestScoreFor(levelId), score);
            SaveProgress();
        }

        /// <summary>Record of a level for the current scoring version (a record of an older version starts over).</summary>
        public LevelRecord RecordFor(string levelId, string contract = null)
        {
            int v = Db?.Scoring.Version ?? 2;
            if (!Progress.Records.TryGetValue(EngineeringScore.RecordKey(levelId, contract), out var rec) || rec.Version != v) rec = new LevelRecord { Version = v };
            return rec;
        }

        /// <summary>Update the level's records with a passed test; the improving design is kept as a named design per kind.</summary>
        public List<string> RecordResult(string levelId, SimulationResult r, BridgeDesign design)
        {
            var rec = RecordFor(levelId, r.Contract);
            var kinds = rec.Improve(r);
            Progress.Records[EngineeringScore.RecordKey(levelId, r.Contract)] = rec;
            // BestScores only marks the level as passed (unlocking); old version-1 scores in it stay untouched
            if (!Progress.BestScores.ContainsKey(levelId)) Progress.BestScores[levelId] = r.Score;
            SaveProgress();
            string suffix = Db?.Scoring.Contract(r.Contract) is { } cc ? Loc.T("（") + cc.Name + Loc.T("）") : "";
            foreach (var k in kinds) SaveManager.SaveNamedDesign(design.Clone(), LevelRecord.KindName(k) + suffix, out _);
            return kinds;
        }

        private readonly System.Collections.Generic.Dictionary<string, string> _defaultLevels = new();

        /// <summary>Level settings: replace this level's data for this player and restart it.</summary>
        public bool SaveLevelSettings(LevelDefinition level, out string error)
        {
            error = Db.LevelError(level);
            if (error != null || !SaveManager.SaveLevelOverride(level, out error)) return false;
            if (!Db.ReplaceLevel(level)) { }   // custom level: ResolveLevel applies the override
            StartLevel(level.Id, true);
            return true;
        }

        /// <summary>Level settings: back to the built-in (or designed) data.</summary>
        public void RestoreLevelDefaults(string levelId)
        {
            SaveManager.RemoveLevelOverride(levelId);
            if (_defaultLevels.TryGetValue(levelId, out var json)) Db.ReplaceLevel(GameDatabase.DeserializeLevel(json));
            StartLevel(levelId, true);
        }

        public bool LevelOverridden(string levelId) => SaveManager.HasLevelOverride(levelId);

        public void StartLevel(string levelId, bool loadSave)
        {
            CurrentLevelId = levelId;
            LoadSavedDesign = loadSave;
            Progress.LastLevelId = levelId;
            SaveProgress();
            BridgeArchitect3D.UI.MainMenuBackdrop.KeepFrom(GetTree().CurrentScene);
            GetTree().ChangeSceneToFile(LevelScene);
        }

        /// <summary>Built-in level, or a player-made level from user://custom_levels.</summary>
        public LevelDefinition ResolveLevel(string levelId)
        {
            var lv = Db?.GetLevel(levelId);
            if (lv != null || !CustomLevelBuilder.IsCustom(levelId)) return lv;
            if (SaveManager.LoadLevelOverride(levelId) is { } o && Db.LevelError(o) == null) { o.Id = levelId; return o; }
            var spec = SaveManager.LoadCustomLevel(levelId);
            return spec == null ? null : CustomLevelBuilder.Build(spec, Db);
        }

        public string NextLevelId(string levelId)
        {
            int i = Db.LevelIndex(levelId);
            return i >= 0 && i + 1 < Db.Levels.Count ? Db.Levels[i + 1].Id : null;
        }

        public void GoToMainMenu() => GetTree().ChangeSceneToFile(MainMenuScene);
    }
}
