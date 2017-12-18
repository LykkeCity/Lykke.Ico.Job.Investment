using Lykke.Ico.Core.Repositories.CampaignSettings;
using System;

namespace Lykke.Job.IcoInvestment.Core.Extensions
{
    public static class ICampaignSettingsExtensions
    {
        public static bool IsPreSale(this ICampaignSettings self, DateTime txCreatedUtc)
        {
            return txCreatedUtc >= self.PreSaleStartDateTimeUtc && txCreatedUtc <= self.PreSaleEndDateTimeUtc;
        }

        public static bool IsCrowdSale(this ICampaignSettings self, DateTime txCreatedUtc)
        {
            return txCreatedUtc >= self.CrowdSaleStartDateTimeUtc && txCreatedUtc <= self.CrowdSaleEndDateTimeUtc;
        }
    }
}
