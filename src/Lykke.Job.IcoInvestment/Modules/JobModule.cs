using Autofac;
using Autofac.Extensions.DependencyInjection;
using Common.Log;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Repositories.CampaignInfo;
using Lykke.Ico.Core.Repositories.CampaignSettings;
using Lykke.Ico.Core.Repositories.Investor;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Ico.Core.Repositories.InvestorRefund;
using Lykke.Ico.Core.Repositories.InvestorTransaction;
using Lykke.Ico.Core.Services;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;
using Lykke.Job.IcoInvestment.Services;
using Lykke.JobTriggers.Extenstions;
using Lykke.Service.IcoExRate.Client;
using Lykke.SettingsReader;

namespace Lykke.Job.IcoInvestment.Modules
{
    public class JobModule : Module
    {
        private readonly IcoInvestmentSettings _settings;
        private readonly IReloadingManager<DbSettings> _dbSettingsManager;
        private readonly IReloadingManager<AzureQueueSettings> _azureQueueSettingsManager;
        private readonly ILog _log;

        public JobModule(
            IcoInvestmentSettings settings, 
            IReloadingManager<DbSettings> dbSettingsManager, 
            IReloadingManager<AzureQueueSettings> azureQueueSettingsManager,
            ILog log)
        {
            _settings = settings;
            _log = log;
            _dbSettingsManager = dbSettingsManager;
            _azureQueueSettingsManager = azureQueueSettingsManager;
        }

        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterInstance(_log)
                .As<ILog>()
                .SingleInstance();

            builder.RegisterType<HealthService>()
                .As<IHealthService>()
                .SingleInstance();

            builder.RegisterType<StartupManager>()
                .As<IStartupManager>();

            builder.RegisterType<ShutdownManager>()
                .As<IShutdownManager>();

            builder.RegisterIcoExRateClient(_settings.IcoExRateServiceUrl, _log);

            builder.RegisterType<InvestorRepository>()
               .As<IInvestorRepository>()
               .WithParameter(TypedParameter.From(_dbSettingsManager.Nested(x => x.DataConnString)));

            builder.RegisterType<InvestorAttributeRepository>()
                .As<IInvestorAttributeRepository>()
                .WithParameter(TypedParameter.From(_dbSettingsManager.Nested(x => x.DataConnString)));

            builder.RegisterType<CampaignInfoRepository>()
                .As<ICampaignInfoRepository>()
                .WithParameter(TypedParameter.From(_dbSettingsManager.Nested(x => x.DataConnString)));

            builder.RegisterType<CampaignSettingsRepository>()
                .As<ICampaignSettingsRepository>()
                .WithParameter(TypedParameter.From(_dbSettingsManager.Nested(x => x.DataConnString)));

            builder.RegisterType<InvestorTransactionRepository>()
                .As<IInvestorTransactionRepository>()
                .WithParameter(TypedParameter.From(_dbSettingsManager.Nested(x => x.DataConnString)));

            builder.RegisterType<InvestorRefundRepository>()
                            .As<IInvestorRefundRepository>()
                            .WithParameter(TypedParameter.From(_dbSettingsManager.Nested(x => x.DataConnString)));

            builder.RegisterType<QueuePublisher<InvestorNewTransactionMessage>>()
                .As<IQueuePublisher<InvestorNewTransactionMessage>>()
                .WithParameter(TypedParameter.From(_azureQueueSettingsManager.Nested(x => x.ConnectionString)));

            builder.RegisterType<TransactionService>()
                .As<ITransactionService>()
                .WithParameter("siteSummaryPageUrl", _settings.SiteSummaryPageUrl)
                .SingleInstance();

            builder.RegisterType<UrlEncryptionService>()
                .As<IUrlEncryptionService>()
                .WithParameter("key", _settings.KycServiceEncriptionKey)
                .WithParameter("iv", _settings.KycServiceEncriptionIv)
                .SingleInstance();

            builder.RegisterType<KycService>()
                .As<IKycService>()
                .SingleInstance();

            RegisterAzureQueueHandlers(builder);
        }

        private void RegisterAzureQueueHandlers(ContainerBuilder builder)
        {
            builder.AddTriggers(
                pool =>
                {
                    pool.AddDefaultConnection(_settings.AzureQueue.ConnectionString);
                });
        }
    }
}
