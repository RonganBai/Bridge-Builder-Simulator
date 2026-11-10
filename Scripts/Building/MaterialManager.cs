using System.Collections.Generic;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.Building
{
    /// <summary>
    /// 材料的 Godot 显示资源缓存（颜色、受力颜色分级）。物理参数仍只来自 MaterialDefinition。
    /// 所有 StandardMaterial3D 只创建一次，运行时只切换引用，避免每帧分配。
    /// </summary>
    public class MaterialManager
    {
        private readonly Dictionary<string, StandardMaterial3D> _byMaterial = new();
        private readonly StandardMaterial3D[] _util = new StandardMaterial3D[21];
        public readonly StandardMaterial3D Gray, Selected, Hover, PreviewOk, PreviewBad, Node, Anchor, NodeSelected, CrossTie, Overload, Chrome, LaneMark, Foundation;
        private readonly Dictionary<StandardMaterial3D, StandardMaterial3D> _rim = new();

        private readonly Dictionary<StandardMaterial3D, StandardMaterial3D> _aged = new();

        /// <summary>风化 / 锈蚀外观：颜色偏暗偏褐、更粗糙，用来区分旧桥原有构件。</summary>
        public StandardMaterial3D Aged(StandardMaterial3D m)
        {
            if (!_aged.TryGetValue(m, out var r))
            {
                r = (StandardMaterial3D)m.Duplicate();
                r.AlbedoColor = m.AlbedoColor.Lerp(new Color(0.55f, 0.33f, 0.2f), 0.55f).Darkened(0.15f);
                r.Roughness = 0.95f; r.Metallic = 0.1f;
                _aged[m] = r;
            }
            return r;
        }

        /// <summary>同色但带边缘轮廓高光的材质（缓存，不会每帧分配）。</summary>
        public StandardMaterial3D WithRim(StandardMaterial3D m)
        {
            if (m.RimEnabled) return m;
            if (!_rim.TryGetValue(m, out var r))
            {
                r = (StandardMaterial3D)m.Duplicate();
                r.RimEnabled = true; r.Rim = 0.8f; r.RimTint = 0.2f;
                _rim[m] = r;
            }
            return r;
        }
        public GameDatabase Db { get; }

        public MaterialManager(GameDatabase db)
        {
            Db = db;
            foreach (var m in db.Materials.Values)
                _byMaterial[m.MaterialId] = Make(ParseColor(m.Color), m.IsDeck ? 0.85f : 0.45f, m.IsDeck ? 0f : 0.6f);
            for (int i = 0; i < _util.Length; i++) _util[i] = Make(UtilColor(i / 20.0), 0.6f, 0.1f);
            Overload = Make(new Color(0.9f, 0.1f, 0.1f), 0.5f, 0f, true);
            Gray = Make(new Color(0.45f, 0.45f, 0.47f), 0.9f, 0f);
            Selected = Make(new Color(1f, 0.85f, 0.2f), 0.4f, 0f, true);
            Hover = Make(new Color(0.6f, 0.9f, 1f), 0.4f, 0f, true);
            PreviewOk = Transparent(new Color(0.3f, 1f, 0.5f, 0.55f));
            PreviewBad = Transparent(new Color(1f, 0.25f, 0.2f, 0.55f));
            Node = Make(new Color(0.92f, 0.94f, 0.97f), 0.3f, 0.2f);
            NodeSelected = Make(new Color(1f, 0.8f, 0.1f), 0.3f, 0f, true);
            Anchor = Make(new Color(0.95f, 0.45f, 0.1f), 0.5f, 0.1f);
            CrossTie = Make(new Color(0.55f, 0.6f, 0.66f), 0.6f, 0.5f);
            Chrome = Make(new Color(0.88f, 0.9f, 0.93f), 0.15f, 0.95f);
            Foundation = Make(new Color(0.62f, 0.62f, 0.58f), 0.9f, 0f);
            LaneMark = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.92f, 0.75f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            // 梁与立柱的材料颜色带边缘轮廓高光（Rim），钢索与桥面不加
            foreach (var m in db.Materials.Values)
                if (!m.IsDeck && !m.IsCable) { var bm = _byMaterial[m.MaterialId]; bm.RimEnabled = true; bm.Rim = 0.8f; bm.RimTint = 0.2f; }
            // 胶合木：表面显示木材层纹（程序生成的条纹贴图，世界坐标三平面映射）
            foreach (var m in db.Materials.Values)
            {
                if (m.Look != "layered") continue;
                var bm = _byMaterial[m.MaterialId];
                bm.AlbedoTexture = LayerTexture(ParseColor(m.Color));
                bm.AlbedoColor = Colors.White;
                bm.Uv1Triplanar = true; bm.Uv1WorldTriplanar = true; bm.Uv1Scale = new Vector3(0.5f, 6f, 0.5f);
                bm.Metallic = 0; bm.Roughness = 0.75f;
            }
        }

        /// <summary>层板纹理：深浅相间的木色条纹。</summary>
        private static ImageTexture LayerTexture(Color baseColor)
        {
            var img = Image.CreateEmpty(4, 32, false, Image.Format.Rgb8);
            for (int y = 0; y < 32; y++)
            {
                float k = (y % 4 == 0) ? 0.72f : (y % 8 < 4 ? 1.0f : 0.9f);
                var c = new Color(baseColor.R * k, baseColor.G * k, baseColor.B * k);
                for (int x = 0; x < 4; x++) img.SetPixel(x, y, c);
            }
            return ImageTexture.CreateFromImage(img);
        }

        public StandardMaterial3D ForMaterial(string id) => id != null && _byMaterial.TryGetValue(id, out var m) ? m : Gray;

        /// <summary>按承载利用率取颜色：绿色安全 → 黄色接近极限 → 红色超限。</summary>
        public StandardMaterial3D ForUtilization(double u)
        {
            if (u > 1.0) return Overload;
            int i = Mathf.Clamp((int)Mathf.Round(u * 20), 0, 20);
            return _util[i];
        }

        public static Color UtilColor(double u)
        {
            var green = new Color(0.2f, 0.8f, 0.3f);
            var yellow = new Color(1f, 0.85f, 0.15f);
            var red = new Color(0.95f, 0.15f, 0.1f);
            if (u < 0.7) return green.Lerp(yellow, (float)(u / 0.7 * 0.35));
            if (u < 0.9) return green.Lerp(yellow, (float)(0.35 + (u - 0.7) / 0.2 * 0.65));
            return yellow.Lerp(red, (float)Mathf.Clamp((u - 0.9) / 0.1, 0, 1));
        }

        public static Color ParseColor(string hex)
        {
            try { return Color.FromHtml(hex ?? "#888888"); } catch { return new Color(0.5f, 0.5f, 0.5f); }
        }

        private static StandardMaterial3D Make(Color c, float rough, float metal, bool emissive = false)
        {
            var m = new StandardMaterial3D { AlbedoColor = c, Roughness = rough, Metallic = metal };
            if (emissive) { m.EmissionEnabled = true; m.Emission = c; m.EmissionEnergyMultiplier = 0.6f; }
            return m;
        }

        private static StandardMaterial3D Transparent(Color c) => new()
        {
            AlbedoColor = c,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
    }
}
