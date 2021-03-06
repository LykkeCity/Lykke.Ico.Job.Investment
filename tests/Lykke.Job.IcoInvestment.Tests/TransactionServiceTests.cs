﻿using Moq;
using Xunit;
using System;
using System.Threading.Tasks;
using Common;
using Common.Log;
using Lykke.Ico.Core.Services;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Ico.Core.Repositories.CampaignInfo;
using Lykke.Ico.Core.Repositories.Investor;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Services;
using Lykke.Service.IcoExRate.Client;
using Lykke.Service.IcoExRate.Client.AutorestClient.Models;
using Lykke.Job.IcoInvestment.Core.Domain;
using Lykke.Job.IcoInvestment.Core.Domain.CryptoInvestments;
using Lykke.Ico.Core.Repositories.InvestorTransaction;
using Lykke.Ico.Core.Repositories.CampaignSettings;
using Lykke.Ico.Core.Repositories.InvestorRefund;
using Lykke.Ico.Core.Repositories.PrivateInvestorAttribute;

namespace Lykke.Job.IcoInvestment.Tests
{
    public class TransactionServiceTests
    {
        private ILog _log;
        private Mock<IIcoExRateClient> _exRateClient;
        private Mock<IInvestorAttributeRepository> _investorAttributeRepository;
        private Mock<IPrivateInvestorAttributeRepository> _privateInvestorAttributeRepository;
        private Mock<ICampaignInfoRepository> _campaignInfoRepository;
        private Mock<ICampaignSettingsRepository> _campaignSettingsRepository;
        private Mock<IInvestorTransactionRepository> _investorTransactionRepository;
        private Mock<IInvestorRefundRepository> _investorRefundRepository;
        private Mock<IInvestorRepository> _investorRepository;
        private Mock<IQueuePublisher<InvestorNewTransactionMessage>> _investmentMailSender;
        private ICampaignSettings _campaignSettings;
        private IInvestor _investor;
        private IInvestorTransaction _investorTransaction;
        private IUrlEncryptionService _urlEncryptionService;
        private IKycService _kycService;
        private IReferralCodeService _referralCodeService;
        private decimal _usdAmount = decimal.Zero;

