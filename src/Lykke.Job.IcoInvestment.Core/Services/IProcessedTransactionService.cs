using System.Threading.Tasks;
using Lykke.Job.IcoInvestment.Core.Domain.Transactions;

namespace Lykke.Job.IcoInvestment.Core.Services
{
    public interface IProcessedTransactionService
    {
        Task Process(ProcessedTransactionMessage message);
    }
}
