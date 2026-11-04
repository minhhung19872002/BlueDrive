using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BlueDrive.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var asm = typeof(DependencyInjection).Assembly;
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(asm);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(AuditBehavior<,>));
        });
        services.AddValidatorsFromAssembly(asm, includeInternalTypes: true);
        services.AddScoped<Features.Auth.SessionRevoker>();
        services.AddScoped<Features.Monitoring.IAlertService, Features.Monitoring.AlertService>();
        services.AddScoped<Security.IResourceAuthorizationService, Security.ResourceAuthorizationService>();
        services.AddScoped<Features.Access.PermissionProjection>();
        services.AddScoped<Features.Access.ReferencePermissionEvaluator>();
        services.AddScoped<Abstractions.IResourceAccessChecker, Security.ResourceAccessChecker>();
        services.AddScoped<Features.Storage.QuotaService>();
        services.AddSingleton<Features.Office.OfficeEditingTracker>();
        services.AddSingleton<Features.Auth.LoginIpGuard>();
        services.AddScoped<Features.Storage.TrashPurger>();
        services.AddScoped<Features.Sync.SyncPolicyService>();
        services.AddScoped<Features.Sync.SyncAccessContext>();
        services.AddSingleton<Features.Storage.IContentTicketService, Features.Storage.ContentTicketService>();
        services.AddSingleton<Features.Sharing.IPublicLinkTickets, Features.Sharing.PublicLinkTickets>();
        ValidatorOptions.Global.LanguageManager.Culture = new System.Globalization.CultureInfo("vi");
        return services;
    }
}

/// <summary>Runs FluentValidation validators and turns failures into a 400 ProblemDetails.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (!validators.Any()) return await next(ct);
        var ctx = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(ctx, ct)));
        var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToList();
        if (failures.Count > 0)
            throw new RequestValidationException(failures
                .GroupBy(f => string.IsNullOrEmpty(f.PropertyName) ? "" : char.ToLowerInvariant(f.PropertyName[0]) + f.PropertyName[1..])
                .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray()));
        return await next(ct);
    }
}

/// <summary>Writes an audit entry after a successful IAuditableRequest so no mutation is missed.</summary>
public sealed class AuditBehavior<TRequest, TResponse>(IAuditLogger audit)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var response = await next(ct);
        if (request is IAuditableRequest auditable && auditable.ToAuditEntry(response) is { } entry)
            await audit.LogAsync(entry, ct);
        return response;
    }
}
