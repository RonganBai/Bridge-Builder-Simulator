using Godot;

namespace BridgeArchitect3D.UI
{
    /// <summary>深色工程风格主题（代码生成，无外部资源）。字体使用系统中文字体。</summary>
    public static class UiTheme
    {
        public static readonly Color Bg = new(0.09f, 0.10f, 0.12f, 0.92f);
        public static readonly Color Panel = new(0.13f, 0.14f, 0.17f, 0.95f);
        public static readonly Color Accent = new(0.25f, 0.62f, 1.0f);
        public static readonly Color TestAccent = new(1.0f, 0.55f, 0.15f);
        public static readonly Color Text = new(0.92f, 0.93f, 0.95f);
        public static readonly Color Muted = new(0.62f, 0.65f, 0.70f);
        public static readonly Color Good = new(0.35f, 0.85f, 0.45f);
        public static readonly Color Bad = new(1.0f, 0.38f, 0.32f);
        public static readonly Color Warn = new(1.0f, 0.82f, 0.25f);

        private static Theme _theme;

        public static Theme Get()
        {
            if (_theme != null) return _theme;
            var t = new Theme();
            var font = new SystemFont { FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "PingFang SC", "Noto Sans CJK SC", "Sans-Serif" } };
            t.DefaultFont = font;
            t.DefaultFontSize = 15;

            t.SetStylebox("normal", "Button", Box(new Color(0.20f, 0.22f, 0.26f), 6));
            t.SetStylebox("hover", "Button", Box(new Color(0.27f, 0.30f, 0.36f), 6));
            t.SetStylebox("pressed", "Button", Box(Accent.Darkened(0.25f), 6, Accent));
            t.SetStylebox("disabled", "Button", Box(new Color(0.15f, 0.16f, 0.18f), 6));
            t.SetStylebox("focus", "Button", new StyleBoxEmpty());
            t.SetColor("font_color", "Button", Text);
            t.SetColor("font_pressed_color", "Button", Colors.White);
            t.SetColor("font_hover_color", "Button", Colors.White);
            t.SetColor("font_disabled_color", "Button", new Color(0.45f, 0.47f, 0.5f));
            t.SetColor("font_color", "Label", Text);
            t.SetStylebox("panel", "PanelContainer", Box(Panel, 8));
            t.SetStylebox("panel", "Panel", Box(Panel, 8));
            t.SetStylebox("background", "ProgressBar", Box(new Color(0.18f, 0.19f, 0.22f), 4));
            t.SetStylebox("fill", "ProgressBar", Box(Accent, 4));
            _theme = t;
            return t;
        }

        public static StyleBoxFlat Box(Color bg, int radius, Color? border = null, int margin = 8)
        {
            var sb = new StyleBoxFlat { BgColor = bg };
            sb.SetCornerRadiusAll(radius);
            sb.SetContentMarginAll(margin);
            if (border.HasValue) { sb.SetBorderWidthAll(2); sb.BorderColor = border.Value; }
            return sb;
        }

        public static Label MakeLabel(string text, int size = 15, Color? color = null)
        {
            var l = new Label { Text = text };
            l.AddThemeFontSizeOverride("font_size", size);
            if (color.HasValue) l.AddThemeColorOverride("font_color", color.Value);
            return l;
        }

        public static Button MakeButton(string text, System.Action onPressed, int minWidth = 0)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(minWidth, 34) };
            if (onPressed != null) b.Pressed += onPressed;
            return b;
        }

        /// <summary>居中的模态对话面板（带半透明遮罩，遮挡下方输入）。</summary>
        public static (Control overlay, VBoxContainer content) MakeDialog(Control parent, string title, float width = 520)
        {
            var overlay = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Stop };
            overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            parent.AddChild(overlay);
            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            overlay.AddChild(center);
            var panel = new PanelContainer { CustomMinimumSize = new Vector2(width, 0) };
            panel.AddThemeStyleboxOverride("panel", Box(new Color(0.12f, 0.13f, 0.16f, 0.98f), 10, Accent, 18));
            center.AddChild(panel);
            var vb = new VBoxContainer();
            vb.AddThemeConstantOverride("separation", 10);
            panel.AddChild(vb);
            vb.AddChild(MakeLabel(title, 24, Colors.White));
            overlay.Visible = false;
            return (overlay, vb);
        }
    }
}
