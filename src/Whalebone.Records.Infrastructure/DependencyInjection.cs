using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Whalebone.Records.Application.Abstractions;
using Whalebone.Records.Infrastructure.Persistence;

namespace Whalebone.Records.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<RecordsDbContext>((provider, builder) =>
        {
            // Resolved lazily from options, so tests can point the real provider at a
            // throwaway container without replacing the registration itself.
            var options = provider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;

            builder.UseNpgsql(options.ConnectionString, npgsql =>
                npgsql.EnableRetryOnFailure(options.MaxRetryCount, TimeSpan.FromSeconds(5), errorCodesToAdd: null));
        });

        services.AddScoped<IRecordRepository, RecordRepository>();

        services.AddHealthChecks()
            .AddDbContextCheck<RecordsDbContext>("database", tags: ["ready"]);

        return services;
    }
}
