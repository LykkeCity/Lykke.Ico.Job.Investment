using System;
using System.Collections.Generic;
using Lykke.Ico.Core.Repositories.CampaignSettings;
using Lykke.Ico.Core;
using Lykke.Job.IcoInvestment.Core.Helpers;
using Lykke.Ico.Core.Repositories.Investor;

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

        public static IList<TokenPrice> GetPriceList(ICampaignSettings settings, IInvestor investor,
            DateTime txDateTimeUtc, decimal amountUsd, decimal currentTotal)
        {
            var tokenInfo = settings.GetTokenInfo(currentTotal, txDateTimeUtc);
            if (tokenInfo == null)
            {
                return null;
            }
             
            var priceList = new List<TokenPrice>();
            var tokenPhase = Enum.GetName(typeof(TokenPricePhase), tokenInfo.Phase);
            var tokens = (amountUsd / tokenInfo.Price).RoundDown(settings.TokenDecimals);

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
                        var priceAbove = settings.GetTokenPrice(TokenPricePhase.CrowdSaleFirstDay);
                        var tokensAbove = (amountUsdAbove / priceAbove).RoundDown(settings.TokenDecimals);

                        priceList.Add(new TokenPrice(tokensAbove, priceAbove, nameof(TokenPricePhase.CrowdSaleFirstDay)));

                        return priceList;
                    }
                }
            }

            if (settings.EnableReferralProgram && 
                (tokenInfo.Phase == TokenPricePhase.CrowdSaleSecondWeek || 
                tokenInfo.Phase == TokenPricePhase.CrowdSaleLastWeek))
            {
                if (settings.ReferralDiscount.HasValue && !string.IsNullOrEmpty(investor.ReferralCodeApplied))
                {
                    var tokenPriceReferral = settings.TokenBasePriceUsd * ((100 - settings.ReferralDiscount.Value) / 100);
                    var takenPhaseReferral = "ReferralDiscount";
                    var tokensReferral = (amountUsd / tokenPriceReferral).RoundDown(settings.TokenDecimals);

                    priceList.Add(new TokenPrice(tokensReferral, tokenPriceReferral, takenPhaseReferral));

                    return priceList;
                }

                if (settings.ReferralOwnerDiscount.HasValue && investor.ReferralsNumber > 0)
                {
                    var tokenPriceReferral = settings.TokenBasePriceUsd * ((100 - settings.ReferralOwnerDiscount.Value) / 100);
                    var takenPhaseReferral = "ReferralOwnerDiscount";
                    var tokensReferral = (amountUsd / tokenPriceReferral).RoundDown(settings.TokenDecimals);

                    priceList.Add(new TokenPrice(tokensReferral, tokenPriceReferral, takenPhaseReferral));

                    return priceList;
                }
            }

            priceList.Add(new TokenPrice(tokens, tokenInfo.Price, tokenPhase));

            return priceList;
        }
    }
}
