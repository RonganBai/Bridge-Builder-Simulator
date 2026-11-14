using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Physics;

namespace BridgeArchitect3D.Data
{
    public sealed class LiftChallengeSpec
    {
        public string LevelId { get; set; }
        public string Design { get; set; }
        public double BudgetFactor { get; set; }=1.2;
    }
    public static class LiftChallengeRules
    {
        public static void Load(GameDatabase db,string json,Func<string,string> read)
        {
            foreach(var spec in JsonSerializer.Deserialize<List<LiftChallengeSpec>>(json,GameDatabase.JsonOptions))
            {
                var original=db.GetLevel(spec.LevelId);if(original==null)continue;
                var level=Prepare(original,GameDatabase.DeserializeDesign(read(spec.Design)),db,spec.BudgetFactor);
                LiftMechanics.Enable(level,db);
                var sample=LiftMechanics.Reference(GameDatabase.DeserializeDesign(read(spec.Design)),level,db);
                level.Budget=Math.Ceiling(sample.TotalCost()*Math.Max(1,spec.BudgetFactor)/1000)*1000;
                db.ReplaceLevel(level);
            }
        }
        public static LevelDefinition Prepare(LevelDefinition original,BridgeDesign supplied,GameDatabase db,double factor=1.2)
        {
            var level=GameDatabase.DeserializeLevel(GameDatabase.SerializeLevel(original));level.ProvidedInfrastructure=null;
            var editor=new DesignEditor(supplied.Clone(),level,db);var lifts=editor.Lifts();
            var liftMembers=lifts.SelectMany(l=>l.Members).Select(m=>m.Id).ToHashSet();
            var liftNodes=lifts.SelectMany(l=>l.Nodes).ToHashSet();
            double memberCost=editor.Design.Members.Where(m=>liftMembers.Contains(m.Id)).Sum(m=>m.EffectiveCost());
            double machineCost=lifts.Sum(l=>LiftStructure.MachineCost(editor.Design.Tunnel.Lift,l,db)-Math.Max(0,l.Shaft.Height)*editor.LiftCfgOf.ShaftCostPerMeter);
            var provided=editor.Design.Clone();provided.Members.RemoveAll(m=>liftMembers.Contains(m.Id));
            provided.Nodes.RemoveAll(n=>liftNodes.Contains(n.Id)&&!n.IsLiftFixed);
            foreach(var n in provided.Nodes)n.Provided=!n.IsLiftFixed;
            foreach(var m in provided.Members)m.Provided=true;
            provided.Tunnel.Structure=1;provided.Tunnel.Lift.Built=null;
            level.ProvidedInfrastructure=provided;
            level.Name=level.Id=="level13"?"第十三关：沉管通道电梯":level.Id=="level14"?"第十四关：悬浮通道电梯":"第十五关：深埋通道电梯";
            level.Budget=Math.Ceiling((memberCost+machineCost)*Math.Max(1,factor)/1000)*1000;
            level.Description="电梯设计挑战：隧道、地面通道及外部支撑由关卡提供。用桥面、梁、钢索和柱子组装两岸电梯，平衡承载、偏载、驱动与造价。";
            level.StartDesign=null;level.StartHint="固定隧道已建成。当前是轻型电梯起步结构；重点检查平台、吊索、井架和驱动，再选择居中单车或多车合乘。预算仅用于电梯。";
            level.ReferenceHint="先查看空载和车辆偏载下的控制部位，再针对性补强。配重减轻驱动负担，但增加井架受力；用结构与经济方案评估比较材料成本。";
            return level;
        }
        /// <summary>One-time migration keeps the user's lift members and machines, replacing only the supplied infrastructure.</summary>
        public static void Ensure(BridgeDesign design,LevelDefinition level,GameDatabase db)
        {
            var supplied=level.ProvidedInfrastructure;if(supplied==null || design.InfrastructureId==level.Id)return;
            var oldLift=design.Tunnel?.Lift?.Clone();double oldLength=design.Tunnel?.LiftLength??0;
            var oldLayouts=design.Tunnel==null?Array.Empty<LiftLayout>():LiftStructure.Analyze(design,level,db);
            var oldMembers=oldLayouts.SelectMany(l=>l.Members).DistinctBy(m=>m.Id).Select(m=>m.Clone()).ToList();
            var oldNodes=oldLayouts.SelectMany(l=>l.Nodes).Distinct().Select(design.FindNode).Where(n=>n!=null).Select(n=>n.Clone()).ToList();
            var baseDesign=supplied.Clone();design.Nodes=baseDesign.Nodes;design.Members=baseDesign.Members;design.Tunnel=baseDesign.Tunnel;
            design.NextNodeId=design.Nodes.Select(n=>n.Id).DefaultIfEmpty(0).Max()+1;design.NextMemberId=design.Members.Select(m=>m.Id).DefaultIfEmpty(0).Max()+1;
            if(oldLength>0)design.Tunnel.LiftLength=oldLength;
            if(oldLift!=null)design.Tunnel.Lift=oldLift;
            var map=new Dictionary<int,int>();
            foreach(var node in oldNodes)
            {
                if(node.IsLiftFixed)
                {
                    int index=node.LiftRole=="pit"?oldNodes.Where(n=>n.LiftBank==node.LiftBank && n.LiftRole=="pit").OrderBy(n=>n.Position.X).ToList().FindIndex(n=>n.Id==node.Id):0;
                    var target=design.Nodes.Where(n=>n.LiftBank==node.LiftBank && n.LiftRole==node.LiftRole).OrderBy(n=>n.Position.X).ElementAtOrDefault(index);
                    if(target!=null){map[node.Id]=target.Id;target.Position=node.Position;}
                }
                else{int old=node.Id;node.Id=design.NextNodeId++;node.Provided=false;design.Nodes.Add(node);map[old]=node.Id;}
            }
            foreach(var member in oldMembers)
                if(map.TryGetValue(member.StartNodeId,out int a)&&map.TryGetValue(member.EndNodeId,out int b))
                {member.Id=design.NextMemberId++;member.StartNodeId=a;member.EndNodeId=b;member.Provided=false;design.Members.Add(member);}
            design.Tunnel.Lift.Built=oldMembers.Count>0?1:null;
            design.InfrastructureId=level.Id;design.DemolitionCost=0;design.RepairCost=0;design.DemolishedCount=0;
        }
        public static bool Editable(BridgeNode n)=>n!=null && !n.Provided;
        public const string Locked="隧道与外部设施由关卡提供；本关只设计两岸电梯。";
    }
}
