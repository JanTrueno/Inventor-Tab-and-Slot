using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Inventor;
using TabSlotAddIn.Core;

// Drives the core (geometry + feature creation + storage) against a running Inventor using scratch documents only.
internal static class CoreTests
{
    private static int _failed;

    private static void Check(string what, bool ok, string detail = "")
    {
        detail = detail ?? "";
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
        if (!ok) _failed++;
    }

    private static bool Near(double a, double b, double tol = 1e-4) => Math.Abs(a - b) <= tol;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = (Inventor.Application)Marshal.GetActiveObject("Inventor.Application");
        if (args.Length > 0 && args[0] == "scene") return BuildScene(app);
        if (args.Length > 0 && args[0] == "cleanup") return Cleanup(app);
        if (args.Length > 1 && args[0] == "probe") return Probe(app, args[1]);
        if (args.Length > 2 && args[0] == "probe2") return Probe2(app, args[1], args[2]);
        if (args.Length > 2 && args[0] == "probe3") return Probe3(app, args[1], args[2]);
        if (args.Length > 0 && args[0] == "tube") return TubeScene(app);
        if (args.Length > 2 && args[0] == "icons") return Icons(app, args[1], args.Skip(2).ToArray());
        if (args.Length > 1 && args[0] == "edges") return Probe5(app, args[1]);
        if (args.Length > 2 && args[0] == "ray") return Probe4(app, double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TabSlotTest_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(dir);
        Console.WriteLine("Scratch dir: " + dir);

        AssemblyDocument asm = null;
        PartDocument partA = null, partB = null;
        try
        {
            TransientGeometry tg = app.TransientGeometry;

            // Part A: vertical plate 50 x 3 mm, 40 mm tall (top face = 50x3 = tab face)
            partA = MakePlate(app, System.IO.Path.Combine(dir, "PlateA.ipt"), 5.0, 0.3, 4.0);
            // Part B: horizontal plate 100 x 100 x 3 mm
            partB = MakePlate(app, System.IO.Path.Combine(dir, "PlateB.ipt"), 10.0, 10.0, 0.3);

            asm = (AssemblyDocument)app.Documents.Add(DocumentTypeEnum.kAssemblyDocumentObject, "", true);
            asm.SaveAs(System.IO.Path.Combine(dir, "Test.iam"), false);
            AssemblyComponentDefinition acd = asm.ComponentDefinition;

            ComponentOccurrence occA = acd.Occurrences.Add(partA.FullFileName, tg.CreateMatrix());
            Matrix mB = tg.CreateMatrix();
            mB.SetTranslation(tg.CreateVector(-2.5, -4.85, 4.0)); // B sits on top of A, tab footprint well inside B
            ComponentOccurrence occB = acd.Occurrences.Add(partB.FullFileName, mB);
            asm.Update();

            double volA0 = Volume(partA), volB0 = Volume(partB);
            Console.WriteLine($"Initial volumes  A={volA0:0.####}  B={volB0:0.####}  (cm3)");

            FaceProxy tabFace = FindFace(occA, new V3(0, 0, 1), 4.0);
            FaceProxy slotFace = FindFace(occB, new V3(0, 0, -1), -4.0); // underside of B, world z = 4.0
            Check("found tab face on A", tabFace != null);
            Check("found slot face on B", slotFace != null);
            if (tabFace == null || slotFace == null) return 2;

            // ---- geometry --------------------------------------------------------------------------------------
            string err;
            FaceInfo tab = FaceInfo.FromSelection(tabFace, false, out err);
            FaceInfo slot = FaceInfo.FromSelection(slotFace, false, out err);
            Check("tab FaceInfo", tab != null, err);
            Check("slot FaceInfo", slot != null, err);
            Check("tab normal is +Z", Near(tab.Normal.Z, 1.0), $"{tab.Normal.X:0.##},{tab.Normal.Y:0.##},{tab.Normal.Z:0.##}");
            Check("slot normal is -Z", Near(slot.Normal.Z, -1.0));
            Check("tab face length 5 cm", Near(tab.Length, 5.0), tab.Length.ToString("0.####"));
            Check("tab face thickness 0.3 cm", Near(tab.Thickness, 0.3), tab.Thickness.ToString("0.####"));
            Check("slot depth into body = 0.3", Near(slot.DepthIntoBody(), 0.3), slot.DepthIntoBody().ToString("0.####"));

            // ---- layout maths -----------------------------------------------------------------------------------
            var p = new TabSlotParams { Name = "Test Set", TabLength = 1.0, Count = 3, Clearance = 0.01, Spacing = SpacingMode.Equal };
            Plan plan = Plan.Create(p, tab, slot);
            Check("plan valid", plan.IsValid, plan.Error);
            Check("plan has 3 tabs", plan.Tabs.Count == 3);
            Check("flush height = slot thickness 0.3", Near(plan.TabHeight, 0.3), plan.TabHeight.ToString("0.####"));

            p.Count = 6; p.TabLength = 1.0;
            string layoutError;
            Plan.Layout(p, 5.0, out layoutError);
            Check("layout rejects tabs that do not fit", layoutError != null);
            p.Count = 3;

            // ---- create -----------------------------------------------------------------------------------------
            p.TabPartFile = tab.PartDoc.FullDocumentName;
            p.SlotPartFile = slot.PartDoc.FullDocumentName;
            TabSlotStore.CaptureKeys(asm, p, tab, slot);
            TabSlotBuilder.Create(app, p, tab, slot, plan);
            TabSlotStore.Save(asm, p);
            asm.Update();

            double tabVol = 3 * 1.0 * 0.3 * 0.3;
            double slotVol = 3 * (1.0 + 0.02) * (0.3 + 0.02) * 0.3;
            double volA1 = Volume(partA), volB1 = Volume(partB);
            Check("A gained 3 tabs of volume", Near(volA1 - volA0, tabVol, 1e-3), $"delta {volA1 - volA0:0.####} expected {tabVol:0.####}");
            Check("B lost 3 slots of volume", Near(volB0 - volB1, slotVol, 1e-3), $"delta {volB0 - volB1:0.####} expected {slotVol:0.####}");
            Check("feature health OK", TabSlotBuilder.GetHealth(asm, p) == FeatureHealth.Ok, TabSlotBuilder.GetHealth(asm, p).ToString());

            // ---- store ------------------------------------------------------------------------------------------
            var loaded = TabSlotStore.LoadAll(asm);
            Check("store round trip", loaded.Count == 1 && loaded[0].Id == p.Id && loaded[0].Count == 3 && Near(loaded[0].Clearance, 0.01));

            // ---- edit: suppress, re-bind faces, rebuild --------------------------------------------------------
            TabSlotBuilder.SetSuppressed(asm, p, true);
            Check("suppress reverts A", Near(Volume(partA), volA0, 1e-3));
            Check("suppress reverts B", Near(Volume(partB), volB0, 1e-3));
            FaceProxy tab2 = TabSlotStore.ResolveFace(asm, loaded[0], loaded[0].TabFaceKey);
            FaceProxy slot2 = TabSlotStore.ResolveFace(asm, loaded[0], loaded[0].SlotFaceKey);
            Check("re-bound tab face via reference key", tab2 != null);
            Check("re-bound slot face via reference key", slot2 != null);

            TabSlotBuilder.DeleteFeatures(asm, p);
            Check("feature lookup after delete is empty", TabSlotBuilder.FindFeature(tab.PartDef, p.Id, "Tab") == null);

            if (tab2 != null && slot2 != null)
            {
                FaceInfo t2 = FaceInfo.FromSelection(tab2, false, out err);
                FaceInfo s2 = FaceInfo.FromSelection(slot2, false, out err);
                p.Count = 2; p.TabLength = 1.5; p.Clearance = 0;
                Plan plan2 = Plan.Create(p, t2, s2);
                Check("edited plan valid", plan2.IsValid, plan2.Error);
                TabSlotBuilder.Create(app, p, t2, s2, plan2);
                asm.Update();
                Check("edited A volume", Near(Volume(partA) - volA0, 2 * 1.5 * 0.3 * 0.3, 1e-3), $"{Volume(partA) - volA0:0.####}");
                Check("edited B volume", Near(volB0 - Volume(partB), 2 * 1.5 * 0.3 * 0.3, 1e-3), $"{volB0 - Volume(partB):0.####}");
            }

            // ---- delete -----------------------------------------------------------------------------------------
            TabSlotBuilder.DeleteFeatures(asm, p);
            TabSlotStore.Remove(asm, p.Id);
            Check("delete restores A", Near(Volume(partA), volA0, 1e-3));
            Check("delete restores B", Near(Volume(partB), volB0, 1e-3));
            Check("store empty after remove", TabSlotStore.LoadAll(asm).Count == 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            _failed++;
        }
        finally
        {
            try { asm?.Close(true); } catch { }
            try { partA?.Close(true); } catch { }
            try { partB?.Close(true); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(_failed == 0 ? "ALL PASSED" : _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Leaves a visible scratch assembly open for interactive UI checks: plate A stands below plate B with a 10 mm gap,
    /// seen from a shallow angle so A's top (tab) face is visible. Prints the screen position of both faces.
    /// </summary>
    private static int BuildScene(Inventor.Application app)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TabSlotScene");
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        TransientGeometry tg = app.TransientGeometry;

        PartDocument partA = MakePlate(app, System.IO.Path.Combine(dir, "PlateA.ipt"), 5.0, 4.0, 0.3);
        PartDocument partB = MakePlate(app, System.IO.Path.Combine(dir, "PlateB.ipt"), 0.3, 8.0, 5.0);
        var asm = (AssemblyDocument)app.Documents.Add(DocumentTypeEnum.kAssemblyDocumentObject, "", true);
        asm.SaveAs(System.IO.Path.Combine(dir, "Scene.iam"), false);
        AssemblyComponentDefinition acd = asm.ComponentDefinition;
        ComponentOccurrence occA = acd.Occurrences.Add(partA.FullFileName, tg.CreateMatrix());
        Matrix mB = tg.CreateMatrix();
        mB.SetTranslation(tg.CreateVector(7.0, -2.0, -2.0)); // B face at x = 7 cm, 20 mm beyond A end face
        ComponentOccurrence occB = acd.Occurrences.Add(partB.FullFileName, mB);
        asm.Update();
        partA.Close(true); partB.Close(true);

        Inventor.View view = app.ActiveView;
        Camera cam = view.Camera;
        V3 target = new V3(5.0, 2.0, 0.15);
        V3 dir3 = new V3(0.4, 0.9, 0.3).Unit();
        V3 eye = target + dir3 * 40;
        cam.Target = tg.CreatePoint(target.X, target.Y, target.Z);
        cam.Eye = tg.CreatePoint(eye.X, eye.Y, eye.Z);
        cam.UpVector = tg.CreateUnitVector(0, 0, 1);
        cam.Perspective = false;
        cam.Apply();
        view.Fit();
        view.Update();

        FaceProxy tab = FindFace(occA, new V3(1, 0, 0), 5.0);
        FaceProxy slot = FindFace(occB, new V3(-1, 0, 0), -7.0);
        Console.WriteLine($"VIEW left={view.Left} top={view.Top} width={view.Width} height={view.Height}");
        foreach (var (name, face) in new[] { ("TAB", tab), ("SLOT", slot) })
        {
            FaceInfo fi = FaceInfo.FromSelection(face, false, out _);
            V3 c = fi.Point((fi.UMin + fi.UMax) / 2, (fi.VMin + fi.VMax) / 2);
            Point2d p = view.Camera.ModelToViewSpace(tg.CreatePoint(c.X, c.Y, c.Z));
            Console.WriteLine($"{name} model=({c.X:0.###},{c.Y:0.###},{c.Z:0.###}) viewspace=({p.X:0.#},{p.Y:0.#})");
        }
        return 0;
    }

    /// <summary>Read-only: runs the face analysis on every planar face of a named occurrence in the active assembly.</summary>
    private static int Probe(Inventor.Application app, string occName)
    {
        var asm = (AssemblyDocument)app.ActiveDocument;
        ComponentOccurrence occ = FindOcc(asm.ComponentDefinition.Occurrences, occName);
        if (occ == null) { Console.WriteLine("occurrence not found"); return 1; }
        var def = (PartComponentDefinition)occ.Definition;
        int n = 0;
        foreach (Face f in def.SurfaceBodies[1].Faces)
        {
            if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
            object proxyObj;
            occ.CreateGeometryProxy(f, out proxyObj);
            var proxy = (FaceProxy)proxyObj;
            string err;
            FaceInfo fi = null;
            try { fi = FaceInfo.FromSelection(proxy, false, out err); } catch (Exception ex) { err = "EXCEPTION " + ex.Message; }
            n++;
            if (fi == null) { Console.WriteLine($"#{n} loops={proxy.EdgeLoops.Count} -> {err}"); continue; }
            Console.WriteLine($"#{n} loops={proxy.EdgeLoops.Count} area={proxy.Evaluator.Area:0.##} normal=({fi.Normal.X:0.##},{fi.Normal.Y:0.##},{fi.Normal.Z:0.##}) tube={fi.IsTube} strips={fi.Strips.Count} len={fi.Length:0.###} thick={fi.Thickness:0.###} min={fi.MinExtent:0.###}");
            if (proxy.EdgeLoops.Count == 2)
                foreach (FaceInfo.Strip s in fi.Strips) Console.WriteLine($"     strip len={s.Length:0.###} t={s.Thickness:0.###} U=({s.U.X:0.##},{s.U.Y:0.##},{s.U.Z:0.##})");
        }
        return 0;
    }

    private static ComponentOccurrence FindOcc(System.Collections.IEnumerable occs, string name)
    {
        foreach (ComponentOccurrence o in occs)
        {
            if (o.Name == name) return o;
            if (o.DefinitionDocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
            {
                ComponentOccurrence r = FindOcc(o.SubOccurrences, name);
                if (r != null) return r;
            }
        }
        return null;
    }

    /// <summary>Read-only: for each planar face of the tab occurrence, which end face would be chosen against a broad face of the slot occurrence.</summary>
    private static int Probe2(Inventor.Application app, string tabName, string slotName)
    {
        var asm = (AssemblyDocument)app.ActiveDocument;
        ComponentOccurrence tabOcc = FindOcc(asm.ComponentDefinition.Occurrences, tabName);
        ComponentOccurrence slotOcc = FindOcc(asm.ComponentDefinition.Occurrences, slotName);
        if (tabOcc == null || slotOcc == null) { Console.WriteLine("occurrence not found"); return 1; }

        foreach (Face sf in ((PartComponentDefinition)slotOcc.Definition).SurfaceBodies[1].Faces)
        {
            if (sf.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
            object so; slotOcc.CreateGeometryProxy(sf, out so);
            FaceInfo slot = FaceInfo.FromSelection((FaceProxy)so, false, out string e0);
            if (slot == null || !slot.IsBroadSheetFace) continue;
            Console.WriteLine($"SLOT candidate normal=({slot.Normal.X:0.##},{slot.Normal.Y:0.##},{slot.Normal.Z:0.##}) origin z={slot.Origin.Z:0.##}");

            foreach (Face f in ((PartComponentDefinition)tabOcc.Definition).SurfaceBodies[1].Faces)
            {
                if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
                object po; tabOcc.CreateGeometryProxy(f, out po);
                var picked = (FaceProxy)po;
                FaceInfo pi = FaceInfo.FromSelection(picked, false, out string e1);
                if (pi == null) continue;
                bool ok = EndFaceFinder.PointsAtSlot(pi, slot);
                FaceProxy end = ok ? picked : EndFaceFinder.Find(picked, slot);
                string what = end == null ? "no end face found" : (ok ? "used as is" : "-> end face");
                if (end != null)
                {
                    FaceInfo ei = FaceInfo.FromSelection(end, false, out string e2);
                    what += $" loops={end.EdgeLoops.Count} tube={ei.IsTube} strips={ei.Strips.Count} z={ei.Origin.Z:0.##}";
                }
                Console.WriteLine($"   picked normal=({pi.Normal.X:0.##},{pi.Normal.Y:0.##},{pi.Normal.Z:0.##}) area={picked.Evaluator.Area:0.##}  {what}");
            }
        }
        return 0;
    }

    /// <summary>Read-only: builds the plan for the tube's ring end face against a broad face of the plate and prints every footprint.</summary>
    private static int Probe3(Inventor.Application app, string tabName, string slotName)
    {
        var asm = (AssemblyDocument)app.ActiveDocument;
        ComponentOccurrence tabOcc = FindOcc(asm.ComponentDefinition.Occurrences, tabName);
        ComponentOccurrence slotOcc = FindOcc(asm.ComponentDefinition.Occurrences, slotName);
        if (tabOcc == null || slotOcc == null) { Console.WriteLine("occurrence not found"); return 1; }

        // the ring: planar face with two loops
        FaceInfo tab = null;
        foreach (Face f in ((PartComponentDefinition)tabOcc.Definition).SurfaceBodies[1].Faces)
        {
            if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface || f.EdgeLoops.Count != 2) continue;
            object po; tabOcc.CreateGeometryProxy(f, out po);
            tab = FaceInfo.FromSelection((FaceProxy)po, false, out string e1);
        }
        Console.WriteLine("tab: tube=" + (tab != null && tab.IsTube) + " strips=" + (tab?.Strips.Count ?? 0) + " normal=" + (tab == null ? "-" : $"({tab.Normal.X:0.##},{tab.Normal.Y:0.##},{tab.Normal.Z:0.##}) z={tab.Origin.Z:0.###}"));

        foreach (Face sf in ((PartComponentDefinition)slotOcc.Definition).SurfaceBodies[1].Faces)
        {
            if (sf.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
            object so; slotOcc.CreateGeometryProxy(sf, out so);
            FaceInfo slot = FaceInfo.FromSelection((FaceProxy)so, false, out string e0);
            if (slot == null || !slot.IsBroadSheetFace) continue;
            var p = new TabSlotParams { TabLength = 2.0, Count = 1 };
            Plan plan = Plan.Create(p, tab, slot);
            Console.WriteLine($"slot normal=({slot.Normal.X:0.##},{slot.Normal.Y:0.##},{slot.Normal.Z:0.##}) z={slot.Origin.Z:0.###} thickness={slot.DepthIntoBody():0.###}  -> valid={plan.IsValid} {plan.Error}");
            if (!plan.IsValid) continue;
            Console.WriteLine($"   tabs={plan.Tabs.Count} height={plan.TabHeight:0.###} dir=({plan.TabDirection.X:0.##},{plan.TabDirection.Y:0.##},{plan.TabDirection.Z:0.##})");
            for (int i = 0; i < plan.Tabs.Count; i++)
            {
                Footprint t = plan.Tabs[i];
                Console.WriteLine($"   tab {i}: origin=({t.Origin.X:0.###},{t.Origin.Y:0.###},{t.Origin.Z:0.###}) len={t.Length:0.###} width={t.Width:0.###} U=({t.U.X:0.##},{t.U.Y:0.##},{t.U.Z:0.##})");
            }
        }
        return 0;
    }

    /// <summary>End-to-end on scratch parts: a square tube standing on a sheet metal plate; tabs on the tube walls, slots in the plate.</summary>
    private static int TubeScene(Inventor.Application app)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TabSlotTube_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        System.IO.Directory.CreateDirectory(dir);
        TransientGeometry tg = app.TransientGeometry;
        PartDocument tube = null, plate = null; AssemblyDocument asm = null;
        try
        {
            // tube: 40 x 40 outer with 4 mm corner radius, 3 mm wall, 110 mm long, z from 0 up
            tube = (PartDocument)app.Documents.Add(DocumentTypeEnum.kPartDocumentObject, "", true);
            PartComponentDefinition td = tube.ComponentDefinition;
            PlanarSketch ts = td.Sketches.Add(td.WorkPlanes[3]);
            RoundedSquare(tg, ts, 4.0, 0.4);
            RoundedSquare(tg, ts, 3.4, 0.1);
            Profile tp = ts.Profiles.AddForSolid();
            ExtrudeDefinition ted = td.Features.ExtrudeFeatures.CreateExtrudeDefinition(tp, PartFeatureOperationEnum.kNewBodyOperation);
            ted.SetDistanceExtent(11.0, PartFeatureExtentDirectionEnum.kPositiveExtentDirection);
            td.Features.ExtrudeFeatures.Add(ted);
            tube.SaveAs(System.IO.Path.Combine(dir, "Tube.ipt"), false);

            // plate: sheet metal, 6 mm, 100 x 100
            plate = (PartDocument)app.Documents.Add(DocumentTypeEnum.kPartDocumentObject, "", true);
            plate.SubType = "{9C464203-9BAE-11D3-8BAD-0060B0CE6BB4}";
            var pd = (SheetMetalComponentDefinition)plate.ComponentDefinition;
            pd.UseSheetMetalStyleThickness = false;
            pd.Thickness.Value = 0.6;
            PlanarSketch ps = pd.Sketches.Add(pd.WorkPlanes[3]);
            ps.SketchLines.AddAsTwoPointCenteredRectangle(tg.CreatePoint2d(0, 0), tg.CreatePoint2d(5, 5));
            Profile pp = ps.Profiles.AddForSolid();
            FaceFeatureDefinition fd = ((SheetMetalFeatures)pd.Features).FaceFeatures.CreateFaceFeatureDefinition(pp);
            ((SheetMetalFeatures)pd.Features).FaceFeatures.Add(fd);
            plate.SaveAs(System.IO.Path.Combine(dir, "Plate.ipt"), false);
            Console.WriteLine("plate is sheet metal=" + (plate.SubType == "{9C464203-9BAE-11D3-8BAD-0060B0CE6BB4}") + " thickness=" + pd.Thickness.Value + " volume=" + pd.MassProperties.Volume);

            asm = (AssemblyDocument)app.Documents.Add(DocumentTypeEnum.kAssemblyDocumentObject, "", true);
            asm.SaveAs(System.IO.Path.Combine(dir, "TubeScene.iam"), false);
            AssemblyComponentDefinition acd = asm.ComponentDefinition;
            ComponentOccurrence occPlate = acd.Occurrences.Add(plate.FullFileName, tg.CreateMatrix());
            Matrix mt = tg.CreateMatrix(); mt.SetTranslation(tg.CreateVector(0, 0, 0.6));   // tube end rests on the plate top (z = 6 mm)
            ComponentOccurrence occTube = acd.Occurrences.Add(tube.FullFileName, mt);
            asm.Update();

            double vTube0 = tube.ComponentDefinition.MassProperties.Volume, vPlate0 = pd.MassProperties.Volume;

            // pick: a wall of the tube (not the ring) and the plate's top face
            FaceProxy wall = null, top = null;
            foreach (Face f in td.SurfaceBodies[1].Faces)
            {
                if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
                object po; occTube.CreateGeometryProxy(f, out po);
                var fp = (FaceProxy)po; V3 n = V3.From(((Plane)fp.Geometry).Normal); if (fp.IsParamReversed) n = -n;
                if (Math.Abs(n.X - 1) < 1e-6 && f.EdgeLoops.Count == 1 && fp.Evaluator.Area > 20) { wall = fp; break; }
            }
            foreach (Face f in pd.SurfaceBodies[1].Faces)
            {
                if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
                object po; occPlate.CreateGeometryProxy(f, out po);
                var fp = (FaceProxy)po; V3 n = V3.From(((Plane)fp.Geometry).Normal); if (fp.IsParamReversed) n = -n;
                if (n.Z > 0.999 && fp.Evaluator.Area > 50) { top = fp; break; }
            }
            Console.WriteLine("picked tube wall: " + (wall != null) + ", plate top: " + (top != null));

            FaceInfo slot = FaceInfo.FromSelection(top, false, out string e0);
            FaceInfo wallInfo = FaceInfo.FromSelection(wall, false, out string e1);
            Console.WriteLine("slot sheet thickness=" + slot.SheetThickness + " broad=" + slot.IsBroadSheetFace + "; wall points at slot=" + EndFaceFinder.PointsAtSlot(wallInfo, slot));
            FaceProxy end = EndFaceFinder.Find(wall, slot);
            Console.WriteLine("end face found: " + (end != null));
            FaceInfo tab = FaceInfo.FromSelection(end, false, out string e2);
            Console.WriteLine($"tab face: tube={tab.IsTube} strips={tab.Strips.Count} normal=({tab.Normal.X:0.##},{tab.Normal.Y:0.##},{tab.Normal.Z:0.##}) z={tab.Origin.Z:0.###}");

            var p = new TabSlotParams { Name = "Tube Test", TabLength = 2.0, Count = 1 };
            Plan plan = Plan.Create(p, tab, slot);
            Console.WriteLine("plan valid=" + plan.IsValid + " " + plan.Error + " tabs=" + plan.Tabs.Count + " height=" + plan.TabHeight);
            foreach (Footprint t in plan.Tabs) Console.WriteLine($"   tab len={t.Length:0.###} width={t.Width:0.###} origin=({t.Origin.X:0.##},{t.Origin.Y:0.##},{t.Origin.Z:0.##})");

            p.TabPartFile = tab.PartDoc.FullDocumentName; p.SlotPartFile = slot.PartDoc.FullDocumentName;
            TabSlotStore.CaptureKeys(asm, p, tab, slot);
            try
            {
                TabSlotBuilder.Create(app, p, tab, slot, plan);
                asm.Update();
                Console.WriteLine($"CREATED. tube volume {vTube0:0.###} -> {tube.ComponentDefinition.MassProperties.Volume:0.###}; plate volume {vPlate0:0.###} -> {pd.MassProperties.Volume:0.###}");
                Console.WriteLine("health=" + TabSlotBuilder.GetHealth(asm, p));
            }
            catch (Exception ex) { Console.WriteLine("CREATE FAILED: " + ex.GetType().Name + ": " + ex.Message); }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("SCENE EXCEPTION: " + ex); return 1; }
        finally
        {
            try { asm?.Close(true); } catch { }
            try { plate?.Close(true); } catch { }
            try { tube?.Close(true); } catch { }
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    private static void RoundedSquare(TransientGeometry tg, PlanarSketch sk, double side, double r)
    {
        double h = side / 2;
        SketchLines L = sk.SketchLines; SketchArcs A = sk.SketchArcs;
        var l1 = L.AddByTwoPoints(tg.CreatePoint2d(-h + r, -h), tg.CreatePoint2d(h - r, -h));
        var a1 = A.AddByCenterStartEndPoint(tg.CreatePoint2d(h - r, -h + r), l1.EndSketchPoint, tg.CreatePoint2d(h, -h + r));
        var l2 = L.AddByTwoPoints(a1.EndSketchPoint, tg.CreatePoint2d(h, h - r));
        var a2 = A.AddByCenterStartEndPoint(tg.CreatePoint2d(h - r, h - r), l2.EndSketchPoint, tg.CreatePoint2d(h - r, h));
        var l3 = L.AddByTwoPoints(a2.EndSketchPoint, tg.CreatePoint2d(-h + r, h));
        var a3 = A.AddByCenterStartEndPoint(tg.CreatePoint2d(-h + r, h - r), l3.EndSketchPoint, tg.CreatePoint2d(-h, h - r));
        var l4 = L.AddByTwoPoints(a3.EndSketchPoint, tg.CreatePoint2d(-h, -h + r));
        A.AddByCenterStartEndPoint(tg.CreatePoint2d(-h + r, -h + r), l4.EndSketchPoint, l1.StartSketchPoint);
    }

    /// <summary>Read-only: fires a ray straight down at (x, y) through the active assembly and lists every flat face it crosses.</summary>
    private static int Probe4(Inventor.Application app, double x, double y)
    {
        var asm = (AssemblyDocument)app.ActiveDocument;
        TransientGeometry tg = app.TransientGeometry;
        ObjectsEnumerator found, points;
        asm.ComponentDefinition.FindUsingRay(tg.CreatePoint(x, y, 5000), tg.CreateUnitVector(0, 0, -1), 0.02, out found, out points, false);
        Console.WriteLine("entities along the ray: " + found.Count);
        int i = 0;
        foreach (object o in found)
        {
            i++;
            var fp = o as FaceProxy;
            if (fp == null) { string t; try { t = ((dynamic)o).Type.ToString(); } catch { t = "?"; } string occ0 = ""; try { occ0 = ((dynamic)o).ContainingOccurrence.Name; } catch { } Console.WriteLine($"{i,3}. {t} {occ0}"); continue; }
            string occ; try { occ = fp.ContainingOccurrence.Name; } catch { occ = "?"; }
            string what = fp.SurfaceType.ToString();
            if (fp.SurfaceType == SurfaceTypeEnum.kPlaneSurface)
            {
                V3 n = V3.From(((Plane)fp.Geometry).Normal); if (fp.IsParamReversed) n = -n;
                what = $"plane normal=({n.X:0.#},{n.Y:0.#},{n.Z:0.#}) area={fp.Evaluator.Area:0.##} loops={fp.EdgeLoops.Count}";
            }
            Console.WriteLine($"{i,3}. face  {occ,-20} {what}");
        }
        return 0;
    }

    /// <summary>Read-only: for the tube's ring face, shows how each linear edge relates to each strip's outer line.</summary>
    private static int Probe5(Inventor.Application app, string tabName)
    {
        var asm = (AssemblyDocument)app.ActiveDocument;
        ComponentOccurrence occ = FindOcc(asm.ComponentDefinition.Occurrences, tabName);
        FaceInfo tab = null;
        foreach (Face f in ((PartComponentDefinition)occ.Definition).SurfaceBodies[1].Faces)
        {
            if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface || f.EdgeLoops.Count != 2) continue;
            object po; occ.CreateGeometryProxy(f, out po);
            tab = FaceInfo.FromSelection((FaceProxy)po, false, out string e1);
        }
        Console.WriteLine("ring: strips=" + tab.Strips.Count);
        for (int s = 0; s < tab.Strips.Count; s++)
        {
            FaceInfo.Strip strip = tab.Strips[s];
            Console.WriteLine($"strip {s}: origin=({strip.Origin.X:0.###},{strip.Origin.Y:0.###},{strip.Origin.Z:0.###}) U=({strip.U.X:0.##},{strip.U.Y:0.##},{strip.U.Z:0.##}) len={strip.Length:0.###}");
            int k = 0;
            foreach (Edge e in tab.Proxy.Edges)
            {
                k++;
                if (e.GeometryType != CurveTypeEnum.kLineSegmentCurve) { Console.WriteLine($"   edge {k}: {e.GeometryType}"); continue; }
                V3 a = V3.From(e.StartVertex.Point), b = V3.From(e.StopVertex.Point);
                double dot = Math.Abs((b - a).Unit().Dot(strip.U));
                V3 rel = a - strip.Origin; double off = (rel - strip.U * rel.Dot(strip.U)).Length;
                int faces = 0; string names = "";
                foreach (Face ff in e.Faces) { faces++; var fp = ff as FaceProxy; names += (fp == null ? "[" + ff.GetType().Name + "]" : fp.SurfaceType.ToString()) + " "; }
                Console.WriteLine($"   edge {k}: line parallel={dot:0.####} off={off:0.####} faces={faces} {names}");
            }
        }
        return 0;
    }

    /// <summary>Read-only: lists commands whose names match the keywords and saves their icons as PNG files.</summary>
    private sealed class IconGrabber : System.Windows.Forms.AxHost
    {
        public IconGrabber() : base("") { }
        public static System.Drawing.Image ToImage(object picture) => GetPictureFromIPicture(picture);
    }

    private static int Icons(Inventor.Application app, string outDir, string[] keywords)
    {
        System.IO.Directory.CreateDirectory(outDir);
        int saved = 0;
        foreach (ControlDefinition cd in app.CommandManager.ControlDefinitions)
        {
            string internalName = "", display = "";
            try { internalName = cd.InternalName; display = cd.DisplayName; } catch { continue; }
            bool match = false;
            foreach (string k in keywords)
                if (internalName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 || display.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
            if (!match) continue;
            dynamic bd = cd;
            try
            {
                object pic = bd.LargeIcon;
                if (pic == null) { Console.WriteLine("   no icon exposed"); continue; }
                using (System.Drawing.Image img = IconGrabber.ToImage(pic))
                {
                    string file = System.IO.Path.Combine(outDir, internalName.Replace(':', '_').Replace('\\', '_').Replace('/', '_') + ".png");
                    using (var bmp = new System.Drawing.Bitmap(img)) bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"{internalName}\t{display}\t{img.Width}x{img.Height}");
                    saved++;
                }
            }
            catch (Exception ex) { Console.WriteLine(internalName + " : " + ex.GetType().Name + " " + ex.Message); }
        }
        Console.WriteLine("saved " + saved);
        return 0;
    }

    private static int Cleanup(Inventor.Application app)
    {
        foreach (Document d in app.Documents.Cast<Document>().ToList())
            if (d.FullDocumentName.Contains("TabSlotScene")) d.Close(true);
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TabSlotScene");
        try { System.IO.Directory.Delete(dir, true); } catch { }
        Console.WriteLine("scene removed");
        return 0;
    }

    private static double Volume(PartDocument d) =>
        d.ComponentDefinition.MassProperties.Volume;

    /// <summary>Creates a box part: x/y rectangle on the XY plane extruded z. Dimensions in cm.</summary>
    private static PartDocument MakePlate(Inventor.Application app, string path, double x, double y, double z)
    {
        var doc = (PartDocument)app.Documents.Add(DocumentTypeEnum.kPartDocumentObject, "", true);
        PartComponentDefinition def = doc.ComponentDefinition;
        TransientGeometry tg = app.TransientGeometry;
        PlanarSketch sk = def.Sketches.Add(def.WorkPlanes[3]); // XY plane
        sk.SketchLines.AddAsTwoPointRectangle(tg.CreatePoint2d(0, 0), tg.CreatePoint2d(x, y));
        Profile prof = sk.Profiles.AddForSolid();
        ExtrudeDefinition ed = def.Features.ExtrudeFeatures.CreateExtrudeDefinition(prof, PartFeatureOperationEnum.kNewBodyOperation);
        ed.SetDistanceExtent(z, PartFeatureExtentDirectionEnum.kPositiveExtentDirection);
        def.Features.ExtrudeFeatures.Add(ed);
        doc.SaveAs(path, false);
        return doc;
    }

    /// <summary>Finds a planar face on the occurrence whose world normal is <paramref name="normal"/> and sits at world z.</summary>
    private static FaceProxy FindFace(ComponentOccurrence occ, V3 normal, double worldZ)
    {
        var def = (PartComponentDefinition)occ.Definition;
        foreach (Face f in def.SurfaceBodies[1].Faces)
        {
            if (f.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;
            object proxyObj;
            occ.CreateGeometryProxy(f, out proxyObj);
            var proxy = (FaceProxy)proxyObj;
            var plane = (Plane)proxy.Geometry;
            V3 n = V3.From(plane.Normal);
            if (proxy.IsParamReversed) n = -n;
            double z = V3.From(plane.RootPoint).Dot(normal);
            if (Math.Abs(n.Dot(normal) - 1) < 1e-6 && Math.Abs(z - worldZ) < 1e-6) return proxy;
        }
        return null;
    }
}





