using System.Collections.Generic;
using System.Linq;
using Godot;

namespace BridgeArchitect3D.Core
{
    /// <summary>
    /// Developer frame-time benchmark, only active when the game is started with the user argument --bench
    /// (e.g. "Godot --path . -- --bench level8,level7"). Each level loads its reference design, switches to the
    /// 3D view and runs the traffic test while frame times are sampled; results are printed and the game quits.
    /// All save writes are suspended while it runs, so player saves and progress are never touched.
    /// </summary>
    public partial class Benchmark : Node
    {
        private const double Warmup = 1.5, Sample = 10;
        private readonly Queue<string> _levels = new();
        private readonly List<double> _frames = new();
        private double _t;
        private bool _started;
        private long _drawCalls, _objects;
        private double _gpu, _cpu; private int _timed;

        public static bool Requested(out string levels)
        {
            var args = OS.GetCmdlineUserArgs();
            int i = System.Array.IndexOf(args, "--bench");
            levels = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "level8,level7,level12";
            return i >= 0;
        }

        public void Begin(string levels)
        {
            SaveManager.WritesSuspended = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            // "--low" benchmarks the low quality setting (in memory only; settings are not written)
            GameManager.Instance.Settings.MigrateGraphics();
            GameManager.Instance.Settings.VSync = false; GameManager.Instance.Settings.MaxFps = 0;
            if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--low") >= 0) GameManager.Instance.Settings.ApplyPreset("low");
            int ai = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--aa");
            if (ai >= 0 && ai + 1 < OS.GetCmdlineUserArgs().Length) GameManager.Instance.Settings.Antialiasing = OS.GetCmdlineUserArgs()[ai + 1];
            GameManager.Instance.ApplyQuality();
            // "--mode windowed|fullscreen|borderless" and "--res 1280x720" try display settings (in memory only)
            var ua = OS.GetCmdlineUserArgs(); var st = GameManager.Instance.Settings;
            int mi = System.Array.IndexOf(ua, "--mode"), ri = System.Array.IndexOf(ua, "--res");
            if (mi >= 0 && mi + 1 < ua.Length) st.Mode = ua[mi + 1];
            if (ri >= 0 && ri + 1 < ua.Length) st.Resolution = ua[ri + 1];
            if (mi >= 0 || ri >= 0) GameManager.Instance.ApplySettings();
            GD.Print($"BENCH display mode {st.Mode} res {st.Resolution ?? "-"} -> window {DisplayServer.WindowGetMode()} size {DisplayServer.WindowGetSize()} borderless {DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless)} screen {DisplayServer.ScreenGetSize()} 3D scale {GetTree().Root.Scaling3DScale:0.00}");
            RenderingServer.ViewportSetMeasureRenderTime(GetTree().Root.GetViewportRid(), true);
            GD.Print($"BENCH renderer {RenderingServer.GetCurrentRenderingMethod()} / {RenderingServer.GetCurrentRenderingDriverName()} on {RenderingServer.GetVideoAdapterName()}, window {DisplayServer.WindowGetSize()}");
            foreach (var id in levels.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)) _levels.Enqueue(id);
            Next();
        }

        private void Next()
        {
            if (_levels.Count == 0) { GD.Print("BENCH done"); GetTree().Quit(); return; }
            var gm = GameManager.Instance;
            gm.CurrentLevelId = _levels.Dequeue();
            gm.LoadSavedDesign = false;
            _t = 0; _started = false; _frames.Clear(); _drawCalls = 0; _objects = 0; _gpu = 0; _cpu = 0; _timed = 0;
            GetTree().ChangeSceneToFile(GameManager.LevelScene);
        }

        public override void _Process(double delta)
        {
            if (GameManager.Instance?.CurrentLevelId == null) return;
            _t += delta;
            if (!_started)
            {
                if (_t < 0.5 || GetTree().CurrentScene is not LevelManager lm) return;
                lm.BenchmarkStart();
                _started = true; _t = 0;
                return;
            }
            if (_t < Warmup) return;
            _frames.Add(delta * 1000);
            var vp = GetTree().Root.GetViewportRid();
            _gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp); _cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp); _timed++;
            if (delta > 0.03) GD.Print($"BENCH spike {delta * 1000:0} ms at t={_t:0.00}s (sim t={(GetTree().CurrentScene as LevelManager)?.BenchSimTime ?? -1:0.00})");
            _drawCalls = System.Math.Max(_drawCalls, (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame));
            _objects = System.Math.Max(_objects, (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame));
            // "--shot <folder>" saves one screenshot per level halfway through the sample (for checking visuals)
            var args = OS.GetCmdlineUserArgs(); int si = System.Array.IndexOf(args, "--shot");
            if (si >= 0 && si + 1 < args.Length && _t - delta < Warmup + Sample / 2 && _t >= Warmup + Sample / 2)
                GetViewport().GetTexture().GetImage().SavePng($"{args[si + 1]}/{GameManager.Instance.CurrentLevelId}{(GameManager.Instance.Settings.LowQuality ? "_low" : "")}.png");
            if (_t < Warmup + Sample) return;
            var sorted = _frames.OrderBy(x => x).ToList();
            double avg = _frames.Average(), p99 = sorted[(int)(sorted.Count * 0.99)], worst = sorted[^1];
            GD.Print($"BENCH {GameManager.Instance.CurrentLevelId}{(GameManager.Instance.Settings.LowQuality ? " [低画质]" : "")}: avg {1000 / avg:0} fps ({avg:0.00} ms), p99 {p99:0.00} ms, worst {worst:0.00} ms, draw calls {_drawCalls}, objects {_objects}, frames {_frames.Count}, GPU {_gpu / System.Math.Max(1, _timed):0.00} ms, render CPU {_cpu / System.Math.Max(1, _timed):0.00} ms");
            Next();
        }
    }
}
