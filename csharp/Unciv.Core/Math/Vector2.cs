using System.Globalization;
using System.Text.Json.Serialization;

namespace Unciv.Mathematics;

// Port of the members of libGDX com.badlogic.gdx.math.Vector2 used by Unciv logic code.
//
// Deliberately a mutable *class* (as in libGDX) with equality by value, compared on the raw float bits
// (Java's NumberUtils.floatToIntBits): 0f != -0f, NaN == NaN. Namespace is not `Unciv.Math` because that
// would shadow System.Math for every namespace under Unciv.
public class Vector2 : IEquatable<Vector2>
{
    public static readonly Vector2 UnitX = new(1f, 0f);
    public static readonly Vector2 UnitY = new(0f, 1f);
    public static readonly Vector2 Zero = new(0f, 0f);

    [JsonPropertyName("x")]
    public float X;

    [JsonPropertyName("y")]
    public float Y;

    public Vector2() { }

    public Vector2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public Vector2(Vector2 v)
    {
        X = v.X;
        Y = v.Y;
    }

    public Vector2 Cpy() => new(this);

    public Vector2 Set(float x, float y)
    {
        X = x;
        Y = y;
        return this;
    }

    public Vector2 Set(Vector2 v) => Set(v.X, v.Y);

    public Vector2 SetZero()
    {
        X = 0f;
        Y = 0f;
        return this;
    }

    public Vector2 Add(Vector2 v) => Add(v.X, v.Y);

    public Vector2 Add(float x, float y)
    {
        X += x;
        Y += y;
        return this;
    }

    public Vector2 Sub(Vector2 v) => Sub(v.X, v.Y);

    public Vector2 Sub(float x, float y)
    {
        X -= x;
        Y -= y;
        return this;
    }

    public Vector2 Scl(float scalar)
    {
        X *= scalar;
        Y *= scalar;
        return this;
    }

    public Vector2 Scl(Vector2 v) => Scl(v.X, v.Y);

    public Vector2 Scl(float x, float y)
    {
        X *= x;
        Y *= y;
        return this;
    }

    public float Len() => (float)Math.Sqrt(X * X + Y * Y);

    public float Len2() => X * X + Y * Y;

    public Vector2 Nor()
    {
        float len = Len();
        if (len != 0f)
        {
            X /= len;
            Y /= len;
        }
        return this;
    }

    public float Dot(Vector2 v) => X * v.X + Y * v.Y;

    public float Dot(float ox, float oy) => X * ox + Y * oy;

    public float Crs(Vector2 v) => X * v.Y - Y * v.X;

    public float Dst(Vector2 v)
    {
        float xd = v.X - X;
        float yd = v.Y - Y;
        return (float)Math.Sqrt(xd * xd + yd * yd);
    }

    public float Dst2(Vector2 v)
    {
        float xd = v.X - X;
        float yd = v.Y - Y;
        return xd * xd + yd * yd;
    }

    public bool IsZero() => X == 0f && Y == 0f;

    public bool Equals(Vector2? other) =>
        other != null && FloatToIntBits(X) == FloatToIntBits(other.X) && FloatToIntBits(Y) == FloatToIntBits(other.Y);

    public override bool Equals(object? obj) => obj is Vector2 v && Equals(v);

    // libGDX: prime = 31; result = 1; result = prime * result + floatToIntBits(x); ... y
    public override int GetHashCode()
    {
        unchecked
        {
            int result = 1;
            result = 31 * result + FloatToIntBits(X);
            result = 31 * result + FloatToIntBits(Y);
            return result;
        }
    }

    // Java's floatToIntBits canonicalizes every NaN to 0x7fc00000
    private static int FloatToIntBits(float f) => float.IsNaN(f) ? 0x7fc00000 : BitConverter.SingleToInt32Bits(f);

    // libGDX gives "(x,y)" using Java float formatting; here invariant-culture shortest round-trip (e.g. "1" not "1.0").
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X},{Y})");
}
