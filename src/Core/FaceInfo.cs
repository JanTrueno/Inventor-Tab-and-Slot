using System;
using System.Collections.Generic;
using Inventor;

namespace TabSlotAddIn.Core
{
    /// <summary>
    /// A planar face picked in the assembly, resolved to everything the tab/slot maths needs:
    /// its owning occurrence and part, the outward normal, an in-plane frame and the extents of the face in that frame.
    /// All coordinates are in the assembly (world) space of the active document.
    /// </summary>
    internal sealed class FaceInfo
    {
        private const int MaxBodyVertices = 6000;

        public FaceProxy Proxy;
        public ComponentOccurrence Occurrence;
        public Face Native;
        public PartComponentDefinition PartDef;
        public PartDocument PartDoc;

        /// <summary>A point on the face plane; the origin of the (U,V) frame.</summary>
        public V3 Origin;
        /// <summary>Outward face normal.</summary>
        public V3 Normal;
        public V3 U, V;
        public double UMin, UMax, VMin, VMax;

        /// <summary>Vertices of the whole body the face belongs to (used for thickness / flush height).</summary>
        public List<V3> BodyPoints = new List<V3>();

        public double Length => UMax - UMin;
        public double Thickness => VMax - VMin;

        public string DisplayName
        {
            get
            {
                try { return Occurrence != null ? Occurrence.Name : "?"; }
                catch { return "?"; }
            }
        }

        public V3 Point(double u, double v) => Origin + U * u + V * v;

        /// <summary>A flat band on the face along which tabs are laid out: Origin, +U*Length, +V*Thickness.</summary>
        public struct Strip
        {
            public V3 Origin, U, V;
            public double Length, Thickness;
        }

        /// <summary>One strip for an ordinary face, one per flat wall for a square tube end.</summary>
        public List<Strip> Strips = new List<Strip>();

        /// <summary>True when the face is the end of a hollow section (a ring-shaped face).</summary>
        public bool IsTube;

        private static List<(V3 A, V3 B)> LinearEdges(System.Collections.IEnumerable edges)
        {
            var list = new List<(V3, V3)>();
            foreach (Edge e in edges)
            {
                if (e.GeometryType != CurveTypeEnum.kLineSegmentCurve) continue;
                list.Add((V3.From(e.StartVertex.Point), V3.From(e.StopVertex.Point)));
            }
            return list;
        }

        private static System.Collections.IEnumerable EdgesOf(EdgeLoop loop)
        {
            return loop.Edges;
        }

        /// <summary>
        /// Works out where tabs can go. A ring-shaped end face (square tube) gives one strip per straight wall, limited to
        /// the part of the wall that is flat on both the outside and the inside, so rounded corners are left alone.
        /// Any other face gives a single strip.
        /// </summary>
        private void BuildStrips(FaceProxy proxy, bool alongShort)
        {
            Strips.Clear();
            IsTube = false;
            try
            {
                if (proxy.EdgeLoops.Count == 2 && TryTubeStrips(proxy)) { IsTube = true; return; }   // a ring is always a tube, however it was picked
            }
            catch { /* fall back to the single strip */ }
            Strips.Add(new Strip { Origin = Point(UMin, VMin), U = U, V = V, Length = UMax - UMin, Thickness = VMax - VMin });
        }

        private bool TryTubeStrips(FaceProxy proxy)
        {
            EdgeLoop outer = null, inner = null;
            foreach (EdgeLoop loop in proxy.EdgeLoops)
            {
                if (loop.IsOuterEdgeLoop) outer = loop; else inner = loop;
            }
            if (outer == null || inner == null) return false;

            var outerLines = LinearEdges(EdgesOf(outer));
            var innerLines = LinearEdges(EdgesOf(inner));
            if (outerLines.Count < 3 || innerLines.Count < 3) return false;

            V3 centre = default(V3);
            foreach (var (a, b) in outerLines) centre = centre + a + b;
            centre = centre * (1.0 / (outerLines.Count * 2));

            var strips = new List<Strip>();
            foreach (var (a, b) in outerLines)
            {
                V3 d = (b - a).Unit();
                V3 mid = (a + b) * 0.5;
                V3 toCentre = centre - mid;
                V3 inward = (toCentre - d * toCentre.Dot(d)).Unit();

                // the inner wall line facing this outer wall: parallel and the nearest one on the inside
                double bestOffset = double.MaxValue;
                (V3 A, V3 B) bestInner = default;
                foreach (var (ia, ib) in innerLines)
                {
                    if (Math.Abs((ib - ia).Unit().Dot(d)) < 0.999) continue;
                    double offset = ((ia + ib) * 0.5 - mid).Dot(inward);
                    if (offset > 1e-6 && offset < bestOffset) { bestOffset = offset; bestInner = (ia, ib); }
                }
                if (bestOffset == double.MaxValue) continue;

                // flat on both sides: the overlap of the two straight edges
                double outerLen = (b - a).Length;
                double i0 = (bestInner.A - a).Dot(d), i1 = (bestInner.B - a).Dot(d);
                double lo = Math.Max(0, Math.Min(i0, i1)), hi = Math.Min(outerLen, Math.Max(i0, i1));
                if (hi - lo < 1e-4) continue;

                // keep U x V equal to the face normal so the preview solids are not mirrored
                V3 u = d, start = a;
                if (Normal.Cross(u).Dot(inward) < 0)
                {
                    u = -d; start = b;
                    double newLo = outerLen - hi, newHi = outerLen - lo;
                    lo = newLo; hi = newHi;
                }
                strips.Add(new Strip { Origin = start + u * lo, U = u, V = Normal.Cross(u).Unit(), Length = hi - lo, Thickness = bestOffset });
            }
            if (strips.Count < 2) return false;
            Strips = strips;
            return true;
        }

