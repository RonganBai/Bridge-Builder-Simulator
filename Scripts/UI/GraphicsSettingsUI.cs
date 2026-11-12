using System;
using BridgeArchitect3D.Core;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>
	/// Graphics options shared by the main menu and the in-level settings: preset buttons, anti-aliasing,
	/// shadows, 3D render scale, brightness / contrast / saturation, V-Sync and frame cap. Build returns a callback that writes the chosen values into the settings.
	/// </summary>
	public static class GraphicsSettingsUI
	{
		private static readonly (string id, string name)[] Aa =
		{
			("off", "关"), ("fxaa", "FXAA（最省，略糊）"), ("smaa", "SMAA（清晰，省）"),
			("msaa2", "MSAA 2×"), ("msaa4", "MSAA 4×"), ("msaa8", "MSAA 8×（最贵）"), ("taa", "TAA（最平滑，运动略拖影）"),
		};
		private static readonly (string id, string name)[] Shadow = { ("off", "关"), ("low", "低"), ("high", "高") };
		private static readonly int[] Fps = { 0, 30, 60, 120, 144, 240 };

		private static HBoxContainer Row(Container parent, string label)
		{
			var row = new HBoxContainer();
			row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(90, 0) });
			parent.AddChild(row);
			return row;
		}

		private static OptionButton Options(Container parent, string label, (string id, string name)[] items, string current)
		{
			var o = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			for (int i = 0; i < items.Length; i++) { o.AddItem(items[i].name); if (items[i].id == current) o.Selected = i; }
			if (o.Selected < 0) o.Selected = 0;
			Row(parent, label).AddChild(o);
			return o;
		}

		private static HSlider Slider(Container parent, string label, double min, double max, double step, double value, Func<double, string> text)
		{
			var row = Row(parent, label);
			var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(140, 0) };
			var v = UiTheme.MakeLabel(text(value), 13); v.CustomMinimumSize = new Vector2(48, 0);
			s.ValueChanged += x => v.Text = text(x);
			row.AddChild(s); row.AddChild(v);
			return s;
		}

		public static Action Build(Container parent, GameSettings s, bool mainMenu)
		{
			s.MigrateGraphics();
			var presets = Row(parent, "画质预设");
			var aa = Options(parent, "抗锯齿", Aa, s.Antialiasing);
			var sh = Options(parent, "阴影", Shadow, s.Shadows);
			var scale = Slider(parent, "3D 渲染比例", 0.5, 1.0, 0.05, s.RenderScale, x => $"{x * 100:0}%");
			scale.TooltipText = "低于 100% 时 3D 画面按更低分辨率渲染再放大（FSR），界面文字不受影响";
			var bright = Slider(parent, "亮度", 0.5, 1.5, 0.05, s.Brightness, x => $"{x * 100:0}%");
			var contrast = Slider(parent, "对比度", 0.5, 1.5, 0.05, s.Contrast, x => $"{x * 100:0}%");
			var sat = Slider(parent, "饱和度", 0.0, 2.0, 0.05, s.Saturation, x => $"{x * 100:0}%");
			var vsync = new CheckBox { Text = "垂直同步（防画面撕裂）", ButtonPressed = s.VSync };
			parent.AddChild(vsync);
			var fps = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			foreach (int f in Fps) fps.AddItem(f == 0 ? "不限" : $"{f} FPS");
			fps.Selected = Math.Max(0, Array.IndexOf(Fps, s.MaxFps));
			Row(parent, "帧率上限").AddChild(fps);

			void Preset(string name, string id)
			{
				presets.AddChild(UiTheme.MakeButton(name, () =>
				{
					var tmp = new GameSettings(); tmp.ApplyPreset(id);
					aa.Selected = Array.FindIndex(Aa, a => a.id == tmp.Antialiasing);
					sh.Selected = Array.FindIndex(Shadow, a => a.id == tmp.Shadows);
					scale.Value = tmp.RenderScale;
				}, 64));
			}
			Preset("低", "low"); Preset("中", "medium"); Preset("高", "high");
			presets.AddChild(UiTheme.MakeButton("颜色复位", () => { bright.Value = 1; contrast.Value = 1; sat.Value = 1; }, 84));

			return () =>
			{
				s.Antialiasing = Aa[Math.Max(0, aa.Selected)].id;
				s.Shadows = Shadow[Math.Max(0, sh.Selected)].id;
				s.RenderScale = scale.Value;
				s.LowQuality = s.Antialiasing == "off" && s.Shadows == "off" && s.RenderScale <= 0.76;
				s.Brightness = bright.Value; s.Contrast = contrast.Value; s.Saturation = sat.Value;
				s.VSync = vsync.ButtonPressed;
				s.MaxFps = Fps[Math.Max(0, fps.Selected)];
			};
		}
	}
}
