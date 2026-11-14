using System;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;

namespace BridgeArchitect3D.Data
{
    public static class CustomLiftLevels
    {
        public static string Fingerprint(LevelDefinition level)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{level.Terrain,level.Anchors},GameDatabase.JsonOptions))));
        public static bool Generate(LevelDefinition original,GameDatabase db,string material,bool adapt,out LevelDefinition result,out string error)
        {
            result=null;error=null;
            var work=LevelEditing.Copy(original);work.ProvidedInfrastructure=null;work.StartDesign=null;
            if(work.TrafficEvents.Any(e=>e.VehicleId!=null && db.Vehicles.TryGetValue(e.VehicleId,out var v)&&RailTrack.IsTrain(v))){error=Loc.T("自动生成的电梯关只提供公路通道，请先移除火车。");return false;}
            var type=db.TunnelConfig.Type(db.GetMaterial(material));if(type==null){error=Loc.T("请选择有效的通道类型。");return false;}
            if(!type.IsShield && !work.Terrain.HasWater){error=Loc.T("沉管或悬浮通道需要水体，请先启用有水河道。");return false;}
            foreach(string id in new[]{material,"deck_steel","steel","concrete","cable","cable_composite"})if(db.Materials.ContainsKey(id)&&!work.AllowedMaterials.Contains(id))work.AllowedMaterials.Add(id);
            if(adapt)
            {
                var t=work.Terrain;
                if(!type.IsShield)t.RiverBedDepth=Math.Min(t.RiverBedDepth,t.WaterLevel-type.TubeHeight-type.MinCover-type.MinBedClearance-.5);
                t.BuildMinY=Math.Min(t.BuildMinY,t.RiverBedDepth-type.TubeHeight-type.DefaultCover-5);
                t.BuildMarginX=Math.Max(t.BuildMarginX,32);
            }
            try
            {
                var editor=new DesignEditor(null,work,db);var built=editor.AddTunnel(0,work.Terrain.RiverWidth,material);
                if(!built.Ok){error=built.Message;return false;}
                editor.SetTunnelGrade(-1,"heavy");if(type.IsFloating)editor.SetTunnelBuoyancy(-1,"low");
                editor.Design.Tunnel.Structure=null;editor.MigrateStructuralTunnel();editor.RecalculateAll();
                var state=TunnelAnalysis.Evaluate(editor.TunnelGeo,work,db.TunnelConfig,TunnelAnalysis.StaticEnvironment(work,db.TunnelConfig));
                if(state.MaxUtil>1){error=Loc.T("当前埋深使固定管节外压超限，请减小埋深、水深或选择其他通道类型。");return false;}
                result=LiftChallengeRules.Prepare(work,editor.Design,db);
                LiftMechanics.Enable(result,db);
                if(original.ProvidedInfrastructure==null)
                {
                    var example=LiftMechanics.Reference(result.ProvidedInfrastructure,result,db);
                    result.Budget=Math.Ceiling(example.TotalCost()*1.2/1000)*1000;
                }
                result.Name=original.Name;
                if(!string.IsNullOrWhiteSpace(original.Description))result.Description=original.Description;
                if(original.ProvidedInfrastructure!=null)result.Budget=original.Budget;
                result.ProvidedTerrainSignature=Fingerprint(result);return true;
            }
            catch(Exception ex){error=Loc.T("生成固定通道失败：")+ex.Message;return false;}
        }
    }
}
