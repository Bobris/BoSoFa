using System.Diagnostics.Metrics;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Machines;
using BTDB.Replication;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BoSoFa.Server.Telemetry;

public static class ServerTelemetry
{
    public const string MeterName = "BoSoFa.Server";

    /// <summary>Exports traces, metrics and logs over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; the standard
    /// <c>OTEL_*</c> variables tune the rest. Without it nothing is collected.</summary>
    public static void AddBoSoFaTelemetry(this WebApplicationBuilder builder, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) return;
        builder.Services.AddHostedService<ServerMetrics>();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("bosofa-server", serviceInstanceId: nodeId))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(aspNet => aspNet.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(MeterName))
            .WithLogging()
            .UseOtlpExporter();
    }
}

/// <summary>What the node knows without asking anyone: read at each export.</summary>
sealed class ServerMetrics(BtdbNode node, DaemonConnections daemons, IMeterFactory meters) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var meter = meters.Create(ServerTelemetry.MeterName);
        meter.CreateObservableGauge("bosofa.daemons.connected", () => daemons.Count, description: "Daemons connected to this node.");
        meter.CreateObservableGauge("bosofa.node.leader", () => node.Role == ReplicationNodeRole.Leader ? 1 : 0,
            description: "1 while this node leads the cluster.");
        meter.CreateObservableCounter("bosofa.events.applied", () => (long)(node.Publisher?.LastAppliedEventId ?? 0),
            description: "Domain events this node has applied.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
