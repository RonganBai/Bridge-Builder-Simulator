using System;
using System.Collections.Generic;
using System.Text.Json;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.Core
{
    /// <summary>玩家设置（保存在 user://settings.json）。</summary>
    public class GameSettings
    {
        /// <summary>视觉变形放大系数（仅影响显示，不影响判定）。</summary>
        public double DeformationScale { get; set; } = 30;
        public bool ShowGrid { get; set; } = true;
        public double CameraSensitivity { get; set; } = 1.0;
        public bool Fullscreen { get; set; }
    }

    /// <summary>关卡进度（保存在 user://progress.json）。</summary>
    public class GameProgress
    {
        /// <summary>各关最高分（通关才记录）。</summary>
        public Dictionary<string, int> BestScores { get; set; } = new();
        public string LastLevelId { get; set; }
    }

    /// <summary>
    /// 全局游戏管理器（Autoload 单例）：加载静态数据、保存进度与设置、负责场景切换。
    /// </summary>
    public partial class GameManager : Node
    {
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

        public override void _Ready()
        {
            Instance = this;
            try
            {
                Db = GameDatabase.FromJson(
                    ReadRes("res://Data/Materials/materials.json"),
                    ReadRes("res://Data/Vehicles/vehicles.json"),
                    ReadRes("res://Data/Levels/levels.json"));
            }
            catch (Exception e)
            {
                LoadError = "数据加载失败：" + e.Message;
                GD.PushError(LoadError);
            }
            var s = SaveManager.ReadUserJson<GameSettings>("user://settings.json");
            if (s != null) Settings = s;
            var p = SaveManager.ReadUserJson<GameProgress>("user://progress.json");
            if (p != null) Progress = p;
            ApplySettings();
        }

        private static string ReadRes(string path)
        {
            if (!Godot.FileAccess.FileExists(path)) throw new Exception($"找不到文件 {path}");
            return Godot.FileAccess.GetFileAsString(path);
        }

        public void ApplySettings()
        {
            DisplayServer.WindowSetMode(Settings.Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
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

        public void StartLevel(string levelId, bool loadSave)
        {
            CurrentLevelId = levelId;
            LoadSavedDesign = loadSave;
            Progress.LastLevelId = levelId;
            SaveProgress();
            GetTree().ChangeSceneToFile(LevelScene);
        }

        public string NextLevelId(string levelId)
        {
            int i = Db.LevelIndex(levelId);
            return i >= 0 && i + 1 < Db.Levels.Count ? Db.Levels[i + 1].Id : null;
        }

        public void GoToMainMenu() => GetTree().ChangeSceneToFile(MainMenuScene);
    }
}
