namespace System
{
    public static class DecimalExtensions
    {
        public static decimal RoundDown(this decimal self, double decimalPlaces)
        {
            var power = Convert.ToDecimal(Math.Pow(10, decimalPlaces));
            var value = Math.Floor(self * power) / power;

            return value;
        }
    }
}
