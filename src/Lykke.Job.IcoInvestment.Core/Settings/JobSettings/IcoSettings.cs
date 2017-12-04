using System;

namespace Lykke.Job.IcoInvestment.Core.Settings.JobSettings
{
    public class IcoSettings
    {
        public decimal KycThreshold { get; set; }
        public DateTime CampaignStartDateTime { get; set; }
        public decimal BasePrice { get; set; }
        public int TokenDecimals { get; set; }
    }
}
