using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>A question for a machine that someone is waiting for. Unlike a <see cref="MachineCommand"/> it is not
/// durable: an offline machine fails the call at once, and a file written hours later would surprise everyone.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]
[JsonDerivedType(typeof(FileRootsRequest), "fileRoots")]
[JsonDerivedType(typeof(FileListRequest), "fileList")]
[JsonDerivedType(typeof(FileReadRequest), "fileRead")]
[JsonDerivedType(typeof(FileFetchRequest), "fileFetch")]
[JsonDerivedType(typeof(FileStoreRequest), "fileStore")]
[JsonDerivedType(typeof(TerminalOpenRequest), "terminalOpen")]
[JsonDerivedType(typeof(TerminalListRequest), "terminalList")]
[JsonDerivedType(typeof(TerminalAttachRequest), "terminalAttach")]
[JsonDerivedType(typeof(TerminalInputRequest), "terminalInput")]
[JsonDerivedType(typeof(TerminalResizeRequest), "terminalResize")]
[JsonDerivedType(typeof(TerminalCloseRequest), "terminalClose")]
[JsonDerivedType(typeof(CodexUsageRequest), "codexUsage")]
[JsonDerivedType(typeof(HarnessModelsRequest), "harnessModels")]
[JsonDerivedType(typeof(MachineResourcesRequest), "resources")]
[JsonDerivedType(typeof(ToolRunRequest), "toolRun")]
[JsonDerivedType(typeof(ToolCancelRequest), "toolCancel")]
[JsonDerivedType(typeof(ToolDiffRequest), "toolDiff")]
[JsonDerivedType(typeof(McpToolsRequest), "mcpTools")]
public abstract record MachineRpc;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]
[JsonDerivedType(typeof(RpcFailure), "failure")]
[JsonDerivedType(typeof(FileRootsResult), "fileRoots")]
[JsonDerivedType(typeof(FileListResult), "fileList")]
[JsonDerivedType(typeof(FileTextResult), "fileText")]
[JsonDerivedType(typeof(FileFetchResult), "fileFetch")]
[JsonDerivedType(typeof(FileStoreResult), "fileStore")]
[JsonDerivedType(typeof(RpcDone), "done")]
[JsonDerivedType(typeof(TerminalOpenResult), "terminalOpen")]
[JsonDerivedType(typeof(TerminalListResult), "terminalList")]
[JsonDerivedType(typeof(TerminalAttachResult), "terminalAttach")]
[JsonDerivedType(typeof(CodexUsageResult), "codexUsage")]
[JsonDerivedType(typeof(HarnessModelsResult), "harnessModels")]
[JsonDerivedType(typeof(MachineResourcesResult), "resources")]
[JsonDerivedType(typeof(ToolRunResult), "toolRun")]
[JsonDerivedType(typeof(ToolDiffResult), "toolDiff")]
[JsonDerivedType(typeof(McpToolsResult), "mcpTools")]
public abstract record MachineRpcResult;

/// <summary>The models a harness offers on this machine, as it reports them with its own sign-in there.</summary>
public sealed record HarnessModelsRequest(string Harness) : MachineRpc;

/// <param name="Id">What the harness takes as its model.</param>
/// <param name="ReasoningEfforts">The levels the model can think at, as the harness names them; none when it has no such choice.</param>
/// <param name="DefaultEffort">The level used when none is chosen, when the harness says.</param>
/// <param name="IsDefault">The harness's own choice when no model is named.</param>
public sealed record HarnessModelDto(string Id, string Name, string? Description, string[] ReasoningEfforts, string? DefaultEffort = null,
    bool IsDefault = false);

public sealed record HarnessModelsResult(HarnessModelDto[] Models) : MachineRpcResult;

/// <summary>Measure the computer now: free memory and disk change all the time, so they are asked for, not reported.</summary>
public sealed record MachineResourcesRequest : MachineRpc;

public sealed record MachineResourcesResult(MachineResources Resources) : MachineRpcResult;

/// <summary>Why a call did not work, for people to read.</summary>
public sealed record RpcFailure(string Message) : MachineRpcResult;

public sealed record FileRootsRequest : MachineRpc;

public sealed record FileListRequest(string Path) : MachineRpc;

/// <summary>Reads text from <c>Offset</c>, up to one chunk (a page asks for more with <c>NextOffset</c>).</summary>
public sealed record FileReadRequest(string Path, long Offset) : MachineRpc;

/// <summary>Copies a file of the machine to the blob store, so people can download it.</summary>
public sealed record FileFetchRequest(string Path) : MachineRpc;

/// <summary>Writes a blob to <c>Directory/Name</c> on the machine.</summary>
public sealed record FileStoreRequest(string Directory, string Name, string Sha256, bool Overwrite) : MachineRpc;

/// <summary>The folders this machine lets people browse: its sessions' working directories and configured folders.</summary>
public sealed record FileRootsResult(string[] Roots) : MachineRpcResult;

public sealed record FileEntry(string Name, string Path, bool IsDirectory, bool IsLink, long Size, DateTime ModifiedAt);

/// <summary><c>Parent</c> is null at a root.</summary>
public sealed record FileListResult(string Path, string? Parent, FileEntry[] Entries, bool Truncated) : MachineRpcResult;

/// <summary><c>Binary</c> files have no text.</summary>
public sealed record FileTextResult(string Path, long Offset, long NextOffset, long TotalSize, bool Binary, string Text)
    : MachineRpcResult;

public sealed record FileFetchResult(string Name, BlobRef Blob) : MachineRpcResult;

public sealed record FileStoreResult(FileEntry Entry) : MachineRpcResult;
