using System.ComponentModel;

namespace SqlObserver.TestSetup;

internal enum SetupNativeOperation
{
    OpenSCManager, OpenService, QueryServiceStatusEx, OpenProcess, OpenProcessToken,
    GetTokenUser, GetTokenGroups, OpenInspectorToken, LookupDebugPrivilege,
    EnableDebugPrivilege, RestoreDebugPrivilege
}

// Only a fixed operation ID and native numeric code are retained in setup diagnostics.
internal sealed class SetupWin32Exception(int code, SetupNativeOperation operation) : Win32Exception(code)
{
    internal SetupNativeOperation Operation { get; } = operation;
}
