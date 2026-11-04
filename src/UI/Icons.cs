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
            // Flat front view in the style of Inventor's feature icons, shaded top to bottom like Engrave's plate edge.
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

                // front view: a grey plate with a slot cut into its top, and a blue plate above whose tab sits in the slot
                Face(Color.FromArgb(150, 160, 176), Color.FromArgb(84, 93, 108),
                    P(1, 19), P(10.5f, 19), P(10.5f, 25), P(21.5f, 25), P(21.5f, 19), P(31, 19), P(31, 28), P(1, 28));
                Face(Color.FromArgb(70, 168, 236), Color.FromArgb(8, 82, 160),
                    P(1, 6), P(31, 6), P(31, 14), P(20, 14), P(20, 24), P(12, 24), P(12, 14), P(1, 14));
            }
            return bmp;
        }

    }
}
