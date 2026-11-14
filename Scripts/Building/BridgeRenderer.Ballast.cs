using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.Building
{
    public partial class BridgeRenderer
    {
        private void AddBallast(Node3D shell,float length,float height,float width,double ratio)
        {
            int layers=TunnelBallastLayout.Layers(ratio),columns=TunnelBallastLayout.Columns(length);
            var concrete=new StandardMaterial3D{AlbedoColor=new Color(layers==3?"777b78":layers==2?"92988f":"adb3a4"),Roughness=.95f};
            var strap=new StandardMaterial3D{AlbedoColor=new Color("34484d"),Roughness=.65f,Metallic=.35f};
            var marker=new StandardMaterial3D{AlbedoColor=new Color("d2ae50"),Roughness=.8f};
            float spacing=length/columns,blockLength=Mathf.Min(1.75f,spacing*.78f),blockHeight=.38f;
            for(int side=-1;side<=1;side+=2)
            {
                float z=side*width*.36f;
                TubeBox(shell,"BallastMount",new(0,-height/2-.075f,z),new(length*.93f,.15f,1.02f),strap);
                for(int col=0;col<columns;col++)
                {
                    float x=-length/2+(col+.5f)*spacing;
                    for(int layer=0;layer<layers;layer++)
                    {
                        float y=-height/2-.18f-blockHeight*(layer+.5f);
                        TubeBox(shell,"BallastBlock_"+side+"_"+col+"_"+layer,new(x,y,z),new(blockLength,blockHeight-.025f,.95f),concrete);
                        TubeBox(shell,"BallastLevelMark",new(x,y,z+side*.48f),new(blockLength*.65f,.065f,.025f),marker);
                    }
                    foreach(float offset in new[]{-.3f,.3f})
                        TubeBox(shell,"BallastRetainer",new(x+blockLength*offset,-height/2-.16f-layers*blockHeight/2,z+side*.49f),new(.06f,layers*blockHeight+.12f,.06f),strap);
                }
            }
        }
    }
}
