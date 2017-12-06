using Lykke.Ico.Core.Repositories.CampaignSettings;
using System;

namespace Lykke.Job.IcoInvestment.Core.Domain
{
    public class CampaignSettings : ICampaignSettings
    {
        public DateTime StartDateTimeUtc { get; set; }

        public DateTime EndDateTimeUtc { get; set; }

        public int TotalTokensAmount { get; set; }

        public decimal TokenBasePriceUsd { get; set; }

        public int TokenDecimals { get; set; }

        public decimal MinInvestAmountUsd { get; set; }
    }
}
