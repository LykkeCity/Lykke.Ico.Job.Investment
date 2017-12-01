using System;
using System.Collections.Generic;
using System.Text;
using Lykke.Ico.Core.Queues;

namespace Lykke.Job.IcoInvestment.Core.Domain.Transactions
{
    [QueueMessage(QueueName = QUEUE_NAME)]
    public class ProcessedTransactionMessage : IMessage
    {
        public const string QUEUE_NAME = "transaction-processed";

        public string InvestorEmail { get; set; }
        public string TransactionId { get; set; }
        public string Link { get; set; }

        public static ProcessedTransactionMessage Create(string investorEmail, string transactionId, string link)
        {
            return new ProcessedTransactionMessage
            {
                InvestorEmail = investorEmail,
                TransactionId = transactionId,
                Link = link
            };
        }
    }
}
