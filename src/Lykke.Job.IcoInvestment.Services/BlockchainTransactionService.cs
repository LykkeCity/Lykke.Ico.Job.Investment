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
using Lykke.Ico.Core.Repositories.CryptoInvestment;
using Lykke.Ico.Core.Repositories.Investor;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Domain.CryptoInvestments;
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
            ICryptoInvestmentRepository cryptoInvestmentRepository,
            IInvestorRepository investorRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender,
            IQueuePublisher<InvestorKycRequestMessage> kycMailSender,
            IcoSettings icoSettings)
        {
            _log = log;
            _exRateClient = exRateClient;
            _investorAttributeRepository = investorAttributeRepository;
            _campaignInfoRepository = campaignInfoRepository;
            _cryptoInvestmentRepository = cryptoInvestmentRepository;
            _investorRepository = investorRepository;
            _investmentMailSender = investmentMailSender;
            _kycMailSender = kycMailSender;
            _icoSettings = icoSettings;
        }

        public async Task Process(BlockchainTransactionMessage msg)
        {
            if (string.IsNullOrWhiteSpace(msg.InvestorEmail))
            {
                // if investor is not specified try to find her by address
                var investorAttributeType = msg.CurrencyType == CurrencyType.Bitcoin
                    ? InvestorAttributeType.PayInBtcAddress
                    : InvestorAttributeType.PayInEthAddress;

                msg.InvestorEmail = await _investorAttributeRepository.GetInvestorEmailAsync(investorAttributeType, msg.DestinationAddress);
            }

            if (string.IsNullOrWhiteSpace(msg.InvestorEmail))
            {
                // if investor still not found then it means that destination address
                // is not a cash-in address of any ICO investor
                return;
            }

            var timeSpan = msg.BlockTimestamp.UtcDateTime - _icoSettings.CampaignStartDateTime.ToUniversalTime();

            if (timeSpan < TimeSpan.Zero ||
                timeSpan > TimeSpan.FromDays(21))
            {
                throw new InvalidOperationException($"Investment {msg.ToJson()} is out of crowd-sale terms");
            }

            var exchangeRate = await GetExchangeRate(msg.CurrencyType == CurrencyType.Bitcoin ? Pair.BTCUSD : Pair.ETHUSD, msg.BlockTimestamp);

            if (exchangeRate == null ||
                exchangeRate.AverageRate == null)
            {
                throw new InvalidOperationException($"Exchange rate not found for investment {msg.ToJson()}");
            }

            var totalVld = 0M;

            if (!decimal.TryParse(await _campaignInfoRepository.GetValueAsync(CampaignInfoType.AmountInvestedVld), out totalVld))
            {
                totalVld = 0M;
            }

            var avgExchangeRate = Convert.ToDecimal(exchangeRate.AverageRate);
            var price = GetPrice(totalVld, msg.BlockTimestamp);
            var amountUsd = msg.Amount * avgExchangeRate;
            var amountVld = DecimalExtensions.RoundDown(amountUsd / price, 4); // round down to 4 decimal places

            // save transaction info for investor 
            await _cryptoInvestmentRepository.SaveAsync(new CryptoInvestment
            {
                InvestorEmail = msg.InvestorEmail,
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
            });

            // log full transaction info
            await _log.WriteInfoAsync(_component, _process, msg.ToJson(), "Investment processed");

            await SendConfirmationEmail(msg);

            await UpdateCampaignDetails(msg, amountUsd, amountVld);

            await UpdateInvestorDetails(msg, amountUsd, amountVld);
        }

        private async Task SendConfirmationEmail(BlockchainTransactionMessage msg)
        {
            var asset = msg.CurrencyType == CurrencyType.Bitcoin ? "BTC" : "ETH";
            var email = new InvestorNewTransactionMessage
            {
                EmailTo = msg.InvestorEmail,
                Payment = $"{msg.Amount} {asset}",
                TransactionLink = msg.Link
            };

            await _investmentMailSender.SendAsync(email);

            await _log.WriteInfoAsync(_component, _process, email.ToJson(),
                $"Investment confirmation sent to {msg.InvestorEmail}");
        }

        private async Task UpdateCampaignDetails(BlockchainTransactionMessage msg, decimal amountUsd, decimal amountVld)
        {
            // increase the total ICO amounts
            await _campaignInfoRepository.IncrementValue(CampaignInfoType.AmountInvestedVld, amountVld);
            await _campaignInfoRepository.IncrementValue(CampaignInfoType.AmountInvestedUsd, amountUsd);
            await _campaignInfoRepository.IncrementValue(msg.CurrencyType == CurrencyType.Bitcoin ? CampaignInfoType.AmountInvestedBtc : CampaignInfoType.AmountInvestedBtc, msg.Amount);
        }

        private async Task UpdateInvestorDetails(BlockchainTransactionMessage msg, decimal amountUsd, decimal amountVld)
        {
            var investor = await _investorRepository.GetAsync(msg.InvestorEmail);

            if (msg.CurrencyType == CurrencyType.Bitcoin)
            {
                investor.AmountBtc += msg.Amount;
            }
            else
            {
                investor.AmountEth += msg.Amount;
            }

            investor.AmountUsd += amountUsd;
            investor.AmountVld += amountVld;

            if (investor.AmountUsd >= _icoSettings.KycThreshold && string.IsNullOrEmpty(investor.KycProcessId))
            {
                // TODO: get actual KYC identitfier from provider
                investor.KycProcessId = Guid.NewGuid().ToString();

                await _kycMailSender.SendAsync(InvestorKycRequestMessage.Create(investor.Email, investor.KycProcessId));

                await _log.WriteInfoAsync(_component, _process, investor.KycProcessId,
                    $"KYC requested for {investor.Email}");
            }

            await _investorRepository.UpdateAsync(investor);
        }

        private async Task<AverageRateResponse> GetExchangeRate(Pair assetPair, DateTimeOffset blockTimestamp)
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

        private decimal GetPrice(decimal currentTotal, DateTimeOffset blockTimestamp)
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
