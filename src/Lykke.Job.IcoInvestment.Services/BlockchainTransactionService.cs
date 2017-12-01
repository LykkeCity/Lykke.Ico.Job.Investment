using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Common;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Ico.Core.Repositories.CampaignInfo;
using Lykke.Ico.Core.Repositories.CryptoInvestment;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Domain.CryptoInvestments;
using Lykke.Job.IcoInvestment.Core.Domain.Transactions;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;
using Lykke.Service.IcoExRate.Client;
using Lykke.Service.IcoExRate.Client.AutorestClient.Models;
using Microsoft.Rest;

namespace Lykke.Job.IcoInvestment.Services
{
    public class BlockchainTransactionService : IBlockchainTransactionService
    {
        private readonly ILog _log;
        private readonly IIcoExRateClient _exRateClient;
        private readonly IInvestorAttributeRepository _investorAttributeRepository;
        private readonly ICampaignInfoRepository _campaignInfoRepository;
        private readonly ICryptoInvestmentRepository _cryptoInvestmentRepository;
        private readonly IQueuePublisher<ProcessedTransactionMessage> _processedTxSender;
        private readonly IcoSettings _icoSettings;
        private readonly string _component = nameof(BlockchainTransactionService);
        private readonly string _process = nameof(Process);
        private volatile uint _processedTotal = 0;
        private volatile uint _processedInvestments = 0;

        private readonly Dictionary<CurrencyType, Pair> _assetPairs = new Dictionary<CurrencyType, Pair>
        {
            { CurrencyType.Bitcoin, Pair.BTCUSD },
            { CurrencyType.Ether, Pair.ETHUSD }
        };

        public BlockchainTransactionService(
            ILog log,
            IIcoExRateClient exRateClient, 
            IInvestorAttributeRepository investorAttributeRepository, 
            ICampaignInfoRepository campaignInfoRepository, 
            ICryptoInvestmentRepository cryptoInvestmentRepository,
            IQueuePublisher<ProcessedTransactionMessage> processedTxSender,
            IcoSettings icoSettings)
        {
            _log = log;
            _exRateClient = exRateClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignInfoRepository = campaignInfoRepository;
            _cryptoInvestmentRepository = cryptoInvestmentRepository;
            _processedTxSender = processedTxSender;
            _icoSettings = icoSettings;
        }

        public async Task Process(BlockchainTransactionMessage msg)
        {
            Debug.Assert(_assetPairs.ContainsKey(msg.CurrencyType), $"Currency pair not defined for [{msg.CurrencyType}]");

            var investorAttributeType = msg.CurrencyType == CurrencyType.Bitcoin
                ? InvestorAttributeType.PayInBtcAddress
                : InvestorAttributeType.PayInEthAddress;

            var investorEmail = await _investorAttributeRepository.GetInvestorEmailAsync(investorAttributeType, msg.DestinationAddress);

            if (string.IsNullOrWhiteSpace(investorEmail))
            {
                // destination address is not a cash-in address of any ICO investor
                _processedTotal++;
                return;
            }

            var timeSpan = msg.BlockTimestamp.UtcDateTime - _icoSettings.CampaignStartDateTime.ToUniversalTime();

            if (timeSpan < TimeSpan.Zero ||
                timeSpan > TimeSpan.FromDays(21))
            {
                // investment is out of period of sale
                await _log.WriteWarningAsync(_component, _process, msg.ToJson(), "Investment is out of crowd-sale terms");
                _processedTotal++;
                _processedInvestments++;
                return;
            }

            var exchangeRate = await GetExchangeRate(_assetPairs[msg.CurrencyType], msg.BlockTimestamp) ?? new AverageRateResponse { AverageRate = 6000.0, Rates = new List<RateResponse>() };

            if (exchangeRate == null || 
                exchangeRate.AverageRate == null)
            {
                await _log.WriteWarningAsync(_component, _process, msg.ToJson(), "Exchange rate not found");
                _processedTotal++;
                _processedInvestments++;
                return;
            }

            var totalVld = 0M;

            if (!decimal.TryParse(await _campaignInfoRepository.GetValueAsync(CampaignInfoType.AmountInvestedVld), out totalVld))
            {
                totalVld = 0M;
            }

            var avgExchangeRate = Convert.ToDecimal(exchangeRate.AverageRate);
            var price = GetPrice(totalVld, msg.BlockTimestamp);
            var amountUsd = msg.Amount * avgExchangeRate;
            var amountVld = decimal.Round(amountUsd / price, 4, MidpointRounding.AwayFromZero);
            var cryptoInvestment = new CryptoInvestment
            {
                InvestorEmail = investorEmail,
                BlockId = msg.BlockId,
                BlockTimestamp = msg.BlockTimestamp.UtcDateTime,
                CurrencyType = msg.CurrencyType,
                DestinationAddress = msg.DestinationAddress,
                TransactionId = msg.TransactionId,
                Amount = msg.Amount,
                ExchangeRate = avgExchangeRate,
                AmountUsd = amountUsd,
                Price = price,
                AmountVld = amountVld,
                Context = exchangeRate.Rates.ToJson()
            };

            // save transaction info for investor 
            await _cryptoInvestmentRepository.SaveAsync(cryptoInvestment);

            // increase the total ICO amounts
            // TODO: add cache for total amounts to prevent double-incrementing:
            // - read all transactions on start
            // - calc total amounts
            // - keep amounts in actual state
            // - re-write amounts instead of incrementing
            // - dedicated thread-safe cache-service to be able to use it from different places 
            await _campaignInfoRepository.IncrementValue(CampaignInfoType.AmountInvestedVld, amountVld);
            await _campaignInfoRepository.IncrementValue(CampaignInfoType.AmountInvestedUsd, amountUsd);
            await _campaignInfoRepository.IncrementValue(msg.CurrencyType == CurrencyType.Bitcoin ? CampaignInfoType.AmountInvestedBtc : CampaignInfoType.AmountInvestedBtc, msg.Amount);

            // queue transaction for sending confirmation emails and processing KYC
            await _processedTxSender.SendAsync(ProcessedTransactionMessage.Create(investorEmail, msg.TransactionId, msg.Link));

            // log full transaction info
            await _log.WriteInfoAsync(_component, _process, msg.ToJson(), "Investment processed");

            _processedTotal++;
            _processedInvestments++;
        }

        public async Task<AverageRateResponse> GetExchangeRate(Pair assetPair, DateTimeOffset blockTimestamp)
        {
            try
            {
                return await _exRateClient.GetAverageRate(assetPair, blockTimestamp.UtcDateTime);
            }
            catch (HttpOperationException ex)
            {
                if (ex.Response.StatusCode == HttpStatusCode.NoContent)
                    return null;
                else
                    throw;
            }
        }

        public async Task FlushProcessInfo()
        {
            if (_processedTotal > 0 || 
                _processedInvestments > 0)
            {
                await _log.WriteInfoAsync(_component, _process, string.Empty,
                    $"{_processedTotal} transaction(s) processed; {_processedInvestments} investments processed");

                _processedTotal = 0;
                _processedInvestments = 0;
            }
        }

        public decimal GetPrice(decimal currentTotal, DateTimeOffset blockTimestamp)
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
