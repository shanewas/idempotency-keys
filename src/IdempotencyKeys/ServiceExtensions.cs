using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyKeys;

public static class ServiceExtensions
{
    public static IServiceCollection AddIdempotencyKeys(this IServiceCollection services, Action<IdempotencyOptions>? configure = null)
    {
        var opt = new IdempotencyOptions();
        configure?.Invoke(opt);
        services.AddSingleton(opt);
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return services;
    }

    public static IApplicationBuilder UseIdempotencyKeys(this IApplicationBuilder app)
        => app.UseMiddleware<IdempotencyKeysMiddleware>();
}
