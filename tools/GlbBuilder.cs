// Minimal piping-to-GLB builder. Input coordinates are in mm as [E, N, EL];
// output glTF is meters, Y-up (x = E, y = EL, z = -N).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public struct V3 {
    public double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
    public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    public static V3 operator *(V3 a, double s) { return new V3(a.X * s, a.Y * s, a.Z * s); }
    public static double Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
    public static V3 Cross(V3 a, V3 b) { return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
    public double Len() { return Math.Sqrt(X * X + Y * Y + Z * Z); }
    public V3 Norm() { double l = Len(); return new V3(X / l, Y / l, Z / l); }
    // Any unit vector perpendicular to this (unit) vector.
    public V3 Perp() { V3 h = Math.Abs(Y) < 0.9 ? new V3(0, 1, 0) : new V3(1, 0, 0); return Cross(this, h).Norm(); }
}

public class MeshData {
    public List<float> Pos = new List<float>(), Nrm = new List<float>();
    public List<uint> Idx = new List<uint>();
    public uint Add(V3 p, V3 n) {
        Pos.Add((float)p.X); Pos.Add((float)p.Y); Pos.Add((float)p.Z);
        Nrm.Add((float)n.X); Nrm.Add((float)n.Y); Nrm.Add((float)n.Z);
        return (uint)(Pos.Count / 3 - 1);
    }
}

public class GlbBuilder {
    const int SEG = 32;
    // name -> [r, g, b, metallic, roughness]
    static readonly Dictionary<string, double[]> MAT = new Dictionary<string, double[]> {
        { "pipe",      new[] { 0.62, 0.65, 0.68, 0.8, 0.35 } },
        { "flange",    new[] { 0.32, 0.35, 0.40, 0.7, 0.45 } },
        { "valve",     new[] { 0.20, 0.36, 0.62, 0.4, 0.50 } },
        { "handwheel", new[] { 0.80, 0.12, 0.10, 0.2, 0.50 } },
        { "support",   new[] { 0.95, 0.72, 0.10, 0.0, 0.60 } },
        { "steel",     new[] { 0.45, 0.50, 0.55, 0.5, 0.55 } },
    };
    readonly Dictionary<string, MeshData> meshes = new Dictionary<string, MeshData>();

    MeshData M(string mat) {
        if (!MAT.ContainsKey(mat)) throw new ArgumentException("Unknown material: " + mat);
        MeshData m; if (!meshes.TryGetValue(mat, out m)) { m = new MeshData(); meshes[mat] = m; }
        return m;
    }
    // [E, N, EL] mm -> glTF meters
    static V3 P(double[] a) { return new V3(a[0] / 1000.0, a[2] / 1000.0, -a[1] / 1000.0); }
    static V3 D(double[] a) { return new V3(a[0], a[2], -a[1]).Norm(); }

    // Circular tube swept along pts with given tangents (parallel-transport frames).
    void Tube(MeshData m, List<V3> pts, List<V3> tans, double r) {
        uint start = (uint)(m.Pos.Count / 3);
        V3 n = tans[0].Perp();
        for (int i = 0; i < pts.Count; i++) {
            V3 t = tans[i];
            n = (n - t * V3.Dot(n, t)).Norm();
            V3 b = V3.Cross(t, n);
            for (int s = 0; s <= SEG; s++) {
                double a = 2 * Math.PI * s / SEG;
                V3 d = n * Math.Cos(a) + b * Math.Sin(a);
                m.Add(pts[i] + d * r, d);
            }
        }
        int ring = SEG + 1;
        for (int i = 0; i < pts.Count - 1; i++)
            for (int s = 0; s < SEG; s++) {
                uint a = start + (uint)(i * ring + s), c = a + (uint)ring;
                m.Idx.Add(a); m.Idx.Add(a + 1); m.Idx.Add(c);
                m.Idx.Add(a + 1); m.Idx.Add(c + 1); m.Idx.Add(c);
            }
    }

