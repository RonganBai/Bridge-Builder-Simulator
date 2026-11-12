using System;
using System.Collections.Generic;
using BridgeArchitect3D.Core;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>
	/// Window mode (windowed / fullscreen / borderless) and resolution pickers, shared by the main menu and the
	/// in-level settings. Build returns a callback that writes the picked values into the settings.
	/// </summary>
	public static class DisplaySettingsUI
	{
		private static readonly (string id, string name)[] Modes =
		{
			(DisplayMode.Windowed, "窗口化"), (DisplayMode.Fullscreen, "全屏"), (DisplayMode.Borderless, "无边框窗口（全屏大小）"),
		};

		public static Action Build(Container parent, GameSettings s)
		{
			var modeRow = new HBoxContainer();
			modeRow.AddChild(new Label { Text = "显示模式", CustomMinimumSize = new Vector2(90, 0) });
			var mode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			for (int i = 0; i < Modes.Length; i++) { mode.AddItem(Modes[i].name); if (Modes[i].id == s.Mode) mode.Selected = i; }
			modeRow.AddChild(mode); parent.AddChild(modeRow);

			var resRow = new HBoxContainer();
			resRow.AddChild(new Label { Text = "分辨率", CustomMinimumSize = new Vector2(90, 0) });
			var res = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			var list = new List<string> { "" };
			res.AddItem("默认 / 屏幕原生");
			foreach (var r in DisplayMode.Choices()) { list.Add(r); res.AddItem(r.Replace("x", " × ")); }
			res.Selected = Math.Max(0, list.IndexOf(s.Resolution ?? ""));
			resRow.AddChild(res); parent.AddChild(resRow);
			var hint = UiTheme.MakeLabel("", 12, UiTheme.Muted);
			hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			// Run from the editor with the game embedded in its Game tab, the window cannot change mode or size
			bool embedded = Engine.IsEmbeddedInEditor();
			if (embedded) { hint.AddThemeColorOverride("font_color", UiTheme.Warn); }
			void Hint() => hint.Text = embedded
				? "当前在编辑器“游戏”标签内嵌运行：显示模式和窗口大小由编辑器控制，切换不会生效。请在“游戏”标签里关闭“嵌入游戏”，或导出后运行"
				: Modes[Math.Max(0, mode.Selected)].id == DisplayMode.Windowed
				? "窗口化：分辨率 = 窗口大小"
				: "全屏 / 无边框：分辨率决定 3D 画面精度，界面始终清晰";
			mode.ItemSelected += _ => Hint(); Hint();
			parent.AddChild(hint);

			return () =>
			{
				s.Mode = Modes[Math.Max(0, mode.Selected)].id;
				s.Resolution = list[Math.Max(0, res.Selected)];
			};
		}
	}
}
