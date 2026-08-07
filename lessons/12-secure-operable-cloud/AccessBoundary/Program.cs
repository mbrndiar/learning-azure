using System.Globalization;
using Azure.Identity;

namespace LearningAzure.Lessons.SecureOperableCloud;

/// <summary>
/// Shows where the boundary between "signed in" and "authorized" actually sits:
/// which identity a process becomes on four different hosts, which role that
/// identity needs at which scope, what a refusal looks like when the role is
/// wrong, and what the same architecture costs after everyone has gone home.
/// </summary>
/// <remarks>
/// This companion touches no network. Every identity, endpoint, and scope in
/// here is a value, so the output is byte-identical on a laptop with an
/// <c>az login</c> session and on a build agent with nothing configured. That
/// is deliberate: the point of this module is the reasoning, and reasoning you
/// can only see when you are signed in is reasoning you cannot practise.
/// </remarks>
internal static class Program
{
    private const string Subscription = "/subscriptions/00000000-0000-0000-0000-000000000000";
    private const string Group = Subscription + "/resourceGroups/rg-expedition-checkpoint";
    private const string Storage = Group + "/providers/Microsoft.Storage/storageAccounts/stexpedition9f2a1c";
    private const string Reports = Storage + "/blobServices/default/containers/reports";
    private const string Hub = Group + "/providers/Microsoft.EventHub/namespaces/ehns-expedition/eventhubs/telemetry";
    private const string Cosmos = Group + "/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-expedition";

    private static int Main()
    {
        Section("1. The chain is an order, not a negotiation");
        PrintChain();

        Section("2. The same binary, four hosts");
        PrintHosts();

        Section("3. The grant plan for the live checkpoint");
        PrintGrantPlan();

        Section("4. Owner is not a data role");
        PrintOwnerDenial();

        Section("5. Names and tags are teardown handles");
        PrintNamesAndTags();

        Section("6. The bill has two numbers");
        PrintCost();

        Section("7. Teardown, and what survives it");
        PrintTeardown();

        Section("8. What this companion cannot tell you");
        PrintLiveBoundary();

        return 0;
    }

    private static void PrintChain()
    {
        // The names come from the assembly rather than from a list in a
        // comment, so a chain that changes under you shows up as a compile
        // error instead of as a paragraph that quietly became false.
        var chain = new (string Type, string Signal, string Kind)[]
        {
            (nameof(EnvironmentCredential), "AZURE_CLIENT_ID + secret/certificate", "deployment"),
            (nameof(WorkloadIdentityCredential), "federated token file (AKS)", "deployment"),
            (nameof(ManagedIdentityCredential), "IMDS endpoint on the host", "deployment"),
            (nameof(VisualStudioCredential), "signed-in Visual Studio account", "developer"),
            (nameof(VisualStudioCodeCredential), "Azure Resources extension sign-in", "developer"),
            (nameof(AzureCliCredential), "az login", "developer"),
            (nameof(AzurePowerShellCredential), "Connect-AzAccount", "developer"),
            (nameof(AzureDeveloperCliCredential), "azd auth login", "developer"),
        };

        Console.WriteLine("DefaultAzureCredential's base chain tries these in order and stops at the first");
        Console.WriteLine("one that produces a token. BrokerCredential may follow when broker support is");
        Console.WriteLine("installed and configured.");
        Console.WriteLine("InteractiveBrowserCredential is in the family but excluded");
        Console.WriteLine("by default: a server that opens a browser is a server that hangs.");
        Console.WriteLine();
        Console.WriteLine("  #  credential                          kind        signal it looks for");
        Console.WriteLine("  -  ----------------------------------  ----------  --------------------------------");
        for (var index = 0; index < chain.Length; index++)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {index + 1}  {chain[index].Type,-34}  {chain[index].Kind,-10}  {chain[index].Signal}"));
        }

