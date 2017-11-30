using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Common;
using Common.Log;
using Lykke.Job.IcoInvestment.Core.Services;

namespace Lykke.Job.IcoInvestment.PeriodicalHandlers
{
    public class FlushHandler : TimerPeriod
    {
        private ILog _log;
        private IBlockchainTransactionService _blockchainService;

        public FlushHandler(ILog log, IBlockchainTransactionService blockchainService) : 
            base(nameof(FlushHandler), 60 * 1000, log)
        {
            _log = log;
            _blockchainService = blockchainService;
        }

        public override async Task Execute()
        {
            try
            {
                await _blockchainService.FlushProcessInfo();
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync(nameof(FlushHandler), nameof(Execute), string.Empty, ex);
            }
        }
    }
}
