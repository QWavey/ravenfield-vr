// Offline timing of ShapeFit (a dropped or let-go weapon's collider pieces, fitted on the main thread): the whole fit and
// its stages, on the offline tests' gun-like cloud, from the built RavenfieldVR.dll. The pieces' total hull point count
// is printed with the time: it must not change when the code gets faster.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

internal static class ShapeFitBench
{
    private static int Main(string[] args)
    {
        string managed = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string path = System.IO.Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        return Run(Assembly.LoadFrom(args[1]));
    }

    // Apart from Main: its UnityEngine types are resolved when it is compiled, after the resolver above is in place.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run(Assembly mod)
    {
        Type fit = mod.GetType("RavenfieldVR.Weapons.ShapeFit");
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo cloud = fit.GetMethod("FitCloud", any);
        MethodInfo slices = fit.GetMethod("Slices", any);
        List<Vector3> gun = Cloud();

        object[] pieces = (object[])cloud.Invoke(null, new object[] { gun, 0.03f, 16 });
        int hull = 0;
        long sum = 17;
        foreach (object p in pieces)
        {
            Vector3[] points = (Vector3[])p.GetType().GetField("Points").GetValue(p);
            hull += points.Length;
            foreach (Vector3 v in points)
            {
                // Every point's exact bits, in order: a faster fit must give the very same pieces.
                sum = sum * 31 + BitConverter.ToInt32(BitConverter.GetBytes(v.x), 0);
                sum = sum * 31 + BitConverter.ToInt32(BitConverter.GetBytes(v.y), 0);
                sum = sum * 31 + BitConverter.ToInt32(BitConverter.GetBytes(v.z), 0);
            }
        }
        const int runs = 200;
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < runs; k++)
        {
            cloud.Invoke(null, new object[] { gun, 0.03f, 16 });
        }
        double total = sw.Elapsed.TotalMilliseconds / runs;

        // The slicing alone, as FitPoints' first attempt calls it.
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        foreach (Vector3 p in gun)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        Vector3 ext = max - min;
        int za = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
        int ya = za == 0 ? (ext.y >= ext.z ? 1 : 2) : za == 1 ? (ext.x >= ext.z ? 0 : 2) : (ext.x >= ext.y ? 0 : 1);
        int xa = 3 - za - ya;
        object[] sliceArgs = { gun, min, max, xa, ya, za, 0.03f, 0.012f };
        sw.Restart();
        for (int k = 0; k < runs; k++)
        {
            slices.Invoke(null, sliceArgs);
        }
        double slicing = sw.Elapsed.TotalMilliseconds / runs;

        // Each piece's own work after the slicing: dedupe, thin to 180, make solid (FitPoints' order).
        MethodInfo dedupe = fit.GetMethod("Dedupe", any), thin = fit.GetMethod("Thin", any), solid = fit.GetMethod("Solid", any);
        var hulls = new List<List<Vector3>>();
        foreach (object g in (System.Collections.IEnumerable)slices.Invoke(null, sliceArgs))
        {
            var h = (List<Vector3>)g.GetType().GetField("Hull").GetValue(g);
            if (h.Count >= 4)
            {
                hulls.Add(h);
            }
        }
        double tDedupe = 0, tThin = 0, tSolid = 0;
        for (int k = 0; k < runs; k++)
        {
            foreach (List<Vector3> h in hulls)
            {
                sw.Restart();
                var a = (Vector3[])dedupe.Invoke(null, new object[] { h });
                tDedupe += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var b = (Vector3[])thin.Invoke(null, new object[] { a, 180 });
                tThin += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                solid.Invoke(null, new object[] { b });
                tSolid += sw.Elapsed.TotalMilliseconds;
            }
        }
        Console.WriteLine($"  per fit: dedupe {tDedupe / runs:0.000} ms, thin {tThin / runs:0.000} ms, solid {tSolid / runs:0.000} ms ({hulls.Count} pieces, hull sizes {string.Join("/", hulls.ConvertAll(h => h.Count))})");
        Console.WriteLine($"shape fit: {total:0.000} ms a fit of {gun.Count} points (slicing {slicing:0.000} ms), {pieces.Length} pieces, hull points {hull}, checksum {sum:X}");
        return 0;
    }

    // The offline tests' cloud (OfflineTests.ShapeFitTests): a receiver's surface, a barrel and a raked grip.
    private static List<Vector3> Cloud()
    {
        var rng = new System.Random(7);
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        var gun = new List<Vector3>();
        for (int i = 0; i < 1200; i++)
        {
            float x = R(-0.025f, 0.025f), y = R(-0.04f, 0.04f), z = R(-0.15f, 0.15f);
            int face = rng.Next(3);
            if (face == 0) x = rng.Next(2) == 0 ? -0.025f : 0.025f;
            else if (face == 1) y = rng.Next(2) == 0 ? -0.04f : 0.04f;
            else z = rng.Next(2) == 0 ? -0.15f : 0.15f;
            gun.Add(new Vector3(x, y, z));
        }
        for (int i = 0; i < 900; i++)
        {
            double t = rng.NextDouble() * Math.PI * 2;
            gun.Add(new Vector3(0.012f * (float)Math.Cos(t), 0.02f + 0.012f * (float)Math.Sin(t), R(0.15f, 0.55f)));
        }
        for (int i = 0; i < 500; i++)
        {
            float d = R(0f, 0.11f), u = R(-0.015f, 0.015f), v = R(-0.015f, 0.015f);
            gun.Add(new Vector3(u, -0.04f - d * 0.94f, -0.08f - d * 0.34f + v));
        }
        return gun;
    }
}
