using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Live;
using BoSoFa.Server.Machines;
using BoSoFa.Server.Sessions;
using BTDB.Replication;
using BTDB.Replication.EventLog;
using BTDB.Replication.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;

namespace BoSoFa.Server;

public static class ServerApp
{
    /// <summary>Single-node mode on local file-system storage, or a cluster node (<see cref="ServerOptions.Cluster"/>)
    /// with peer endpoints on their own port. Storage is chosen by <see cref="NodeStorage"/>.</summary>
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
        builder.Services.AddHealthChecks();
        // Cookies and daemon tokens must be readable on every node, so a cluster shares the key ring.
        storage.PersistKeys(builder.Services.AddDataProtection().SetApplicationName("bosofa"));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddBoSoFaAuthentication(options);
        builder.Services.AddSingleton<DaemonTokens>();
        builder.Services.AddSingleton<DaemonConnections>();
        builder.Services.AddSingleton<LiveHub>();
        builder.Services.AddSingleton<IDomainSignals, DomainSignals>();

        // BTDB: one replicated database plus the EventLog (see NodeStorage).
        builder.Services.AddSingleton<IReplicationScheduler>(new SystemReplicationScheduler());
        builder.Services.AddSingleton(storage.Leader);
        builder.Services.AddSingleton(storage.EventLog);
        // The node owns (and disposes) its canonical storage.
        var nodeId = cluster?.NodeId ?? "single-node";
        var endpoint = cluster?.PeerUrl ?? "single-node";
        var nodeOptions = new BtdbNodeOptions("bosofa", nodeId, endpoint, 1, Path.Combine(data, "cache"), storage.Canonical)
        {
            FlushToDisk = options.FlushToDisk
        };
        builder.Services.AddSingleton(nodeOptions);
        builder.Services.AddSingleton<BtdbNode>();
        builder.Services.AddSingleton<IReplicationNodeHost>(sp => sp.GetRequiredService<BtdbNode>());
        var replication = new ReplicationNodeOptions("bosofa", endpoint, TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 1);
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
        builder.Services.AddSingleton<MachinePresenceService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MachinePresenceService>());

        var app = builder.Build();
        app.Services.GetRequiredService<BtdbNode>().Attach(app.Services);

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpenApi();
        app.MapHealthChecks("/health/ready");
        app.MapAuthEndpoints(options);
        app.MapApiEndpoints();
        app.MapMachineEndpoints();
        app.MapSessionEndpoints();
        if (cluster != null)
        {
            // Peers reach each other on a separate port that the ingress never exposes.
            var peerHost = $"*:{new Uri(cluster.PeerUrl).Port}";
            app.MapBTDBReplication().RequireHost(peerHost);
            app.MapBTDBEventLog().RequireHost(peerHost);
        }
        app.MapSpa(Path.GetFullPath(options.WebRoot, builder.Environment.ContentRootPath));
        return app;
    }

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
