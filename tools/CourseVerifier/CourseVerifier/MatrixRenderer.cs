using System.Text;

namespace LearningAzure.CourseVerifier;

/// <summary>
/// Renders the curriculum conformance matrix from the plan so the published
/// document can never disagree with the graph the verifier checks.
/// </summary>
internal static class MatrixRenderer
{
    internal static string Render(CurriculumPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();
        builder.AppendLine("# 🧭 Curriculum evidence matrix");
        builder.AppendLine();
        builder.AppendLine("<!--");
        builder.AppendLine("  GENERATED FILE — do not edit by hand.");
        builder.AppendLine("  Source: docs/architecture/curriculum.json");
        builder.AppendLine("  Regenerate: dotnet run --project tools/CourseVerifier/CourseVerifier -- matrix --write");
        builder.AppendLine("-->");
        builder.AppendLine();
        builder.AppendLine("Every promised outcome is classified against the quality contract's coverage");
        builder.AppendLine("progression — **named → explained → demonstrated → practiced → applied**.");
        builder.AppendLine();
        builder.AppendLine("| status | meaning |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine("| `covered` | the cited artifact exists and the verifier resolved it |");
        builder.AppendLine("| `planned` | the artifact does not exist yet; the unit is not trackable |");
        builder.AppendLine("| `deferred` | satisfied by a later unit that is not built yet |");
        builder.AppendLine("| `not-applicable` | the stage does not apply, with a recorded rationale |");
        builder.AppendLine();
        builder.AppendLine("A unit is registered for Learning Mentor tracking only once every stage has left");
        builder.AppendLine("`planned`, so this matrix cannot claim coverage before content exists.");
        builder.AppendLine();

        AppendSummary(builder, plan);
        AppendRoles(builder, plan);

        foreach (var unit in plan.Units.OrderBy(unit => unit.Sequence))
        {
            AppendUnit(builder, unit);
        }

        return builder.ToString();
    }

    private static void AppendSummary(StringBuilder builder, CurriculumPlan plan)
    {
        var counts = plan.Units
            .SelectMany(unit => unit.Evidence)
            .GroupBy(record => record.Status, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine("| measure | value |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| units | {CourseVerification.FormatCount(plan.Units.Count)} |");
        builder.AppendLine($"| modules | {CourseVerification.FormatCount(plan.Units.Count(unit => unit.Kind == UnitKinds.Module))} |");
        builder.AppendLine($"| applied projects | {CourseVerification.FormatCount(plan.Units.Count(unit => unit.Kind == UnitKinds.Project))} |");
        builder.AppendLine($"| capstones | {CourseVerification.FormatCount(plan.Units.Count(unit => unit.Kind == UnitKinds.Capstone))} |");
        builder.AppendLine($"| declared outcomes | {CourseVerification.FormatCount(plan.Units.Sum(unit => unit.Outcomes.Count))} |");
        builder.AppendLine($"| milestones | {CourseVerification.FormatCount(plan.Units.Sum(unit => unit.Milestones.Count))} |");
        builder.AppendLine($"| units with artifacts present | {CourseVerification.FormatCount(plan.Units.Count(unit => unit.ArtifactStatus == ArtifactStatuses.Present))} |");

        foreach (var status in EvidenceStatuses.All)
        {
            var count = counts.TryGetValue(status, out var value) ? value : 0;
            builder.AppendLine($"| evidence records `{status}` | {CourseVerification.FormatCount(count)} |");
        }

        builder.AppendLine();
    }

    private static void AppendRoles(StringBuilder builder, CurriculumPlan plan)
    {
        builder.AppendLine("## Repository roles");
        builder.AppendLine();
        builder.AppendLine("| role | path | status |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var role in plan.Roles)
        {
            builder.AppendLine($"| `{role.Role}` | `{role.Path}` | `{role.Status}` |");
        }

        builder.AppendLine();
    }

    private static void AppendUnit(StringBuilder builder, CurriculumUnit unit)
    {
        builder.AppendLine($"## `{unit.Id}` — {unit.Title}");
        builder.AppendLine();
        builder.AppendLine($"- **kind:** {unit.Kind}, sequence {CourseVerification.FormatCount(unit.Sequence)}");
        builder.AppendLine($"- **artifacts:** `{unit.ArtifactStatus}`");
        builder.AppendLine($"- **environments:** {string.Join(", ", unit.Environments)}");
        builder.AppendLine($"- **prerequisites:** {FormatList(unit.Prerequisites)}");
        builder.AppendLine();
        builder.AppendLine("| outcome | statement | measured by |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var outcome in unit.Outcomes)
        {
            builder.AppendLine($"| `{outcome.Id}` | {outcome.Statement} | `{outcome.MeasuredBy}` |");
        }

        builder.AppendLine();
        builder.AppendLine("| stage | status | evidence | note |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var record in unit.Evidence)
        {
            var artifact = record.Artifact switch
            {
                null => "—",
                { Role: { } role, Anchor: { } anchor } => $"`{role}#{anchor}`",
                { Role: { } role } => $"`{role}`",
                { Path: { } path, Anchor: { } anchor } => $"`{path}#{anchor}`",
                { Path: { } path } => $"`{path}`",
                _ => "—",
            };
            var note = record.Note ?? record.Rationale ?? string.Empty;
            builder.AppendLine($"| {record.Stage} | `{record.Status}` | {artifact} | {note} |");
        }

        builder.AppendLine();

        if (unit.Milestones.Count > 0)
        {
            builder.AppendLine("| milestone | required outcome | depends on |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (var milestone in unit.Milestones.OrderBy(milestone => milestone.Ordinal))
            {
                builder.AppendLine($"| `{milestone.Id}` | {milestone.RequiredOutcome} | {FormatList(milestone.Prerequisites)} |");
            }

            builder.AppendLine();
        }
    }

    private static string FormatList(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values.Select(value => $"`{value}`"));
}
