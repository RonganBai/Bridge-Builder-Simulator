using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Physics
{
    /// <summary>
    /// Finds passive rigid subassemblies without choosing a ground pivot or an actuator.
    /// Triangles retain shape by their edges; braced ordinary deck junctions retain their
    /// angles. Sharing only one node is a hinge, never sufficient to merge bodies.
    /// </summary>
    public static class RigidFrameDiscovery
    {
        public static void Discover(StructuralModel model, HashSet<int> scope = null)
        {
            var edges = Enumerable.Range(0, model.MemberCount).Where(k => !model.Broken[k] &&
                !model.Mat[k].Extendable && !model.Mat[k].IsCable && !model.Mat[k].IsColumn &&
                model.EI[k] > 0 && (scope == null || scope.Contains(model.A[k]))).ToArray();
            var incident = new Dictionary<int, List<int>>();
            foreach (int k in edges)
                foreach (int n in new[] { model.A[k], model.B[k] })
                {
                    if (!incident.TryGetValue(n, out var list)) incident[n] = list = new List<int>();
                    list.Add(k);
                }
            var parts = new List<(HashSet<int> nodes, HashSet<int> members)>();
            bool EnclosesCylinder(HashSet<int> nodes) => Enumerable.Range(0,model.MemberCount).Any(k=>
                !model.Broken[k] && model.Mat[k].Extendable && nodes.Contains(model.A[k]) && nodes.Contains(model.B[k]));
            foreach (var entry in incident)
                for (int i = 0; i < entry.Value.Count; i++)
                    for (int j = i + 1; j < entry.Value.Count; j++)
                    {
                        int n = entry.Key, first = entry.Value[i], second = entry.Value[j];
                        int a = model.A[first] == n ? model.B[first] : model.A[first];
                        int b = model.A[second] == n ? model.B[second] : model.A[second];
                        if (a == b) continue;
                        int closing = edges.FirstOrDefault(k =>
                            model.A[k] == a && model.B[k] == b || model.A[k] == b && model.B[k] == a, -1);
                        if (closing < 0) continue;
                        var members = new HashSet<int> { first, second };
                        if (closing >= 0) members.Add(closing);
                        parts.Add((new HashSet<int> { n, a, b }, members));
                    }
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < parts.Count && !changed; i++)
                    for (int j = i + 1; j < parts.Count; j++)
                        if (parts[i].nodes.Intersect(parts[j].nodes).Take(2).Count() == 2)
                        {
                            parts[i].nodes.UnionWith(parts[j].nodes);
                            parts[i].members.UnionWith(parts[j].members);
                            parts.RemoveAt(j); changed = true; break;
                        }
            } while (changed);
            // Extend known trusses along the deck before considering dangling support bars.
            // A single unbraced steel rod remains a follower, rather than being welded into
            // whichever deck junction happens to be visited first.
            foreach(var entry in incident.OrderBy(e=>model.NodeIds[e.Key]))
            {
                int n=entry.Key;
                var deck=entry.Value.Where(k=>model.Mat[k].IsDeck).ToArray();
                if(deck.Length<2 || model.SplitJoints.Contains(n) || model.IsAnchor[n] ||
                   entry.Value.Any(k=>model.IsAnchor[model.A[k]==n?model.B[k]:model.A[k]]))continue;
                var nodes=deck.SelectMany(k=>new[]{model.A[k],model.B[k]}).ToHashSet();
                var members=deck.ToHashSet();var joined=new HashSet<int>();
                do
                {
                    changed=false;
                    for(int i=0;i<parts.Count;i++)
                        if(!joined.Contains(i) && nodes.Intersect(parts[i].nodes).Take(2).Count()==2)
                        {joined.Add(i);nodes.UnionWith(parts[i].nodes);members.UnionWith(parts[i].members);changed=true;}
                }while(changed);
                // Existing passive triangles are real constraints. An inferred continuation
                // may not add an invisible brace across either an active or a passive cylinder.
                if(EnclosesCylinder(nodes))continue;
                foreach(int index in joined.OrderByDescending(i=>i))parts.RemoveAt(index);
                parts.Add((nodes,members));
            }
            foreach (var part in parts)
            {
                int a = part.nodes.First();
                bool hasArea = part.nodes.Any(b => part.nodes.Any(c =>
                    Math.Abs((model.X0[b] - model.X0[a]) * (model.Y0[c] - model.Y0[a]) -
                             (model.Y0[b] - model.Y0[a]) * (model.X0[c] - model.X0[a])) > 1e-8));
                if (!hasArea || model.RigidBodies.Any(body => part.nodes.IsSubsetOf(body.nodes))) continue;
                model.RigidBodies.Add((part.nodes.OrderBy(n => n).ToArray(), -1,
                    part.members.OrderBy(k => k).ToArray()));
            }
        }
    }
}
