using System;
using System.Collections.Generic;
using System.Text;
using Lykke.Ico.Core.Repositories.CampaignSettings;

namespace Lykke.Job.IcoInvestment.Core.Domain
{
    public class TokenPrice
    {
        public TokenPrice(decimal count, decimal price)
        {
            Count = count;
            Price = price;
        }

        public decimal Count { get; }
        public decimal Price { get; }

        public static IList<TokenPrice> GetPriceList(ICampaignSettings campaignSettings, DateTime txDateTimeUtc, decimal amountUsd, decimal currentTotal)
        {
            if (campaignSettings.StartDateTimeUtc > txDateTimeUtc || 
                campaignSettings.EndDateTimeUtc < txDateTimeUtc ||
                campaignSettings.TotalTokensAmount <= currentTotal)
            {
                return null;
            }

            var countDown = 20_000_000M - currentTotal;
            var priceList = new List<TokenPrice>();

            TokenPrice priceByDate(decimal amount)
            {
                var price = 0M;
                var timeSpan = txDateTimeUtc - campaignSettings.StartDateTimeUtc;

                if (timeSpan < TimeSpan.FromDays(1))
                    price = campaignSettings.TokenBasePriceUsd * 0.80M;
                else if (timeSpan < TimeSpan.FromDays(7))
                    price = campaignSettings.TokenBasePriceUsd * 0.85M;
                else if (timeSpan < TimeSpan.FromDays(7 * 2))
                    price = campaignSettings.TokenBasePriceUsd * 0.90M;
                else
                    price = campaignSettings.TokenBasePriceUsd;;

                return new TokenPrice(
                    DecimalExtensions.RoundDown(amount / price, campaignSettings.TokenDecimals),
                    price);
            };

            if (countDown > 0M)
            {
                var price = campaignSettings.TokenBasePriceUsd * 0.75M;
                var count = DecimalExtensions.RoundDown(amountUsd / price, campaignSettings.TokenDecimals);

                if (count > countDown)
                {
                    count = countDown; // count of tokens below threshold
                    amountUsd -= countDown * price; // rest of amount after purchasing tokens below threshold
                    
                    priceList.Add(new TokenPrice(count, price)); // price and count of tokens below threshold
                    priceList.Add(priceByDate(amountUsd)); // price and count of tokens above threshold
                }
                else
                {
                    priceList.Add(new TokenPrice(count, price)); // the whole purchase is below threshold
                }
            }
            else
            {
                priceList.Add(priceByDate(amountUsd));
            }

            return priceList;
        }
    }
}
