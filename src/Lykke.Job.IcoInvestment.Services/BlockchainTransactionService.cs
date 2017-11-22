using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Ico.Core.Repositories.Campaign;
using Lykke.Ico.Core.Repositories.CryptoInvestment;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.Service.RateCalculator.Client;

namespace Lykke.Job.IcoInvestment.Services
{
    public class BlockchainTransactionService : IBlockchainTransactionService
    {
        private readonly IRateCalculatorClient _rateCalculatorClient;
        private readonly IInvestorAttributeRepository _investorAttributeRepository;
        private readonly ICampaignRepository _campaignRepository;
        private readonly ICryptoInvestmentRepository _cryptoInvestmentRepository;
        private readonly IQueuePublisher<InvestorNewTransactionMessage> _investmentMailSender;
        private readonly Dictionary<CurrencyType, string> _assetPairs = new Dictionary<CurrencyType, string>
        {
            { CurrencyType.Bitcoin, "BTCUSD" },
            { CurrencyType.Ether, "ETHUSD" }
        };

        public BlockchainTransactionService(
            IRateCalculatorClient rateCalculatorClient, 
            IInvestorAttributeRepository investorAttributeRepository, 
            ICampaignRepository campaignRepository, 
            ICryptoInvestmentRepository cryptoInvestmentRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender)
        {
            _rateCalculatorClient = rateCalculatorClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignRepository = campaignRepository;
            _cryptoInvestmentRepository = cryptoInvestmentRepository;
            _investmentMailSender = investmentMailSender;
        }

        public async Task Process(BlockchainTransactionMessage msg)
        {
            var investorAttributeType = msg.CurrencyType == CurrencyType.Bitcoin
                ? InvestorAttributeType.BtcPublicKey 
                : InvestorAttributeType.EthPublicKey;

            var investorEmail = await _investorAttributeRepository.GetInvestorEmailAsync(investorAttributeType, msg.DestinationAddress);

            if (string.IsNullOrWhiteSpace(investorEmail))
            {
                // destination address is not a cash-in address of any ICO investor
                return;
            }

            if (!_assetPairs.ContainsKey(msg.CurrencyType))
            {
                throw new InvalidOperationException($"Unknown currency: [{msg.CurrencyType}]");
            }

            // calc amounts
            var exchangeRate = Convert.ToDecimal(await _rateCalculatorClient.GetBestPriceAsync(_assetPairs[msg.CurrencyType], true));
            var usdAmount = msg.Amount * exchangeRate;

            if (exchangeRate == 0M)
            {
                throw new InvalidOperationException($"Exchange rate for [{msg.CurrencyType}] not found. Transaction [{msg.TransactionId}] skipped.");
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

            // send confirmation email
            await _investmentMailSender.SendAsync(new InvestorNewTransactionMessage
            {
                Amount = msg.Amount.ToString(),
                CurrencyType = msg.CurrencyType,
                EmailTo = investorEmail
            });

            // increase the total ICO amount
            await _campaignRepository.IncreaseTotalRaisedAsync(usdAmount);
        }
    }
}
