using System;
using System.Diagnostics;

namespace RavenfieldVR.Tests
{
    /// <summary>
    /// Offline timing of HandModel.NearSkin (is a point within reach of a glove's skin: 15 finger capsules and a palm box), as
    /// copies of the test before and after its bounding sphere, on a hand-shaped skin and the points another hand's glove asks
    /// about (most of them well clear of it). Both must answer every point alike. The old copy turns the point into palm space
    /// with a matrix where the game called Transform.InverseTransformPoint (a native call): its time here is a lower bound.
    /// Build and run: tools/tests/bench.ps1.
    /// </summary>
    internal static class SkinBench
    {
        private struct V
        {
            public float X, Y, Z;
            public V(float x, float y, float z) { X = x; Y = y; Z = z; }
        }

        private static readonly V[] A = new V[15], B = new V[15];
        private static readonly float[] R = new float[15];
        private static V _center;
        private static float _radius;
        private const float Grow = 0.012f;
        // The palm box (palm space = world here: the hand at the origin, unscaled).
        private static readonly V Lo = new V(-0.045f, -0.012f, -0.05f), Hi = new V(0.045f, 0.012f, 0.05f);

        private static int Main()
        {
            // Five fingers of three capsules out of the palm's front edge, 1 cm thick.
            int n = 0;
            for (int f = 0; f < 5; f++)
            {
                float x = -0.04f + f * 0.02f;
                for (int s = 0; s < 3; s++)
                {
                    A[n] = new V(x, 0f, 0.05f + s * 0.03f);
                    B[n] = new V(x, -0.005f * s, 0.08f + s * 0.03f);
                    R[n] = 0.009f;
                    n++;
                }
            }
            // The bounding sphere as HandModel.UpdateSkin makes it: the box round every capsule end and palm corner, plus the
            // thickest capsule.
            V min = A[0], max = A[0];
            void Take(V p)
            {
                min = new V(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new V(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
            for (int i = 0; i < 15; i++)
            {
                Take(A[i]);
                Take(B[i]);
            }
            for (int c = 0; c < 8; c++)
            {
                Take(new V((c & 1) == 0 ? Lo.X : Hi.X, (c & 2) == 0 ? Lo.Y : Hi.Y, (c & 4) == 0 ? Lo.Z : Hi.Z));
            }
            _center = new V((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f);
            float dx = max.X - min.X, dy = max.Y - min.Y, dz = max.Z - min.Z;
            _radius = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) * 0.5f + 0.009f;

            // Another hand's glove points: three in ten within 15 cm of this hand (cupping it), the rest 15-40 cm off.
            var rng = new Random(11);
            var pts = new V[1000000];
            for (int i = 0; i < pts.Length; i++)
            {
                double dist = rng.NextDouble() < 0.3 ? rng.NextDouble() * 0.15 : 0.15 + rng.NextDouble() * 0.25;
                double u = rng.NextDouble() * 2 - 1, phi = rng.NextDouble() * Math.PI * 2, s = Math.Sqrt(1 - u * u);
                pts[i] = new V((float)(dist * s * Math.Cos(phi)), (float)(dist * s * Math.Sin(phi)), 0.06f + (float)(dist * u));
            }
            int mismatch = 0, near = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                bool o = Old(pts[i]), w = New(pts[i]);
                mismatch += o != w ? 1 : 0;
                near += w ? 1 : 0;
            }
            double oldNs = Time(Old, pts), newNs = Time(New, pts);
            Console.WriteLine($"NearSkin (managed copies, 1,000,000 points, {near} near the skin): plain {oldNs:0.0} ns, with the bounding sphere {newNs:0.0} ns"
                              + (mismatch == 0 ? " (same answers)" : $" ({mismatch} DIFFERENT)"));
            return mismatch == 0 ? 0 : 1;
        }

        private static double Time(Func<V, bool> test, V[] pts)
        {
            double best = double.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                int h = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < pts.Length; i++)
                {
                    h += test(pts[i]) ? 1 : 0;
                }
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / pts.Length + h * 0);
            }
            return best;
        }

        private static bool Capsules(V p)
        {
            for (int i = 0; i < 15; i++)
            {
                V a = A[i];
                float abx = B[i].X - a.X, aby = B[i].Y - a.Y, abz = B[i].Z - a.Z;
                float t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby + (p.Z - a.Z) * abz) / Math.Max(abx * abx + aby * aby + abz * abz, 1e-8f);
                t = t < 0f ? 0f : t > 1f ? 1f : t;
                float cx = a.X + abx * t - p.X, cy = a.Y + aby * t - p.Y, cz = a.Z + abz * t - p.Z, r = R[i] + Grow;
                if (cx * cx + cy * cy + cz * cz <= r * r)
                {
                    return true;
                }
            }
            return false;
        }

        // The palm box after the capsules (here palm space is world: the native InverseTransformPoint left out).
        private static bool Palm(V l) => l.Z > Lo.Z - Grow && l.Z < Hi.Z + Grow && l.Y > Lo.Y - Grow && l.Y < Hi.Y + Grow && l.X > Lo.X - Grow && l.X < Hi.X + Grow;

        private static bool Old(V p) => Capsules(p) || Palm(p);

        private static bool New(V p)
        {
            float ox = p.X - _center.X, oy = p.Y - _center.Y, oz = p.Z - _center.Z, reach = _radius + Grow;
            if (ox * ox + oy * oy + oz * oz > reach * reach)
            {
                return false;
            }
            return Capsules(p) || Palm(p);
        }
    }
}
