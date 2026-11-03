using System;
using System.Collections.Generic;
using Inventor;

namespace TabSlotAddIn.Core
{
    /// <summary>
    /// A tube or sheet standing on another part touches it, so its end face cannot be clicked. Given any face of that part
    /// (a wall, the far end, ...) this finds the end face that points at the slot face by walking to neighbouring faces.
    /// </summary>
    internal static class EndFaceFinder
    {
        private const int MaxDepth = 2;

        /// <summary>True when the face is parallel to the slot face and the slot plane lies in front of it.</summary>
        public static bool PointsAtSlot(FaceInfo face, FaceInfo slot) =>
            Math.Abs(face.Normal.Dot(slot.Normal)) >= Plan.ParallelTolerance &&
            (slot.Origin - face.Origin).Dot(face.Normal) >= -0.05;

        /// <summary>Returns the neighbouring planar face (within two steps) parallel to the slot face and facing it, nearest first; null if none.</summary>
        public static FaceProxy Find(FaceProxy start, FaceInfo slot)
        {
            var seen = new HashSet<int> { start.TransientKey };
            var level = new List<FaceProxy> { start };
            FaceProxy best = null;
            double bestDistance = double.MaxValue;
            double slotPlane = slot.Origin.Dot(slot.Normal);

            for (int depth = 0; depth < MaxDepth; depth++)
            {
                var next = new List<FaceProxy>();
                foreach (FaceProxy face in level)
                {
                    foreach (Edge edge in face.Edges)
                    {
                        foreach (Face f in edge.Faces)
                        {
                            var fp = f as FaceProxy;
                            if (fp == null || !seen.Add(fp.TransientKey)) continue;
                            next.Add(fp);
                            if (fp.SurfaceType != SurfaceTypeEnum.kPlaneSurface) continue;

                            var plane = (Plane)fp.Geometry;
                            V3 n = V3.From(plane.Normal);
                            if (fp.IsParamReversed) n = -n;
                            if (Math.Abs(n.Dot(slot.Normal)) < Plan.ParallelTolerance) continue;
                            V3 p = V3.From(plane.RootPoint);
                            if ((slot.Origin - p).Dot(n) < -0.05) continue;            // faces away from the slot plane

                            double distance = Math.Abs(p.Dot(slot.Normal) - slotPlane);
                            if (distance < bestDistance) { bestDistance = distance; best = fp; }
                        }
                    }
                }
                if (best != null) return best;   // nearest ring found at this depth
                level = next;
            }
            return best;
        }
    }
}
