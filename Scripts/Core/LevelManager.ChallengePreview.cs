using System;
using System.Linq;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.Core
{
    public partial class LevelManager
    {
        private string _challengeShots;
        private int _challengeFrames;
        private void BackupLegacyLiftDesign(BridgeDesign design)
        {
            if(design?.Tunnel==null || _level.ProvidedInfrastructure==null || (design.InfrastructureId==_level.Id && (!_level.MechanicalLift||design.Nodes.Any(n=>n.LiftRole?.StartsWith("mount")==true))) || SaveManager.WritesSuspended)return;
            string name=Loc.T("改版前隧道方案_")+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+"UTC";
            if(!SaveManager.SaveNamedDesign(design.Clone(),name,out var error))GD.PushWarning(Loc.T("原方案备份未完成：")+error);
        }
        private void BeginChallengePreview()
        {
            var args=OS.GetCmdlineUserArgs();int at=Array.IndexOf(args,"--lift-challenge-preview");
            if(at<0 || at+1>=args.Length)return;
            SaveManager.WritesSuspended=true;_challengeShots=args[at+1];
            if(_level.MechanicalLift)_editor.ReplaceDesign(Physics.LiftMechanics.Reference(_editor.Design,_level,_db));else _editor.RebuildLifts("standard");_building.SelectedTunnelElement=0;RefreshProperties();
            if(Array.IndexOf(args,"--rope-preview")>=0)
            {
                foreach(var l in _editor.Lifts())
                {
                    var left=_editor.Design.Nodes.Single(n=>n.LiftBank==l.Bank&&n.LiftRole=="mount0");
                    var right=_editor.Design.Nodes.Single(n=>n.LiftBank==l.Bank&&n.LiftRole=="mount1");
                    var old=l.Members.First(m=>_db.GetMaterial(m.MaterialId)?.Extendable==true&&(m.StartNodeId==left.Id||m.EndNodeId==left.Id));_editor.DeleteMember(old.Id);
                    double x=l.Shaft.X1+_editor.LiftCfgOf.ColumnOffset,y=right.Position.Y;
                    var beam=_editor.AddMemberToNewNode(right.Id,x,y,"steel");if(!beam.Ok)throw new InvalidOperationException("QA beam: "+beam.Message+" x="+x+" y="+y);int over=_editor.Design.FindMember(beam.CreatedId).EndNodeId;
                    _editor.InstallDevice(left.Id,"fixed_pulley");_editor.InstallDevice(over,"fixed_pulley");
                    _editor.AddMember(l.Deck0.Id,left.Id,"cable_composite");_editor.AddMember(left.Id,over,"cable_composite");
                    var tail=_editor.AddMemberToNewNode(over,x,y-1,"cable_composite");int weight=_editor.Design.FindMember(tail.CreatedId).EndNodeId;
                    _editor.InstallDevice(weight,"counterweight");
                    _editor.AddMember(l.Pits.OrderBy(n=>n.Position.X).Last().Id,weight,"hydraulic_lift");
                }
            }
            if(Array.IndexOf(args,"--stress-preview")>=0){_building.SetStressView(true);_hud.SetStressChecked(true);}
            SetCutaway(true);_building.FocusTunnel("entry",true);
        }
        private void TickChallengePreview()
        {
            if(_challengeShots==null)return;
            _challengeFrames++;
            if(_challengeFrames==40)
            {
                if(DisplayServer.GetName()!="headless")GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_challengeShots,"lift-challenge.png"));
                GD.Print("Lift challenge ready: "+_level.Id+"; budget="+_level.Budget+"; cost="+_editor.UsedCost);
                ShowEngineeringStrategy();
            }
            if(_challengeFrames>60 && !_strategyBusy)
            {
                if(DisplayServer.GetName()!="headless")GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_challengeShots,"lift-strategy.png"));
                GD.Print("Lift strategy panel rendered.");GetTree().Quit();
            }
        }
    }
}
