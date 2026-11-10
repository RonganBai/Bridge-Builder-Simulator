using System;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// 不依赖 Godot 的双精度三维向量，用于数据模型与结构求解（单位：米）。
    /// 初版建造平面为 Z=0，但数据始终保存三维坐标，便于后续升级到完整 3D 求解。
    /// </summary>
    public struct Vec3d
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public Vec3d(double x, double y, double z = 0) { X = x; Y = y; Z = z; }

        public static Vec3d operator +(Vec3d a, Vec3d b) => new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3d operator -(Vec3d a, Vec3d b) => new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3d operator *(Vec3d a, double s) => new Vec3d(a.X * s, a.Y * s, a.Z * s);

        public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
        public static double Distance(Vec3d a, Vec3d b) => (a - b).Length();
        public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + (b - a) * t;
        public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
    }
}
