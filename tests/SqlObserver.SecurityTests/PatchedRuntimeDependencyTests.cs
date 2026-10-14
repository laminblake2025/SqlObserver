using System.Diagnostics;
using System.Reflection;

namespace SqlObserver.SecurityTests;

public sealed class PatchedRuntimeDependencyTests
{
    [Theory]
    [InlineData("Microsoft.AspNetCore.Authentication.Negotiate")]
    [InlineData("System.Security.Cryptography.Pkcs")]
    public void RuntimeBindingCannotSelectTheVulnerableFrameworkCopy(string assemblyName)
    {
        Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
        string productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion!;
        Assert.True(Version.TryParse(productVersion.Split('+')[0], out Version? version), productVersion);
        Assert.True(version >= new Version(10, 0, 13), $"{assemblyName} loaded {productVersion}");
    }
}
