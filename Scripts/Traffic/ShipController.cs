using BridgeArchitect3D.Data;
using Godot;
using BridgeArchitect3D.World;

namespace BridgeArchitect3D.Traffic
{
    /// <summary>
    /// 船只的程序化 3D 模型（船体 + 甲板舱 + 烟囱），沿河道（Z 方向）航行。
    /// 尺寸取自交通事件（船长、船宽、水面以上高度），与车辆比例一致。
    /// 船是否撞桥由 BridgeSimulation 的净空检查判定，这里只负责显示。
    /// </summary>
    public partial class ShipController : Node3D
    {
        // Cutaway clip: fragments outside the river channel box (world space) are discarded, so only the part
        // of the ship that has entered the river is drawn.
        private const string ClipShader = @"shader_type spatial;
render_mode cull_disabled;
uniform vec4 albedo : source_color = vec4(1.0);
uniform float roughness = 0.6;
uniform vec3 clip_min;
uniform vec3 clip_max;
varying vec3 world_pos;
void vertex() { world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
    if (any(lessThan(world_pos, clip_min)) || any(greaterThan(world_pos, clip_max))) discard;
    ALBEDO = albedo.rgb;
    ROUGHNESS = roughness;
}";
        private static Shader _clip,_foamClip;

        private static ShaderMaterial ClipMat(Color c, float rough, Vector3 min, Vector3 max,bool transparent=false)
        {
            _clip ??= new Shader { Code = ClipShader };
            if(transparent)_foamClip??=new Shader {Code=ClipShader.Replace("ROUGHNESS = roughness;","ROUGHNESS = roughness; ALPHA = albedo.a;")};
            var m = new ShaderMaterial { Shader = transparent?_foamClip:_clip };
            m.SetShaderParameter("albedo", c); m.SetShaderParameter("roughness", rough);
            m.SetShaderParameter("clip_min", min); m.SetShaderParameter("clip_max", max);
            return m;
        }

        /// <param name="riverWidth">River channel spans x = 0..riverWidth; z spans the scene depth.</param>
        public void Setup(TrafficEvent ev, double waterLevel, double riverWidth, double riverBed,bool showWake=true)
        {
            var min = new Vector3(0, (float)riverBed - 1, -World.TerrainBuilder.SceneDepth / 2);
            var max = new Vector3((float)riverWidth, 1e4f, World.TerrainBuilder.SceneDepth / 2);
            string kind=ShipCatalog.Kind(ev);
            var colors=new[]{new Color(kind=="speedboat"?"2c7391":kind=="sailboat"?"e9e4d8":kind=="tugboat"?"324f65":"a44639"),
                new Color("eee7d8"),new Color("263c4b"),new Color("537f7c"),new Color("a87c52"),new Color("73868d"),new Color("f4eee0"),new Color("da7753"),new Color("78b79e"),new Color("1c2429")};
            var materials=new Material[colors.Length];
            for(int i=0;i<colors.Length;i++)materials[i]=ClipMat(colors[i],i==2?0.16f:0.65f,min,max);
            foreach(var solid in ShipGeometry.Solids(ev))AddChild(new MeshInstance3D {Mesh=CurvedMeshes.Solid(solid),MaterialOverride=materials[solid.Material]});
            foreach(var part in ShipGeometry.Parts(ev))
                Add(new Vector3((float)part.X,(float)part.Y,(float)part.Z),new Vector3((float)part.Width,(float)part.Height,(float)part.Length),materials[part.Material]);
            LowPolyDetails.Batch(this);
            if(showWake)AddWake(ev,min,max);
            Position = new Vector3((float)ev.LaneX, (float)waterLevel, (float)ShipStateStartZ());
        }

        private static double ShipStateStartZ() => Physics.ShipState.StartZ;

        /// <summary>Surface foam is an effect, not a solid collision part.</summary>
        private void AddWake(TrafficEvent e,Vector3 min,Vector3 max)
        {
            var tool=new SurfaceTool();tool.Begin(Mesh.PrimitiveType.Triangles);
            float length=(float)(e.ShipKind=="speedboat"?7:4),half=(float)e.Width/2,z=-(float)e.Length/2;
            for(int side=-1;side<=1;side+=2)
            {
                var a=new Vector3(side*half*0.45f,0.035f,z);var b=new Vector3(side*(half+1.2f),0.035f,z-length);var c=new Vector3(side*(half+0.75f),0.035f,z-length);
                tool.AddVertex(a);tool.AddVertex(b);tool.AddVertex(c);
            }
            tool.GenerateNormals();tool.Index();
            var foam=ClipMat(new Color(0.8f,0.94f,0.92f,0.23f),0.8f,min,max,true);
            AddChild(new MeshInstance3D {Mesh=tool.Commit(),MaterialOverride=foam,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off});
        }

        private void Add(Vector3 pos, Vector3 size, Material mat)
        {
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat, Position = pos });
        }

        public void SetZ(float z) => Position = new Vector3(Position.X, Position.Y, z);
    }
}

