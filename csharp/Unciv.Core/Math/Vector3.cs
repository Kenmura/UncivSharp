using System.Globalization;
using System.Text.Json.Serialization;

namespace Unciv.Mathematics;

// Port of the members of libGDX com.badlogic.gdx.math.Vector3 used by Unciv logic code.
// Mutable class with value equality on raw float bits, as in libGDX (see Vector2).
public class Vector3 : IEquatable<Vector3>
{
    public static readonly Vector3 UnitX = new(1f, 0f, 0f);
    public static readonly Vector3 UnitY = new(0f, 1f, 0f);
    public static readonly Vector3 UnitZ = new(0f, 0f, 1f);
    public static readonly Vector3 Zero = new(0f, 0f, 0f);

    [JsonPropertyName("x")]
    public float X;

    [JsonPropertyName("y")]
    public float Y;

    [JsonPropertyName("z")]
    public float Z;

    public Vector3() { }

    public Vector3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Vector3(Vector3 v)
    {
        X = v.X;
        Y = v.Y;
        Z = v.Z;
    }

    public Vector3 Cpy() => new(this);

    public Vector3 Set(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
        return this;
    }

    public Vector3 Set(Vector3 v) => Set(v.X, v.Y, v.Z);

    public Vector3 SetZero()
    {
        X = 0f;
        Y = 0f;
        Z = 0f;
        return this;
    }

    public Vector3 Add(Vector3 v) => Add(v.X, v.Y, v.Z);

    public Vector3 Add(float x, float y, float z) => Set(X + x, Y + y, Z + z);

    public Vector3 Sub(Vector3 v) => Sub(v.X, v.Y, v.Z);

    public Vector3 Sub(float x, float y, float z) => Set(X - x, Y - y, Z - z);

    public Vector3 Scl(float scalar) => Set(X * scalar, Y * scalar, Z * scalar);

    public Vector3 Scl(Vector3 v) => Set(X * v.X, Y * v.Y, Z * v.Z);

    public float Len() => (float)Math.Sqrt(X * X + Y * Y + Z * Z);

    public float Len2() => X * X + Y * Y + Z * Z;

    public Vector3 Nor()
    {
        float len2 = Len2();
        if (len2 == 0f || len2 == 1f) return this;
        return Scl(1f / (float)Math.Sqrt(len2));
    }

    public float Dot(Vector3 v) => X * v.X + Y * v.Y + Z * v.Z;

    public Vector3 Crs(Vector3 v) => Set(Y * v.Z - Z * v.Y, Z * v.X - X * v.Z, X * v.Y - Y * v.X);

    public float Dst(Vector3 v)
    {
        float a = v.X - X;
        float b = v.Y - Y;
        float c = v.Z - Z;
        return (float)Math.Sqrt(a * a + b * b + c * c);
    }

    public float Dst2(Vector3 v)
    {
        float a = v.X - X;
        float b = v.Y - Y;
        float c = v.Z - Z;
        return a * a + b * b + c * c;
    }

    public bool IsZero() => X == 0f && Y == 0f && Z == 0f;

    public bool Equals(Vector3? other) =>
        other != null && FloatToIntBits(X) == FloatToIntBits(other.X)
                      && FloatToIntBits(Y) == FloatToIntBits(other.Y)
                      && FloatToIntBits(Z) == FloatToIntBits(other.Z);

    public override bool Equals(object? obj) => obj is Vector3 v && Equals(v);

    public override int GetHashCode()
    {
        unchecked
        {
            int result = 1;
            result = 31 * result + FloatToIntBits(X);
            result = 31 * result + FloatToIntBits(Y);
            result = 31 * result + FloatToIntBits(Z);
            return result;
        }
    }

    // Java's floatToIntBits canonicalizes every NaN to 0x7fc00000
    private static int FloatToIntBits(float f) => float.IsNaN(f) ? 0x7fc00000 : BitConverter.SingleToInt32Bits(f);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X},{Y},{Z})");
}
