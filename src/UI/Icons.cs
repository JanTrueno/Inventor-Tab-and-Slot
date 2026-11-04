using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace TabSlotAddIn.UI
{
    /// <summary>The ribbon icon: embedded PNG images (src\Assets\Icon), converted to the IPictureDisp Inventor wants.</summary>
    internal static class Icons
    {
        private sealed class PictureConverter : AxHost
        {
            private PictureConverter() : base(string.Empty) { }
            public static object ToPictureDisp(Image image) => GetIPictureDispFromPicture(image);
        }

        private static readonly int[] Sizes = { 16, 24, 32, 48, 64 };

        /// <summary>The icon for a 16 or 32 px ribbon slot, in the size that fits the display scale (48 for 32 at 150 %).</summary>
        public static object CreateTabSlot(int size)
        {
            float scale = 1f;
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = Math.Max(1f, g.DpiX / 96f); } catch { }
            int wanted = (int)Math.Round(size * scale);
            int pick = Array.Find(Sizes, n => n >= wanted);
            if (pick == 0) pick = Sizes[Sizes.Length - 1];

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TabSlotAddIn.Icon.tabslot_" + pick + ".png"))
            using (var png = Image.FromStream(stream))
                return PictureConverter.ToPictureDisp(new Bitmap(png));
        }
    }
}
