namespace EcoPause.Hardware.Abstractions;

public enum GpuOperationState { Unknown, Limited, Restored, Unchanged }

public sealed record GpuOperationResult(
    GpuOperationState State,
    bool Verified,
    bool RecoveryPending,
    uint FinalLimit,
    uint DefaultLimit)
{
    public bool IsValid => Verified && DefaultLimit > 0 && State switch
    {
        GpuOperationState.Limited => RecoveryPending && FinalLimit > 0 && FinalLimit < DefaultLimit,
        GpuOperationState.Restored => !RecoveryPending && FinalLimit == DefaultLimit,
        GpuOperationState.Unchanged => !RecoveryPending && FinalLimit > 0,
        _ => false
    };
}
