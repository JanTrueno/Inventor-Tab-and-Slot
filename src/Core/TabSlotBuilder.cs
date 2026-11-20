using System;
using System.Collections.Generic;
using Inventor;

namespace TabSlotAddIn.Core
{
    internal enum FeatureHealth { Ok, Suppressed, Missing, PartNotLoaded, Error }

    /// <summary>
    /// Creates, finds, suppresses and deletes the part features that make up a tab-and-slot set.
    /// The tab is an extruded join on the tab part, the slot an extruded cut on the slot part. Both features carry
    /// an attribute set (named after the set id) so they can be found again even if the user renames them.
    /// </summary>
    internal static class TabSlotBuilder
    {
        public const string AttrPrefix = "TabSlot.";
        public const string RoleTab = "Tab";
        public const string RoleSlot = "Slot";

        // ----- create ---------------------------------------------------------------------------------------------

        public static void Create(Inventor.Application app, AssemblyDocument asm, TabSlotParams p, FaceInfo tab, FaceInfo slot, Plan plan)
        {
            var tabQuads = new List<V3[]>();
            var slotQuads = new List<V3[]>();
            for (int i = 0; i < plan.Tabs.Count; i++)
            {
                tabQuads.Add(plan.Tabs[i].Corners());
                slotQuads.Add(plan.SlotFootprint(i).Corners());
            }

            // Sheet metal parts need sheet metal features (Face / Cut) so the flat pattern stays correct;
            // ordinary parts get a plain extruded join / cut.
            if (IsSheetMetal(tab)) CreateSheetTab(app, p, tab, plan);
            else
            {
                bool done = false;
                if (p.Chamfer)
                {
                    try { CreateSideProfileTabs(app, p, tab, plan); done = true; }
                    catch (Exception ex)
                    {
                        // fall back to plain rectangular tabs rather than failing the whole set
                        Log.Write("Chamfered tabs failed, using plain tabs: " + ex.Message);
                        DeleteSetFeatures(tab.PartDef, p.Id, RoleTab);
                    }
                }
                if (!done)
                    CreateExtrude(app, tab, tabQuads, plan.TabDirection, false, false,
                                  plan.TabHeight, p.Name + " - Tab", p.Id, RoleTab);
            }
            CreateTabReliefs(app, p, tab, plan);
            try
            {
                CreateSlot(app, p, slot, slotQuads, RoleSlot);
            }
            catch
            {
                DeleteSetFeatures(tab.PartDef, p.Id, RoleRelief);   // best effort rollback
                DeleteSetFeatures(tab.PartDef, p.Id, RoleTab);
                throw;
            }

            p.ExtraSlotFiles = "";
            if (p.AllCopies)
            {
                try { CreateSlotsForCopies(app, asm, p, tab, slot, slotQuads); }
                catch (Exception ex) { Log.Write("Slots for other copies failed: " + ex.Message); }
            }
        }

        private static void CreateSlot(Inventor.Application app, TabSlotParams p, FaceInfo slot, List<V3[]> quads, string role)
        {
            double relief = p.ReliefFor(slot.DepthIntoBody());
            if (IsSheetMetal(slot)) CreateSheetCut(app, p, slot, quads, relief, role);
            else CreateExtrude(app, slot, quads, -slot.Normal, true, false,
                               slot.DepthIntoBody() + 0.01,   // through the wall only, not the far wall of a tube
                               p.Name + " - Slot", p.Id, role, relief);
        }

        // ----- other copies of the tab part ----------------------------------------------------------------------

        /// <summary>Where one other copy of the tab part lands: how to move the picked tab there, and the slot face it meets.</summary>
        public sealed class CopySlot
        {
            public string Name;
            public Matrix ToTabPart;   // picked tab occurrence -> tab part space
            public Matrix Move;        // tab part space -> this copy
            public List<V3[]> Quads;   // the slot outlines, moved to this copy
            public FaceProxy Face;
        }

