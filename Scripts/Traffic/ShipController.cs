using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.Traffic
{
    /// <summary>
    /// 船只的程序化 3D 模型（船体 + 甲板舱 + 烟囱），沿河道（Z 方向）航行。
    /// 尺寸取自交通事件（船长、船宽、水面以上高度），与车辆比例一致。
    /// 船是否撞桥由 BridgeSimulation 的净空检查判定，这里只负责显示。
    /// </summary>
    public partial class ShipController : Node3D
    {
        public void Setup(TrafficEvent ev, double waterLevel)
        {
            float len = (float)ev.Length, w = (float)ev.Width, h = (float)ev.Height;
            var hull = new StandardMaterial3D { AlbedoColor = new Color(0.75f, 0.2f, 0.18f), Roughness = 0.6f };
            var deck = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.92f, 0.9f), Roughness = 0.7f };
            var dark = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.2f, 0.24f), Roughness = 0.6f };
            // 船体：吃水 0.6 m，水面以上约占总高度的 40%
            float hullH = h * 0.4f;
            Add(new Vector3(0, hullH / 2 - 0.3f, 0), new Vector3(w, hullH + 0.6f, len), hull);
            Add(new Vector3(0, hullH + 0.02f, 0), new Vector3(w * 0.96f, 0.05f, len * 0.98f), deck);
            // 舱室与驾驶室
            float cabinH = h - hullH - 0.5f;
            Add(new Vector3(0, hullH + cabinH / 2, -len * 0.15f), new Vector3(w * 0.7f, cabinH, len * 0.35f), deck);
            Add(new Vector3(0, hullH + cabinH * 0.75f, -len * 0.15f + len * 0.175f + 0.01f), new Vector3(w * 0.6f, cabinH * 0.25f, 0.05f), dark);
            // 烟囱（最高点 = 船高）
            Add(new Vector3(0, h - 0.25f, -len * 0.3f), new Vector3(0.5f, 0.5f, 0.5f), dark);
            Position = new Vector3((float)ev.LaneX, (float)waterLevel, (float)ShipStateStartZ());
        }

        private static double ShipStateStartZ() => Physics.ShipState.StartZ;

        private void Add(Vector3 pos, Vector3 size, Material mat)
        {
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat, Position = pos });
        }

        public void SetZ(float z) => Position = new Vector3(Position.X, Position.Y, z);
    }
}
