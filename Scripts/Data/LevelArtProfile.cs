using System;

namespace BridgeArchitect3D.Data
{
    public sealed record LevelArtProfile(string Name,string Ground,string Soil,string Foliage,string Accent,string Water,string Sky,string Horizon,string Landmark)
    {
        public static readonly LevelArtProfile[] All =
        {
            new(Loc.T("春日草甸"),"8cac72","a78c69","73a85c","e8c775","5f9fa6","75a9cc","dce7df","cottage"),
            new(Loc.T("樱花河畔"),"9cb87c","99836d","dda3ba","b86777","6ca6b0","8db5d5","f1dee4","orchard"),
            new(Loc.T("盛夏农场"),"b2b96e","9c8055","5f914c","cc8650","50918e","6aafd1","dce6cc","farm"),
            new(Loc.T("高山冷杉"),"779c89","797e76","356f61","c5cfc0","5895a1","7cacc4","d9e5df","alpine"),
            new(Loc.T("暖色峡谷"),"c0a37b","aa7356","9b9259","dbb582","72908d","9dbac9","f0dbc0","canyon"),
            new(Loc.T("金秋白桦"),"b4a66a","8d755e","d5aa4e","a95c44","749c9b","90b2c5","ecddbf","birch"),
            new(Loc.T("清晨港湾"),"87a099","8a8679","597c6e","c68159","648e9e","88a8bc","dbe5e6","harbor"),
            new(Loc.T("晚秋货运站"),"a49b78","827669","9a805a","b46a4e","6e9698","9aadb9","e4d8c8","freight"),
            new(Loc.T("冬日立交"),"dce4e4","8d9295","8eaaa1","c58e63","7899ac","97b7cf","e7edf0","city"),
            new(Loc.T("红叶旧镇"),"b09b79","937768","b76d50","6d918d","78948e","b4bdc0","e9d3bc","oldtown"),
            new(Loc.T("芦苇湿地"),"a9ae81","8c8263","8e9c61","b09a64","839d94","a5bdbc","e4e5d7","wetland"),
            new(Loc.T("雨季松林"),"708b80","747c71","3c7167","bec1a0","698e91","879da7","d2dedb","rainforest")
        };
        public static LevelArtProfile For(string id)
        {
            int number=0;foreach(char c in id ?? "")if(char.IsDigit(c))number=number*10+c-'0';
            return All[Math.Abs(number-1)%All.Length];
        }
        public static uint Seed(string id)
        {
            uint value=2166136261;foreach(char c in id ?? "")value=unchecked((value^c)*16777619);return value;
        }
    }
}
