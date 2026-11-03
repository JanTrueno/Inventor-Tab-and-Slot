using System;
using System.Collections.Generic;

namespace TabSlotAddIn.Core
{
    /// <summary>One tab footprint on the tab face plane: Origin, +U*Length, +V*Width.</summary>
    internal struct Footprint
    {
        public V3 Origin, U, V;
        public double Length, Width;
        public int StripIndex;   // which strip of the tab face it sits on (tube: one per wall)

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

        public string Error;
        public bool IsValid => Error == null;

        public List<Footprint> Tabs = new List<Footprint>();
        public double TabHeight;
        public V3 TabDirection;
        public V3 SlotInto;
        public V3 SlotPlanePoint;
        public double Clearance;
        public bool SlotThrough;
        public double SlotDepth;     // depth to cut when blind
        public double SlotPreviewDepth;

        /// <summary>Footprint of the slot for tab i: the tab outline grown by the clearance and projected onto the slot face plane.</summary>
        public Footprint SlotFootprint(int i)
        {
            Footprint t = Tabs[i];
            V3 o = t.Origin - t.U * Clearance - t.V * Clearance;
            double along = (o - SlotPlanePoint).Dot(TabDirection);
            return new Footprint
            {
                Origin = o - TabDirection * along,
                U = t.U,
                V = t.V,
                Length = t.Length + 2 * Clearance,
                Width = t.Width + 2 * Clearance
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
            if (Math.Abs(tab.Normal.Dot(slot.Normal)) < ParallelTolerance)
            {
                plan.Error = "The tab face and the slot face are not parallel.";
                return plan;
            }
            if (p.TabLength <= 1e-6) { plan.Error = "Tab length must be greater than zero."; return plan; }
            if (p.Count < 1) { plan.Error = "At least one tab is required."; return plan; }
            if (p.Clearance < 0) { plan.Error = "Slot oversize cannot be negative."; return plan; }

            // One strip for an ordinary face (an edge face of a sheet); four for a square tube, one per flat wall.
            // The tabs are laid out along each strip, so on a tube every wall gets the same arrangement.
            for (int stripIndex = 0; stripIndex < tab.Strips.Count; stripIndex++)
            {
                FaceInfo.Strip strip = tab.Strips[stripIndex];
                if (tab.IsTube && p.TubeWalls != 0)
                {
                    // Two opposite walls only: pair 1 runs parallel to the first wall, pair 2 square to it.
                    bool parallelToFirst = Math.Abs(strip.U.Dot(tab.Strips[0].U)) > 0.9;
                    if (parallelToFirst != (p.TubeWalls == 1)) continue;
                }
                // Width of the tab across the strip (normally the plate or wall thickness).
                double width = strip.Thickness;
                if (p.Width == WidthMode.Custom && !tab.IsTube)
                {
                    if (p.CustomWidth <= 1e-6) { plan.Error = "Tab width must be greater than zero."; return plan; }
                    if (p.CustomWidth > width + 1e-6) { plan.Error = "Tab width is larger than the face it grows from."; return plan; }
                    width = p.CustomWidth;
                }
                double v0 = (strip.Thickness - width) / 2;

                string layoutError;
                List<double> starts = Layout(p, strip.Length, out layoutError);
                if (layoutError != null) { plan.Error = layoutError; return plan; }

                foreach (double a in starts)
                {
                    double u0 = p.FlipStart ? strip.Length - a - p.TabLength : a;
                    plan.Tabs.Add(new Footprint
                    {
                        Origin = strip.Origin + strip.U * u0 + strip.V * v0,
                        U = strip.U,
                        V = strip.V,
                        Length = p.TabLength,
                        Width = width,
                        StripIndex = stripIndex
                    });
                }
            }

            plan.TabDirection = tab.Normal;
            plan.SlotInto = -slot.Normal;
            plan.SlotPlanePoint = slot.Origin;
            plan.Clearance = p.ClearanceFor(slot.DepthIntoBody(), p.TabLength);
            plan.SlotThrough = p.SlotThrough;

            if (p.Height == HeightMode.Flush)
            {
                // Distance from the tab face plane to the far side of the slot sheet, measured locally at the picked face
                // (the whole slot part can be much bigger than the sheet, e.g. with bends and flanges).
                double slotSheet = slot.DepthIntoBody();
                if (slotSheet <= 1e-6) { plan.Error = "Could not measure the slot part; use a fixed tab height."; return plan; }
                double toSlotPlane = (slot.Origin - tab.Origin).Dot(tab.Normal);
                bool slotFaceIsNear = slot.Normal.Dot(tab.Normal) < 0;
                plan.TabHeight = toSlotPlane + (slotFaceIsNear ? slotSheet : 0) + p.HeightOffset;
            }
            else
            {
                plan.TabHeight = p.HeightValue;
            }
            if (plan.TabHeight <= 1e-6)
            {
                plan.Error = "The tab height is zero or negative. Check that the slot part lies in front of the tab face.";
                return plan;
            }

            plan.SlotDepth = p.SlotDepth;
            double thickness = slot.DepthIntoBody();
            plan.SlotPreviewDepth = p.SlotThrough ? Math.Max(thickness, 1e-3) : p.SlotDepth;
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