        private TransactionService Init(string investorEmail = "test@test.test", double exchangeRate = 1.0)
        {
            _log = new LogToMemory();

            _campaignInfoRepository = new Mock<ICampaignInfoRepository>();

            _campaignInfoRepository
                .Setup(m => m.IncrementValue(It.Is<CampaignInfoType>(t => t == CampaignInfoType.AmountInvestedUsd), It.IsAny<decimal>()))
                .Callback((CampaignInfoType t, decimal v) => _usdAmount += v)
                .Returns(() => Task.CompletedTask);

            _campaignInfoRepository
                .Setup(m => m.SaveLatestTransactionsAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => Task.CompletedTask);

            _campaignSettings = new CampaignSettings
            {
                PreSaleStartDateTimeUtc = DateTime.UtcNow.AddDays(-15),
                PreSaleEndDateTimeUtc = DateTime.UtcNow,
                PreSaleTotalTokensAmount = 100000000,
                CrowdSaleStartDateTimeUtc = DateTime.UtcNow,
                CrowdSaleEndDateTimeUtc = DateTime.UtcNow.AddDays(21),
                CrowdSaleTotalTokensAmount = 360000000,
                TokenDecimals = 4,
                MinInvestAmountUsd = 1000,
                TokenBasePriceUsd = 0.064M,
                HardCapUsd = 1000000,
                EnableReferralProgram = true,
                ReferralCodeLength = 6
            };

            _campaignSettingsRepository = new Mock<ICampaignSettingsRepository>();

            _campaignSettingsRepository
                .Setup(m => m.GetAsync())
                .Returns(() => Task.FromResult(_campaignSettings));

            _exRateClient = new Mock<IIcoExRateClient>();

            _exRateClient
                .Setup(m => m.GetAverageRate(It.IsAny<Pair>(), It.IsAny<DateTime>()))
                .Returns(() => Task.FromResult(new AverageRateResponse { AverageRate = exchangeRate }));

            _investor = new Investor()
            {
                Email = investorEmail,
                ConfirmationToken = Guid.NewGuid(),
                AmountUsd = _campaignSettings.MinInvestAmountUsd + 10
            };         

            _investorRepository = new Mock<IInvestorRepository>();

            _investorRepository
                .Setup(m => m.GetAsync(It.Is<string>(v => !string.IsNullOrWhiteSpace(v) && v == investorEmail)))
                .Returns(() => Task.FromResult(_investor));

            _investorRepository
                .Setup(m => m.SaveReferralCode(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => Task.CompletedTask);

            _investorAttributeRepository = new Mock<IInvestorAttributeRepository>();

            _investorAttributeRepository
                .Setup(m => m.SaveAsync(It.IsAny<InvestorAttributeType>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => Task.CompletedTask);

            _investorAttributeRepository
                .Setup(m => m.GetInvestorEmailAsync(
                    It.IsIn(new InvestorAttributeType[] { InvestorAttributeType.PayInBtcAddress, InvestorAttributeType.PayInEthAddress }), 
                    It.IsAny<string>()))
                .Returns(() => Task.FromResult(investorEmail));

            _investorAttributeRepository
                .Setup(m => m.GetInvestorEmailAsync(
                    It.IsIn(new InvestorAttributeType[] { InvestorAttributeType.ReferralCode }),
                    It.IsAny<string>()))
                .Returns(() => Task.FromResult(""));

            _privateInvestorAttributeRepository = new Mock<IPrivateInvestorAttributeRepository>();

            _privateInvestorAttributeRepository
                .Setup(m => m.GetInvestorEmailAsync(
                    It.IsIn(new PrivateInvestorAttributeType[] { PrivateInvestorAttributeType.ReferralCode }),
                    It.IsAny<string>()))
                .Returns(() => Task.FromResult(""));

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

            _investorRefundRepository = new Mock<IInvestorRefundRepository>();

            _investorRefundRepository
                .Setup(m => m.SaveAsync(It.IsAny<string>(), It.IsAny<InvestorRefundReason>(), It.IsAny<string>()))
                .Returns(() => Task.CompletedTask);

            _investmentMailSender = new Mock<IQueuePublisher<InvestorNewTransactionMessage>>();

            _investmentMailSender
                .Setup(m => m.SendAsync(It.IsAny<InvestorNewTransactionMessage>()))
                .Returns(() => Task.CompletedTask);

            _urlEncryptionService = new UrlEncryptionService("E546C8DF278CD5931069B522E695D4F2", "1234567890123456");
            _kycService = new KycService(_campaignSettingsRepository.Object, _urlEncryptionService);

            _referralCodeService = new ReferralCodeService(_investorAttributeRepository.Object,
                _privateInvestorAttributeRepository.Object);

            return new TransactionService(
                _log,
                _exRateClient.Object,
                _investorAttributeRepository.Object,
                _campaignInfoRepository.Object,
                _campaignSettingsRepository.Object,
                _investorTransactionRepository.Object,
                _investorRefundRepository.Object,
                _investorRepository.Object,
                _investmentMailSender.Object,
                _kycService,
                _referralCodeService,
                "http://test-ito.valid.global/summary/{token}/overview");
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

            _investorRepository.Verify(m => m.SaveReferralCode(It.IsAny<string>(), It.IsAny<string>()));
            _investorAttributeRepository.Verify(m => m.SaveAsync(
                It.Is<InvestorAttributeType>(f => f == InvestorAttributeType.ReferralCode), 
                It.IsAny<string>(), 
                It.IsAny<string>()));

            Assert.Equal(testAmountUsd, _usdAmount);
        }

        [Fact]
        public void ShouldThrowExceptionWhenCampainSettingsNull()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "test@test.test",
                CreatedUtc = DateTime.Now.ToUniversalTime(),
                UniqueId = "111"
            };

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            _campaignSettingsRepository
                .Setup(m => m.GetAsync())
                .Returns(() => Task.FromResult<ICampaignSettings>(null));

            // Act
            var ex = Assert.Throws<AggregateException>(() => svc.Process(message).Wait());

            Assert.Contains("Campaign settings", ex.Message);
        }

        [Fact]
        public async void ShouldDoIgnoreAlreadySavedTranscation()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "test@test.test",
                CreatedUtc = DateTime.Now.ToUniversalTime(),
                UniqueId = "111"
            };

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            _investorTransactionRepository
                .Setup(m => m.GetAsync(
                    It.Is<string>(v => v == message.Email),
                    It.Is<string>(v => v == message.UniqueId)))
                .Returns(() => Task.FromResult(_investorTransaction));            

            // Act
            await svc.Process(message);

