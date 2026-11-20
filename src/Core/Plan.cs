using System;
using System.Collections.Generic;

namespace TabSlotAddIn.Core
{
    /// <summary>
    /// One tab: its root cross-section Origin, +U*Length, +V*Width, growing along Dir by Height.
    /// On a square-cut end Dir is the face normal; on a mitered end it is tilted, staying in the plane of the wall.
    /// </summary>
    internal struct Footprint
    {
        public V3 Origin, U, V;
        public double Length, Width;
        public int StripIndex;   // which strip of the tab face it sits on (tube: one per wall)
        public V3 Dir;
        public double Height;

        public V3[] Corners() => new[]
        {
            Origin,
            Origin + U * Length,
            Origin + U * Length + V * Width,
            Origin + V * Width
        };
    }

    /// <summary>
    /// The resolved geometry of a tab-and-slot set: where every tab sits, how tall it is and
    /// where the matching slots land on the slot face. Pure maths, no Inventor object creation.
    /// </summary>
    internal sealed class Plan
    {
        public const double ParallelTolerance = 0.99985; // ~1 degree
        private const double MinCrossing = 0.17;          // a tab must meet the slot face at more than ~10 degrees

        public string Error;
        public bool IsValid => Error == null;

        public List<Footprint> Tabs = new List<Footprint>();
        public double TabHeight;       // the tallest tab, for the status line
        public V3 TabDirection;        // the picked face's normal
        public V3 SlotInto;
        public V3 SlotPlanePoint;
        public double Clearance;
        public bool SlotThrough;
        public double SlotDepth;       // depth to cut when blind
        public double SlotPreviewDepth;
        private double _slotWall;      // thickness of the slot wall, for the tab's path through it

        /// <summary>True when any tab leaves its face at an angle (a mitered end).</summary>
        public bool AnyMitered
        {
            get { foreach (Footprint t in Tabs) if (t.Dir.Dot(TabDirection) < 0.99999) return true; return false; }
        }

        /// <summary>
        /// Footprint of the slot for tab i on the slot face: the outline of the tab's path through the slot wall, seen square
        /// to the face (so a tab passing at an angle gets a longer slot), grown by the clearance along the tab and by half
        /// the clearance across it (the sheet thickness is rolled to a tight tolerance).
        /// </summary>
        public Footprint SlotFootprint(int i)
        {
            Footprint t = Tabs[i];
            V3 n = -SlotInto;
            V3 u = (t.U - n * t.U.Dot(n)).Unit();
            V3 v = n.Cross(u).Unit();
            if (v.Dot(t.V) < 0) v = -v;

            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (V3 c in t.Corners())
            {
                foreach (V3 plane in new[] { SlotPlanePoint, SlotPlanePoint - n * _slotWall })
                {
                    double s = (plane - c).Dot(n) / t.Dir.Dot(n);
                    V3 d = c + t.Dir * s - SlotPlanePoint;
                    u0 = Math.Min(u0, d.Dot(u)); u1 = Math.Max(u1, d.Dot(u));
                    v0 = Math.Min(v0, d.Dot(v)); v1 = Math.Max(v1, d.Dot(v));
                }
            }
            double across = Clearance / 2;
            return new Footprint
            {
                Origin = SlotPlanePoint + u * (u0 - Clearance) + v * (v0 - across),
                U = u,
                V = v,
                Length = u1 - u0 + 2 * Clearance,
                Width = v1 - v0 + 2 * across,
                Dir = SlotInto
            };
        }

