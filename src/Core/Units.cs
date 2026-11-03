using System;
using Inventor;

namespace TabSlotAddIn.Core
{
    /// <summary>
    /// Converts between the text a user types (expressions, units, document default units) and
    /// Inventor's internal centimetres.
    /// </summary>
    internal sealed class Units
    {
        private readonly UnitsOfMeasure _uom;

        public Units(Document doc) { _uom = doc.UnitsOfMeasure; }

        public string Format(double cm) =>
            _uom.GetStringFromValue(cm, UnitsTypeEnum.kDefaultDisplayLengthUnits);

        /// <summary>Parses "12", "12 mm", "2*6" ... Unitless input is read in the document's length units.</summary>
        public bool TryParse(string text, out double cm)
        {
            cm = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                // The result is always in database units (cm); the second argument only decides which unit
                // plain numbers such as "20" are read in, so it must be the document's length units.
                cm = Convert.ToDouble(_uom.GetValueFromExpression(text.Trim(), _uom.LengthUnits));
                return !double.IsNaN(cm) && !double.IsInfinity(cm);
            }
            catch
            {
                return false;
            }
        }
    }
}
