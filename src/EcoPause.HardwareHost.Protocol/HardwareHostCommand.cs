using System.Security.Cryptography;
using System.Text.Json;

namespace EcoPause.HardwareHost.Protocol;

public enum HardwareHostOperation
{
    ActivatePrepared = 1,
    RestorePending = 2
}

public sealed record HardwareHostCommand(
    int ProtocolVersion,
    Guid SessionId,
    Guid CommandId,
    long Sequence,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    HardwareHostOperation Operation,
    Guid SnapshotId);

public sealed record HardwareHostCommandEnvelope(
    HardwareHostCommand Command,
    string AuthenticationTagSha256);

public enum HardwareHostValidationOutcome
{
    Accepted,
    SessionUnavailable,
    ProtocolRejected,
    AuthenticationFailed,
    CommandExpired,
    ReplayRejected,
    SequenceRejected
}

public sealed record HardwareHostValidationResult(
    HardwareHostValidationOutcome Outcome,
    string Message)
{
    public bool Accepted => Outcome == HardwareHostValidationOutcome.Accepted;
}

public sealed class HardwareHostClientSession : IDisposable
{
    private static readonly TimeSpan DefaultCommandLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromMinutes(2);

    private readonly byte[] _sessionKey;
    private readonly TimeProvider _timeProvider;
    private readonly object _sequenceLock = new();
    private long _nextSequence = 1;
    private bool _disposed;

    internal HardwareHostClientSession(
        Guid sessionId,
        byte[] sessionKey,
        TimeProvider timeProvider)
    {
        SessionId = sessionId;
        _sessionKey = sessionKey;
        _timeProvider = timeProvider;
    }

    public Guid SessionId { get; }

    public HardwareHostCommandEnvelope CreateCommand(
        HardwareHostOperation operation,
        Guid snapshotId,
        TimeSpan? lifetime = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var effectiveLifetime = lifetime ?? DefaultCommandLifetime;

        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > MaximumCommandLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                $"Command lifetime must be greater than zero and no more than {MaximumCommandLifetime.TotalMinutes} minutes.");
        }

        var issuedAtUtc = _timeProvider.GetUtcNow();
        long sequence;
        lock (_sequenceLock)
        {
            sequence = _nextSequence++;
        }

        var command = new HardwareHostCommand(
            HardwareHostProtocol.Version,
            SessionId,
            Guid.NewGuid(),
            sequence,
            issuedAtUtc,
            issuedAtUtc.Add(effectiveLifetime),
            operation,
            snapshotId);

        return Sign(command);
    }

    public HardwareHostCommandEnvelope Sign(HardwareHostCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        return new HardwareHostCommandEnvelope(
            command,
            HardwareHostProtocol.ComputeAuthenticationTag(command, _sessionKey));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_sessionKey);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

public sealed class HardwareHostSessionState : IDisposable
{
    private static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AllowedFutureClockSkew = TimeSpan.FromSeconds(5);

    private readonly byte[] _sessionKey;
    private readonly HashSet<Guid> _consumedCommandIds = [];
    private readonly object _validationLock = new();
    private long _nextSequence = 1;
    private bool _disposed;

    internal HardwareHostSessionState(
        Guid sessionId,
        byte[] sessionKey,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        SessionId = sessionId;
        _sessionKey = sessionKey;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid SessionId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public HardwareHostValidationResult ValidateAndConsume(
        HardwareHostCommandEnvelope envelope,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Command);

        lock (_validationLock)
        {
            if (_disposed || nowUtc.Offset != TimeSpan.Zero || nowUtc > ExpiresAtUtc)
            {
                return Reject(HardwareHostValidationOutcome.SessionUnavailable, "The helper session is unavailable or expired.");
            }

            var command = envelope.Command;
            if (command.ProtocolVersion != HardwareHostProtocol.Version ||
                command.SessionId != SessionId ||
                command.CommandId == Guid.Empty ||
                command.SnapshotId == Guid.Empty ||
                !Enum.IsDefined(command.Operation))
            {
                return Reject(HardwareHostValidationOutcome.ProtocolRejected, "The command shape or protocol version is invalid.");
            }

            if (!HardwareHostProtocol.AuthenticationTagMatches(envelope, _sessionKey))
            {
                return Reject(HardwareHostValidationOutcome.AuthenticationFailed, "The command authentication tag is invalid.");
            }

            if (command.IssuedAtUtc.Offset != TimeSpan.Zero ||
                command.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                command.IssuedAtUtc < CreatedAtUtc ||
                command.IssuedAtUtc > nowUtc.Add(AllowedFutureClockSkew) ||
                command.ExpiresAtUtc <= command.IssuedAtUtc ||
                command.ExpiresAtUtc - command.IssuedAtUtc > MaximumCommandLifetime ||
                command.ExpiresAtUtc > ExpiresAtUtc ||
                nowUtc > command.ExpiresAtUtc)
            {
                return Reject(HardwareHostValidationOutcome.CommandExpired, "The command timestamps are invalid or expired.");
            }

            if (_consumedCommandIds.Contains(command.CommandId))
            {
                return Reject(HardwareHostValidationOutcome.ReplayRejected, "The command has already been consumed.");
            }

            if (command.Sequence != _nextSequence)
            {
                return Reject(HardwareHostValidationOutcome.SequenceRejected, "The command sequence is not the next expected value.");
            }

            _consumedCommandIds.Add(command.CommandId);
            _nextSequence++;
            return new HardwareHostValidationResult(HardwareHostValidationOutcome.Accepted, "The command was authenticated and consumed.");
        }
    }