        public static Plan Create(TabSlotParams p, FaceInfo tab, FaceInfo slot)
        {
            var plan = new Plan();

            if (ReferenceEquals(tab.PartDoc, slot.PartDoc) ||
                string.Equals(tab.PartDoc.FullDocumentName, slot.PartDoc.FullDocumentName, StringComparison.OrdinalIgnoreCase))
            {
                plan.Error = "The tab face and the slot face must belong to different parts.";
                return plan;
            }
            if (p.TabLength <= 1e-6) { plan.Error = "Tab length must be greater than zero."; return plan; }
            if (p.Count < 1) { plan.Error = "At least one tab is required."; return plan; }
            if (p.Clearance < 0) { plan.Error = "Slot oversize cannot be negative."; return plan; }

            plan.TabDirection = tab.Normal;
            plan.SlotInto = -slot.Normal;
            plan.SlotPlanePoint = slot.Origin;
            plan.Clearance = p.ClearanceFor(slot.DepthIntoBody(), p.TabLength);
            plan.SlotThrough = p.SlotThrough;
            double slotSheet = slot.DepthIntoBody();
            plan._slotWall = Math.Max(slotSheet, 0);

            // One strip for an ordinary face (an edge face of a sheet); four for a square tube, one per flat wall.
            // The tabs are laid out along each strip, so on a tube every wall gets the same arrangement.
            int skipped = 0;
            for (int stripIndex = 0; stripIndex < tab.Strips.Count; stripIndex++)
            {
                FaceInfo.Strip strip = tab.Strips[stripIndex];
                if (tab.IsTube && p.TubeWalls != 0)
                {
                    // Two opposite walls only: pair 1 runs parallel to the first wall, pair 2 square to it.
                    bool parallelToFirst = Math.Abs(strip.U.Dot(tab.Strips[0].U)) > 0.9;
                    if (parallelToFirst != (p.TubeWalls == 1)) continue;
                }

                // A wall whose tabs would run along the slot face instead of into it (a mitered tube's walls that lie in
                // the plane of the corner) gets none.
                double crossing = strip.Dir.Dot(slot.Normal);
                if (Math.Abs(crossing) < MinCrossing)
                {
                    if (tab.IsTube) { skipped++; continue; }
                    plan.Error = "The tabs would run along the slot face instead of into it.";
                    return plan;
                }

                // Width of the tab across the strip (normally the plate or wall thickness).
                double width = strip.Wall;
                if (p.Width == WidthMode.Custom && !tab.IsTube && !strip.Mitered)
                {
                    if (p.CustomWidth <= 1e-6) { plan.Error = "Tab width must be greater than zero."; return plan; }
                    if (p.CustomWidth > width + 1e-6) { plan.Error = "Tab width is larger than the face it grows from."; return plan; }
                    width = p.CustomWidth;
                }
                double v0 = (strip.Wall - width) / 2;

                string layoutError;
                List<double> starts = Layout(p, strip.Length, out layoutError);
                if (layoutError != null) { plan.Error = layoutError; return plan; }

                foreach (double a in starts)
                {
                    double u0 = p.FlipStart ? strip.Length - a - p.TabLength : a;
                    var f = new Footprint
                    {
                        Origin = strip.Origin + strip.U * u0 + strip.Across * v0,
                        U = strip.U,
                        V = strip.Across,
                        Length = p.TabLength,
                        Width = width,
                        StripIndex = stripIndex,
                        Dir = strip.Dir
                    };

                    if (p.Height == HeightMode.Flush)
                    {
                        // Up to the far side of the slot wall, measured along the tab from the middle of its root.
                        if (slotSheet <= 1e-6) { plan.Error = "Could not measure the slot part; use a fixed tab height."; return plan; }
                        V3 mid = f.Origin + f.U * (f.Length / 2) + f.V * (f.Width / 2);
                        V3 farSide = crossing < 0 ? slot.Origin - slot.Normal * slotSheet : slot.Origin;
                        f.Height = (farSide - mid).Dot(slot.Normal) / crossing + p.HeightOffset;
                    }
                    else f.Height = p.HeightValue;

                    if (f.Height <= 1e-6)
                    {
                        plan.Error = "The tab height is zero or negative. Check that the slot part lies in front of the tab face.";
                        return plan;
                    }
                    plan.TabHeight = Math.Max(plan.TabHeight, f.Height);
                    plan.Tabs.Add(f);
                }
            }
            if (plan.Tabs.Count == 0)
            {
                plan.Error = skipped > 0 ? "None of the walls runs into the slot face." : "No tabs to place.";
                return plan;
            }

            plan.SlotDepth = p.SlotDepth;
            plan.SlotPreviewDepth = p.SlotThrough ? Math.Max(slotSheet, 1e-3) : p.SlotDepth;
            if (!p.SlotThrough && p.SlotDepth <= 1e-6) plan.Error = "Slot depth must be greater than zero.";
            return plan;
        }

        /// <summary>Start position of each tab, measured along the face from its start edge.</summary>
        public static List<double> Layout(TabSlotParams p, double faceLength, out string error)
        {
            error = null;
            var starts = new List<double>();
            double total = p.Count * p.TabLength;
            if (total > faceLength + 1e-6)
            {
                error = "The tabs are longer than the face they sit on.";
                return starts;
            }

            if (p.Spacing == SpacingMode.Equal)
            {
                double gap = (faceLength - total) / (p.Count + 1);
                for (int i = 0; i < p.Count; i++) starts.Add(gap + i * (p.TabLength + gap));
                return starts;
            }

            if (p.Count > 1 && p.Pitch < p.TabLength - 1e-9)
            {
                error = "The pitch is smaller than the tab length, so the tabs would overlap.";
                return starts;
            }
            double span = (p.Count - 1) * p.Pitch + p.TabLength;
            double first = p.CenterGroup ? (faceLength - span) / 2 : p.Offset;
            if (first < -1e-6 || first + span > faceLength + 1e-6)
            {
                error = "The tabs do not fit on the face with this pitch/offset.";
                return starts;
            }
            for (int i = 0; i < p.Count; i++) starts.Add(first + i * p.Pitch);
            return starts;
        }
    }
}
