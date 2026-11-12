using System;
using System.Linq;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    /// <summary>
    /// Two hydraulic connections at a deck junction define a lockable seam. Expand only a runtime clone:
    /// left and right members keep their own endpoint while the closed lock transfers loads between them.
    /// </summary>
    public static class HydraulicDeckJoints
    {
        public static BridgeDesign Expand(BridgeDesign original,GameDatabase db)
        {
            BridgeDesign result=null;
            foreach(var node in original.Nodes)
            {
                if(node.Id<=0 || node.IsAnchor || node.IsRigidSplit || node.PierBase || node.PierPart || original.Mechanisms?.LockOf(node.Id)!=null) continue;
                var members=original.Members.Where(m=>m.DamageState!=DamageState.Broken &&
                    (m.StartNodeId==node.Id || m.EndNodeId==node.Id)).ToList();
                if(members.Any(m=>m.Existing)) continue;
                if(members.Count(m=>db.GetMaterial(m.MaterialId)?.Extendable==true)<2) continue;
                bool Left(BridgeMember m)=>original.FindNode(m.Other(node.Id))?.Position.X < node.Position.X-1e-6;
                bool Right(BridgeMember m)=>original.FindNode(m.Other(node.Id))?.Position.X > node.Position.X+1e-6;
                if(!members.Any(m=>db.GetMaterial(m.MaterialId)?.IsDeck==true && Left(m)) ||
                   !members.Any(m=>db.GetMaterial(m.MaterialId)?.IsDeck==true && Right(m))) continue;
                // Members on the split plane retain their original (left) endpoint;
                // only members unambiguously on the right are reassigned.
                if(original.FindNode(-node.Id)!=null) continue;
                result ??= original.Clone();
                var twin=node.Clone();twin.Id=-node.Id; result.Nodes.Add(twin);
                foreach(var member in members.Where(Right))
                {
                    var copy=result.FindMember(member.Id);
                    if(copy.StartNodeId==node.Id) copy.StartNodeId=twin.Id; else copy.EndNodeId=twin.Id;
                }
                var mechanisms=result.EnsureMechanisms();
                mechanisms.Locks.Add(new DeckLockData {Id=mechanisms.NextId++,NodeA=node.Id,NodeB=twin.Id});
            }
            return result ?? original;
        }
    }
}
