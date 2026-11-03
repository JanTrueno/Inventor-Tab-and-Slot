using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TabSlotAddIn.UI
{
    /// <summary>Ribbon icons drawn at run time, converted to the IPictureDisp Inventor wants.</summary>
    internal static class Icons
    {
        private sealed class PictureConverter : AxHost
        {
            private PictureConverter() : base(string.Empty) { }
            public static object ToPictureDisp(Image image) => GetIPictureDispFromPicture(image);
        }

        public static object CreateTabSlot(int size) => PictureConverter.ToPictureDisp(DrawTabSlot(size));

        private static Bitmap NewCanvas(int size, out Graphics g, out float s)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            s = size / 32f;
            return bmp;
        }

        private static Bitmap DrawTabSlot(int size)
        {
            Bitmap bmp = NewCanvas(size, out Graphics g, out float s);
            // Shaded isometric view in the style of Inventor's feature icons: a grey plate with a slot,
            // and a blue tab standing in it. Light from the top left: top face lightest, right face darkest.
            using (g)
            using (var outline = new Pen(Color.FromArgb(60, 68, 82), Math.Max(1f, s * 0.8f)))
            {
                PointF P(float x, float y) => new PointF(x * s, y * s);
                void Face(Color a, Color b, params PointF[] pts)
                {
                    RectangleF bounds = new RectangleF(pts[0].X, pts[0].Y, 0, 0);
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    foreach (PointF p in pts) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
                    bounds = new RectangleF(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
                    using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(bounds, a, b, 90f))
                        g.FillPolygon(brush, pts);
                    g.DrawPolygon(outline, pts);
                }

                // plate (slab): top, left and right faces
                Face(Color.FromArgb(214, 221, 232), Color.FromArgb(176, 186, 201), P(2, 18), P(16, 12), P(30, 18), P(16, 24));
                Face(Color.FromArgb(150, 160, 176), Color.FromArgb(120, 130, 146), P(2, 18), P(16, 24), P(16, 28), P(2, 22));
                Face(Color.FromArgb(112, 122, 138), Color.FromArgb(84, 93, 108), P(16, 24), P(30, 18), P(30, 22), P(16, 28));

                // slot cut into the top face
                Face(Color.FromArgb(70, 78, 92), Color.FromArgb(40, 46, 58), P(11, 18), P(16, 15.8f), P(21, 18), P(16, 20.2f));

                // tab standing in the slot: left, right, top
                Face(Color.FromArgb(70, 168, 236), Color.FromArgb(20, 120, 205), P(12.5f, 9), P(16, 10.8f), P(16, 20), P(12.5f, 18.2f));
                Face(Color.FromArgb(16, 112, 196), Color.FromArgb(8, 82, 160), P(16, 10.8f), P(19.5f, 9), P(19.5f, 18.2f), P(16, 20));
                Face(Color.FromArgb(150, 214, 250), Color.FromArgb(110, 190, 242), P(12.5f, 9), P(16, 7.2f), P(19.5f, 9), P(16, 10.8f));
            }
            return bmp;
        }

    }
}
