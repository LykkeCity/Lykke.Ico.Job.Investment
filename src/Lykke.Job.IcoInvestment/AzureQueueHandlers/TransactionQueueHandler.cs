using System.Threading.Tasks;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.JobTriggers.Triggers.Attributes;
using System;
using Common;

namespace Lykke.Job.IcoInvestment.AzureQueueHandlers
{
    public class TransactionQueueHandler
    {
        private ILog _log;
        private ITransactionService _blockchainTxService;

        public TransactionQueueHandler(ILog log, ITransactionService blockchainTxService)
        {
            _log = log;
            _blockchainTxService = blockchainTxService;
        }
        
        [QueueTrigger(Consts.Transactions.Queues.Investor)]
        public async Task HandleTransactionMessage(TransactionMessage msg)
        {
            try
            {
                await _blockchainTxService.Process(msg);
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(nameof(TransactionQueueHandler), nameof(HandleTransactionMessage),
                    $"Failed to process message: {msg.ToJson()}", ex);
                throw;
            }
        }
    }
}   
