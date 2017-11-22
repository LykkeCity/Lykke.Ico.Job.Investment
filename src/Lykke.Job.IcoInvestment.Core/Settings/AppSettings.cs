using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;
using Lykke.Job.IcoInvestment.Core.Settings.SlackNotifications;

namespace Lykke.Job.IcoInvestment.Core.Settings
{
    public class AppSettings
    {
        public IcoInvestmentSettings IcoInvestmentJob { get; set; }
        public SlackNotificationsSettings SlackNotifications { get; set; }
    }
}