using BridgeArchitect3D.Core;
using Godot;

namespace BridgeArchitect3D.UI
{
    /// <summary>主菜单：开始游戏、选择关卡、继续游戏、设置、退出。</summary>
    public partial class MainMenu : Control
    {
        private GameManager _gm;
        private Control _levelOverlay, _settingsOverlay;
        private VBoxContainer _levelBox;

        public override void _Ready()
        {
            _gm = GameManager.Instance;
            Theme = UiTheme.Get();
            SetAnchorsPreset(LayoutPreset.FullRect);

            var bg = new ColorRect { Color = new Color(0.07f, 0.09f, 0.12f) };
            bg.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(bg);
            var stripe = new ColorRect { Color = new Color(0.25f, 0.62f, 1f, 0.08f) };
            stripe.AnchorLeft = 0; stripe.AnchorRight = 1; stripe.AnchorTop = 0.62f; stripe.AnchorBottom = 0.7f;
            AddChild(stripe);

            var center = new CenterContainer();
            center.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(center);
            var vb = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
            vb.AddThemeConstantOverride("separation", 12);
            center.AddChild(vb);
            vb.AddChild(UiTheme.MakeLabel("Bridge Architect 3D", 44, Colors.White));
            vb.AddChild(UiTheme.MakeLabel("建桥物理模拟 · 初版", 18, UiTheme.Muted));
            vb.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });

            if (_gm?.Db == null)
            {
                vb.AddChild(UiTheme.MakeLabel(_gm?.LoadError ?? "游戏数据未加载", 16, UiTheme.Bad));
                vb.AddChild(UiTheme.MakeButton("退出游戏", () => GetTree().Quit()));
                return;
            }

            vb.AddChild(Big("开始游戏", () => _gm.StartLevel(_gm.Db.Levels[0].Id, false)));
            vb.AddChild(Big("选择关卡", ShowLevels));
            var last = _gm.Progress.LastLevelId;
            var cont = Big("继续游戏", () => _gm.StartLevel(last, true));
            cont.Disabled = string.IsNullOrEmpty(last) || _gm.Db.GetLevel(last) == null || !SaveManager.HasSave(last);
            if (!cont.Disabled) cont.TooltipText = "继续：" + _gm.Db.GetLevel(last).Name;
            vb.AddChild(cont);
            vb.AddChild(Big("设置", () => _settingsOverlay.Visible = true));
            vb.AddChild(Big("退出游戏", () => GetTree().Quit()));

            var (lo, lb) = UiTheme.MakeDialog(this, "选择关卡", 560);
            _levelOverlay = lo; _levelBox = lb;
            BuildSettings();

            if (_gm.ShowLevelSelectOnMenu) { _gm.ShowLevelSelectOnMenu = false; ShowLevels(); }
        }

        private static Button Big(string text, System.Action a)
        {
            var b = UiTheme.MakeButton(text, a);
            b.CustomMinimumSize = new Vector2(360, 48);
            b.AddThemeFontSizeOverride("font_size", 20);
            return b;
        }

        private void ShowLevels()
        {
            foreach (var c in _levelBox.GetChildren()) if (c.GetIndex() > 0) c.QueueFree();
            for (int i = 0; i < _gm.Db.Levels.Count; i++)
            {
                var lv = _gm.Db.Levels[i];
                bool unlocked = _gm.IsUnlocked(i);
                int best = _gm.BestScoreFor(lv.Id);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                info.AddChild(UiTheme.MakeLabel(lv.Name, 18, unlocked ? Colors.White : UiTheme.Muted));
                info.AddChild(UiTheme.MakeLabel($"{(string.IsNullOrEmpty(lv.Difficulty) ? "" : lv.Difficulty + " · ")}河宽 {lv.Terrain.RiverWidth:0} m · 预算 ${lv.Budget:N0} · " + (best > 0 ? $"最高分 {best:N0}" : "未通关"), 14, UiTheme.Muted));
                row.AddChild(info);
                string id = lv.Id;
                var play = UiTheme.MakeButton(unlocked ? "新建" : "未解锁", () => _gm.StartLevel(id, false), 80);
                play.Disabled = !unlocked;
                var load = UiTheme.MakeButton("读取存档", () => _gm.StartLevel(id, true), 90);
                load.Disabled = !unlocked || !SaveManager.HasSave(id);
                row.AddChild(play); row.AddChild(load);
                _levelBox.AddChild(row);
            }
            _levelBox.AddChild(UiTheme.MakeButton("关闭", () => _levelOverlay.Visible = false, 90));
            _levelOverlay.Visible = true;
        }

        private void BuildSettings()
        {
            var (so, sb) = UiTheme.MakeDialog(this, "设置", 440);
            _settingsOverlay = so;
            var s = _gm.Settings;
            var full = new CheckBox { Text = "全屏", ButtonPressed = s.Fullscreen };
            var grid = new CheckBox { Text = "显示建造网格", ButtonPressed = s.ShowGrid };
            sb.AddChild(UiTheme.MakeLabel("变形显示放大系数", 14, UiTheme.Muted));
            var def = new HSlider { MinValue = 1, MaxValue = 100, Step = 1, Value = s.DeformationScale };
            sb.AddChild(def);
            sb.AddChild(full); sb.AddChild(grid);
            sb.AddChild(UiTheme.MakeButton("重置关卡进度", () => { _gm.Progress.BestScores.Clear(); _gm.SaveProgress(); }, 140));
            sb.AddChild(UiTheme.MakeButton("确定", () =>
            {
                s.Fullscreen = full.ButtonPressed; s.ShowGrid = grid.ButtonPressed; s.DeformationScale = def.Value;
                _gm.SaveSettings(); _gm.ApplySettings();
                _settingsOverlay.Visible = false;
            }, 90));
        }
    }
}
