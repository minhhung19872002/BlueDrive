using System.Reflection;
using BlueDrive.Application.Abstractions;
using BlueDrive.Infrastructure.Jobs;
using BlueDrive.Api.Storage;
using BlueDrive.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;

namespace BlueDrive.Api.Infrastructure;

public static class ApiSetup
{
    public static IServiceCollection AddApiServices(this IServiceCollection services, IConfiguration config, IWebHostEnvironment env)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddControllers(o => o.ModelBinderProviders.Insert(0, new SnakeCaseEnumBinderProvider()))
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
            })
            .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx => ProblemFactory.FromModelState(ctx));
        services.AddBlueDriveSecurity(config);
        services.AddSignalR();
        services.AddHostedService<BlueDrive.Api.Realtime.RealtimeRelay>();
        services.AddHostedService<BlueDrive.Api.Storage.TusCleanupService>();
        services.AddBlueDriveHangfire(config, runServer: false);
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "BlueDrive API",
                Version = "v1",
                Description = "API hệ thống phần mềm lưu trữ sản phẩm truyền thông dân số (BlueDrive).",
            });
            var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml)) o.IncludeXmlComments(xml);
            o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "Nhập access token JWT (không cần tiền tố 'Bearer ').",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
            });
            o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] });
        });

        services.AddHealthChecks()
            .AddNpgSql(config.GetConnectionString("Default")!, name: "postgres", tags: ["ready"]);

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
        services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
                .WithExposedHeaders("Location", "Upload-Offset", "Upload-Length", "Tus-Resumable", "Content-Disposition");
        }));
        return services;
    }

    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:MigrateOnStartup", true)) return;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception ex) when (attempt < 10 && ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException)
            {
                Log.Warning("Chưa kết nối được CSDL (lần {Attempt}), thử lại sau 3 giây...", attempt);
                await Task.Delay(3000);
            }
        }
        foreach (var seeder in scope.ServiceProvider.GetServices<IDataSeeder>().OrderBy(s => s.Order))
            await seeder.SeedAsync();
    }

    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
            ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
            : ctx.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

        app.UseSwagger(o => o.RouteTemplate = "api/swagger/{documentName}/swagger.json");
        app.UseSwaggerUI(o =>
        {
            o.RoutePrefix = "api/swagger";
            o.SwaggerEndpoint("/api/swagger/v1/swagger.json", "BlueDrive API v1");
            o.DocumentTitle = "BlueDrive API - Tài liệu";
        });

        // tus PATCH chunks may exceed Kestrel's default 30 MB body limit.
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/tus") && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f)
                f.MaxRequestBodySize = null;
            return next(ctx);
        });
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapControllers();
        app.MapHub<BlueDrive.Api.Realtime.NotificationHub>("/hubs/notifications");
        app.MapBlueDriveTus();
        app.MapBlueDriveHealthChecks();
        return app;
    }
}
