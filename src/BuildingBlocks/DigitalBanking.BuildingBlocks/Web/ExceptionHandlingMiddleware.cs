using System.Net;
using System.Text.Json;
using DigitalBanking.BuildingBlocks.Exceptions;
using Microsoft.AspNetCore.Http;
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
        var (statusCode, code) = exception switch
        {
            ValidationException => (HttpStatusCode.BadRequest, "validation_error"),
            UnauthorizedException => (HttpStatusCode.Unauthorized, "unauthorized"),
            ForbiddenException => (HttpStatusCode.Forbidden, "forbidden"),
            NotFoundException => (HttpStatusCode.NotFound, "not_found"),
            ConflictException => (HttpStatusCode.Conflict, "conflict"),
            BusinessRuleException => (HttpStatusCode.UnprocessableEntity, "business_rule"),
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
