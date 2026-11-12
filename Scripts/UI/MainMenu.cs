using BridgeArchitect3D.Core;
using Godot;

namespace BridgeArchitect3D.UI
{
	/// <summary>A quiet menu over a seamless harbor video, with a live-scene fallback.</summary>
	public partial class MainMenu : Control
	{
		private GameManager _gm;
		private Control _levelOverlay,_settingsOverlay;
		private VBoxContainer _levelBox;
		private LevelDesigner _designer;
		private PanelContainer _navigation;

		public override void _Ready()
		{
			_gm=GameManager.Instance;Theme=UiTheme.Get();SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			var fallback=new ColorRect {Color=UiTheme.Bg,MouseFilter=MouseFilterEnum.Ignore};
			AddChild(fallback);fallback.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			if(_gm?.Db!=null)
			{
				// Real-time 3D scene (the pre-rendered video background is no longer used)
				var backdrop=new MainMenuBackdrop {Name="MenuBackdrop"};AddChild(backdrop);backdrop.Setup(_gm.Db);
				fallback.Hide();
			}
			var shade=new ColorRect {MouseFilter=MouseFilterEnum.Ignore};
			shade.Material=new ShaderMaterial {Shader=new Shader {Code="shader_type canvas_item; void fragment(){float a=mix(0.86,0.12,smoothstep(0.0,0.86,UV.x));COLOR=vec4(0.035,0.05,0.075,a);}"}};
			AddChild(shade);shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			_navigation=new PanelContainer();_navigation.AddThemeStyleboxOverride("panel",UiTheme.Box(new Color(0.06f,0.08f,0.115f,0.91f),8,UiTheme.Border,24));AddChild(_navigation);
			var content=new VBoxContainer();content.AddThemeConstantOverride("separation",10);_navigation.AddChild(content);
			content.AddChild(UiTheme.MakeLabel("BRIDGE ARCHITECT 3D",12,UiTheme.Accent));
			content.AddChild(UiTheme.MakeLabel("桥梁建筑师",32));
			content.AddChild(UiTheme.MakeLabel("设计你的桥梁，让每一次通行更稳妥。",13,UiTheme.Muted));
			content.AddChild(new Control {CustomMinimumSize=new Vector2(0,14)});
			if(_gm?.Db==null)
			{
				var error=UiTheme.MakeLabel(_gm?.LoadError??"游戏数据未加载",14,UiTheme.Bad);error.AutowrapMode=TextServer.AutowrapMode.WordSmart;content.AddChild(error);
				content.AddChild(Action("退出游戏",()=>GetTree().Quit()));return;
			}
			var start=Action("开始游戏",()=>_gm.StartLevel(_gm.Db.Levels[0].Id,false));UiTheme.Primary(start);content.AddChild(start);
			string last=_gm.Progress.LastLevelId;var resume=Action("继续游戏",()=>_gm.StartLevel(last,true));
			resume.Disabled=string.IsNullOrEmpty(last)||_gm.ResolveLevel(last)==null||!SaveManager.HasSave(last);
			resume.TooltipText=resume.Disabled?"还没有可继续的存档":"继续："+_gm.ResolveLevel(last).Name;content.AddChild(resume);
			content.AddChild(Action("选择关卡",ShowLevels));
			content.AddChild(Action("关卡设计",()=>{_designer??=LevelDesigner.Create(this,_gm);_designer.Open();}));
			content.AddChild(new HSeparator());
			var secondary=new HBoxContainer();content.AddChild(secondary);
			var settings=Action("设置",()=>_settingsOverlay.Show());settings.SizeFlagsHorizontal=SizeFlags.ExpandFill;secondary.AddChild(settings);
			var quit=Action("退出",()=>GetTree().Quit());quit.SizeFlagsHorizontal=SizeFlags.ExpandFill;secondary.AddChild(quit);
			var caption=UiTheme.MakeLabel("河谷 · 通车时刻",12,new Color(0.9f,0.94f,1,0.7f));AddChild(caption);
			caption.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);caption.OffsetLeft=-180;caption.OffsetTop=-40;caption.OffsetRight=-28;caption.OffsetBottom=-16;caption.HorizontalAlignment=HorizontalAlignment.Right;
			(_levelOverlay,_levelBox)=UiTheme.MakeDialog(this,"选择关卡",760);BuildSettings();
			Resized+=LayoutNavigation;LayoutNavigation();
			content.MinimumSizeChanged+=LayoutNavigation;
			if(_gm.ShowLevelSelectOnMenu){_gm.ShowLevelSelectOnMenu=false;ShowLevels();}
		}
		private void LayoutNavigation()
		{
			float width=Mathf.Min(360,Mathf.Max(280,Size.X-40));float height=_navigation.GetCombinedMinimumSize().Y;
			_navigation.Size=new Vector2(width,height);
			_navigation.Position=new Vector2(Size.X<720?(Size.X-width)/2:Mathf.Clamp(Size.X*0.055f,28,96),Mathf.Max(20,(Size.Y-height)/2));
		}
		private static Button Action(string text,System.Action action)
		{
			var button=UiTheme.MakeButton(text,action);button.CustomMinimumSize=new Vector2(0,42);return button;
		}
		private void ShowLevels()
		{
			foreach(var child in _levelBox.GetChildren())if(child.GetIndex()>0){_levelBox.RemoveChild(child);child.QueueFree();}
			_levelBox.AddChild(UiTheme.MakeLabel("选择挑战，或读取已保存的桥梁方案。",13,UiTheme.Muted));
			for(int i=0;i<_gm.Db.Levels.Count;i++)
			{
				var level=_gm.Db.Levels[i];bool unlocked=_gm.IsUnlocked(i);int best=_gm.BestScoreFor(level.Id);
				var card=new PanelContainer();card.AddThemeStyleboxOverride("panel",UiTheme.Box(UiTheme.Surface,5,null,12));_levelBox.AddChild(card);
				var row=new HBoxContainer();row.AddThemeConstantOverride("separation",14);card.AddChild(row);
				row.AddChild(UiTheme.MakeLabel($"{i+1:00}",16,unlocked?UiTheme.Accent:UiTheme.Muted));
				var info=new VBoxContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill};info.AddThemeConstantOverride("separation",3);
				var title=UiTheme.MakeLabel(level.Name,15,unlocked?UiTheme.Text:UiTheme.Muted);title.ClipText=true;title.TooltipText=level.Name;info.AddChild(title);
				var description=UiTheme.MakeLabel($"河宽 {level.Terrain.RiverWidth:0} m  ·  预算 ${level.Budget:N0}  ·  "+(best>0?$"最高分 {best:N0}":"未通关"),12,UiTheme.Muted);description.ClipText=true;info.AddChild(description);row.AddChild(info);
				string id=level.Id;var play=UiTheme.MakeButton(unlocked?"进入":"未解锁",()=>_gm.StartLevel(id,false),66);play.Disabled=!unlocked;row.AddChild(play);
				var load=UiTheme.MakeButton("读档",()=>_gm.StartLevel(id,true),56);load.Disabled=!unlocked||!SaveManager.HasSave(id);row.AddChild(load);
			}
			_levelBox.AddChild(UiTheme.MakeButton("返回",()=>_levelOverlay.Hide(),90));_levelOverlay.Show();
		}
		private void BuildSettings()
		{
			VBoxContainer content;(_settingsOverlay,content)=UiTheme.MakeDialog(this,"设置",440);var settings=_gm.Settings;
			content.AddChild(UiTheme.MakeLabel("显示",13,UiTheme.Muted));
			var commitDisplay=DisplaySettingsUI.Build(content,settings);var grid=new CheckBox {Text="显示建造网格",ButtonPressed=settings.ShowGrid};content.AddChild(grid);
			content.AddChild(new HSeparator());content.AddChild(UiTheme.MakeLabel("画面",13,UiTheme.Muted));
			var commitGraphics=GraphicsSettingsUI.Build(content,settings,true);
			content.AddChild(new HSeparator());content.AddChild(UiTheme.MakeLabel("静态桥梁变形显示",13,UiTheme.Muted));
			var row=new HBoxContainer();var value=UiTheme.MakeLabel($"{settings.DeformationScale:0}×",14);
			var deformation=new HSlider {MinValue=1,MaxValue=100,Step=1,Value=settings.DeformationScale,SizeFlagsHorizontal=SizeFlags.ExpandFill};deformation.ValueChanged+=v=>value.Text=$"{v:0}×";
			row.AddChild(deformation);row.AddChild(value);content.AddChild(row);content.AddChild(new HSeparator());
			var reset=UiTheme.MakeButton("重置关卡进度",null);reset.AddThemeColorOverride("font_color",UiTheme.Bad);
			reset.Pressed+=()=>{if(reset.Text!="确认重置进度"){reset.Text="确认重置进度";return;}_gm.Progress.BestScores.Clear();_gm.SaveProgress();reset.Text="进度已重置";};content.AddChild(reset);
			var done=UiTheme.MakeButton("保存设置",()=>{commitDisplay();settings.ShowGrid=grid.ButtonPressed;commitGraphics();settings.DeformationScale=deformation.Value;_gm.SaveSettings();_gm.ApplySettings();_settingsOverlay.Hide();});UiTheme.Primary(done);content.AddChild(done);
		}
	}
}
