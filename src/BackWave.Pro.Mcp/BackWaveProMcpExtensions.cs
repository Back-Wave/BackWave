using BackWave.Hosting;
using BackWave.Pro.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace BackWave.Pro.Mcp;

/// <summary>
/// Extension methods that register the BackWave MCP server inside an <c>AddBackWave</c> block and
/// mount it on an application's request pipeline. AI agents connect to the mounted endpoint over
/// MCP (Model Context Protocol, streamable HTTP) and use tools that read — and, where you grant
/// it, act on — your BackWave jobs.
/// </summary>
public static class BackWaveProMcpExtensions
{
    /// <summary>
    /// Registers the BackWave MCP server from inside the <c>AddBackWave</c> block, next to
    /// <c>AddWorkerGroup</c> and <c>AddObservers</c> — so the whole BackWave setup lives in one
    /// place. The server is stateless: any node of a multi-node deployment serves any request, so
    /// no sticky sessions are needed behind a load balancer. Registration adds the tool surface
    /// and the permission pipeline; nothing is reachable until you also mount an endpoint with
    /// <see cref="UseBackWaveProMcp"/> (or <see cref="MapBackWaveProMcp"/>).
    /// </summary>
    /// <param name="builder">The BackWave builder to register the MCP server through.</param>
    /// <param name="configure">
    /// Optional configuration — chiefly the authorization callbacks. When omitted, defaults are
    /// used: viewing is allowed, and every write action and sensitive-data read is denied, so the
    /// tool surface is safe and read-only until you opt in.
    /// </param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddBackWave(bw =&gt;
    /// {
    ///     bw.UseStore(/* ... */).UseJobs(BackWaveJobs.Module);
    ///     bw.AddMcp(mcp =&gt;
    ///     {
    ///         mcp.AuthorizeView = ctx =&gt; ValueTask.FromResult(ctx.User.IsInRole("ops"));
    ///     });
    /// });
    ///
    /// var app = builder.Build();
    /// app.UseBackWaveProMcp(); // serves MCP at /backwave-mcp
    /// </code>
    /// </example>
    public static BackWaveBuilder AddMcp(
        this BackWaveBuilder builder, Action<BackWaveProMcpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigureServices(services =>
        {
            var options = new BackWaveProMcpOptions();
            configure?.Invoke(options);
            services.AddSingleton(options);

            // The permission callbacks receive the live HttpContext; under streamable HTTP every
            // tool call is its own POST, so the accessor is correct per call.
            services.AddHttpContextAccessor();
            // The mount does its own UseRouting/UseEndpoints (see UseBackWaveProMcp), which needs
            // routing services even in hosts that never call AddRouting themselves.
            services.AddRouting();

            services.AddMcpServer(server => server.ServerInfo = new Implementation
                {
                    Name = "BackWave",
                    Title = "BackWave job processing",
                    Version = typeof(BackWaveProMcpExtensions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                })
                // Stateless: no session handshake required, no per-session server state — any node
                // serves any request. The tool list is therefore fixed per node at startup.
                .WithHttpTransport(transport => transport.Stateless = true)
                // Explicit tool registration only — never assembly scanning. Each tool is registered
                // with source-generated JSON options (BackWaveProMcpJson) so schema generation and
                // result (de)serialization resolve without reflection, keeping the surface Native-AOT
                // safe. The generic WithTools<T> closes the type at compile time, so the invocation
                // path is a statically-known MethodInfo (no dynamic code).
                .WithTools<QueueTools>(BackWaveProMcpJson.ToolOptions)
                .WithTools<JobTools>(BackWaveProMcpJson.ToolOptions)
                .WithTools<ReadTools>(BackWaveProMcpJson.ToolOptions)
                .WithTools<ObserverTools>(BackWaveProMcpJson.ToolOptions)
                .WithTools<SensitiveDataTools>(BackWaveProMcpJson.ToolOptions)
                .WithTools<WriteTools>(BackWaveProMcpJson.ToolOptions)
                .WithTools<WorkflowTools>(BackWaveProMcpJson.ToolOptions)
                .WithRequestFilters(filters =>
                {
                    // The view gate fronts the whole surface: a denied request sees an empty tool
                    // list, and a direct call (the backstop) gets a tool-execution error. Per-tool
                    // gates layer onto this pipeline below via McpToolGates (0227); sensitive-data
                    // locks (0226) slot into the same map.
                    filters.AddListToolsFilter(next => async (context, cancellationToken) =>
                        await ViewAllowedAsync(context.Services, options).ConfigureAwait(false)
                            ? await next(context, cancellationToken).ConfigureAwait(false)
                            : new ListToolsResult { Tools = [] });

                    filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                        await ViewAllowedAsync(context.Services, options).ConfigureAwait(false)
                            ? await next(context, cancellationToken).ConfigureAwait(false)
                            : new CallToolResult
                            {
                                IsError = true,
                                Content =
                                [
                                    new TextContentBlock
                                    {
                                        Text = "Permission denied: this request may not view the BackWave "
                                            + "tool surface. The host's AuthorizeView callback denied it; "
                                            + "authenticate the request to satisfy the host's policy, or have "
                                            + "the host grant it in BackWaveProMcpOptions.AuthorizeView.",
                                    },
                                ],
                            });

                    // The generalized per-tool gating (0227): the list filter removes every tool
                    // whose own gate denies this request — an unconfigured host presents the clean
                    // read-only list, and granting one options callback surfaces exactly its
                    // tool(s) — and the call filter is the backstop for a client that ignores (or
                    // cached) the list. The name→gate mapping lives in McpToolGates; tools with no
                    // entry there (the plain reads) pass through on the view gate alone.
                    filters.AddListToolsFilter(next => async (context, cancellationToken) =>
                    {
                        var result = await next(context, cancellationToken).ConfigureAwait(false);
                        return await McpToolGates
                            .FilterListAsync(result, HttpContextOf(context.Services), options)
                            .ConfigureAwait(false);
                    });

                    filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                        await McpToolGates
                            .AllowsAsync(context.Params?.Name, HttpContextOf(context.Services), options)
                            .ConfigureAwait(false)
                            ? await next(context, cancellationToken).ConfigureAwait(false)
                            : McpToolGates.DeniedResult(context.Params?.Name));
                });
        });
    }

    /// <summary>
    /// Mounts the BackWave MCP server as a self-contained branch on the host's own request
    /// pipeline — agent access to your jobs with no separate service to run. Requires
    /// <see cref="AddMcp"/> inside the <c>AddBackWave</c> block. Mount it after your
    /// authentication and authorization middleware so the permission callbacks configured there
    /// see an authenticated request. The endpoint speaks MCP streamable HTTP: clients POST to the
    /// prefix and receive Server-Sent-Events-framed responses.
    /// </summary>
    /// <param name="app">The application's request pipeline builder to mount the MCP server on.</param>
    /// <param name="pathPrefix">
    /// The URL path the MCP endpoint is served under. Must start with <c>'/'</c> and name at
    /// least one path segment (the server cannot mount at the site root). Defaults to
    /// <c>"/backwave-mcp"</c>.
    /// </param>
    /// <returns>The same <paramref name="app"/>, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pathPrefix"/> is empty, does not start with <c>'/'</c>, or is the bare
    /// root <c>"/"</c>.
    /// </exception>
    /// <example>
    /// <code>
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// app.UseBackWaveProMcp("/backwave-mcp");
    /// </code>
    /// </example>
    public static IApplicationBuilder UseBackWaveProMcp(
        this IApplicationBuilder app, string pathPrefix = "/backwave-mcp")
    {
        ArgumentNullException.ThrowIfNull(app);
        if (string.IsNullOrEmpty(pathPrefix) || !pathPrefix.StartsWith('/') || pathPrefix == "/")
        {
            throw new ArgumentException(
                $"The MCP path prefix must start with '/' and name a path segment (got '{pathPrefix}').",
                nameof(pathPrefix));
        }

        // Branch mounting verified by the mcp-0007 spike: MapMcp needs endpoint routing, so the
        // branch runs its own UseRouting/UseEndpoints. The prefix shifts into PathBase inside the
        // branch, exactly like the dashboard's mount.
        return app.Map(pathPrefix, branch =>
        {
            branch.UseRouting();
            branch.UseEndpoints(endpoints => endpoints.MapMcp());
        });
    }

    /// <summary>
    /// Maps the BackWave MCP server as endpoints on the host's own endpoint route builder — the
    /// composable alternative to <see cref="UseBackWaveProMcp"/> for hosts that attach
    /// endpoint-level policies. Requires <see cref="AddMcp"/> inside the <c>AddBackWave</c>
    /// block. The returned convention builder chains the standard endpoint conventions, for
    /// example <c>RequireAuthorization()</c> or a rate-limiting policy.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map the MCP endpoints on.</param>
    /// <param name="pathPrefix">
    /// The URL path the MCP endpoint is served under. Must start with <c>'/'</c>. Defaults to
    /// <c>"/backwave-mcp"</c>.
    /// </param>
    /// <returns>A convention builder for the mapped MCP endpoints, so policies can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pathPrefix"/> is empty or does not start with <c>'/'</c>.
    /// </exception>
    /// <example>
    /// <code>
    /// app.MapBackWaveProMcp("/backwave-mcp")
    ///    .RequireAuthorization("ops-policy");
    /// </code>
    /// </example>
    public static IEndpointConventionBuilder MapBackWaveProMcp(
        this IEndpointRouteBuilder endpoints, string pathPrefix = "/backwave-mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrEmpty(pathPrefix) || !pathPrefix.StartsWith('/'))
        {
            throw new ArgumentException(
                $"The MCP path prefix must start with '/' (got '{pathPrefix}').", nameof(pathPrefix));
        }

        return endpoints.MapMcp(pathPrefix);
    }

    // Fail closed when no HttpContext is visible: over the HTTP transport there always is one, so
    // a null here means the call arrived some other way and the host's callback cannot judge it.
    // Internal (not private) so the fail-closed branch is directly unit-testable: the mounted HTTP
    // transport always supplies a context, so no endpoint test can reach the null path.
    internal static ValueTask<bool> ViewAllowedAsync(IServiceProvider? services, BackWaveProMcpOptions options)
    {
        var httpContext = services?.GetService<IHttpContextAccessor>()?.HttpContext;
        return httpContext is null ? ValueTask.FromResult(false) : options.AuthorizeView(httpContext);
    }

    // The live HttpContext of the request a filter is judging; under streamable HTTP every tool
    // call is its own POST, so the accessor is correct per call. Null when the call arrived some
    // way other than the HTTP transport (the gates then fail closed).
    private static HttpContext? HttpContextOf(IServiceProvider? services)
        => services?.GetService<IHttpContextAccessor>()?.HttpContext;
}
