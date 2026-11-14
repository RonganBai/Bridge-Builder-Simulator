using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    /// <summary>One continuous elastic rope passing through fixed, frictionless pulleys.</summary>
    public sealed class PulleyRope
    {
        public int[] Members;
        public Dictionary<int,double> Gradient(StructuralModel m)
        {
            var g=new Dictionary<int,double>();
            foreach(int k in Members)
            {
                void Add(int i,double v)=>g[i]=g.GetValueOrDefault(i)+v;
                Add(2*m.A[k],-m.Cos[k]);Add(2*m.A[k]+1,-m.Sin[k]);
                Add(2*m.B[k],m.Cos[k]);Add(2*m.B[k]+1,m.Sin[k]);
            }
            return g;
        }
        public bool Active(StructuralModel m)=>Members.All(k=>!m.Broken[k]&&!m.Slack[k]);
        public double Stiffness(StructuralModel m)=>1/Members.Sum(k=>m.Length[k]/m.EA[k]);
        public double Extension(StructuralModel m,double[] u)=>Gradient(m).Sum(q=>q.Value*u[q.Key])-Members.Sum(k=>m.Prestrain[k]);
    }
    public sealed class LiftMotionPlan
    {
        public string Problem;
        public double Speed=.6;
        public readonly Dictionary<int,double> Motion=new();
        public readonly List<int> Drivers=new();
    }
    public static class LiftMechanics
    {
        public const double PulleyCapacity=600000,WeightStep=5;
        public static bool IsMechanical(LiftLayout l)=>l.Mechanical;
        public static void Enable(LevelDefinition level,GameDatabase db)
        {
            bool validSignature=level.ProvidedTerrainSignature!=null&&level.ProvidedTerrainSignature==CustomLiftLevels.Fingerprint(level);
            var geometry=level.ProvidedInfrastructure==null?null:TunnelGeometry.Build(level.ProvidedInfrastructure,db);
            if(geometry!=null)
            {
                var shafts=TunnelRules.Shafts(level,geometry);var cfg=LiftStructure.Cfg(db);
                level.Terrain.BuildMarginX=Math.Max(level.Terrain.BuildMarginX,geometry.LiftLength+2);
                level.Terrain.BuildMinY=Math.Min(level.Terrain.BuildMinY,shafts.Min(s=>LiftStructure.PitY(cfg,s))-1);
                level.Terrain.BuildMaxY=Math.Max(level.Terrain.BuildMaxY,shafts.Max(s=>LiftStructure.HeadY(cfg,s))+1);
            }
            if(validSignature)level.ProvidedTerrainSignature=CustomLiftLevels.Fingerprint(level);
            level.MechanicalLift=true;
            foreach(string id in new[]{"hydraulic","hydraulic_lift","cable","cable_composite","fixed_pulley","counterweight","steel","deck_steel"})
                if(!level.AllowedMaterials.Contains(id))level.AllowedMaterials.Add(id);
            AddMounts(level.ProvidedInfrastructure,level,db);
            level.StartHint="自行铺设平台，用液压缸驱动，或经定滑轮连接钢索与配重。橙色支点可安装滑轮；平台端点可铺路。安装、断绳、行程不足均会提示。";
            if(string.IsNullOrWhiteSpace(level.Description)||level.Description.StartsWith("电梯设计挑战")||level.Description.StartsWith("机构电梯"))level.Description="机构电梯：通道和少量支点由关卡提供，玩家设计平台、液压缸、钢索、定滑轮及配重；比较行程、拉力、支反力与造价。";
        }
        public static void AddMounts(BridgeDesign d,LevelDefinition level,GameDatabase db)
        {
            if(d?.Tunnel==null||!level.MechanicalLift)return;
            var g=TunnelGeometry.Build(d,db);if(g==null)return;
            var shafts=TunnelRules.Shafts(level,g);var c=LiftStructure.Cfg(db);
            for(int b=1;b<=shafts.Length;b++)
            {
                var s=shafts[b-1];
                foreach(var (role,x) in new[]{("mount0",s.X0),("mount1",s.X1)})
                {
                    var n=d.Nodes.FirstOrDefault(n=>n.LiftBank==b && n.LiftRole==role);
                    if(n==null){n=new BridgeNode{Id=d.NextNodeId++,LiftBank=b,LiftRole=role};d.Nodes.Add(n);}
                    n.Position=new Vec3d(x,LiftStructure.HeadY(c,s));n.Provided=false;
                }
            }
        }
        public static bool StableMount(BridgeDesign d,GameDatabase db,int node)
        {
            var target=d.FindNode(node);if(target==null)return false;
            bool Fixed(BridgeNode n)=>n.PierBase||n.LiftRole=="pit"||n.LiftRole?.StartsWith("mount")==true;
            if(Fixed(target)||target.IsAnchor&&target.SupportType==SupportType.Pinned)return true;
            bool supported=false;var seen=new HashSet<int>();var q=new Queue<int>();q.Enqueue(node);
            while(q.Count>0)
            {
                int id=q.Dequeue();if(!seen.Add(id))continue;var n=d.FindNode(id);if(n==null)continue;
                supported|=n.IsAnchor||Fixed(n);
                foreach(var m in d.Members)
                {
                    if(m.StartNodeId!=id&&m.EndNodeId!=id)continue;
                    var mat=db.GetMaterial(m.MaterialId);if(mat==null||mat.IsCable||mat.Extendable)continue;
                    q.Enqueue(m.Other(id));
                }
            }
            if(!supported)return false;
            var sub=new BridgeDesign{Nodes=d.Nodes.Where(n=>seen.Contains(n.Id)).Select(n=>n.Clone()).ToList(),Members=d.Members.Where(m=>seen.Contains(m.StartNodeId)&&seen.Contains(m.EndNodeId)&&db.GetMaterial(m.MaterialId)?.IsCable==false&&db.GetMaterial(m.MaterialId)?.Extendable==false).Select(m=>m.Clone()).ToList()};
            var level=new LevelDefinition{Anchors=sub.Nodes.Where(n=>n.IsAnchor).Select(n=>new AnchorDefinition{Id=n.AnchorId,Position=n.Position,SupportType=n.SupportType}).ToList()};
            var model=StructuralModel.Build(sub,level,db);
            foreach(var n in sub.Nodes.Where(Fixed))if(model.NodeIndex.TryGetValue(n.Id,out int i)){model.FixX[i]=true;model.FixY[i]=true;model.FixRot[i]=true;}
            var load=new double[model.NodeCount*2];LoadCalculator.Compute(model,Array.Empty<AxleLoad>(),0,load);var output=new SolveOutput();new StructureSolver().Solve(model,load,output);
            return output.Stable;
        }

        public static List<int[]> Ropes(BridgeDesign d,GameDatabase db)
        {
            var cables=d.Members.Where(m=>db.GetMaterial(m.MaterialId)?.IsCable==true).ToDictionary(m=>m.Id);
            var seen=new HashSet<int>();var groups=new List<int[]>();
            foreach(var edge in cables.Values)
            {
                if(seen.Contains(edge.Id))continue;var ids=new HashSet<int>();var q=new Queue<int>();q.Enqueue(edge.Id);
                while(q.Count>0)
                {
                    int id=q.Dequeue();if(!ids.Add(id))continue;seen.Add(id);var m=cables[id];
                    foreach(int node in new[]{m.StartNodeId,m.EndNodeId})
                    {
                        if(d.FindNode(node)?.Pulley!=true)continue;
                        var branches=cables.Values.Where(x=>x.StartNodeId==node||x.EndNodeId==node).ToArray();
                        if(branches.Length==2)foreach(var part in branches)q.Enqueue(part.Id);
                    }
                }
                if(ids.Count>1)groups.Add(ids.ToArray());
            }
            return groups;
        }
        public static LiftMotionPlan Plan(BridgeDesign d,LevelDefinition level,GameDatabase db,LiftLayout l)
        {
            var plan=new LiftMotionPlan();foreach(int id in l.Platform)plan.Motion[id]=1;
            foreach(var pulley in d.Nodes.Where(n=>n.Pulley&&l.Nodes.Contains(n.Id)))
            {
                int count=l.Members.Count(m=>db.GetMaterial(m.MaterialId)?.IsCable==true&&(m.StartNodeId==pulley.Id||m.EndNodeId==pulley.Id));
                if(count!=2){plan.Problem=$"定滑轮 #{pulley.Id} 必须连接两段钢索，当前 {count} 段";return plan;}
                if(!StableMount(d,db,pulley.Id)){plan.Problem=$"定滑轮 #{pulley.Id} 的安装点没有稳定支撑";return plan;}
            }
            foreach(var group in Ropes(d,db))
            {
                var parts=group.Select(d.FindMember).ToArray();if(!parts.Any(m=>l.Members.Any(v=>v.Id==m.Id)))continue;
                var g=new Dictionary<int,double>();
                foreach(var m in parts)
                {
                    var a=d.FindNode(m.StartNodeId);var b=d.FindNode(m.EndNodeId);double len=Vec3d.Distance(a.Position,b.Position);
                    double s=(b.Position.Y-a.Position.Y)/Math.Max(.001,len);
                    g[a.Id]=g.GetValueOrDefault(a.Id)-s;g[b.Id]=g.GetValueOrDefault(b.Id)+s;
                }
                double platform=g.Where(v=>l.Platform.Contains(v.Key)).Sum(v=>v.Value);
                if(Math.Abs(platform)<.1)continue;
                var moving=g.Where(v=>!l.Platform.Contains(v.Key)&&!StableMount(d,db,v.Key)&&Math.Abs(v.Value)>.1).ToArray();
                if(moving.Length!=1){plan.Problem="跨过定滑轮的钢索需要一个可移动端点，连接配重或液压缸；不能把两端都钉死。";return plan;}
                var end=moving[0];
                foreach(var part in parts)
                {
                    bool hasEnd=part.StartNodeId==end.Key||part.EndNodeId==end.Key||l.Platform.Contains(part.StartNodeId)||l.Platform.Contains(part.EndNodeId);
                    if(hasEnd && Math.Abs(d.FindNode(part.StartNodeId).Position.X-d.FindNode(part.EndNodeId).Position.X)>.1)
                    {plan.Problem=$"钢索 #{part.Id} 的移动端必须沿竖直导轨走绳，避免绳长变化锁死";return plan;}
                }
                plan.Motion[end.Key]=-platform/end.Value;
            }
            foreach(var h in l.Members.Where(m=>db.GetMaterial(m.MaterialId)?.Extendable==true))
            {
                double va=plan.Motion.GetValueOrDefault(h.StartNodeId),vb=plan.Motion.GetValueOrDefault(h.EndNodeId);
                if(Math.Abs(va-vb)<.01)continue;
                var a=d.FindNode(h.StartNodeId);var b=d.FindNode(h.EndNodeId);
                int fixedId=Math.Abs(va)<.01?a.Id:b.Id;
                if(!StableMount(d,db,fixedId)){plan.Problem=$"液压缸 #{h.Id} 的固定端没有稳定支撑";return plan;}
                if(Math.Abs(a.Position.X-b.Position.X)>.1){plan.Problem=$"液压缸 #{h.Id} 必须沿竖直导轨安装，避免侧向卡死";return plan;}
                double len=Vec3d.Distance(a.Position,b.Position),delta=(b.Position.Y>=a.Position.Y?1:-1)*(vb-va)*(l.Shaft.Top-l.Shaft.Bottom);
                var mat=db.GetMaterial(h.MaterialId);double stroke=h.Stroke??(delta<0?-mat.RetractRatio:mat.ExtendRatio);
                if(len+delta<.1||Math.Sign(stroke)!=Math.Sign(delta)||Math.Abs(len*stroke)+.01<Math.Abs(delta))
                {plan.Problem=$"液压缸 #{h.Id} 行程不足或方向相反：需要 {Math.Abs(delta):0.0} m，可用 {Math.Abs(len*stroke):0.0} m；调整初始长度与目标长度";return plan;}
                plan.Drivers.Add(h.Id);
            }
            if(plan.Drivers.Count==0){plan.Problem="没有有效液压驱动：将液压缸接到平台，或接到经定滑轮转向的钢索端点，并提供足够行程";return plan;}
            foreach(var move in plan.Motion)
            {
                var node=d.FindNode(move.Key);double end=node.Position.Y+move.Value*(l.Shaft.Top-l.Shaft.Bottom);
                if(end<LiftStructure.PitY(LiftStructure.Cfg(db),l.Shaft)+.3||end>LiftStructure.HeadY(LiftStructure.Cfg(db),l.Shaft)+.1)
                {plan.Problem=$"移动节点 #{node.Id} 会超出井道行程，调整配重初始位置";return plan;}
            }
            foreach(var m in l.Members.Where(m=>db.GetMaterial(m.MaterialId)?.IsCable==true))
            {
                var a=d.FindNode(m.StartNodeId);var b=d.FindNode(m.EndNodeId);
                if(d.FindNode(m.StartNodeId).Pulley||d.FindNode(m.EndNodeId).Pulley)continue;
                if(Math.Abs(plan.Motion.GetValueOrDefault(a.Id)-plan.Motion.GetValueOrDefault(b.Id))>.01)
                {plan.Problem=$"钢索 #{m.Id} 两端直接固定，会锁住升降行程；请经过定滑轮连接可移动端";return plan;}
            }
            return plan;
        }
        public static void ConfigureModel(StructuralModel m,BridgeDesign d,LevelDefinition level,GameDatabase db)
        {
            foreach(var n in d.Nodes)
            {
                if(!m.NodeIndex.TryGetValue(n.Id,out int i))continue;
                m.NodeLoadY[i]-=(n.CounterweightTonnes*1000+(n.Pulley?40:0))*StructuralModel.Gravity;
                if(n.LiftRole?.StartsWith("mount")==true){m.FixX[i]=true;m.FixY[i]=true;m.FixRot[i]=true;m.IsAnchor[i]=true;m.AnchorMaxLoad[i]=PulleyCapacity;m.AnchorIds[i]="井道固定支点 #"+n.Id;}
                if(n.CounterweightTonnes>0)m.FixX[i]=true;
                if(n.Pulley)m.PulleyBearings[i]=PulleyCapacity;
            }
            foreach(var ids in Ropes(d,db))
            {
                int[] parts=ids.Where(m.MemberIndex.ContainsKey).Select(id=>m.MemberIndex[id]).ToArray();
                if(parts.Length!=ids.Length)continue;
                m.PulleyRopes.Add(new PulleyRope{Members=parts});foreach(int k in parts)m.PulleyMembers.Add(k);
            }
        }
        public static double DriveUtil(StructuralModel m,SolveOutput output,LiftLayout l)
        {
            double u=0;
            foreach(var member in l.Members)
                if(m.MemberIndex.TryGetValue(member.Id,out int k)&&m.Mat[k].Extendable)u=Math.Max(u,Math.Abs(output.AxialForce[k])/Math.Max(1,m.Mat[k].ActuatorForce));
            return u;
        }
        public static BridgeDesign Reference(BridgeDesign source,LevelDefinition level,GameDatabase db)
        {
            var e=new BridgeArchitect3D.Building.DesignEditor(source.Clone(),level,db);
            if(e.Lifts().Any(l=>l.DeckPath.Count==0))e.RebuildLifts("heavy");
            foreach(var l in e.Lifts())
            {
                var remove=l.Members.Where(m=>!l.IsPlatformMember(m)).Select(m=>m.Id).ToHashSet();
                e.Design.Members.RemoveAll(m=>remove.Contains(m.Id));
                foreach(var (role,deck) in new[]{("mount0",l.Deck0),("mount1",l.Deck1)})
                {
                    var mount=e.Design.Nodes.Single(n=>n.LiftBank==l.Bank&&n.LiftRole==role);
                    var built=e.AddMember(mount.Id,deck.Id,"hydraulic_lift");
                    if(!built.Ok)throw new InvalidOperationException(built.Message);
                    e.Design.FindMember(built.CreatedId).Stroke=-.95;
                }
            }
            e.Design.Nodes.RemoveAll(n=>!n.Provided&&!n.IsAnchor&&!n.IsTunnelNode&&!e.Design.Members.Any(m=>m.StartNodeId==n.Id||m.EndNodeId==n.Id));
            e.RecalculateAll();return e.Design;
        }
        public static string Report(BridgeDesign d,LevelDefinition level,GameDatabase db)
        {
            var text=new System.Text.StringBuilder("[b]机构电梯 · 承载与经济检查[/b]\n");
            foreach(var l in LiftStructure.Analyze(d,level,db))
            {
                text.AppendLine("\n[b]"+LiftStructure.Name(l)+"[/b]");
                if(l.Problem!=null){text.AppendLine("未通过："+l.Problem);continue;}
                var empty=LiftStructure.Check(d,level,db,l,Array.Empty<(double,double)>());
                text.AppendLine($"空载最大利用率 {empty.Util*100:0}%");
                foreach(string id in level.TrafficEvents.Where(e=>e.Type==TrafficEventType.Vehicle&&e.VehicleId!=null).Select(e=>e.VehicleId).Distinct())
                {
                    if(!db.Vehicles.TryGetValue(id,out var vehicle)||RailTrack.IsTrain(vehicle))continue;
                    var result=LiftStructure.Check(d,level,db,l,LiftStructure.VehicleAxles(vehicle,l.Shaft,TunnelRules.LiftCfg.Margin,d.Tunnel.Lift.BoardingPolicy));
                    text.AppendLine($"{vehicle.Name}：最大利用率 {result.Util*100:0}%（{result.Where} / {result.Why}）");
                    if(result.Util>1)text.AppendLine("建议："+result.Fix);
                }
            }
            text.AppendLine("\n[b]当前造价[/b]");
            foreach(var group in d.Members.Where(m=>!m.Provided).GroupBy(m=>m.MaterialId))text.AppendLine($"{db.GetMaterial(group.Key)?.Name}：${group.Sum(m=>m.EffectiveCost()):N0}");
            text.AppendLine($"定滑轮：${d.Nodes.Count(n=>n.Pulley)*1800:N0}；配重：${d.Nodes.Sum(n=>n.CounterweightTonnes)*260:N0}");
            text.AppendLine($"合计 ${d.TotalCost():N0} / 预算 ${level.Budget:N0}");
            text.AppendLine("\n下层、半程、上层三个位形做二维准静态验算。单车校核不保证多车合乘；合乘在测试时按实际车队校核。定滑轮只转向，配重不消除支点负荷。");
            return text.ToString();
        }
        public static string Summary(BridgeDesign d,LevelDefinition level,GameDatabase db)=>string.Join("\n",LiftStructure.Analyze(d,level,db).Select(l=>LiftStructure.Name(l)+"："+(l.Problem??"机构已连接；测试将校核平台、液压拉力、绳张力与支点反力")));
    }
}
