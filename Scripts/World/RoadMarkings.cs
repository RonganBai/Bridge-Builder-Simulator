using Godot;

namespace BridgeArchitect3D.World
{
    public static class RoadMarkings
    {
        public static void Arrow(Node3D parent,Vector3 position,bool forward)
        {
            var root=new Node3D{Position=position,RotationDegrees=new(0,forward?0:180,0)};parent.AddChild(root);
            var paint=new StandardMaterial3D{AlbedoColor=new Color("e4debe"),ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,CullMode=BaseMaterial3D.CullModeEnum.Disabled};
            root.AddChild(new MeshInstance3D{Mesh=new BoxMesh{Size=new(1.3f,.008f,.13f)},Position=new(-.2f,0,0),MaterialOverride=paint});
            var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);st.SetNormal(Vector3.Up);st.AddVertex(new(1,0,0));st.AddVertex(new(.35f,0,-.4f));st.AddVertex(new(.35f,0,.4f));
            root.AddChild(new MeshInstance3D{Mesh=st.Commit(),MaterialOverride=paint});
        }
    }
}
