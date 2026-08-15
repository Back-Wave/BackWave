using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackWave.Dashboard;

/// <summary>
/// Renders a Razor component to an HTML string via <see cref="HtmlRenderer"/> and writes it
/// through the existing middleware. Server-rendered only: no Blazor router, no SignalR
/// circuit, no WASM. The one client-side script the dashboard ships is the SSE
/// <c>EventSource</c> client that live views inline (see <c>DashboardLayout</c>); it swaps the
/// <c>#bw-live</c> region with fragments this same renderer produces (<see cref="RenderAsync"/>
/// with <c>document: false</c>). The component tree is still rendered top to bottom into a
/// string; the host integration contract is unchanged.
/// </summary>
internal static class DashboardRenderer
{
    /// <summary>Renders <typeparamref name="TComponent"/> (a full document root) to a complete HTML response string.</summary>
    public static Task<string> RenderDocumentAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        HttpContext context, IReadOnlyDictionary<string, object?> parameters)
        where TComponent : IComponent
        => RenderAsync(context, typeof(TComponent), parameters, document: true);

    /// <summary>
    /// Renders <paramref name="componentType"/> to an HTML string. When <paramref name="document"/>
    /// is true the doctype is prepended (a full page); when false the bare component markup is
    /// returned — the live-region fragment streamed over SSE.
    /// </summary>
    public static async Task<string> RenderAsync(
        HttpContext context,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        IReadOnlyDictionary<string, object?> parameters,
        bool document)
    {
        var services = context.RequestServices;
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        await using var renderer = new HtmlRenderer(services, loggerFactory);
        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer
                .RenderComponentAsync(componentType, ParameterView.FromDictionary(new Dictionary<string, object?>(parameters)))
                .ConfigureAwait(false);
            return output.ToHtmlString();
        }).ConfigureAwait(false);

        // HtmlRenderer emits the component markup (starting at <html>); the doctype is not a
        // component, so it is prepended here for full-document renders.
        return document ? "<!DOCTYPE html>\n" + markup : markup;
    }
}
