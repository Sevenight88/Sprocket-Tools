using System.Numerics;

namespace SprocketTools;

/// Straight cuts propagate to opposite quad edges. Crossing cuts form a grid, never a triangulated fan.
public static class EdgeSubdivision
{
    public static MeshPlans.Rebuild Split(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces,
        IEnumerable<(int A,int B)> selected, int sections, ISet<int>? faceScope = null)
    {
        if (sections < 2 || sections > 16) return MeshPlans.Rebuild.Fail("分割段数请在 2 到 16 之间");
        // Checked before the edge tally below, which indexes the mesh by these very numbers.
        if (pos.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) ||
            faces.Any(f => f.Length < 3 || f.Distinct().Count() != f.Length || f.Any(v => v < 0 || v >= pos.Count)))
            return MeshPlans.Rebuild.Fail("网格含有无效的顶点或面");
        if (faceScope != null && (faceScope.Count == 0 || faceScope.Any(f => f < 0 || f >= faces.Count)))
            return MeshPlans.Rebuild.Fail("请先选择面");
        var uses = new Dictionary<(int,int),List<int>>();
        for (int f=0;f<faces.Count;f++)
            for (int k=0;k<faces[f].Length;k++)
            {
                var key=FaceMerge.Key(faces[f][k],faces[f][(k+1)%faces[f].Length]);
                if (!uses.TryGetValue(key,out var owners)) uses[key]=owners=new();
                owners.Add(f);
            }
        var edges=selected.Select(e=>FaceMerge.Key(e.A,e.B)).ToHashSet();
        if (edges.Count==0) return MeshPlans.Rebuild.Fail("请先选择边");
        if (edges.Any(e=>!uses.ContainsKey(e))) return MeshPlans.Rebuild.Fail("所选边须属于某个面");
        if (faceScope != null && edges.Any(e => !uses[e].Any(faceScope.Contains)))
            return MeshPlans.Rebuild.Fail("所选的边不属于所选的面，请重新选择");
        var pending=new Queue<(int,int)>(edges);
        while (pending.TryDequeue(out var edge))
        {
            if (uses[edge].Count>2) return MeshPlans.Rebuild.Fail("所选切割会传到三个以上面共用的边上");
            foreach (int f in uses[edge])
            {
                if (faceScope != null && !faceScope.Contains(f)) continue;
                var c=faces[f];
                if (c.Length is not (3 or 4)) return MeshPlans.Rebuild.Fail("直线分割只支持三角面或四边形面");
                int k=Enumerable.Range(0,c.Length).First(i=>FaceMerge.Key(c[i],c[(i+1)%c.Length])==edge);
                // Triangles receive an even triangular grid so a cut never ends in a fan at their apex.
                foreach (int side in c.Length==4?new[]{(k+2)%4}:Enumerable.Range(0,3))
                {
                    var next=FaceMerge.Key(c[side],c[(side+1)%c.Length]);
                    if(edges.Add(next)) pending.Enqueue(next);
                }
            }
        }
        var points=new List<MeshPlans.NewPoint>();
        var cuts=new Dictionary<(int,int),int[]>();
        int Add(Vector3 p,(int V,float W)[] blend)
        { int id=pos.Count+points.Count; points.Add(new(p,blend)); return id; }
        foreach(var (a,b) in edges.OrderBy(e=>e.Item1).ThenBy(e=>e.Item2))
        {
            if(Vector3.DistanceSquared(pos[a],pos[b])<1e-12f) return MeshPlans.Rebuild.Fail("有一条边太短，无法分割");
            var path=new int[sections+1]; path[0]=a; path[^1]=b;
            for(int i=1;i<sections;i++)
            {
                float t=i/(float)sections;
                path[i]=Add(Vector3.Lerp(pos[a],pos[b],t),new[]{(a,1-t),(b,t)});
            }
            cuts[(a,b)]=path;
        }
        int OnEdge(int a,int b,int i,int count)
        {
            if(i==0) return a;
            if(i==count) return b;
            var key=FaceMerge.Key(a,b);
            return cuts[key][a==key.Item1?i:sections-i];
        }
        var remove=new List<int>(); var add=new List<MeshPlans.NewFace>();
        for(int f=0;f<faces.Count;f++)
        {
            var c=faces[f];
            bool Cut(int k)=>cuts.ContainsKey(FaceMerge.Key(c[k],c[(k+1)%c.Length]));
            if(!Enumerable.Range(0,c.Length).Any(Cut)) continue;
            remove.Add(f);
            if (faceScope != null && !faceScope.Contains(f))
            {
                // Keep the neighbour as one face, only adding shared boundary vertices. This
                // stops the cut here without introducing a crack or lines across the neighbour.
                var boundary = new List<int>();
                for (int k=0;k<c.Length;k++)
                {
                    int a=c[k], b=c[(k+1)%c.Length]; boundary.Add(a);
                    if (Cut(k)) for(int s=1;s<sections;s++) boundary.Add(OnEdge(a,b,s,sections));
                }
                add.Add(new(boundary.ToArray(),f));
                continue;
            }
            if(c.Length==4)
            {
                int nu=Cut(0)?sections:1, nv=Cut(1)?sections:1;
                var grid=new int[nu+1,nv+1];
                for(int j=0;j<=nv;j++) for(int i=0;i<=nu;i++)
                {
                    if(j==0) grid[i,j]=OnEdge(c[0],c[1],i,nu);
                    else if(j==nv) grid[i,j]=OnEdge(c[3],c[2],i,nu);
                    else if(i==0) grid[i,j]=OnEdge(c[0],c[3],j,nv);
                    else if(i==nu) grid[i,j]=OnEdge(c[1],c[2],j,nv);
                    else
                    {
                        float u=i/(float)nu,v=j/(float)nv;
                        float a=(1-u)*(1-v),b=u*(1-v),d=(1-u)*v,e=u*v;
                        grid[i,j]=Add(a*pos[c[0]]+b*pos[c[1]]+e*pos[c[2]]+d*pos[c[3]],
                            new[]{(c[0],a),(c[1],b),(c[2],e),(c[3],d)});
                    }
                }
                for(int j=0;j<nv;j++) for(int i=0;i<nu;i++)
                    add.Add(new(new[]{grid[i,j],grid[i+1,j],grid[i+1,j+1],grid[i,j+1]},f));
            }
            else
            {
                int n=sections; var grid=new int[n+1,n+1];
                for(int j=0;j<=n;j++) for(int i=0;i+j<=n;i++)
                {
                    if(j==0) grid[i,j]=OnEdge(c[0],c[1],i,n);
                    else if(i==0) grid[i,j]=OnEdge(c[0],c[2],j,n);
                    else if(i+j==n) grid[i,j]=OnEdge(c[1],c[2],j,n);
                    else
                    {
                        float u=i/(float)n,v=j/(float)n;
                        grid[i,j]=Add((1-u-v)*pos[c[0]]+u*pos[c[1]]+v*pos[c[2]],new[]{(c[0],1-u-v),(c[1],u),(c[2],v)});
                    }
                }
                for(int j=0;j<n;j++) for(int i=0;i+j<n;i++)
                {
                    add.Add(new(new[]{grid[i,j],grid[i+1,j],grid[i,j+1]},f));
                    if(i+j<n-1) add.Add(new(new[]{grid[i+1,j],grid[i+1,j+1],grid[i,j+1]},f));
                }
            }
        }
        return new(remove,add,points,null);
    }
}
