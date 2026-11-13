using Godot;

namespace BridgeArchitect3D.CameraControl
{
    /// <summary>
    /// 自由观察相机：右键拖动旋转、滚轮缩放、中键平移；Home 重置、F1 侧视、F2 俯视、F3 透视。
    /// 所有参数通过插值平滑过渡。
    /// </summary>
    public partial class OrbitCamera : Camera3D
    {
        public float Sensitivity = 1f;
        /// <summary>当前是否为正交视图（侧视 / 俯视）。</summary>
        public bool IsOrtho => _ortho;

        private Vector3 _target, _targetGoal;
        private float _yaw, _pitch, _dist;
        private float _yawGoal, _pitchGoal, _distGoal;
        private Vector3 _homeTarget;
        private float _homeDist = 30;
        private float _zoomLimit = 150;
        private bool _ortho;
        private float _rightDrag;
        private const float RotateThreshold = 12f;   // 右键拖动超过多少像素才开始旋转

        /// <summary>侧视 / 俯视（正交）时使用的固定米色背景，保证建造网格清晰可见。</summary>
        public static readonly Color SideBackground = new("efe6d2");
        private Godot.Environment _orthoEnv;

        /// <summary>设置默认观察中心与距离（关卡加载时调用）。</summary>
        public void Setup(Vector3 center, float distance)
        {
            _homeTarget = center; _homeDist = distance;
            ResetView(true);
        }

        public void ResetView(bool snap = false)
        {
            _zoomLimit = Mathf.Max(150, _homeDist);
            _ortho = false;
            _targetGoal = _homeTarget; _yawGoal = 0.35f; _pitchGoal = -0.25f; _distGoal = _homeDist;
            if (snap) { _target = _targetGoal; _yaw = _yawGoal; _pitch = _pitchGoal; _dist = _distGoal; }
        }

        /// <summary>正交侧视（建造默认视角）。snap=true 时立即到位，不做过渡。</summary>
        public void SideView(bool snap = false)
        {
            _zoomLimit = Mathf.Max(150, _homeDist);
            _ortho = true; _targetGoal = _homeTarget; _yawGoal = 0; _pitchGoal = 0; _distGoal = _homeDist;
            if (snap) { _target = _targetGoal; _yaw = 0; _pitch = 0; _dist = _distGoal; }
        }
        /// <summary>Frame affected geometry in the usable center area, without changing the home view.</summary>
        public void FrameDiagnostic(Vector3 center,Vector3 extent,float maxDistance = 150)
        {
            _ortho=true; _targetGoal=center; _yawGoal=0; _pitchGoal=0;
            _zoomLimit = Mathf.Max(maxDistance, _homeDist);
            Far = Mathf.Max(Far, _zoomLimit + 200);
            var viewport=GetViewport().GetVisibleRect().Size;
            float aspect=Mathf.Max(0.4f,(viewport.X-480)/Mathf.Max(1,viewport.Y));
            _distGoal=Mathf.Clamp(Mathf.Max(extent.Y+4,(extent.X+4)/aspect)*1.35f/0.85f,8,_zoomLimit);
        }

        public void RememberFrameAsHome() {_homeTarget=_targetGoal;_homeDist=_distGoal;}
        public void RememberBoundsAsHome(Vector3 center,Vector3 extent)
        {
            var viewport=GetViewport().GetVisibleRect().Size;
            float aspect=Mathf.Max(0.4f,(viewport.X-480)/Mathf.Max(1,viewport.Y));
            _homeTarget=center;_homeDist=Mathf.Clamp(Mathf.Max(extent.Y+4,(extent.X+4)/aspect)*1.35f/0.85f,8,2000);
            Far=Mathf.Max(Far,_homeDist+200);
        }

        private bool _following;
        /// <summary>Follow a moving point in perspective (the first call frames it; scroll zoom still works).</summary>
        public void Follow(Vector3 p)
        {
            if (!_following) { _following = true; _ortho = false; _distGoal = 22f; _pitchGoal = -0.3f; }
            _targetGoal = p + new Vector3(0, 1.5f, 0);
        }
        public void StopFollow() { if (_following) { _following = false; _targetGoal = _homeTarget; } }

