using System;
using System.Globalization;
using System.Windows.Data;

namespace PLCSIM.UnitTest.View
{
    [ValueConversion(typeof(bool), typeof(bool))]
    public class InvertBoolConverter : ConverterMarkupExtension<InvertBoolConverter >
    {
        public InvertBoolConverter()
        {
        }

        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value != null && value is bool)
            {
                return !((bool)value);
            }

            return true;
        }

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Convert(value, targetType, parameter, culture);
        }
    }
}
