using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.World
{
    /// <summary>程序化生成低多边形关卡环境：两岸、河床、水面、道路引道、岩墩、锚点标记、天空与灯光。</summary>
    public static class TerrainBuilder
    {
        public const float SceneDepth = 40f;   // 场景沿 Z 方向的宽度
        public const float RoadWidth = 4f;

        /// <summary>生成环境；返回装饰物根节点（建造侧视时可隐藏以减少干扰）。</summary>
        public static Node3D Build(Node3D root, LevelDefinition level)
        {
            var t = level.Terrain;
            float w = (float)t.RiverWidth;
            float bed = (float)t.RiverBedDepth;
            float lh = (float)t.LeftBankHeight, rh = (float)t.RightBankHeight;
            const float bankLen = 45f;

            var grass = Mat(new Color("6f9a57"));
            var soil = Mat(new Color("8a6e4f"));
            var rock = Mat(new Color("7b7f86"));
            var asphalt = Mat(new Color("3c3f44"));
            var line = Mat(new Color("e8e2c8"));

            // 两岸（实体 + 碰撞，供碎片落地）
            AddBox(root, new Vector3(-bankLen / 2, (lh + bed - 2) / 2, 0), new Vector3(bankLen, lh - bed + 2, SceneDepth), soil, true);
            AddBox(root, new Vector3(w + bankLen / 2, (rh + bed - 2) / 2, 0), new Vector3(bankLen, rh - bed + 2, SceneDepth), soil, true);
            AddBox(root, new Vector3(-bankLen / 2, lh + 0.05f, 0), new Vector3(bankLen, 0.1f, SceneDepth), grass, false);
            AddBox(root, new Vector3(w + bankLen / 2, rh + 0.05f, 0), new Vector3(bankLen, 0.1f, SceneDepth), grass, false);
            // 河床
            AddBox(root, new Vector3(w / 2, bed - 1, 0), new Vector3(w + 2, 2, SceneDepth), rock, true);
            // 河中岩墩
            foreach (var p in t.Pillars)
            {
                float ph = (float)(p.TopY - t.RiverBedDepth);
                AddBox(root, new Vector3((float)p.X, (float)(t.RiverBedDepth + ph / 2), 0), new Vector3((float)p.Width, ph, 6), rock, true);
            }
            // 水面（半透明，无流体模拟）
            var water = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.20f, 0.45f, 0.70f, 0.72f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                Roughness = 0.1f, Metallic = 0.2f,
            };
            float wl = (float)t.WaterLevel;
            var waterMesh = AddBox(root, new Vector3(w / 2, (wl + bed) / 2, 0), new Vector3(w, wl - bed, SceneDepth), water, false);
            waterMesh.Name = "Water";
            BuildSoilProfile(root, level);

            // 道路引道与标线
            AddBox(root, new Vector3(-bankLen / 2, lh + 0.15f, 0), new Vector3(bankLen, 0.12f, RoadWidth), asphalt, false);
            AddBox(root, new Vector3(w + bankLen / 2, rh + 0.15f, 0), new Vector3(bankLen, 0.12f, RoadWidth), asphalt, false);
            AddBox(root, new Vector3(-bankLen / 2, lh + 0.22f, 0), new Vector3(bankLen, 0.02f, 0.15f), line, false);
            AddBox(root, new Vector3(w + bankLen / 2, rh + 0.22f, 0), new Vector3(bankLen, 0.02f, 0.15f), line, false);

            var deco = new Node3D { Name = "Decorations" };
            root.AddChild(deco);
            BuildDecorations(deco, level, bankLen);
            BuildShipLanes(root, level);
            BuildElevatedApproaches(root, level, bankLen, asphalt, line, Mat(new Color("9a9a94")));
            BuildSlopeBands(root, level);
            BuildNoBuildZones(root, level);

            // 可建造区域边界（淡色线框）
            var boundary = new MeshInstance3D { Mesh = BuildBoundaryMesh(level), MaterialOverride = Unshaded(new Color(0.55f, 0.8f, 1f, 0.5f)) };
            root.AddChild(boundary);

            // 环境与灯光
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky
                {
                    SkyMaterial = new ProceduralSkyMaterial
                    {
                        SkyTopColor = new Color("4d7fbf"),
                        SkyHorizonColor = new Color("b9d3ea"),
                        GroundHorizonColor = new Color("b9d3ea"),
                        GroundBottomColor = new Color("5d6b58"),
                    }
                },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.8f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            };
            root.AddChild(new WorldEnvironment { Environment = env });
            var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.1f };
            sun.RotationDegrees = new Vector3(-50, -35, 0);
            root.AddChild(sun);
            return deco;
        }

        private static Mesh BuildBoundaryMesh(LevelDefinition level)
        {
            var t = level.Terrain;
            float x0 = (float)-t.BuildMarginX, x1 = (float)(t.RiverWidth + t.BuildMarginX);
            float y0 = (float)t.BuildMinY, y1 = (float)t.BuildMaxY;
            float z = BuildPlane.FrontZ;
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            Vector3[] c = { new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z) };
            for (int i = 0; i < 4; i++) { im.SurfaceAddVertex(c[i]); im.SurfaceAddVertex(c[(i + 1) % 4]); }
            im.SurfaceEnd();
            return im;
        }

        // ---------------- 装饰 ----------------
        /// <summary>
        /// 道路两侧的装饰：远处小山、树木、公园（草坪、步道、长椅、路灯）。
        /// 尺寸按真实比例：轿车长 4.4 m、高 1.4 m；树高约 5~8 m；长椅长 1.6 m；路灯高 4 m。
        /// 装饰离道路至少 5 m，离河岸至少 4 m，不遮挡建造区域；位置由固定种子生成，每次一致。
        /// </summary>
        private static void BuildDecorations(Node3D root, LevelDefinition level, float bankLen)
        {
            var t = level.Terrain;
            float w = (float)t.RiverWidth, lh = (float)t.LeftBankHeight, rh = (float)t.RightBankHeight;
            var trunk = Mat(new Color("6b4a2f"));
            var leaf1 = Mat(new Color("3f7d3a")); var leaf2 = Mat(new Color("5b9442")); var leaf3 = Mat(new Color("2f6a3c"));
            var hill1 = Mat(new Color("7fa55e")); var hill2 = Mat(new Color("6a9150"));
            var lawn = Mat(new Color("82b663")); var path = Mat(new Color("c9b99a"));
            var wood = Mat(new Color("8c5a32")); var metal = Mat(new Color("3a3f45"));
            var lamp = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.95f, 0.75f), EmissionEnabled = true, Emission = new Color(1f, 0.9f, 0.6f), EmissionEnergyMultiplier = 0.4f };
            var rng = new Lcg(12345 + (uint)level.Id.GetHashCode());

            // 小山：建造区域后方（-Z 一侧）的低多边形山丘
            float[] hillX = { -bankLen + 8, -bankLen * 0.55f, w + bankLen * 0.55f, w + bankLen - 8 };
            for (int i = 0; i < hillX.Length; i++)
            {
                bool left = hillX[i] < 0;
                float r = 7 + rng.Next() * 5, hgt = 6 + rng.Next() * 6;
                var cone = new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = r * 0.15f, BottomRadius = r, Height = hgt, RadialSegments = 7, Rings = 1 },
                    MaterialOverride = i % 2 == 0 ? hill1 : hill2,
                    Position = new Vector3(hillX[i], (left ? lh : rh) + hgt / 2, -SceneDepth / 2 + r * 0.6f),
                };
                root.AddChild(cone);
            }

            // 树木：两岸、道路两侧，离道路 ≥ 5 m
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                float y0 = left ? lh : rh;
                int count = 14;
                for (int i = 0; i < count; i++)
                {
                    float x = left ? -(5 + rng.Next() * (bankLen - 8)) : w + 5 + rng.Next() * (bankLen - 8);
                    float zSign = rng.Next() < 0.7f ? -1 : 1;          // 前侧（面向相机）少放，避免遮挡
                    float z = zSign * (5.5f + rng.Next() * (SceneDepth / 2 - 7));
                    if (zSign > 0 && Mathf.Abs(x - w / 2) < w / 2 + 12) continue; // 前侧靠近桥位不放树
                    if (left && x > -16 && x < -6 && z < -6 && z > -17) continue; // 给左岸公园留空
                    AddTree(root, new Vector3(x, y0, z), 0.8f + rng.Next() * 0.5f, trunk, rng.Next() < 0.5f ? leaf1 : (rng.Next() < 0.5f ? leaf2 : leaf3));
                }
            }

            // 左岸公园：草坪 + 步道 + 长椅 + 路灯 + 几棵树（位于道路后侧）
            var parkC = new Vector3(-11, lh, -11.5f);
            AddBox(root, parkC + new Vector3(0, 0.12f, 0), new Vector3(10, 0.06f, 8), lawn, false);
            AddBox(root, parkC + new Vector3(0, 0.16f, 0), new Vector3(10, 0.04f, 1.4f), path, false);
            AddBox(root, parkC + new Vector3(0, 0.17f, 0), new Vector3(1.4f, 0.04f, 8), path, false);
            for (int i = 0; i < 3; i++) AddBench(root, parkC + new Vector3(-3.5f + i * 3.5f, 0.15f, 1.3f), wood, metal);
            AddLamp(root, parkC + new Vector3(-4.5f, 0.15f, -1.2f), metal, lamp);
            AddLamp(root, parkC + new Vector3(4.5f, 0.15f, -1.2f), metal, lamp);
            AddTree(root, parkC + new Vector3(-3.5f, 0.15f, -3), 0.9f, trunk, leaf2);
            AddTree(root, parkC + new Vector3(3.2f, 0.15f, -3.2f), 1.0f, trunk, leaf1);

            // 右岸路边：沿路的小路灯
            for (int i = 0; i < 3; i++) AddLamp(root, new Vector3(w + 8 + i * 10, rh + 0.15f, -3.2f), metal, lamp);
        }

        /// <summary>一棵低多边形树：树干高 2.4 m，树冠圆锥高 4.5 m（scale = 1 时总高约 6.9 m）。</summary>
        private static void AddTree(Node3D root, Vector3 basePos, float scale, Material trunk, Material leaf)
        {
            var tree = new Node3D { Position = basePos, Scale = Vector3.One * scale };
            tree.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.25f, Height = 2.4f, RadialSegments = 6 }, MaterialOverride = trunk, Position = new Vector3(0, 1.2f, 0) });
            tree.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 1.8f, Height = 3.2f, RadialSegments = 7 }, MaterialOverride = leaf, Position = new Vector3(0, 3.4f, 0) });
            tree.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 1.35f, Height = 2.6f, RadialSegments = 7 }, MaterialOverride = leaf, Position = new Vector3(0, 5.0f, 0) });
            root.AddChild(tree);
        }

        /// <summary>长椅：长 1.6 m，座高 0.45 m。</summary>
        private static void AddBench(Node3D root, Vector3 p, Material wood, Material metal)
        {
            AddBox(root, p + new Vector3(0, 0.45f, 0), new Vector3(1.6f, 0.06f, 0.45f), wood, false);
            AddBox(root, p + new Vector3(0, 0.75f, -0.2f), new Vector3(1.6f, 0.35f, 0.05f), wood, false);
            AddBox(root, p + new Vector3(-0.7f, 0.22f, 0), new Vector3(0.06f, 0.45f, 0.4f), metal, false);
            AddBox(root, p + new Vector3(0.7f, 0.22f, 0), new Vector3(0.06f, 0.45f, 0.4f), metal, false);
        }

        /// <summary>路灯：高 4 m。</summary>
        private static void AddLamp(Node3D root, Vector3 p, Material metal, Material light)
        {
            root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.08f, Height = 4f, RadialSegments = 6 }, MaterialOverride = metal, Position = p + new Vector3(0, 2f, 0) });
            AddBox(root, p + new Vector3(0, 4.05f, 0), new Vector3(0.35f, 0.25f, 0.35f), light, false);
        }

        /// <summary>航道净空区域（半透明框）：船只经过时，任何构件进入该区域都会被撞。</summary>
        private static void BuildShipLanes(Node3D root, LevelDefinition level)
        {
            foreach (var e in level.TrafficEvents)
            {
                if (e.Type != TrafficEventType.Ship) continue;
                float x0 = (float)(e.LaneX - e.Width / 2), x1 = (float)(e.LaneX + e.Width / 2);
                float y0 = (float)level.Terrain.WaterLevel, y1 = (float)(level.Terrain.WaterLevel + e.Height);
                var im = new ImmediateMesh();
                im.SurfaceBegin(Mesh.PrimitiveType.Lines);
                float z = BuildPlane.FrontZ + 0.02f;
                Vector3[] c = { new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z) };
                for (int i = 0; i < 4; i++) { im.SurfaceAddVertex(c[i]); im.SurfaceAddVertex(c[(i + 1) % 4]); }
                im.SurfaceEnd();
                root.AddChild(new MeshInstance3D { Mesh = im, MaterialOverride = Unshaded(new Color(1f, 0.6f, 0.2f, 0.9f)) });
                var fill = AddBox(root, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, 0), new Vector3(x1 - x0, y1 - y0, 2 * BuildPlane.FrontZ), Unshaded(new Color(1f, 0.6f, 0.2f, 0.08f)), false);
                fill.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                root.AddChild(new Label3D
                {
                    Text = $"航道 净空 {e.Height:0.#} m", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    FontSize = 36, PixelSize = 0.01f, Modulate = new Color(1f, 0.7f, 0.35f),
                    Position = new Vector3((x0 + x1) / 2, y1 + 0.4f, BuildPlane.FrontZ + 0.3f),
                });
            }
        }

        /// <summary>
        /// 河床地基剖面：软土显示为褐色分层，岩石显示为灰色块体；冲刷区用橙色虚线标出预设的冲刷轮廓。
        /// </summary>
        private static void BuildSoilProfile(Node3D root, LevelDefinition level)
        {
            var t = level.Terrain;
            if (t.FoundationZones.Count == 0) return;
            float bed = (float)t.RiverBedDepth;
            var soft1 = Mat(new Color("8a6a45")); var soft2 = Mat(new Color("a3825a")); var rock = Mat(new Color("6f747b"));
            foreach (var z in t.FoundationZones)
            {
                float x0 = (float)Mathf.Max(0, z.X0), x1 = (float)Mathf.Min(t.RiverWidth, z.X1), cx = (x0 + x1) / 2, wz = x1 - x0;
                if (wz <= 0) continue;
                if (z.IsRock) AddBox(root, new Vector3(cx, bed - 1.5f, BuildPlane.FrontZ + 1.2f), new Vector3(wz, 3f, 0.3f), rock, false);
                else for (int i = 0; i < 4; i++) AddBox(root, new Vector3(cx, bed - 0.4f - i * 0.75f, BuildPlane.FrontZ + 1.2f), new Vector3(wz, 0.75f, 0.3f), i % 2 == 0 ? soft1 : soft2, false);
                root.AddChild(new Label3D
                {
                    Text = z.Name + (z.IsRock ? "：浅基础即可" : "：浅基础承载低，建议桩基础"), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                    FontSize = 28, PixelSize = 0.01f, Modulate = z.IsRock ? new Color(0.85f, 0.88f, 0.92f) : new Color(1f, 0.82f, 0.55f),
                    Position = new Vector3(cx, bed + 0.6f, BuildPlane.FrontZ + 1.5f),
                });
                if (z.Scour)
                {
                    // 预设冲刷轮廓：洪水时河床被掏空的范围
                    var im = new ImmediateMesh(); im.SurfaceBegin(Mesh.PrimitiveType.Lines);
                    float zf = BuildPlane.FrontZ + 1.4f, depth = 2.5f;
                    for (int i = 0; i < 12; i++)
                    {
                        float a0 = i / 12f, a1 = (i + 0.5f) / 12f;
                        Vector3 P(float a) => new(x0 + (x1 - x0) * a, bed - depth * Mathf.Sin(Mathf.Pi * a), zf);
                        im.SurfaceAddVertex(P(a0)); im.SurfaceAddVertex(P(a1));
                    }
                    im.SurfaceEnd();
                    root.AddChild(new MeshInstance3D { Mesh = im, MaterialOverride = Unshaded(new Color(1f, 0.55f, 0.2f)) });
                }
            }
        }

        /// <summary>立交上层道路：锚点高于地面时，用墩柱托起的高架引桥连接到岸上。</summary>
        private static void BuildElevatedApproaches(Node3D root, LevelDefinition level, float bankLen, Material asphalt, Material line, Material concrete)
        {
            var t = level.Terrain;
            foreach (var a in level.Anchors)
            {
                if (!a.IsRoadStart && !a.IsRoadEnd) continue;
                bool left = a.IsRoadStart;
                float ground = (float)(left ? t.LeftBankHeight : t.RightBankHeight);
                float y = (float)a.Position.Y;
                if (y < ground + 1.0f) continue;   // 与地面同高的普通道路已由引道绘制
                float x0 = left ? -bankLen : (float)t.RiverWidth, x1 = left ? 0 : (float)t.RiverWidth + bankLen;
                float cx = (x0 + x1) / 2, len = x1 - x0;
                AddBox(root, new Vector3(cx, y - 0.25f, 0), new Vector3(len, 0.5f, RoadWidth + 0.6f), concrete, false);
                AddBox(root, new Vector3(cx, y + 0.15f, 0), new Vector3(len, 0.12f, RoadWidth), asphalt, false);
                AddBox(root, new Vector3(cx, y + 0.22f, 0), new Vector3(len, 0.02f, 0.15f), line, false);
                for (float px = x0 + 4; px < x1 - 1; px += 9)
                    AddBox(root, new Vector3(px, (ground + y - 0.5f) / 2, 0), new Vector3(1.0f, y - 0.5f - ground, 1.6f), concrete, false);
            }
        }

        /// <summary>桥面坡度可行范围：从每个道路起点画出 ±最大坡度的半透明扇形。</summary>
        private static void BuildSlopeBands(Node3D root, LevelDefinition level)
        {
            var t = level.Terrain;
            double s = level.Objectives.MaxDeckSlope;
            foreach (var a in level.Anchors)
            {
                if (!a.IsRoadStart) continue;
                var end = level.Anchors.Find(e => e.IsRoadEnd && e.RoadLane == a.RoadLane);
                if (end == null || System.Math.Abs(end.Position.Y - a.Position.Y) < 0.05) continue;  // 两岸等高时不需要提示
                float x0 = (float)a.Position.X, y0 = (float)a.Position.Y, x1 = (float)end.Position.X;
                float z = BuildPlane.FrontZ - 0.03f;
                var im = new ImmediateMesh();
                im.SurfaceBegin(Mesh.PrimitiveType.Triangles);
                var p0 = new Vector3(x0, y0, z);
                var pUp = new Vector3(x1, y0 + (float)(s * (x1 - x0)), z);
                var pDn = new Vector3(x1, y0 - (float)(s * (x1 - x0)), z);
                im.SurfaceAddVertex(p0); im.SurfaceAddVertex(pUp); im.SurfaceAddVertex(pDn);
                im.SurfaceEnd();
                var mi = new MeshInstance3D { Mesh = im, MaterialOverride = Unshaded(new Color(0.4f, 0.9f, 1f, 0.10f)) };
                mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                root.AddChild(mi);
                root.AddChild(new Label3D
                {
                    Text = $"桥面坡度 ≤ {s * 100:0}%", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                    FontSize = 32, PixelSize = 0.01f, Modulate = new Color(0.5f, 0.9f, 1f),
                    Position = new Vector3((x0 + x1) / 2, y0 + (float)(s * (x1 - x0) / 2) + 0.5f, z + 0.3f),
                });
            }
        }

        /// <summary>禁止建墩区：半透明绿色区域（航道或生态保护区），区内道路标高以下不能放置节点。</summary>
        private static void BuildNoBuildZones(Node3D root, LevelDefinition level)
        {
            var t = level.Terrain;
            foreach (var zn in t.NoBuildZones)
            {
                float x0 = (float)zn.X0, x1 = (float)zn.X1, y1 = (float)zn.BelowY, y0 = (float)t.WaterLevel;
                var fill = AddBox(root, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, BuildPlane.FrontZ - 0.05f), new Vector3(x1 - x0, y1 - y0, 0.02f),
                    Unshaded(new Color(0.3f, 0.9f, 0.4f, 0.12f)), false);
                fill.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                root.AddChild(new Label3D
                {
                    Text = zn.Name, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                    FontSize = 32, PixelSize = 0.01f, Modulate = new Color(0.5f, 1f, 0.6f),
                    Position = new Vector3((x0 + x1) / 2, y0 + 1.2f, BuildPlane.FrontZ + 0.3f),
                });
            }
        }

        /// <summary>确定性伪随机数（装饰摆放每次一致，不参与任何判定）。</summary>
        private sealed class Lcg
        {
            private uint _s;
            public Lcg(uint seed) { _s = seed == 0 ? 1u : seed; }
            public float Next() { _s = _s * 1664525u + 1013904223u; return (_s >> 8) / 16777216f; }
        }

        public static StandardMaterial3D Mat(Color c) => new() { AlbedoColor = c, Roughness = 0.9f };

        public static StandardMaterial3D Unshaded(Color c) => new()
        {
            AlbedoColor = c,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = c.A < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        };

        public static MeshInstance3D AddBox(Node3D root, Vector3 center, Vector3 size, Material mat, bool collision)
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat };
            if (collision)
            {
                var body = new StaticBody3D { Position = center };
                body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
                body.AddChild(mi);
                root.AddChild(body);
            }
            else
            {
                mi.Position = center;
                root.AddChild(mi);
            }
            return mi;
        }
    }

    /// <summary>建造平面参数：初版所有节点位于 Z=0 的竖直平面，桁架在桥面两侧 ±TrussOffset 处各渲染一片。</summary>
    public static class BuildPlane
    {
        public const float TrussOffset = 2.2f;
        /// <summary>鼠标拾取平面（前侧桁架所在平面，面向默认相机）。</summary>
        public const float FrontZ = TrussOffset;
    }
}
