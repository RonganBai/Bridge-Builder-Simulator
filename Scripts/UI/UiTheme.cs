using Godot;

namespace BridgeArchitect3D.UI
{
    /// <summary>Shared visual tokens and controls for all screens.</summary>
    public static class UiTheme
    {
        public static readonly Color Bg=new("11161f"), Panel=new(0.085f,0.11f,0.15f,0.97f), Surface=new("202a38"), Border=new("303d4e");
        public static readonly Color Accent=new("91bbeb"), TestAccent=new("e3b67b"), Text=new("e8edf4"), Muted=new("a3afbf");
        public static readonly Color Good=new("8ecdb1"), Bad=new("f09797"), Warn=new("e3c384");
        private static Theme _theme;
        public static Theme Get()
        {
            if(_theme!=null)return _theme;
            var t=new Theme {DefaultFont=new SystemFont {FontNames=new[]{"Microsoft YaHei UI","Microsoft YaHei","PingFang SC","Noto Sans CJK SC","Sans-Serif"}},DefaultFontSize=14};
            foreach(string type in new[]{"Button","OptionButton","MenuButton"})
            {
                t.SetStylebox("normal",type,Box(Surface,6,Border));
                t.SetStylebox("hover",type,Box(new Color("2a384b"),6,new Color("526984")));
                t.SetStylebox("pressed",type,Box(new Color("2c435e"),6,Accent));
                t.SetStylebox("disabled",type,Box(new Color("19212c"),6,new Color("27313f")));
                var focus=Box(Colors.Transparent,6,Accent,0);focus.DrawCenter=false;t.SetStylebox("focus",type,focus);
                t.SetColor("font_color",type,Text);t.SetColor("font_hover_color",type,Colors.White);t.SetColor("font_pressed_color",type,Colors.White);
                t.SetColor("font_disabled_color",type,new Color("778395"));t.SetConstant("h_separation",type,8);
            }
            t.SetColor("font_color","Label",Text);
            foreach(string type in new[]{"RichTextLabel","TextEdit","LineEdit"})
            {
                t.SetColor(type=="RichTextLabel"?"default_color":"font_color",type,Text);
                t.SetColor("font_placeholder_color",type,Muted);t.SetColor("caret_color",type,Accent);t.SetColor("selection_color",type,new Color("385674"));
                t.SetFontSize(type=="RichTextLabel"?"normal_font_size":"font_size",type,14);
            }
            foreach(string type in new[]{"TextEdit","LineEdit"})
            {
                t.SetStylebox("normal",type,Box(Bg,5,Border));t.SetStylebox("focus",type,Box(Bg,5,Accent));t.SetStylebox("read_only",type,Box(Panel,5,Border));
            }
            foreach(string type in new[]{"Panel","PanelContainer","PopupMenu","TooltipPanel"})t.SetStylebox("panel",type,Box(Panel,7,Border,type=="PanelContainer"?12:8));
            t.SetStylebox("hover","PopupMenu",Box(Surface,4,null,6));t.SetColor("font_color","PopupMenu",Text);t.SetColor("font_hover_color","PopupMenu",Colors.White);
            t.SetColor("font_disabled_color","PopupMenu",Muted.Darkened(0.25f));t.SetConstant("v_separation","PopupMenu",12);
            t.SetColor("font_color","TooltipLabel",Text);t.SetFontSize("font_size","TooltipLabel",13);
            t.SetStylebox("background","ProgressBar",Box(Surface,3,null,3));t.SetStylebox("fill","ProgressBar",Box(Accent,3,null,3));
            foreach(string type in new[]{"HSeparator","VSeparator"})t.SetStylebox("separator",type,new StyleBoxLine {Color=Border,Thickness=1,Vertical=type=="VSeparator"});
            foreach(string type in new[]{"HBoxContainer","VBoxContainer"})t.SetConstant("separation",type,8);
            foreach(string type in new[]{"HScrollBar","VScrollBar"})
            {
                t.SetStylebox("scroll",type,Box(Bg,3,null,4));t.SetStylebox("grabber",type,Box(Border.Lightened(0.12f),3,null,4));
                t.SetStylebox("grabber_highlight",type,Box(Muted,3,null,4));t.SetStylebox("grabber_pressed",type,Box(Accent,3,null,4));
            }
            t.SetStylebox("slider","HSlider",Box(Border,2,null,2));t.SetStylebox("grabber_area","HSlider",Box(Accent.Darkened(0.25f),2,null,2));
            t.SetStylebox("grabber_area_highlight","HSlider",Box(Accent,2,null,2));
            foreach(string key in new[]{"grabber","grabber_highlight"})t.SetIcon(key,"HSlider",Svg("<circle cx='7' cy='7' r='5' fill='#91bbeb'/>",14,14));
            t.SetIcon("grabber_disabled","HSlider",Svg("<circle cx='7' cy='7' r='5' fill='#536174'/>",14,14));
            foreach(bool selected in new[]{false,true})
            {
                string state=selected?"checked":"unchecked";
                var icon=Svg(selected?"<rect x='1' y='1' width='14' height='14' rx='3' fill='#91bbeb'/><path d='M4 8l3 3 5-6' fill='none' stroke='#11161f' stroke-width='2'/>":"<rect x='1' y='1' width='14' height='14' rx='3' fill='#11161f' stroke='#73849a'/>",16,16);
                t.SetIcon(state,"CheckBox",icon);t.SetIcon(state+"_disabled","CheckBox",icon);
                var toggle=Svg($"<rect x='1' y='2' width='28' height='14' rx='7' fill='{(selected?"#91bbeb":"#3a485c")}'/><circle cx='{(selected?22:8)}' cy='9' r='5' fill='{(selected?"#11161f":"#c5cedb")}'/>",30,18);
                t.SetIcon(selected?"on":"off","CheckButton",toggle);t.SetIcon(selected?"on_disabled":"off_disabled","CheckButton",toggle);
            }
            foreach(string type in new[]{"CheckBox","CheckButton"})
            {
                t.SetColor("font_color",type,Text);t.SetColor("font_hover_color",type,Colors.White);
                foreach(string state in new[]{"normal","pressed","disabled"})t.SetStylebox(state,type,new StyleBoxEmpty());
                t.SetStylebox("hover",type,Box(Surface,4,null,4));
            }
            _theme=t;return t;
        }
        private static Texture2D Svg(string content,int width,int height)
        {
            var image=new Image();image.LoadSvgFromString($"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}'>{content}</svg>");return ImageTexture.CreateFromImage(image);
        }
        public static StyleBoxFlat Box(Color bg,int radius,Color? border=null,int margin=8)
        {
            var box=new StyleBoxFlat {BgColor=bg};box.SetCornerRadiusAll(radius);box.SetContentMarginAll(margin);
            if(border.HasValue){box.SetBorderWidthAll(1);box.BorderColor=border.Value;}return box;
        }
        public static void Primary(Button button)
        {
            button.AddThemeStyleboxOverride("normal",Box(Accent,6,null,10));button.AddThemeStyleboxOverride("hover",Box(Accent.Lightened(0.12f),6,null,10));
            button.AddThemeStyleboxOverride("pressed",Box(Accent.Darkened(0.13f),6,null,10));
            foreach(string key in new[]{"font_color","font_hover_color","font_pressed_color"})button.AddThemeColorOverride(key,Bg);
        }
        public static Label MakeLabel(string text,int size=14,Color? color=null)
        {
            var label=new Label {Text=text};label.AddThemeFontSizeOverride("font_size",size);if(color.HasValue)label.AddThemeColorOverride("font_color",color.Value);return label;
        }
        public static Button MakeButton(string text,System.Action onPressed,int minWidth=0)
        {
            var button=new Button {Text=text,CustomMinimumSize=new Vector2(minWidth,34),SizeFlagsVertical=Control.SizeFlags.ShrinkCenter};if(onPressed!=null)button.Pressed+=onPressed;return button;
        }
        /// <summary>Scrollable dialogs fit the viewport and retain keyboard focus indicators.</summary>
        public static (Control overlay,VBoxContainer content) MakeDialog(Control parent,string title,float width=520)
        {
            var overlay=new ColorRect {Color=new Color(0.025f,0.04f,0.065f,0.78f),MouseFilter=Control.MouseFilterEnum.Stop,FocusMode=Control.FocusModeEnum.All,Theme=Get()};
            parent.AddChild(overlay);overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var center=new CenterContainer {MouseFilter=Control.MouseFilterEnum.Ignore};overlay.AddChild(center);center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var panel=new PanelContainer();panel.AddThemeStyleboxOverride("panel",Box(Panel,8,Border,20));center.AddChild(panel);
            var shell=new VBoxContainer();shell.AddThemeConstantOverride("separation",12);panel.AddChild(shell);
            var heading=new HBoxContainer();shell.AddChild(heading);
            var titleLabel=MakeLabel(title,22);titleLabel.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;heading.AddChild(titleLabel);
            heading.AddChild(MakeButton("关闭",()=>overlay.Hide(),56));
            var scroll=new ScrollContainer {FollowFocus=true};shell.AddChild(scroll);
            var content=new VBoxContainer {SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};content.AddThemeConstantOverride("separation",12);scroll.AddChild(content);
            // Callers retain child zero when rebuilding dialog bodies.
            var titleSlot=MakeLabel(title,22);titleSlot.Hide();content.AddChild(titleSlot);
            bool queued=false;
            void Layout()
            {
                if(!GodotObject.IsInstanceValid(panel) || !overlay.IsInsideTree())return;
                var view=overlay.GetViewportRect().Size;panel.CustomMinimumSize=new Vector2(Mathf.Clamp(width,240,Mathf.Max(240,view.X-48)),0);
                scroll.CustomMinimumSize=new Vector2(0,Mathf.Min(content.GetCombinedMinimumSize().Y,Mathf.Max(120,view.Y-144)));
            }
            void Schedule(){if(queued)return;queued=true;Callable.From(()=>{queued=false;Layout();}).CallDeferred();}
            content.MinimumSizeChanged+=Schedule;overlay.Resized+=Schedule;
            overlay.VisibilityChanged+=()=>{if(overlay.Visible){Schedule();overlay.GrabFocus();}};
            overlay.GuiInput+=e=>{if(e is InputEventKey key && key.Pressed && key.Keycode==Key.Escape){overlay.Hide();overlay.AcceptEvent();}};
            Schedule();overlay.Hide();return(overlay,content);
        }
    }
}
