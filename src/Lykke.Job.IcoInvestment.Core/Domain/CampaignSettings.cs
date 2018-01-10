using Lykke.Ico.Core.Repositories.CampaignSettings;
using System;

namespace Lykke.Job.IcoInvestment.Core.Domain
{
    public class CampaignSettings : ICampaignSettings
    {
        public DateTime PreSaleStartDateTimeUtc { get; set; }

        public DateTime PreSaleEndDateTimeUtc { get; set; }

        public int PreSaleTotalTokensAmount { get; set; }

        public DateTime CrowdSaleStartDateTimeUtc { get; set; }

        public DateTime CrowdSaleEndDateTimeUtc { get; set; }

        public int CrowdSaleTotalTokensAmount { get; set; }

        public decimal TokenBasePriceUsd { get; set; }

        public int TokenDecimals { get; set; }

        public decimal MinInvestAmountUsd { get; set; }

        public bool KycEnableRequestSending { get; set; }

        public string KycLinkTemplate { get; set; }
    }
}
