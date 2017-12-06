﻿using System;

namespace Lykke.Job.IcoInvestment.Core.Settings.JobSettings
{
    public class IcoInvestmentSettings
    {
        public AzureQueueSettings AzureQueue { get; set; }
        public DbSettings Db { get; set; }
        public String IcoExRateServiceUrl { get; set; }
    }
}
