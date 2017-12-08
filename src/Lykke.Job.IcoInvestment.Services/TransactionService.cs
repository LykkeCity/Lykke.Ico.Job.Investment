using System;
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
using Lykke.Service.IcoExRate.Client;
using Lykke.Service.IcoExRate.Client.AutorestClient.Models;
using Microsoft.Rest;
using Lykke.Ico.Core.Repositories.InvestorTransaction;
using Lykke.Ico.Core.Repositories.CampaignSettings;
using System.Collections.Generic;

namespace Lykke.Job.IcoInvestment.Services
{
    public class TransactionService : ITransactionService
    {
        private readonly ILog _log;
        private readonly IIcoExRateClient _exRateClient;
        private readonly IInvestorAttributeRepository _investorAttributeRepository;
        private readonly ICampaignInfoRepository _campaignInfoRepository;
        private readonly ICampaignSettingsRepository _campaignSettingsRepository;
        private readonly IInvestorTransactionRepository _investorTransactionRepository;
        private readonly IInvestorRepository _investorRepository;
        private readonly IQueuePublisher<InvestorNewTransactionMessage> _investmentMailSender;
        private readonly IQueuePublisher<InvestorKycRequestMessage> _kycMailSender;
        private readonly IQueuePublisher<InvestorNeedMoreInvestmentMessage> _needMoreInvestmentMailSender;
        private readonly string _component = nameof(TransactionService);
        private readonly string _process = nameof(Process);

        public TransactionService(
            ILog log,
            IIcoExRateClient exRateClient, 
            IInvestorAttributeRepository investorAttributeRepository, 
            ICampaignInfoRepository campaignInfoRepository,
            ICampaignSettingsRepository campaignSettingsRepository,
            IInvestorTransactionRepository investorTransactionRepository,
            IInvestorRepository investorRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender,
            IQueuePublisher<InvestorKycRequestMessage> kycMailSender,
            IQueuePublisher<InvestorNeedMoreInvestmentMessage> needMoreInvestmentMailSender)
        {
            _log = log;
            _exRateClient = exRateClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignInfoRepository = campaignInfoRepository;
            _campaignSettingsRepository = campaignSettingsRepository;
            _investorTransactionRepository = investorTransactionRepository;
            _investorRepository = investorRepository;
            _investmentMailSender = investmentMailSender;
            _kycMailSender = kycMailSender;
            _needMoreInvestmentMailSender = needMoreInvestmentMailSender;
        }

        public async Task Process(TransactionMessage msg)
        {
            await _log.WriteInfoAsync(_component, _process, $"New transaction: {msg.ToJson()}");

            var existingTransaction = await _investorTransactionRepository.GetAsync(msg.Email, msg.UniqueId);
            if (existingTransaction != null)
            {
                await _log.WriteInfoAsync(_component, _process, 
                    $"The transaction with UniqueId='{msg.UniqueId}' was already processed");
                return;
            }

            var investor = await _investorRepository.GetAsync(msg.Email);
            if (investor == null)
            {
                throw new InvalidOperationException($"Investor with email={msg.Email} was not found");
            }

            var settings = await _campaignSettingsRepository.GetAsync();
            if (settings == null)
            {
                throw new InvalidOperationException($"Campaign settings was not found");
            }

            if (msg.CreatedUtc < settings.StartDateTimeUtc || msg.CreatedUtc > settings.EndDateTimeUtc)
            {
                throw new InvalidOperationException($"Transaction date {msg.CreatedUtc} is out of campaign start/end dates: " +
                    $"{settings.StartDateTimeUtc} - {settings.EndDateTimeUtc}");
            }

            var soldTokensAmountStr = await _campaignInfoRepository.GetValueAsync(CampaignInfoType.AmountInvestedToken);
            if (!Decimal.TryParse(soldTokensAmountStr, out var soldTokensAmount))
            {
                soldTokensAmount = 0;
            }
            if (soldTokensAmount > settings.TotalTokensAmount)
            {
                throw new InvalidOperationException($"All tokens were sold out. Sold Tokens={soldTokensAmount}, " +
                    $"Total Tokens= {settings.TotalTokensAmount}");
            }

            var transaction = await SaveTransaction(msg, settings, soldTokensAmount);

            await UpdateCampaignAmounts(transaction);
            await UpdateInvestorAmounts(transaction);
            await SendConfirmationEmail(transaction, msg.Link);

            investor = await _investorRepository.GetAsync(msg.Email);
            if (investor.AmountUsd < settings.MinInvestAmountUsd)
            {
                await SendNeedMoreInvestmentEmail(investor.Email, investor.AmountUsd, settings.MinInvestAmountUsd);
            }
            if (!investor.KycRequestedUtc.HasValue && investor.AmountUsd >= settings.MinInvestAmountUsd)
            {
                await RequestKyc(investor.Email);
            }
        }

