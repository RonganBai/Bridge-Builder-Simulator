using BridgeArchitect3D.Core;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
    /// <summary>Resolution-specific playback with a hidden decoder during stream changes.</summary>
    public partial class MainMenuVideo : Control
    {
        public const string SourcePath="res://Media/homepage-loop-forward.ogv";
        private VideoStreamPlayer _player,_pending;
        private MenuVideoProfile _activeProfile,_pendingProfile;
        private GameManager _manager;
        private bool _dirty;
        private double _debounce,_pendingWait;
        private int _pendingFrames;
        public string ActiveResolution=>_activeProfile?.Resolution;
        public string ActivePath=>_activeProfile?.Path;

        public static bool TryCreate(Control parent)
        {
            var target=TargetResolution(parent);
            var profile=MenuVideoProfiles.Select(target.X,target.Y,FileAccess.FileExists);
            if(profile==null)return false;
            var stream=ResourceLoader.Load<VideoStream>(profile.Path);
            if(stream==null)return false;
            var backdrop=new MainMenuVideo {Name="MenuVideo",ClipContents=true,MouseFilter=MouseFilterEnum.Ignore,_manager=GameManager.Instance,_activeProfile=profile};
            parent.AddChild(backdrop);backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            backdrop._player=MakePlayer(stream,"HarborLoop");backdrop.AddChild(backdrop._player);
            backdrop.Resized+=backdrop.OnResized;
            if(backdrop._manager!=null)backdrop._manager.DisplaySettingsChanged+=backdrop.RequestRefresh;
            UpdateColor();backdrop.LayoutPlayers();return true;
        }

        // The video is drawn at the window pixel size: a larger video would be shrunk without mipmaps and
        // shimmer (jagged edges), so the window size is the upper bound; a lower configured resolution still
        // picks a smaller (cheaper to decode) video.
        private static Vector2I TargetResolution(Control control)
        {
            var actual=control.GetWindow().Size;
            var target=new Vector2I(Mathf.Max(1,actual.X),Mathf.Max(1,actual.Y));
            if(DisplayMode.Parse(GameManager.Instance?.Settings?.Resolution) is { } selected && selected.X<=target.X && selected.Y<=target.Y)target=selected;
            return target;
        }

        // Brightness / contrast / saturation from the graphics settings, shared by every menu video player
        private static readonly ShaderMaterial ColorMaterial=new() {Shader=new Shader {Code=
            "shader_type canvas_item; uniform float brightness=1.0; uniform float contrast=1.0; uniform float saturation=1.0;"+
            "void fragment(){vec4 c=texture(TEXTURE,UV)*COLOR;vec3 rgb=c.rgb*brightness;rgb=(rgb-0.5)*contrast+0.5;"+
            "float l=dot(rgb,vec3(0.299,0.587,0.114));rgb=mix(vec3(l),rgb,saturation);COLOR=vec4(clamp(rgb,0.0,1.0),c.a);}"}};

        private static void UpdateColor()
        {
            var g=GameManager.Instance?.Settings;if(g==null)return;
            ColorMaterial.SetShaderParameter("brightness",(float)g.Brightness);ColorMaterial.SetShaderParameter("contrast",(float)g.Contrast);ColorMaterial.SetShaderParameter("saturation",(float)g.Saturation);
        }

        private static VideoStreamPlayer MakePlayer(VideoStream stream,string name)=>new()
        {Name=name,Stream=stream,Expand=true,Loop=true,Autoplay=true,VolumeDb=-80,BufferingMsec=500,MouseFilter=MouseFilterEnum.Ignore,Material=ColorMaterial};

        private void OnResized(){LayoutPlayers();RequestRefresh();}
        private void RequestRefresh(){UpdateColor();_dirty=true;_debounce=0.15;}

        public override void _Process(double delta)
        {
            if(_dirty && (_debounce-=delta)<=0){_dirty=false;RefreshStream();}
            if(_pending==null)return;
            _pendingWait+=delta;_pendingFrames++;
            var texture=_pending.GetVideoTexture();
            if(_pendingFrames>=2 && texture!=null && texture.GetWidth()>0 && _pending.StreamPosition>0)
            {
                // All profiles share one timeline, including the bridge pose and queue positions.
                _pending.StreamPosition=_player.StreamPosition;
                var old=_player;_player=_pending;_activeProfile=_pendingProfile;_pending=null;_pendingProfile=null;
                _player.Modulate=Colors.White;old.Hide();old.Stop();old.QueueFree();LayoutPlayers();
            }
            else if(_pendingWait>5)
            {
                GD.PushWarning("Menu video decoder did not become ready: "+_pendingProfile.Path);
                DiscardPending();
            }
        }

        private void RefreshStream()
        {
            var target=TargetResolution(this);
            var profile=MenuVideoProfiles.Select(target.X,target.Y,FileAccess.FileExists);
            if(profile==null)return;
            if(profile.Path==_activeProfile.Path){DiscardPending();return;}
            if(profile.Path==_pendingProfile?.Path)return;
            DiscardPending();var stream=ResourceLoader.Load<VideoStream>(profile.Path);
            if(stream==null)return;
            _pendingProfile=profile;_pending=MakePlayer(stream,"HarborLoopLoading");_pending.Modulate=new Color(1,1,1,0);
            AddChild(_pending);_pending.StreamPosition=_player.StreamPosition;_pendingWait=0;_pendingFrames=0;LayoutPlayers();
        }

        private void DiscardPending()
        {
            if(_pending==null)return;
            _pending.Stop();_pending.QueueFree();_pending=null;_pendingProfile=null;
        }

        private void LayoutPlayers()
        {
            void Layout(VideoStreamPlayer player,MenuVideoProfile profile)
            {
                if(player==null || profile==null)return;
                float aspect=profile.Width/(float)profile.Height,width=Mathf.Max(Size.X,Size.Y*aspect);
                player.Size=new Vector2(width,width/aspect);player.Position=(Size-player.Size)/2;
            }
            Layout(_player,_activeProfile);Layout(_pending,_pendingProfile);
        }

        public override void _ExitTree()
        {
            if(IsInstanceValid(_manager))_manager.DisplaySettingsChanged-=RequestRefresh;
        }
    }
}
