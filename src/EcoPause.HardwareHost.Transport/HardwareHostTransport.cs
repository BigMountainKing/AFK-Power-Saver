using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using EcoPause.HardwareHost.Protocol;
using Microsoft.Win32.SafeHandles;

namespace EcoPause.HardwareHost.Transport;

[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "This boundary exposes only bounded transport failures.")]
public sealed class HardwareHostTransportException : Exception
{
    public HardwareHostTransportException(string message)
        : base(message)
    {
    }

    public HardwareHostTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class HardwareHostPipeName
{
    private const string Prefix = "EcoPause-";
    private const int IdentifierLength = 32;

    public static string Create() => $"{Prefix}{Guid.NewGuid():N}";

    public static void Validate(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        if (pipeName.Length != Prefix.Length + IdentifierLength ||
            !pipeName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The pipe name is not an AFK Power Saver-generated name.", nameof(pipeName));
        }

        foreach (var character in pipeName.AsSpan(Prefix.Length))
        {
            if (!char.IsAsciiHexDigit(character))
            {
                throw new ArgumentException("The pipe name contains invalid characters.", nameof(pipeName));
            }
        }
    }
}

public static class BoundedJsonPipe
{
    public const int MaximumFrameBytes = 64 * 1024;
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16,
        WriteIndented = false
    };

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new HardwareHostTransportException("The transport value could not be serialized safely.", exception);
        }

        try
        {
            if (payload.Length is <= 0 or > MaximumFrameBytes)
            {
                throw new HardwareHostTransportException("The outgoing transport frame has an invalid size.");
            }

            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

            using var timeout = CreateTimeout(DefaultOperationTimeout, cancellationToken);
            await stream.WriteAsync(header, timeout.Token);
            await stream.WriteAsync(payload, timeout.Token);
            await stream.FlushAsync(timeout.Token);
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
        => await ReadAsync<T>(stream, DefaultOperationTimeout, cancellationToken);

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "A pipe read timeout must be greater than zero and no more than two minutes.");
        }

        var header = new byte[sizeof(int)];
        using var timeout = CreateTimeout(operationTimeout, cancellationToken);

        await ReadExactlyAsync(stream, header, timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
        {
            throw new HardwareHostTransportException("The incoming transport frame has an invalid size.");
        }

        var payload = new byte[length];
        try
        {
            await ReadExactlyAsync(stream, payload, timeout.Token);
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new HardwareHostTransportException("The incoming transport frame was empty or malformed.");
        }
        catch (JsonException exception)
        {
            throw new HardwareHostTransportException("The incoming transport frame was malformed.", exception);
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (count == 0)
            {
                throw new HardwareHostTransportException("The transport stream ended before the frame was complete.");
            }

            offset += count;
        }
    }

    private static CancellationTokenSource CreateTimeout(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }
}

[SupportedOSPlatform("windows")]
public static class HardwareHostNamedPipe
{
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        HardwareHostPipeName.Validate(pipeName);
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 4096);
    }

    public static async Task WaitForConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken = default) =>
        await WaitForConnectionAsync(server, TimeSpan.FromSeconds(10), cancellationToken);

    public static async Task WaitForConnectionAsync(
        NamedPipeServerStream server,
        TimeSpan connectionTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (connectionTimeout <= TimeSpan.Zero || connectionTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectionTimeout),
                "A connection timeout must be positive and no longer than two minutes.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectionTimeout);
        await server.WaitForConnectionAsync(timeout.Token);
    }

    public static async Task<NamedPipeClientStream> ConnectClientAsync(
        string pipeName,
        CancellationToken cancellationToken = default) =>
        await ConnectClientCoreAsync(
            pipeName,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            cancellationToken);

    public static async Task<NamedPipeClientStream> ConnectVerifiedClientAsync(
        string pipeName,
        int expectedServerProcessId,
        CancellationToken cancellationToken = default)
    {
        HardwareHostPipePeer.ValidateExpectedProcessId(expectedServerProcessId);
        var client = await ConnectClientCoreAsync(
            pipeName,
            PipeOptions.Asynchronous,
            cancellationToken);

        try
        {
            HardwareHostPipePeer.VerifyServerProcess(client, expectedServerProcessId);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<NamedPipeClientStream> ConnectClientCoreAsync(
        string pipeName,
        PipeOptions options,
        CancellationToken cancellationToken)
    {
        HardwareHostPipeName.Validate(pipeName);
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            options);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }
}

[SupportedOSPlatform("windows")]
public static class HardwareHostPipePeer
{
    public static void VerifyClientProcess(
        NamedPipeServerStream server,
        int expectedProcessId)
    {
        ArgumentNullException.ThrowIfNull(server);
        ValidateExpectedProcessId(expectedProcessId);

        if (!server.IsConnected)
        {
            throw new HardwareHostTransportException("The pipe server is not connected.");
        }

        if (!NativeMethods.GetNamedPipeClientProcessId(server.SafePipeHandle, out var actualProcessId))
        {
            throw CreatePeerLookupException();
        }

        VerifyProcessId(actualProcessId, expectedProcessId);
    }

    public static void VerifyServerProcess(
        NamedPipeClientStream client,
        int expectedProcessId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateExpectedProcessId(expectedProcessId);

        if (!client.IsConnected)
        {
            throw new HardwareHostTransportException("The pipe client is not connected.");
        }

        if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var actualProcessId))
        {
            throw CreatePeerLookupException();
        }

        VerifyProcessId(actualProcessId, expectedProcessId);
    }

    public static void ValidateExpectedProcessId(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "A peer process ID must be positive.");
        }
    }

    private static void VerifyProcessId(uint actualProcessId, int expectedProcessId)
    {
        if (actualProcessId != (uint)expectedProcessId)
        {
            throw new HardwareHostTransportException("The connected pipe peer is not the expected process.");
        }
    }

    private static HardwareHostTransportException CreatePeerLookupException() =>
        new(
            "Windows could not identify the connected pipe peer.",
            new Win32Exception(Marshal.GetLastWin32Error()));

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern bool GetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern bool GetNamedPipeServerProcessId(
            SafePipeHandle pipe,
            out uint serverProcessId);
    }
}

public sealed record HardwareHostTransportBootstrap(
    HardwareHostSessionBootstrap Session,
    Guid SnapshotId);

public sealed record HardwareHostWireResult(
    HardwareHostProcessOutcome Outcome,
    string? ExecutionOutcome,
    string Message);

public sealed record HardwareHostElevationSummary(
    bool PeerVerified,
    uint FinalLimitMilliwatts,
    string RecoveryStage,
    bool SafeFinalState);

public sealed record HardwareHostElevationStatus(
    bool Succeeded,
    string Stage,
    string Message);

public sealed record HardwareHostCrashRecoverySummary(
    bool PeerVerified,
    uint FinalLimitMilliwatts,
    string RecoveryStage,
    bool SafeFinalState,
    bool SimulationArtifactsCleaned);

public sealed record HardwareHostPersistentToggleBootstrap(
    HardwareHostSessionBootstrap Session,
    Guid SnapshotId,
    HardwareHostOperation Operation);

public sealed record HardwareHostPersistentToggleSummary(
    bool PeerVerified,
    string ToggleState,
    string ProfileId,
    uint TargetLimitMilliwatts,
    uint FinalLimitMilliwatts,
    string RecoveryStage,
    bool SafeFinalState,
    bool SimulationArtifactsCleaned);
