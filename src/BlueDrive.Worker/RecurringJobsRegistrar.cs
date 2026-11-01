namespace BlueDrive.Worker;

/// <summary>Registers recurring jobs (trash purge, backups, quota checks...) once the host starts.</summary>
public sealed class RecurringJobsRegistrar(IServiceProvider services, ILogger<RecurringJobsRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        foreach (var registration in scope.ServiceProvider.GetServices<BlueDrive.Infrastructure.Jobs.IRecurringJobRegistration>())
        {
            await registration.RegisterAsync(ct);
            logger.LogInformation("Đã đăng ký job định kỳ {Job}", registration.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
