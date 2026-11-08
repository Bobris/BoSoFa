using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BTDB.Replication;
using BTDB.Replication.EventLog;
using BTDB.Replication.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;

namespace BoSoFa.Server;

public static class ServerApp
{
    const string InitialLeaderJson =
        """{"format":1,"clusterId":"bosofa","term":0,"revision":0,"applicationGeneration":0,"databaseNames":[]} """;

    /// <summary>Single-node mode on local file-system storage; the cluster mode on Azure Blob comes in milestone M2.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        configure?.Invoke(builder);
        var options = builder.Configuration.GetSection("BoSoFa").Get<ServerOptions>() ?? new ServerOptions();
        var data = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
        var shared = Path.Combine(data, "shared");
        builder.Services.AddSingleton(options);

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, ProtocolJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(1, ServerJsonContext.Default);
        });
        builder.Services.AddOpenApi();
        builder.Services.AddHealthChecks();
        builder.Services.AddDataProtection().SetApplicationName("bosofa")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")));
        builder.Services.AddBoSoFaAuthentication(options);

        // BTDB: one replicated database plus the EventLog, both on local directories in single-node mode.
        builder.Services.AddSingleton<IReplicationScheduler>(new SystemReplicationScheduler());
        builder.Services.AddSingleton<IReplicationLeaderStorage>(
            new FileSystemLeaderStorage(Path.Combine(shared, "leader"), TimeSpan.FromSeconds(15), InitialLeaderJson,
                flushToDisk: options.FlushToDisk));
        builder.Services.AddSingleton<IEventLogStorage>(
            new FileSystemEventLogStorage(Path.Combine(shared, "eventlog"), flushToDisk: options.FlushToDisk));
        var nodeOptions = new BtdbNodeOptions("bosofa", "single-node", "single-node", 1, Path.Combine(data, "cache"),
            new FileSystemReplicationStorage(Path.Combine(shared, "db", BtdbNode.Database), flushToDisk: options.FlushToDisk))
        {
            FlushToDisk = options.FlushToDisk
        };
        builder.Services.AddSingleton(nodeOptions);
        builder.Services.AddSingleton<BtdbNode>();
        builder.Services.AddSingleton<IReplicationNodeHost>(sp => sp.GetRequiredService<BtdbNode>());
        builder.Services.AddBTDBReplicationSingleNode(new ReplicationNodeOptions("bosofa", "single-node",
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1), 1), 1000, TimeSpan.FromMilliseconds(250));
        builder.Services.AddBTDBEventLogSingleNode();

        var app = builder.Build();
        app.Services.GetRequiredService<BtdbNode>().Attach(app.Services);

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOpenApi();
        app.MapHealthChecks("/health/ready");
        app.MapAuthEndpoints(options);
        app.MapApiEndpoints();
        app.MapSpa(Path.GetFullPath(options.WebRoot, builder.Environment.ContentRootPath));
        return app;
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
