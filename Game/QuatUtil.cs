using System.Numerics;

namespace FF14HousingKit.Game;

/// <summary>
/// 四元数与欧拉角互转。
/// 注意：这里照抄 BDTHPlugin 的约定（FF14 的四元数轴向与标准库不同，FromQ 里交换了 Y/Z 分量），
/// 不要用 System.Numerics 的默认公式，否则家具朝向会歪。
/// </summary>
public static class QuatUtil
{
    private const float Deg2Rad = (float)(Math.PI * 2) / 360;
    private const float Rad2Deg = (float)(360 / (Math.PI * 2));

    /// <summary>欧拉角（度）→ 四元数。X=pitch, Y=yaw, Z=roll。</summary>
    public static Quaternion ToQ(Vector3 euler)
    {
        var xOver2 = euler.X * Deg2Rad * 0.5f;
        var yOver2 = euler.Y * Deg2Rad * 0.5f;
        var zOver2 = euler.Z * Deg2Rad * 0.5f;

        var sinX = MathF.Sin(xOver2);
        var cosX = MathF.Cos(xOver2);
        var sinY = MathF.Sin(yOver2);
        var cosY = MathF.Cos(yOver2);
        var sinZ = MathF.Sin(zOver2);
        var cosZ = MathF.Cos(zOver2);

        return new Quaternion
        {
            X = cosY * sinX * cosZ + sinY * cosX * sinZ,
            Y = sinY * cosX * cosZ - cosY * sinX * sinZ,
            Z = cosY * cosX * sinZ - sinY * sinX * cosZ,
            W = cosY * cosX * cosZ + sinY * sinX * sinZ,
        };
    }

    /// <summary>四元数 → 欧拉角（度）。X=pitch, Y=yaw, Z=roll。</summary>
    public static Vector3 FromQ(Quaternion source)
    {
        // 分量重排：BDTH 的约定
        var q = new Quaternion(source.W, source.Z, source.X, source.Y);

        var yaw = MathF.Atan2(2f * q.X * q.W + 2f * q.Y * q.Z, 1 - 2f * (q.Z * q.Z + q.W * q.W));
        var pitch = MathF.Asin(2f * (q.X * q.Z - q.W * q.Y));
        var roll = MathF.Atan2(2f * q.X * q.Y + 2f * q.Z * q.W, 1 - 2f * (q.Y * q.Y + q.Z * q.Z));

        return new Vector3(pitch * Rad2Deg, yaw * Rad2Deg, roll * Rad2Deg);
    }

    /// <summary>只改 yaw，保留原来的 pitch / roll，避免把墙挂或倾斜的家具拍正。</summary>
    public static Quaternion WithYaw(Quaternion current, float yawDegrees)
    {
        var euler = FromQ(current);
        euler.Y = yawDegrees;
        return ToQ(euler);
    }
}
