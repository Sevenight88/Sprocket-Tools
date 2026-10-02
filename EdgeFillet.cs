using System.Numerics;
using static SprocketTools.MeshPlans;

namespace SprocketTools;

/// Circular edge fillets, specified by their physical cylinder radius rather than bevel width.
/// Plans only: an unsupported or oversized corner leaves the input untouched.
public static class EdgeFillet
{
    public static MeshPlans.Rebuild Round(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<(int A, int B)> edges, float radius, int segments = 4)
    {
        if (!float.IsFinite(radius) || radius <= 0) return Rebuild.Fail("圆角半径要大于 0 毫米");
        if (pos.Any(p => !Finite(p)) || faces.Any(f => f.Length < 3 || f.Distinct().Count() != f.Length || f.Any(v => v < 0 || v >= pos.Count)))
            return Rebuild.Fail("网格含有无效的顶点或面");
        if (segments < 2 || segments > 16) return Rebuild.Fail("分段数请在 2 到 16 之间");
        var edgeFaces = EdgeFaces(faces);
        var sel = edges.Select(e => Key(e.A, e.B)).ToHashSet();
        if (sel.Count == 0) return Rebuild.Fail("请选择两侧各有一个面的边");
        if (sel.Any(e => !edgeFaces.TryGetValue(e, out var fs) || fs.Count != 2))
            return Rebuild.Fail("每条所选边都要恰好连着两个面");
        var normals = faces.Select(f => Normal(pos, f)).ToArray();
        var profiles = new Dictionary<(int, int), Profile>();
        float scale = pos.Count == 0 ? 1 : Math.Max(1e-3f, Vector3.Distance(pos.Aggregate(Vector3.Min), pos.Aggregate(Vector3.Max)));
        float tolerance = Math.Max(1e-7f, scale * 2e-6f);
        foreach (var e in sel)
        {
            var sides = edgeFaces[e];
            Vector3 axis = pos[e.Item2] - pos[e.Item1];
            if (axis.Length() <= tolerance || sides.Any(f => normals[f].LengthSquared() < 0.5f))
                return Rebuild.Fail("所选的边或它某一侧的面没有面积");
            axis = Vector3.Normalize(axis);
            int k0 = SideIndex(faces[sides[0]], e), k1 = SideIndex(faces[sides[1]], e);
            if (faces[sides[0]][k0] == faces[sides[1]][k1])
                return Rebuild.Fail("这条边两侧的面绕序相反，请先修正其中一面");
            if (sides.Any(f => faces[f].Any(v => Math.Abs(Vector3.Dot(pos[v] - pos[e.Item1], normals[f])) > tolerance)))
                return Rebuild.Fail("圆角要求这条边两侧的面是平面");
            Vector3 Inward(int f) => Vector3.Normalize(Vector3.Cross(normals[f], faces[f][SideIndex(faces[f], e)] == e.Item1 ? axis : -axis));
            Vector3 d1 = Inward(sides[0]), d2 = Inward(sides[1]);
            float dot = Math.Clamp(Vector3.Dot(d1, d2), -1f, 1f);
            if (Math.Abs(dot) > 0.99999f) return Rebuild.Fail("请选择两个不同平面之间的拐角");
            float setback = radius * MathF.Sqrt((1 + dot) / (1 - dot));
            Vector3 centre = (d1 + d2) * (setback / (1 + dot));
            if (!Finite(centre)) return Rebuild.Fail("这个拐角太尖，圆不出来");
            profiles[e] = new Profile(axis, centre, setback, new Dictionary<int, Vector3> { [sides[0]] = d1, [sides[1]] = d2 });
        }
        var pointFaces = new Dictionary<int, List<int>>();
        for (int f = 0; f < faces.Count; f++)
            foreach (int v in faces[f])
            {
                if (!pointFaces.TryGetValue(v, out var l)) pointFaces[v] = l = new();
                l.Add(f);
            }
        var points = new List<NewPoint>();
        // How each face's corner at a filleted point changes: one point in its place (or two, cutting the corner off).
        var corner = new Dictionary<(int Face, int Point), int[]>();
        var endsAt = new HashSet<int>(); // points where a bevel ends inside the plate and the point stays
        int Add(Vector3 p, int from)
        {
            points.Add(new NewPoint(p, new[] { (from, 1f) }));
            return pos.Count + points.Count - 1;
        }
        Vector3? Corner(int u, IReadOnlyList<int> sector, IReadOnlyList<int> own)
        {
            var constraints = new List<(Vector3 N, float D)>();
            foreach (int f in sector)
            {
                constraints.Add((normals[f], 0));
                foreach (int w in Beside(faces[f], u))
                    if (profiles.TryGetValue(Key(u,w), out var profile)) constraints.Add((profile.Inward[f], profile.Setback));
            }
            Vector3 delta;
            if (own.Count == 1)
            {
                Vector3 along = pos[own[0]] - pos[u];
                var limiting = constraints.OrderByDescending(c => Math.Abs(Vector3.Dot(c.N, along))).First();
                float denominator = Vector3.Dot(limiting.N, along);
                if (Math.Abs(denominator) <= tolerance) return null;
                delta = along * (limiting.D / denominator);
                if (Vector3.Dot(delta,along) <= 0 || delta.Length() >= .49f * along.Length()) return null;
            }
            else
            {
                if (!Solve(constraints, out delta)) return null;
                // A face corner must stay inside the neighbouring original sides.
                foreach (int f in sector) foreach (int w in Beside(faces[f], u))
                {
                    Vector3 along = pos[w] - pos[u];
                    float projection = Vector3.Dot(delta, along) / along.LengthSquared();
                    if (projection < -tolerance || projection >= .49f) return null;
                }
            }
            if (!Finite(delta) || constraints.Any(c => Math.Abs(Vector3.Dot(c.N,delta) - c.D) > tolerance)) return null;
            return pos[u] + delta;
        }

        foreach (int u in sel.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct())
        {
            var around = pointFaces[u];
            var parent = around.ToDictionary(f => f, f => f);
            int Find(int x)
            {
                int root=x; while(parent[root]!=root) root=parent[root];
                while(parent[x]!=x) { int next=parent[x]; parent[x]=root; x=next; }
                return root;
            }
            foreach (int f in around)
                foreach (int w in Beside(faces[f], u))
                    if (!sel.Contains(Key(u, w)))
                        foreach (int g in edgeFaces[Key(u, w)]) if (g != f && parent.ContainsKey(g)) parent[Find(g)] = Find(f);
            var sectors = around.GroupBy(Find).Select(g => g.ToList()).ToList();
            if (sectors.Count >= 2)
            {
                foreach (var sector in sectors)
                {
                    // Intersect the exact tangent offsets while keeping the corner on its original face planes.
                    var own = sector.SelectMany(f => Beside(faces[f], u)).Distinct().Where(w => !sel.Contains(Key(u, w))).ToList();
                    var location = Corner(u, sector, own);
                    if (location is not Vector3 p) return Rebuild.Fail("半径过大，或这个交点圆不了");
                    int id = Add(p, u);
                    foreach (int f in sector) corner[(f, u)] = new[] { id };
                }
                continue;
            }
            // One filleted edge ends here, inside the plate: new points on the two faces' other edges at u. The faces
            // either side take them in place of u; the faces beyond those edges get them added; u goes if nothing
            // else keeps it (a box corner).
            var e0 = sel.First(e => e.Item1 == u || e.Item2 == u);
            var sides = edgeFaces[e0];
            var edgeOf = new Dictionary<int, int>(); // face beside the bevel -> its other edge's far point at u
            var at = new Dictionary<int, int>();     // that far point -> the new point on the edge
            foreach (int f in sides)
            {
                int x = Beside(faces[f], u).First(w => Key(u, w) != e0);
                edgeOf[f] = x;
                if (!at.ContainsKey(x))
                {
                    var location = Corner(u, new[] { f }, new[] { x });
                    if (location is not Vector3 p) return Rebuild.Fail("半径过大，相邻面放不下");
                    at[x] = Add(p, u);
                }
                corner[(f, u)] = new[] { at[x] };
            }
            bool cutOff = false;
            foreach (int g in around.Where(g => !sides.Contains(g)))
            {
                // g's corner at u, between its two neighbours there: add the new point on each side it shares.
                int k = Array.IndexOf(faces[g], u);
                int before = faces[g][(k - 1 + faces[g].Length) % faces[g].Length], after = faces[g][(k + 1) % faces[g].Length];
                bool hasBefore = at.ContainsKey(before), hasAfter = at.ContainsKey(after);
                if (hasBefore && hasAfter) { corner[(g, u)] = new[] { at[before], at[after] }; cutOff = true; } // box corner: u is cut off
                else if (hasBefore) corner[(g, u)] = new[] { at[before], u };
                else if (hasAfter) corner[(g, u)] = new[] { u, at[after] };
            }
            // More faces round u than a box corner: u stays, and the strip's end runs through it (else a hole is left
            // between u and the two new points).
            if (!cutOff) endsAt.Add(u);
        }

        int[] Rebuilt(int f) => faces[f].SelectMany(v => corner.TryGetValue((f, v), out var r) ? r : new[] { v }).ToArray();
        var remove = corner.Keys.Select(k => k.Face).Distinct().ToList();
        var shapes = remove.Select(f => (Corners: Rebuilt(f), Source: f)).ToList();
        // A strip along each filleted edge between its two faces' new corners (a point where the bevel ends in a cut).
        var capSides = new Dictionary<int, List<(int From, int To)>>();
        var curves = new Dictionary<(int, int), int[]>();
        Vector3 Position(int id) => id < pos.Count ? pos[id] : points[id - pos.Count].P;
        int[]? Curve(int from, int to, int u, (int,int) edge)
        {
            var key = Key(from,to);
            if (!curves.TryGetValue(key, out var path))
            {
                Vector3 p = Position(key.Item1), q = Position(key.Item2);
                var profile = profiles[edge];
                Vector3 axis = profile.Axis, centre = pos[u] + profile.Centre;
                float ap = Vector3.Dot(p-centre, axis), aq = Vector3.Dot(q-centre, axis);
                Vector3 rp = p-centre-axis*ap, rq = q-centre-axis*aq;
                if (Math.Abs(rp.Length()-radius) > tolerance || Math.Abs(rq.Length()-radius) > tolerance) return null;
                Vector3 radial = Vector3.Normalize(rp), other = Vector3.Normalize(rq);
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(radial,other),-1,1));
                Vector3 tangent = other-radial*Vector3.Dot(radial,other);
                if (tangent.LengthSquared() < 1e-10f) return null;
                tangent = Vector3.Normalize(tangent);
                // At an oblique end face, the cylinder intersects that face in an ellipse.
                // Solve the axis coordinate from its plane instead of warping the radius.
                Vector3? endNormal = pointFaces[u].Where(f => !edgeFaces[edge].Contains(f))
                    .Select(f => normals[f]).Where(n => Math.Abs(Vector3.Dot(n,axis)) > 1e-5f
                      && Math.Abs(Vector3.Dot(n,p-pos[u])) < tolerance && Math.Abs(Vector3.Dot(n,q-pos[u])) < tolerance)
                    .Select(n => (Vector3?)n).FirstOrDefault();
                path = new int[segments+1]; path[0]=key.Item1; path[^1]=key.Item2;
                for (int s=1;s<segments;s++)
                {
                    float t=s/(float)segments, a=angle*t;
                    Vector3 offset=profile.Centre+radius*(radial*MathF.Cos(a)+tangent*MathF.Sin(a));
                    float along=endNormal is Vector3 normal ? -Vector3.Dot(normal,offset)/Vector3.Dot(normal,axis) : ap+(aq-ap)*t;
                    path[s]=Add(pos[u]+offset+axis*along,u);
                }
                curves[key]=path;
            }
            return from==key.Item1 ? path : path.Reverse().ToArray();
        }
        foreach (var e in sel)
        {
            var fs = edgeFaces[e];
            int f1 = SideIndex(faces[fs[0]], e) is int k0 && faces[fs[0]][k0] == e.Item1 ? fs[0] : fs[1];
            int f2 = fs[0] == f1 ? fs[1] : fs[0];
            int a = e.Item1, b = e.Item2; // f1 runs a -> b, f2 runs b -> a
            int[] At(int f, int v) => corner.TryGetValue((f, v), out var r) ? r : new[] { v };
            {
                var aa = At(f1,a); var ab = At(f2,a); var ba = At(f1,b); var bb = At(f2,b);
                if (new[] { aa, ab, ba, bb }.Any(c => c.Length != 1)) return Rebuild.Fail("这个交点圆不了");
                var ap = Curve(aa[0], ab[0], a, e); var bp = Curve(ba[0], bb[0], b, e);
                if (ap == null || bp == null) return Rebuild.Fail("这个半径接不上这条边的端点");
                for (int s = 0; s < segments; s++)
                    shapes.Add((new[] { bp[s], ap[s], ap[s+1], bp[s+1] }, s * 2 < segments ? f1 : f2));
                foreach (var (u, path, reverse) in new[] { (a, ap, true), (b, bp, false) })
                {
                    for (int s = 0; s < segments; s++)
                    {
                        int p = reverse ? path[s+1] : path[s], q = reverse ? path[s] : path[s+1];
                        if (endsAt.Contains(u))
                        {
                            var n = Vector3.Cross(Position(q)-Position(p), pos[u]-Position(p));
                            int source = pointFaces[u].OrderByDescending(f => Vector3.Dot(n, Vector3.Normalize(Newell(pos, faces[f])))).First();
                            shapes.Add((new[] { p, q, u }, source));
                        }
                        else
                        {
                            if (!capSides.TryGetValue(u, out var sides)) capSides[u] = sides = new();
                            sides.Add((p,q));
                        }
                    }
                }
            }
        }
        if (shapes.Count == remove.Count) return Rebuild.Fail("只选一条边时，它的一端要是拐角，要么在板的边缘上");
        // End faces share the same arc vertices as the rounded strip, so no T-junctions are left behind.
        for (int i = 0; i < remove.Count; i++)
            {
                var (c, source) = shapes[i];
                var expanded = new List<int>();
                for (int k = 0; k < c.Length; k++)
                {
                    int a = c[k], b = c[(k+1)%c.Length]; expanded.Add(a);
                    if (curves.TryGetValue(Key(a,b), out var path))
                        expanded.AddRange((a == path[0] ? path : path.Reverse()).Skip(1).Take(path.Length-2));
                }
                shapes[i] = (expanded.ToArray(), source);
            }
        // Where three or more filleted edges meet, a cap closes the corner.
        foreach (var (u, sidesAtU) in capSides.Where(c => c.Value.Count >= 3))
        {
            var chain = sidesAtU.GroupBy(s => s.From).ToDictionary(g => g.Key, g => g.First().To);
            var loop = new List<int> { sidesAtU[0].From };
            while (loop.Count <= sidesAtU.Count && chain.TryGetValue(loop[^1], out int to) && to != loop[0]) loop.Add(to);
            if (loop.Count == sidesAtU.Count)
            {
                {
                    // A curved multi-edge junction is not planar. A centre fan avoids the sliver
                    // ears made by projecting its near-tangent boundary onto one flat plate.
                    int centre = Add(loop.Aggregate(Vector3.Zero, (sum,v) => sum + Position(v)) / loop.Count, u);
                    for (int k = 0; k < loop.Count; k++)
                    {
                        int p = loop[k], q = loop[(k+1)%loop.Count];
                        var n = Vector3.Cross(Position(q)-Position(p), Position(centre)-Position(p));
                        int source = pointFaces[u].OrderByDescending(f => Vector3.Dot(n, Vector3.Normalize(Newell(pos, faces[f])))).First();
                        shapes.Add((new[] { p,q,centre },source));
                    }
                }
            }
        }
        // Faces that grew past four corners are filled with triangles and quads from their own points.
        var all = pos.Concat(points.Select(p => p.P)).ToList();
        var add = new List<NewFace>();
        foreach (var (c, source) in shapes)
        {
            if (c.Length <= 4) { add.Add(new NewFace(c, source)); continue; }
            var filled = Fill.Region(all, c.ToList(), new List<List<int>>(), Newell(all, c), null);
            if (filled.Count == 0) return Rebuild.Fail("圆角旁边的一个面重建不了");
            // A tangent arc has nearly collinear boundary vertices. Ear clipping can make
            // tiny end-cap slivers; fan a convex cap from an interior point instead.
            float minArea = Newell(pos, faces[source]).Length() * 1e-5f;
            if (filled.Any(x => Newell(all,x).Length() < minArea))
            {
                Vector3 normal = Newell(all,c), centre = c.Aggregate(Vector3.Zero,(sum,v)=>sum+all[v])/c.Length;
                if (Enumerable.Range(0,c.Length).Any(k => Vector3.Dot(Vector3.Cross(all[c[(k+1)%c.Length]]-all[c[k]],centre-all[c[k]]),normal) <= 0))
                    return Rebuild.Fail("这个端面上的面没法按该半径安全填满");
                int middle=pos.Count+points.Count;
                points.Add(new NewPoint(centre,faces[source].Select(v=>(v,1f/faces[source].Length)).ToArray()));
                all.Add(centre);
                for(int k=0;k<c.Length;k++) add.Add(new NewFace(new[]{c[k],c[(k+1)%c.Length],middle},source));
            }
            else add.AddRange(filled.Select(x => new NewFace(x, source)));
        }
        var result = new Rebuild(remove, add, points, null);
        string? why = MeshPlans.Check(pos, faces, result);
        return why == null ? result : Rebuild.Fail("圆角放不进这些面：" + why);
    }


    sealed record Profile(Vector3 Axis, Vector3 Centre, float Setback, Dictionary<int,Vector3> Inward);
    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    static bool Solve(IReadOnlyList<(Vector3 N,float D)> constraints,out Vector3 result)
    {
        // Minimum-length solution of face and tangent-plane constraints.
        var basis=new List<(Vector3 N,float D)>();
        foreach(var (normal,distance) in constraints)
        {
            Vector3 n=normal; float d=distance;
            foreach(var existing in basis)
            {
                float projection=Vector3.Dot(n,existing.N); n-=existing.N*projection; d-=existing.D*projection;
            }
            float length=n.Length();
            if(length>1e-5f) basis.Add((n/length,d/length));
        }
        result=basis.Aggregate(Vector3.Zero,(sum,c)=>sum+c.N*c.D);
        return basis.Count>=2 && Finite(result);
    }
    static Vector3 Normal(IReadOnlyList<Vector3> pos,int[] face)
    {
        Vector3 n=Vector3.Zero,origin=pos[face[0]];
        for(int k=1;k+1<face.Length;k++) n+=Vector3.Cross(pos[face[k]]-origin,pos[face[k+1]]-origin);
        return n.LengthSquared()<1e-18f ? Vector3.Zero : Vector3.Normalize(n);
    }
    static Dictionary<(int, int), List<int>> EdgeFaces(IReadOnlyList<int[]> faces)
    {
        var map = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < faces.Count; f++)
            for (int k = 0; k < faces[f].Length; k++)
            {
                var key = Key(faces[f][k], faces[f][(k + 1) % faces[f].Length]);
                if (!map.TryGetValue(key, out var l)) map[key] = l = new();
                l.Add(f);
            }
        return map;
    }

    /// The corner starting the face's side along `edge`, or -1.
    static int SideIndex(int[] face, (int, int) edge)
    {
        for (int k = 0; k < face.Length; k++) if (Key(face[k], face[(k + 1) % face.Length]) == edge) return k;
        return -1;
    }

    static Vector3 Newell(IReadOnlyList<Vector3> pos, IReadOnlyList<int> loop)
    {
        var n = Vector3.Zero;
        if (loop.Count < 3) return n;
        var origin = pos[loop[0]];
        for (int k = 1; k+1 < loop.Count; k++) n += Vector3.Cross(pos[loop[k]]-origin, pos[loop[k+1]]-origin);
        return n;
    }

    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
    static IEnumerable<int> Beside(int[] face, int u)
    {
        int k = Array.IndexOf(face, u);
        yield return face[(k + 1) % face.Length];
        yield return face[(k - 1 + face.Length) % face.Length];
    }

}