        /// <summary>Builds the frame for a picked object; returns null and sets <paramref name="error"/> if it is unusable.</summary>
        public static FaceInfo FromSelection(object picked, bool runAlongShortEdge, out string error)
        {
            error = null;
            var proxy = picked as FaceProxy;
            if (proxy == null)
            {
                error = "Pick a face on a component in the assembly (finish any in-place edit first).";
                return null;
            }
            if (proxy.SurfaceType != SurfaceTypeEnum.kPlaneSurface)
            {
                error = "The face must be planar.";
                return null;
            }

            var info = new FaceInfo { Proxy = proxy };
            try
            {
                info.Native = proxy.NativeObject;
                info.Occurrence = proxy.ContainingOccurrence;
                info.PartDef = info.Occurrence.Definition as PartComponentDefinition;
            }
            catch (Exception ex)
            {
                error = "Could not resolve the component for this face: " + ex.Message;
                return null;
            }
            if (info.PartDef == null)
            {
                error = "The face must belong to a part component.";
                return null;
            }
            info.PartDoc = (PartDocument)info.PartDef.Document;
            if (info.PartDoc.SubType == "{9C464203-9BAE-11D3-8BAD-0060B0CE6BB4}")
            {
                try { info.SheetThickness = Convert.ToDouble(((SheetMetalComponentDefinition)info.PartDef).Thickness.Value); }
                catch { /* fall back to measuring the body */ }
            }

            var plane = (Plane)proxy.Geometry;
            V3 n = V3.From(plane.Normal);
            if (proxy.IsParamReversed) n = -n;
            info.Normal = n.Unit();

            // Vertices + longest straight edge give the in-plane frame.
            var verts = new List<V3>();
            foreach (Vertex v in proxy.Vertices) verts.Add(V3.From(v.Point));
            if (verts.Count < 3)
            {
                error = "The face has too few vertices to place a tab on it.";
                return null;
            }

            V3 longDir = default(V3);
            double longLen = 0;
            foreach (Edge e in proxy.Edges)
            {
                if (e.GeometryType != CurveTypeEnum.kLineSegmentCurve) continue;
                V3 a = V3.From(e.StartVertex.Point), b = V3.From(e.StopVertex.Point);
                double len = (b - a).Length;
                if (len > longLen) { longLen = len; longDir = (b - a); }
            }
            if (longLen < 1e-9)
            {
                // No straight edges: pick any in-plane direction.
                V3 any = Math.Abs(info.Normal.Z) < 0.9 ? new V3(0, 0, 1) : new V3(1, 0, 0);
                longDir = info.Normal.Cross(any);
            }
            V3 u = (longDir - info.Normal * longDir.Dot(info.Normal)).Unit();
            V3 w = info.Normal.Cross(u).Unit();
            if (runAlongShortEdge) { V3 t = u; u = w; w = -t; }

            info.U = u; info.V = w;
            info.Origin = verts[0];
            info.UMin = info.VMin = double.MaxValue;
            info.UMax = info.VMax = double.MinValue;
            foreach (V3 p in verts)
            {
                V3 d = p - info.Origin;
                double pu = d.Dot(u), pv = d.Dot(w);
                info.UMin = Math.Min(info.UMin, pu); info.UMax = Math.Max(info.UMax, pu);
                info.VMin = Math.Min(info.VMin, pv); info.VMax = Math.Max(info.VMax, pv);
            }

            // Use only the straight (flat) part along the tab direction, so rounded corners do not stretch the face.
            double flatMin = double.MaxValue, flatMax = double.MinValue;
            foreach (var (ea, eb) in LinearEdges(proxy.Edges))
            {
                if (Math.Abs((eb - ea).Unit().Dot(u)) < 0.999) continue;
                foreach (V3 q in new[] { ea, eb })
                {
                    double pu = (q - info.Origin).Dot(u);
                    flatMin = Math.Min(flatMin, pu); flatMax = Math.Max(flatMax, pu);
                }
            }
            if (flatMax - flatMin > 1e-6) { info.UMin = flatMin; info.UMax = flatMax; }

            info.BuildStrips(proxy, runAlongShortEdge);
            info.CollectBodyPoints();
            return info;
        }

