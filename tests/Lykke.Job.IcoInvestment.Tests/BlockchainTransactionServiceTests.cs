using System;
using System.Threading.Tasks;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Ico.Core.Repositories.CampaignInfo;
using Lykke.Ico.Core.Repositories.Investor;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;
using Lykke.Job.IcoInvestment.Services;
using Lykke.Service.IcoExRate.Client;
using Lykke.Service.IcoExRate.Client.AutorestClient.Models;
using Moq;
using Xunit;
using Lykke.Job.IcoInvestment.Core.Domain;
using Lykke.Job.IcoInvestment.Core.Domain.CryptoInvestments;
using Lykke.Ico.Core.Repositories.InvestorTransaction;
using Lykke.Ico.Core.Repositories.CampaignSettings;

namespace Lykke.Job.IcoInvestment.Tests
{
    public class BlockchainTransactionServiceTests
    {
        private ILog _log;
        private Mock<IIcoExRateClient> _exRateClient;
        private Mock<IInvestorAttributeRepository> _investorAttributeRepository;
        private Mock<ICampaignInfoRepository> _campaignInfoRepository;
        private Mock<ICampaignSettingsRepository> _campaignSettingsRepository;
        private Mock<IInvestorTransactionRepository> _investorTransactionRepository;
        private Mock<IInvestorRepository> _investorRepository;
        private Mock<IQueuePublisher<InvestorNewTransactionMessage>> _investmentMailSender;
        private Mock<IQueuePublisher<InvestorKycRequestMessage>> _kycMailSender;
        private Mock<IQueuePublisher<InvestorNeedMoreInvestmentMessage>> _needMoreInfoMailSender;
        private ICampaignSettings _campaignSettings;
        private IInvestor _investor;
        private IInvestorTransaction _investorTransaction;
        private decimal _usdAmount = decimal.Zero;

