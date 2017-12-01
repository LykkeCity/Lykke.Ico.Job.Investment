using System;
using Lykke.Ico.Core;
using Lykke.Ico.Core.Repositories.CryptoInvestment;

namespace Lykke.Job.IcoInvestment.Core.Domain.CryptoInvestments
{
    public class CryptoInvestment : ICryptoInvestment
    {
        public string InvestorEmail { get; set; }
        public string TransactionId { get; set; }
        public string BlockId { get; set; }
        public DateTime BlockTimestamp { get; set; }
        public string DestinationAddress { get; set; }
        public CurrencyType CurrencyType { get; set; }
        public decimal Amount { get; set; }
        public decimal ExchangeRate { get; set; }
        public decimal AmountUsd { get; set; }
        public decimal Price { get; set; }
        public decimal AmountVld { get; set; }
        public string Context { get; set; }
        public DateTime? EmailTimestamp { get; set; }
    }
}
