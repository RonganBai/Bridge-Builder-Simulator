using System;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;

internal static class CustomLiftTests
{
    public static void Run(GameDatabase db,Action<string,bool,string> check)
    {
        foreach(string type in new[]{"tunnel_immersed","tunnel_floating","tunnel_shield"})
        {
            var source=CustomLevelBuilder.Build(new CustomLevelSpec{Name="我的电梯关",RiverWidth=30},db);
            string before=GameDatabase.SerializeLevel(source);
            bool ok=CustomLiftLevels.Generate(source,db,type,true,out var level,out var error);
            check("Custom lift: generates "+type,ok,error);if(!ok)continue;
            check("Custom lift: preserves the source until generation succeeds",GameDatabase.SerializeLevel(source)==before,"");
            var loaded=LevelEditing.ReadCustomLevel(GameDatabase.SerializeLevel(level),db);
            check("Custom lift: save and reload retains mode, route and author name",loaded.Name==source.Name && loaded.ProvidedInfrastructure.Tunnel.MaterialId==type,"");
            check("Custom lift: generated level passes shared validation",LevelEditing.Validate(loaded,db).Count==0,string.Join(";",LevelEditing.Validate(loaded,db)));
            var editor=new DesignEditor(null,loaded,db);
            check("Custom lift: play starts with fixed tube and editable lift",editor.TunnelGeo!=null && editor.Lifts().Length==2 && !editor.Design.Members.Any(m=>!m.Provided),"");
            check("Custom lift: player cannot delete the supplied tunnel",!editor.DeleteTunnel().Ok,"");
            var missing=new DesignEditor(editor.Design.Clone(),loaded,db);
            missing.ReplaceDesign(LiftMechanics.Reference(missing.Design,loaded,db));
            var right=missing.Lifts()[1];
            foreach(var deck in right.DeckPath.ToArray())missing.DeleteMember(deck.Id);
            var blocked=new BridgeSimulation(missing.Design,loaded,db);blocked.Initialize();
            check("Custom lift: incomplete destination stops before spawning any cars",blocked.State==SimState.Failed && blocked.Vehicles.Count==0 && blocked.Result.FailureMessage.Contains("右岸电梯"),blocked.Result.FailureMessage);
            var broken=new DesignEditor(editor.Design.Clone(),loaded,db);
            broken.ReplaceDesign(LiftMechanics.Reference(broken.Design,loaded,db));
            var physical=new BridgeSimulation(broken.Design,loaded,db);physical.Initialize();
            if(physical.State==SimState.Running)
            {
                int id=physical.Lifts[0].Layout.DeckPath[0].Id;
                physical.Model.Broken[physical.Model.MemberIndex[id]]=true;
                physical.Step();
                check("Custom lift: physical platform failure stops the geometric lift route",physical.State==SimState.Failed && physical.Vehicles.Count==0,physical.Result.FailureMessage);
            }
            LevelEditing.SetRiverWidth(loaded,32);
            check("Custom lift: terrain changes require rebuilding the supplied route",LevelEditing.Validate(loaded,db).Any(s=>s.Contains("更新固定通道")),"");
        }
    }
}
