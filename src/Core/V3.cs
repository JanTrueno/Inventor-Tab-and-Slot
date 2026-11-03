using System;
using Inventor;

namespace TabSlotAddIn.Core
{
    /// <summary>Plain 3D vector/point in double precision; keeps the maths off the COM boundary.</summary>
    internal struct V3
    {
        public double X, Y, Z;

        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static V3 From(Point p) => new V3(p.X, p.Y, p.Z);
        public static V3 From(Vector v) => new V3(v.X, v.Y, v.Z);
        public static V3 From(UnitVector v) => new V3(v.X, v.Y, v.Z);

        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator -(V3 a) => new V3(-a.X, -a.Y, -a.Z);
        public static V3 operator *(V3 a, double s) => new V3(a.X * s, a.Y * s, a.Z * s);

        public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public V3 Cross(V3 b) => new V3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(Dot(this));

        public V3 Unit()
        {
            double l = Length;
            return l < 1e-12 ? this : new V3(X / l, Y / l, Z / l);
        }

        public Point ToPoint(TransientGeometry tg) => tg.CreatePoint(X, Y, Z);
        public Vector ToVector(TransientGeometry tg) => tg.CreateVector(X, Y, Z);
    }
}
