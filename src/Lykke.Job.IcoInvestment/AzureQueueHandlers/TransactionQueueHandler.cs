using System.Threading.Tasks;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.JobTriggers.Triggers.Attributes;

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
        
        [QueueTrigger(Consts.Transactions.Queues.BlockchainTransaction)]
        public async Task HandleBlockchainTransactionMessage(BlockchainTransactionMessage msg)
        {
            await _blockchainTxService.Process(msg);
        }
    }
}   
