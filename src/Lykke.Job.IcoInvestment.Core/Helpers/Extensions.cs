using Lykke.Ico.Core;
using System;

namespace Lykke.Job.IcoInvestment.Core.Helpers
{
    public static class Extensions
    {
        public static decimal RoundDown(this decimal self, double decimalPlaces)
        {
            var power = Convert.ToDecimal(Math.Pow(10, decimalPlaces));
            var value = Math.Floor(self * power) / power;

            return value;
        }

        public static string ToAssetName(this CurrencyType self)
        {
            switch (self)
            {
                case CurrencyType.Bitcoin:
                    return "BTC";
                case CurrencyType.Ether:
                    return "ETH";
                case CurrencyType.Fiat:
                    return "USD";
            }

            return "";
        }
    }
}
