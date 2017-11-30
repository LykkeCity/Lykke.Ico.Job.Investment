using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Common;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Ico.Core.Repositories.CampaignInfo;
using Lykke.Ico.Core.Repositories.CryptoInvestment;
using Lykke.Ico.Core.Repositories.Investor;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Domain.CryptoInvestment;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;
using Lykke.Service.RateCalculator.Client;
using Newtonsoft.Json;

namespace Lykke.Job.IcoInvestment.Services
{
    public class BlockchainTransactionService : IBlockchainTransactionService
    {
        private readonly ILog _log;
        private readonly IRateCalculatorClient _rateCalculatorClient;
        private readonly IInvestorRepository _investorRepository;
        private readonly IInvestorAttributeRepository _investorAttributeRepository;
        private readonly ICampaignInfoRepository _campaignInfoRepository;
        private readonly ICryptoInvestmentRepository _cryptoInvestmentRepository;
        private readonly IQueuePublisher<InvestorNewTransactionMessage> _investmentMailSender;
        private readonly IQueuePublisher<InvestorKycRequestMessage> _kycSender;
        private readonly IcoSettings _icoSettings;
        private readonly string _component = nameof(BlockchainTransactionService);
        private readonly string _process = nameof(Process);
        private volatile uint _processedTotal = 0;
        private volatile uint _processedInvestments = 0;

        private readonly Dictionary<CurrencyType, string> _assetPairs = new Dictionary<CurrencyType, string>
        {
            { CurrencyType.Bitcoin, "BTCUSD" },
            { CurrencyType.Ether, "ETHUSD" }
        };

        private readonly Dictionary<CurrencyType, string> _assetNames = new Dictionary<CurrencyType, string>
        {
            { CurrencyType.Bitcoin, "BTC" },
            { CurrencyType.Ether, "ETH" }
        };

        public BlockchainTransactionService(
            ILog log,
            IRateCalculatorClient rateCalculatorClient, 
            IInvestorAttributeRepository investorAttributeRepository, 
            ICampaignInfoRepository campaignInfoRepository, 
            ICryptoInvestmentRepository cryptoInvestmentRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender,
            IQueuePublisher<InvestorKycRequestMessage> kycSender,
            IcoSettings icoSettings)
        {
            _log = log;
            _rateCalculatorClient = rateCalculatorClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignInfoRepository = campaignInfoRepository;
            _cryptoInvestmentRepository = cryptoInvestmentRepository;
            _investmentMailSender = investmentMailSender;
            _kycSender = kycSender;
            _icoSettings = icoSettings;
        }

        public async Task Process(BlockchainTransactionMessage msg)
        {
            Debug.Assert(_assetPairs.ContainsKey(msg.CurrencyType), $"Currency pair not defined for [{msg.CurrencyType}]");
            Debug.Assert(_assetNames.ContainsKey(msg.CurrencyType), $"Currency name not defined for [{msg.CurrencyType}]");

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

            if (msg.BlockTimestamp < _icoSettings.CampaignStartDateTime ||
                msg.BlockTimestamp > _icoSettings.CampaignStartDateTime + TimeSpan.FromDays(21))
            {
                // investment is out of period of sale
                await _log.WriteInfoAsync(_component, _process, msg.ToJson(), "Investment skipped due to crowd-sale terms");
                _processedTotal++;
                _processedInvestments++;
                return;
            }

            var exchangeRate = Convert.ToDecimal(await _rateCalculatorClient.GetBestPriceAsync(_assetPairs[msg.CurrencyType], true));

            if (exchangeRate == 0M)
            {
                // re-queue message in hope to find rate later
                throw new InvalidOperationException($"Exchange rate for [{msg.CurrencyType}] not found");
            }

            var totalVld = 0M;

            if (!decimal.TryParse(await _campaignInfoRepository.GetValueAsync(CampaignInfoType.AmountInvestedVld), out totalVld))
            {
                totalVld = 0M;
            }

            var price = GetPrice(totalVld, msg.BlockTimestamp);
            var amountUsd = msg.Amount * exchangeRate;
            var amountVld = decimal.Round(amountUsd / price, 4, MidpointRounding.AwayFromZero);

            // save transaction info for investor 
            await _cryptoInvestmentRepository.SaveAsync(new CryptoInvestment
            {
                InvestorEmail = investorEmail,
                BlockId = msg.BlockId,
                BlockTimestamp = msg.BlockTimestamp,
                CurrencyType = msg.CurrencyType,
                DestinationAddress = msg.DestinationAddress,
                TransactionId = msg.TransactionId,
                Amount = msg.Amount,
                ExchangeRate = exchangeRate,
                AmountUsd = amountUsd,
                Price = price,
                AmountVld = amountVld
            });

            // increase the total ICO amount
            await _campaignInfoRepository.IncrementValue(CampaignInfoType.AmountInvestedVld, amountVld);
            await _campaignInfoRepository.IncrementValue(CampaignInfoType.AmountInvestedUsd, amountUsd);

            // send confirmation email
            await _investmentMailSender.SendAsync(new InvestorNewTransactionMessage
            {
                EmailTo = investorEmail,
                Payment = $"{msg.Amount} {_assetNames[msg.CurrencyType]}",
                TransactionLink = msg.Link
            });


            // log full transaction info
            await _log.WriteInfoAsync(_component, _process, msg.ToJson(), "Investment transaction processed");

            _processedTotal++;
            _processedInvestments++;

            await ProcessInvestor(investorEmail);
        }

        public async Task ProcessInvestor(string investorEmail)
        {
            var investments = await _cryptoInvestmentRepository.GetInvestmentsAsync(investorEmail);
            var total = investments.Sum(x => x.AmountUsd);

            if (total > _icoSettings.KycUsdThreshold)
            {
                await _kycSender.SendAsync(InvestorKycRequestMessage.Create(investorEmail, string.Empty));
            }

            var investor = await _investorRepository.GetAsync(investorEmail);
            if (investor == null)
            {
                throw new InvalidOperationException($"Investor's data {investorEmail} not found");
            }

            await _investorRepository.UpdateAsync(investor);
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
                return _icoSettings.TokenPrice * 0.75M;
            }

            if (blockTimestamp - _icoSettings.CampaignStartDateTime < TimeSpan.FromDays(1))
            {
                return _icoSettings.TokenPrice * 0.80M;
            }

            if (blockTimestamp - _icoSettings.CampaignStartDateTime < TimeSpan.FromDays(7))
            {
                return _icoSettings.TokenPrice * 0.85M;
            }

            if (blockTimestamp - _icoSettings.CampaignStartDateTime < TimeSpan.FromDays(7 * 2))
            {
                return _icoSettings.TokenPrice * 0.95M;
            }
            else
            {
                return _icoSettings.TokenPrice;
            }
        }
    }
}
