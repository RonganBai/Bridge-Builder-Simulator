using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

internal static class LiftChallengeTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        foreach(string id in new[]{"level13","level14","level15"})
        {
            var reference=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/LiftChallenges",id+"_provided.json")));
            var level=LiftChallengeRules.Prepare(db.GetLevel(id),reference,db);
            var starter=new DesignEditor(null,level,db);
            check($"Lift challenge {id}: supplies a fixed route and external structure",starter.TunnelGeo!=null && starter.Design.InfrastructureId==id && starter.Design.Nodes.Any(n=>n.Provided) && starter.Design.Members.Where(m=>m.Provided).All(m=>m.EffectiveCost()==0),"");
            var point=starter.Design.Nodes.First(n=>n.TunnelPoint);
            check($"Lift challenge {id}: provided tube cannot be moved, deleted or upgraded",!starter.MoveNode(point.Id,point.Position.X,point.Position.Y-1).Ok && !starter.DeleteTunnel().Ok && !starter.SetTunnelGrade(-1,"heavy").Ok && !starter.SetTunnelBuoyancy(-1,"low").Ok,"");
            var fixedMember=starter.Design.Members.FirstOrDefault(m=>m.Provided);
            if(fixedMember!=null)check($"Lift challenge {id}: fixed support cannot be removed or refunded",!starter.DeleteMember(fixedMember.Id).Ok && !starter.DeleteMany(Array.Empty<int>(),new[]{fixedMember.Id}).Ok,"");
            check($"Lift challenge {id}: construction outside the lift shafts is blocked",!starter.AddNode(level.Terrain.RiverWidth/2,5).Ok,"");
            check($"Lift challenge {id}: platform and operating mode remain player decisions",starter.SetLiftLength(20).Ok && starter.SetLiftOperation(LiftOperation.SingleCentered).Ok,"");
            var editable=new DesignEditor(starter.Design.Clone(),level,db);
            bool canBuild=true;
            foreach(var lift in editable.Lifts())
            {
                var decks=lift.Members.Where(m=>db.GetMaterial(m.MaterialId)?.IsDeck==true).ToArray();
                foreach(var deck in decks)
                {
                    int a=deck.StartNodeId,b=deck.EndNodeId;
                    canBuild &= editable.DeleteMember(deck.Id).Ok;
                    canBuild &= editable.AddMember(a,b,deck.MaterialId).Ok;
                }
                var end=lift.Deck0;
                canBuild &= end!=null && !end.Provided && Math.Abs(end.Position.Y-lift.Shaft.Bottom)<1e-6;
            }
            check($"Lift challenge {id}: lower landing platform supports accept replacement road segments",canBuild,"");
            string once=JsonSerializer.Serialize(starter.Design,GameDatabase.JsonOptions);
            var reload=new DesignEditor(starter.Design.Clone(),level,db);
            check($"Lift challenge {id}: migration is repeatable without duplicated facilities",once==JsonSerializer.Serialize(reload.Design,GameDatabase.JsonOptions),"");
            var old=new DesignEditor(reference.Clone(),db.GetLevel(id),db);old.SetLiftOperation(LiftOperation.SingleCentered);
            int oldCount=old.Lifts().Sum(l=>l.Members.Count);double cw=old.Design.Tunnel.Lift.Counterweight;
            var migrated=new DesignEditor(old.Design.Clone(),level,db);
            check($"Lift challenge {id}: preserves old lift members, machine and dispatch policy",migrated.Lifts().Sum(l=>l.Members.Count)==oldCount && migrated.Design.Tunnel.Lift.Counterweight==cw && migrated.Design.Tunnel.Lift.BoardingPolicy==LiftOperation.SingleCentered,"");
            // Reference geometry remains physical; only supplied costs and editing rights change.
            var solution=new DesignEditor(reference.Clone(),level,db);
            var result=new BridgeSimulation(solution.Design,level,db).RunToEnd();
            check($"Lift challenge {id}: reference completes under the lift-only budget",result.IsSuccess && solution.UsedCost<=level.Budget,$"cost={solution.UsedCost:0}/{level.Budget:0}; {result.FailureMessage}");
            double cost=solution.UsedCost;
            solution.SetLiftMachine(null,1);
            check($"Lift challenge {id}: counterweight is charged for both lifts",Math.Abs(solution.UsedCost-cost-2*solution.LiftCfgOf.CounterweightStep*solution.LiftCfgOf.CounterweightCostPerTonne)<1,"");
        }
    }
}
