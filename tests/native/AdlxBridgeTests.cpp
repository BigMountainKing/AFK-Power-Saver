// Exercise the actual selection/default-query path through fake ADLX interfaces.
#include "../../native/AFKPowerSaver.AmdAdlx.Native/AmdAdlxBridge.cpp"

int main()
{
    static int mode = 0;
    static IAdlxGpuVtable gpuTable{};
    static IAdlxGpu gpu{ &gpuTable };
    gpuTable.Release = [](IAdlxGpu*) -> AdlxLong { return 1; };
    gpuTable.VendorId = [](IAdlxGpu*, const char** value) -> AdlxResult { *value = "1002"; return Ok; };
    gpuTable.UniqueId = [](IAdlxGpu*, AdlxInt* value) -> AdlxResult { *value = 42; return Ok; };
    gpuTable.Name = [](IAdlxGpu*, const char** value) -> AdlxResult { *value = "Fake Radeon"; return Ok; };
    gpuTable.PnpString = [](IAdlxGpu*, const char** value) -> AdlxResult { *value = "fake-gpu"; return Ok; };
    static IAdlxManualPowerTuning1Vtable power1Table{};
    static IAdlxManualPowerTuning1 power1{ &power1Table };
    power1Table.Release = [](IAdlxManualPowerTuning1*) -> AdlxLong { return 1; };
    power1Table.GetPowerLimitDefault = [](IAdlxManualPowerTuning1*, AdlxInt* value) -> AdlxResult
    {
        *value = mode == 3 ? 100 : mode == 4 ? 1 : 0;
        return mode == 2 ? -1 : Ok;
    };
    static IAdlxManualPowerTuningVtable powerTable{};
    static IAdlxManualPowerTuning power{ &powerTable };
    powerTable.Release = [](IAdlxManualPowerTuning*) -> AdlxLong { return 1; };
    powerTable.QueryInterface = [](IAdlxManualPowerTuning*, const wchar_t* id, void** value) -> AdlxResult
    {
        if (wcscmp(id, L"IADLXManualPowerTuning1") == 0)
        {
            *value = mode == 1 ? nullptr : &power1;
            return mode == 1 ? -1 : Ok;
        }
        *value = &power;
        return Ok;
    };
    powerTable.GetPowerLimitRange = [](IAdlxManualPowerTuning*, AdlxIntRange* range) -> AdlxResult
    { *range = { -10, 10, 2 }; return Ok; };
    powerTable.GetPowerLimit = [](IAdlxManualPowerTuning*, AdlxInt* value) -> AdlxResult { *value = 0; return Ok; };
    static IAdlxGpuListVtable listTable{};
    static IAdlxGpuList list{ &listTable };
    listTable.Release = [](IAdlxGpuList*) -> AdlxLong { return 1; };
    listTable.Size = [](IAdlxGpuList*) -> AdlxUInt { return 1; };
    listTable.AtGpu = [](IAdlxGpuList*, AdlxUInt, IAdlxGpu** value) -> AdlxResult { *value = &gpu; return Ok; };
    static IAdlxGpuTuningServicesVtable servicesTable{};
    static IAdlxGpuTuningServices services{ &servicesTable };
    servicesTable.Release = [](IAdlxGpuTuningServices*) -> AdlxLong { return 1; };
    servicesTable.IsSupportedManualPowerTuning = [](IAdlxGpuTuningServices*, IAdlxGpu*, AdlxBool* value) -> AdlxResult
    { *value = 1; return Ok; };
    servicesTable.GetManualPowerTuning = [](IAdlxGpuTuningServices*, IAdlxGpu*, IAdlxInterface** value) -> AdlxResult
    { *value = reinterpret_cast<IAdlxInterface*>(&power); return Ok; };
    IAdlxSystemVtable systemTable{};
    IAdlxSystem system{ &systemTable };
    systemTable.GetGpus = [](IAdlxSystem*, IAdlxGpuList** value) -> AdlxResult { *value = &list; return Ok; };
    systemTable.GetGpuTuningServices = [](IAdlxSystem*, IAdlxGpuTuningServices** value) -> AdlxResult { *value = &services; return Ok; };
    for (mode = 0; mode < 5; ++mode)
    {
        SelectedGpu selected;
        char error[512]{};
        const auto result = SelectGpu(&system, 0, false, selected, error, sizeof(error));
        const bool expected = mode == 0 ? result == Ok && selected.defaultValue == 0 : result == BridgeInvalidRange;
        ReleaseSelected(selected);
        if (!expected) { printf("FAIL: AMD default contract mode %d\n", mode); return 1; }
    }
    puts("5/5 native AMD default-query contract tests passed (fake interfaces; no driver loaded).");
    return 0;
}
