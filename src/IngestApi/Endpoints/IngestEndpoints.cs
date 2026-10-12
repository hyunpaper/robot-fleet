using RobotFleet.Ingest;

namespace RobotFleet.IngestApi.Endpoints;

public static class IngestEndpoints
{
    public static async Task<IResult> HandleAsync(HttpContext ctx, IngestService svc)
    {
        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
            body = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or BadHttpRequestException)
        {
            return Results.Json(new IngestResponseBody(null, 0, 0, false, "request body read failed"), statusCode: 400);
        }

        var resp = await svc.HandleAsync(body, ctx.RequestAborted);
        if (resp.RetryAfterSeconds is { } retry)
            ctx.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(resp.Body, statusCode: resp.StatusCode);
    }
}
