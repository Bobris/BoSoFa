using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Tokens;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Machines;

/// <summary>A machine's file browser (plan §5.6) and terminals. Both need the right to control that machine.</summary>
public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/machines/{id}/codex-usage", CodexUsageAsync).RequireAuthorization();
        var files = app.MapGroup("/api/v1/machines/{id}/files").RequireAuthorization();
        files.MapGet("/roots", RootsAsync);
        files.MapGet("", ListAsync);
        files.MapGet("/text", ReadAsync);
        files.MapPost("/fetch", FetchAsync);
        files.MapPost("/store", StoreAsync);
        var terminals = app.MapGroup("/api/v1/machines/{id}/terminals").RequireAuthorization();
        terminals.MapGet("", ListTerminalsAsync);
        terminals.MapPost("", OpenTerminalAsync);
        terminals.MapDelete("/{terminalId}", CloseTerminalAsync);
    }

    static Task<Results<Ok<TerminalListResult>, NotFound, ForbidHttpResult, BadRequest<string>>> ListTerminalsAsync(ulong id, BtdbNode node,
        MachineRpcClient rpc, HttpContext http) => CallAsync<TerminalListResult>(id, new TerminalListRequest(), node, rpc, http);

    static Task<Results<Ok<TerminalOpenResult>, NotFound, ForbidHttpResult, BadRequest<string>>> OpenTerminalAsync(ulong id,
        TerminalOpenRequest request, BtdbNode node, MachineRpcClient rpc, HttpContext http) =>
        CallAsync<TerminalOpenResult>(id, request, node, rpc, http);

    static Task<Results<Ok<RpcDone>, NotFound, ForbidHttpResult, BadRequest<string>>> CloseTerminalAsync(ulong id, string terminalId,
        BtdbNode node, MachineRpcClient rpc, HttpContext http) => CallAsync<RpcDone>(id, new TerminalCloseRequest(terminalId), node, rpc, http);

    static Task<Results<Ok<CodexUsageResult>, NotFound, ForbidHttpResult, BadRequest<string>>> CodexUsageAsync(ulong id, BtdbNode node,
        MachineRpcClient rpc, HttpContext http) => CallAsync<CodexUsageResult>(id, new CodexUsageRequest(), node, rpc, http);

    static Task<Results<Ok<FileRootsResult>, NotFound, ForbidHttpResult, BadRequest<string>>> RootsAsync(ulong id, BtdbNode node,
        MachineRpcClient rpc, HttpContext http) => CallAsync<FileRootsResult>(id, new FileRootsRequest(), node, rpc, http);

    static Task<Results<Ok<FileListResult>, NotFound, ForbidHttpResult, BadRequest<string>>> ListAsync(ulong id, string path, BtdbNode node,
        MachineRpcClient rpc, HttpContext http) => CallAsync<FileListResult>(id, new FileListRequest(path), node, rpc, http);

    static Task<Results<Ok<FileTextResult>, NotFound, ForbidHttpResult, BadRequest<string>>> ReadAsync(ulong id, string path, long? offset,
        BtdbNode node, MachineRpcClient rpc, HttpContext http) =>
        CallAsync<FileTextResult>(id, new FileReadRequest(path, offset ?? 0), node, rpc, http);

    static Task<Results<Ok<FileFetchResult>, NotFound, ForbidHttpResult, BadRequest<string>>> FetchAsync(ulong id, FileFetchRequest request,
        BtdbNode node, MachineRpcClient rpc, HttpContext http) => CallAsync<FileFetchResult>(id, request, node, rpc, http);

    static Task<Results<Ok<FileStoreResult>, NotFound, ForbidHttpResult, BadRequest<string>>> StoreAsync(ulong id, FileStoreRequest request,
        BtdbNode node, MachineRpcClient rpc, HttpContext http) =>
        BlobHash.IsValid(request.Sha256)
            ? CallAsync<FileStoreResult>(id, request, node, rpc, http)
            : Task.FromResult<Results<Ok<FileStoreResult>, NotFound, ForbidHttpResult, BadRequest<string>>>(TypedResults.BadRequest("Not a lowercase SHA-256."));

    /// <summary>What crosses the machine's boundary or starts something on it; browsing is not audited.</summary>
    static (string Action, string Detail)? AuditLineOf(MachineRpc request) => request switch
    {
        FileFetchRequest fetch => ("file.downloaded", fetch.Path),
        FileStoreRequest store => ("file.uploaded", Path.Combine(store.Directory, store.Name)),
        TerminalOpenRequest open => ("terminal.opened", open.WorkingDirectory ?? "home folder"),
        TerminalCloseRequest close => ("terminal.closed", close.TerminalId),
        _ => null
    };

    static async Task<Results<Ok<T>, NotFound, ForbidHttpResult, BadRequest<string>>> CallAsync<T>(ulong id, MachineRpc request, BtdbNode node,
        MachineRpcClient rpc, HttpContext http) where T : MachineRpcResult
    {
        if (http.User.IsReadOnly()) return TypedResults.Forbid();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<IMachineTable>().FindByIdOrDefault(id) is not { } machine) return TypedResults.NotFound();
            if (!MachineAccess.CanControl(http.User.UserId() ?? 0, machine)) return TypedResults.Forbid();
        }
        var answer = await rpc.CallAsync(id, request, http.RequestAborted);
        if (answer is T && AuditLineOf(request) is var (action, detail))
            await AuditEndpoints.RecordAsync(publisher, http.User.UserId() ?? 0, action, $"machine {id}", detail,
                http.RequestServices.GetRequiredService<ILogger<MachineRpcClient>>());
        return answer switch
        {
            T result => TypedResults.Ok(result),
            RpcFailure failure => TypedResults.BadRequest(failure.Message),
            _ => TypedResults.BadRequest("The daemon gave an unexpected answer.")
        };
    }
}