    public void Dispose()
    {
        lock (_validationLock)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_sessionKey);
            _consumedCommandIds.Clear();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private static HardwareHostValidationResult Reject(
        HardwareHostValidationOutcome outcome,
        string message) => new(outcome, message);
}

public sealed class HardwareHostSessionPair : IDisposable
{
    internal HardwareHostSessionPair(
        HardwareHostSessionState host,
        HardwareHostClientSession client)
    {
        Host = host;
        Client = client;
    }

    public HardwareHostSessionState Host { get; }

    public HardwareHostClientSession Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        Host.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class HardwareHostSessionBootstrap : IDisposable
{
    private readonly byte[] _sessionKey;
    private bool _disposed;

    public HardwareHostSessionBootstrap(
        Guid sessionId,
        ReadOnlyMemory<byte> sessionKey,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A bootstrap requires a non-empty session ID.", nameof(sessionId));
        }

        if (sessionKey.Length != HardwareHostProtocol.SessionKeyBytes)
        {
            throw new ArgumentException(
                $"A bootstrap key must contain exactly {HardwareHostProtocol.SessionKeyBytes} bytes.",
                nameof(sessionKey));
        }

        if (createdAtUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc.Offset != TimeSpan.Zero ||
            createdAtUtc == default ||
            expiresAtUtc <= createdAtUtc ||
            expiresAtUtc - createdAtUtc > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentException("Bootstrap timestamps must describe a valid UTC interval.");
        }

        SessionId = sessionId;
        _sessionKey = sessionKey.ToArray();
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid SessionId { get; }

    public ReadOnlyMemory<byte> SessionKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sessionKey;
        }
    }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_sessionKey);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

public sealed class HardwareHostTransportSession : IDisposable
{
    internal HardwareHostTransportSession(
        HardwareHostSessionState host,
        HardwareHostSessionBootstrap bootstrap)
    {
        Host = host;
        Bootstrap = bootstrap;
    }

    public HardwareHostSessionState Host { get; }

    public HardwareHostSessionBootstrap Bootstrap { get; }

    public void Dispose()
    {
        Bootstrap.Dispose();
        Host.Dispose();
        GC.SuppressFinalize(this);
    }
}

public static class HardwareHostSessionFactory
{
    private static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromMinutes(30);

    public static HardwareHostSessionPair Create(
        TimeProvider? timeProvider = null,
        TimeSpan? lifetime = null)
    {
        using var transportSession = CreateTransportSession(timeProvider, lifetime);
        return new HardwareHostSessionPair(
            new HardwareHostSessionState(
                transportSession.Host.SessionId,
                transportSession.Bootstrap.SessionKey.ToArray(),
                transportSession.Host.CreatedAtUtc,
                transportSession.Host.ExpiresAtUtc),
            ImportClientSession(transportSession.Bootstrap, timeProvider));
    }

    public static HardwareHostTransportSession CreateTransportSession(
        TimeProvider? timeProvider = null,
        TimeSpan? lifetime = null)
    {
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        var effectiveLifetime = lifetime ?? DefaultSessionLifetime;

        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > MaximumSessionLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                $"Session lifetime must be greater than zero and no more than {MaximumSessionLifetime.TotalMinutes} minutes.");
        }

        var sessionId = Guid.NewGuid();
        var sessionKey = RandomNumberGenerator.GetBytes(32);
        var createdAtUtc = effectiveTimeProvider.GetUtcNow();

        try
        {
            return new HardwareHostTransportSession(
                new HardwareHostSessionState(
                    sessionId,
                    sessionKey.ToArray(),
                    createdAtUtc,
                    createdAtUtc.Add(effectiveLifetime)),
                new HardwareHostSessionBootstrap(
                    sessionId,
                    sessionKey,
                    createdAtUtc,
                    createdAtUtc.Add(effectiveLifetime)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    public static HardwareHostClientSession ImportClientSession(
        HardwareHostSessionBootstrap bootstrap,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        var nowUtc = effectiveTimeProvider.GetUtcNow();

        if (nowUtc.Offset != TimeSpan.Zero ||
            nowUtc < bootstrap.CreatedAtUtc.Subtract(TimeSpan.FromSeconds(5)) ||
            nowUtc > bootstrap.ExpiresAtUtc)
        {
            throw new InvalidOperationException("The transported helper session is not currently valid.");
        }

        return new HardwareHostClientSession(
            bootstrap.SessionId,
            bootstrap.SessionKey.ToArray(),
            effectiveTimeProvider);
    }
}

internal static class HardwareHostProtocol
{
    private static readonly JsonSerializerOptions CanonicalSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public const int Version = 1;

    public const int SessionKeyBytes = 32;

    public static string ComputeAuthenticationTag(
        HardwareHostCommand command,
        byte[] sessionKey)
    {
        var commandBytes = JsonSerializer.SerializeToUtf8Bytes(command, CanonicalSerializerOptions);
        return Convert.ToHexString(HMACSHA256.HashData(sessionKey, commandBytes));
    }

    public static bool AuthenticationTagMatches(
        HardwareHostCommandEnvelope envelope,
        byte[] sessionKey)
    {
        byte[] suppliedTag;
        try
        {
            suppliedTag = Convert.FromHexString(envelope.AuthenticationTagSha256);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedTag = Convert.FromHexString(
            ComputeAuthenticationTag(envelope.Command, sessionKey));
        return CryptographicOperations.FixedTimeEquals(suppliedTag, expectedTag);
    }
}
