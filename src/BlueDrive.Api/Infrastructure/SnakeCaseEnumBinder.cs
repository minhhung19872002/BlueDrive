using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BlueDrive.Api.Infrastructure;

/// <summary>Binds enums from route/query values in the same snake_case form used by the JSON API ("don_vi" → DonVi).</summary>
public sealed class SnakeCaseEnumBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        var type = Nullable.GetUnderlyingType(context.Metadata.ModelType) ?? context.Metadata.ModelType;
        return type.IsEnum && type.Namespace == "BlueDrive.Domain.Enums" ? new SnakeCaseEnumBinder(type) : null;
    }
}

public sealed class SnakeCaseEnumBinder(Type enumType) : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext ctx)
    {
        var value = ctx.ValueProvider.GetValue(ctx.ModelName).FirstValue;
        if (string.IsNullOrWhiteSpace(value)) return Task.CompletedTask;
        ctx.ModelState.SetModelValue(ctx.ModelName, value, value);
        if (Enum.TryParse(enumType, value.Replace("_", ""), ignoreCase: true, out var parsed) && Enum.IsDefined(enumType, parsed!))
            ctx.Result = ModelBindingResult.Success(parsed);
        else
            ctx.ModelState.TryAddModelError(ctx.ModelName, $"Giá trị '{value}' không hợp lệ.");
        return Task.CompletedTask;
    }
}
