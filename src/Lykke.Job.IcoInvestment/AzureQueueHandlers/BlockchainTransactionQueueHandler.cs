using System;
using System.Threading.Tasks;
using Common.Log;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Queues.Transactions;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.JobTriggers.Triggers.Attributes;
using Newtonsoft.Json;

namespace Lykke.Job.IcoInvestment.AzureQueueHandlers
{
    public class BlockchainTransactionQueueHandler
    {
        private ILog _log;
        private IBlockchainTransactionService _blockchainTxService;

        public BlockchainTransactionQueueHandler(ILog log, IBlockchainTransactionService blockchainTxService)
        {
            _log = log;
            _blockchainTxService = blockchainTxService;
        }
        
        [QueueTrigger(Consts.Transactions.Queues.BlockchainTransaction)]
        public async Task HandleBlockchainTransactionMessage(BlockchainTransactionMessage msg)
        {
            try
            {
                await _blockchainTxService.Process(msg);
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(
                    nameof(BlockchainTransactionQueueHandler), 
                    nameof(HandleBlockchainTransactionMessage),
                    JsonConvert.SerializeObject(msg),
                    ex);

                throw;
            }
        }
    }
}   