        /// <summary>
        /// Every other copy of the tab part that has a slot face under its tabs. Lazy, so when cutting, each face is
        /// looked up only after the previous slot is made. Copies whose spot is already cut (a shared slot part) are left out.
        /// </summary>
        public static IEnumerable<CopySlot> FindCopySlots(Inventor.Application app, AssemblyDocument asm, FaceInfo tab, FaceInfo slot, List<V3[]> slotQuads)
        {
            TransientGeometry tg = app.TransientGeometry;
            Matrix toTabPart = tab.Occurrence.Transformation.Copy();
            toTabPart.Invert();

            var occurrences = new List<ComponentOccurrence>();
            foreach (ComponentOccurrence o in asm.ComponentDefinition.Occurrences.AllLeafOccurrences) occurrences.Add(o);

            foreach (ComponentOccurrence copy in occurrences)
            {
                CopySlot found = null;
                try
                {
                    if (copy.Suppressed || !SameDocument(copy, tab.PartDoc)) continue;
                    Matrix move = copy.Transformation;
                    V3 MapPoint(V3 x)
                    {
                        Point pt = tg.CreatePoint(x.X, x.Y, x.Z);
                        pt.TransformBy(toTabPart); pt.TransformBy(move);
                        return V3.From(pt);
                    }
                    V3 MapVector(V3 x)
                    {
                        Vector v = tg.CreateVector(x.X, x.Y, x.Z);
                        v.TransformBy(toTabPart); v.TransformBy(move);
                        return V3.From(v);
                    }

                    var quads = new List<V3[]>();
                    V3 centre = default(V3);
                    foreach (V3[] q in slotQuads)
                    {
                        var moved = new V3[q.Length];
                        for (int i = 0; i < q.Length; i++) { moved[i] = MapPoint(q[i]); centre = centre + moved[i]; }
                        quads.Add(moved);
                    }
                    centre = centre * (1.0 / (slotQuads.Count * 4));
                    if ((MapPoint(slot.Origin) - slot.Origin).Length < 1e-4 && (MapVector(slot.Normal) - slot.Normal).Length < 1e-4)
                        continue;   // the picked copy itself

                    FaceProxy face = FindSlotFace(occurrences, tab.PartDoc, centre, MapVector(slot.Normal).Unit());
                    if (face != null) found = new CopySlot { Name = copy.Name, ToTabPart = toTabPart, Move = move, Quads = quads, Face = face };
                }
                catch (Exception ex) { Log.Write("Copy " + copy.Name + ": " + ex.Message); }
                if (found != null) yield return found;
            }
        }

        /// <summary>Cuts the slot for every other copy of the tab part, in whichever part each one lands.</summary>
        private static void CreateSlotsForCopies(Inventor.Application app, AssemblyDocument asm, TabSlotParams p,
                                                 FaceInfo tab, FaceInfo slot, List<V3[]> slotQuads)
        {
            var extraFiles = new List<string>();
            int made = 0;
            foreach (CopySlot c in FindCopySlots(app, asm, tab, slot, slotQuads))
            {
                try
                {
                    FaceInfo target = FaceInfo.FromSelection(c.Face, p.RunAlongShortEdge, out string error);
                    if (target == null) { Log.Write("Copy " + c.Name + ": " + error); continue; }

                    CreateSlot(app, p, target, c.Quads, RoleSlot + (++made + 1));
                    string file = target.PartDoc.FullDocumentName;
                    if (!string.Equals(file, p.SlotPartFile, StringComparison.OrdinalIgnoreCase) && !extraFiles.Contains(file)) extraFiles.Add(file);
                    Log.Write("Copy " + c.Name + ": slot cut in " + target.DisplayName);
                }
                catch (Exception ex) { Log.Write("Copy " + c.Name + ": " + ex.Message); }
            }
            p.ExtraSlotFiles = string.Join("|", extraFiles);
        }

