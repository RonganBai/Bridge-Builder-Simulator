using System;
using System.Collections.Generic;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.Traffic
{
    /// <summary>
    /// 车辆的程序化 3D 模型：轿车、货车、公交车、坦克、货运列车（多节车厢）、行人队伍。
    /// 每一节车身（或每个行人）都按自己的前后位置沿真实桥面路径摆放，因此能跟随桥面的坡度与变形。
    /// 车辆对结构的作用通过 BridgeSimulation 的车轴载荷实现，这里只负责显示。
    /// 尺寸按真实比例：轿车 4.4 m、货车 8 m、公交车 11 m、坦克 7 m、每节车厢 15 m、行人身高约 1.75 m。
    /// </summary>
    public partial class VehicleController : Node3D
    {
        public VehicleDefinition Def { get; private set; }

        /// <summary>一个随路径摆放的部件：front/rear 为相对车头的偏移（m，向后为负）。</summary>
        private class Unit { public Node3D Node; public float Front, Rear; }
        private readonly List<Unit> _units = new();
        private bool _falling;
        private Vector3 _fallVel;
        private const float WheelR = 0.45f;

        public void Setup(VehicleDefinition def)
        {
            Def = def;
            var color = MaterialManager.ParseColor(def.Color);
            var body = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.45f, Metallic = 0.25f };
            var dark = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.13f), Roughness = 0.8f };
            var glass = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.75f, 0.9f), Roughness = 0.1f, Metallic = 0.5f };
            var light = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.87f, 0.88f), Roughness = 0.6f };
            float w = (float)def.Width, h = (float)def.Height, len = (float)def.Length;
            float rear = 0;
            foreach (var a in def.Axles) rear = Mathf.Min(rear, (float)a.Offset);

            // 共用的少量材质：轮胎、轮毂、车灯、尾灯（每辆车只创建一次）
            var chrome = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.82f, 0.85f), Roughness = 0.25f, Metallic = 0.9f };
            var head = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.97f, 0.85f), EmissionEnabled = true, Emission = new Color(1f, 0.95f, 0.8f), EmissionEnergyMultiplier = 0.8f };
            var tail = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.1f, 0.1f), EmissionEnabled = true, Emission = new Color(0.9f, 0.1f, 0.1f), EmissionEnergyMultiplier = 0.6f };
            var trim = new StandardMaterial3D { AlbedoColor = color.Darkened(0.35f), Roughness = 0.6f };

            switch (def.Kind)
            {
                case "train":
                {
                    // 每节车厢一个部件：车厢长 UnitLength，两端各一个转向架；机车带驾驶室窗、头灯和排障器
                    float ul = (float)(def.UnitLength > 0 ? def.UnitLength : len / Math.Max(1, def.Units));
                    for (int i = 0; i < Math.Max(1, def.Units); i++)
                    {
                        var n = NewUnit(-i * ul, -(i + 1) * ul + 0.6f);
                        float cl = ul - 0.6f, baseY = WheelR + 0.25f;
                        bool loco = i == 0;
                        AddBox(n, new Vector3(-cl / 2, baseY + h * 0.45f, 0), new Vector3(cl, h * 0.9f, w), loco ? body : light);
                        AddBox(n, new Vector3(-cl / 2, baseY + h * 0.92f, 0), new Vector3(cl - 0.4f, 0.12f, w * 0.85f), trim);          // 车顶
                        AddBox(n, new Vector3(-cl / 2, WheelR + 0.1f, 0), new Vector3(cl, 0.25f, w * 0.85f), dark);                       // 底架
                        if (loco)
                        {
                            AddBox(n, new Vector3(-1.1f, baseY + h * 0.68f, 0), new Vector3(1.6f, h * 0.28f, w * 1.01f), glass);          // 驾驶室窗
                            AddBox(n, new Vector3(0.05f, baseY + 0.35f, 0), new Vector3(0.3f, 0.5f, w * 0.9f), dark);                     // 排障器
                            for (int s = -1; s <= 1; s += 2) AddBox(n, new Vector3(0.02f, baseY + h * 0.35f, s * (w / 2 - 0.35f)), new Vector3(0.06f, 0.22f, 0.3f), head);
                        }
                        else
                        {
                            for (int r = 1; r <= 4; r++) AddBox(n, new Vector3(-cl * r / 5f, baseY + h * 0.45f, 0), new Vector3(0.08f, h * 0.88f, w * 1.02f), trim);   // 车厢加强筋
                        }
                        AddBox(n, new Vector3(-cl - 0.3f, baseY + 0.3f, 0), new Vector3(0.6f, 0.18f, 0.3f), dark);                       // 车钩
                        foreach (float bx in new[] { -2.5f, -cl + 2.5f })
                        {
                            AddBox(n, new Vector3(bx, WheelR + 0.05f, 0), new Vector3(2.6f, 0.35f, w * 0.7f), dark);                       // 转向架
                            for (int s = -1; s <= 1; s += 2)
                                foreach (float wx in new[] { bx - 1f, bx + 1f }) AddWheel(n, new Vector3(wx, WheelR, s * (w / 2 - 0.15f)), dark, chrome, 0.25f);
                        }
                    }
                    break;
                }
                case "pedestrians":
                {
                    // 行人队伍：每个“车轴”位置一个人（身高约 1.75 m）：躯干、头、双腿
                    var skin = new StandardMaterial3D { AlbedoColor = new Color(0.93f, 0.78f, 0.62f) };
                    var pants = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.22f, 0.3f) };
                    var palette = new[] { body, new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.35f, 0.3f) }, new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.65f, 0.4f) }, light };
                    var torso = new CapsuleMesh { Radius = 0.2f, Height = 0.75f, RadialSegments = 8, Rings = 2 };
                    var leg = new BoxMesh { Size = new Vector3(0.14f, 0.8f, 0.14f) };
                    var headMesh = new SphereMesh { Radius = 0.13f, Height = 0.26f, RadialSegments = 8, Rings = 4 };
                    int i = 0;
                    foreach (var a in def.Axles)
                    {
                        float off = (float)a.Offset;
                        var n = NewUnit(off + 0.2f, off - 0.2f);
                        float z = (i % 2 == 0 ? -0.6f : 0.6f);
                        n.AddChild(new MeshInstance3D { Mesh = torso, MaterialOverride = palette[i % palette.Length], Position = new Vector3(0, 1.17f, z) });
                        n.AddChild(new MeshInstance3D { Mesh = headMesh, MaterialOverride = skin, Position = new Vector3(0, 1.62f, z) });
                        for (int s = -1; s <= 1; s += 2) n.AddChild(new MeshInstance3D { Mesh = leg, MaterialOverride = pants, Position = new Vector3(0.05f * s * (i % 2 == 0 ? 1 : -1), 0.4f, z + s * 0.09f) });
                        i++;
                    }
                    break;
                }
                case "tank":
                {
                    var n = NewUnit(2.9f, -4.1f);
                    // 履带（重量集中在 1.2 m 的短距离内，对应“单点高负载”）+ 负重轮
                    var roadWheel = new CylinderMesh { TopRadius = 0.32f, BottomRadius = 0.32f, Height = 0.3f, RadialSegments = 10 };
                    for (int s = -1; s <= 1; s += 2)
                    {
                        float tz = s * (w / 2 - 0.4f);
                        AddBox(n, new Vector3(-0.6f, 0.08f, tz), new Vector3(len * 0.82f, 0.16f, 0.8f), dark);       // 履带下段
                        AddBox(n, new Vector3(-0.6f, 0.82f, tz), new Vector3(len * 0.85f, 0.16f, 0.8f), dark);       // 履带上段
                        for (int k = 0; k < 5; k++)
                            n.AddChild(new MeshInstance3D { Mesh = roadWheel, MaterialOverride = trim, Position = new Vector3(1.6f - k * 1.1f, 0.42f, tz + s * 0.25f), RotationDegrees = new Vector3(90, 0, 0) });
                        AddBox(n, new Vector3(-0.6f, 0.95f, s * (w / 2 - 0.35f)), new Vector3(len * 0.88f, 0.08f, 0.95f), body);  // 挡泥板
                    }
                    AddBox(n, new Vector3(-0.8f, 1.2f, 0), new Vector3(len * 0.72f, 0.6f, w * 0.72f), body);                       // 车体
                    var glacis = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.4f, 0.12f, w * 0.72f) }, MaterialOverride = body, Position = new Vector3(2.0f, 1.2f, 0), RotationDegrees = new Vector3(0, 0, -28) };
                    n.AddChild(glacis);                                                                                            // 倾斜首上装甲
                    AddBox(n, new Vector3(-1.0f, 1.82f, 0), new Vector3(2.6f, 0.65f, 2.2f), body);                                  // 炮塔
                    AddBox(n, new Vector3(-1.3f, 2.2f, 0.5f), new Vector3(0.6f, 0.15f, 0.6f), trim);                                // 舱盖
                    n.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.13f, Height = 4.0f, RadialSegments = 8 }, MaterialOverride = dark, Position = new Vector3(1.6f, 1.9f, 0), RotationDegrees = new Vector3(0, 0, 90) });
                    break;
                }
                default:
                {
                    // 轿车 / 货车 / 公交车：单个刚体，按前后轴摆放
                    float overhang = Mathf.Max(0.3f, (len + rear) * 0.5f);
                    float front = overhang, back = rear - overhang;   // 车头 / 车尾 X
                    var n = NewUnit(0, rear);
                    if (def.Kind == "truck")
                    {
                        float cabX = front - 1.1f;
                        AddBox(n, new Vector3(cabX, WheelR + 1.05f, 0), new Vector3(2.2f, 2.0f, w), body);                          // 驾驶室
                        AddBox(n, new Vector3(cabX + 0.35f, WheelR + 1.55f, 0), new Vector3(1.55f, 0.7f, w * 1.01f), glass);           // 车窗
                        AddBox(n, new Vector3(front + 0.02f, WheelR + 0.55f, 0), new Vector3(0.08f, 0.6f, w * 0.7f), dark);            // 格栅
                        AddBox(n, new Vector3(front + 0.08f, WheelR + 0.12f, 0), new Vector3(0.18f, 0.25f, w), dark);                  // 保险杠
                        for (int s = -1; s <= 1; s += 2)
                        {
                            AddBox(n, new Vector3(front + 0.03f, WheelR + 0.4f, s * (w / 2 - 0.25f)), new Vector3(0.06f, 0.18f, 0.32f), head);
                            AddBox(n, new Vector3(cabX + 0.9f, WheelR + 1.55f, s * (w / 2 + 0.12f)), new Vector3(0.1f, 0.3f, 0.06f), dark);   // 后视镜
                            AddBox(n, new Vector3(back - 0.02f, WheelR + 0.6f, s * (w / 2 - 0.2f)), new Vector3(0.05f, 0.18f, 0.25f), tail);
                        }
                        float boxLen = len - 2.6f, boxX = front - 2.5f - boxLen / 2;
                        AddBox(n, new Vector3(boxX, WheelR + h / 2 + 0.1f, 0), new Vector3(boxLen, h - 0.5f, w), light);              // 货箱
                        for (int r = 1; r <= 3; r++) AddBox(n, new Vector3(boxX - boxLen / 2 + boxLen * r / 4f, WheelR + h / 2 + 0.1f, 0), new Vector3(0.06f, h - 0.45f, w * 1.02f), trim);
                        AddBox(n, new Vector3((front + back) / 2, WheelR + 0.15f, 0), new Vector3(len - 0.4f, 0.22f, w * 0.6f), dark); // 底盘大梁
                        n.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 1.2f, RadialSegments = 10 }, MaterialOverride = chrome, Position = new Vector3(front - 2.9f, WheelR + 0.2f, w / 2 - 0.35f), RotationDegrees = new Vector3(0, 0, 90) });   // 油箱
                    }
                    else if (def.Kind == "bus")
                    {
                        AddBox(n, new Vector3((front + back) / 2, WheelR + h / 2, 0), new Vector3(len, h - 0.2f, w), body);
                        AddBox(n, new Vector3((front + back) / 2 - 0.3f, WheelR + h * 0.66f, 0), new Vector3(len * 0.86f, h * 0.3f, w * 1.01f), glass);   // 侧窗带
                        AddBox(n, new Vector3(front + 0.01f, WheelR + h * 0.6f, 0), new Vector3(0.05f, h * 0.5f, w * 0.9f), glass);                         // 前挡风
                        AddBox(n, new Vector3(front + 0.02f, WheelR + h * 0.92f, 0), new Vector3(0.05f, 0.25f, w * 0.6f), head);                           // 线路牌
                        AddBox(n, new Vector3(front - 1.2f, WheelR + h * 0.42f, w / 2 + 0.01f), new Vector3(1.0f, h * 0.75f, 0.04f), trim);                 // 前门
                        AddBox(n, new Vector3((front + back) / 2 - 0.5f, WheelR + h * 0.42f, w / 2 + 0.01f), new Vector3(1.0f, h * 0.75f, 0.04f), trim);    // 中门
                        AddBox(n, new Vector3((front + back) / 2 - 1.5f, WheelR + h + 0.05f, 0), new Vector3(2.0f, 0.3f, w * 0.6f), light);                 // 车顶空调
                        AddBox(n, new Vector3(front + 0.06f, WheelR + 0.15f, 0), new Vector3(0.15f, 0.25f, w), dark);
                        for (int s = -1; s <= 1; s += 2)
                        {
                            AddBox(n, new Vector3(front + 0.03f, WheelR + 0.45f, s * (w / 2 - 0.3f)), new Vector3(0.06f, 0.18f, 0.35f), head);
                            AddBox(n, new Vector3(back - 0.02f, WheelR + 0.55f, s * (w / 2 - 0.25f)), new Vector3(0.05f, 0.3f, 0.2f), tail);
                        }
                    }
                    else
                    {
                        // 轿车：下车身 + 收窄的车舱 + 倾斜挡风玻璃 + 保险杠、灯、后视镜
                        float cx = (front + back) / 2;
                        AddBox(n, new Vector3(cx, WheelR + 0.3f, 0), new Vector3(len, 0.55f, w), body);
                        AddBox(n, new Vector3(cx - 0.25f, WheelR + 0.82f, 0), new Vector3(len * 0.48f, 0.5f, w * 0.86f), body);
                        AddBox(n, new Vector3(cx - 0.25f, WheelR + 0.84f, 0), new Vector3(len * 0.44f, 0.4f, w * 0.88f), glass);       // 侧窗
                        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.75f, 0.05f, w * 0.82f) }, MaterialOverride = glass, Position = new Vector3(cx + len * 0.24f, WheelR + 0.8f, 0), RotationDegrees = new Vector3(0, 0, -38) });   // 前挡风
                        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.6f, 0.05f, w * 0.82f) }, MaterialOverride = glass, Position = new Vector3(cx - len * 0.29f, WheelR + 0.82f, 0), RotationDegrees = new Vector3(0, 0, 42) });    // 后挡风
                        AddBox(n, new Vector3(front + 0.04f, WheelR + 0.15f, 0), new Vector3(0.12f, 0.2f, w * 1.01f), dark);
                        AddBox(n, new Vector3(back - 0.04f, WheelR + 0.15f, 0), new Vector3(0.12f, 0.2f, w * 1.01f), dark);
                        for (int s = -1; s <= 1; s += 2)
                        {
                            AddBox(n, new Vector3(front + 0.01f, WheelR + 0.42f, s * (w / 2 - 0.28f)), new Vector3(0.04f, 0.13f, 0.35f), head);
                            AddBox(n, new Vector3(back - 0.01f, WheelR + 0.45f, s * (w / 2 - 0.25f)), new Vector3(0.04f, 0.13f, 0.3f), tail);
                            AddBox(n, new Vector3(cx + len * 0.18f, WheelR + 0.72f, s * (w / 2 + 0.08f)), new Vector3(0.12f, 0.1f, 0.12f), body);   // 后视镜
                        }
                    }
                    foreach (var a in def.Axles)
                        for (int s = -1; s <= 1; s += 2) AddWheel(n, new Vector3((float)a.Offset, WheelR, s * (w / 2 - 0.12f)), dark, chrome, def.Kind == "car" ? 0.28f : 0.38f);
                    break;
                }
            }
        }
        private Node3D NewUnit(float front, float rearOffset)
        {
            var n = new Node3D();
            AddChild(n);
            _units.Add(new Unit { Node = n, Front = front, Rear = rearOffset });
            return n;
        }

        private static void AddBox(Node3D parent, Vector3 pos, Vector3 size, Material mat) =>
            parent.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat, Position = pos });

        // 共享网格：所有车轮共用同一份轮胎与轮毂网格（低面数：12 段）
        private static readonly CylinderMesh TyreMesh = new() { TopRadius = WheelR, BottomRadius = WheelR, Height = 1f, RadialSegments = 12, Rings = 1 };
        private static readonly CylinderMesh HubMesh = new() { TopRadius = WheelR * 0.55f, BottomRadius = WheelR * 0.55f, Height = 1f, RadialSegments = 8, Rings = 1 };

        /// <summary>车轮：轮胎 + 外侧轮毂。</summary>
        private static void AddWheel(Node3D parent, Vector3 pos, Material tyre, Material hub, float width)
        {
            parent.AddChild(new MeshInstance3D { Mesh = TyreMesh, MaterialOverride = tyre, Position = pos, RotationDegrees = new Vector3(90, 0, 0), Scale = new Vector3(1, width, 1) });
            float side = pos.Z >= 0 ? 1 : -1;
            parent.AddChild(new MeshInstance3D { Mesh = HubMesh, MaterialOverride = hub, Position = pos + new Vector3(0, 0, side * width * 0.5f), RotationDegrees = new Vector3(90, 0, 0), Scale = new Vector3(1, 0.04f, 1) });
        }

        /// <summary>按路径摆放每个部件：eval(弧长) 返回路面上的点，frontS 为车头轴所在弧长。</summary>
        public void PlaceAlongPath(Func<double, Vector3> eval, double frontS)
        {
            if (_falling) return;
            Transform = Transform3D.Identity;
            foreach (var u in _units)
            {
                var f = eval(frontS + u.Front);
                var r = eval(frontS + u.Rear);
                var dir = f - r;
                float ang = dir.LengthSquared() > 1e-6f ? Mathf.Atan2(dir.Y, dir.X) : 0;
                // 部件原点放在其前端，局部 -X 指向后方
                u.Node.Transform = new Transform3D(new Basis(Vector3.Back, ang), f);
            }
        }

        /// <summary>兼容旧接口：按车头轴与车尾轴位置摆放（单刚体车辆）。</summary>
        public void Place(Vector3 front, Vector3 rear)
        {
            if (_falling || _units.Count == 0) return;
            var dir = front - rear;
            float ang = dir.LengthSquared() > 1e-6f ? Mathf.Atan2(dir.Y, dir.X) : 0;
            _units[0].Node.Transform = new Transform3D(new Basis(Vector3.Back, ang), front);
        }

        /// <summary>桥面断裂时开始坠落（纯视觉效果）。</summary>
        public void StartFalling()
        {
            if (_falling) return;
            _falling = true; _fallVel = new Vector3(1.5f, 0, 0);
        }

        public void ResetState() { _falling = false; Position = Vector3.Zero; Rotation = Vector3.Zero; Visible = true; }

        public override void _Process(double delta)
        {
            if (!_falling) return;
            float dt = (float)delta;
            _fallVel += new Vector3(0, -9.81f, 0) * dt;
            Position += _fallVel * dt;
            RotateZ(-0.6f * dt);
            if (Position.Y < -30) Visible = false;
        }
    }
}