    void Cyl(MeshData m, V3 c, V3 axis, double len, double r) {
        V3 h = axis * (len / 2);
        Tube(m, new List<V3> { c - h, c + h }, new List<V3> { axis, axis }, r);
    }

    // Flat annulus (rIn may be 0) facing 'axis'.
    void Disk(MeshData m, V3 c, V3 axis, double rIn, double rOut) {
        V3 n = axis.Perp(), b = V3.Cross(axis, n);
        uint start = (uint)(m.Pos.Count / 3);
        for (int s = 0; s <= SEG; s++) {
            double a = 2 * Math.PI * s / SEG;
            V3 d = n * Math.Cos(a) + b * Math.Sin(a);
            m.Add(c + d * rIn, axis); m.Add(c + d * rOut, axis);
        }
        for (int s = 0; s < SEG; s++) {
            uint i = start + (uint)(s * 2);
            m.Idx.Add(i); m.Idx.Add(i + 1); m.Idx.Add(i + 3);
            m.Idx.Add(i); m.Idx.Add(i + 3); m.Idx.Add(i + 2);
        }
    }

    void SolidCyl(MeshData m, V3 c, V3 axis, double len, double r, double bore) {
        Cyl(m, c, axis, len, r);
        Disk(m, c + axis * (len / 2), axis, bore, r);
        Disk(m, c - axis * (len / 2), axis * -1, bore, r);
    }

    // ---- Public components (all dimensions in mm) ----

    // Straight pipe through points with elbows at every corner.
    public void Run(double[] flat, double od, double bendR) {
        var m = M("pipe");
        double r = od / 2000.0, R = bendR / 1000.0;
        var c = new List<V3>();
        for (int i = 0; i + 2 < flat.Length; i += 3) c.Add(P(new[] { flat[i], flat[i + 1], flat[i + 2] }));
        var pts = new List<V3>(); var tans = new List<V3>();
        V3 d0 = (c[1] - c[0]).Norm();
        pts.Add(c[0]); tans.Add(d0);
        for (int i = 1; i < c.Count - 1; i++) {
            V3 d1 = (c[i] - c[i - 1]).Norm(), d2 = (c[i + 1] - c[i]).Norm();
            double cos = Math.Max(-1, Math.Min(1, V3.Dot(d1, d2)));
            if (cos > 0.9999) continue;
            double th = Math.Acos(cos), T = R * Math.Tan(th / 2);
            V3 s = c[i] - d1 * T, nrm = (d2 - d1 * cos).Norm(), ctr = s + nrm * R;
            int k = Math.Max(4, (int)(th / (Math.PI / 2) * 16));
            for (int j = 0; j <= k; j++) {
                double t = th * j / k;
                pts.Add(ctr - nrm * (R * Math.Cos(t)) + d1 * (R * Math.Sin(t)));
                tans.Add((d1 * Math.Cos(t) + nrm * Math.Sin(t)).Norm());
            }
        }
        pts.Add(c[c.Count - 1]); tans.Add((c[c.Count - 1] - c[c.Count - 2]).Norm());
        Tube(m, pts, tans, r);
    }

    public void Flange(double[] c, double[] axis, double od, double thick, double bore) {
        SolidCyl(M("flange"), P(c), D(axis), thick / 1000.0, od / 2000.0, bore / 2000.0);
    }

    // Gate valve: body along pipe axis, bonnet + stem + handwheel along stemDir.
    public void Valve(double[] c, double[] axis, double[] stemDir, double pipeOd, double length) {
        V3 C = P(c), A = D(axis), S = D(stemDir);
        double r = pipeOd / 2000.0, L = length / 1000.0;
        SolidCyl(M("valve"), C, A, L * 0.55, r * 1.9, 0);
        SolidCyl(M("valve"), C + S * (r * 2.2), S, r * 4.4, r * 1.2, 0);
        double stemLen = Math.Max(0.06, r * 3);
        SolidCyl(M("steel"), C + S * (r * 4.4 + stemLen / 2), S, stemLen, Math.Max(0.004, r * 0.18), 0);
        double wheelR = Math.Max(0.05, r * 3.2);
        SolidCyl(M("handwheel"), C + S * (r * 4.4 + stemLen), S, 0.012, wheelR, wheelR * 0.8);
    }

