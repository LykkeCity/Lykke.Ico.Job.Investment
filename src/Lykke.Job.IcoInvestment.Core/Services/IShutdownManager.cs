using System.Threading.Tasks;

namespace Lykke.Job.IcoInvestment.Core.Services
{
    public interface IShutdownManager
    {
        Task StopAsync();
    }
}