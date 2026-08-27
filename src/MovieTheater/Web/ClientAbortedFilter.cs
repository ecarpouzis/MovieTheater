using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MovieTheater.Web
{
    /// <summary>
    /// The endpoint boundary for a request the CLIENT walked away from (R9 closing pass).
    ///
    /// <para>Threading <c>HttpContext.RequestAborted</c> into the browse endpoints' EF calls is what
    /// actually stops the pod executing a query nobody will read — the catalog engine aborts a swept-past
    /// band fetch, and until that token reached <c>ToListAsync</c>/<c>CountAsync</c> the query ran to
    /// completion anyway (<c>docs/catalog.md</c> → "The instruments": the desktop Wall's landing left ~41
    /// swept-past queries still executing). But an honoured token means the action now THROWS
    /// <see cref="OperationCanceledException"/>, and an unhandled one is logged as a server fault —
    /// trading a wasted query for an exception storm would be no improvement.</para>
    ///
    /// <para>So: when the exception is a cancellation AND this request's own
    /// <see cref="Microsoft.AspNetCore.Http.HttpContext.RequestAborted"/> is what signalled it, the
    /// request is closed QUIETLY with 499 (nginx's "client closed request" — chosen over letting the
    /// exception escape so the log stays clean, and over a 200 empty envelope because an empty page is a
    /// lie about the catalog; nothing reads either, the socket is already gone). A cancellation from any
    /// OTHER token — a server-side timeout, a bug — is a real failure and is left to propagate.</para>
    ///
    /// <para>It is a global filter rather than a try/catch per action so no future browse endpoint can
    /// forget it, and it is pure enough to test: <see cref="ShouldSwallow"/> is the whole decision.</para>
    /// </summary>
    public sealed class ClientAbortedFilter : IExceptionFilter
    {
        /// <summary>nginx's non-standard "client closed request"; the de-facto code for this case.</summary>
        public const int ClientClosedRequest = 499;

        /// <summary>
        /// True when <paramref name="ex"/> is a cancellation raised by the caller going away
        /// (<paramref name="requestAborted"/> signalled), rather than any other cancellation.
        /// </summary>
        public static bool ShouldSwallow(Exception? ex, CancellationToken requestAborted) =>
            ex is OperationCanceledException && requestAborted.IsCancellationRequested;

        public void OnException(ExceptionContext context)
        {
            if (!ShouldSwallow(context.Exception, context.HttpContext.RequestAborted)) return;
            context.ExceptionHandled = true;
            context.Result = new StatusCodeResult(ClientClosedRequest);
        }
    }
}
