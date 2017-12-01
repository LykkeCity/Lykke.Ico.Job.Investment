using Autofac;
using Autofac.Extensions.DependencyInjection;
using Common.Log;
using Lykke.Ico.Core.Queues;
using Lykke.Ico.Core.Queues.Emails;
using Lykke.Ico.Core.Repositories.CampaignInfo;
using Lykke.Ico.Core.Repositories.CryptoInvestment;
using Lykke.Ico.Core.Repositories.Investor;
using Lykke.Ico.Core.Repositories.InvestorAttribute;
using Lykke.Job.IcoInvestment.Core.Services;
using Lykke.Job.IcoInvestment.Core.Settings.JobSettings;
using Lykke.Job.IcoInvestment.Services;
using Lykke.JobTriggers.Extenstions;
using Lykke.Service.IcoExRate.Client;
using Lykke.SettingsReader;
using Microsoft.Extensions.DependencyInjection;

namespace Lykke.Job.IcoInvestment.Modules
{
    public class JobModule : Module
    {
        private readonly IcoInvestmentSettings _settings;
        private readonly IReloadingManager<DbSettings> _dbSettingsManager;
        private readonly IReloadingManager<AzureQueueSettings> _azureQueueSettingsManager;
        private readonly ILog _log;
        // NOTE: you can remove it if you don't need to use IServiceCollection extensions to register service specific dependencies
        private readonly IServiceCollection _services;

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

            _services = new ServiceCollection();
        }

        protected override void Load(ContainerBuilder builder)
        {
            // NOTE: Do not register entire settings in container, pass necessary settings to services which requires them
            // ex:
            // builder.RegisterType<QuotesPublisher>()
            //  .As<IQuotesPublisher>()
            //  .WithParameter(TypedParameter.From(_settings.Rabbit.ConnectionString))

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

            builder.RegisterType<CryptoInvestmentRepository>()
                .As<ICryptoInvestmentRepository>()
                .WithParameter(TypedParameter.From(_dbSettingsManager.Nested(x => x.DataConnString)));

            builder.RegisterType<QueuePublisher<InvestorNewTransactionMessage>>()
                .As<IQueuePublisher<InvestorNewTransactionMessage>>()
                .WithParameter(TypedParameter.From(_azureQueueSettingsManager.Nested(x => x.ConnectionString)));

            builder.RegisterType<QueuePublisher<InvestorKycRequestMessage>>()
                .As<IQueuePublisher<InvestorKycRequestMessage>>()
                .WithParameter(TypedParameter.From(_azureQueueSettingsManager.Nested(x => x.ConnectionString)));

            builder.RegisterType<BlockchainTransactionService>()
                .As<IBlockchainTransactionService>()
                .WithParameter(TypedParameter.From(_settings.Ico))
                .SingleInstance();

            RegisterAzureQueueHandlers(builder);

            // TODO: Add your dependencies here

            builder.Populate(_services);
        }

        private void RegisterAzureQueueHandlers(ContainerBuilder builder)
        {
            // NOTE: You can implement your own poison queue notifier for azure queue subscription.
            // See https://github.com/LykkeCity/JobTriggers/blob/master/readme.md
            // builder.Register<PoisionQueueNotifierImplementation>().As<IPoisionQueueNotifier>();

            builder.AddTriggers(
                pool =>
                {
                    pool.AddDefaultConnection(_settings.AzureQueue.ConnectionString);
                });
        }
    }
}
