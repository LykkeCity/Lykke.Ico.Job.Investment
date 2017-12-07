using System.Threading.Tasks;
using Lykke.Ico.Core.Queues.Transactions;

namespace Lykke.Job.IcoInvestment.Core.Services
{
    public interface ITransactionService
    {
        Task Process(TransactionMessage message);
    }
}
