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
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.Service.RateCalculator.Client;

namespace Lykke.Job.IcoInvestment.Services
{
    public class BlockchainTransactionService : IBlockchainTransactionService
    {
        private readonly ILog _log;
        private readonly IRateCalculatorClient _rateCalculatorClient;
        private readonly IInvestorAttributeRepository _investorAttributeRepository;
        private readonly ICampaignInfoRepository _campaignInfoRepository;
        private readonly ICryptoInvestmentRepository _cryptoInvestmentRepository;
        private readonly IQueuePublisher<InvestorNewTransactionMessage> _investmentMailSender;
        private readonly string _component = nameof(BlockchainTransactionService);
        private readonly string _process = nameof(Process);

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

        private readonly Dictionary<CurrencyType, Func<string, string>> _assetLinks = new Dictionary<CurrencyType, Func<string, string>>
        {
            { CurrencyType.Bitcoin, a => a.StartsWith("1") ? "https://blockchainexplorer.lykke.com/transaction" : "https://live.blockcypher.com/btc-testnet/tx" },
            { CurrencyType.Ether, a => "https://etherscan.io/tx" }
        };

        public BlockchainTransactionService(
            ILog log,
            IRateCalculatorClient rateCalculatorClient, 
            IInvestorAttributeRepository investorAttributeRepository, 
            ICampaignInfoRepository campaignInfoRepository, 
            ICryptoInvestmentRepository cryptoInvestmentRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender)
        {
            _log = log;
            _rateCalculatorClient = rateCalculatorClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignInfoRepository = campaignInfoRepository;
            _cryptoInvestmentRepository = cryptoInvestmentRepository;
            _investmentMailSender = investmentMailSender;
        }

        public async Task Process(BlockchainTransactionMessage msg)
        {
            var investorAttributeType = msg.CurrencyType == CurrencyType.Bitcoin
                ? InvestorAttributeType.PayInBtcAddress
                : InvestorAttributeType.PayInEthAddress;

            var investorEmail = await _investorAttributeRepository.GetInvestorEmailAsync(investorAttributeType, msg.DestinationAddress);

            if (string.IsNullOrWhiteSpace(investorEmail))
            {
                // destination address is not a cash-in address of any ICO investor
                return;
            }

            Debug.Assert(_assetPairs.ContainsKey(msg.CurrencyType), $"Currency pair not defined for [{msg.CurrencyType}]");
            Debug.Assert(_assetNames.ContainsKey(msg.CurrencyType), $"Currency name not defined for [{msg.CurrencyType}]");
            Debug.Assert(_assetLinks.ContainsKey(msg.CurrencyType), $"Explorer link not defined for [{msg.CurrencyType}]");

            // calc amounts
            var exchangeRate = Convert.ToDecimal(await _rateCalculatorClient.GetBestPriceAsync(_assetPairs[msg.CurrencyType], true));
            var usdAmount = msg.Amount * exchangeRate;

            if (exchangeRate == 0M)
            {
                throw new InvalidOperationException($"Exchange rate for [{msg.CurrencyType}] not found");
            }

            // save transaction info for investor 
            await _cryptoInvestmentRepository.SaveAsync(investorEmail, 
                msg.TransactionId, 
                msg.BlockId, 
                msg.BlockTimestamp, 
                msg.DestinationAddress, 
                msg.CurrencyType,
                msg.Amount,
                exchangeRate,
                usdAmount);

            var assetName = _assetNames[msg.CurrencyType];
            var assetLink = _assetLinks[msg.CurrencyType](msg.DestinationAddress);
            var transactionHash = msg.TransactionId.Split("-").First();

            // send confirmation email
            await _investmentMailSender.SendAsync(new InvestorNewTransactionMessage
            {
                EmailTo = investorEmail,
                Payment = $"{msg.Amount} {assetName}",
                TransactionLink = $"{assetLink}/{transactionHash}"
            });

            // increase the total ICO amount
            await _campaignInfoRepository.IncrementValue(CampaignInfoType.AmountInvestedUsd, usdAmount);

            // log full transaction info
            await _log.WriteInfoAsync(_component, _process, msg.ToJson(), "Investment transaction processed");
        }
    }
}
