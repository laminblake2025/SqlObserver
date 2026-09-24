using System.Text.Json;
using Json.Schema;

namespace SqlObserver.McpContractTests;

internal static class JsonSchemaAssertions
{
    internal static void AssertValid(JsonElement instance, JsonElement schema, string context)
    {
        EvaluationResults result = Evaluate(instance, schema);
        Assert.True(result.IsValid, $"{context}: {JsonSerializer.Serialize(result)}");
    }

    internal static EvaluationResults Evaluate(JsonElement instance, JsonElement schema) =>
        JsonSchema.Build(schema).Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
}
