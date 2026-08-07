using System.Text.Json;

namespace LearningAzure.CourseVerifier;

internal sealed class PlanLoadException(string message) : Exception(message);

internal static class PlanLoader
{
    /// <summary>Reads the curriculum plan, rejecting unknown fields so a typo can never be ignored.</summary>
    internal static CurriculumPlan Load(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            throw new PlanLoadException($"curriculum plan not found at {absolutePath}");
        }

        try
        {
            using var stream = File.OpenRead(absolutePath);
            return JsonSerializer.Deserialize(stream, PlanSerializerContext.Default.CurriculumPlan)
                   ?? throw new PlanLoadException("curriculum plan is empty");
        }
        catch (JsonException error)
        {
            throw new PlanLoadException($"curriculum plan is not valid JSON: {error.Message}");
        }
    }
}
