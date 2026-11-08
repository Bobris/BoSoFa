using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Machines;

/// <summary>A machine's file browser (plan §5.6) and terminals. Both need the right to control that machine.</summary>
public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
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

    static async Task<Results<Ok<T>, NotFound, ForbidHttpResult, BadRequest<string>>> CallAsync<T>(ulong id, MachineRpc request, BtdbNode node,
        MachineRpcClient rpc, HttpContext http) where T : MachineRpcResult
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<IMachineTable>().FindByIdOrDefault(id) is not { } machine) return TypedResults.NotFound();
            if (!MachineAccess.CanControl(http.User.UserId() ?? 0, machine)) return TypedResults.Forbid();
        }
        return await rpc.CallAsync(id, request, http.RequestAborted) switch
        {
            T result => TypedResults.Ok(result),
            RpcFailure failure => TypedResults.BadRequest(failure.Message),
            _ => TypedResults.BadRequest("The daemon gave an unexpected answer.")
        };
    }
}
