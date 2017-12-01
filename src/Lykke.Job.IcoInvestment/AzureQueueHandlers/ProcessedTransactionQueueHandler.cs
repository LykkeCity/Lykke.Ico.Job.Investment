using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Common.Log;
using Lykke.Job.IcoInvestment.Core.Domain.Transactions;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.JobTriggers.Triggers.Attributes;

namespace Lykke.Job.IcoInvestment.AzureQueueHandlers
{
    public class ProcessedTransactionQueueHandler
    {
        private ILog _log;
        private IProcessedTransactionService _processedTxService;

        public ProcessedTransactionQueueHandler(ILog log, IProcessedTransactionService processedTxService)
        {
            _log = log;
            _processedTxService = processedTxService;
        }

        [QueueTrigger(ProcessedTransactionMessage.QUEUE_NAME)]
        public async Task HandleProcessedTransactionMessage(ProcessedTransactionMessage msg)
        {
            await _processedTxService.Process(msg);
        }
    }
}
