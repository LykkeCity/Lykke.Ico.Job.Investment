using System.Threading.Tasks;
using Lykke.Ico.Core.Queues.Transactions;

namespace Lykke.Job.IcoInvestment.Core.Services
{
    public interface IBlockchainTransactionService
    {
        Task Process(BlockchainTransactionMessage message); 
    }
}
