using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BridgeArchitect3D.Data
{
    public enum MechanismDiagnosticKind { None, Underconstrained, DeadPoint, ConstraintConflict }
    /// <summary>Typed scene references extracted from diagnostic text; node and member ids are separate namespaces.</summary>
    public sealed class DiagnosticTargets
    {
        public int SourceMember { get; private set; } = -1;
        public MechanismDiagnosticKind Kind { get; private set; }
        public HashSet<int> Members { get; } = new();
        public HashSet<int> BlockingMembers { get; } = new();
        public HashSet<int> Nodes { get; } = new();
        public bool HasTargets => Members.Count>0 || Nodes.Count>0 || SourceMember>=0;
        public static DiagnosticTargets Parse(string message,int sourceMember=-1)
        {
            var result=new DiagnosticTargets {SourceMember=sourceMember};
            if(string.IsNullOrEmpty(message)) return result;
            result.Kind=message.Contains("机构欠约束：") ? MechanismDiagnosticKind.Underconstrained :
                message.Contains("几何死点：") ? MechanismDiagnosticKind.DeadPoint :
                message.Contains("约束冲突：") ? MechanismDiagnosticKind.ConstraintConflict : MechanismDiagnosticKind.None;
            foreach(Match m in Regex.Matches(message,@"(?:构件|液压杆|钢梁|桥面)\s*#\s*(\d+)"))
                if(int.TryParse(m.Groups[1].Value,out int id)) result.Members.Add(id);
            foreach(Match m in Regex.Matches(message,@"(?:转轴节点|节点)\s*#\s*(\d+)"))
                if(int.TryParse(m.Groups[1].Value,out int id)) result.Nodes.Add(id);
            foreach(Match m in Regex.Matches(message,@"构件\s*#\s*(\d+)[^\r\n]{0,70}(?:锁死|阻碍|阻挡)"))
                if(int.TryParse(m.Groups[1].Value,out int id)) result.BlockingMembers.Add(id);
            result.Members.Remove(sourceMember);
            result.BlockingMembers.Remove(sourceMember);
            if(result.Kind==MechanismDiagnosticKind.ConstraintConflict) result.BlockingMembers.UnionWith(result.Members);
            return result;
        }
    }
}
