using BlueDrive.Api.Infrastructure;
using BlueDrive.Application;
using BlueDrive.Infrastructure;
using BlueDrive.Infrastructure.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, sp, cfg) => cfg.ConfigureBlueDrive(ctx.Configuration, "api"));

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddApiServices(builder.Configuration, builder.Environment);

    var app = builder.Build();

    await app.InitializeDatabaseAsync();
    app.UseApiPipeline();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API dừng do lỗi không xử lý được");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
