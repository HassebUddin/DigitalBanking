using System.Net;
using System.Text.Json;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalBanking.BuildingBlocks.Web;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Request failed.");
            await WriteErrorAsync(context, exception);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, Exception exception)
    {
        // The province's primary is gone or fenced: the coordinator is (or will be) promoting the standby,
        // so the client should retry rather than treat it as a bug.
        if (RegionErrors.IsUnavailable(exception))
        {
            context.RequestServices.GetService<RegionalDatabaseOptions>()?.Topology.RefreshNow();
            exception = new ServiceUnavailableException("This region is switching to its standby database. Please retry in a few seconds.");
        }
        else if (exception is DbUpdateConcurrencyException)
        {
            exception = new ConflictException("Someone else changed this record at the same moment. Please retry.");
        }

        var (statusCode, code) = exception switch
        {
            ValidationException => (HttpStatusCode.BadRequest, "validation_error"),
            UnauthorizedException => (HttpStatusCode.Unauthorized, "unauthorized"),
            ForbiddenException => (HttpStatusCode.Forbidden, "forbidden"),
            NotFoundException => (HttpStatusCode.NotFound, "not_found"),
            ConflictException => (HttpStatusCode.Conflict, "conflict"),
            BusinessRuleException => (HttpStatusCode.UnprocessableEntity, "business_rule"),
            ServiceUnavailableException => (HttpStatusCode.ServiceUnavailable, "service_unavailable"),
            _ => (HttpStatusCode.InternalServerError, "server_error")
        };

        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(new
        {
            code,
            message = exception is DomainException ? exception.Message : "An unexpected error occurred."
        });

        await context.Response.WriteAsync(body);
    }
}