        public void TopView() { _ortho = true; _targetGoal = _homeTarget; _yawGoal = 0; _pitchGoal = -1.5533f; _distGoal = _homeDist; }
        public void PerspectiveView() { _ortho = false; _yawGoal = 0.6f; _pitchGoal = -0.45f; _distGoal = _homeDist * 0.9f; }

        public override void _UnhandledInput(InputEvent e)
        {
            if (e is InputEventMouseMotion mm)
            {
                if ((mm.ButtonMask & MouseButtonMask.Right) != 0)
                {
                    // 右键单击（例如取消连接）时鼠标的轻微抖动不应把侧视切成 3D：拖动超过阈值才开始旋转
                    _rightDrag += mm.Relative.Length();
                    if (_rightDrag < RotateThreshold) return;
                    _yawGoal -= mm.Relative.X * 0.005f * Sensitivity;
                    _pitchGoal = Mathf.Clamp(_pitchGoal - mm.Relative.Y * 0.005f * Sensitivity, -1.55f, 0.6f);
                    _ortho = false;   // 旋转时回到透视，避免正交视图下方向迷失
                }
                else if ((mm.ButtonMask & MouseButtonMask.Middle) != 0)
                {
                    var b = GlobalTransform.Basis;
                    float k = _dist * 0.0018f;
                    _targetGoal += (-b.X * mm.Relative.X + b.Y * mm.Relative.Y) * k;
                }
            }
            else if (e is InputEventMouseButton rmb && rmb.ButtonIndex == MouseButton.Right)
            {
                _rightDrag = 0;   // 每次按下 / 松开右键都重新计算拖动距离
            }
            else if (e is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp) _distGoal = Mathf.Max(5, _distGoal * 0.9f);
                else if (mb.ButtonIndex == MouseButton.WheelDown) _distGoal = Mathf.Min(_zoomLimit, _distGoal * 1.1f);
            }
            else if (e is InputEventKey k && k.Pressed && !k.Echo)
            {
                switch (k.Keycode)
                {
                    case Key.Home: SideView(); break;
                    case Key.F1: SideView(); break;
                    case Key.F2: TopView(); break;
                    case Key.F3: PerspectiveView(); break;
                }
            }
        }

        public override void _Process(double delta)
        {
            float a = 1f - Mathf.Exp(-(float)delta * 10f);  // 帧率无关的平滑系数
            _yaw = Mathf.Lerp(_yaw, _yawGoal, a);
            _pitch = Mathf.Lerp(_pitch, _pitchGoal, a);
            _dist = Mathf.Lerp(_dist, _distGoal, a);
            _target = _target.Lerp(_targetGoal, a);

            Projection = _ortho ? ProjectionType.Orthogonal : ProjectionType.Perspective;
            // 正交视图用相机自己的环境（纯米色背景、无天空渐变），透视视图回到场景的天空
            if (_ortho && _orthoEnv == null)
                _orthoEnv = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = SideBackground,
                    AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(1, 1, 1), AmbientLightEnergy = 0.8f,
                };
            Environment = _ortho ? _orthoEnv : null;
            Size = _dist * 0.85f;
            var dir = new Vector3(Mathf.Sin(_yaw) * Mathf.Cos(_pitch), -Mathf.Sin(_pitch), Mathf.Cos(_yaw) * Mathf.Cos(_pitch));
            // Orthographic zoom comes from Size, so the camera can stand back outside the scenery: placed closer
            // than the scene depth it sits inside the bank soil blocks and their faces get culled (banks vanish)
            float back = _ortho ? Mathf.Max(_dist, (float)BridgeArchitect3D.Data.SceneryLayout.HalfDepth + 40f) : _dist;
            var pos = _target + dir * back;
            // 俯视时 up 向量改为 -Z，避免 LookAt 与竖直方向平行
            var up = Mathf.Abs(_pitch) > 1.45f ? Vector3.Forward : Vector3.Up;
            LookAtFromPosition(pos, _target, up);
        }
    }
}
