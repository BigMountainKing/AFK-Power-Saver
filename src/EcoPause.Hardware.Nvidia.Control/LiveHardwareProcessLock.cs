using System.Runtime.Versioning;
using EcoPause.LiveCanary.Model;

namespace EcoPause.Hardware.Nvidia.Control;

/// <summary>
/// Serializes live GPU writers while preserving Windows mutex thread ownership.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LiveHardwareProcessLock : IDisposable
{
    private const string TestMutexPrefix = @"Local\EcoPause-LockTest-";

    private readonly string _mutexName;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly ManualResetEventSlim _releaseRequested = new(initialState: false);
    private readonly Thread _ownerThread;
    private Exception? _ownerException;
    private bool _acquired;
    private int _disposeState;

    private LiveHardwareProcessLock(string mutexName)
    {
        _mutexName = mutexName;
        _ownerThread = new Thread(OwnMutex)
        {
            IsBackground = true,
            Name = "AFK Power Saver live-hardware mutex owner"
        };

        _ownerThread.Start();
        _ready.Wait();

        if (_ownerException is not null)
        {
            FinishFailedConstruction();
            throw new InvalidOperationException("The live-hardware lock could not be initialized.", _ownerException);
        }

        if (!_acquired)
        {
            FinishFailedConstruction();
            throw new InvalidOperationException("Another elevated AFK Power Saver live-hardware helper is active.");
        }
    }

    public static LiveHardwareProcessLock Acquire() =>
        new(LiveCanaryPolicy.GlobalHardwareLockName);

    internal static LiveHardwareProcessLock AcquireForTesting(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        if (!mutexName.StartsWith(TestMutexPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Test lock names must use the isolated EcoPause test prefix.", nameof(mutexName));
        }

        return new LiveHardwareProcessLock(mutexName);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _releaseRequested.Set();
        _ownerThread.Join();
        _ready.Dispose();
        _releaseRequested.Dispose();
        GC.SuppressFinalize(this);

        if (_ownerException is not null)
        {
            throw new InvalidOperationException("The live-hardware lock could not be released safely.", _ownerException);
        }
    }

    private void OwnMutex()
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            try
            {
                _acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _acquired = true;
            }

            _ready.Set();
            if (!_acquired)
            {
                return;
            }

            _releaseRequested.Wait();
            mutex.ReleaseMutex();
        }
#pragma warning disable CA1031 // The constructor or disposer reports owner-thread failures.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _ownerException = exception;
            _ready.Set();
        }
    }

    private void FinishFailedConstruction()
    {
        _releaseRequested.Set();
        _ownerThread.Join();
        _ready.Dispose();
        _releaseRequested.Dispose();
    }
}