        private static bool SameDocument(ComponentOccurrence occ, PartDocument doc)
        {
            return occ.Definition is PartComponentDefinition d &&
                   string.Equals(((Document)d.Document).FullDocumentName, doc.FullDocumentName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The planar face, on a part other than the tab part, that lies in the plane through <paramref name="centre"/>
        /// with normal <paramref name="normal"/> and has solid material right behind that point.
        /// </summary>
        private static FaceProxy FindSlotFace(List<ComponentOccurrence> occurrences, PartDocument tabDoc, V3 centre, V3 normal)
        {
            const double tol = 1e-3;   // cm
            foreach (ComponentOccurrence occ in occurrences)
            {
                try
                {
                    if (occ.Suppressed || !(occ.Definition is PartComponentDefinition) || SameDocument(occ, tabDoc)) continue;
                    Box box = occ.RangeBox;
                    if (centre.X < box.MinPoint.X - tol || centre.X > box.MaxPoint.X + tol ||
                        centre.Y < box.MinPoint.Y - tol || centre.Y > box.MaxPoint.Y + tol ||
                        centre.Z < box.MinPoint.Z - tol || centre.Z > box.MaxPoint.Z + tol) continue;

                    foreach (SurfaceBody body in occ.SurfaceBodies)
                    {
                        if (!SolidBehind(body, centre, normal)) continue;
                        foreach (Face f in body.Faces)
                        {
                            if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
                            V3 n = V3.From(((Plane)f.Geometry).Normal);
                            if (f.IsParamReversed) n = -n;
                            if (n.Unit().Dot(normal) < Plan.ParallelTolerance) continue;
                            if (Math.Abs((centre - V3.From(f.Vertices[1].Point)).Dot(normal)) > tol) continue;
                            if (FaceSpans(f, centre, normal)) return f as FaceProxy;
                        }
                    }
                }
                catch { /* occurrence without accessible geometry */ }
            }
            return null;
        }

        /// <summary>True when the body has material just behind the point and none just in front (so the point is on its skin).</summary>
        private static bool SolidBehind(SurfaceBody body, V3 centre, V3 normal)
        {
            try
            {
                TransientGeometry tg = body.Application is Inventor.Application a ? a.TransientGeometry : null;
                if (tg == null) return true;
                V3 behind = centre - normal * 0.005, front = centre + normal * 0.005;
                dynamic b = body;
                string inside = Convert.ToString(b.IsPointInside(tg.CreatePoint(behind.X, behind.Y, behind.Z)));
                string outside = Convert.ToString(b.IsPointInside(tg.CreatePoint(front.X, front.Y, front.Z)));
                return inside.Contains("Inside") && !outside.Contains("Inside");
            }
            catch { return true; }   // cannot tell: rely on the plane and extent checks
        }

        /// <summary>True when <paramref name="p"/> lies within the in-plane extent of the face's vertices.</summary>
        private static bool FaceSpans(Face f, V3 p, V3 normal)
        {
            V3 any = Math.Abs(normal.Z) < 0.9 ? new V3(0, 0, 1) : new V3(1, 0, 0);
            V3 u = normal.Cross(any).Unit(), v = normal.Cross(u).Unit();
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (Vertex vx in f.Vertices)
            {
                V3 d = V3.From(vx.Point) - p;
                u0 = Math.Min(u0, d.Dot(u)); u1 = Math.Max(u1, d.Dot(u));
                v0 = Math.Min(v0, d.Dot(v)); v1 = Math.Max(v1, d.Dot(v));
            }
            return u0 <= 1e-4 && u1 >= -1e-4 && v0 <= 1e-4 && v1 >= -1e-4;
        }

        private static void DeleteSetFeatures(PartComponentDefinition def, string id, string rolePrefix)
        {
            foreach (PartFeature f in FindFeatures(def, id, rolePrefix))
            {
                try { f.Delete(false, true, true); } catch { /* already gone */ }
            }
        }

        private const string SheetMetalSubType = "{9C464203-9BAE-11D3-8BAD-0060B0CE6BB4}";

        public static bool IsSheetMetal(FaceInfo f) => f.PartDoc.SubType == SheetMetalSubType;

        /// <summary>
        /// Draws the footprints as rectangles on a new sketch attached to <paramref name="face"/>. With a relief radius the
        /// outline gets a small circular relief at every corner; if Inventor refuses that outline, plain rectangles are drawn.
        /// </summary>
        private static PlanarSketch DrawSketch(Inventor.Application app, FaceInfo owner, Face face, List<V3[]> quads,
                                               out Matrix toPart, double relief = 0)
        {
            if (relief > 1e-6)
            {
                try { return BuildSketch(app, owner, face, quads, out toPart, relief); }
                catch { /* fall back to plain rectangles */ }
            }
            return BuildSketch(app, owner, face, quads, out toPart, 0);
        }

        private static double Dist(Point2d a, Point2d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>Rectangle with a circular relief centred on each corner: edges, then a 270 degree arc round each corner.</summary>
        private static void DrawRelievedRectangle(TransientGeometry tg, PlanarSketch sketch, Point2d[] c, double relief)
        {
            double r = Math.Min(relief, 0.45 * Math.Min(Dist(c[0], c[1]), Dist(c[1], c[2])));
            if (r <= 1e-6) { sketch.SketchLines.AddAsThreePointRectangle(c[0], c[1], c[2]); return; }

            // the outline's winding in sketch space decides which way the arcs run
            double area = 0;
            for (int i = 0; i < 4; i++) { Point2d a = c[i], b = c[(i + 1) % 4]; area += a.X * b.Y - b.X * a.Y; }
            bool counterClockwise = area > 0;

            Point2d Toward(Point2d from, Point2d to)
            {
                double d = Dist(from, to);
                return tg.CreatePoint2d(from.X + (to.X - from.X) / d * r, from.Y + (to.Y - from.Y) / d * r);
            }

            var p = new SketchPoint[4];   // start of the arc at each corner (on the incoming edge)
            var q = new SketchPoint[4];   // end of the arc (on the outgoing edge)
            for (int i = 0; i < 4; i++)
            {
                p[i] = sketch.SketchPoints.Add(Toward(c[i], c[(i + 3) % 4]), false);
                q[i] = sketch.SketchPoints.Add(Toward(c[i], c[(i + 1) % 4]), false);
            }
            for (int i = 0; i < 4; i++)
            {
                SketchPoint centre = sketch.SketchPoints.Add(c[i], false);
                if (counterClockwise) sketch.SketchArcs.AddByCenterStartEndPoint(centre, p[i], q[i]);
                else sketch.SketchArcs.AddByCenterStartEndPoint(centre, q[i], p[i]);
                sketch.SketchLines.AddByTwoPoints(q[i], p[(i + 1) % 4]);
            }
        }

        private static PlanarSketch BuildSketch(Inventor.Application app, FaceInfo owner, Face face, List<V3[]> quads,
                                                out Matrix toPart, double relief)
        {
            TransientGeometry tg = app.TransientGeometry;
            Matrix inverse = owner.Occurrence.Transformation.Copy();
            inverse.Invert();
            toPart = inverse;

            PlanarSketch sketch = owner.PartDef.Sketches.Add(face, false);
            try
            {
                foreach (V3[] q in quads)
                {
                    Point2d[] pts = new Point2d[q.Length];
                    for (int i = 0; i < q.Length; i++)
                    {
                        Point pt = tg.CreatePoint(q[i].X, q[i].Y, q[i].Z);
                        pt.TransformBy(inverse);
                        pts[i] = sketch.ModelToSketchSpace(pt);
                    }
                    if (q.Length != 4) DrawPolygon(sketch, pts);                       // e.g. a chamfered tab outline
                    else if (relief > 1e-6) DrawRelievedRectangle(tg, sketch, pts, relief);
                    else sketch.SketchLines.AddAsThreePointRectangle(pts[0], pts[1], pts[2]);
                }
                return sketch;
            }
            catch
            {
                try { sketch.Delete(); } catch { }
                throw;
            }
        }

        private static PartFeatureExtentDirectionEnum SideOf(Inventor.Application app, PlanarSketch sketch, Matrix toPart, V3 wanted)
        {
            Vector dir = app.TransientGeometry.CreateVector(wanted.X, wanted.Y, wanted.Z);
            dir.TransformBy(toPart);
            V3 normal = V3.From(sketch.PlanarEntityGeometry.Normal);
            return V3.From(dir).Dot(normal) >= 0
                ? PartFeatureExtentDirectionEnum.kPositiveExtentDirection
                : PartFeatureExtentDirectionEnum.kNegativeExtentDirection;
        }

        private static void Label(object feature, string name, string id, string role)
        {
            dynamic f = feature;
            TryName(n => f.Name = n, name);
            AttributeSet set = f.AttributeSets.Add(AttrPrefix + id, false);
            set.Add("Role", ValueTypeEnum.kStringType, role);
        }

        /// <summary>The planar sheet face that shares a long edge with the picked end face.</summary>
        private static FaceProxy FindAdjacentSheetFace(FaceInfo end)
        {
            foreach (Edge e in end.Proxy.Edges)
            {
                if (e.GeometryType != CurveTypeEnum.kLineSegmentCurve) continue;
                V3 d = (V3.From(e.StopVertex.Point) - V3.From(e.StartVertex.Point)).Unit();
                if (Math.Abs(d.Dot(end.U)) < 0.999) continue;
                foreach (Face f in e.Faces)
                {
                    var fp = f as FaceProxy;
                    if (fp != null && fp.TransientKey != end.Proxy.TransientKey && fp.SurfaceType == SurfaceTypeEnum.kPlaneSurface)
                        return fp;
                }
            }
            return null;
        }

        /// <summary>
        /// Sheet metal tab: a Face feature sketched on the sheet face next to the picked edge, reaching out from that edge.
        /// The face feature takes the sheet thickness itself, so the flat pattern follows the tab.
        /// </summary>
        private static void CreateSheetTab(Inventor.Application app, TabSlotParams p, FaceInfo tab, Plan plan)
        {
            FaceProxy sheet = FindAdjacentSheetFace(tab);
            if (sheet == null) throw new InvalidOperationException("Could not find the sheet face next to the selected edge face.");

            var plane = (Plane)sheet.Geometry;
            V3 nb = V3.From(plane.Normal);
            if (sheet.IsParamReversed) nb = -nb;
            nb = nb.Unit();
            double vb = (V3.From(plane.RootPoint) - tab.Origin).Dot(nb) / tab.V.Dot(nb);

            var quads = new List<V3[]>();
            foreach (Footprint fp in plan.Tabs)
            {
                V3 a = fp.Origin + tab.V * (vb - (fp.Origin - tab.Origin).Dot(tab.V));
                V3 b = a + fp.U * fp.Length;
                quads.Add(TabOutline(a, b, plan.TabDirection, plan.TabHeight, p.Chamfer ? p.ChamferSize : 0));
            }

            var def = (SheetMetalComponentDefinition)tab.PartDef;
            string error = "the face feature was not healthy";
            // The sheet material lies behind the sheet face; if Inventor reads the direction the other way, retry flipped.
            foreach (bool flip in new[] { false, true })
            {
                PlanarSketch sketch = DrawSketch(app, tab, (Face)sheet.NativeObject, quads, out Matrix toPart);
                FaceFeature feature = null;
                try
                {
                    Profile profile = sketch.Profiles.AddForSolid(true);
                    FaceFeatureDefinition fd = ((SheetMetalFeatures)def.Features).FaceFeatures.CreateFaceFeatureDefinition(profile);
                    fd.Direction = SideOf(app, sketch, toPart, flip ? nb : -nb);
                    feature = ((SheetMetalFeatures)def.Features).FaceFeatures.Add(fd);
                    if (feature.HealthStatus == HealthStatusEnum.kUpToDateHealth)
                    {
                        Label(feature, p.Name + " - Tab", p.Id, RoleTab);
                        try { sketch.Name = p.Name + " - Tab Sketch"; } catch { }
                        return;
                    }
                }
                catch (Exception ex) { error = ex.Message; }

                try { if (feature != null) feature.Delete(false, true, true); } catch { }
                try { sketch.Delete(); } catch { }
            }
            throw new InvalidOperationException("Could not create the sheet metal tab: " + error);
        }

        /// <summary>Sheet metal slot: a Cut feature through the sheet, sketched on the picked sheet face.</summary>
        private static void CreateSheetCut(Inventor.Application app, TabSlotParams p, FaceInfo slot, List<V3[]> quads, double relief, string role)
        {
            var def = (SheetMetalComponentDefinition)slot.PartDef;
            PlanarSketch sketch = DrawSketch(app, slot, slot.Native, quads, out Matrix toPart, relief);
            try
            {
                Profile profile = sketch.Profiles.AddForSolid(true);
                CutDefinition cd = ((SheetMetalFeatures)def.Features).CutFeatures.CreateCutDefinition(profile);
                // Symmetric, twice the sheet thickness: removes the sheet whichever way the sketch normal points,
                // without reaching the opposite flange of a channel or box.
                cd.SetDistanceExtent(2 * slot.DepthIntoBody() + 0.02, PartFeatureExtentDirectionEnum.kSymmetricExtentDirection);
                CutFeature feature = ((SheetMetalFeatures)def.Features).CutFeatures.Add(cd);
                Label(feature, p.Name + " - Slot", p.Id, role);
                try { sketch.Name = p.Name + " - Slot Sketch"; } catch { }
            }
            catch
            {
                try { sketch.Delete(); } catch { }
                throw;
            }
        }

        private static ExtrudeFeature CreateExtrude(Inventor.Application app, FaceInfo owner, List<V3[]> quads, V3 direction,
                                                    bool cut, bool throughAll, double distance, string name, string id, string role, double relief = 0)
        {
            TransientGeometry tg = app.TransientGeometry;
            PartComponentDefinition def = owner.PartDef;

            Matrix toPart = owner.Occurrence.Transformation.Copy();
            toPart.Invert();

            PlanarSketch sketch = DrawSketch(app, owner, owner.Native, quads, out Matrix _, relief);
            try
            {
                Profile profile = sketch.Profiles.AddForSolid(true);
                ExtrudeFeatures extrudes = def.Features.ExtrudeFeatures;
                ExtrudeDefinition ed = extrudes.CreateExtrudeDefinition(
                    profile, cut ? PartFeatureOperationEnum.kCutOperation : PartFeatureOperationEnum.kJoinOperation);

                // Extrude direction is relative to the sketch normal, so work out which way the wanted direction lies.
                Vector dir = tg.CreateVector(direction.X, direction.Y, direction.Z);
                dir.TransformBy(toPart);
                V3 sketchNormal = V3.From(sketch.PlanarEntityGeometry.Normal);
                PartFeatureExtentDirectionEnum extentDir = V3.From(dir).Dot(sketchNormal) >= 0
                    ? PartFeatureExtentDirectionEnum.kPositiveExtentDirection
                    : PartFeatureExtentDirectionEnum.kNegativeExtentDirection;

                if (throughAll) ed.SetThroughAllExtent(extentDir);
                else ed.SetDistanceExtent(distance, extentDir);

                ExtrudeFeature feature = extrudes.Add(ed);
                TryName(n => feature.Name = n, name);
                try { sketch.Name = feature.Name + " Sketch"; } catch { /* cosmetic */ }

                AttributeSet set = feature.AttributeSets.Add(AttrPrefix + id, false);
                set.Add("Role", ValueTypeEnum.kStringType, role);
                return feature;
            }
            catch
            {
                try { sketch.Delete(); } catch { /* sketch may already be consumed */ }
                throw;
            }
        }

        private static void TryName(Action<string> setName, string baseName)
        {
            for (int n = 0; n < 50; n++)
            {
                string candidate = n == 0 ? baseName : baseName + " (" + (n + 1) + ")";
                try { setName(candidate); return; }
                catch { /* name taken, try the next one */ }
            }
        }

        // ----- find / health --------------------------------------------------------------------------------------

        public static Document FindDocument(AssemblyDocument asm, string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            foreach (Document d in asm.AllReferencedDocuments)
                if (string.Equals(d.FullDocumentName, fullName, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        public static PartFeature FindFeature(PartComponentDefinition def, string id, string role)
        {
            string setName = AttrPrefix + id;
            foreach (PartFeature f in def.Features)
            {
                try
                {
                    if (!f.AttributeSets.NameIsUsed[setName]) continue;
                    AttributeSet set = f.AttributeSets[setName];
                    if (set.NameIsUsed["Role"] && Convert.ToString(set["Role"].Value) == role) return f;
                }
                catch { /* feature without attribute access, skip */ }
            }
            return null;
        }

        /// <summary>All features of this set whose role starts with <paramref name="rolePrefix"/> (a tube has one tab feature per wall).</summary>
        public static List<PartFeature> FindFeatures(PartComponentDefinition def, string id, string rolePrefix)
        {
            var result = new List<PartFeature>();
            string setName = AttrPrefix + id;
            foreach (PartFeature f in def.Features)
            {
                try
                {
                    if (!f.AttributeSets.NameIsUsed[setName]) continue;
                    AttributeSet set = f.AttributeSets[setName];
                    if (set.NameIsUsed["Role"] && Convert.ToString(set["Role"].Value).StartsWith(rolePrefix, StringComparison.Ordinal)) result.Add(f);
                }
                catch { /* feature without attribute access, skip */ }
            }
            return result;
        }

        public static FeatureHealth GetHealth(AssemblyDocument asm, TabSlotParams p)
        {
            var tabDoc = FindDocument(asm, p.TabPartFile) as PartDocument;
            var slotDoc = FindDocument(asm, p.SlotPartFile) as PartDocument;
            if (tabDoc == null || slotDoc == null) return FeatureHealth.PartNotLoaded;

            List<PartFeature> tabs = FindFeatures(tabDoc.ComponentDefinition, p.Id, RoleTab);
            List<PartFeature> slots = FindFeatures(slotDoc.ComponentDefinition, p.Id, RoleSlot);
            if (tabs.Count == 0 || slots.Count == 0) return FeatureHealth.Missing;

            var all = new List<PartFeature>(tabs);
            all.AddRange(slots);
            foreach (string file in p.SlotFiles())
                if (file != p.SlotPartFile && FindDocument(asm, file) is PartDocument extra)
                    all.AddRange(FindFeatures(extra.ComponentDefinition, p.Id, RoleSlot));
            all.AddRange(FindFeatures(tabDoc.ComponentDefinition, p.Id, RoleRelief));
            foreach (PartFeature f in all) if (f.Suppressed) return FeatureHealth.Suppressed;
            try
            {
                foreach (PartFeature f in all) if (f.HealthStatus != HealthStatusEnum.kUpToDateHealth) return FeatureHealth.Error;
            }
            catch { /* health not available */ }
            return FeatureHealth.Ok;
        }

        // ----- suppress / delete ----------------------------------------------------------------------------------

        /// <summary>Every feature of a set, in the order they must be removed: tab-part reliefs, tab features, slot features.</summary>
        public static List<KeyValuePair<PartDocument, PartFeature>> AllFeatures(AssemblyDocument asm, TabSlotParams p)
        {
            var list = new List<KeyValuePair<PartDocument, PartFeature>>();
            if (FindDocument(asm, p.TabPartFile) is PartDocument tabDoc)
            {
                foreach (PartFeature f in FindFeatures(tabDoc.ComponentDefinition, p.Id, RoleRelief)) list.Add(new KeyValuePair<PartDocument, PartFeature>(tabDoc, f));
                foreach (PartFeature f in FindFeatures(tabDoc.ComponentDefinition, p.Id, RoleTab)) list.Add(new KeyValuePair<PartDocument, PartFeature>(tabDoc, f));
            }
            foreach (string file in p.SlotFiles())
            {
                if (FindDocument(asm, file) is PartDocument slotDoc)
                    foreach (PartFeature f in FindFeatures(slotDoc.ComponentDefinition, p.Id, RoleSlot)) list.Add(new KeyValuePair<PartDocument, PartFeature>(slotDoc, f));
            }
            return list;
        }

        /// <summary>Suppresses (or restores) all features of the set. Used while editing so the original faces are whole again.</summary>
        public static void SetSuppressed(AssemblyDocument asm, TabSlotParams p, bool suppressed)
        {
            foreach (var entry in AllFeatures(asm, p))
            {
                PartFeature f = entry.Value;
                if (f.Suppressed == suppressed) continue;
                f.Suppressed = suppressed;
                entry.Key.Update();
            }
            asm.Update();
        }

        public static void DeleteFeatures(AssemblyDocument asm, TabSlotParams p)
        {
            foreach (var entry in AllFeatures(asm, p))
            {
                try { entry.Value.Delete(false, true, true); }
                catch (Exception ex) { Log.Write("Delete feature failed: " + ex.Message); }
                entry.Key.Update();
            }
            asm.Update();
        }

        public static void RenameFeatures(AssemblyDocument asm, TabSlotParams p)
        {
            foreach (var (file, role) in new[] { (p.TabPartFile, RoleTab), (p.SlotPartFile, RoleSlot) })
            {
                var doc = FindDocument(asm, file) as PartDocument;
                if (doc == null) continue;
                PartFeature f = FindFeature(doc.ComponentDefinition, p.Id, role);
                if (f != null) TryName(n => f.Name = n, p.Name + " - " + role);
            }
        }

        // ----- tab outline, side-profile tabs and relief notches --------------------------------------------------

        public const string RoleRelief = "Relief";

        /// <summary>Tab outline in the plane of a side face: root a-b, rising along <paramref name="up"/>; tip corners chamfered if asked.</summary>
        private static V3[] TabOutline(V3 a, V3 b, V3 up, double height, double chamfer)
        {
            V3 along = (b - a).Unit();
            double length = (b - a).Length;
            double c = Math.Min(chamfer, Math.Min(height, length / 2) * 0.9);
            if (c <= 1e-6)
                return new[] { a, b, b + up * height, a + up * height };
            return new[]
            {
                a, b,
                b + up * (height - c),
                b - along * c + up * height,
                a + along * c + up * height,
                a + up * (height - c)
            };
        }

        private static void DrawPolygon(PlanarSketch sketch, Point2d[] pts)
        {
            var points = new SketchPoint[pts.Length];
            for (int i = 0; i < pts.Length; i++) points[i] = sketch.SketchPoints.Add(pts[i], false);
            for (int i = 0; i < pts.Length; i++) sketch.SketchLines.AddByTwoPoints(points[i], points[(i + 1) % pts.Length]);
        }

        /// <summary>The planar face across the strip's outer edge: the broad face of a sheet, or the outer wall of a tube.</summary>
        private static FaceProxy FindFaceBesideStrip(FaceInfo tab, FaceInfo.Strip strip)
        {
            FaceProxy found = null;
            try { found = FindOuterPlaneFace(tab, strip); }
            catch (Exception ex) { Log.Write("Outer face search failed: " + ex.Message); }
            if (found == null) found = FindFaceBesideStripByEdges(tab, strip);
            if (found == null) Log.Write($"No face beside strip: origin=({strip.Origin.X:0.###},{strip.Origin.Y:0.###},{strip.Origin.Z:0.###}) U=({strip.U.X:0.##},{strip.U.Y:0.##},{strip.U.Z:0.##}) V=({strip.V.X:0.##},{strip.V.Y:0.##},{strip.V.Z:0.##})");
            return found;
        }

        /// <summary>
        /// Looks through the part's own flat faces for the one lying in the plane of the strip's outer edge and facing away from
        /// the material (the broad face of a sheet, or the outer wall of a tube), which also reaches the strip.
        /// </summary>
        private static FaceProxy FindOuterPlaneFace(FaceInfo tab, FaceInfo.Strip strip)
        {
            V3 outward = -strip.V;               // V points from the outer edge into the material
            FaceProxy best = null;
            double bestArea = -1;
            foreach (Face f in tab.Proxy.SurfaceBody.Faces)
            {
                var fp = f as FaceProxy;
                if (fp == null || fp.TransientKey == tab.Proxy.TransientKey || fp.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
                var plane = (Plane)fp.Geometry;
                V3 n = V3.From(plane.Normal);
                if (fp.IsParamReversed) n = -n;
                if (n.Unit().Dot(outward) < 0.999) continue;                                   // not facing outward
                if (Math.Abs((strip.Origin - V3.From(plane.RootPoint)).Dot(outward)) > 0.01) continue;   // not in the outer plane
                Box box = fp.Evaluator.RangeBox;
                const double slack = 0.05;
                V3 o = strip.Origin;
                if (o.X < box.MinPoint.X - slack || o.X > box.MaxPoint.X + slack ||
                    o.Y < box.MinPoint.Y - slack || o.Y > box.MaxPoint.Y + slack ||
                    o.Z < box.MinPoint.Z - slack || o.Z > box.MaxPoint.Z + slack) continue;    // does not reach the strip
                double area = fp.Evaluator.Area;
                if (area > bestArea) { bestArea = area; best = fp; }
            }
            return best;
        }

        private static FaceProxy FindFaceBesideStripByEdges(FaceInfo tab, FaceInfo.Strip strip)
        {
            foreach (Edge e in tab.Proxy.Edges)
            {
                if (e.GeometryType != CurveTypeEnum.kLineSegmentCurve) continue;
                V3 a = V3.From(e.StartVertex.Point), b = V3.From(e.StopVertex.Point);
                if (Math.Abs((b - a).Unit().Dot(strip.U)) < 0.999) continue;
                V3 rel = a - strip.Origin;
                if ((rel - strip.U * rel.Dot(strip.U)).Length > 0.01) continue;      // not on this strip's outer edge line
                foreach (Face f in e.Faces)
                {
                    var fp = f as FaceProxy;
                    if (fp != null && fp.TransientKey != tab.Proxy.TransientKey && fp.SurfaceType == SurfaceTypeEnum.kPlaneSurface) return fp;
                }
            }
            return null;
        }

        private static V3 OutwardNormal(FaceProxy f)
        {
            V3 n = V3.From(((Plane)f.Geometry).Normal);
            return (f.IsParamReversed ? -n : n).Unit();
        }

        /// <summary>
        /// Tabs with chamfered tips on an ordinary part: the side profile of each tab is sketched on the face beside the strip
        /// and extruded through the wall thickness, one feature per strip (a tube wall each).
        /// </summary>
        private static void CreateSideProfileTabs(Inventor.Application app, TabSlotParams p, FaceInfo tab, Plan plan)
        {
            TransientGeometry tg = app.TransientGeometry;
            for (int s = 0; s < tab.Strips.Count; s++)
            {
                FaceInfo.Strip strip = tab.Strips[s];
                var outlines = new List<V3[]>();
                foreach (Footprint f in plan.Tabs)
                {
                    if (f.StripIndex != s) continue;
                    if (f.Width < strip.Thickness - 1e-6) throw new InvalidOperationException("Chamfered tabs need the full wall width.");
                    // start 1 mm inside the wall so the new material overlaps the part and joins with it (not just touches an edge)
                    const double overlap = 0.1;
                    V3 back = plan.TabDirection * overlap;
                    outlines.Add(TabOutline(f.Origin - back, f.Origin + f.U * f.Length - back, plan.TabDirection, plan.TabHeight + overlap, p.ChamferSize));
                }
                if (outlines.Count == 0) continue;

                FaceProxy side = FindFaceBesideStrip(tab, strip);
                if (side == null) throw new InvalidOperationException("Could not find the face beside the tab edge.");
                V3 into = -OutwardNormal(side);

                PlanarSketch sketch = DrawSketch(app, tab, (Face)side.NativeObject, outlines, out Matrix toPart);
                try
                {
                    Profile profile = sketch.Profiles.AddForSolid(true);
                    ExtrudeFeatures extrudes = tab.PartDef.Features.ExtrudeFeatures;
                    ExtrudeDefinition ed = extrudes.CreateExtrudeDefinition(profile, PartFeatureOperationEnum.kJoinOperation);
                    ed.SetDistanceExtent(strip.Thickness, SideOf(app, sketch, toPart, into));
                    ExtrudeFeature feature = extrudes.Add(ed);
                    Label(feature, p.Name + " - Tab" + (s == 0 ? "" : " " + (s + 1)), p.Id, RoleTab + (s == 0 ? "" : s.ToString()));
                    try { sketch.Name = p.Name + " - Tab Sketch" + (s == 0 ? "" : " " + (s + 1)); } catch { }
                }
                catch
                {
                    try { sketch.Delete(); } catch { }
                    throw;
                }
            }
        }

        /// <summary>Small circular notches in the tab part at both root corners of every tab, cut through the wall or sheet.</summary>
        private static void CreateTabReliefs(Inventor.Application app, TabSlotParams p, FaceInfo tab, Plan plan)
        {
            if (!p.Relief) return;
            for (int s = 0; s < tab.Strips.Count; s++)
            {
                try
                {
                    FaceInfo.Strip strip = tab.Strips[s];
                    double radius = p.ReliefFor(strip.Thickness);
                    if (radius <= 1e-6) continue;
                    var centres = new List<V3>();
                    foreach (Footprint f in plan.Tabs)
                    {
                        if (f.StripIndex != s || f.Width < strip.Thickness - 1e-6) continue;   // narrower tabs have no root corners on the outer side
                        centres.Add(f.Origin);
                        centres.Add(f.Origin + f.U * f.Length);
                    }
                    if (centres.Count == 0) continue;
                    FaceProxy side = FindFaceBesideStrip(tab, strip);
                    if (side == null) continue;
                    CreateCircleCut(app, p, tab, side, centres, radius, -OutwardNormal(side), strip.Thickness, RoleRelief + s);
                }
                catch (Exception ex) { Log.Write("Tab relief failed: " + ex.Message); }
            }
        }

        private static void CreateCircleCut(Inventor.Application app, TabSlotParams p, FaceInfo owner, FaceProxy sketchFace,
                                            List<V3> centres, double radius, V3 into, double depth, string role)
        {
            TransientGeometry tg = app.TransientGeometry;
            Matrix inverse = owner.Occurrence.Transformation.Copy();
            inverse.Invert();

            PlanarSketch sketch = owner.PartDef.Sketches.Add((Face)sketchFace.NativeObject, false);
            try
            {
                foreach (V3 c in centres)
                {
                    Point pt = tg.CreatePoint(c.X, c.Y, c.Z);
                    pt.TransformBy(inverse);
                    sketch.SketchCircles.AddByCenterRadius(sketch.ModelToSketchSpace(pt), radius);
                }
                Profile profile = sketch.Profiles.AddForSolid(true);
                string name = p.Name + " - Relief";
                if (IsSheetMetal(owner))
                {
                    var def = (SheetMetalComponentDefinition)owner.PartDef;
                    CutDefinition cd = ((SheetMetalFeatures)def.Features).CutFeatures.CreateCutDefinition(profile);
                    cd.SetDistanceExtent(2 * depth + 0.02, PartFeatureExtentDirectionEnum.kSymmetricExtentDirection);
                    Label(((SheetMetalFeatures)def.Features).CutFeatures.Add(cd), name, p.Id, role);
                }
                else
                {
                    ExtrudeFeatures extrudes = owner.PartDef.Features.ExtrudeFeatures;
                    ExtrudeDefinition ed = extrudes.CreateExtrudeDefinition(profile, PartFeatureOperationEnum.kCutOperation);
                    ed.SetDistanceExtent(depth, SideOf(app, sketch, inverse, into));
                    Label(extrudes.Add(ed), name, p.Id, role);
                }
            }
            catch
            {
                try { sketch.Delete(); } catch { }
                throw;
            }
        }
    }
}
