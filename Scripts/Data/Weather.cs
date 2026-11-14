using System.Collections.Generic;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// One weather state shared by bridges and tunnels (game balance values in Data/Environment/weather.json).
    /// Weather never lowers material strength directly: it acts through the water level, the current, the
    /// groundwater, softer ground, bed scour and water ponding in tunnel portals.
    /// </summary>
    public class WeatherPreset
    {
        public string Name { get; set; }
        /// <summary>River level rise at the peak, m.</summary>
        public double WaterRise { get; set; }
        /// <summary>River current at the peak, m/s.</summary>
        public double Current { get; set; } = 0.5;
        /// <summary>Groundwater rise from rain soaking in (on top of what the river rise brings), m.</summary>
        public double GroundwaterRise { get; set; }
        /// <summary>Ground softening: soil bearing / anchor capacity × (1 − s), required cover and earth pressure × (1 + s).</summary>
        public double SoilSoftening { get; set; }
        /// <summary>Rain running into the tunnel portals: ponding depth growth, m/s (game time).</summary>
        public double PondRate { get; set; }
        /// <summary>The flood scours the bed in scour zones (shallow pier foundations, tunnel beds and anchors).</summary>
        public bool Scour { get; set; }
        /// <summary>Visual only: rain intensity 0..1 (slanted rain drops) and how overcast the sky turns 0..1.</summary>
        public double Rain { get; set; }
        public double Overcast { get; set; }
        /// <summary>Earthquake: the river widens by this much (right bank moves away, permanently), m.</summary>
        public double QuakeWiden { get; set; }
        /// <summary>Earthquake: peak horizontal ground acceleration, in g.</summary>
        public double QuakeAccel { get; set; }
        /// <summary>Earthquake: shaking frequency, Hz.</summary>
        public double QuakeFrequency { get; set; } = 1.2;
        public bool IsQuake => QuakeWiden > 0 || QuakeAccel > 0;
    }

    /// <summary>Flood protection of the tunnel portals (both banks).</summary>
    public class PortalConfig
    {
        public double MaxRaise { get; set; } = 3;
        public double RaiseStep { get; set; } = 0.5;
        /// <summary>Earthworks for raising both portals, per metre of raise (the extra approach ramp is priced as ramp).</summary>
        public double RaiseCostPerMeter { get; set; }
        /// <summary>Flood gates (both portals): water they hold above the entrance, m; closed gates stop traffic.</summary>
        public double GateCost { get; set; }
        public double GateHeight { get; set; } = 3;
        /// <summary>Gates close when the water comes this close to the entrance, m.</summary>
        public double GateCloseMargin { get; set; } = 0.3;
        /// <summary>Drainage pumps (both portals): ponding they remove, m/s. Pumps cannot hold back a flood.</summary>
        public double PumpCost { get; set; }
        public double PumpRate { get; set; }
        /// <summary>Natural drainage once the rain stops, m/s, and the ponding depth vehicles cannot drive through, m.</summary>
        public double DrainRate { get; set; } = 0.02;
        public double PondBlockDepth { get; set; } = 0.3;
    }

    public class WeatherConfig
    {
        /// <summary>Time to reach the peak and to fall back, s.</summary>
        public double RampTime { get; set; } = 2;
        public Dictionary<string, WeatherPreset> Presets { get; set; } = new();
        public PortalConfig Portal { get; set; } = new();

        public WeatherPreset Preset(string id) =>
            id != null && Presets.TryGetValue(id, out var p) ? p : Presets.TryGetValue("normal", out var n) ? n : new WeatherPreset { Name = "-" };
        public WeatherPreset Normal => Preset("normal");

        /// <summary>Preset of an environment event: Weather id, or a legacy flood event (no Weather) = "flood".</summary>
        public WeatherPreset PresetOf(TrafficEvent e) => Preset(e.Weather ?? "flood");

        /// <summary>Water rise of an event: legacy floods use Height; new events use Rise or the preset.</summary>
        public double RiseOf(TrafficEvent e) => e.Weather == null ? e.Height : e.Rise ?? PresetOf(e).WaterRise;

        /// <summary>Built-in values when weather.json is missing (old installs, custom tools).</summary>
        public static WeatherConfig Default() => new()
        {
            Presets = new()
            {
                ["normal"] = new WeatherPreset { Name = Loc.T("正常"), Current = 0.5 },
                ["rainstorm"] = new WeatherPreset { Name = Loc.T("暴雨"), WaterRise = 2, Current = 0.8, GroundwaterRise = 0.75, SoilSoftening = 0.1, PondRate = 0.06, Rain = 1, Overcast = 0.85 },
                ["flood"] = new WeatherPreset { Name = Loc.T("严重洪水"), WaterRise = 5, Current = 1.1, GroundwaterRise = 2, SoilSoftening = 0.25, PondRate = 0.03, Scour = true, Rain = 0.7, Overcast = 0.75 },
                ["earthquake"] = new WeatherPreset { Name = Loc.T("地震"), Current = 0.5, QuakeWiden = 0.3, QuakeAccel = 0.15, QuakeFrequency = 1.2, Overcast = 0.2 },
            },
        };
    }
}
