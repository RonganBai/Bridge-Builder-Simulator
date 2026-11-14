using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.World
{
    /// <summary>
    /// Weather look during a test: the sky turns overcast (darker sky, dimmer sun, light fog), slanted rain falls
    /// over the river, and an earthquake shakes the camera. Visual only; the simulation decides the values.
    /// </summary>
    public partial class WeatherVisuals : Node3D
    {
        private GpuParticles3D _rain;
        private ProceduralSkyMaterial _sky;
        private Godot.Environment _env;
        private DirectionalLight3D _sun;
        private Color _top, _horizon, _groundHorizon;
        private float _sunEnergy, _ambient;
        private float _rainNow, _overNow;
        /// <summary>Wind slant of the rain drops (horizontal speed / fall speed).</summary>
        public const float RainSlant = 0.45f;

        public void Setup(Node root, LevelDefinition level)
        {
            Name = "WeatherVisuals";
            foreach (var n in root.FindChildren("*", nameof(WorldEnvironment), true, false))
                if (n is WorldEnvironment we && we.Environment != null) { _env = we.Environment; break; }
            foreach (var n in root.FindChildren("*", nameof(DirectionalLight3D), true, false))
                if (n is DirectionalLight3D d) { _sun = d; break; }
            _sky = _env?.Sky?.SkyMaterial as ProceduralSkyMaterial;
            if (_sky != null) { _top = _sky.SkyTopColor; _horizon = _sky.SkyHorizonColor; _groundHorizon = _sky.GroundHorizonColor; }
            if (_sun != null) _sunEnergy = _sun.LightEnergy;
            if (_env != null) _ambient = _env.AmbientLightEnergy;

            float w = (float)level.Terrain.RiverWidth;
            var drop = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.78f, 0.84f, 0.92f, 0.45f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                BillboardKeepScale = true,
            };
            var process = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(w / 2 + 90, 1, (float)SceneryLayout.HalfDepth),
                Direction = new Vector3(RainSlant, -1, 0).Normalized(),
                Spread = 2,
                InitialVelocityMin = 26, InitialVelocityMax = 32,
                Gravity = Vector3.Zero,
                ParticleFlagAlignY = true,
            };
            _rain = new GpuParticles3D
            {
                Name = "Rain",
                Amount = 6000,
                Lifetime = 1.6,
                Preprocess = 1.6,
                Emitting = false,
                ProcessMaterial = process,
                DrawPass1 = new QuadMesh { Size = new Vector2(0.035f, 0.9f), Material = drop },
                VisibilityAabb = new Aabb(new Vector3(-w / 2 - 140, -60, -(float)SceneryLayout.HalfDepth - 20), new Vector3(w + 280, 100, (float)SceneryLayout.HalfDepth * 2 + 40)),
                Position = new Vector3(w / 2 - 16, 36, 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_rain);
        }

        /// <summary>Called every frame: rain 0..1, overcast 0..1 (eased so the sky changes smoothly).</summary>
        public void Apply(double rain, double overcast, double delta)
        {
            float k = 1 - Mathf.Exp(-(float)delta * 2.5f);
            _rainNow = Mathf.Lerp(_rainNow, (float)rain, k);
            _overNow = Mathf.Lerp(_overNow, (float)overcast, k);
            if (_rain != null)
            {
                _rain.Emitting = _rainNow > 0.02f;
                _rain.AmountRatio = Mathf.Clamp(_rainNow, 0.05f, 1);
            }
            float o = _overNow;
            if (_sky != null)
            {
                var grey = new Color(0.36f, 0.39f, 0.44f);
                _sky.SkyTopColor = _top.Lerp(grey, o * 0.85f);
                _sky.SkyHorizonColor = _horizon.Lerp(new Color(0.55f, 0.57f, 0.6f), o * 0.8f);
                _sky.GroundHorizonColor = _groundHorizon.Lerp(new Color(0.5f, 0.52f, 0.54f), o * 0.7f);
            }
            if (_sun != null) _sun.LightEnergy = _sunEnergy * (1 - 0.6f * o);
            if (_env != null)
            {
                _env.AmbientLightEnergy = _ambient * (1 - 0.25f * o);
                _env.FogEnabled = o > 0.02f;
                _env.FogLightColor = new Color(0.6f, 0.63f, 0.67f);
                _env.FogDensity = 0.0018f * o;
            }
        }
    }
}
