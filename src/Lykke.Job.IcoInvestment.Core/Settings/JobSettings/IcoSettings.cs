using System;
using System.Collections.Generic;
using System.Text;

namespace Lykke.Job.IcoInvestment.Core.Settings.JobSettings
{
    public class IcoSettings
    {
        public Decimal KycUsdThreshold { get; set; }
        public DateTimeOffset CampaignStartDateTime { get; set; }
        public Decimal TokenPrice { get; set; }
    }
}
