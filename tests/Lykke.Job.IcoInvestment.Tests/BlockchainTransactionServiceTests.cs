using System;
using System.Threading.Tasks;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Ico.Core.Repositories.CampaignInfo;
using Lykke.Ico.Core.Repositories.CryptoInvestment;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Domain.Transactions;
using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;
using Lykke.Job.IcoInvestment.Services;
using Lykke.Service.IcoExRate.Client;
using Lykke.Service.IcoExRate.Client.AutorestClient.Models;
using Moq;
using Xunit;

namespace Lykke.Job.IcoInvestment.Tests
{
    public class BlockchainTransactionServiceTests
    {
        private ILog _log;
        private Mock<IIcoExRateClient> _exRateClient;
        private Mock<IInvestorAttributeRepository> _investorAttributeRepository;
        private Mock<ICampaignInfoRepository> _campaignInfoRepository;
        private Mock<ICryptoInvestmentRepository> _cryptoInvestmentRepository;
        private Mock<IQueuePublisher<ProcessedTransactionMessage>> _processedTxSender;
        private IcoSettings _icoSettings;
        private decimal _usdAmount = decimal.Zero;

        private BlockchainTransactionService Init(string investorEmail = "test@test.test", double exchangeRate = 1.0)
        {
            _log = new LogToMemory();
            _icoSettings = new IcoSettings
            {
                CampaignStartDateTime = DateTime.Today,
                KycThreshold = decimal.MaxValue,
                BasePrice = decimal.One
            };

            _campaignInfoRepository = new Mock<ICampaignInfoRepository>();

            _campaignInfoRepository
                .Setup(m => m.IncrementValue(It.Is<CampaignInfoType>(t => t == CampaignInfoType.AmountInvestedUsd), It.IsAny<decimal>()))
                .Callback((CampaignInfoType t, decimal v) => _usdAmount += v)
                .Returns(() => Task.CompletedTask);

            _exRateClient = new Mock<IIcoExRateClient>();

            _exRateClient
                .Setup(m => m.GetAverageRate(It.IsAny<Pair>(), It.IsAny<DateTime>()))
                .Returns(() => Task.FromResult(new AverageRateResponse { AverageRate = exchangeRate }));

            _investorAttributeRepository = new Mock<IInvestorAttributeRepository>();

            _investorAttributeRepository
                .Setup(m => m.GetInvestorEmailAsync(
                    It.IsIn(new InvestorAttributeType[] { InvestorAttributeType.PayInBtcAddress, InvestorAttributeType.PayInEthAddress }), 
                    It.IsAny<string>()))
                .Returns(() => Task.FromResult(investorEmail));

            _cryptoInvestmentRepository = new Mock<ICryptoInvestmentRepository>();

            _cryptoInvestmentRepository
                .Setup(m => m.SaveAsync(It.IsAny<ICryptoInvestment>()))
                .Returns(() => Task.CompletedTask);

            _processedTxSender = new Mock<IQueuePublisher<ProcessedTransactionMessage>>();

            _processedTxSender
                .Setup(m => m.SendAsync(It.IsAny<ProcessedTransactionMessage>()))
                .Returns(() => Task.CompletedTask);

            return new BlockchainTransactionService(
                _log,
                _exRateClient.Object,
                _investorAttributeRepository.Object,
                _campaignInfoRepository.Object,
                _cryptoInvestmentRepository.Object,
                _processedTxSender.Object,
                _icoSettings);
        }

        [Fact]
        public async void ShouldProcessMessage()
        {
            // Arrange
            var testExchangeRate = 2M;
            var testAmount = 1M;
            var testAmountUsd = testAmount * testExchangeRate;
            var testBlockId = "testBlock";
            var testBlockTimestamp = DateTimeOffset.Now;
            var testAddress = "testAddress";
            var testTransactionId = "testTransaction";
            var testEmail = "test@test.test";
            var testLink = "testLink";
            var testCurrency = CurrencyType.Bitcoin;
            var svc = Init(testEmail, Decimal.ToDouble(testExchangeRate));

            // Act
            await svc.Process(new BlockchainTransactionMessage
            {
                Amount = testAmount,
                BlockId = testBlockId,
                BlockTimestamp = testBlockTimestamp,
                CurrencyType = testCurrency,
                DestinationAddress = testAddress,
                Link = testLink,
                TransactionId = testTransactionId
            });

            // Assert

            // History saved
            _cryptoInvestmentRepository.Verify(m => m.SaveAsync(It.IsAny<ICryptoInvestment>()));

            // Processed tx message sent
            _processedTxSender.Verify(m => m.SendAsync(It.Is<ProcessedTransactionMessage>(msg =>
                msg.InvestorEmail == testEmail && 
                msg.TransactionId == testTransactionId)));

            // Total amount incremented
            _campaignInfoRepository.Verify(m => m.IncrementValue(
                It.Is<CampaignInfoType>(v => v == CampaignInfoType.AmountInvestedUsd),
                It.Is<decimal>(v => v == testAmountUsd)));

            Assert.Equal(testAmountUsd, _usdAmount);
        }

        [Fact]
        public async void ShouldDiscardMessage()
        {
            // Arrange
            var svc = Init(null);

            // Act
            await svc.Process(new BlockchainTransactionMessage
            {
                Amount = 0M,
                BlockId = "",
                BlockTimestamp = DateTimeOffset.MinValue,
                CurrencyType = CurrencyType.Bitcoin,
                DestinationAddress = "test@test.test",
                Link = "",
                TransactionId = ""
            });

            // Assert

            // History not saved
            _cryptoInvestmentRepository.Verify(
                m => m.SaveAsync(It.IsAny<ICryptoInvestment>()), 
                Times.Never);

            // Processed tx message not sent
            _processedTxSender.Verify(m => m.SendAsync(null), Times.Never);

            // Total amount not incremented
            _campaignInfoRepository.Verify(m => m.IncrementValue(It.IsAny<CampaignInfoType>(), It.IsAny<decimal>()), Times.Never);

            Assert.Equal(0M, _usdAmount);
        }
    }
}
