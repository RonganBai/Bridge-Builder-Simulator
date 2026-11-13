using System.Collections.Generic;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.World
{
    /// <summary>Open only the access corridor in rendered terrain; the tunnel lining closes the excavation.</summary>
    public static class TunnelTerrainMaterial
    {
        private static readonly Dictionary<(bool alpha,bool unshaded),Shader> Shaders=new();

        public static ShaderMaterial Create(StandardMaterial3D source,TunnelAccessPlan plan)
        {
            bool alpha=source.Transparency!=BaseMaterial3D.TransparencyEnum.Disabled;
            bool unshaded=source.ShadingMode==BaseMaterial3D.ShadingModeEnum.Unshaded;
            var key=(alpha,unshaded);
            if(!Shaders.TryGetValue(key,out var shader))
            {
                string modes="cull_back"+(alpha?", blend_mix":"")+(unshaded?", unshaded":"");
                shader=new Shader {Code="shader_type spatial;\nrender_mode "+modes+@";
uniform vec4 base_color : source_color = vec4(1.0);
uniform float surface_roughness = 0.9;
uniform float surface_metallic = 0.0;
uniform vec4 access_segments[128];
uniform int segment_count = 0;
uniform float half_width;
uniform float floor_depth;
uniform float headroom;
varying vec3 world_position;
void vertex() { world_position = (MODEL_MATRIX * vec4(VERTEX,1.0)).xyz; }
void fragment() {
    if (abs(world_position.z) < half_width) {
        for (int i=0; i<128; i++) {
            if (i>=segment_count) break;
            vec4 segment=access_segments[i];
            float dx=segment.z-segment.x;
            if (dx>0.00001 && world_position.x>=segment.x && world_position.x<=segment.z) {
                float road_y=mix(segment.y,segment.w,clamp((world_position.x-segment.x)/dx,0.0,1.0));
                if (world_position.y>road_y-floor_depth && world_position.y<road_y+headroom) discard;
            }
        }
    }
    ALBEDO=base_color.rgb;
    ROUGHNESS=surface_roughness;
    METALLIC=surface_metallic;
"+(alpha?"    ALPHA=base_color.a;\n":"")+"}\n"};
                Shaders[key]=shader;
            }
            var material=new ShaderMaterial {Shader=shader};
            var segments=new Vector4[128];int count=0;
            // Lift shafts: stacked horizontal strips open the ground from the tube floor up to the bank road
            foreach(var c in plan.Cuts)
                if(count<segments.Length)segments[count++]=new Vector4((float)c.x0,(float)c.y0,(float)c.x1,(float)c.y1);
            material.SetShaderParameter("base_color",source.AlbedoColor);
            material.SetShaderParameter("surface_roughness",source.Roughness);
            material.SetShaderParameter("surface_metallic",source.Metallic);
            material.SetShaderParameter("access_segments",segments);material.SetShaderParameter("segment_count",count);
            material.SetShaderParameter("half_width",(float)plan.Geometry.Type.TubeWidth/2+0.04f);
            material.SetShaderParameter("floor_depth",(float)plan.Geometry.Type.FloorHeight+0.05f);
            material.SetShaderParameter("headroom",(float)(plan.Geometry.Type.TubeHeight-plan.Geometry.Type.FloorHeight)+0.05f);
            return material;
        }
    }
}