        private void CollectBodyPoints()
        {
            try
            {
                SurfaceBody body = Proxy.SurfaceBody;
                if (body.Vertices.Count <= MaxBodyVertices)
                {
                    foreach (Vertex v in body.Vertices) BodyPoints.Add(V3.From(v.Point));
                    return;
                }
                Box box = body.RangeBox;
                foreach (double x in new[] { box.MinPoint.X, box.MaxPoint.X })
                    foreach (double y in new[] { box.MinPoint.Y, box.MaxPoint.Y })
                        foreach (double z in new[] { box.MinPoint.Z, box.MaxPoint.Z })
                            BodyPoints.Add(new V3(x, y, z));
            }
            catch
            {
                BodyPoints.Clear();
            }
        }

        /// <summary>Sheet thickness when the part is sheet metal, otherwise NaN.</summary>
        public double SheetThickness = double.NaN;

        /// <summary>True for a broad face of a sheet metal part, as opposed to one of its thin edge faces.</summary>
        public bool IsBroadSheetFace => !double.IsNaN(SheetThickness) && MinExtent > SheetThickness * 3;

        /// <summary>The smaller of the face's two extents; an edge face of a sheet is as thin as the sheet.</summary>
        public double MinExtent => IsTube && Strips.Count > 0 ? Strips[0].Thickness : Math.Min(Length, Thickness);

        private double _wallDepth = double.NaN;

        /// <summary>
        /// Distance from the face to the nearest parallel face behind it that lies under the face's centre: the wall
        /// thickness of a tube or the plate thickness of a profile. NaN when there is no such face (solid block).
        /// </summary>
        private double MeasureWall()
        {
            try
            {
                double cu = 0, cv = 0; int n = 0;
                foreach (Vertex v in Proxy.Vertices)
                {
                    V3 d = V3.From(v.Point) - Origin;
                    cu += d.Dot(U); cv += d.Dot(V); n++;
                }
                if (n == 0) return double.NaN;
                cu /= n; cv /= n;

                double best = double.NaN;
                foreach (Face f in Proxy.SurfaceBody.Faces)
                {
                    if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
                    V3 fn = V3.From(((Plane)f.Geometry).Normal);
                    if (f.IsParamReversed) fn = -fn;
                    if (fn.Unit().Dot(Normal) > -0.999) continue;
                    V3 fp = V3.From(f.Vertices[1].Point);
                    double dist = (Origin - fp).Dot(Normal);
                    if (dist < 1e-4 || (!double.IsNaN(best) && dist >= best)) continue;

                    double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
                    foreach (Vertex v in f.Vertices)
                    {
                        V3 d = V3.From(v.Point) - Origin;
                        u0 = Math.Min(u0, d.Dot(U)); u1 = Math.Max(u1, d.Dot(U));
                        v0 = Math.Min(v0, d.Dot(V)); v1 = Math.Max(v1, d.Dot(V));
                    }
                    if (cu >= u0 - 1e-4 && cu <= u1 + 1e-4 && cv >= v0 - 1e-4 && cv <= v1 + 1e-4) best = dist;
                }
                return best;
            }
            catch { return double.NaN; }
        }

        public double DepthIntoBody()
        {
            if (!double.IsNaN(SheetThickness)) return SheetThickness;
            if (double.IsNaN(_wallDepth)) _wallDepth = MeasureWall();
            if (!double.IsNaN(_wallDepth)) return _wallDepth;
            double faceD = Origin.Dot(Normal), min = faceD;
            foreach (V3 p in BodyPoints) min = Math.Min(min, p.Dot(Normal));
            return faceD - min;
        }

        /// <summary>Largest extent of the body along <paramref name="dir"/>, measured from the face plane.</summary>
        public double ExtentAlong(V3 dir)
        {
            double faceD = Origin.Dot(dir), max = double.MinValue;
            foreach (V3 p in BodyPoints) max = Math.Max(max, p.Dot(dir));
            return max == double.MinValue ? 0 : max - faceD;
        }
    }
}
