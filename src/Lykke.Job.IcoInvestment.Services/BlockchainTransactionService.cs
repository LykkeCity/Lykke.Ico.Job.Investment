using System;
using System.Net;
using System.Threading.Tasks;
using Common;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Ico.Core.Repositories.CampaignInfo;
using Lykke.Ico.Core.Repositories.Investor;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Domain.CryptoInvestments;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;
using Lykke.Service.IcoExRate.Client;
using Lykke.Service.IcoExRate.Client.AutorestClient.Models;
using Microsoft.Rest;
using Lykke.Ico.Core.Repositories.InvestorTransaction;

namespace Lykke.Job.IcoInvestment.Services
{
    public class BlockchainTransactionService : IBlockchainTransactionService
    {
        private readonly ILog _log;
        private readonly IIcoExRateClient _exRateClient;
        private readonly IInvestorAttributeRepository _investorAttributeRepository;
        private readonly ICampaignInfoRepository _campaignInfoRepository;
        private readonly IInvestorTransactionRepository _investorTransactionRepository;
        private readonly IInvestorRepository _investorRepository;
        private readonly IQueuePublisher<InvestorNewTransactionMessage> _investmentMailSender;
        private readonly IQueuePublisher<InvestorKycRequestMessage> _kycMailSender;
        private readonly IcoSettings _icoSettings;
        private readonly string _component = nameof(BlockchainTransactionService);
        private readonly string _process = nameof(Process);

        public BlockchainTransactionService(
            ILog log,
            IIcoExRateClient exRateClient, 
            IInvestorAttributeRepository investorAttributeRepository, 
            ICampaignInfoRepository campaignInfoRepository,
            IInvestorTransactionRepository investorTransactionRepository,
            IInvestorRepository investorRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender,
            IQueuePublisher<InvestorKycRequestMessage> kycMailSender,
            IcoSettings icoSettings)
        {
            _log = log;
            _exRateClient = exRateClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignInfoRepository = campaignInfoRepository;
            _investorTransactionRepository = investorTransactionRepository;
            _investorRepository = investorRepository;
            _investmentMailSender = investmentMailSender;
            _kycMailSender = kycMailSender;
            _icoSettings = icoSettings;
        }

        public async Task Process(BlockchainTransactionMessage msg)
        {
            await _log.WriteInfoAsync(_component, _process, $"New transaction: {msg.ToJson()}");

            var timeSpan = msg.BlockTimestamp.UtcDateTime - _icoSettings.CampaignStartDateTime.ToUniversalTime();
            if (timeSpan < TimeSpan.Zero || timeSpan > TimeSpan.FromDays(21))
            {
                throw new InvalidOperationException($"Investment {msg.ToJson()} is out of crowd-sale terms");
            }

            var existingTransaction = await _investorTransactionRepository.GetAsync(msg.InvestorEmail, msg.TransactionId);
            if (existingTransaction != null)
            {
                await _log.WriteInfoAsync(_component, _process, $"The transaction with TransactionId='{msg.TransactionId}' was already processed");
                return;
            }

            var investor = await _investorRepository.GetAsync(msg.InvestorEmail);
            if (investor == null)
            {
                throw new InvalidOperationException($"The investor with email='{msg.InvestorEmail}' was not found");
            }

            var assetPair = msg.CurrencyType == CurrencyType.Bitcoin ? Pair.BTCUSD : Pair.ETHUSD;
            var exchangeRate = await _exRateClient.GetAverageRate(assetPair, msg.BlockTimestamp.UtcDateTime);
            if (exchangeRate == null)
            {
                throw new InvalidOperationException($"Exchange rate not found for investment {msg.ToJson()}");
            }
            if (exchangeRate.AverageRate == null || exchangeRate.AverageRate == 0)
            {
                throw new InvalidOperationException($"Exchange rate is not valid: {exchangeRate.ToJson()}. Transaction message: {msg.ToJson()}");
            }

            var transaction = await SaveTransaction(msg, exchangeRate);

            await SendConfirmationEmail(transaction, msg.Link);
            await UpdateCampaignAmounts(transaction);
            await UpdateInvestorAmounts(transaction);
            await RequestKyc(transaction.Email);
        }