            // Assert
            _investorTransactionRepository.Verify(m => m.GetAsync(
                It.IsAny<string>(),
                It.IsAny<string>()));
        }

        [Fact]
        public void ShouldThrowExceptionWhenInvestorNotFound()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "test@test.test",
                CreatedUtc = DateTime.Now.ToUniversalTime(),
                UniqueId = "111"
            };

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            _investorRepository
                .Setup(m => m.GetAsync(It.Is<string>(v => v == message.Email)))
                .Returns(() => Task.FromResult<IInvestor>(null));

            // Act
            var ex = Assert.Throws<AggregateException>(() => svc.Process(message).Wait());

            Assert.Contains("Investor with email", ex.Message);
        }

        [Fact]
        public void ShouldThrowExceptionWhenUniqueIdIsEmpty()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "test@test.test",
                CreatedUtc = DateTime.Now.ToUniversalTime(),
                UniqueId = ""
            };

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            _investorRepository
                .Setup(m => m.GetAsync(It.Is<string>(v => v == message.Email)))
                .Returns(() => Task.FromResult<IInvestor>(null));

            // Act
            var ex = Assert.Throws<AggregateException>(() => svc.Process(message).Wait());

            Assert.Contains("UniqueId can not be empty", ex.Message);
        }

        [Fact]
        public void ShouldThrowExceptionWhenEmailIsEmpty()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "",
                CreatedUtc = DateTime.Now.ToUniversalTime(),
                UniqueId = "111"
            };

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            _investorRepository
                .Setup(m => m.GetAsync(It.Is<string>(v => v == message.Email)))
                .Returns(() => Task.FromResult<IInvestor>(null));

            // Act
            var ex = Assert.Throws<AggregateException>(() => svc.Process(message).Wait());

            Assert.Contains("Email can not be empty", ex.Message);
        }

        [Fact]
        public async void ShouldThrowExceptionWhenTxOutOfDates()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "test@test.test",
                CreatedUtc = DateTime.Now.AddDays(-20).ToUniversalTime(),
                UniqueId = "111"
            };

            var messageJson = message.ToJson();

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            // Act
            await svc.Process(message);

            // Assert
            _investorRefundRepository.Verify(m => m.SaveAsync(
                It.Is<string>(v => v == message.Email),
                It.Is<InvestorRefundReason>(v => v == InvestorRefundReason.OutOfDates),
                It.Is<string>(v => v == messageJson)));
        }

        [Fact]
        public async void ShouldThrowExceptionWhenAllPresalesTokensSoldOut()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "test@test.test",
                CreatedUtc = DateTime.Now.AddDays(-10).ToUniversalTime(),
                UniqueId = "111"
            };

            var messageJson = message.ToJson();

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            _campaignInfoRepository
                .Setup(m => m.GetValueAsync(It.Is<CampaignInfoType>(t => t == CampaignInfoType.AmountInvestedToken)))
                .Returns(() => Task.FromResult("200000000"));

            // Act
            await svc.Process(message);

            // Assert
            _investorRefundRepository.Verify(m => m.SaveAsync(
                It.Is<string>(v => v == message.Email),
                It.Is<InvestorRefundReason>(v => v == InvestorRefundReason.PreSaleTokensSoldOut),
                It.Is<string>(v => v == messageJson)));
        }

        [Fact]
        public async void ShouldThrowExceptionWhenAllTokensSoldOut()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "test@test.test",
                CreatedUtc = DateTime.Now.AddDays(10).ToUniversalTime(),
                UniqueId = "111"
            };

            var messageJson = message.ToJson();

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            _campaignInfoRepository
                .Setup(m => m.GetValueAsync(It.Is<CampaignInfoType>(t => t == CampaignInfoType.AmountInvestedToken)))
                .Returns(() => Task.FromResult("600000000"));

            await svc.Process(message);

            // Assert
            _investorRefundRepository.Verify(m => m.SaveAsync(
                It.Is<string>(v => v == message.Email),
                It.Is<InvestorRefundReason>(v => v == InvestorRefundReason.TokensSoldOut),
                It.Is<string>(v => v == messageJson)));
        }

        [Fact]
        public async void ShouldThrowExceptionWhenHardCapUsdExceeded()
        {
            // Arrange
            var message = new TransactionMessage
            {
                Email = "test@test.test",
                CreatedUtc = DateTime.Now.AddDays(10).ToUniversalTime(),
                UniqueId = "111"
            };

            var messageJson = message.ToJson();

            var svc = Init(message.Email, Decimal.ToDouble(1M));

            _campaignInfoRepository
                .Setup(m => m.GetValueAsync(It.Is<CampaignInfoType>(t => t == CampaignInfoType.AmountInvestedUsd)))
                .Returns(() => Task.FromResult("1000001"));

            await svc.Process(message);

            // Assert
            _investorRefundRepository.Verify(m => m.SaveAsync(
                It.Is<string>(v => v == message.Email),
                It.Is<InvestorRefundReason>(v => v == InvestorRefundReason.HardCapUsdExceeded),
                It.Is<string>(v => v == messageJson)));
        }
    }
}
