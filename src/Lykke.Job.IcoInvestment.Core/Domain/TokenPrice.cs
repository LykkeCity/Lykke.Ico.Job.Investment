using System;
using System.Collections.Generic;
using Lykke.Ico.Core.Repositories.CampaignSettings;
using Lykke.Job.IcoInvestment.Core.Extensions;

namespace Lykke.Job.IcoInvestment.Core.Domain
{
    public class TokenPrice
    {
        public TokenPrice(decimal count, decimal price, string Phase)
        {
            Count = count;
            Price = price;
        }

        public decimal Count { get; }
        public decimal Price { get; }
        public string Phase { get; }

        public static IList<TokenPrice> GetPriceList(ICampaignSettings campaignSettings, DateTime txDateTimeUtc,
            decimal amountUsd, decimal currentTotal)
        {
            var isPreSale = campaignSettings.IsPreSale(txDateTimeUtc);
            var isIsCrowdSale = campaignSettings.IsCrowdSale(txDateTimeUtc);

            if (isPreSale)
            {
                var priceList = new List<TokenPrice>();
                var price = campaignSettings.TokenBasePriceUsd * 0.75M;
                var count = DecimalExtensions.RoundDown(amountUsd / price, campaignSettings.TokenDecimals);

                priceList.Add(new TokenPrice(count, price, "PreSale"));

                return priceList;
            }

            if (isIsCrowdSale)
            {
                var priceList = new List<TokenPrice>();
                var countDown = 20_000_000M - currentTotal;

                TokenPrice priceByDate(decimal amount)
                {
                    var price = 0M;
                    var phase = "CrowdSale";

                    var timeSpan = txDateTimeUtc - campaignSettings.CrowdSaleStartDateTimeUtc;

                    if (timeSpan < TimeSpan.FromDays(1))
                    {
                        price = campaignSettings.TokenBasePriceUsd * 0.80M;
                        phase = $"{phase}-FirstDay";
                    }
                    else if (timeSpan < TimeSpan.FromDays(7))
                    {
                        price = campaignSettings.TokenBasePriceUsd * 0.85M;
                        phase = $"{phase}-FirstWeek";
                    }
                    else if (timeSpan < TimeSpan.FromDays(7 * 2))
                    {
                        price = campaignSettings.TokenBasePriceUsd * 0.90M;
                        phase = $"{phase}-SeckondWeek";
                    }
                    else
                    {
                        price = campaignSettings.TokenBasePriceUsd;
                        phase = $"{phase}-FinalWeek";
                    }

                    var count = DecimalExtensions.RoundDown(amount / price, campaignSettings.TokenDecimals);

                    return new TokenPrice(count, price, phase);
                };

                if (countDown > 0M)
                {
                    var price = campaignSettings.TokenBasePriceUsd * 0.75M;
                    var count = DecimalExtensions.RoundDown(amountUsd / price, campaignSettings.TokenDecimals);
                    var phase = "CrowdSale-First-20_000_000";

                    if (count > countDown)
                    {
                        count = countDown; // count of tokens below threshold
                        amountUsd -= countDown * price; // rest of amount after purchasing tokens below threshold

                        priceList.Add(new TokenPrice(count, price, phase)); // price and count of tokens below threshold
                        priceList.Add(priceByDate(amountUsd)); // price and count of tokens above threshold
                    }
                    else
                    {
                        priceList.Add(new TokenPrice(count, price, phase)); // the whole purchase is below threshold
                    }
                }
                else
                {
                    priceList.Add(priceByDate(amountUsd));
                }

                return priceList;
            }

            return null;
        }
    }
}
