using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyKeys.Postgres;

public static class PostgresExtensions
{
    public static IServiceCollection AddNpgsqlIdempotencyStore(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<IIdempotencyStore>(new NpgsqlIdempotencyStore(connectionString));
        return services;
    }
}
