using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using Godot;

namespace BridgeArchitect3D.UI
{
    public partial class GameHUD
    {
        private VBoxContainer _liveMetrics;
        private RichTextLabel _liveHeading;
        private Button _showPropertyDetails;
        private readonly List<(VBoxContainer root,Label title,Label value,ProgressBar bar,StyleBoxFlat fill)> _metricRows=new();
        private void BuildLiveMetrics(VBoxContainer parent)
        {
            _liveHeading=new RichTextLabel{BbcodeEnabled=true,FitContent=true,ScrollActive=false,CustomMinimumSize=new(250,0),Visible=false};parent.AddChild(_liveHeading);
            _liveMetrics=new VBoxContainer{Visible=false,SizeFlagsHorizontal=SizeFlags.ExpandFill};_liveMetrics.AddThemeConstantOverride("separation",7);parent.AddChild(_liveMetrics);
            _showPropertyDetails=UiTheme.MakeButton(Loc.T("展开数值明细与说明"),()=>{_props.Visible=!_props.Visible;_showPropertyDetails.Text=_props.Visible?Loc.T("收起数值明细与说明"):Loc.T("展开数值明细与说明");},240);
            _showPropertyDetails.Visible=false;parent.AddChild(_showPropertyDetails);
        }
        private void SetLiveMetrics(IReadOnlyList<LivePropertyMetric> rows,string bbcode)
        {
            bool active=rows is {Count:>0};_liveMetrics.Visible=_liveHeading.Visible=_showPropertyDetails.Visible=active;
            if(!active){_props.Visible=true;_showPropertyDetails.RemoveMeta("active");_showPropertyDetails.Text=Loc.T("展开数值明细与说明");return;}
            _liveHeading.Text=string.Join("\n",(bbcode??"").Split('\n').Take(2))+"\n[color=#a3afbf]"+Loc.T("蓝条：相对性能 · 彩条：限值占用")+"[/color]";
            if(!_showPropertyDetails.HasMeta("active")){_props.Visible=false;_showPropertyDetails.SetMeta("active",true);}
            for(int i=_metricRows.Count;i<rows.Count;i++)
            {
                var root=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};root.AddThemeConstantOverride("separation",2);_liveMetrics.AddChild(root);
                var title=UiTheme.MakeLabel("",12);title.ClipText=true;root.AddChild(title);
                var bar=new ProgressBar{MinValue=0,MaxValue=100,ShowPercentage=false,CustomMinimumSize=new(0,9),MouseFilter=MouseFilterEnum.Ignore};root.AddChild(bar);
                var fill=UiTheme.Box(UiTheme.Accent,2,null,0);bar.AddThemeStyleboxOverride("fill",fill);
                var value=UiTheme.MakeLabel("",12,UiTheme.Muted);value.ClipText=true;root.AddChild(value);
                _metricRows.Add((root,title,value,bar,fill));
            }
            for(int i=0;i<_metricRows.Count;i++)
            {
                var r=_metricRows[i];r.root.Visible=i<rows.Count;if(i>=rows.Count)continue;var m=rows[i];
                r.bar.Visible=r.value.Visible=!m.Header;r.title.Modulate=m.Header?UiTheme.Accent:Colors.White;
                r.title.Text=m.Header?"— "+m.Label+" —":m.Label;r.value.Text=m.Readout+(m.Risk && !m.Readout.Contains("%")?$" · {m.Ratio*100:0.#}%":"");
                r.root.TooltipText=m.Label+"\n"+r.value.Text+(m.Risk?"\n"+Loc.T("100% 为上限；超过上限时读数继续增加。"):"\n"+Loc.T("相对数值比较，条长不表示安全程度。"));
                r.bar.Value=m.Fill*100;r.fill.BgColor=!m.Risk?UiTheme.Accent:m.Band==3?UiTheme.Bad:m.Band==2?new Color("ef8a2e"):m.Band==1?UiTheme.Warn:UiTheme.Good;
            }
        }
        public void ShowMemberProperties(BridgeMember member,GameDatabase db,StructuralModel model,MemberStatus[] statuses,bool stable,string loadCase)
        {
            ShowProperties(MemberProperties(member,db,model,statuses,stable,loadCase),LivePropertyMetrics.Member(member,db,model,statuses,stable));
            _liveHeading.Text+="\n"+loadCase+(!stable?"\n[color=#f09797]"+Loc.T("结构不稳定，无法给出内力")+"[/color]":"");
        }
        public void ShowTunnelProperties(TunnelGeometry geometry,int element,TunnelState state,LevelDefinition level,TunnelConfig cfg,string loadCase,System.Collections.Generic.List<LivePropertyMetric> lift=null)
        {
            var rows=LivePropertyMetrics.Tunnel(geometry,element,state,cfg,level);if(lift!=null)rows.AddRange(lift);
            ShowProperties(TunnelElementProperties(geometry,element,state,level,cfg,loadCase),rows);
            _liveHeading.Text+="\n"+loadCase;
        }
    }
}
