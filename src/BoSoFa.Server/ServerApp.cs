using BoSoFa.Protocol;
using BoSoFa.Server.Accounts;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Live;
using BoSoFa.Server.Machines;
using BoSoFa.Server.Tokens;
using BoSoFa.Server.Sessions;
using BTDB.Replication;
using BTDB.Replication.EventLog;
using BTDB.Replication.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using BoSoFa.Server.Telemetry;
using Microsoft.OpenApi;

namespace BoSoFa.Server;

public static class ServerApp
{
    /// <summary>Single-node mode on local file-system storage, or a cluster node (<see cref="ServerOptions.Cluster"/>)
    /// with peer endpoints on the same port as everything else. Storage is chosen by <see cref="NodeStorage"/>.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        configure?.Invoke(builder);
        var options = builder.Configuration.GetSection("BoSoFa").Get<ServerOptions>() ?? new ServerOptions();
        var data = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
        var cluster = options.Cluster;
        var storage = NodeStorage.Create(options, data, builder.Environment.ContentRootPath);
        builder.Services.AddSingleton(options);

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(1, ServerJsonContext.Default);
        });
        builder.Services.AddOpenApi(openApi => openApi.AddDocumentTransformer(async (document, context, cancellation) =>
        {
            // WebSocket messages are not in any endpoint signature; list them so the TypeScript client gets them too.
            foreach (var type in new[] { typeof(LiveClientMessage), typeof(LiveServerMessage) })
                document.AddComponent(type.Name, PolymorphicComponents(document,
                    await context.GetOrCreateSchemaAsync(type, null, cancellation)));
        }));
        builder.Services.AddHealthChecks().AddCheck<ApplicationHealthCheck>("bosofa-application", tags: ["ready"]);
        if (options.TrustForwardedHeaders)
            builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                // Proxy addresses change with its pods; the deployment keeps everything else off this port.
                forwarded.KnownIPNetworks.Clear();
                forwarded.KnownProxies.Clear();
            });
        // Cookies and daemon tokens must be readable on every node, so a cluster shares the key ring.
        storage.PersistKeys(builder.Services.AddDataProtection().SetApplicationName("bosofa"));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddBoSoFaAuthentication(options);
        builder.Services.AddSingleton<DaemonTokens>();
        builder.Services.AddSingleton<DaemonConnections>();
        builder.Services.AddSingleton<LiveHub>();
        builder.Services.AddSingleton<LiveRelay>();
        builder.Services.AddSingleton<MachineRpcClient>();
        builder.Services.AddSingleton<BoSoFa.Server.Desktop.DesktopTokenService>();
        builder.Services.AddSingleton<BoSoFa.Server.Desktop.LiveTicketsUsed>();
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy.WithOrigins(options.DesktopOrigins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE").WithHeaders("authorization", "content-type")
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));
        builder.Services.AddSingleton<DaemonPump>();
        builder.Services.AddSingleton<LongPollChannels>();
        builder.Services.AddSingleton<PollEndpoints>();
        builder.Services.AddHostedService(services => services.GetRequiredService<PollEndpoints>());
        builder.Services.AddHostedService<DaemonRolloutTicker>();
        builder.Services.AddSingleton<IDomainSignals, DomainSignals>();

        // BTDB: one replicated database plus the EventLog (see NodeStorage).
        builder.Services.AddSingleton<IReplicationScheduler>(new SystemReplicationScheduler());
        builder.Services.AddSingleton(storage.Leader);
        builder.Services.AddSingleton(storage.EventLog);
        builder.Services.AddSingleton(storage.Blobs);
        // The node owns (and disposes) its canonical storage.
        var nodeId = cluster?.NodeId ?? "single-node";
        var endpoint = cluster?.PeerUrl ?? "single-node";
        var nodeOptions = new BtdbNodeOptions("bosofa", nodeId, endpoint, Domain.Domain.ApplicationGeneration, Path.Combine(data, "cache"), storage.Canonical)
        {
            FlushToDisk = options.FlushToDisk
        };
        builder.Services.AddSingleton(nodeOptions);
        builder.Services.AddSingleton<BtdbNode>();
        builder.Services.AddSingleton<IReplicationNodeHost>(sp => sp.GetRequiredService<BtdbNode>());
        var replication = new ReplicationNodeOptions("bosofa", endpoint, TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), Domain.Domain.ApplicationGeneration);
        if (cluster == null)
        {
            builder.Services.AddBTDBReplicationSingleNode(replication, 1000, TimeSpan.FromMilliseconds(250));
            builder.Services.AddBTDBEventLogSingleNode();
        }
        else
        {
            builder.Services.AddBTDBReplication(replication, 1000, TimeSpan.FromMilliseconds(250));
            builder.Services.AddBTDBEventLog(cluster.PeerUrl, cluster.PeerKey);
        }
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BtdbNode>()); // after BTDB's: stops first
        builder.AddBoSoFaTelemetry(nodeId);
        builder.Services.AddSingleton<MachinePresenceService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MachinePresenceService>());

        var app = builder.Build();
        app.Services.GetRequiredService<BtdbNode>().Attach(app.Services);

        if (cluster != null) app.UsePeerRoutesOnlyFromPeers(); // before the forwarded headers are consumed
        if (options.TrustForwardedHeaders) app.UseForwardedHeaders();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseCors(); // before authentication: a preflight carries no credentials
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpenApi();
        // Liveness runs no checks: a node that is restoring or catching up must not be killed for it.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
        app.MapAuthEndpoints(options);
        app.MapApiEndpoints();
        app.MapMachineEndpoints();
        app.MapFileEndpoints();
        app.MapReleaseEndpoints();
        app.MapSessionEndpoints();
        app.MapAccountEndpoints();
        app.MapApiTokenEndpoints();
        BoSoFa.Server.Desktop.DesktopEndpoints.MapDesktopEndpoints(app);
        app.MapAuditEndpoints();
        app.MapCatalogEndpoints();
        app.MapBlobEndpoints();
        if (cluster != null)
        {
            // Peers use the same port as everyone else; UsePeerRoutesOnlyFromPeers keeps proxied clients off these routes.
            app.MapBTDBReplication();
            app.MapBTDBEventLog();
            app.MapGet(LiveRelay.PathPrefix + "{machineId}",
                (HttpContext http, ulong machineId, LiveRelay relay) => relay.ServeAsync(http, machineId));
            app.MapPost(MachineRpcClient.PathPrefix + "{machineId}/rpc",
                (HttpContext http, ulong machineId, MachineRpcClient rpc) => rpc.ServeAsync(http, machineId));
            app.MapMethods(PollEndpoints.PeerPrefix + "{machineId}/{connection}/{action}", ["GET", "POST"],
                (HttpContext http, ulong machineId, string connection, string action, PollEndpoints endpoints) =>
                    endpoints.ServePeerAsync(http, machineId, connection, action));
        }
        app.MapSpa(Path.GetFullPath(options.WebRoot, builder.Environment.ContentRootPath));
        return app;
    }

    /// <summary>Nodes call each other directly, so a request that went through the ingress is never a peer's: the proxy
    /// always adds X-Forwarded-For and a client cannot remove it. The peer key stays the actual authentication.</summary>
    static void UsePeerRoutesOnlyFromPeers(this WebApplication app) => app.Use((http, next) =>
    {
        if (!http.Request.Headers.ContainsKey("X-Forwarded-For") ||
            !(http.Request.Path.StartsWithSegments("/internal") || http.Request.Path.StartsWithSegments("/_btdb")))
            return next(http);
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    });

    /// <summary>The generator inlines the derived types but points the discriminator mapping at components it never
    /// adds. Moves each branch to its mapped component and refers to it, so the document resolves.</summary>
    static OpenApiSchema PolymorphicComponents(OpenApiDocument document, OpenApiSchema schema)
    {
        if (schema.Discriminator is not { PropertyName: { } property, Mapping: { } mapping } || schema.AnyOf == null) return schema;
        for (var i = 0; i < schema.AnyOf.Count; i++)
        {
            if (schema.AnyOf[i] is not OpenApiSchema branch || branch.Properties == null ||
                !branch.Properties.TryGetValue(property, out var tagSchema) || tagSchema.Enum is not [{ } tag, ..]) continue;
            var name = tag.GetValue<string>();
            if (!mapping.TryGetValue(name, out var reference) || reference.Reference.Id is not { } id) continue;
            document.AddComponent(id, branch);
            schema.AnyOf[i] = new OpenApiSchemaReference(id, document);
        }
        return schema;
    }

    static void MapSpa(this WebApplication app, string webRoot)
    {
        // Unknown API paths answer 404 instead of the SPA. A plain catch-all route ranks below every specific route.
        // (MapFallback with a pattern lost its pattern under the request delegate generator and caught everything.)
        app.Map("/api/{**path}", () => Results.NotFound());
        if (!Directory.Exists(webRoot)) return; // development: Vite serves the SPA and proxies to this server
        var files = new PhysicalFileProvider(webRoot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                context.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache"
        });
        app.MapFallbackToFile("{**path:nonfile}", "index.html", new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
        });
    }
}
