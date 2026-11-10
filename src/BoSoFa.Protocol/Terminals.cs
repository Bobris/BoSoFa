namespace BoSoFa.Protocol;

/// <summary>Calls about a machine's terminals. The daemon applies them in arrival order, so keystrokes stay in order.</summary>
public abstract record TerminalRpc : MachineRpc;

/// <summary>Opens a shell; without a directory it starts in the home folder of the daemon's user.</summary>
public sealed record TerminalOpenRequest(string? WorkingDirectory, int Cols, int Rows) : TerminalRpc;

public sealed record TerminalListRequest : TerminalRpc;

/// <summary>Asks for the retained output; new output follows as <see cref="TerminalOutputMessage"/>.</summary>
public sealed record TerminalAttachRequest(string TerminalId) : TerminalRpc;

/// <summary><c>Data</c> is base64: a terminal carries bytes.</summary>
public sealed record TerminalInputRequest(string TerminalId, string Data) : TerminalRpc;

public sealed record TerminalResizeRequest(string TerminalId, int Cols, int Rows) : TerminalRpc;

public sealed record TerminalCloseRequest(string TerminalId) : TerminalRpc;

/// <param name="LastActivityAt">When it last printed; absent from daemons before it.</param>
public sealed record TerminalDto(string Id, string Shell, string WorkingDirectory, DateTime StartedAt, bool Exited, int? ExitCode,
    DateTime? LastActivityAt = null);

/// <summary>A terminal of one of the person's machines, for a list across machines.</summary>
public sealed record MachineTerminalDto(ulong MachineId, string MachineName, TerminalDto Terminal);

public sealed record RpcDone : MachineRpcResult;

public sealed record TerminalOpenResult(TerminalDto Terminal) : MachineRpcResult;

public sealed record TerminalListResult(TerminalDto[] Terminals) : MachineRpcResult;

/// <summary>The last output (base64), ending at byte <c>EndOffset</c> of the terminal's whole output.</summary>
public sealed record TerminalAttachResult(long EndOffset, string Data, bool Exited, int? ExitCode) : MachineRpcResult;
