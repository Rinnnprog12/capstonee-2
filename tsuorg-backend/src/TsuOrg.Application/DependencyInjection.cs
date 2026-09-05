using Microsoft.Extensions.DependencyInjection;

namespace TsuOrg.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Register application services / handlers here as features are built.
        return services;
    }
}
