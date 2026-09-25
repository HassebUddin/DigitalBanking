using DigitalBanking.BuildingBlocks.Exceptions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBanking.BuildingBlocks.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InternalApiKeyAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Internal-Api-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedKey = configuration["InternalApi:Key"];
        var providedKey = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(expectedKey) || providedKey != expectedKey)
        {
            throw new ForbiddenException("Internal service access was denied.");
        }

        await next();
    }
}
