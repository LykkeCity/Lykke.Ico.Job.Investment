using System;
using System.Collections.Generic;
using Lykke.Ico.Core.Repositories.CampaignSettings;
using Lykke.Ico.Core;
using Lykke.Job.IcoInvestment.Core.Helpers;

namespace Lykke.Job.IcoInvestment.Core.Domain
{
    public class TokenPrice
    {
        public TokenPrice(decimal count, decimal price, string phase)
        {
            Count = count;
            Price = price;
            Phase = phase;
        }

        public decimal Count { get; }
        public decimal Price { get; }
        public string Phase { get; }

        public static IList<TokenPrice> GetPriceList(ICampaignSettings campaignSettings, DateTime txDateTimeUtc,
            decimal amountUsd, decimal currentTotal)
        {
            var tokenInfo = campaignSettings.GetTokenInfo(currentTotal, txDateTimeUtc);
            if (tokenInfo == null)
            {
                return null;
            }

            var priceList = new List<TokenPrice>();
            var tokenPhase = Enum.GetName(typeof(TokenPricePhase), tokenInfo.Phase);
            var tokens = (amountUsd / tokenInfo.Price).RoundDown(campaignSettings.TokenDecimals);

            if (tokenInfo.Phase == TokenPricePhase.CrowdSaleInitial)
            {
                var tokensBelow = Consts.CrowdSale.InitialAmount - currentTotal;
                if (tokensBelow > 0M)
                {
                    if (tokens > tokensBelow)
                    {
                        // tokens below threshold
                        priceList.Add(new TokenPrice(tokensBelow, tokenInfo.Price, tokenPhase)); 

                        // tokens above threshold
                        var amountUsdAbove = amountUsd - (tokensBelow * tokenInfo.Price);
                        var priceAbove = campaignSettings.GetTokenPrice(TokenPricePhase.CrowdSaleFirstDay);
                        var tokensAbove = (amountUsdAbove / priceAbove).RoundDown(campaignSettings.TokenDecimals);

                        priceList.Add(new TokenPrice(tokensAbove, priceAbove, nameof(TokenPricePhase.CrowdSaleFirstDay)));

                        return priceList;
                    }
                }
            }

            priceList.Add(new TokenPrice(tokens, tokenInfo.Price, tokenPhase));

            return priceList;
        }
    }
}
