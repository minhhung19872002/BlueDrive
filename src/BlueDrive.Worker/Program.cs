using BlueDrive.Application;
using BlueDrive.Application.Abstractions;
using BlueDrive.Infrastructure;
using BlueDrive.Infrastructure.Jobs;
using BlueDrive.Infrastructure.Logging;
using BlueDrive.Infrastructure.Security;
using BlueDrive.Worker;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog((sp, cfg) => cfg.ConfigureBlueDrive(builder.Configuration, "worker"));

    builder.Services.AddScoped<SystemCurrentUser>();
    builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<SystemCurrentUser>());
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddBlueDriveHangfire(builder.Configuration, runServer: true);
    builder.Services.AddHostedService<RecurringJobsRegistrar>();

    await builder.Build().RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Worker dừng do lỗi không xử lý được");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

