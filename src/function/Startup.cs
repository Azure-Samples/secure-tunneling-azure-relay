using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(SecureTunneling.Functions.Startup))]

namespace SecureTunneling.Functions
{
    /// <summary>
    /// Startup class.
    /// </summary>
    public class Startup : FunctionsStartup
    {
        /// <inheritdoc/>
        public override void Configure(IFunctionsHostBuilder builder)
        {
            FunctionsHostBuilderContext context = builder.GetContext();
            builder.Services.Configure<SecureTunnelingConfiguration>(context.Configuration);
        }

        /// <inheritdoc/>
        public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
        {
            builder.ConfigurationBuilder.AddEnvironmentVariables();
        }
    }
}