    public void Cap(double[] c, double[] axis, double od) {
        SolidCyl(M("flange"), P(c), D(axis), od / 1000.0 * 0.6, od / 2000.0 * 1.15, 0);
    }

    public void Box(string mat, double[] min, double[] max) {
        var m = M(mat);
        V3 a = P(min), b = P(max);
        V3 mn = new V3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        V3 mx = new V3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        V3 ctr = (mn + mx) * 0.5, half = (mx - mn) * 0.5;
        V3[] faces = { new V3(1,0,0), new V3(-1,0,0), new V3(0,1,0), new V3(0,-1,0), new V3(0,0,1), new V3(0,0,-1) };
        foreach (V3 f in faces) {
            V3 u = Math.Abs(f.Y) > 0.5 ? new V3(1, 0, 0) : new V3(0, 1, 0);
            V3 v = V3.Cross(f, u);
            Func<double, double, V3> Q = (su, sv) => {
                V3 d = f + u * su + v * sv;
                return new V3(ctr.X + d.X * half.X, ctr.Y + d.Y * half.Y, ctr.Z + d.Z * half.Z);
            };
            uint s0 = m.Add(Q(-1, -1), f); m.Add(Q(1, -1), f); m.Add(Q(1, 1), f); m.Add(Q(-1, 1), f);
            m.Idx.Add(s0); m.Idx.Add(s0 + 1); m.Idx.Add(s0 + 2); m.Idx.Add(s0); m.Idx.Add(s0 + 2); m.Idx.Add(s0 + 3);
        }
    }

    // ---- GLB writer ----
    static string F(double d) { return d.ToString("0.######", CultureInfo.InvariantCulture); }

    // floor = true shifts the geometry so its lowest point sits at y = 0 and it is centered in X/Z
    // (for viewers like AR Quick Look that place the model's origin on the detected floor).
    public void Save(string path, string name, bool floor) {
        double ox = 0, oy = 0, oz = 0;
        if (floor) {
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            foreach (var md in meshes.Values)
                for (int i = 0; i < md.Pos.Count; i++) { int k = i % 3; lo[k] = Math.Min(lo[k], md.Pos[i]); hi[k] = Math.Max(hi[k], md.Pos[i]); }
            ox = (lo[0] + hi[0]) / 2; oy = lo[1]; oz = (lo[2] + hi[2]) / 2;
        }
        var saved = new Dictionary<string, List<float>>();
        foreach (var kv in meshes) {
            saved[kv.Key] = kv.Value.Pos;
            var p = new List<float>(kv.Value.Pos.Count);
            for (int i = 0; i < kv.Value.Pos.Count; i += 3) {
                p.Add((float)(kv.Value.Pos[i] - ox)); p.Add((float)(kv.Value.Pos[i + 1] - oy)); p.Add((float)(kv.Value.Pos[i + 2] - oz));
            }
            kv.Value.Pos = p;
        }
        try { Write(path, name); } finally { foreach (var kv in saved) meshes[kv.Key].Pos = kv.Value; }
    }

