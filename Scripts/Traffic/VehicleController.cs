using System;
using System.Collections.Generic;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using Godot;
using BridgeArchitect3D.World;

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
        private readonly List<StandardMaterial3D> _beacons=new();
        private readonly List<OmniLight3D> _beaconLights=new();
        private bool _falling;
        // Derailment: each car rolls off the track one after another (visual only)
        private bool _derailing;
        private float _derailTime;
        private readonly List<Transform3D> _derailBase = new();
        private Vector3 _fallVel;
        private const float WheelR = 0.45f;

        /// <summary>Copies an already built vehicle (its parts share meshes and materials with the template).</summary>
        public void SetupFrom(VehicleController template)
        {
            Def = template.Def;
            foreach (var u in template._units)
            {
                var n = (Node3D)u.Node.Duplicate();
                AddChild(n);
                _units.Add(new Unit { Node = n, Front = u.Front, Rear = u.Rear });
            }
            CollectBeacons(); CollectRunningGear();
            SelectTractorVariant((long)GD.Randi());
        }

        private void CollectBeacons()
        {
            foreach (var unit in _units) foreach (var child in unit.Node.GetChildren())
            {
                if (child is MeshInstance3D mesh && mesh.MaterialOverride is StandardMaterial3D mat && (mat.ResourceName.StartsWith("AmbulanceBeacon") || mat.ResourceName.StartsWith("EmergencyBeacon")))
                {
                    var own=(StandardMaterial3D)mat.Duplicate();mesh.MaterialOverride=own;_beacons.Add(own);
                }
                if (child is OmniLight3D beacon) _beaconLights.Add(beacon);
            }
        }

        public void Setup(VehicleDefinition def)
        {
            Def = def;
            var color = MaterialManager.ParseColor(def.Color);
            var body = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.45f, Metallic = 0.25f };
            var dark = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.13f), Roughness = 0.8f };
            var glass = new StandardMaterial3D { AlbedoColor = new Color(0.24f, 0.39f, 0.47f), Roughness = 0.1f, Metallic = 0.5f };
            var light = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.87f, 0.88f), Roughness = 0.6f };
            float w = (float)def.Width, h = (float)def.Height, len = (float)def.Length;
            float rear = 0;
            foreach (var a in def.Axles) rear = Mathf.Min(rear, (float)a.Offset);

            // 共用的少量材质：轮胎、轮毂、车灯、尾灯（每辆车只创建一次）
            var chrome = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.82f, 0.85f), Roughness = 0.25f, Metallic = 0.9f };
            var head = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.97f, 0.85f), EmissionEnabled = true, Emission = new Color(1f, 0.95f, 0.8f), EmissionEnergyMultiplier = 0.8f };
            var tail = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.1f, 0.1f), EmissionEnabled = true, Emission = new Color(0.9f, 0.1f, 0.1f), EmissionEnergyMultiplier = 0.6f };
            var trim = new StandardMaterial3D { AlbedoColor = color.Darkened(0.35f), Roughness = 0.6f };

            if(SpecialVehicleFactory.Supports(def.Kind))
            {
                SpecialVehicleFactory.Build(def,NewUnit);
                for(int i=0;i<_units.Count;i++)LowPolyDetails.DressVehicle(_units[i].Node,def,i);
                FitToBody(); CollectRunningGear();
                CollectBeacons();
                SelectTractorVariant((long)GD.Randi());
                return;
            }
            float roadRadius = def.Kind == "car" ? 0.32f : def.Kind == "bus" ? 0.48f : 0.50f;
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
                        if(loco)
                        {
                            var nose=new[]{new Vector2(-cl,baseY),new Vector2(0.1f,baseY),new Vector2(0.17f,baseY+h*0.24f),new Vector2(0.1f,baseY+h*0.52f),new Vector2(-0.18f,baseY+h*0.76f),new Vector2(-0.65f,baseY+h*0.9f),new Vector2(-cl,baseY+h*0.9f)};
                            n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(nose,w),MaterialOverride=body});
                            CurvedMeshes.Windshield(n,0.082f,baseY+h*0.54f,-0.148f,baseY+h*0.74f,w*0.78f,0f,glass);
                        }
                        else AddBox(n, new Vector3(-cl / 2, baseY + h * 0.45f, 0), new Vector3(cl, h * 0.9f, w), light);
                        AddBox(n, new Vector3(-cl / 2, baseY + h * 0.92f, 0), new Vector3(cl - 0.4f, 0.12f, w * 0.85f), trim);          // 车顶
                        AddBox(n, new Vector3(-cl / 2, WheelR + 0.1f, 0), new Vector3(cl, 0.25f, w * 0.85f), dark);                       // 底架
                        if (loco)
                        {
                            foreach(int side in new[]{-1,1}) AddBox(n,new Vector3(-1.22f,baseY+h*.66f,side*(w/2+.012f)),new Vector3(1.15f,h*.20f,.018f),glass);          // 驾驶室窗
                            AddBox(n, new Vector3(0.05f, baseY + 0.35f, 0), new Vector3(0.3f, 0.5f, w * 0.9f), dark);                     // 排障器
                            for (int s = -1; s <= 1; s += 2) AddBox(n, new Vector3(0.23f, baseY + h * 0.35f, s * (w / 2 - 0.35f)), new Vector3(0.06f, 0.22f, 0.3f), head);
                        }
                        else
                        {
                            for (int r = 1; r <= 4; r++) AddBox(n, new Vector3(-cl * r / 5f, baseY + h * 0.45f, 0), new Vector3(0.08f, h * 0.88f, w * 1.02f), trim);   // 车厢加强筋
                        }
                        AddBox(n, new Vector3(-cl - 0.3f, baseY + 0.3f, 0), new Vector3(0.6f, 0.18f, 0.3f), dark);                       // 车钩
                        foreach (float bx in new[] { -2.5f, -cl + 2.5f })
                        {
                            AddBox(n, new Vector3(bx, WheelR + 0.05f, 0), new Vector3(2.6f, 0.35f, (float)RailTrack.Gauge - 0.3f), dark);   // 转向架（在两侧车轮之间）
                            for (int s = -1; s <= 1; s += 2)
                                // Steel wheels stand on the rail heads: rail gauge apart, bottom of the tread at the unit origin
                                foreach (float wx in new[] { bx - 1f, bx + 1f }) AddWheel(n, new Vector3(wx, WheelR, s * (float)(RailTrack.Gauge / 2)), dark, chrome, 0.14f);
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
                    // Origin on the front axle like every other vehicle, so the model sits inside the simulated body
                    var n = NewUnit(0, rear);
                    // 履带（重量集中在 1.2 m 的短距离内，对应“单点高负载”）+ 负重轮
                    var roadWheel = new CylinderMesh { TopRadius = 0.32f, BottomRadius = 0.32f, Height = 0.3f, RadialSegments = 10 };
                    for (int s = -1; s <= 1; s += 2)
                    {
                        float tz = s * (w / 2 - 0.4f);
                        CurvedMeshes.Track(n,len*0.85f,tz,0.8f,dark,chrome);
                        for (int k = 0; k < 5; k++)
                        {
                            var wheel = new MeshInstance3D { Name = "RoadWheel", Mesh = roadWheel, MaterialOverride = trim, Position = new Vector3(1.6f - k * 1.1f, 0.42f, tz + s * 0.25f), RotationDegrees = new Vector3(90, 0, 0) };
                            wheel.SetMeta("animated", true); n.AddChild(wheel);
                        }
                        AddBox(n, new Vector3(-0.6f, 0.95f, s * (w / 2 - 0.35f)), new Vector3(len * 0.88f, 0.08f, 0.95f), body);  // 挡泥板
                    }
                    AddBox(n, new Vector3(-0.8f, 1.2f, 0), new Vector3(len * 0.72f, 0.6f, w * 0.72f), body);                       // 车体
                    var glacis = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.4f, 0.12f, w * 0.72f) }, MaterialOverride = body, Position = new Vector3(2.0f, 1.2f, 0), RotationDegrees = new Vector3(0, 0, -28) };
                    n.AddChild(glacis);                                                                                            // 倾斜首上装甲
                    AddBox(n, new Vector3(-1.0f, 1.82f, 0), new Vector3(2.6f, 0.65f, 2.2f), body);                                  // 炮塔
                    AddBox(n, new Vector3(-1.3f, 2.2f, 0.5f), new Vector3(0.6f, 0.15f, 0.6f), trim);                                // 舱盖
                    // Gun barrel ends at the front of the body (it used to reach 3.6 m past it)
                    n.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.13f, Height = 3.0f, RadialSegments = 8 }, MaterialOverride = dark, Position = new Vector3(1.3f, 1.9f, 0), RotationDegrees = new Vector3(0, 0, 90) });
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
                        var cabProfile = new[] { new Vector2(front-2.2f,0.5f), new Vector2(front,0.5f), new Vector2(front,1.65f), new Vector2(front-0.23f,2.30f), new Vector2(front-0.48f,2.50f), new Vector2(front-2.2f,2.50f) };
                        n.AddChild(new MeshInstance3D { Mesh = CurvedMeshes.Extrude(cabProfile,w*0.97f), MaterialOverride = body });                          // 驾驶室
                        CurvedMeshes.Windshield(n,front+0.015f,1.68f,front-0.215f,2.30f,w*0.85f,0.035f,glass);
                        foreach(int side in new[]{-1,1}) AddBox(n,new Vector3(cabX-0.15f,2.03f,side*w*.489f),new Vector3(1.1f,.58f,.025f),glass);           // 车窗
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
                        n.AddChild(new MeshInstance3D { Mesh = CurvedMeshes.RoundedBox(new Vector3(len,h-0.2f,w),0.18f), Position = new Vector3((front+back)/2,WheelR+h/2,0), MaterialOverride = body });
                        AddBox(n, new Vector3((front + back) / 2 - 0.3f, WheelR + h * 0.66f, 0), new Vector3(len * 0.86f, h * 0.3f, w * 1.01f), glass);   // 侧窗带
                        AddBox(n, new Vector3(front + 0.01f, WheelR + h * 0.6f, 0), new Vector3(0.05f, h * 0.5f, w * 0.9f), glass);                         // 前挡风
                        AddBox(n, new Vector3(front + 0.02f, WheelR + h * 0.92f, 0), new Vector3(0.05f, 0.25f, w * 0.6f), head);                           // 线路牌
                        SpecialVehicleFactory.AddPassengerDoor(n,front-1.2f,w/2+.028f,h);                 // 前门
                        SpecialVehicleFactory.AddPassengerDoor(n,(front+back)/2-.5f,w/2+.028f,h);    // 中门
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
                        n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.RoundedBox(new Vector3(len,0.55f,w),0.22f),Position=new Vector3(cx,WheelR+0.3f,0),MaterialOverride=body});
                        var cabin=new[]{new Vector2(cx-len*0.32f,1),new Vector2(cx+len*0.28f,1),new Vector2(cx+len*0.13f,1.45f),new Vector2(cx-len*0.2f,1.45f)};
                        n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(cabin,w*0.86f),MaterialOverride=body});
                        foreach(int side in new[]{-1,1})AddBox(n,new Vector3(cx-0.15f,1.25f,side*w*0.435f),new Vector3(len*0.31f,0.3f,0.025f),glass);
                        CurvedMeshes.Windshield(n,cx+len*0.28f,1.01f,cx+len*0.13f,1.43f,w*0.83f,0.09f,glass);
                        CurvedMeshes.Windshield(n,cx-len*0.32f,1.01f,cx-len*0.2f,1.43f,w*0.83f,-0.07f,glass);
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
                        for (int s = -1; s <= 1; s += 2) AddWheel(n, new Vector3((float)a.Offset, roadRadius, s * (w / 2 - 0.12f)), dark, chrome, def.Kind == "car" ? 0.22f : 0.34f, roadRadius);
                    break;
                }
            }
            for(int i=0;i<_units.Count;i++) LowPolyDetails.DressVehicle(_units[i].Node,def,i);
            FitToBody(); CollectRunningGear();
        }
        // ---------------- model fitted to the collision body ----------------

        /// <summary>Allowed overshoot of the model past the collision body (m): tiny, so nothing visibly pokes out.</summary>
        private const float FitTolerance = 0.02f;

        /// <summary>
        /// Squeezes the model into the simulated collision body (<see cref="VehicleBody"/>): along the path each part
        /// is mapped into [Rear, Front], height into [0, Height], width into ±Width/2. The body is a plain box, the
        /// model only has to stay inside it. Parts that already fit are left exactly as built. Done once per template.
        /// </summary>
        private void FitToBody()
        {
            if (Def == null || _units.Count == 0) return;
            float bodyFront = (float)VehicleBody.Front(Def), bodyRear = (float)VehicleBody.Rear(Def);
            float height = (float)Def.Height, halfWidth = (float)Def.Width / 2;
            if (RailTrack.IsTrain(Def)) height -= (float)RailTrack.Height;   // trains are drawn raised onto the rails
            foreach (var u in _units)
            {
                if (!ModelBounds(u.Node, Transform3D.Identity, out var box)) continue;
                // Path coordinates of this part (its origin sits u.Front metres from the leading axle)
                float x0 = box.Position.X + u.Front, x1 = box.End.X + u.Front;
                float t0 = Mathf.Max(x0, bodyRear), t1 = Mathf.Min(x1, bodyFront);
                float sx = 1, ox = 0, sy = 1, sz = 1;
                if ((x0 < bodyRear - FitTolerance || x1 > bodyFront + FitTolerance) && t1 > t0 && x1 - x0 > 1e-4f)
                { sx = (t1 - t0) / (x1 - x0); ox = (t0 - u.Front) - sx * box.Position.X; }
                if (box.End.Y > height + FitTolerance && box.End.Y > 1e-4f) sy = height / box.End.Y;
                float zMax = Mathf.Max(Mathf.Abs(box.Position.Z), Mathf.Abs(box.End.Z));
                if (zMax > halfWidth + FitTolerance) sz = halfWidth / zMax;
                if (sx == 1 && sy == 1 && sz == 1) continue;
                var fit = new Transform3D(Basis.FromScale(new Vector3(sx, sy, sz)), new Vector3(ox, 0, 0));
                foreach (var c in u.Node.GetChildren()) if (c is Node3D child) child.Transform = fit * child.Transform;
            }
        }

        /// <summary>Bounding box of every mesh under node, in node space (lights and other non-meshes ignored).</summary>
        private static bool ModelBounds(Node3D node, Transform3D toRoot, out Aabb box)
        {
            box = default; bool any = false;
            foreach (var c in node.GetChildren())
            {
                if (c is not Node3D child) continue;
                var xf = toRoot * child.Transform;
                if (child is MeshInstance3D m && m.Mesh != null)
                {
                    var b = xf * m.Mesh.GetAabb();
                    box = any ? box.Merge(b) : b; any = true;
                }
                if (ModelBounds(child, xf, out var inner)) { box = any ? box.Merge(inner) : inner; any = true; }
            }
            return any;
        }

        // ---------------- running gear animation ----------------

        private readonly List<Node3D> _tracks = new();
        private readonly List<(Node3D wheel, Basis rest, float radius)> _rollers = new();
        private double _lastFrontS = double.NaN;
        private float _travel;

        /// <summary>Finds the animated parts (tank tracks and road wheels, marked with meta "animated") of the model.</summary>
        private void CollectRunningGear()
        {
            _tracks.Clear(); _rollers.Clear();
            void Walk(Node node)
            {
                foreach (var c in node.GetChildren())
                {
                    if (c is Node3D t && t.HasMeta("loop")) { _tracks.Add(t); continue; }
                    if (c is MeshInstance3D w && w.HasMeta("animated") && w.Mesh is CylinderMesh cyl) _rollers.Add((w, w.Basis, cyl.TopRadius));
                    Walk(c);
                }
            }
            foreach (var u in _units) Walk(u.Node);
        }

        /// <summary>
        /// Runs the tracks and spins the road wheels by the distance driven since the last frame, so the bottom of
        /// each track stays put on the road while the tank moves (links run backwards along the bottom).
        /// </summary>
        private void AnimateRunningGear(double frontS)
        {
            if (_tracks.Count == 0 && _rollers.Count == 0) return;
            double moved = double.IsNaN(_lastFrontS) ? 0 : frontS - _lastFrontS;
            _lastFrontS = frontS;
            if (Math.Abs(moved) < 1e-6 || Math.Abs(moved) > 20) return;   // standing, or a replay jump
            _travel += (float)moved;
            foreach (var t in _tracks) CurvedMeshes.PlaceLinks(t, -_travel);
            foreach (var (wheel, rest, radius) in _rollers)
                wheel.Basis = new Basis(Vector3.Back, -_travel / Mathf.Max(0.05f, radius)) * rest;
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
        private static void AddWheel(Node3D parent, Vector3 pos, Material tyre, Material hub, float width, float radius = WheelR)
        {
            float radialScale = radius / WheelR;
            parent.AddChild(new MeshInstance3D { Mesh = TyreMesh, MaterialOverride = tyre, Position = pos, RotationDegrees = new Vector3(90, 0, 0), Scale = new Vector3(radialScale, width, radialScale) });
            float side = pos.Z >= 0 ? 1 : -1;
            parent.AddChild(new MeshInstance3D { Mesh = HubMesh, MaterialOverride = hub, Position = pos + new Vector3(0, 0, side * width * 0.5f), RotationDegrees = new Vector3(90, 0, 0), Scale = new Vector3(radialScale, 0.04f, radialScale) });
        }

        /// <summary>按路径摆放每个部件：eval(弧长) 返回路面上的点，frontS 为车头轴所在弧长。</summary>
        public void PlaceAlongPath(Func<double, Vector3> eval, double frontS)
        {
            if (_falling || _derailing) return;
            AnimateRunningGear(frontS);
            Transform = Transform3D.Identity;
            foreach (var u in _units)
            {
                var f = eval(frontS + u.Front);
                var r = eval(frontS + u.Rear);
                var dir = f - r;
                float ang = dir.LengthSquared() > 1e-6f ? Mathf.Atan2(dir.Y, dir.X) : 0;
                // A rigid body rests on the road: where the road under it bulges above the front-rear chord
                // (a sagging bridge meeting the bank), lift the body so it never sinks into the road or bank
                if (dir.LengthSquared() > 1e-6f)
                {
                    var n = new Vector3(-dir.Y, dir.X, 0).Normalized();
                    if (n.Y < 0) n = -n;
                    float lift = 0;
                    for (int i = 1; i < 12; i++)
                    {
                        double sv = frontS + u.Rear + (u.Front - u.Rear) * i / 12.0;
                        lift = Mathf.Max(lift, (eval(sv) - r).Dot(n));
                    }
                    f += n * lift;
                }
                // 部件原点放在其前端，局部 -X 指向后方
                var forward=dir.LengthSquared()>1e-6f?dir.Normalized():Vector3.Right;
                var upright=new Vector3(-forward.Y,forward.X,0);if(upright.Y<0)upright=-upright;
                u.Node.Transform = new Transform3D(new Basis(forward,upright,forward.Cross(upright)), f);
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

        public void StartDerail()
        {
            if (_derailing || _falling) return;
            _derailing = true; _derailTime = 0; _derailBase.Clear();
            foreach (var u in _units) _derailBase.Add(u.Node.Transform);
        }

        public void ResetState() { _lastFrontS = double.NaN; _derailing = false; _derailBase.Clear(); _falling = false; Position = Vector3.Zero; Rotation = Vector3.Zero; Visible = true; }

        private long _payloadTrip=long.MinValue;
        public void SelectPayloadVariant(long trip)
        {
            if(Def.Kind!="flatbed" || trip==_payloadTrip)return;
            _payloadTrip=trip;
            int choice=(int)(GD.Randi()%4);
            string[] names={"PayloadExcavator","PayloadForklifts","PayloadSteel","PayloadTransformer"};
            foreach(var u in _units)for(int i=0;i<names.Length;i++)
            {
                var payload=u.Node.GetNodeOrNull<Node3D>(names[i]);
                if(payload!=null)payload.Visible=i==choice;
            }
        }
        private long _tractorTrip=long.MinValue;
        public void SelectTractorVariant(long trip)
        {
            SelectPayloadVariant(trip);
            if(Def.Kind!="semi" || trip==_tractorTrip)return;
            _tractorTrip=trip;
            bool american=(GD.Randi()&1)==0;
            int advertisement=(int)(GD.Randi()%4);
            foreach(var u in _units)
            {
                var cab=u.Node.GetNodeOrNull<Node3D>("CabOver");
                var nose=u.Node.GetNodeOrNull<Node3D>("AmericanCab");
                for(int i=0;i<4;i++)
                {
                    var ad=u.Node.GetNodeOrNull<Node3D>("Advertisement"+i);
                    if(ad!=null)ad.Visible=i==advertisement;
                }
                if(cab!=null)cab.Visible=!american;
                if(nose!=null)nose.Visible=american;
            }
        }

        public void SetEmergencyLights(bool active,double time)
        {
            foreach(var material in _beacons)
            {
                string name=material.ResourceName;
                var color=EmergencyBeaconPattern.ColorFor(name);
                bool lit=active && EmergencyBeaconPattern.IsLit(name,time);
                material.AlbedoColor=lit?new Color(color):new Color(color).Darkened(0.55f);
                material.EmissionEnabled=lit;material.Emission=new Color(color);material.EmissionEnergyMultiplier=lit?3:0;
                material.ShadingMode=lit?BaseMaterial3D.ShadingModeEnum.Unshaded:BaseMaterial3D.ShadingModeEnum.PerPixel;
            }
            foreach(var light in _beaconLights)
                light.Visible=active && EmergencyBeaconPattern.IsLit(light.Name.ToString(),time);
        }

        public override void _Process(double delta)
        {
            if (_derailing)
            {
                _derailTime += (float)delta;
                for (int i = 0; i < _units.Count && i < _derailBase.Count; i++)
                {
                    // Front car first; each car tips over sideways and slides off the track
                    float k = Mathf.Clamp((_derailTime - i * 0.25f) / 1.4f, 0, 1);
                    k = k * k * (3 - 2 * k);
                    var t = _derailBase[i] * new Transform3D(new Basis(Vector3.Right, -1.25f * k), Vector3.Zero);
                    t.Origin += new Vector3(0, -0.5f * k, 2.2f * k);
                    _units[i].Node.Transform = t;
                }
                return;
            }
            if (!_falling) return;
            float dt = (float)delta;
            _fallVel += new Vector3(0, -9.81f, 0) * dt;
            Position += _fallVel * dt;
            RotateZ(-0.6f * dt);
            if (Position.Y < -30) Visible = false;
        }
    }
}