        private async Task<InvestorTransaction> SaveTransaction(TransactionMessage msg, 
            ICampaignSettings settings, decimal soldTokensAmount)
        {
            var exchangeRate = await GetExchangeRate(msg);
            var avgExchangeRate = Convert.ToDecimal(exchangeRate.AverageRate);
            var tokenPrice = GetTokenPrice(soldTokensAmount, settings.TokenBasePriceUsd, 
                settings.StartDateTimeUtc, msg.CreatedUtc);
            var amountUsd = msg.Amount * avgExchangeRate;
            var amountVld = amountUsd / tokenPrice;

            var investorTransaction = new InvestorTransaction
            {
                Email = msg.Email,
                UniqueId = msg.UniqueId,
                CreatedUtc = msg.CreatedUtc,
                Currency = msg.Currency,
                TransactionId = msg.TransactionId,
                BlockId = msg.BlockId,
                PayInAddress = msg.PayInAddress,
                Amount = msg.Amount,
                AmountUsd = amountUsd,
                AmountToken = DecimalExtensions.RoundDown(amountVld, settings.TokenDecimals),
                Fee = msg.Fee,
                TokenPrice = tokenPrice,
                ExchangeRate = avgExchangeRate,
                ExchangeRateContext = exchangeRate.Rates.ToJson()
            };

            await _investorTransactionRepository.SaveAsync(investorTransaction);
            await _log.WriteInfoAsync(_component, nameof(SaveTransaction), 
                $"Transaction saved : {investorTransaction.ToJson()}");

            return investorTransaction;
        }

        private async Task<AverageRateResponse> GetExchangeRate(TransactionMessage msg)
        {
            if (msg.Currency == CurrencyType.Fiat)
            {
                return new AverageRateResponse { AverageRate = 1, Rates = new List<RateResponse>() };
            }

            var assetPair = msg.Currency == CurrencyType.Bitcoin ? Pair.BTCUSD : Pair.ETHUSD;
            var exchangeRate = await _exRateClient.GetAverageRate(assetPair, msg.CreatedUtc);
            if (exchangeRate == null)
            {
                throw new InvalidOperationException($"Exchange rate was not found");
            }
            if (exchangeRate.AverageRate == null || exchangeRate.AverageRate == 0)
            {
                throw new InvalidOperationException($"Exchange rate is not valid: {exchangeRate.ToJson()}");
            }

            return exchangeRate;
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
                    case CurrencyType.Fiat:
                        asset = "USD";
                        break;
                }

                var message = new InvestorNewTransactionMessage
                {
                    EmailTo = tx.Email,
                    Payment = $"{tx.Amount + tx.Fee} {asset}",
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
                await _log.WriteErrorAsync(_component, nameof(IncrementCampaignInfoParam),
                    $"Failed to update CampaignInfo.{Enum.GetName(typeof(CampaignInfoType), type)}: {value}",
                    ex);
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
                // TODO: get actual KYC identitfier from provider
                var kycRequestId = Guid.NewGuid().ToString();

                await _investorRepository.SaveKycAsync(email, kycRequestId);

                var message = new InvestorKycRequestMessage
                {
                    EmailTo = email,
                    KycLink = "http://test.valid.global/kyc/" + kycRequestId
                };
                await _kycMailSender.SendAsync(message);

                await _log.WriteInfoAsync(_component, nameof(RequestKyc),
                    $"InvestorKycRequestMessage was sent: {message.ToJson()}");
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(_component, nameof(RequestKyc),
                    $"Failed to request KYC: {email}", ex);
            }
        }

        private async Task SendNeedMoreInvestmentEmail(string email, decimal investedAmount, decimal minAmount)
        {
            try
            {
                var message = new InvestorNeedMoreInvestmentMessage
                {
                    EmailTo = email,
                    InvestedAmount = DecimalExtensions.RoundDown(investedAmount, 2),
                    MinAmount = minAmount
                };

                await _needMoreInvestmentMailSender.SendAsync(message);

                await _log.WriteInfoAsync(_component, nameof(SendNeedMoreInvestmentEmail),
                    $"InvestorNeedMoreInvestmentMessage was sent: {message.ToJson()}");
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(_component, nameof(SendNeedMoreInvestmentEmail),
                    $"Failed to send InvestorNeedMoreInvestmentMessage: email={email}, investedAmount={investedAmount}, minAmount", 
                    ex);
            }
        }        

        private decimal GetTokenPrice(decimal currentTotal, decimal tokenBasePrice, 
            DateTime txDateTimeUtc, DateTime campaignStartDateTimeUtc)
        {
            if (currentTotal < 20000000)
            {
                return tokenBasePrice * 0.75M;
            }

            var timeSpan = txDateTimeUtc - campaignStartDateTimeUtc;

            if (timeSpan < TimeSpan.FromDays(1))
            {
                return tokenBasePrice * 0.80M;
            }

            if (timeSpan < TimeSpan.FromDays(7))
            {
                return tokenBasePrice * 0.85M;
            }

            if (timeSpan < TimeSpan.FromDays(7 * 2))
            {
                return tokenBasePrice * 0.95M;
            }
            else
            {
                return tokenBasePrice;
            }
        }
    }
}
