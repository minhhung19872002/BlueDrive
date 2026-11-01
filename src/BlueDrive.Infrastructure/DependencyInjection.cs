using BlueDrive.Application.Abstractions;
using BlueDrive.Infrastructure.Auditing;
using BlueDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BlueDrive.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("Thiếu cấu hình ConnectionStrings:Default.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(cs).Build());
        services.AddScoped<AuditableInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npg => npg.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<AuditableInterceptor>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddMemoryCache();
        return services;
    }
}
