using Godot;
namespace BridgeArchitect3D.Traffic
{
    public static partial class SpecialVehicleFactory
    {
        public static void AddPassengerDoor(Node3D root, float x, float z, float height)
        {
            float bottom = .55f, top = height - .30f;
            float center = (bottom + top) / 2, doorHeight = top - bottom;
            Box(root,new(x,center,z),new(.96f,doorHeight,.025f),Dark);
            foreach(int side in new[]{-1,1})
            {
                float leaf = x + side * .235f;
                Box(root,new(leaf,center,z+.018f),new(.43f,doorHeight-.07f,.012f),White);
                Box(root,new(leaf,top-.57f,z+.028f),new(.35f,.90f,.012f),Glass);
                Box(root,new(leaf,center-.15f,z+.038f),new(.025f,.30f,.012f),Dark);
            }
            Box(root,new(x,bottom+.04f,z+.02f),new(.95f,.08f,.08f),Alloy);
        }
    }
}
