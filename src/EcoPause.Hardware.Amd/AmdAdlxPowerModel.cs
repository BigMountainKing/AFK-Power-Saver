using System.Security.Cryptography;
using System.Text;
using EcoPause.Core.Recovery;
using EcoPause.Hardware.Abstractions;
using EcoPause.LiveCanary.Model;

namespace EcoPause.Hardware.Amd;

internal static class AmdAdlxPowerModel
{
    public const string Provider = "AMD";
    public const string ProbeBackend = "ADLX";
    public const string ControlBackend = "ADLX-ManualPowerTuning";
    public const string StateDirectoryName = "AFKPowerSaverAmdAdlx";

    public static int ToDefaultRelativePercentage(int offset, int defaultOffset) =>
        checked(100 + offset - defaultOffset);

    public static int ToOffsetPercentage(int relativePercentage, int defaultOffset) =>
        checked(defaultOffset + relativePercentage - 100);

    public static DeviceFingerprint CreateFingerprint(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        return new DeviceFingerprint(
            "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    public static LiveCanaryDeviceState CreateState(AmdAdlxGpuInfo info)
    {
        var current = ToDefaultRelativePercentage(info.CurrentOffsetPercent, info.DefaultOffsetPercent);
        var minimum = ToDefaultRelativePercentage(info.MinimumOffsetPercent, info.DefaultOffsetPercent);
        var maximum = ToDefaultRelativePercentage(info.MaximumOffsetPercent, info.DefaultOffsetPercent);
        var state = PortableLivePowerPolicy.ValidateDevice(
            NormalizeName(info.Name),
            CreateFingerprint(info.Identity),
            EncodePercentage(current),
            EncodePercentage(100),
            EncodePercentage(minimum),
            EncodePercentage(maximum));
        return state with
        {
            Provider = Provider,
            Backend = ControlBackend,
            LimitKind = GpuPowerLimitKind.DefaultRelativePercentage,
            LimitStepMilliwatts = EncodePercentage(info.StepPercent),
            StateDirectoryName = StateDirectoryName
        };
    }

    public static uint EncodePercentage(int value)
    {
        if (value is <= 0 or > 1_000)
        {
            throw new InvalidOperationException("The AMD driver returned an invalid default-relative percentage.");
        }
        return checked((uint)value * 1_000);
    }

    public static int DecodePercentage(uint encoded)
    {
        if (encoded % 1_000 != 0)
        {
            throw new InvalidOperationException("The AMD power-limit percentage was not encoded canonically.");
        }
        return checked((int)(encoded / 1_000));
    }

    public static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "AMD Radeon GPU" : name.Trim();
}
