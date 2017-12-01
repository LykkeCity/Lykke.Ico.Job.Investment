using System;
using System.Collections.Generic;
using System.Text;

namespace Lykke.Job.IcoInvestment.Core.Settings.JobSettings
{
    public class IcoSettings
    {
        public Decimal KycThreshold { get; set; }
        public DateTime CampaignStartDateTime { get; set; }
        public Decimal BasePrice { get; set; }
    }
}
