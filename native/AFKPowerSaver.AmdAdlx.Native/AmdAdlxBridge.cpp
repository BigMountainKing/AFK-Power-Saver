// AFK Power Saver's minimal ABI bridge to the AMD driver-installed ADLX library.
// The declarations below mirror ADLX's documented C ABI; no AMD SDK binary or
// sample code is redistributed with the application.

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace
{
    constexpr int Ok = 0;
    constexpr int AdlxResetNeeded = 18;
    constexpr int BridgeLoadFailed = 1001;
    constexpr int BridgeExportMissing = 1002;
    constexpr int BridgeNoSupportedGpu = 1003;
    constexpr int BridgeAmbiguousGpu = 1004;
    constexpr int BridgeGpuNotFound = 1005;
    constexpr int BridgeInvalidRange = 1006;
    constexpr int BridgeReadbackMismatch = 1007;
    constexpr int BridgeResetRequired = 1008;

    using AdlxResult = int32_t;
    using AdlxInt = int32_t;
    using AdlxUInt = uint32_t;
    using AdlxLong = long;
    using AdlxBool = uint8_t;

    struct AdlxIntRange
    {
        AdlxInt minValue;
        AdlxInt maxValue;
        AdlxInt step;
    };

    struct IAdlxInterface;
    struct IAdlxGpu;
    struct IAdlxGpuList;
    struct IAdlxGpuTuningServices;
    struct IAdlxManualPowerTuning;
    struct IAdlxManualPowerTuning1;
    struct IAdlxSystem;

    struct IAdlxInterfaceVtable
    {
        AdlxLong(__stdcall* Acquire)(IAdlxInterface* self);
        AdlxLong(__stdcall* Release)(IAdlxInterface* self);
        AdlxResult(__stdcall* QueryInterface)(IAdlxInterface* self, const wchar_t* interfaceId, void** result);
    };

    struct IAdlxInterface { const IAdlxInterfaceVtable* vtable; };

    struct IAdlxGpuVtable
    {
        AdlxLong(__stdcall* Acquire)(IAdlxGpu* self);
        AdlxLong(__stdcall* Release)(IAdlxGpu* self);
        AdlxResult(__stdcall* QueryInterface)(IAdlxGpu* self, const wchar_t* interfaceId, void** result);
        AdlxResult(__stdcall* VendorId)(IAdlxGpu* self, const char** vendorId);
        void* AsicFamilyType;
        void* Type;
        void* IsExternal;
        AdlxResult(__stdcall* Name)(IAdlxGpu* self, const char** name);
        void* DriverPath;
        AdlxResult(__stdcall* PnpString)(IAdlxGpu* self, const char** pnpString);
        void* HasDesktops;
        void* TotalVram;
        void* VramType;
        void* BiosInfo;
        void* DeviceId;
        void* RevisionId;
        void* SubSystemId;
        void* SubSystemVendorId;
        AdlxResult(__stdcall* UniqueId)(IAdlxGpu* self, AdlxInt* uniqueId);
    };

    struct IAdlxGpu { const IAdlxGpuVtable* vtable; };

    struct IAdlxGpuListVtable
    {
        AdlxLong(__stdcall* Acquire)(IAdlxGpuList* self);
        AdlxLong(__stdcall* Release)(IAdlxGpuList* self);
        void* QueryInterface;
        AdlxUInt(__stdcall* Size)(IAdlxGpuList* self);
        void* Empty;
        void* Begin;
        void* End;
        void* At;
        void* Clear;
        void* RemoveBack;
        void* AddBack;
        AdlxResult(__stdcall* AtGpu)(IAdlxGpuList* self, AdlxUInt location, IAdlxGpu** gpu);
        void* AddBackGpu;
    };

    struct IAdlxGpuList { const IAdlxGpuListVtable* vtable; };

    struct IAdlxGpuTuningServicesVtable
    {
        AdlxLong(__stdcall* Acquire)(IAdlxGpuTuningServices* self);
        AdlxLong(__stdcall* Release)(IAdlxGpuTuningServices* self);
        void* QueryInterface;
        void* GetChangedHandling;
        void* IsAtFactory;
        AdlxResult(__stdcall* ResetToFactory)(IAdlxGpuTuningServices* self, IAdlxGpu* gpu);
        void* IsSupportedAutoTuning;
        void* IsSupportedPresetTuning;
        void* IsSupportedManualGfxTuning;
        void* IsSupportedManualVramTuning;
        void* IsSupportedManualFanTuning;
        AdlxResult(__stdcall* IsSupportedManualPowerTuning)(IAdlxGpuTuningServices* self, IAdlxGpu* gpu, AdlxBool* supported);
        void* GetAutoTuning;
        void* GetPresetTuning;
        void* GetManualGfxTuning;
        void* GetManualVramTuning;
        void* GetManualFanTuning;
        AdlxResult(__stdcall* GetManualPowerTuning)(IAdlxGpuTuningServices* self, IAdlxGpu* gpu, IAdlxInterface** tuning);
    };

    struct IAdlxGpuTuningServices { const IAdlxGpuTuningServicesVtable* vtable; };

    struct IAdlxManualPowerTuningVtable
    {
        AdlxLong(__stdcall* Acquire)(IAdlxManualPowerTuning* self);
        AdlxLong(__stdcall* Release)(IAdlxManualPowerTuning* self);
        AdlxResult(__stdcall* QueryInterface)(IAdlxManualPowerTuning* self, const wchar_t* interfaceId, void** result);
        AdlxResult(__stdcall* GetPowerLimitRange)(IAdlxManualPowerTuning* self, AdlxIntRange* range);
        AdlxResult(__stdcall* GetPowerLimit)(IAdlxManualPowerTuning* self, AdlxInt* current);
        AdlxResult(__stdcall* SetPowerLimit)(IAdlxManualPowerTuning* self, AdlxInt current);
        void* IsSupportedTdcLimit;
        void* GetTdcLimitRange;
        void* GetTdcLimit;
        void* SetTdcLimit;
    };

    struct IAdlxManualPowerTuning { const IAdlxManualPowerTuningVtable* vtable; };

    struct IAdlxManualPowerTuning1Vtable
    {
        AdlxLong(__stdcall* Acquire)(IAdlxManualPowerTuning1* self);
        AdlxLong(__stdcall* Release)(IAdlxManualPowerTuning1* self);
        void* QueryInterface;
        void* GetPowerLimitRange;
        void* GetPowerLimit;
        void* SetPowerLimit;
        void* IsSupportedTdcLimit;
        void* GetTdcLimitRange;
        void* GetTdcLimit;
        void* SetTdcLimit;
        AdlxResult(__stdcall* GetPowerLimitDefault)(IAdlxManualPowerTuning1* self, AdlxInt* value);
        void* GetTdcLimitDefault;
    };

    struct IAdlxManualPowerTuning1 { const IAdlxManualPowerTuning1Vtable* vtable; };

    struct IAdlxSystemVtable
    {
        void* GetHybridGraphicsType;
        AdlxResult(__stdcall* GetGpus)(IAdlxSystem* self, IAdlxGpuList** gpus);
        void* QueryInterface;
        void* GetDisplayServices;
        void* GetDesktopServices;
        void* GetGpusChangedHandling;
        void* EnableLog;
        void* Get3dSettingsServices;
        AdlxResult(__stdcall* GetGpuTuningServices)(IAdlxSystem* self, IAdlxGpuTuningServices** services);
        void* GetPerformanceMonitoringServices;
        void* TotalSystemRam;
        void* GetI2c;
    };

    struct IAdlxSystem { const IAdlxSystemVtable* vtable; };

    using QueryFullVersionFunction = AdlxResult(__cdecl*)(uint64_t* version);
    using InitializeFunction = AdlxResult(__cdecl*)(uint64_t version, IAdlxSystem** system);
    using TerminateFunction = AdlxResult(__cdecl*)();

    struct AdlxSession
    {
        HMODULE library = nullptr;
        IAdlxSystem* system = nullptr;
        TerminateFunction terminate = nullptr;

        ~AdlxSession()
        {
            if (system != nullptr && terminate != nullptr)
            {
                terminate();
            }
            if (library != nullptr)
            {
                FreeLibrary(library);
            }
        }
    };

    void SetError(char* buffer, int capacity, const char* message)
    {
        if (buffer == nullptr || capacity <= 0) return;
        strncpy_s(buffer, static_cast<size_t>(capacity), message, _TRUNCATE);
    }

    void SetAdlxError(char* buffer, int capacity, const char* operation, AdlxResult result)
    {
        char message[256]{};
        sprintf_s(message, "%s failed with ADLX result %d.", operation, result);
        SetError(buffer, capacity, message);
    }

    bool IsSuccess(AdlxResult result)
    {
        return result == 0 || result == 1 || result == 2;
    }

    int OpenSession(AdlxSession& session, char* error, int errorCapacity)
    {
        session.library = LoadLibraryExW(L"amdadlx64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (session.library == nullptr)
        {
            SetError(error, errorCapacity, "The AMD ADLX library is not installed in Windows System32.");
            return BridgeLoadFailed;
        }

        const auto queryVersion = reinterpret_cast<QueryFullVersionFunction>(
            GetProcAddress(session.library, "ADLXQueryFullVersion"));
        const auto initialize = reinterpret_cast<InitializeFunction>(
            GetProcAddress(session.library, "ADLXInitialize"));
        session.terminate = reinterpret_cast<TerminateFunction>(
            GetProcAddress(session.library, "ADLXTerminate"));
        if (queryVersion == nullptr || initialize == nullptr || session.terminate == nullptr)
        {
            SetError(error, errorCapacity, "The installed AMD ADLX library is missing required exports.");
            return BridgeExportMissing;
        }

        uint64_t version = 0;
        auto result = queryVersion(&version);
        if (!IsSuccess(result))
        {
            SetAdlxError(error, errorCapacity, "ADLX version query", result);
            return result;
        }
        result = initialize(version, &session.system);
        if (!IsSuccess(result) || session.system == nullptr)
        {
            session.system = nullptr;
            SetAdlxError(error, errorCapacity, "ADLX initialization", result);
            return result;
        }
        return Ok;
    }

    struct SelectedGpu
    {
        IAdlxGpu* gpu = nullptr;
        IAdlxManualPowerTuning* power = nullptr;
        AdlxInt uniqueId = 0;
        const char* name = nullptr;
        const char* pnpString = nullptr;
        AdlxIntRange range{};
        AdlxInt current = 0;
        AdlxInt defaultValue = 0;
    };

    void ReleaseSelected(SelectedGpu& selected)
    {
        if (selected.power != nullptr)
        {
            selected.power->vtable->Release(selected.power);
            selected.power = nullptr;
        }
        if (selected.gpu != nullptr)
        {
            selected.gpu->vtable->Release(selected.gpu);
            selected.gpu = nullptr;
        }
    }

    int SelectGpu(
        IAdlxSystem* system,
        AdlxInt requiredUniqueId,
        bool requireIdentity,
        SelectedGpu& selected,
        char* error,
        int errorCapacity)
    {
        IAdlxGpuList* gpus = nullptr;
        IAdlxGpuTuningServices* services = nullptr;
        auto result = system->vtable->GetGpus(system, &gpus);
        if (!IsSuccess(result) || gpus == nullptr)
        {
            SetAdlxError(error, errorCapacity, "AMD GPU enumeration", result);
            return result;
        }
        result = system->vtable->GetGpuTuningServices(system, &services);
        if (!IsSuccess(result) || services == nullptr)
        {
            gpus->vtable->Release(gpus);
            SetAdlxError(error, errorCapacity, "AMD GPU tuning service lookup", result);
            return result;
        }

        int supportedCount = 0;
        const auto count = gpus->vtable->Size(gpus);
        for (AdlxUInt index = 0; index < count; ++index)
        {
            IAdlxGpu* gpu = nullptr;
            result = gpus->vtable->AtGpu(gpus, index, &gpu);
            if (!IsSuccess(result) || gpu == nullptr) continue;

            const char* vendorId = nullptr;
            AdlxInt uniqueId = 0;
            AdlxBool supported = 0;
            const auto vendorResult = gpu->vtable->VendorId(gpu, &vendorId);
            const auto idResult = gpu->vtable->UniqueId(gpu, &uniqueId);
            const auto supportResult = services->vtable->IsSupportedManualPowerTuning(services, gpu, &supported);
            const bool isAmd = IsSuccess(vendorResult) && vendorId != nullptr &&
                (strstr(vendorId, "1002") != nullptr || strstr(vendorId, "AMD") != nullptr);
            const bool identityMatches = !requireIdentity || uniqueId == requiredUniqueId;
            if (!isAmd || !IsSuccess(idResult) || !IsSuccess(supportResult) || supported == 0 || !identityMatches)
            {
                gpu->vtable->Release(gpu);
                continue;
            }

            IAdlxInterface* rawPower = nullptr;
            result = services->vtable->GetManualPowerTuning(services, gpu, &rawPower);
            if (!IsSuccess(result) || rawPower == nullptr)
            {
                gpu->vtable->Release(gpu);
                continue;
            }
            IAdlxManualPowerTuning* power = nullptr;
            result = rawPower->vtable->QueryInterface(
                rawPower,
                L"IADLXManualPowerTuning",
                reinterpret_cast<void**>(&power));
            rawPower->vtable->Release(rawPower);
            if (!IsSuccess(result) || power == nullptr)
            {
                gpu->vtable->Release(gpu);
                continue;
            }

            AdlxIntRange range{};
            AdlxInt current = 0;
            const auto rangeResult = power->vtable->GetPowerLimitRange(power, &range);
            const auto currentResult = power->vtable->GetPowerLimit(power, &current);
            if (!IsSuccess(rangeResult) || !IsSuccess(currentResult) ||
                range.step <= 0 || range.minValue > range.maxValue ||
                current < range.minValue || current > range.maxValue)
            {
                power->vtable->Release(power);
                gpu->vtable->Release(gpu);
                continue;
            }

            ++supportedCount;
            if (supportedCount == 1)
            {
                selected.gpu = gpu;
                selected.power = power;
                selected.uniqueId = uniqueId;
                selected.range = range;
                selected.current = current;
                gpu->vtable->Name(gpu, &selected.name);
                gpu->vtable->PnpString(gpu, &selected.pnpString);

                IAdlxManualPowerTuning1* power1 = nullptr;
                const auto queryDefaultResult = power->vtable->QueryInterface(
                    power,
                    L"IADLXManualPowerTuning1",
                    reinterpret_cast<void**>(&power1));
                if (IsSuccess(queryDefaultResult) && power1 != nullptr)
                {
                    AdlxInt defaultValue = 0;
                    if (IsSuccess(power1->vtable->GetPowerLimitDefault(power1, &defaultValue)))
                    {
                        selected.defaultValue = defaultValue;
                    }
                    power1->vtable->Release(power1);
                }
            }
            else
            {
                power->vtable->Release(power);
                gpu->vtable->Release(gpu);
            }
        }

        services->vtable->Release(services);
        gpus->vtable->Release(gpus);

        if (supportedCount == 0)
        {
            SetError(error, errorCapacity, requireIdentity
                ? "The previously selected AMD GPU is no longer available."
                : "No AMD GPU with ADLX manual power tuning support was found.");
            return requireIdentity ? BridgeGpuNotFound : BridgeNoSupportedGpu;
        }
        if (supportedCount > 1)
        {
            ReleaseSelected(selected);
            SetError(error, errorCapacity, "More than one AMD GPU exposes manual power tuning; automatic selection is unsafe.");
            return BridgeAmbiguousGpu;
        }
        return Ok;
    }
}

extern "C"
{
    struct ApsAdlxGpuInfo
    {
        int structureSize;
        int uniqueId;
        int currentOffsetPercent;
        int defaultOffsetPercent;
        int minimumOffsetPercent;
        int maximumOffsetPercent;
        int stepPercent;
        char name[128];
        char identity[256];
    };

    __declspec(dllexport) int __cdecl ApsAdlxGetSingleGpuInfo(
        ApsAdlxGpuInfo* info,
        char* error,
        int errorCapacity)
    {
        if (info == nullptr || info->structureSize != sizeof(ApsAdlxGpuInfo))
        {
            SetError(error, errorCapacity, "The AMD bridge received an invalid info structure.");
            return BridgeInvalidRange;
        }

        AdlxSession session;
        auto result = OpenSession(session, error, errorCapacity);
        if (result != Ok) return result;

        SelectedGpu selected;
        result = SelectGpu(session.system, 0, false, selected, error, errorCapacity);
        if (result != Ok) return result;

        info->uniqueId = selected.uniqueId;
        info->currentOffsetPercent = selected.current;
        info->defaultOffsetPercent = selected.defaultValue;
        info->minimumOffsetPercent = selected.range.minValue;
        info->maximumOffsetPercent = selected.range.maxValue;
        info->stepPercent = selected.range.step;
        strncpy_s(info->name, selected.name == nullptr ? "AMD Radeon GPU" : selected.name, _TRUNCATE);
        snprintf(
            info->identity,
            sizeof(info->identity),
            "%d|%s",
            selected.uniqueId,
            selected.pnpString == nullptr ? "AMD" : selected.pnpString);
        ReleaseSelected(selected);
        SetError(error, errorCapacity, "");
        return Ok;
    }

    __declspec(dllexport) int __cdecl ApsAdlxSetPowerLimitOffset(
        int uniqueId,
        int targetOffsetPercent,
        int* observedOffsetPercent,
        char* error,
        int errorCapacity)
    {
        if (observedOffsetPercent == nullptr)
        {
            SetError(error, errorCapacity, "The AMD bridge received an invalid read-back pointer.");
            return BridgeInvalidRange;
        }

        AdlxSession session;
        auto result = OpenSession(session, error, errorCapacity);
        if (result != Ok) return result;

        SelectedGpu selected;
        result = SelectGpu(session.system, uniqueId, true, selected, error, errorCapacity);
        if (result != Ok) return result;

        const auto delta = targetOffsetPercent - selected.range.minValue;
        if (targetOffsetPercent < selected.range.minValue ||
            targetOffsetPercent > selected.range.maxValue ||
            targetOffsetPercent > selected.defaultValue ||
            delta % selected.range.step != 0)
        {
            ReleaseSelected(selected);
            SetError(error, errorCapacity, "The AMD power-limit adjustment is outside the driver range, raises power, or misses the required step.");
            return BridgeInvalidRange;
        }

        result = selected.power->vtable->SetPowerLimit(selected.power, targetOffsetPercent);
        if (result == AdlxResetNeeded)
        {
            ReleaseSelected(selected);
            SetError(error, errorCapacity, "AMD automatic tuning is active. Disable it in AMD Software before using AFK Power Saver; other tuning will not be reset automatically.");
            return BridgeResetRequired;
        }
        if (!IsSuccess(result))
        {
            ReleaseSelected(selected);
            SetAdlxError(error, errorCapacity, "AMD power-limit write", result);
            return result;
        }

        AdlxInt observed = 0;
        result = selected.power->vtable->GetPowerLimit(selected.power, &observed);
        ReleaseSelected(selected);
        if (!IsSuccess(result))
        {
            SetAdlxError(error, errorCapacity, "AMD power-limit read-back", result);
            return result;
        }
        *observedOffsetPercent = observed;
        if (observed != targetOffsetPercent)
        {
            SetError(error, errorCapacity, "AMD ADLX did not read back the exact requested power-limit adjustment.");
            return BridgeReadbackMismatch;
        }

        SetError(error, errorCapacity, "");
        return Ok;
    }
}
