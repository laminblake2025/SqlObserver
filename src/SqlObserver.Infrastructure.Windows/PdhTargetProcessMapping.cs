// This source is also embedded verbatim in the Windows PowerShell 5.1 native helper.
// Keep it C# 5 compatible so managed tests exercise the native selection policy.
namespace SqlObserver.Infrastructure.Windows
{
    internal enum PdhTargetProcessMappingResult { Missing, Ambiguous, Selected }

    internal static class PdhTargetProcessMapping
    {
        internal static PdhTargetProcessMappingResult Select(string[] idNames, long[] processIds,
            uint[] idStatuses, string[] cpuNames, int processId, out int idIndex, out int cpuIndex)
        {
            idIndex = -1;
            cpuIndex = -1;
            if (idNames.Length != processIds.Length || idNames.Length != idStatuses.Length ||
                idNames.Length > 1024 || cpuNames.Length > 1024)
                throw new System.ArgumentException("Process mapping arrays exceed their bounds or have different lengths.");
            if (processId <= 0) return PdhTargetProcessMappingResult.Missing;

            int selectedId = -1;
            for (int i = 0; i < processIds.Length; i++)
            {
                if (processIds[i] != processId || (idStatuses[i] != 0 && idStatuses[i] != 1)) continue;
                if (selectedId >= 0) return PdhTargetProcessMappingResult.Ambiguous;
                selectedId = i;
            }
            if (selectedId < 0) return PdhTargetProcessMappingResult.Missing;

            // Count every occurrence, including invalid ID rows. Never pick a first
            // duplicate, nor correlate the independent arrays by their row order.
            string name = idNames[selectedId];
            if (string.IsNullOrEmpty(name)) return PdhTargetProcessMappingResult.Missing;
            int idMatches = 0, cpuMatches = 0, selectedCpu = -1;
            for (int i = 0; i < idNames.Length; i++)
                if (string.Equals(idNames[i], name, System.StringComparison.Ordinal)) idMatches++;
            for (int i = 0; i < cpuNames.Length; i++)
            {
                if (!string.Equals(cpuNames[i], name, System.StringComparison.Ordinal)) continue;
                cpuMatches++;
                selectedCpu = i;
            }
            if (idMatches > 1 || cpuMatches > 1) return PdhTargetProcessMappingResult.Ambiguous;
            if (cpuMatches == 0) return PdhTargetProcessMappingResult.Missing;
            idIndex = selectedId;
            cpuIndex = selectedCpu;
            return PdhTargetProcessMappingResult.Selected;
        }
    }
}
