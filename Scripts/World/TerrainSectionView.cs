using System.Collections.Generic;
using Godot;
using BridgeArchitect3D.Data;

namespace BridgeArchitect3D.World
{
    /// <summary>Trim only the rendered depth of extruded terrain; retain closed faces and all collision shapes.</summary>
    public partial class TerrainSectionView : Node
    {
        public const float CutPlaneZ = -7f;
        public const string SectionOnlyGroup = "section_only";
        public const string AccessCutGroup = "tunnel_access_cut";
        private readonly List<(MeshInstance3D mesh, float z, float scale)> _meshes = new();
        private readonly List<Node3D> _sectionOnly = new();
        private readonly List<(MeshInstance3D mesh,StandardMaterial3D source)> _cutSurfaces=new();
        private readonly Dictionary<ulong,ShaderMaterial> _accessMaterials=new();
        private TunnelAccessPlan _access;
        private readonly List<(MeshInstance3D mesh, Material original, Material section, MeshInstance3D level)> _water = new();
        public bool Enabled { get; private set; }

        public void Capture(Node root)
        {
            foreach (Node child in root.GetChildren())
            {
                if (child == this) continue;
                if(child is MeshInstance3D surface&&(surface.IsInGroup(TerrainBuilder.CutawayGroup)||surface.IsInGroup(AccessCutGroup))&&surface.MaterialOverride is StandardMaterial3D sourceMaterial)
                    _cutSurfaces.Add((surface,sourceMaterial));
                if (child is MeshInstance3D mesh && mesh.IsInGroup(TerrainBuilder.CutawayGroup))
                {
                    _meshes.Add((mesh, mesh.Position.Z, mesh.Scale.Z));
                    if(mesh.Name=="Water" && mesh.MaterialOverride is StandardMaterial3D source)
                    {
                        var section=(StandardMaterial3D)source.Duplicate();
                        section.AlbedoColor=new Color(source.AlbedoColor.Darkened(0.25f),0.26f);
                        section.ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded;section.Metallic=0;section.Roughness=0.35f;
                        var contour=new MeshInstance3D {Name="SectionWaterLevel",Mesh=new BoxMesh {Size=new Vector3(mesh.Mesh.GetAabb().Size.X,0.04f,0.06f)},
                            MaterialOverride=TerrainBuilder.Unshaded(new Color("327b91")),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off,Visible=false};
                        mesh.AddChild(contour);_water.Add((mesh,source,section,contour));
                    }
                }
                if (child is Node3D marker && marker.IsInGroup(SectionOnlyGroup)) _sectionOnly.Add(marker);
                Capture(child);
            }
        }

        public void SetEnabled(bool on)
        {
            Enabled = on;
            foreach (var (mesh, z, scale) in _meshes)
            {
                if (!GodotObject.IsInstanceValid(mesh)) continue;
                var position = mesh.Position; var size = mesh.Scale;
                position.Z = z; size.Z = scale;
                if (on)
                {
                    var bounds = mesh.Mesh.GetAabb();
                    float low = z + bounds.Position.Z * scale;
                    float high = z + bounds.End.Z * scale;
                    if (high > CutPlaneZ)
                    {
                        if (low < CutPlaneZ)
                        {
                            float ratio = (CutPlaneZ - low) / (high - low);
                            size.Z = scale * ratio;
                            position.Z = low - bounds.Position.Z * size.Z;
                        }
                        else position.Z = CutPlaneZ + 0.04f - bounds.End.Z * scale;
                    }
                }
                // Preserve flood height and all other transforms that may change during simulation.
                mesh.Position = position; mesh.Scale = size;
            }
            foreach (var node in _sectionOnly) node.Visible = on;
            foreach (var water in _water) {water.mesh.MaterialOverride = on ? water.section : water.original;water.level.Visible=on;}
            UpdateWaterLevels();
            ApplyAccessMaterials();
        }

        public void SetTunnel(TunnelGeometry geometry,LevelDefinition level)
        {
            _access=geometry==null?null:new TunnelAccessPlan(level,geometry);_accessMaterials.Clear();ApplyAccessMaterials();
        }

        private void ApplyAccessMaterials()
        {
            foreach(var surface in _cutSurfaces)
            {
                var source=surface.source;
                if(surface.mesh.Name=="Water"&&Enabled)
                    foreach(var water in _water)if(water.mesh==surface.mesh)source=(StandardMaterial3D)water.section;
                if(_access==null){surface.mesh.MaterialOverride=source;continue;}
                ulong key=source.GetInstanceId();
                if(!_accessMaterials.TryGetValue(key,out var material))
                {_accessMaterials[key]=material=TunnelTerrainMaterial.Create(source,_access);}
                surface.mesh.MaterialOverride=material;
            }
        }

        public override void _Process(double delta) {if(Enabled)UpdateWaterLevels();}

        private void UpdateWaterLevels()
        {
            foreach(var water in _water)
            {
                var bounds=water.mesh.Mesh.GetAabb();var scale=water.mesh.Scale;
                float sy=Mathf.Max(0.001f,Mathf.Abs(scale.Y)),sz=Mathf.Max(0.001f,Mathf.Abs(scale.Z));
                water.level.Position=new Vector3(0,bounds.End.Y+0.03f/sy,bounds.End.Z+0.12f/sz);
                water.level.Scale=new Vector3(1,1/sy,1/sz);
            }
        }
    }
}
