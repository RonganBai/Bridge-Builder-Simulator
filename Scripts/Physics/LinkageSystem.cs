using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Physics
{
    public enum LinkageFaultKind { None, Underconstrained, DeadPoint, ConstraintConflict, Other }
    /// <summary>
    /// Quasi-static planar pin-jointed linkage. Distances, rather than a guessed leaf/pivot,
    /// determine motion. Drivers prescribe lengths; passive members retain their built length.
    /// No coordinates are committed until all constraints converge and the Jacobian has full rank.
    /// </summary>
    public sealed class LinkageSystem
    {
        public const double LengthTolerance = 1e-6;
        private StructuralModel _model;
        private double[] _rest, _lastX, _lastY;
        private int[] _variable;
        private int _count;
        private readonly Dictionary<int, double> _drivers = new();
        private readonly List<(int driver, int first, int second)> _triangles = new();
        private const double MinimumTriangleSine = 0.075;
        private bool[] _pins, _releaseA, _releaseB;
        public bool Ready { get; private set; }
        public string Fault { get; private set; }
        public LinkageFaultKind FaultKind { get; private set; }
        public HashSet<int> FaultNodes { get; } = new();
        public HashSet<int> FaultMembers { get; } = new();
        public double Progress { get; private set; }
        public bool PendingForceCheck { get; private set; }
        private double _previousProgress;
        public IReadOnlyCollection<int> Drivers => _drivers.Keys;

        public bool Initialize(StructuralModel model, LinkageData settings)
        {
            if(Ready) Deactivate();
            _drivers.Clear(); _triangles.Clear(); _count=0; Progress=0; Fault=null; FaultKind=LinkageFaultKind.None;
            FaultNodes.Clear(); FaultMembers.Clear(); PendingForceCheck=false; Ready=false;
            _model = model;
            if(settings?.Drivers==null) return Fail("联动驱动配置为空");
            _rest = (double[])model.Length.Clone();
            _pins = (bool[])model.Pinned.Clone();
            _releaseA=(bool[])model.ReleaseA.Clone();_releaseB=(bool[])model.ReleaseB.Clone();
            _lastX = (double[])model.X.Clone(); _lastY = (double[])model.Y.Clone();
            _variable = Enumerable.Repeat(-1, model.NodeCount * 2).ToArray();
            foreach (var driver in settings.Drivers)
            {
                if (!model.MemberIndex.TryGetValue(driver.MemberId, out int k) || !model.Mat[k].Extendable || model.Broken[k])
                    return Fail($"驱动液压杆 #{driver.MemberId} 不存在或已失效");
                var mat = model.Mat[k];
                double stroke = Math.Abs(driver.Ratio);
                if (!double.IsFinite(driver.Ratio) || stroke < LinkageDriverData.MinStroke - 1e-9 || stroke > LinkageDriverData.MaxStroke + 1e-9)
                    return Fail($"液压杆 #{driver.MemberId} 行程必须在 {LinkageDriverData.MinStroke:P0}～{LinkageDriverData.MaxStroke:P0} 之间");
                if (!_drivers.TryAdd(k, driver.Ratio)) return Fail($"液压杆 #{driver.MemberId} 重复配置");
            }
            if (_drivers.Count == 0) return Fail("请选择至少一根通用联动驱动杆");
            // Recognize closed triangular loops around a driver, including other hydraulic sides.
            foreach (int k in _drivers.Keys)
                for (int a = 0; a < model.MemberCount; a++)
                {
                    if (a == k || model.Broken[a]) continue;
                    int common = model.A[a] == model.A[k] ? model.B[a] : model.B[a] == model.A[k] ? model.A[a] : -1;
                    if (common < 0 || common == model.B[k]) continue;
                    for (int b = 0; b < model.MemberCount; b++)
                        if (b != k && b != a && !model.Broken[b] &&
                            ((model.A[b] == common && model.B[b] == model.B[k]) || (model.B[b] == common && model.A[b] == model.B[k])))
                            _triangles.Add((k, a, b));
                }
            // Only the connected components of selected drivers become pin-jointed linkages.
            var connected = new HashSet<int>(_drivers.Keys.SelectMany(k => new[] { model.A[k], model.B[k] }));
            bool changed;
            do
            {
                changed = false;
                for (int k=0; k<model.MemberCount; k++)
                {
                    if (model.Broken[k]) continue;
                    if (connected.Contains(model.A[k]) && connected.Add(model.B[k])) changed=true;
                    if (connected.Contains(model.B[k]) && connected.Add(model.A[k])) changed=true;
                }
            } while (changed);
            // These constraints belong to the structure, not to a successful actuator plan.
            // Keep them across failed driver attempts and fallback to a smaller driver set.
            RigidFrameDiscovery.Discover(model, connected);
            for (int k=0; k<model.MemberCount; k++)
            {
                if (model.Broken[k] || !connected.Contains(model.A[k])) continue;
                if (model.Mat[k].IsCable || model.Mat[k].IsColumn)
                    return Fail("通用连杆组件暂不支持钢索或立柱；请将驱动组件与这些构件分开");
            }
            for (int i=0; i<model.NodeCount; i++)
            {
                if (!connected.Contains(i)) continue;
                if (!model.FixX[i]) _variable[2*i]=_count++;
                if (!model.FixY[i]) _variable[2*i+1]=_count++;
            }
            if (_count == 0 || _count > 160) return Fail("联动组件没有可动自由度，或超过 80 个自由节点的求解规模");
            for (int k=0; k<model.MemberCount; k++)
                if (!model.Broken[k] && connected.Contains(model.A[k])) model.Pinned[k]=true;
            ApplyRigidSplits();
            Ready=true;
            if (!TryMove(0)) { Deactivate(); return false; }
            return true;
        }

        /// <summary>Probe actual motion, then pin only the affected mechanism, not the whole connected bridge.</summary>
        public bool RestrictToDrivenMotion()
        {
            if(!Ready || !TryMove(0.001)) return false;
            var moving=Enumerable.Range(0,_model.NodeCount).Where(i=>
                Math.Abs(_model.X[i]-_lastX[i])+Math.Abs(_model.Y[i]-_lastY[i])>1e-9).ToHashSet();
            Rollback(); PendingForceCheck=false;
            if(moving.Count==0) return Fail("所选液压杆没有可实现的行程；请检查固定支撑或零行程设置");
            Array.Copy(_pins,_model.Pinned,_pins.Length);
            Array.Copy(_releaseA,_model.ReleaseA,_releaseA.Length);
            Array.Copy(_releaseB,_model.ReleaseB,_releaseB.Length);
            _count=0;
            for(int i=0;i<_model.NodeCount;i++)
            {
                _variable[2*i]=moving.Contains(i) && !_model.FixX[i]?_count++:-1;
                _variable[2*i+1]=moving.Contains(i) && !_model.FixY[i]?_count++:-1;
            }
            for(int k=0;k<_model.MemberCount;k++)
                if(!_model.Broken[k] && (moving.Contains(_model.A[k]) || moving.Contains(_model.B[k]))) _model.Pinned[k]=true;
            ApplyRigidSplits();
            return TryMove(0);
        }

        /// <summary>Shared stationary supports are allowed; shared moving nodes or dependencies are not.</summary>
        public bool CanRunAlongside(LeafGroupPlan group)
        {
            var dependencies=new HashSet<int>();
            for(int k=0;k<_model.MemberCount;k++)
                if(IsMovingNode(_model.A[k]) || IsMovingNode(_model.B[k]) || _drivers.ContainsKey(k))
                { dependencies.Add(_model.A[k]); dependencies.Add(_model.B[k]); }
            foreach(int key in group.LeafKeys)
                if(_model.NodeIndex.TryGetValue(key,out int i) && dependencies.Contains(i)) return false;
            foreach(int id in group.MemberIds.Concat(group.Actuators.Select(a=>a.MemberId)))
                if(_model.MemberIndex.TryGetValue(id,out int k) && (IsMovingNode(_model.A[k]) || IsMovingNode(_model.B[k]))) return false;
            return true;
        }

        public bool IsMovingNode(int i) => Ready && (_variable[2*i]>=0 || _variable[2*i+1]>=0);
        private bool Fail(string message) { Fault=message; FaultKind=LinkageFaultKind.Other; return false; }
        public void Deactivate()
        {
            Ready=false;
            if (_pins != null) { Array.Copy(_pins, _model.Pinned, _pins.Length);Array.Copy(_releaseA,_model.ReleaseA,_releaseA.Length);Array.Copy(_releaseB,_model.ReleaseB,_releaseB.Length); }
        }

        /// <summary>Damped Newton continuation from the last accepted configuration.</summary>
        public bool TryMove(double progress)
        {
            if (!Ready || !double.IsFinite(progress) || progress<0 || progress>1) return false;
            if (_model.RigidBodies.Any(body=>body.nodes.Any(IsVariable) && body.members.Any(k=>_model.Broken[k])))
                return Fail("主桥叶构件已断裂，联合机构停止；请修复从动结构");
            if (_model.RigidSplits.Any(j=>IsVariable(j.node) && j.members.Any(k=>_model.Broken[k])))
                return Fail("刚性原构件已断裂，无法继续驱动；请修复从动结构");
            if (!ValidateTriangles(progress)) return false;
            var x=(double[])_model.X.Clone(); var y=(double[])_model.Y.Clone();
            int worst=-1;
            bool converged=false;
            for (int iter=0; iter<80; iter++)
            {
                Build(x,y,progress,out var normal,out var rhs,out double error,out worst);
                if (error < LengthTolerance)
                {
                    // An undetermined mechanism must not select an arbitrary branch using damping alone.
                    if (!Solve(normal, new double[_count], _count, out _))
                        return DiagnoseRank(x,y,normal);
                    converged=true; break;
                }
                bool accepted=false;
                for (double damping=1e-8; damping<=1; damping*=10)
                {
                    var matrix=(double[])normal.Clone();
                    for(int i=0;i<_count;i++) matrix[i*_count+i]+=damping;
                    if (!Solve(matrix,rhs,_count,out var step)) continue;
                    // Bound each local Newton step to avoid jumping between remote assembly branches.
                    double largest=step.Select(Math.Abs).DefaultIfEmpty(0).Max();
                    double initial=largest>0.1?0.1/largest:1;
                    for(double alpha=initial; alpha>=initial/128; alpha*=0.5)
                    {
                        var nx=(double[])x.Clone(); var ny=(double[])y.Clone();
                        for(int i=0;i<_model.NodeCount;i++)
                        {
                            if(_variable[2*i]>=0) nx[i]+=alpha*step[_variable[2*i]];
                            if(_variable[2*i+1]>=0) ny[i]+=alpha*step[_variable[2*i+1]];
                        }
                        Build(nx,ny,progress,out _,out _,out double next,out _);
                        if(next>=error) continue;
                        x=nx; y=ny; accepted=true; break;
                    }
                    if(accepted) break;
                }
                if(!accepted) break;
            }
            if(!converged)
            {
                Build(x,y,progress,out var normal,out _,out _,out _);
                if(!Solve(normal,new double[_count],_count,out _)) return DiagnoseRank(x,y,normal);
                return ConstraintFailure(x,y,progress,worst);
            }
            Array.Copy(_model.X,_lastX,x.Length); Array.Copy(_model.Y,_lastY,y.Length);
            _previousProgress=Progress;
            PendingForceCheck=Math.Abs(progress-Progress)>1e-12;
            Array.Copy(x,_model.X,x.Length); Array.Copy(y,_model.Y,y.Length);
            Progress=progress;
            Refresh(); Fault=null; FaultKind=LinkageFaultKind.None; FaultNodes.Clear(); FaultMembers.Clear(); return true;
        }

        private double TargetLength(int member, double progress) =>
            _rest[member] * (1 + (_drivers.TryGetValue(member, out double ratio) ? ratio * progress : 0));

        private string TargetDescription() => string.Join("、", FaultNodes.OrderBy(id=>id).Select(id=>$"节点 #{id}")) +
            (FaultMembers.Count==0 ? "" : "；"+string.Join("、",FaultMembers.OrderBy(id=>id).Select(id=>$"构件 #{id}")));

        private void MarkMember(int k)
        {
            FaultMembers.Add(_model.MemberIds[k]);
            FaultNodes.Add(Math.Abs(_model.NodeIds[_model.A[k]]));
            FaultNodes.Add(Math.Abs(_model.NodeIds[_model.B[k]]));
        }

        private bool TriangleFailure((int driver,int first,int second) triangle,LinkageFaultKind kind,string message)
        {
            FaultNodes.Clear(); FaultMembers.Clear();
            MarkMember(triangle.driver); MarkMember(triangle.first); MarkMember(triangle.second);
            FaultKind=kind; Fault=message+"。涉及："+TargetDescription(); return false;
        }

        // RREF exposes the full null-space support, rather than blaming the last member visited.
        private static int Rank(double[] matrix,int n,out bool[] ambiguous)
        {
            var a=(double[])matrix.Clone();var pivots=new List<int>();int row=0;
            for(int c=0;c<n && row<n;c++)
            {
                int best=row;for(int r=row+1;r<n;r++)if(Math.Abs(a[r*n+c])>Math.Abs(a[best*n+c]))best=r;
                if(Math.Abs(a[best*n+c])<1e-9)continue;
                for(int j=0;j<n;j++)(a[row*n+j],a[best*n+j])=(a[best*n+j],a[row*n+j]);
                double scale=a[row*n+c];for(int j=0;j<n;j++)a[row*n+j]/=scale;
                for(int r=0;r<n;r++)if(r!=row)
                {double factor=a[r*n+c];for(int j=0;j<n;j++)a[r*n+j]-=factor*a[row*n+j];}
                pivots.Add(c);row++;
            }
            ambiguous=new bool[n];
            foreach(int free in Enumerable.Range(0,n).Except(pivots))
            {
                ambiguous[free]=true;
                for(int r=0;r<pivots.Count;r++)if(Math.Abs(a[r*n+free])>1e-6)ambiguous[pivots[r]]=true;
            }
            return row;
        }

        private bool DiagnoseRank(double[] x,double[] y,double[] normal)
        {
            int rank=Rank(normal,_count,out var ambiguous),genericRank=rank;
            // Deterministic off-pose probes are diagnostic only and never committed. A rank
            // recovered away from the current geometry indicates a singular assembly, whereas
            // a persistent deficiency indicates missing constraints in the topology.
            for(int probe=1;probe<=3;probe++)
            {
                var px=(double[])x.Clone();var py=(double[])y.Clone();
                for(int i=0;i<_model.NodeCount;i++)
                {
                    if(_variable[2*i]>=0)px[i]+=0.03*Math.Sin((i+1)*1.73*probe);
                    if(_variable[2*i+1]>=0)py[i]+=0.03*Math.Cos((i+1)*2.31*probe);
                }
                Build(px,py,Progress,out var perturbed,out _,out _,out _);
                genericRank=Math.Max(genericRank,Rank(perturbed,_count,out _));
            }
            FaultNodes.Clear();FaultMembers.Clear();
            var affected=new HashSet<int>();
            for(int i=0;i<_model.NodeCount;i++)
                if((_variable[2*i]>=0 && ambiguous[_variable[2*i]]) ||
                   (_variable[2*i+1]>=0 && ambiguous[_variable[2*i+1]]))
                {affected.Add(i);FaultNodes.Add(Math.Abs(_model.NodeIds[i]));}
            for(int k=0;k<_model.MemberCount;k++)
                if(!_model.Broken[k] && (affected.Contains(_model.A[k]) || affected.Contains(_model.B[k])))
                    FaultMembers.Add(_model.MemberIds[k]);
            FaultKind=genericRank<_count ? LinkageFaultKind.Underconstrained : LinkageFaultKind.DeadPoint;
            Fault=FaultKind==LinkageFaultKind.Underconstrained
                ? $"机构欠约束：仍有 {_count-genericRank} 个未驱动自由度；请在高亮位置增加有效支撑或明确选择所需驱动。"
                : "几何死点：当前姿态使约束方向退化；请调整高亮位置的初始夹角或支点。";
            Fault+="涉及："+TargetDescription();return false;
        }

        private bool ConstraintFailure(double[] x,double[] y,double progress,int worst)
        {
            FaultNodes.Clear();FaultMembers.Clear();
            for(int k=0;k<_model.MemberCount;k++)
            {
                if(_model.Broken[k] || !(IsVariable(_model.A[k]) || IsVariable(_model.B[k]) || _drivers.ContainsKey(k)))continue;
                int a=_model.A[k],b=_model.B[k];
                double length=Math.Sqrt(Math.Pow(x[b]-x[a],2)+Math.Pow(y[b]-y[a],2));
                if(Math.Abs(length-TargetLength(k,progress))>LengthTolerance)MarkMember(k);
            }
            foreach(var body in _model.RigidBodies)
                if(body.nodes.Any(IsVariable) && body.nodes.Any(a=>body.nodes.Any(b=>
                    Math.Abs(Math.Sqrt(Math.Pow(x[b]-x[a],2)+Math.Pow(y[b]-y[a],2))-
                        Math.Sqrt(Math.Pow(_model.X0[b]-_model.X0[a],2)+Math.Pow(_model.Y0[b]-_model.Y0[a],2)))>LengthTolerance)))
                    foreach(int k in body.members)MarkMember(k);
            foreach(var joint in _model.RigidSplits)
                if(IsVariable(joint.node) &&
                   (Math.Abs(x[joint.node]-(1-joint.fraction)*x[joint.a]-joint.fraction*x[joint.b])>LengthTolerance ||
                    Math.Abs(y[joint.node]-(1-joint.fraction)*y[joint.a]-joint.fraction*y[joint.b])>LengthTolerance))
                    foreach(int k in joint.members)MarkMember(k);
            if(FaultMembers.Count==0 && worst>=0)MarkMember(worst);
            FaultKind=LinkageFaultKind.ConstraintConflict;
            Fault="约束冲突：所选行程与高亮杆件或刚性连接无法同时满足；已保留上一稳定位置。涉及："+TargetDescription();
            return false;
        }

        private static double TriangleSine(double a, double b, double c)
        {
            double longest = Math.Max(a, Math.Max(b, c));
            double shortest = Math.Min(a, Math.Min(b, c));
            double middle = a + b + c - longest - shortest;
            // Heron's formula, normalized by the two longest sides; zero at a toggle position.
            double twiceAreaSquared = (longest + (middle + shortest)) * (shortest - (longest - middle)) *
                (shortest + (longest - middle)) * (longest + (middle - shortest)) / 4;
            return Math.Sqrt(Math.Max(0, twiceAreaSquared)) / (longest * middle);
        }

        private bool ValidateTriangles(double progress)
        {
            foreach (var triangle in _triangles)
            {
                double a = TargetLength(triangle.driver, progress), b = TargetLength(triangle.first, progress), c = TargetLength(triangle.second, progress);
                double minimum = Math.Abs(b - c), maximum = b + c;
                if (a <= minimum + LengthTolerance || a >= maximum - LengthTolerance)
                    return TriangleFailure(triangle, LinkageFaultKind.ConstraintConflict,
                        $"约束冲突：三角形无法闭合，目标 {a:0.000} m，可行范围 {minimum:0.000}～{maximum:0.000} m");
                double quality = TriangleSine(a, b, c);
                double current = TriangleSine(_model.Length[triangle.driver], _model.Length[triangle.first], _model.Length[triangle.second]);
                if (progress != Progress && quality < MinimumTriangleSine && quality < current - 1e-9)
                    return TriangleFailure(triangle, LinkageFaultKind.DeadPoint,
                        "几何死点：三角形接近共线死点；已保留上一稳定位置，请调整支点或初始夹角");
            }
            return true;
        }

        /// <summary>Slow down near high mechanical advantage instead of jumping between distant poses.</summary>
        public bool TryMoveLimited(double progress, double maximumNodeTravel)
        {
            if (!double.IsFinite(maximumNodeTravel) || maximumNodeTravel <= 0) return false;
            double start = Progress;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (!TryMove(progress)) return false;
                double travel = 0;
                for (int i = 0; i < _model.NodeCount; i++)
                    if (IsMovingNode(i))
                        travel = Math.Max(travel, Math.Sqrt(Math.Pow(_model.X[i] - _lastX[i], 2) + Math.Pow(_model.Y[i] - _lastY[i], 2)));
                if (travel <= maximumNodeTravel) return true;
                Rollback(); PendingForceCheck = false;
                progress = start + (progress - start) / 2;
            }
            return Fail("联动机构接近运动奇异点，已保留上一稳定位置");
        }

        private void Build(double[] x,double[] y,double p,out double[] normal,out double[] rhs,out double error,out int worst)
        {
            normal=new double[_count*_count]; rhs=new double[_count]; error=0; worst=-1;
            for(int k=0;k<_model.MemberCount;k++)
            {
                if(_model.Broken[k]) continue;
                int a=_model.A[k], b=_model.B[k];
                if(_variable[2*a]<0 && _variable[2*a+1]<0 && _variable[2*b]<0 && _variable[2*b+1]<0 && !_drivers.ContainsKey(k)) continue;
                double dx=x[b]-x[a],dy=y[b]-y[a],len=Math.Sqrt(dx*dx+dy*dy);
                double target=_rest[k]*(1+(_drivers.TryGetValue(k,out double ratio)?ratio*p:0));
                double residual=len-target;
                if(Math.Abs(residual)>error){error=Math.Abs(residual); worst=k;}
                if(len<1e-12) continue;
                int[] ids={_variable[2*a],_variable[2*a+1],_variable[2*b],_variable[2*b+1]};
                double[] jac={-dx/len,-dy/len,dx/len,dy/len};
                for(int i=0;i<4;i++) if(ids[i]>=0)
                {
                    rhs[ids[i]]-=jac[i]*residual;
                    for(int j=0;j<4;j++) if(ids[j]>=0) normal[ids[i]*_count+ids[j]]+=jac[i]*jac[j];
                }
            }
            foreach(var body in _model.RigidBodies)
                for(int ia=0;ia<body.nodes.Length;ia++)for(int ib=ia+1;ib<body.nodes.Length;ib++)
                {
                    int a=body.nodes[ia],b=body.nodes[ib];double dx=x[b]-x[a],dy=y[b]-y[a],len=Math.Sqrt(dx*dx+dy*dy);
                    double ox=_model.X0[b]-_model.X0[a],oy=_model.Y0[b]-_model.Y0[a];double residual=len-Math.Sqrt(ox*ox+oy*oy);
                    if(Math.Abs(residual)>error){error=Math.Abs(residual);worst=-1;}if(len<1e-12)continue;
                    int[] ids={_variable[2*a],_variable[2*a+1],_variable[2*b],_variable[2*b+1]};double[] jac={-dx/len,-dy/len,dx/len,dy/len};
                    for(int i=0;i<4;i++)if(ids[i]>=0){rhs[ids[i]]-=jac[i]*residual;for(int j=0;j<4;j++)if(ids[j]>=0)normal[ids[i]*_count+ids[j]]+=jac[i]*jac[j];}
                }
            foreach(var joint in _model.RigidSplits)
                for(int axis=0;axis<2;axis++)
                {
                    int[] nodes={joint.node,joint.a,joint.b};double[] weights={1,-(1-joint.fraction),-joint.fraction};
                    double residual=0;
                    for(int i=0;i<3;i++)residual+=weights[i]*(axis==0?x[nodes[i]]:y[nodes[i]]);
                    if(Math.Abs(residual)>error){error=Math.Abs(residual);worst=-1;}
                    for(int i=0;i<3;i++)
                    {
                        int vi=_variable[2*nodes[i]+axis];if(vi<0)continue;
                        rhs[vi]-=weights[i]*residual;
                        for(int j=0;j<3;j++){int vj=_variable[2*nodes[j]+axis];if(vj>=0)normal[vi*_count+vj]+=weights[i]*weights[j];}
                    }
                }
        }

        private void ApplyRigidSplits()
        {
            foreach(var body in _model.RigidBodies)
                if(body.nodes.Any(IsVariable))
                {
                bool Boundary(int node) => Enumerable.Range(0,_model.MemberCount).Any(k=>
                    !_model.Broken[k] && !_model.Mat[k].Extendable && !body.members.Contains(k) &&
                    (_model.A[k]==node || _model.B[k]==node) &&
                    (!IsVariable(node) || _model.RigidBodies.Any(other=>other.members.Contains(k))));
                foreach(int k in body.members)
                {
                    _model.Pinned[k]=false;
                    _model.ReleaseA[k]=_model.A[k]==body.pivot || _model.SplitJoints.Contains(_model.A[k]) || Boundary(_model.A[k]);
                    _model.ReleaseB[k]=_model.B[k]==body.pivot || _model.SplitJoints.Contains(_model.B[k]) || Boundary(_model.B[k]);
                }
                }
            foreach(var joint in _model.RigidSplits)
                for(int k=0;k<_model.MemberCount;k++)
                    if(IsVariable(joint.node) && (_model.A[k]==joint.node || _model.B[k]==joint.node) && !_model.Mat[k].Extendable)
                    {
                        _model.Pinned[k]=false;
                        // The split itself transmits moments; other ends may still be mechanism hinges.
                        _model.ReleaseA[k]=!_model.RigidSplits.Any(j=>j.node==_model.A[k]);
                        _model.ReleaseB[k]=!_model.RigidSplits.Any(j=>j.node==_model.B[k]);
                    }
        }
        private bool IsVariable(int i) => _variable[2*i]>=0 || _variable[2*i+1]>=0;

        private static bool Solve(double[] matrix,double[] load,int n,out double[] solution)
        {
            var a=(double[])matrix.Clone(); solution=(double[])load.Clone();
            for(int c=0;c<n;c++)
            {
                int pivot=c;
                for(int r=c+1;r<n;r++) if(Math.Abs(a[r*n+c])>Math.Abs(a[pivot*n+c])) pivot=r;
                if(Math.Abs(a[pivot*n+c])<1e-10) return false;
                if(pivot!=c){for(int j=c;j<n;j++) (a[c*n+j],a[pivot*n+j])=(a[pivot*n+j],a[c*n+j]); (solution[c],solution[pivot])=(solution[pivot],solution[c]);}
                for(int r=c+1;r<n;r++)
                {
                    double f=a[r*n+c]/a[c*n+c];
                    for(int j=c;j<n;j++) a[r*n+j]-=f*a[c*n+j];
                    solution[r]-=f*solution[c];
                }
            }
            for(int r=n-1;r>=0;r--){for(int j=r+1;j<n;j++) solution[r]-=a[r*n+j]*solution[j]; solution[r]/=a[r*n+r]; if(!double.IsFinite(solution[r])) return false;}
            return true;
        }
        private void Refresh()
        {
            for(int k=0;k<_model.MemberCount;k++)
            {
                double mass=_model.Mass[k]; _model.UpdateMemberGeometry(k); _model.Mass[k]=mass;
            }
        }
        public void Rollback()
        {
            for(int i=0;i<_model.NodeCount;i++)
            {
                if(_variable[2*i]>=0) _model.X[i]=_lastX[i];
                if(_variable[2*i+1]>=0) _model.Y[i]=_lastY[i];
            }
            Progress=_previousProgress; Refresh();
        }
        public bool CheckForces(SolveOutput output)
        {
            if(!PendingForceCheck) return true;
            PendingForceCheck=false;
            foreach(int k in _drivers.Keys)
                if(Math.Abs(output.AxialForce[k])>_model.Mat[k].ActuatorForce)
                {
                    Rollback(); return Fail($"液压杆 #{_model.MemberIds[k]} 推拉力不足：需要 {Math.Abs(output.AxialForce[k])/1000:0.0} kN，最大 {_model.Mat[k].ActuatorForce/1000:0.0} kN");
                }
            return true;
        }
        public void Displacement(double[] values)
        {
            for(int i=0;i<_model.NodeCount;i++){values[2*i]=_model.X[i]-_model.X0[i]; values[2*i+1]=_model.Y[i]-_model.Y0[i];}
        }
    }
}
