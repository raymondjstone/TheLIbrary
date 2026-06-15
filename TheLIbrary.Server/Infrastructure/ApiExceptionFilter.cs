using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TheLibrary.Server.Infrastructure;

// Converts any unhandled exception thrown by a controller action into a JSON
// 500 response. Without this, an unhandled exception bubbles to the framework's
// developer-exception-page middleware (in Development) and is rendered as an
// HTML page — which the SPA's fetch() then tries to JSON.parse, producing the
// cryptic "Unexpected token '<', \"<!DOCTYPE \"..." error. The filter runs
// inside MVC, so it pre-empts that and every API route always returns JSON.
public sealed class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _log;
    public ApiExceptionFilter(ILogger<ApiExceptionFilter> log) { _log = log; }

    public void OnException(ExceptionContext context)
    {
        if (context.ExceptionHandled) return;

        // Client disconnects / request aborts aren't server errors — don't log
        // them as such or dress them up as a 500.
        if (context.Exception is OperationCanceledException
            && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.ExceptionHandled = true;
            context.Result = new StatusCodeResult(499); // client closed request
            return;
        }

        _log.LogError(context.Exception, "Unhandled exception handling {Method} {Path}",
            context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        context.Result = new ObjectResult(new { error = context.Exception.Message })
        {
            StatusCode = StatusCodes.Status500InternalServerError,
        };
        context.ExceptionHandled = true;
    }
}
