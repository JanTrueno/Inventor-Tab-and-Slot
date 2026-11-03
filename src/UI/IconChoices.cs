namespace TabSlotAddIn.UI
{
    /// <summary>Which Inventor command's icon each control uses; empty means the add-in's own icon.</summary>
    internal static class IconChoices
    {
        public static string Command(string slot)
        {
            switch (slot)
            {
                case "Ribbon": return "SheetMetalFaceCmd";
                case "SpacingEqual": return "PartRectangularPatternCmd";
                case "SpacingPitch": return "SketchGeneralDimensionCmd";
                case "Edit": return "PartDirectCmd";
                default: return "";
            }
        }
    }
}
