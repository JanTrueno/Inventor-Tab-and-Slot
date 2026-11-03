using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TabSlotAddIn.Core
{
    internal enum SpacingMode { Equal = 0, Pitch = 1 }
    internal enum WidthMode { FullThickness = 0, Custom = 1 }
    internal enum HeightMode { Flush = 0, Distance = 1 }

    /// <summary>
    /// Everything the user can set on a tab-and-slot feature. All lengths are in Inventor database units (cm).
    /// Instances round-trip through <see cref="Serialize"/> / <see cref="Parse"/> so they can be stored
    /// as an attribute on the assembly and edited again later.
    /// </summary>
    internal sealed class TabSlotParams
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name = "Tab and Slot";

        // Layout of the tabs along the tab face
        public double TabLength = 2.0;
        public int Count = 1;
        public SpacingMode Spacing = SpacingMode.Equal;
        public double Pitch = 4.0;
        public double Offset = 1.0;
        public bool CenterGroup = true;
        public bool FlipStart;
        public bool RunAlongShortEdge;

        // Tab cross-section / height
        public WidthMode Width = WidthMode.FullThickness;
        public double CustomWidth = 0.5;
        public HeightMode Height = HeightMode.Flush;
        public double HeightValue = 0.5;
        public double HeightOffset;

        // Slot
        public double Clearance = 0.01; // slot oversize per side, cm (0.1 mm)
        public bool Relief = true;       // small circular relief at each slot corner
        public double ReliefRadius = 0.02; // cm (0.2 mm), used when ReliefAuto is off
        public bool ClearanceAuto = true;  // oversize follows the cutting tolerance table (thickness x tab length)

        // ISO 9013 cutting tolerances (mm, +/-): rows by plate thickness, columns by nominal size (the tab length).
        private static readonly double[] TolThickness = { 1.0, 3.0, 6.0, 10.0 };   // upper bounds; anything thicker uses the last row
        private static readonly double[] TolSize = { 3, 10, 35, 125, 315, 1000, 2000 };   // upper bounds; above uses the last column
        private static readonly double[,] Tol =
        {
            { 0.2, 0.2, 0.2, 0.2, 0.2, 0.3, 0.3, 0.3 },
            { 0.2, 0.2, 0.2, 0.3, 0.3, 0.4, 0.4, 0.4 },
            { 0.2, 0.3, 0.4, 0.4, 0.5, 0.5, 0.5, 0.6 },
            { 0.5, 0.5, 0.6, 0.6, 0.7, 0.7, 0.7, 0.8 },   // no 0-3 value in the table: same as 3-10
            { 0.6, 0.6, 0.7, 0.7, 0.8, 1.0, 1.6, 2.5 },
        };

        /// <summary>Slot oversize per side in cm for a plate of the given thickness and a tab of the given length (both cm).</summary>
        public double ClearanceFor(double thicknessCm, double lengthCm)
        {
            if (!ClearanceAuto) return Clearance;
            double t = thicknessCm * 10, l = lengthCm * 10;
            int row = 0; while (row < TolThickness.Length && t > TolThickness[row] + 1e-6) row++;
            int col = 0; while (col < TolSize.Length && l > TolSize[col] + 1e-6) col++;
            return Tol[row, col] / 10;
        }

        public int TubeWalls;             // square tube: 0 all four walls, 1 or 2 one pair of opposite walls
        public bool ReliefAuto = true;    // radius follows the plate thickness (corner hole table)

        /// <summary>Relief radius in cm for a plate of the given thickness (cm).</summary>
        public double ReliefFor(double thicknessCm)
        {
            if (!Relief) return 0;
            if (!ReliefAuto) return ReliefRadius;
            double t = thicknessCm * 10;   // mm. Hole diameter: up to 3 mm: 1.0, up to 6: 1.5, up to 12: 2.0, above: 4.0
            double diameterMm = t <= 3.0 + 1e-6 ? 1.0 : t <= 6.0 + 1e-6 ? 1.5 : t <= 12.0 + 1e-6 ? 2.0 : 4.0;
            return diameterMm / 20;
        }
        public bool Chamfer = true;        // chamfer the two tip corners of every tab (1 mm unless changed)
        public double ChamferSize = 0.1;   // cm (1 mm)
        public bool SlotThrough = true;
        public double SlotDepth = 0.5;

        // Bookkeeping needed to find things again
        public string TabFaceKey = "";
        public string SlotFaceKey = "";
        public string KeyContext = "";
        public string TabPartFile = "";
        public string SlotPartFile = "";
        public string TabOccName = "";
        public string SlotOccName = "";

        public TabSlotParams Clone() => Parse(Serialize());

        public string Serialize()
        {
            var sb = new StringBuilder();
            void S(string k, string v) => sb.Append(k).Append('=').Append((v ?? "").Replace("\r", " ").Replace("\n", " ")).Append('\n');
            void D(string k, double v) => S(k, v.ToString("R", CultureInfo.InvariantCulture));
            void B(string k, bool v) => S(k, v ? "1" : "0");
            void I(string k, int v) => S(k, v.ToString(CultureInfo.InvariantCulture));

            S("Id", Id); S("Name", Name);
            D("TabLength", TabLength); I("Count", Count); I("Spacing", (int)Spacing);
            D("Pitch", Pitch); D("Offset", Offset); B("CenterGroup", CenterGroup);
            B("FlipStart", FlipStart); B("RunAlongShortEdge", RunAlongShortEdge);
            I("Width", (int)Width); D("CustomWidth", CustomWidth);
            I("Height", (int)Height); D("HeightValue", HeightValue); D("HeightOffset", HeightOffset);
            D("Clearance", Clearance); B("SlotThrough", SlotThrough); D("SlotDepth", SlotDepth);
            B("Relief", Relief); D("ReliefRadius", ReliefRadius); B("ReliefAuto", ReliefAuto); I("TubeWalls", TubeWalls); B("ClearanceAuto", ClearanceAuto);
            B("Chamfer", Chamfer); D("ChamferSize", ChamferSize);
            S("TabFaceKey", TabFaceKey); S("SlotFaceKey", SlotFaceKey); S("KeyContext", KeyContext);
            S("TabPartFile", TabPartFile); S("SlotPartFile", SlotPartFile);
            S("TabOccName", TabOccName); S("SlotOccName", SlotOccName);
            return sb.ToString();
        }

        public static TabSlotParams Parse(string text)
        {
            var map = new Dictionary<string, string>();
            foreach (string line in (text ?? "").Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i > 0) map[line.Substring(0, i)] = line.Substring(i + 1);
            }

            string Str(string k, string d) => map.TryGetValue(k, out var v) ? v : d;
            double Dbl(string k, double d) => map.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : d;
            int Int(string k, int d) => map.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : d;
            bool Bool(string k, bool d) => map.TryGetValue(k, out var v) ? v == "1" : d;

            var p = new TabSlotParams();
            p.Id = Str("Id", p.Id); p.Name = Str("Name", p.Name);
            p.TabLength = Dbl("TabLength", p.TabLength); p.Count = Int("Count", p.Count);
            p.Spacing = (SpacingMode)Int("Spacing", (int)p.Spacing);
            p.Pitch = Dbl("Pitch", p.Pitch); p.Offset = Dbl("Offset", p.Offset);
            p.CenterGroup = Bool("CenterGroup", p.CenterGroup);
            p.FlipStart = Bool("FlipStart", p.FlipStart);
            p.RunAlongShortEdge = Bool("RunAlongShortEdge", p.RunAlongShortEdge);
            p.Width = (WidthMode)Int("Width", (int)p.Width); p.CustomWidth = Dbl("CustomWidth", p.CustomWidth);
            p.Height = (HeightMode)Int("Height", (int)p.Height);
            p.HeightValue = Dbl("HeightValue", p.HeightValue); p.HeightOffset = Dbl("HeightOffset", p.HeightOffset);
            p.Clearance = Dbl("Clearance", p.Clearance);
            p.SlotThrough = Bool("SlotThrough", p.SlotThrough); p.SlotDepth = Dbl("SlotDepth", p.SlotDepth);
            p.Relief = Bool("Relief", p.Relief); p.ReliefRadius = Dbl("ReliefRadius", p.ReliefRadius);
            p.TubeWalls = Int("TubeWalls", 0);
            p.ClearanceAuto = Bool("ClearanceAuto", false);   // older sets keep their oversize
            p.ReliefAuto = Bool("ReliefAuto", false);  // sets saved before the table existed keep their radius
            p.Chamfer = Bool("Chamfer", p.Chamfer); p.ChamferSize = Dbl("ChamferSize", p.ChamferSize);
            p.TabFaceKey = Str("TabFaceKey", ""); p.SlotFaceKey = Str("SlotFaceKey", ""); p.KeyContext = Str("KeyContext", "");
            p.TabPartFile = Str("TabPartFile", ""); p.SlotPartFile = Str("SlotPartFile", "");
            p.TabOccName = Str("TabOccName", ""); p.SlotOccName = Str("SlotOccName", "");
            return p;
        }
    }
}
