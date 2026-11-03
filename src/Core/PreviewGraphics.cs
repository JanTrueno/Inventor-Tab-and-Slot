using System;
using Inventor;

namespace TabSlotAddIn.Core
{
    /// <summary>Transient client graphics showing the tabs (green) and slots (red) before the features are created.</summary>
    internal sealed class PreviewGraphics : IDisposable
    {
        private const string GraphicsId = "TabSlotAddIn.Preview";

        private readonly Inventor.Application _app;
        private readonly AssemblyDocument _doc;
        private ClientGraphics _graphics;

        public PreviewGraphics(Inventor.Application app, AssemblyDocument doc)
        {
            _app = app;
            _doc = doc;
        }

        public void Show(Plan plan)
        {
            Clear();
            if (plan == null || !plan.IsValid) { Refresh(); return; }

            TransientGeometry tg = _app.TransientGeometry;
            TransientBRep brep = _app.TransientBRep;
            AssemblyComponentDefinition def = _doc.ComponentDefinition;

            _graphics = def.ClientGraphicsCollection.Add(GraphicsId);
            GraphicsNode slotNode = _graphics.AddNode(1);   // drawn first, so the tab shows on top of it
            GraphicsNode tabNode = _graphics.AddNode(2);

            Color green = _app.TransientObjects.CreateColor(30, 190, 80, 0.9);
            Color red = _app.TransientObjects.CreateColor(220, 60, 50, 0.55);

            for (int i = 0; i < plan.Tabs.Count; i++)
            {
                Footprint t = plan.Tabs[i];
                AddBlock(tabNode, tg, brep, t.Origin, t.U, t.V, plan.TabDirection, t.Length, t.Width, plan.TabHeight, green);

                Footprint s = plan.SlotFootprint(i);
                // The slot block always extends along the tab axis; flip its origin when the cut runs against it.
                V3 origin = s.Origin;
                if (plan.SlotInto.Dot(plan.TabDirection) < 0) origin = origin - plan.TabDirection * plan.SlotPreviewDepth;
                AddBlock(slotNode, tg, brep, origin, s.U, s.V, plan.TabDirection, s.Length, s.Width, plan.SlotPreviewDepth, red);
            }
            Refresh();
        }

        private static void AddBlock(GraphicsNode node, TransientGeometry tg, TransientBRep brep,
                                     V3 origin, V3 ex, V3 ey, V3 ez, double lx, double ly, double lz, Color color)
        {
            Box box = tg.CreateBox();
            box.MinPoint = tg.CreatePoint(0, 0, 0);
            box.MaxPoint = tg.CreatePoint(lx, ly, lz);
            SurfaceBody body = brep.CreateSolidBlock(box);

            Matrix m = tg.CreateMatrix();
            m.SetCoordinateSystem(origin.ToPoint(tg), ex.ToVector(tg), ey.ToVector(tg), ez.ToVector(tg));
            brep.Transform(body, m);

            SurfaceGraphics sg = node.AddSurfaceGraphics(body);
            sg.Color = color;
        }

        public void Clear()
        {
            if (_graphics == null) return;
            try { _graphics.Delete(); } catch { /* document may be closing */ }
            _graphics = null;
        }

        private void Refresh()
        {
            try { _app.ActiveView.Update(); } catch { /* no active view */ }
        }

        public void Dispose()
        {
            Clear();
            Refresh();
        }
    }
}
