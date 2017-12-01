using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Common;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Repositories.CryptoInvestment;
using Lykke.Ico.Core.Repositories.Investor;
using Lykke.Job.IcoInvestment.Core.Domain.Transactions;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;

namespace Lykke.Job.IcoInvestment.Services
{
    public class ProcessedTransactionService : IProcessedTransactionService
    {
        private readonly ILog _log;
        private readonly ICryptoInvestmentRepository _cryptoInvestmentRepository;
        private readonly IInvestorRepository _investorRepository;
        private readonly IQueuePublisher<InvestorNewTransactionMessage> _investmentMailSender;
        private readonly IQueuePublisher<InvestorKycRequestMessage> _kycMailSender;
        private readonly IcoSettings _icoSettings;
        private readonly string _component = nameof(ProcessedTransactionService);
        private readonly string _process = nameof(Process);

        private readonly Dictionary<CurrencyType, string> _assetNames = new Dictionary<CurrencyType, string>
        {
            { CurrencyType.Bitcoin, "BTC" },
            { CurrencyType.Ether, "ETH" }
        };

        public ProcessedTransactionService(
            ILog log,
            ICryptoInvestmentRepository cryptoInvestmentRepository,
            IInvestorRepository investorRepository,
            IQueuePublisher<InvestorNewTransactionMessage> investmentMailSender,
            IQueuePublisher<InvestorKycRequestMessage> kycMailSender,
            IcoSettings icoSettings)
        {
            _log = log;
            _cryptoInvestmentRepository = cryptoInvestmentRepository;
            _investorRepository = investorRepository;
            _investmentMailSender = investmentMailSender;
            _kycMailSender = kycMailSender;
            _icoSettings = icoSettings;
        }

        public async Task Process(ProcessedTransactionMessage msg)
        {
            // The idea is to allow blockchain re-processing but prevent double-sending emails and double-incrementing amount.
            // Implement that with dedicated queue for processing investor details and re-calculating on each transaction.

            var investments = await _cryptoInvestmentRepository.GetInvestmentsAsync(msg.InvestorEmail);
            var investor = await _investorRepository.GetAsync(msg.InvestorEmail);

            // send investment confirmation(s) if required,
            // if smth was not sent previously 
            // then it will be sent on next tx processing or re-processing of previous tx
            var unconfirmed = investments
                .Where(x => x.EmailTimestamp == null)
                .ToArray();

            foreach (var i in unconfirmed)
            {
                var email = new InvestorNewTransactionMessage
                {
                    EmailTo = i.InvestorEmail,
                    Payment = $"{i.Amount} {_assetNames[i.CurrencyType]}",
                    TransactionLink = msg.Link
                };

                await _investmentMailSender.SendAsync(email);

                await _cryptoInvestmentRepository.SaveEmailTimestampAsync(i.InvestorEmail, i.TransactionId);

                await _log.WriteInfoAsync(_component, _process, email.ToJson(), 
                    $"Investment confirmation sent to {i.InvestorEmail}");
            }

            // re-calculate and update investor details
            investor.AmountBtc = investments.Where(x => x.CurrencyType == CurrencyType.Bitcoin).Sum(x => x.Amount);
            investor.AmountEth = investments.Where(x => x.CurrencyType == CurrencyType.Ether).Sum(x => x.Amount);
            investor.AmountUsd = investments.Sum(x => x.AmountUsd);
            investor.AmountVld = investments.Sum(x => x.AmountVld);

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
    }
}
