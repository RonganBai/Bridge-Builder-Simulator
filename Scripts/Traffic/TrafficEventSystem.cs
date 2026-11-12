using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.Traffic
{
    /// <summary>
    /// 交通事件系统：按时间顺序触发关卡中定义的事件。
    /// 初版只处理车辆事件；Train / Ship / Environment 事件类型已在数据中预留，后续在 Dispatch 中扩展。
    /// </summary>
    public class TrafficEventSystem
    {
        private readonly List<TrafficEvent> _events;
        private int _next;

        public TrafficEventSystem(IEnumerable<TrafficEvent> events)
        {
            _events = events.OrderBy(e => e.Time).ToList();
        }

        public int Count => _events.Count;
        public IReadOnlyList<TrafficEvent> Events => _events;
        public void Reset() => _next = 0;

        /// <summary>The next event that has not fired yet (null when all fired), without consuming it.</summary>
        public TrafficEvent PeekNext() => _next < _events.Count ? _events[_next] : null;

        /// <summary>Fires the next event now, ahead of its scheduled time.</summary>
        public TrafficEvent PopNext() => _next < _events.Count ? _events[_next++] : null;

        /// <summary>返回当前时间之前尚未触发的下一个事件（无则返回 null）。</summary>
        public TrafficEvent PopDue(double time)
        {
            if (_next < _events.Count && _events[_next].Time <= time + 1e-9) return _events[_next++];
            return null;
        }
    }
}
