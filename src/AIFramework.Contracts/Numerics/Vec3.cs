#nullable enable
using System;
using System.Globalization;

namespace AIFramework.Contracts.Numerics
{
    /// <summary>纯值类型三维向量，零引擎依赖。</summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);

        public static readonly Vec3 One = new Vec3(1f, 1f, 1f);

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float SqrLength
        {
            get { return (X * X) + (Y * Y) + (Z * Z); }
        }

        public float Length
        {
            get { return (float)System.Math.Sqrt(SqrLength); }
        }

        public Vec3 Normalized
        {
            get
            {
                float length = Length;
                return length > 0f ? new Vec3(X / length, Y / length, Z / length) : Zero;
            }
        }

        public static Vec3 operator +(Vec3 a, Vec3 b)
        {
            return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static Vec3 operator -(Vec3 a, Vec3 b)
        {
            return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static Vec3 operator -(Vec3 a)
        {
            return new Vec3(-a.X, -a.Y, -a.Z);
        }

        public static Vec3 operator *(Vec3 a, float scale)
        {
            return new Vec3(a.X * scale, a.Y * scale, a.Z * scale);
        }

        public static Vec3 operator *(float scale, Vec3 a)
        {
            return new Vec3(a.X * scale, a.Y * scale, a.Z * scale);
        }

        public static float Dot(in Vec3 a, in Vec3 b)
        {
            return (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        }

        public static float Distance(in Vec3 a, in Vec3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return (float)System.Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        public bool Equals(Vec3 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        }

        public override bool Equals(object? obj)
        {
            return obj is Vec3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
        }

        public static bool operator ==(Vec3 a, Vec3 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Vec3 a, Vec3 b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return "(" + X.ToString(CultureInfo.InvariantCulture) + ", " + Y.ToString(CultureInfo.InvariantCulture) + ", " + Z.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