    void Write(string path, string name) {
        var bin = new MemoryStream(); var bw = new BinaryWriter(bin);
        var views = new List<string>(); var accs = new List<string>(); var jm = new List<string>();
        var nodes = new List<string>(); var mats = new List<string>();
        int mi = 0;
        foreach (var kv in meshes) {
            var md = kv.Value; double[] c = MAT[kv.Key];
            mats.Add("{\"name\":\"" + kv.Key + "\",\"doubleSided\":true,\"pbrMetallicRoughness\":{\"baseColorFactor\":[" +
                F(c[0]) + "," + F(c[1]) + "," + F(c[2]) + ",1],\"metallicFactor\":" + F(c[3]) + ",\"roughnessFactor\":" + F(c[4]) + "}}");
            float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < md.Pos.Count; i++) { int k = i % 3; mn[k] = Math.Min(mn[k], md.Pos[i]); mx[k] = Math.Max(mx[k], md.Pos[i]); }
            int vc = md.Pos.Count / 3;
            long o = bin.Position; foreach (var f in md.Pos) bw.Write(f);
            views.Add("{\"buffer\":0,\"byteOffset\":" + o + ",\"byteLength\":" + (bin.Position - o) + ",\"target\":34962}");
            accs.Add("{\"bufferView\":" + (views.Count - 1) + ",\"componentType\":5126,\"count\":" + vc + ",\"type\":\"VEC3\",\"min\":[" + F(mn[0]) + "," + F(mn[1]) + "," + F(mn[2]) + "],\"max\":[" + F(mx[0]) + "," + F(mx[1]) + "," + F(mx[2]) + "]}");
            o = bin.Position; foreach (var f in md.Nrm) bw.Write(f);
            views.Add("{\"buffer\":0,\"byteOffset\":" + o + ",\"byteLength\":" + (bin.Position - o) + ",\"target\":34962}");
            accs.Add("{\"bufferView\":" + (views.Count - 1) + ",\"componentType\":5126,\"count\":" + vc + ",\"type\":\"VEC3\"}");
            o = bin.Position; foreach (var i in md.Idx) bw.Write(i);
            views.Add("{\"buffer\":0,\"byteOffset\":" + o + ",\"byteLength\":" + (bin.Position - o) + ",\"target\":34963}");
            accs.Add("{\"bufferView\":" + (views.Count - 1) + ",\"componentType\":5125,\"count\":" + md.Idx.Count + ",\"type\":\"SCALAR\"}");
            int a = accs.Count - 3;
            jm.Add("{\"name\":\"" + kv.Key + "\",\"primitives\":[{\"attributes\":{\"POSITION\":" + a + ",\"NORMAL\":" + (a + 1) + "},\"indices\":" + (a + 2) + ",\"material\":" + mi + "}]}");
            nodes.Add(mi.ToString());
            mi++;
        }
        var nodeDefs = new List<string>();
        for (int i = 0; i < mi; i++) nodeDefs.Add("{\"mesh\":" + i + "}");
        string json = "{\"asset\":{\"version\":\"2.0\",\"generator\":\"KGI GlbBuilder\"},\"scene\":0," +
            "\"scenes\":[{\"name\":\"" + name.Replace("\"", "\\\"") + "\",\"nodes\":[" + string.Join(",", nodes) + "]}]," +
            "\"nodes\":[" + string.Join(",", nodeDefs) + "],\"meshes\":[" + string.Join(",", jm) + "],\"materials\":[" + string.Join(",", mats) + "]," +
            "\"accessors\":[" + string.Join(",", accs) + "],\"bufferViews\":[" + string.Join(",", views) + "],\"buffers\":[{\"byteLength\":" + bin.Length + "}]}";
        byte[] jb = Encoding.UTF8.GetBytes(json);
        int jPad = (4 - jb.Length % 4) % 4, bPad = (int)((4 - bin.Length % 4) % 4);
        using (var fs = new BinaryWriter(File.Create(path))) {
            int total = 12 + 8 + jb.Length + jPad + 8 + (int)bin.Length + bPad;
            fs.Write(0x46546C67u); fs.Write(2u); fs.Write((uint)total);
            fs.Write((uint)(jb.Length + jPad)); fs.Write(0x4E4F534Au); fs.Write(jb); for (int i = 0; i < jPad; i++) fs.Write((byte)0x20);
            fs.Write((uint)(bin.Length + bPad)); fs.Write(0x004E4942u); fs.Write(bin.ToArray()); for (int i = 0; i < bPad; i++) fs.Write((byte)0);
        }
    }
}
