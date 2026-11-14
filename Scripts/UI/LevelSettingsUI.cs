using BridgeArchitect3D.Core;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>
	/// In-game "level settings": the same editor as the main-menu level designer (<see cref="LevelEditorPanel"/>).
	/// Saving restarts the level with the new data — a custom level is written back to its own file (so the designer
	/// shows the change), a built-in level gets a per-player override that "restore defaults" removes.
	/// "Save as custom level" keeps the edits as a new custom level, which then appears in the level designer.
	/// </summary>
	public static class LevelSettingsUI
	{
		public static void Open(Control parent, GameManager gm, LevelDefinition current, System.Action beforeRestart)
		{
			bool custom = CustomLevelBuilder.IsCustom(current.Id), overridden = gm.LevelOverridden(current.Id);
			var (overlay, content) = UiTheme.MakeDialog(parent, Loc.T("关卡设置"), 1060);
			overlay.VisibilityChanged += () => { if (!overlay.Visible) overlay.QueueFree(); };
			var panel = LevelEditorPanel.Create(overlay, content, gm.Db);
			panel.Load(current);

			var a = panel.Actions;
			var ok = UiTheme.MakeButton(Loc.T("保存并重新开始本关"), () =>
			{
				var errs = panel.Problems();
				if (errs.Count > 0) { panel.ShowErrors(Loc.T("无法保存：\n"), errs); return; }
				beforeRestart();
				if (!gm.SaveLevelSettings(panel.Level, out var err)) panel.Status("✘ " + err);
			}, 190);
			UiTheme.Primary(ok); a.AddChild(ok);
			a.AddChild(UiTheme.MakeButton(Loc.T("另存为自定义关卡"), () =>
			{
				string id = gm.SaveAsCustomLevel(panel.Level, out var err);
				if (id == null) { panel.Status("✘ " + err); return; }
				panel.Status(Loc.F("已另存为自定义关卡“{0}”，可在主菜单“关卡设计”里继续编辑", (panel.Level.Name)), true);
			}, 160));
			var reset = UiTheme.MakeButton(Loc.T("恢复默认"), () => { overlay.Hide(); beforeRestart(); gm.RestoreLevelDefaults(current.Id); }, 100);
			reset.Disabled = !overridden;
			a.AddChild(reset);
			a.AddChild(UiTheme.MakeButton(Loc.T("取消"), () => overlay.Hide(), 80));

			string note = custom
				? Loc.T("这是自定义关卡：保存后直接写回关卡文件（与主菜单“关卡设计”是同一份），本关重新开始，方案存档不变。")
				: (overridden ? Loc.T("本关正在使用你修改过的设置。") : Loc.T("本关使用内置设置。"))
				  + Loc.T("保存后替换本关的内置数据（存在本机，方案存档不变），本关重新开始；“恢复默认”还原内置数据。");
			var noteLabel = UiTheme.MakeLabel(note, 12, UiTheme.Muted);
			noteLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart; noteLabel.CustomMinimumSize = new Vector2(400, 0);
			panel.Extra.AddChild(noteLabel);
			overlay.Show();
		}
	}
}
