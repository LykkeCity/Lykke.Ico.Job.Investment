using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Rest;
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
using Lykke.Ico.Core.Repositories.InvestorTransaction;
using Lykke.Ico.Core.Repositories.CampaignSettings;
using Lykke.Job.IcoInvestment.Core.Domain;
using Lykke.Ico.Core.Repositories.InvestorRefund;
using Lykke.Ico.Core.Helpers;
using Lykke.Ico.Core.Services;

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
        private readonly IInvestorRefundRepository _investorRefundRepository;
        private readonly IInvestorRepository _investorRepository;
        private readonly IQueuePublisher<InvestorNewTransactionMessage> _investmentMailSender;
        private readonly IUrlEncryptionService _urlEncryptionService;

        public TransactionService(
            ILog log,
            IIcoExRateClient exRateClient, 
            IInvestorAttributeRepository investorAttributeRepository, 
            ICampaignInfoRepository campaignInfoRepository,
            ICampaignSettingsRepository campaignSettingsRepository,
            IInvestorTransactionRepository investorTransactionRepository,
            IInvestorRefundRepository investorRefundRepository,
            IInvestorRepository investorRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender,
            IUrlEncryptionService urlEncryptionService)
        {
            _log = log;
            _exRateClient = exRateClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignInfoRepository = campaignInfoRepository;
            _campaignSettingsRepository = campaignSettingsRepository;
            _investorTransactionRepository = investorTransactionRepository;
            _investorRefundRepository = investorRefundRepository;
            _investorRepository = investorRepository;
            _investmentMailSender = investmentMailSender;
            _urlEncryptionService = urlEncryptionService;
        }

        public async Task Process(TransactionMessage msg)
        {
            await _log.WriteInfoAsync(nameof(Process),
                $"msg: {msg.ToJson()}", $"New transaction");

            await ValidateMessage(msg);

            var txProcessed = await WasTxAlreadyProcessed(msg);
            if (txProcessed)
            {
                return;
            }

            var settings = await GetCampaignSettings();
            var soldTokensAmount = await GetSoldTokensAmount();

            var validTx = await IsTxValid(msg, settings, soldTokensAmount);
            if (validTx)
            {
                var transaction = await SaveTransaction(msg, settings, soldTokensAmount);

                await UpdateCampaignAmounts(transaction);
                await UpdateInvestorAmounts(transaction);
                await SendConfirmationEmail(transaction, msg.Link, settings);
            }
        }

        private async Task ValidateMessage(TransactionMessage msg)
        {
            if (string.IsNullOrEmpty(msg.UniqueId))
            {
                throw new InvalidOperationException($"UniqueId can not be empty");
            }

            if (string.IsNullOrEmpty(msg.Email))
            {
                throw new InvalidOperationException($"Email can not be empty");
            }

            var investor = await _investorRepository.GetAsync(msg.Email);
            if (investor == null)
            {
                throw new InvalidOperationException($"Investor with email {msg.Email} was not found");
            }            
        }

        private async Task<bool> WasTxAlreadyProcessed(TransactionMessage msg)
        {
            var existingTransaction = await _investorTransactionRepository.GetAsync(msg.Email, msg.UniqueId);
            if (existingTransaction != null)
            {
                await _log.WriteInfoAsync(nameof(Process),
                    $"emai: {msg.Email}, uniqueId: {msg.UniqueId}, existingTransaction: {existingTransaction.ToJson()}",
                    $"The transaction was already processed");

                return true;
            }

            return false;
        }

        private async Task<ICampaignSettings> GetCampaignSettings()
        {
            var settings = await _campaignSettingsRepository.GetAsync();
            if (settings == null)
            {
                throw new InvalidOperationException($"Campaign settings was not found");
            }

            return settings;
        }

        private async Task<decimal> GetSoldTokensAmount()
        {
            var soldTokensAmountStr = await _campaignInfoRepository.GetValueAsync(CampaignInfoType.AmountInvestedToken);
            if (!Decimal.TryParse(soldTokensAmountStr, out var soldTokensAmount))
            {
                soldTokensAmount = 0;
            }

            return soldTokensAmount;
        }

        private async Task<bool> IsTxValid(TransactionMessage msg, ICampaignSettings settings, decimal soldTokensAmount)
        {
            var preSalePhase = settings.IsPreSale(msg.CreatedUtc);
            var crowdSalePhase = settings.IsCrowdSale(msg.CreatedUtc);

            if (!preSalePhase && !crowdSalePhase)
            {
                await _log.WriteInfoAsync(nameof(Process),
                    $"msg: {msg}, settings: {settings.ToJson()}",
                    $"Transaction is out of campaign dates");

                await _investorRefundRepository.SaveAsync(msg.Email, 
                    InvestorRefundReason.OutOfDates, 
                    msg.ToJson());

                return false;
            }
            if (preSalePhase && soldTokensAmount > settings.PreSaleTotalTokensAmount)
            {
                await _log.WriteInfoAsync(nameof(Process),
                    $"soldTokensAmount: {soldTokensAmount}, settings: {settings.ToJson()}, msg: {msg.ToJson()}",
                    $"All presale tokens were sold out");

                await _investorRefundRepository.SaveAsync(msg.Email, 
                    InvestorRefundReason.PreSaleTokensSoldOut, 
                    msg.ToJson());

                return false;
            }
            if (crowdSalePhase && soldTokensAmount > settings.GetTotalTokensAmount())
            {
                await _log.WriteInfoAsync(nameof(Process),
                    $"soldTokensAmount: {soldTokensAmount}, totalTokensAmount: {settings.GetTotalTokensAmount()}, " +
                    $"settings: {settings.ToJson()}, msg: {msg.ToJson()}",
                    $"All tokens were sold out");

                await _investorRefundRepository.SaveAsync(msg.Email,
                    InvestorRefundReason.TokensSoldOut,
                    msg.ToJson());

                return false;
            }

            return true;
        }

        private async Task<InvestorTransaction> SaveTransaction(TransactionMessage msg, 
            ICampaignSettings settings, decimal soldTokensAmount)
        {
            var exchangeRate = await GetExchangeRate(msg);
            var avgExchangeRate = Convert.ToDecimal(exchangeRate.AverageRate);
            var amountUsd = msg.Amount * avgExchangeRate;
            var tokenPriceList = TokenPrice.GetPriceList(settings, msg.CreatedUtc, amountUsd, soldTokensAmount);
            var amountToken = tokenPriceList.Sum(p => p.Count);
            var tokenPrice = (amountUsd / amountToken).RoundDown(settings.TokenDecimals);

            var tx = new InvestorTransaction
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
                AmountToken = amountToken,
                Fee = msg.Fee,
                TokenPrice = tokenPrice,
                TokenPriceContext = tokenPriceList.ToJson(),
                ExchangeRate = avgExchangeRate,
                ExchangeRateContext = exchangeRate.Rates.ToJson()
            };

            await _log.WriteInfoAsync(nameof(SaveTransaction),
                $"tx: {tx.ToJson()}", $"Save transaction");

            await _investorTransactionRepository.SaveAsync(tx);

            return tx;
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

        private async Task SendConfirmationEmail(InvestorTransaction tx, string link, ICampaignSettings settings)
        {
            try
            {
                var investor = await _investorRepository.GetAsync(tx.Email);
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

                if (investor.AmountUsd < settings.MinInvestAmountUsd)
                {
                    message.MoreInvestmentRequired = true;
                    message.InvestedAmount = DecimalExtensions.RoundDown(investor.AmountUsd, 2);
                    message.MinAmount = settings.MinInvestAmountUsd;
                }

                if (investor.KycRequestedUtc == null && 
                    investor.AmountUsd >= settings.MinInvestAmountUsd)
                {
                    var kycId = await SaveInvestorKyc(investor.Email);

                    var kycMessage = new { campaign="", email = tx.Email, kycid = kycId };
                    var kycEncryptedMessage = _urlEncryptionService.Encrypt(kycMessage.ToJson());
                    var kycLink = $"https://cbfs-ico-service.com/#/register?message={kycEncryptedMessage}";

                    message.KycRequired = true;
                    message.KycLink = kycLink;
                }

                await _log.WriteInfoAsync(nameof(SendConfirmationEmail),
                    $"message: {message.ToJson()}",
                    $"Send transaction confirmation message to queue");

                await _investmentMailSender.SendAsync(message);
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(nameof(SendConfirmationEmail),
                    $"Tx: {tx.ToJson()}, TxLink: {link}",
                    ex);
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
            if (tx.Currency == CurrencyType.Fiat)
            {
                await IncrementCampaignInfoParam(CampaignInfoType.AmountInvestedFiat, tx.Amount);
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
                await _log.WriteErrorAsync(nameof(IncrementCampaignInfoParam),
                    $"{Enum.GetName(typeof(CampaignInfoType), type)}: {value}",
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
                await _log.WriteErrorAsync(nameof(UpdateInvestorAmounts),
                    $"Tx: {tx.ToJson()}",
                    ex);
            }
        }

        private async Task<string> SaveInvestorKyc(string email)
        {
            try
            {
                var kycRequestId = Guid.NewGuid().ToString();

                await _log.WriteInfoAsync(nameof(SaveInvestorKyc),
                    $"email: {email}, kycRequestId: {kycRequestId}",
                    $"Save KYC request info");

                await _investorRepository.SaveKycAsync(email, kycRequestId);

                return kycRequestId;
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(nameof(SaveInvestorKyc), 
                    $"Email: {email}", 
                    ex);

                throw;
            }
        }
    }
}
