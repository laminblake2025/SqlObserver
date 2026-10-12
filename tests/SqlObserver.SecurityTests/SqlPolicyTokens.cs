namespace SqlObserver.SecurityTests;

internal static class SqlPolicyTokens
{
    internal static string WithoutQuotedTextAndComments(string source)
        => SqlObserver.Tools.SqlPolicyTokens.WithoutQuotedTextAndComments(source);
}