        private TransactionService Init(string investorEmail = "test@test.test", double exchangeRate = 1.0)
        {
            _log = new LogToMemory();

            _campaignInfoRepository = new Mock<ICampaignInfoRepository>();

            _campaignInfoRepository
                .Setup(m => m.IncrementValue(It.Is<CampaignInfoType>(t => t == CampaignInfoType.AmountInvestedUsd), It.IsAny<decimal>()))
                .Callback((CampaignInfoType t, decimal v) => _usdAmount += v)
                .Returns(() => Task.CompletedTask);

            _campaignSettings = new CampaignSettings
            {
                StartDateTimeUtc = DateTime.UtcNow.AddDays(-1),
                EndDateTimeUtc = DateTime.UtcNow.AddDays(1),
                TokenDecimals = 4,
                MinInvestAmountUsd = 1000,
                TokenBasePriceUsd = 0.064M,
                TotalTokensAmount = 1000000000
            };

            _campaignSettingsRepository = new Mock<ICampaignSettingsRepository>();

            _campaignSettingsRepository
                .Setup(m => m.GetAsync())
                .Returns(() => Task.FromResult(_campaignSettings));

            _exRateClient = new Mock<IIcoExRateClient>();

            _exRateClient
                .Setup(m => m.GetAverageRate(It.IsAny<Pair>(), It.IsAny<DateTime>()))
                .Returns(() => Task.FromResult(new AverageRateResponse { AverageRate = exchangeRate }));

            _investor = new Investor() { Email = investorEmail };         

            _investorRepository = new Mock<IInvestorRepository>();

            _investorRepository
                .Setup(m => m.GetAsync(It.Is<string>(v => !string.IsNullOrWhiteSpace(v) && v == investorEmail)))
                .Returns(() => Task.FromResult(_investor));

            _investorAttributeRepository = new Mock<IInvestorAttributeRepository>();

            _investorAttributeRepository
                .Setup(m => m.GetInvestorEmailAsync(
                    It.IsIn(new InvestorAttributeType[] { InvestorAttributeType.PayInBtcAddress, InvestorAttributeType.PayInEthAddress }), 
                    It.IsAny<string>()))
                .Returns(() => Task.FromResult(investorEmail));

            _investorTransaction = new InvestorTransaction { };

            _investorTransactionRepository = new Mock<IInvestorTransactionRepository>();

            _investorTransactionRepository
                .Setup(m => m.GetAsync(
                    It.Is<string>(v => !string.IsNullOrWhiteSpace(v) && v == "test-1@test.test"),
                    It.IsAny<string>()))
                .Returns(() => Task.FromResult(_investorTransaction));

            _investorTransactionRepository
                .Setup(m => m.SaveAsync(It.IsAny<IInvestorTransaction>()))
                .Returns(() => Task.CompletedTask);

            _investmentMailSender = new Mock<IQueuePublisher<InvestorNewTransactionMessage>>();

            _investmentMailSender
                .Setup(m => m.SendAsync(It.IsAny<InvestorNewTransactionMessage>()))
                .Returns(() => Task.CompletedTask);

            _kycMailSender = new Mock<IQueuePublisher<InvestorKycRequestMessage>>();

            _kycMailSender
                .Setup(m => m.SendAsync(It.IsAny<InvestorKycRequestMessage>()))
                .Returns(() => Task.CompletedTask);

            _needMoreInfoMailSender = new Mock<IQueuePublisher<InvestorNeedMoreInvestmentMessage>>();

            _needMoreInfoMailSender
                .Setup(m => m.SendAsync(It.IsAny<InvestorNeedMoreInvestmentMessage>()))
                .Returns(() => Task.CompletedTask);

            return new TransactionService(
                _log,
                _exRateClient.Object,
                _investorAttributeRepository.Object,
                _campaignInfoRepository.Object,
                _campaignSettingsRepository.Object,
                _investorTransactionRepository.Object,
                _investorRepository.Object,
                _investmentMailSender.Object,
                _kycMailSender.Object,
                _needMoreInfoMailSender.Object);
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
            var testTransactionId = "testTransaction-1";
            var uniqueId = "testTransaction";
            var testEmail = "test@test.test";
            var testLink = "testLink";
            var testCurrency = CurrencyType.Bitcoin;
            var svc = Init(testEmail, Decimal.ToDouble(testExchangeRate));

            // Act
            await svc.Process(new TransactionMessage
            {
                Email = testEmail,
                Amount = testAmount,
                BlockId = testBlockId,
                CreatedUtc = testBlockTimestamp.UtcDateTime,
                Currency = testCurrency,
                PayInAddress = testAddress,
                Link = testLink,
                TransactionId = testTransactionId,
                UniqueId = uniqueId
            });

            // Assert

            // History saved
            _investorTransactionRepository.Verify(m => m.SaveAsync(It.IsAny<IInvestorTransaction>()));

            // Mail sent
            _investmentMailSender.Verify(m => m.SendAsync(It.Is<InvestorNewTransactionMessage>(msg => msg.EmailTo == testEmail)));

            // Total amount incremented
            _campaignInfoRepository.Verify(m => m.IncrementValue(
                It.Is<CampaignInfoType>(v => v == CampaignInfoType.AmountInvestedUsd),
                It.Is<decimal>(v => v == testAmountUsd)));

            Assert.Equal(testAmountUsd, _usdAmount);
        }

        // TODO: should also discard when terms violated or exchange rate not found
        [Fact]
        public void ShouldDiscardMessage()
        {
            // Arrange
            var svc = Init(null);
            var message = new TransactionMessage
            {
                Amount = 0M,
                BlockId = "",
                CreatedUtc = DateTimeOffset.MinValue.UtcDateTime,
                Currency = CurrencyType.Bitcoin,
                PayInAddress = "test@test.test",
                Link = "",
                TransactionId = "",
                UniqueId = ""
            };

            // Act
            Assert.Throws<AggregateException>(() => svc.Process(message).Wait());
        }
    }
}
