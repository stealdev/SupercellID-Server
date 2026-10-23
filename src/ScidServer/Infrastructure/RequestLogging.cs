using ScidServer.Configuration;

namespace ScidServer.Infrastructure;

public sealed class RequestLogging(RequestDelegate next, ServerOptions options, ILogger<RequestLogging> log)
{
    private const int MaxLoggedBody = 2000;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.Contains('\0'))
            ctx.Request.Path = new PathString(path.Replace("\0", ""));
        path = ctx.Request.Path.Value ?? "";

        var verbose = options.LogBodies
                      && !path.StartsWith("/cdn", StringComparison.OrdinalIgnoreCase)
                      && !path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase)
                      && !path.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase);
        if (!verbose)
        {
            await next(ctx);
            log.LogInformation("{Method} {Path} -> {Status}", ctx.Request.Method, path, ctx.Response.StatusCode);
            return;
        }

        ctx.Request.EnableBuffering();
        string requestBody;
        using (var reader = new StreamReader(ctx.Request.Body, leaveOpen: true))
            requestBody = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;

        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next(ctx);

            buffer.Position = 0;
            var responseBody = (ctx.Response.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase)
                ? await new StreamReader(buffer, leaveOpen: true).ReadToEndAsync()
                : $"({buffer.Length} bytes)";
            log.LogInformation("{Method} {Path} {RequestBody} -> {Status} {ResponseBody}",
                ctx.Request.Method, path, Truncate(requestBody), ctx.Response.StatusCode, Truncate(responseBody));

            buffer.Position = 0;
            await buffer.CopyToAsync(original);
        }
        finally
        {
            ctx.Response.Body = original;
        }
    }

    private static string Truncate(string text) =>
        text.Length > MaxLoggedBody ? text[..MaxLoggedBody] + "...(truncated)" : text;
}
