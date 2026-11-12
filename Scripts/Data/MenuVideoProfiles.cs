using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    public sealed record MenuVideoProfile(int Width,int Height,string Path)
    {
        public long Pixels=>(long)Width*Height;
        public string Resolution=>$"{Width}x{Height}";
    }

    /// <summary>Exact configured resolution first, with deterministic fallback for custom or missing assets.</summary>
    public static class MenuVideoProfiles
    {
        public static IReadOnlyList<MenuVideoProfile> All { get; }=new[]
        {
            new MenuVideoProfile(1280,720,"res://Media/homepage/harbor-1280x720.ogv"),
            new MenuVideoProfile(1366,768,"res://Media/homepage/harbor-1366x768.ogv"),
            new MenuVideoProfile(1600,900,"res://Media/homepage/harbor-1600x900.ogv"),
            new MenuVideoProfile(1920,1080,"res://Media/homepage/harbor-1920x1080.ogv"),
            new MenuVideoProfile(1980,1080,"res://Media/homepage-loop-forward.ogv"),
            new MenuVideoProfile(2560,1440,"res://Media/homepage/harbor-2560x1440.ogv"),
            new MenuVideoProfile(3840,2160,"res://Media/homepage/harbor-3840x2160.ogv")
        };

        public static IEnumerable<MenuVideoProfile> Ranked(int width,int height)
        {
            if(width<=0 || height<=0)throw new ArgumentOutOfRangeException(nameof(width));
            bool Fits(MenuVideoProfile p)=>p.Width>=width && p.Height>=height;
            return All.OrderBy(p=>p.Width==width && p.Height==height?0:1)
                .ThenBy(p=>Fits(p)?0:1)
                .ThenBy(p=>Fits(p)?Math.Round(Math.Abs(Math.Log((p.Width/(double)p.Height)/(width/(double)height))),3):0)
                .ThenBy(p=>Fits(p)?p.Pixels:-p.Pixels);
        }

        public static MenuVideoProfile Select(int width,int height,Func<string,bool> exists)=>Ranked(width,height).FirstOrDefault(p=>exists(p.Path));
    }
}
