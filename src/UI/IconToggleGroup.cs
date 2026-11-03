using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TabSlotAddIn.UI
{
    /// <summary>
    /// A row of icon buttons of which exactly one is selected, like the Direction / Distribution / Orientation
    /// toggles in Inventor's feature panels. Drop-in replacement for a drop-down: same SelectedIndex semantics.
    /// </summary>
    internal sealed class IconToggleGroup : Control
    {
        public delegate void IconDrawer(Graphics g, Rectangle bounds, Color ink, Color accent);

        private const int ButtonWidth = 34;
        private const int ButtonHeight = 28;

        private readonly Theme _theme;
        private readonly (string Tip, IconDrawer Draw)[] _items;
        private readonly ToolTip _tip = new ToolTip();
        private int _selected;
        private int _hover = -1;

        public event EventHandler SelectedIndexChanged;

        public IconToggleGroup(Theme theme, params (string Tip, IconDrawer Draw)[] items)
        {
            _theme = theme;
            _items = items;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(items.Length * ButtonWidth + 2, ButtonHeight + 2);
            MinimumSize = Size;
            BackColor = theme.Back;
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value < -1 || value >= _items.Length || value == _selected) return;
                _selected = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private Rectangle ButtonRect(int i) => new Rectangle(1 + i * ButtonWidth, 1, ButtonWidth - 2, ButtonHeight);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle r = ButtonRect(i);
                bool on = i == _selected;
                // no outline boxes: the selected icon gets a soft accent highlight, hover a faint tint, the rest nothing
                if (on || i == _hover)
                    using (var fill = new SolidBrush(on ? _theme.AccentFill : _theme.Hover))
                        g.FillRectangle(fill, r);
                _items[i].Draw(g, r, Enabled ? _theme.Text : _theme.Dim, _theme.Accent);
            }
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _items.Length; i++) if (ButtonRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h == _hover) return;
            _hover = h;
            if (h >= 0) _tip.SetToolTip(this, _items[h].Tip);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h < 0 || e.Button != MouseButtons.Left) return;
            SelectedIndex = h;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The icon of a spacing toggle: the chosen Inventor command's icon, or a small drawn pictogram if it cannot be read.</summary>
    internal static class ToggleIcons
    {
        private static Pen Ink(Color c) => new Pen(c, 1.4f);

        /// <summary>Draws the icon of the Inventor command chosen for <paramref name="slot"/> (24 px, centred); otherwise <paramref name="fallback"/>.</summary>
        public static IconToggleGroup.IconDrawer Cmd(string slot, IconToggleGroup.IconDrawer fallback) => (g, b, ink, accent) =>
        {
            Bitmap icon = InventorCommandIcons.Get(IconChoices.Command(slot), true);
            if (icon == null) { fallback(g, b, ink, accent); return; }
            var before = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new Rectangle(b.X + (b.Width - 24) / 2, b.Y + (b.Height - 24) / 2, 24, 24));
            g.InterpolationMode = before;
        };

        /// <summary>A plate edge along the bottom with tabs standing on it.</summary>
        private static void Plate(Graphics g, Rectangle b, Color ink, Color accent, params float[] tabCentres)
        {
            var r = new RectangleF(b.X + 7, b.Y + 6, b.Width - 14, b.Height - 12);
            float baseY = r.Bottom - 2;
            using (var pen = Ink(ink)) g.DrawLine(pen, r.Left, baseY, r.Right, baseY);
            using (var fill = new SolidBrush(accent))
                foreach (float c in tabCentres)
                    g.FillRectangle(fill, r.Left + c * r.Width - 2, r.Top, 4, baseY - r.Top);
        }

        public static void EqualSpacing(Graphics g, Rectangle b, Color ink, Color accent) =>
            Plate(g, b, ink, accent, 0.2f, 0.5f, 0.8f);

        /// <summary>Square tube cross-section; the walls that get tabs are drawn in the accent colour.</summary>
        private static void Tube(Graphics g, Rectangle b, Color ink, Color accent, bool horizontal, bool vertical)
        {
            int s = Math.Min(b.Width, b.Height) - 10, t = 3;
            var r = new Rectangle(b.X + (b.Width - s) / 2, b.Y + (b.Height - s) / 2, s, s);
            using (var pen = new Pen(ink, 1f)) g.DrawRectangle(pen, r);
            using (var fill = new SolidBrush(accent))
            {
                if (horizontal) { g.FillRectangle(fill, r.X, r.Y, s, t); g.FillRectangle(fill, r.X, r.Bottom - t + 1, s, t); }
                if (vertical) { g.FillRectangle(fill, r.X, r.Y, t, s); g.FillRectangle(fill, r.Right - t + 1, r.Y, t, s); }
            }
        }

        public static void TubeAll(Graphics g, Rectangle b, Color ink, Color accent) => Tube(g, b, ink, accent, true, true);
        public static void TubePairA(Graphics g, Rectangle b, Color ink, Color accent) => Tube(g, b, ink, accent, true, false);
        public static void TubePairB(Graphics g, Rectangle b, Color ink, Color accent) => Tube(g, b, ink, accent, false, true);

        public static void FixedPitch(Graphics g, Rectangle b, Color ink, Color accent)
        {
            Plate(g, b, ink, accent, 0.25f, 0.65f);
            var r = new RectangleF(b.X + 7, b.Y + 6, b.Width - 14, b.Height - 12);
            using (var pen = Ink(ink))
                g.DrawLine(pen, r.Left + 0.25f * r.Width, r.Top - 1, r.Left + 0.65f * r.Width, r.Top - 1);
        }
    }
}
