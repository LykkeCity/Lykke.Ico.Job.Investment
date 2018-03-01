using System;
using Lykke.Ico.Core.Repositories.CampaignSettings;
using Lykke.Job.IcoInvestment.Core.Domain;
using Xunit;
using Lykke.Ico.Core.Repositories.Investor;

namespace Lykke.Job.IcoInvestment.Tests
{
    public class TokenPriceTests
    {
        CampaignSettings _campaignSettings = new CampaignSettings()
        {
            PreSaleStartDateTimeUtc = DateTime.Today.ToUniversalTime().AddDays(-15),
            PreSaleEndDateTimeUtc = DateTime.Today.ToUniversalTime(),
            CrowdSaleStartDateTimeUtc = DateTime.Today.ToUniversalTime(),
            CrowdSaleEndDateTimeUtc = DateTime.Today.ToUniversalTime().AddDays(21),
            TokenDecimals = 4,
            TokenBasePriceUsd = 1M
        };

        Investor _investor = new Investor()
        {
            ReferralCodeApplied = "TTT",
            ReferralsNumber = 0
        };

        [Fact]
        public void ShouldReturnZeroPriceIfOutOfDates()
        {
            // Arrange 
            var currentTotal = 1000M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.AddDays(-20).ToUniversalTime();

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Null(priceList);
        }

        [Fact]
        public void ShouldReturnPresalePriceOfBasePrice()
        {
            // Arrange 
            // - Presale tokens will be sold at 25% discount 
            var currentTotal = 1000M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.AddDays(-10).ToUniversalTime();

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Single(priceList);
            Assert.Equal(0.7500M, priceList[0].Price);
            Assert.Equal(1.3333M, priceList[0].Count);
        }

        [Fact]
        public void ShouldReturn75PercentOfBasePrice()
        {
            // Arrange 
            // - First 20,000,000 tokens will be sold at 25% discount 
            var currentTotal = 0M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime();

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Single(priceList);
            Assert.Equal(0.7500M, priceList[0].Price);
            Assert.Equal(1.3333M, priceList[0].Count);
        }

        [Fact]
        public void ShouldReturnTwoPricesIfCrossingAmountThreshold()
        {
            // Arrange 
            //  - First 20,000,000 tokens will be sold at 25% discount
            //  - Then, for the first 24 hours of the crowd sale, tokens will be sold at a 20% discount
            var currentTotal = 19_999_999M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime();

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Equal(2, priceList.Count);
            Assert.Equal(0.7500M, priceList[0].Price);
            Assert.Equal(1.0000M, priceList[0].Count);
            Assert.Equal(0.8000M, priceList[1].Price);
            Assert.Equal(0.3125M, priceList[1].Count);
        }

        [Fact]
        public void ShouldReturn80PercentOfBasePrice()
        {
            // Arrange
            //  - Then, for the first 24 hours of the crowd sale, tokens will be sold at a 20% discount
            var currentTotal = 20_000_000M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime();

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Single(priceList);
            Assert.Equal(0.8000M, priceList[0].Price);
            Assert.Equal(1.2500M, priceList[0].Count);
        }

        [Fact]
        public void ShouldReturn85PercentOfBasePrice()
        {
            // Arrange
            // - Thereafter and for one week, tokens will be sold at a 15% discount
            var currentTotal = 20_000_000M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime().AddDays(1);

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Single(priceList);
            Assert.Equal(0.8500M, priceList[0].Price);
            Assert.Equal(1.1764M, priceList[0].Count);
        }

        [Fact]
        public void ShouldReturn90PercentOfBasePrice()
        {
            // Arrange
            //  - The second week, tokens will be sold at a 10% discount
            var currentTotal = 20_000_000M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime().AddDays(7);

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);


            // Assert
            Assert.Single(priceList);
            Assert.Equal(0.9000M, priceList[0].Price);
            Assert.Equal(1.1111M, priceList[0].Count);
        }

        [Fact]
        public void ShouldReturnFullBasePrice()
        {
            // Arrange
            // - No discount will be granted the final week of the crowd sale
            var currentTotal = 20000000M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime().AddDays(14);

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);


            // Assert
            Assert.Single(priceList);
            Assert.Equal(1M, priceList[0].Price);
            Assert.Equal(1M, priceList[0].Count);
        }

        [Fact]
        public void ShouldReturnZeroCountButCorrectPriceIfAmountUsdIsZero()
        {
            // Arrange 
            var currentTotal = 0M;
            var amountUsd = 0M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime();

            // Act
            var priceList = TokenPrice.GetPriceList(_campaignSettings, _investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Single(priceList);
            Assert.Equal(0M, priceList[0].Count);
            Assert.Equal(0.75M, priceList[0].Price);
        }

        [Fact]
        public void ShouldUseRefferalDiscount()
        {
            // Arrange
            var currentTotal = 20_000_000M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime().AddDays(7);

            var settings = _campaignSettings.Clone();
            settings.EnableReferralProgram = true;
            settings.ReferralDiscount = 15;

            // Act
            var priceList = TokenPrice.GetPriceList(settings, _investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Single(priceList);
            Assert.Equal("ReferralDiscount", priceList[0].Phase);
            Assert.Equal(0.8500M, priceList[0].Price);
            Assert.Equal(1.1764M, priceList[0].Count);
        }

        [Fact]
        public void ShouldUseRefferalOwnerDiscount()
        {
            // Arrange
            var currentTotal = 20_000_000M;
            var amountUsd = 1M;
            var txDateTimeUtc = DateTime.Now.ToUniversalTime().AddDays(14);

            var settings = _campaignSettings.Clone();
            settings.EnableReferralProgram = true;
            settings.ReferralOwnerDiscount = 15;

            var investor = _investor.Clone();
            investor.ReferralsNumber = 1;
            investor.ReferralCodeApplied = "";

            // Act
            var priceList = TokenPrice.GetPriceList(settings, investor, txDateTimeUtc, amountUsd, currentTotal);

            // Assert
            Assert.Single(priceList);
            Assert.Equal("ReferralOwnerDiscount", priceList[0].Phase);
            Assert.Equal(0.8500M, priceList[0].Price);
            Assert.Equal(1.1764M, priceList[0].Count);
        }
    }
}
