using Microsoft.AspNetCore.Http;

namespace DigitalBanking.BuildingBlocks.Distributed;

// Carries the hybrid logical clock across HTTP calls between services, in both directions: the caller stamps
// the request, the callee merges it and stamps its response, and the caller merges that. So a debit applied by
// the account service is always "after" the transfer request that caused it, and the transfer's completion after the debit.
public sealed class HlcPropagationHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(HybridLogicalClock.HeaderName);
        request.Headers.Add(HybridLogicalClock.HeaderName, HybridLogicalClock.Instance.Now().ToString());
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues(HybridLogicalClock.HeaderName, out var values) && long.TryParse(values.FirstOrDefault(), out var remote))
        {
            HybridLogicalClock.Instance.Receive(remote);
        }

        return response;
    }
}

public sealed class HlcMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (long.TryParse(context.Request.Headers[HybridLogicalClock.HeaderName], out var remote))
        {
            HybridLogicalClock.Instance.Receive(remote);
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HybridLogicalClock.HeaderName] = HybridLogicalClock.Instance.Now().ToString();
            return Task.CompletedTask;
        });
        return next(context);
    }
}
