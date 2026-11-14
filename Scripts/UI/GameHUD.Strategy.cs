using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.UI
{
    public partial class GameHUD
    {
        private Control _strategyOverlay;
        private RichTextLabel _strategyText;
        private HBoxContainer _strategyChoices;
        private HBoxContainer _operationRow;
        private void BuildOperationChoices(VBoxContainer parent)
        {
            _operationRow=new HBoxContainer();parent.AddChild(_operationRow);
            foreach(string policy in new[]{LiftOperation.Shared,LiftOperation.SingleCentered})
            {
                string id=policy;
                var b=UiTheme.MakeButton(LiftOperation.Name(policy),()=>_ctl.TunnelAction("liftset:operation:"+id),118);
                b.SetMeta("policy",id);b.TooltipText=policy==LiftOperation.Shared?"多车在结构允许时合乘，减少往返次数。":"每次一辆车，停在平台中央减轻偏载；排队时间可能增加。";_operationRow.AddChild(b);
            }
            parent.AddChild(UiTheme.MakeButton("结构与经济方案评估",()=>_ctl.TunnelAction("strategy"),240));
        }
        public void SetProvidedTunnel(bool provided)
        {
            if(!provided)return;
            _tunnelBox.GetParent().MoveChild(_tunnelBox,0);
            _tunnelGate.Visible=false;
            _tunnelGrades.Visible=false;_tunnelBuoy.Visible=false;_tunnelAll.Visible=false;_tunnelReinforce.Visible=false;
            _tunnelCableKinds.Visible=false;_tunnelCableRow.Visible=false;
        }
        private void MarkOperation(string policy)
        {
            if(_operationRow==null)return;
            foreach(var c in _operationRow.GetChildren())if(c is Button b)b.Disabled=(string)b.GetMeta("policy")==policy;
        }
        public void ShowStrategyLoading()
        {
            if(_strategyOverlay==null)
            {
                VBoxContainer box;(_strategyOverlay,box)=UiTheme.MakeDialog(this,"结构与经济方案评估",850);
                _strategyText=new RichTextLabel{BbcodeEnabled=true,FitContent=true,ScrollActive=false,CustomMinimumSize=new(720,0)};box.AddChild(_strategyText);
                _strategyChoices=new HBoxContainer();box.AddChild(_strategyChoices);
                foreach(string id in new[]{"light","standard","heavy"})
                {
                    string template=id;
                    var b=UiTheme.MakeButton("采用"+LiftConfig.TemplateName(id),()=>{_strategyOverlay.Hide();_ctl.TunnelAction("lifttemplate:"+template);},180);
                    b.TooltipText="替换两岸当前电梯构件，保留平台长度和运营方式；可撤销。";_strategyChoices.AddChild(b);
                }
            }
            _strategyText.Text="正在用当前设计的副本计算空载、单车与模板方案……";
            _strategyChoices.Visible=false;_strategyOverlay.Show();
        }
        public void ShowStrategyResult(string text,bool allowTemplates=true)
        {
            if(_strategyOverlay==null)return;
            _strategyText.Text=text;_strategyChoices.Visible=allowTemplates;
        }
    }
}