        Console.WriteLine();
        Console.WriteLine("Every deployment source sits above every developer tool. That ordering is the");
        Console.WriteLine("only reason a host with a managed identity never runs as whoever last signed in");
        Console.WriteLine("on it.");
    }

    private static void PrintHosts()
    {
        var hosts = new (string Name, string Winner, string Shadowed)[]
        {
            ("your laptop", nameof(AzureCliCredential), "AzureDeveloperCliCredential"),
            ("GitHub Actions after azure/login OIDC", nameof(AzureCliCredential), "-"),
            ("App Service + system-assigned identity", nameof(ManagedIdentityCredential), "-"),
            ("AKS pod with workload identity", nameof(WorkloadIdentityCredential), "ManagedIdentityCredential"),
        };

        Console.WriteLine("  host                                    resolves to                     also configured");
        Console.WriteLine("  --------------------------------------  -----------------------------  --------------------------");
        foreach (var (name, winner, shadowed) in hosts)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {name,-38}  {winner,-29}  {shadowed}"));
        }

        Console.WriteLine();
        Console.WriteLine("The last row is the one worth staring at. The node has an identity and the pod");
        Console.WriteLine("has one; the chain picks the pod's, and the node's is one deployment change away");
        Console.WriteLine("from becoming the answer instead. That is why production code pins a single");
        var productionCredential = new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId("00000000-0000-0000-0000-000000000001"));
        Console.WriteLine(
            $"credential: `{productionCredential.GetType().Name}(ManagedIdentityId)` cannot resolve to anything");
        Console.WriteLine("else, and it fails loudly on a laptop, which is the correct behaviour.");
    }

    private static void PrintGrantPlan()
    {
        var plan = new (string Work, string Role, string System, string Scope)[]
        {
            ("read expedition reports", "Storage Blob Data Reader", "Azure RBAC", Reports),
            ("write expedition reports", "Storage Blob Data Contributor", "Azure RBAC", Reports),
            ("publish telemetry", "Azure Event Hubs Data Sender", "Azure RBAC", Hub),
            ("consume telemetry", "Azure Event Hubs Data Receiver", "Azure RBAC", Hub),
            ("write journal documents", "Cosmos DB Built-in Data Contributor", "Cosmos data plane", Cosmos),
        };

        foreach (var (work, role, system, scope) in plan)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {work}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    role   {role}  ({system})"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    scope  {Short(scope)}"));
        }

        Console.WriteLine();
        Console.WriteLine("The last row is assigned with a different command from the four above it:");
        Console.WriteLine("  az cosmosdb sql role assignment create ...   (Cosmos data plane)");
        Console.WriteLine("  az role assignment create ...                (everything else)");
        Console.WriteLine("Using the second command for a Cosmos data role produces an assignment that");
        Console.WriteLine("exists, reads correctly in the portal, and grants nothing.");
    }

    private static void PrintOwnerDenial()
    {
        Console.WriteLine("  identity holds : Owner at " + Short(Subscription));
        Console.WriteLine("  identity wants : read a blob in " + Short(Reports));
        Console.WriteLine();
        Console.WriteLine("  result         : denied");
        Console.WriteLine("  storage        : 403 AuthorizationPermissionMismatch");
        Console.WriteLine("  event hubs     : the AMQP link is refused; there is no HTTP status to read");
        Console.WriteLine("  cosmos         : 403 with a substatus, and no separate error-code field");
        Console.WriteLine();
        Console.WriteLine("Owner, Contributor, and Storage Account Contributor are control-plane roles.");
        Console.WriteLine("They can delete the account and rotate its keys; they carry no data action at");
        Console.WriteLine("all. Reading the bytes needs a data role, and the two hierarchies only meet in");
        Console.WriteLine("the portal's left-hand menu.");
    }

    private static void PrintNamesAndTags()
    {
        const string runId = "9f2a1c";
        var names = new (string Service, string Composed, string Rule)[]
        {
            ("storage account", "stexpedition" + runId, "3-24, lower-case letters and digits, global"),
            ("event hubs namespace", "ehns-expedition-" + runId, "6-50, starts with a letter, global"),
            ("cosmos account", "cosmos-expedition-" + runId, "3-44, lower-case, digits, hyphens, global"),
            ("resource group", "rg-expedition-checkpoint", "1-90, not globally unique"),
        };

        Console.WriteLine("  resource               name                        rule");
        Console.WriteLine("  ---------------------  --------------------------  -----------------------------------------");
        foreach (var (service, composed, rule) in names)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {service,-21}  {composed,-26}  {rule}"));
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  The run id '{runId}' is the part that keeps two people in one subscription apart."));
        Console.WriteLine("  When a name is too long, cut the prefix. Cutting the tail is how two runs end");
        Console.WriteLine("  up asking for the same globally unique name, and the failure arrives as a 409");
        Console.WriteLine("  on somebody else's resource.");
        Console.WriteLine();
        Console.WriteLine("  Every resource carries the same four tags:");
        Console.WriteLine("    owner=field-team  managed-by=learning-azure");
        Console.WriteLine("    purpose=module-12-checkpoint  expires-on=2026-12-31");
        Console.WriteLine("  managed-by is not decoration: the teardown refuses to delete a group without");
        Console.WriteLine("  it, which is what stops a wrong RESOURCE_GROUP from becoming an incident.");
    }

    private static void PrintCost()
    {
        var resources = new (string Name, string Shape, decimal PerHour)[]
        {
            ("Cosmos DB, 400 RU/s provisioned", "provisioned", 0.032m),
            ("Event Hubs namespace, Basic", "provisioned", 0.015m),
            ("Blob storage, ~1 GiB", "storage", 0.00003m),
            ("Storage + Cosmos requests", "consumption", 0.004m),
            ("Log Analytics ingestion", "consumption", 0.010m),
        };

        var runHours = 1.5m;
        var runCost = resources.Sum(resource => resource.PerHour * runHours);
        var idlePerDay = resources
            .Where(resource => resource.Shape != "consumption")
            .Sum(resource => resource.PerHour) * 24m;

        Console.WriteLine("  resource                          shape        USD/hour");
        Console.WriteLine("  --------------------------------  -----------  --------");
        foreach (var (name, shape, perHour) in resources)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {name,-32}  {shape,-11}  {perHour,8:F5}"));
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  a {runHours:F1} hour checkpoint    ~ ${runCost:F3}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  the same thing, forgotten  ~ ${idlePerDay:F2} per day, ~ ${idlePerDay * 30m:F2} per month"));
        Console.WriteLine();
        Console.WriteLine("  Consumption lines fall to zero the moment nobody calls anything. Provisioned");
        Console.WriteLine("  throughput and stored bytes do not: they are billed for existing. The second");
        Console.WriteLine("  number is what a missing `az group delete` actually costs, and it is roughly");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {idlePerDay * 30m / runCost:F0}x the run itself."));
    }

    private static void PrintTeardown()
    {
        var decisions = new (string Situation, string Decision)[]
        {
            ("scope is a subscription", "refuse - a teardown deletes a group, never anything above one"),
            ("group has no tags", "refuse - nothing proves this run created it"),
            ("managed-by is 'terraform'", "refuse - somebody else's automation owns it"),
            ("owner is another person", "refuse - not this run's to delete"),
            ("group holds foreign resources", "delete only the tagged resources"),
            ("group is entirely this run's", "delete the resource group"),
        };

        Console.WriteLine("  what the platform reports          what the teardown does");
        Console.WriteLine("  ---------------------------------  ----------------------------------------------------");
        foreach (var (situation, decision) in decisions)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {situation,-33}  {decision}"));
        }

        Console.WriteLine();
        Console.WriteLine("  Then verify, because \"deleted\" is a state and not an absence:");
        Console.WriteLine("    - `az group delete` waits by default; `--no-wait` makes it asynchronous");
        Console.WriteLine("    - a deleted storage account is recoverable for 14 days, and creating a new");
        Console.WriteLine("      account with the same name silently forfeits that recovery");
        Console.WriteLine("    - a deleted Log Analytics workspace keeps its data, and its name, for 14 days");
        Console.WriteLine("    - a deleted key vault is soft-deleted for 7-90 days, and purge protection");
        Console.WriteLine("      means it cannot be purged early at all");
        Console.WriteLine("    - role assignments whose principal was deleted survive as 'Identity not");
        Console.WriteLine("      found' entries at a scope that still exists");
    }

    private static void PrintLiveBoundary()
    {
        Console.WriteLine("  Nothing above touched Azure, so nothing above can prove:");
        Console.WriteLine("    - that a role assignment takes effect (documented as up to 10 minutes)");
        Console.WriteLine("    - what your subscription's policy assignments will refuse to create");
        Console.WriteLine("    - what a 403 looks like in your own terminal, with your own principal id");
        Console.WriteLine("    - whether a name you like is still free in the global namespace");
        Console.WriteLine("    - what Cost Management reports, which lags and needs a settled subscription");
        Console.WriteLine();
        Console.WriteLine("  Those five are exactly what the management labs are for:");
        Console.WriteLine("    infra/azure-cli/secure-operable-cloud.sh");
        Console.WriteLine("    infra/powershell/secure-operable-cloud.ps1");
    }

    private static string Short(string scope)
    {
        // Full resource ids are 150 characters of subscription guid nobody
        // reads. The tail is where the meaning is.
        var segments = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 2
            ? "/" + string.Join('/', segments)
            : ".../" + string.Join('/', segments[2..]);
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('=', title.Length));
        Console.WriteLine();
    }
}
