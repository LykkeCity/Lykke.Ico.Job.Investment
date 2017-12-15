﻿using System;
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
using Lykke.Job.IcoInvestment.Core.Domain;
using System.Linq;

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

        public TransactionService(
            ILog log,
            IIcoExRateClient exRateClient, 
            IInvestorAttributeRepository investorAttributeRepository, 
            ICampaignInfoRepository campaignInfoRepository,
            ICampaignSettingsRepository campaignSettingsRepository,
            IInvestorTransactionRepository investorTransactionRepository,
            IInvestorRepository investorRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender)
        {
            _log = log;
            _exRateClient = exRateClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignInfoRepository = campaignInfoRepository;
            _campaignSettingsRepository = campaignSettingsRepository;
            _investorTransactionRepository = investorTransactionRepository;
            _investorRepository = investorRepository;
            _investmentMailSender = investmentMailSender;
        }

        public async Task Process(TransactionMessage msg)
        {
            await _log.WriteInfoAsync(nameof(Process),
                $"Message: {msg.ToJson()}", 
                $"New transaction");

            var existingTransaction = await _investorTransactionRepository.GetAsync(msg.Email, msg.UniqueId);
            if (existingTransaction != null)
            {
                await _log.WriteInfoAsync(nameof(Process),
                    $"Message: {msg.ToJson()}",
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
            await SendConfirmationEmail(transaction, msg.Link, settings);
        }

        private async Task<InvestorTransaction> SaveTransaction(TransactionMessage msg, 
            ICampaignSettings settings, decimal soldTokensAmount)
        {
            var exchangeRate = await GetExchangeRate(msg);
            var avgExchangeRate = Convert.ToDecimal(exchangeRate.AverageRate);
            var amountUsd = msg.Amount * avgExchangeRate;
            var tokenPriceList = TokenPrice.GetPriceList(settings, msg.CreatedUtc, amountUsd, soldTokensAmount);
            var amountToken = tokenPriceList.Sum(p => p.Count);
            var avgTokenPrice = tokenPriceList.Average(p => p.Price);

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
                AmountToken = amountToken,
                Fee = msg.Fee,
                TokenPrice = avgTokenPrice,
                TokenPriceContext = tokenPriceList.ToJson(),
                ExchangeRate = avgExchangeRate,
                ExchangeRateContext = exchangeRate.Rates.ToJson()
            };

            await _investorTransactionRepository.SaveAsync(investorTransaction);
            await _log.WriteInfoAsync(nameof(SaveTransaction),
                $"Tx: {investorTransaction.ToJson()}",
                $"Transaction saved");

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
                    message.KycRequired = true;
                    message.KycLink = await RequestKyc(investor.Email);
                }

                await _investmentMailSender.SendAsync(message);

                await _log.WriteInfoAsync(nameof(SendConfirmationEmail),
                    $"Message: {message.ToJson()}",
                    $"Transaction confirmation email was sent");
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

        private async Task<string> RequestKyc(string email)
        {
            try
            {
                // TODO: get actual KYC identitfier from provider
                var kycRequestId = Guid.NewGuid().ToString();

                await _investorRepository.SaveKycAsync(email, kycRequestId);

                await _log.WriteInfoAsync(nameof(RequestKyc),
                    $"Email: {email}",
                    $"KYC requested");

                return kycRequestId;
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(nameof(RequestKyc), 
                    $"Email: {email}", 
                    ex);

                throw;
            }
        }
    }
}
