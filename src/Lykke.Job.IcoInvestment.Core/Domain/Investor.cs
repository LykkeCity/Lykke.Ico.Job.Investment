using Lykke.Ico.Core.Repositories.Investor;
using System;

namespace Lykke.Job.IcoInvestment.Core.Domain
{
    [Serializable]
    public class Investor : IInvestor
    {
        public string Email { get; set; }

        public string TokenAddress { get; set; }

        public string RefundEthAddress { get; set; }

        public string RefundBtcAddress { get; set; }

        public string PayInEthPublicKey { get; set; }

        public string PayInEthAddress { get; set; }

        public string PayInBtcPublicKey { get; set; }

        public string PayInBtcAddress { get; set; }

        public DateTime UpdatedUtc { get; set; }

        public Guid? ConfirmationToken { get; set; }

        public DateTime? ConfirmationTokenCreatedUtc { get; set; }

        public DateTime? ConfirmedUtc { get; set; }

        public string KycRequestId { get; set; }

        public DateTime? KycRequestedUtc { get; set; }

        public bool? KycPassed { get; set; }

        public DateTime? KycPassedUtc { get; set; }

        public DateTime? KycManuallyUpdatedUtc { get; set; }

        public decimal AmountBtc { get; set; }

        public decimal AmountEth { get; set; }

        public decimal AmountFiat { get; set; }

        public decimal AmountUsd { get; set; }

        public decimal AmountToken { get; set; }

        public string ReferralCode { get; set; }

        public DateTime? ReferralCodeUtc { get; set; }

        public string ReferralCodeApplied { get; set; }

        public DateTime? ReferralCodeAppliedUtc { get; set; }

        public int ReferralsNumber { get; set; }

        public DateTime? ReferralsNumberUtc { get; set; }
    }
}