        private async Task<InvestorTransaction> SaveTransaction(BlockchainTransactionMessage msg, AverageRateResponse exchangeRate)
        {
            var totalVldStr = await _campaignInfoRepository.GetValueAsync(CampaignInfoType.AmountInvestedToken);
            if (!decimal.TryParse(totalVldStr, out var totalVld))
            {
                totalVld = 0M;
            }

            var avgExchangeRate = Convert.ToDecimal(exchangeRate.AverageRate);
            var tokenPrice = GetTokenPrice(totalVld, msg.BlockTimestamp);
            var amountUsd = msg.Amount * avgExchangeRate;
            var amountVld = DecimalExtensions.RoundDown(amountUsd / tokenPrice, _icoSettings.TokenDecimals);

            var transaction = msg.TransactionId;
            if (msg.CurrencyType == CurrencyType.Bitcoin && msg.TransactionId.Contains("-"))
            {
                transaction = msg.TransactionId.Substring(0, msg.TransactionId.IndexOf("-"));
            }

            var investorTransaction = new InvestorTransaction
            {
                Email = msg.InvestorEmail,
                TransactionId = msg.TransactionId,
                CreatedUtc = msg.BlockTimestamp.UtcDateTime,
                Currency = msg.CurrencyType,
                BlockId = msg.BlockId,
                Transaction = transaction,
                PayInAddress = msg.DestinationAddress,
                Amount = msg.Amount,
                AmountUsd = amountUsd,
                AmountToken = amountVld,
                TokenPrice = tokenPrice,
                ExchangeRate = avgExchangeRate,
                ExchangeRateContext = exchangeRate.Rates.ToJson()
            };

            await _investorTransactionRepository.SaveAsync(investorTransaction);
            await _log.WriteInfoAsync(_component, _process, $"Transaction saved : {investorTransaction.ToJson()}");

            return investorTransaction;
        }

        private async Task SendConfirmationEmail(InvestorTransaction tx, string link)
        {
            try
            {
                var asset = "";

                switch (tx.Currency)
                {
                    case CurrencyType.Bitcoin:
                        asset = "BTC";
                        break;
                    case CurrencyType.Ether:
                        asset = "ETH";
                        break;
                    default:
                        break;
                }

                var message = new InvestorNewTransactionMessage
                {
                    EmailTo = tx.Email,
                    Payment = $"{tx.Amount} {asset}",
                    TransactionLink = link
                };

                await _investmentMailSender.SendAsync(message);

                await _log.WriteInfoAsync(_component, nameof(SendConfirmationEmail),
                    $"Transaction confirmation email was sent: {message.ToJson()}");
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(_component, nameof(SendConfirmationEmail), 
                    $"Failed to send confirmation email for transaction: tx={tx.ToJson()}, link={link}", ex);
            }
        }

        private async Task UpdateCampaignAmounts(InvestorTransaction tx)
        {
            if (tx.Currency == CurrencyType.Bitcoin)
            {
                await IncrementCampaignInfoParam(CampaignInfoType.AmountInvestedBtc, tx.Amount);
            }
            if (tx.Currency == CurrencyType.Ether)
            {
                await IncrementCampaignInfoParam(CampaignInfoType.AmountInvestedEth, tx.Amount);
            }

            await IncrementCampaignInfoParam(CampaignInfoType.AmountInvestedToken, tx.AmountToken);
            await IncrementCampaignInfoParam(CampaignInfoType.AmountInvestedUsd, tx.AmountUsd);
        }

        private async Task IncrementCampaignInfoParam(CampaignInfoType type, decimal value)
        {
            try
            {
                await _campaignInfoRepository.IncrementValue(type, value);
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(_component, nameof(UpdateCampaignAmounts),
                    $"Failed to update CampaignInfo.{Enum.GetName(typeof(CampaignInfoType), type)}: {value}", ex);
            }
        }

        private async Task UpdateInvestorAmounts(InvestorTransaction tx)
        {
            try
            {
                await _investorRepository.IncrementAmount(tx.Email, tx.Currency, tx.Amount, tx.AmountUsd, tx.AmountToken);
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(_component, nameof(UpdateInvestorAmounts),
                    $"Failed to update investor amount: email={tx.ToJson()}", ex);
            }
        }

        private async Task RequestKyc(string email)
        {
            try
            {
                var investor = await _investorRepository.GetAsync(email);

                if (string.IsNullOrEmpty(investor.KycRequestId) && investor.AmountUsd >= _icoSettings.KycThreshold)
                {
                    // TODO: get actual KYC identitfier from provider
                    var kycRequestId = Guid.NewGuid().ToString();

                    await _investorRepository.SaveKycAsync(email, kycRequestId);

                    var message = new InvestorKycRequestMessage
                    {
                        EmailTo = investor.Email,
                        KycLink = "http://test.valid.global/kyc/" + investor.KycRequestId
                    };
                    await _kycMailSender.SendAsync(message);

                    await _log.WriteInfoAsync(_component, nameof(RequestKyc),
                        $"KYC request email was sent: {message.ToJson()}");
                }
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(_component, nameof(UpdateCampaignAmounts),
                    $"Failed to Request KYC: {email}", ex);
            }
        }

        private decimal GetTokenPrice(decimal currentTotal, DateTimeOffset blockTimestamp)
        {
            if (currentTotal < 20000000)
            {
                return _icoSettings.BasePrice * 0.75M;
            }

            var timeSpan = blockTimestamp.UtcDateTime - _icoSettings.CampaignStartDateTime.ToUniversalTime();

            if (timeSpan < TimeSpan.FromDays(1))
            {
                return _icoSettings.BasePrice * 0.80M;
            }

            if (timeSpan < TimeSpan.FromDays(7))
            {
                return _icoSettings.BasePrice * 0.85M;
            }

            if (timeSpan < TimeSpan.FromDays(7 * 2))
            {
                return _icoSettings.BasePrice * 0.95M;
            }
            else
            {
                return _icoSettings.BasePrice;
            }
        }
    }
}
