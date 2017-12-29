using Lykke.Ico.Core.Helpers;
using Lykke.Job.IcoInvestment.Core.Services;

namespace Lykke.Job.IcoInvestment.Services
{
    public class EncryptionService : IEncryptionService
    {
        private readonly string _key;
        private readonly string _iv;

        public EncryptionService(string key, string iv)
        {
            _key = key;
            _iv = iv;
        }

        public string Encrypt(string message)
        {
            return EncryptionHelper.Decrypt(message, _key, _iv);
        }
    }
}
