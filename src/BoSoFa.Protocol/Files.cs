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
public abstract record MachineRpcResult;

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
