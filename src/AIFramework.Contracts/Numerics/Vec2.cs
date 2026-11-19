#nullable enable
using System;
using System.Globalization;

namespace AIFramework.Contracts.Numerics
{
    /// <summary>纯值类型二维向量，零引擎依赖。</summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        public static readonly Vec2 One = new Vec2(1f, 1f);

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float X { get; }

        public float Y { get; }

        public float SqrLength
        {
            get { return (X * X) + (Y * Y); }
        }

        public float Length
        {
            get { return (float)System.Math.Sqrt(SqrLength); }
        }

        public Vec2 Normalized
        {
            get
            {
                float length = Length;
                return length > 0f ? new Vec2(X / length, Y / length) : Zero;
            }
        }

        public static Vec2 operator +(Vec2 a, Vec2 b)
        {
            return new Vec2(a.X + b.X, a.Y + b.Y);
        }

        public static Vec2 operator -(Vec2 a, Vec2 b)
        {
            return new Vec2(a.X - b.X, a.Y - b.Y);
        }

        public static Vec2 operator -(Vec2 a)
        {
            return new Vec2(-a.X, -a.Y);
        }

        public static Vec2 operator *(Vec2 a, float scale)
        {
            return new Vec2(a.X * scale, a.Y * scale);
        }

        public static Vec2 operator *(float scale, Vec2 a)
        {
            return new Vec2(a.X * scale, a.Y * scale);
        }

        public static Vec2 operator /(Vec2 a, float scale)
        {
            return new Vec2(a.X / scale, a.Y / scale);
        }

        public static float Dot(in Vec2 a, in Vec2 b)
        {
            return (a.X * b.X) + (a.Y * b.Y);
        }

        public static float Distance(in Vec2 a, in Vec2 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)System.Math.Sqrt((dx * dx) + (dy * dy));
        }

        public bool Equals(Vec2 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y);
        }

        public override bool Equals(object? obj)
        {
            return obj is Vec2 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() ^ (Y.GetHashCode() << 2);
        }

        public static bool operator ==(Vec2 a, Vec2 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Vec2 a, Vec2 b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return "(" + X.ToString(CultureInfo.InvariantCulture) + ", " + Y.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
