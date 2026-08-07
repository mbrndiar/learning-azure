using LearningAzure.Exercises.SecureOperableCloud;

namespace LearningAzure.Exercises.SecureOperableCloud.Tests;

/// <summary>
/// Checks that an intent maps to the least-privilege role that satisfies it,
/// that a grant lands at the narrowest scope covering the work, and that an
/// evaluation refuses everything a real service would refuse.
/// </summary>
public sealed class RoleCatalogTests
{
    [Theory]
    [InlineData(AzureService.BlobStorage, AccessIntent.Read, "Storage Blob Data Reader")]
    [InlineData(AzureService.BlobStorage, AccessIntent.Write, "Storage Blob Data Contributor")]
    [InlineData(AzureService.BlobStorage, AccessIntent.Administer, "Storage Blob Data Owner")]
    [InlineData(AzureService.QueueStorage, AccessIntent.Read, "Storage Queue Data Reader")]
    [InlineData(AzureService.QueueStorage, AccessIntent.SendMessages, "Storage Queue Data Message Sender")]
    [InlineData(AzureService.QueueStorage, AccessIntent.ProcessMessages, "Storage Queue Data Message Processor")]
    [InlineData(AzureService.QueueStorage, AccessIntent.Administer, "Storage Queue Data Contributor")]
    [InlineData(AzureService.TableStorage, AccessIntent.Read, "Storage Table Data Reader")]
    [InlineData(AzureService.TableStorage, AccessIntent.Write, "Storage Table Data Contributor")]
    [InlineData(AzureService.EventHubs, AccessIntent.SendMessages, "Azure Event Hubs Data Sender")]
    [InlineData(AzureService.EventHubs, AccessIntent.ProcessMessages, "Azure Event Hubs Data Receiver")]
    [InlineData(AzureService.EventHubs, AccessIntent.Administer, "Azure Event Hubs Data Owner")]
    [InlineData(AzureService.CosmosNoSql, AccessIntent.Read, "Cosmos DB Built-in Data Reader")]
    [InlineData(AzureService.CosmosNoSql, AccessIntent.Write, "Cosmos DB Built-in Data Contributor")]
    public void RoleFor_NamesTheLeastPrivilegeRole(AzureService service, AccessIntent intent, string expected)
    {
        Assert.Equal(expected, RoleCatalog.RoleFor(service, intent).RoleName);
    }

    [Fact]
    public void RoleFor_KeepsSendingAndProcessingApartOnAQueue()
    {
        // A producer that can also delete other processors' messages is not a
        // producer with a convenient role; it is a producer that can lose work.
        var sender = RoleCatalog.RoleFor(AzureService.QueueStorage, AccessIntent.SendMessages);
        var processor = RoleCatalog.RoleFor(AzureService.QueueStorage, AccessIntent.ProcessMessages);

        Assert.NotEqual(sender.RoleName, processor.RoleName);
    }

    [Fact]
    public void RoleFor_DoesNotGiveAProducerTheRightToDeleteMessages()
    {
        // Queue Data Contributor is the role that gets handed out when nobody
        // reads the list carefully, and it lets a producer dequeue and delete
        // work that another consumer was in the middle of.
        var sender = RoleCatalog.RoleFor(AzureService.QueueStorage, AccessIntent.SendMessages);
        var processor = RoleCatalog.RoleFor(AzureService.QueueStorage, AccessIntent.ProcessMessages);

        Assert.False(RoleCatalog.Satisfies(sender.RoleName, processor.RoleName));
    }

    [Fact]
    public void RoleFor_DoesNotGiveAReaderAWriteRole()
    {
        Assert.NotEqual(
            RoleCatalog.RoleFor(AzureService.BlobStorage, AccessIntent.Write).RoleName,
            RoleCatalog.RoleFor(AzureService.BlobStorage, AccessIntent.Read).RoleName);
    }

    [Theory]
    [InlineData(AzureService.BlobStorage)]
    [InlineData(AzureService.QueueStorage)]
    [InlineData(AzureService.TableStorage)]
    [InlineData(AzureService.EventHubs)]
    public void RoleFor_PutsStorageAndEventHubsRolesInAzureRbac(AzureService service)
    {
        Assert.Equal(RoleSystem.AzureRbac, RoleCatalog.RoleFor(service, AccessIntent.Read).System);
    }

    [Theory]
    [InlineData(AccessIntent.Read)]
    [InlineData(AccessIntent.Write)]
    public void RoleFor_PutsCosmosDataRolesInTheirOwnSystem(AccessIntent intent)
    {
        // Assigning these with `az role assignment create` creates something
        // that exists and grants nothing.
        Assert.Equal(RoleSystem.CosmosDataPlane, RoleCatalog.RoleFor(AzureService.CosmosNoSql, intent).System);
    }

    [Theory]
    [InlineData(AzureService.BlobStorage, AccessIntent.SendMessages)]
    [InlineData(AzureService.BlobStorage, AccessIntent.ProcessMessages)]
    [InlineData(AzureService.TableStorage, AccessIntent.SendMessages)]
    [InlineData(AzureService.CosmosNoSql, AccessIntent.Administer)]
    [InlineData(AzureService.CosmosNoSql, AccessIntent.SendMessages)]
    public void RoleFor_RefusesAnIntentTheServiceHasNoRoleFor(AzureService service, AccessIntent intent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleCatalog.RoleFor(service, intent));
    }

    [Fact]
    public void DenialFor_GivesStorageItsDocumentedErrorCode()
    {
        var denial = RoleCatalog.DenialFor(AzureService.BlobStorage);

        Assert.Equal(403, denial.HttpStatus);
        Assert.Equal("AuthorizationPermissionMismatch", denial.ErrorCode);
    }

    [Fact]
    public void DenialFor_DoesNotInventAnErrorCodeForEventHubs()
    {
        // Event Hubs refuses the AMQP link. There is no HTTP status to read,
        // and teaching one would send the learner looking for a string that
        // does not exist.
        var denial = RoleCatalog.DenialFor(AzureService.EventHubs);

        Assert.Null(denial.HttpStatus);
        Assert.Null(denial.ErrorCode);
    }

    [Fact]
    public void NarrowestScope_StopsAtASingleContainerWhenThatIsAllTheWorkTouches()
    {
        var scope = RoleCatalog.NarrowestScope([Fixtures.ReportsContainer]);

        Assert.Equal(ScopeLevel.SubResource, scope.Level);
        Assert.Equal(Fixtures.ReportsContainer.Path, scope.Path);
    }

    [Fact]
    public void NarrowestScope_ClimbsToTheAccountForTwoContainersInIt()
    {
        var scope = RoleCatalog.NarrowestScope([Fixtures.ReportsContainer, Fixtures.CheckpointsContainer]);

        Assert.Equal(ScopeLevel.Resource, scope.Level);
        Assert.Equal(Fixtures.StorageAccount.Path, scope.Path);
    }

    [Fact]
    public void NarrowestScope_ClimbsNoFurtherThanItMust()
    {
        // A container and a queue in the same account share the account, not
        // the resource group. An implementation that jumps to the group grants
        // access to the Event Hubs namespace as well.
        var scope = RoleCatalog.NarrowestScope([Fixtures.ReportsContainer, Fixtures.WorkQueue]);

        Assert.Equal(Fixtures.StorageAccount.Path, scope.Path);
    }

    [Fact]
    public void NarrowestScope_FallsBackToTheResourceGroupAcrossServices()
    {
        var scope = RoleCatalog.NarrowestScope([Fixtures.StorageAccount, Fixtures.EventHubsNamespace]);

        Assert.Equal(ScopeLevel.ResourceGroup, scope.Level);
        Assert.Equal(Fixtures.ResourceGroup.Path, scope.Path);
    }

    [Fact]
    public void NarrowestScope_NeverReturnsAPathThatIsNotAScope()
    {
        // The common prefix of two resources in one group runs into
        // ".../providers/Microsoft.Storage" and ".../providers/Microsoft.EventHub",
        // which stops inside a provider identifier. A scope may not be built
        // from a prefix that stops there.
        var scope = RoleCatalog.NarrowestScope([Fixtures.StorageAccount, Fixtures.EventHubsNamespace]);

        Assert.NotNull(ResourceScope.LevelFor(scope.Segments));
    }

    [Fact]
    public void NarrowestScope_DoesNotStopOnTheBlobServiceWrapper()
    {
        // ".../storageAccounts/x/blobServices/default" is a longer common
        // prefix than the account and looks like a scope if you only count
        // segments. Azure will not assign a role there.
        var scope = RoleCatalog.NarrowestScope([Fixtures.ReportsContainer, Fixtures.CheckpointsContainer]);

        Assert.DoesNotContain("blobServices", scope.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceScope_RefusesToParseAServiceWrapperAsAScope()
    {
        Assert.Throws<ArgumentException>(() =>
            ResourceScope.Parse(Fixtures.StorageAccount.Path + "/blobServices/default"));
    }

    [Fact]
    public void NarrowestScope_KeepsTheHubWhenBothTargetsAreTheSameHub()
    {
        var scope = RoleCatalog.NarrowestScope([Fixtures.TelemetryHub, Fixtures.TelemetryHub]);

        Assert.Equal(Fixtures.TelemetryHub.Path, scope.Path);
    }

    [Fact]
    public void NarrowestScope_RefusesTargetsInDifferentSubscriptions()
    {
        Assert.Throws<ArgumentException>(() =>
            RoleCatalog.NarrowestScope([Fixtures.ResourceGroup, Fixtures.ForeignSubscriptionGroup]));
    }

    [Fact]
    public void NarrowestScope_RefusesAnEmptySet()
    {
        Assert.Throws<ArgumentException>(() => RoleCatalog.NarrowestScope([]));
    }

    [Fact]
    public void Evaluate_AllowsTheExactRoleAtTheExactScope()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Read, Fixtures.ReportsContainer),
            [new RoleAssignment("Storage Blob Data Reader", Fixtures.ReportsContainer, RoleSystem.AzureRbac)]);

        Assert.True(outcome.Allowed);
        Assert.Null(outcome.MissingRole);
        Assert.Null(outcome.Denial);
    }

    [Fact]
    public void Evaluate_AllowsAnAssignmentMadeFurtherUpTheTree()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Read, Fixtures.ReportsContainer),
            [new RoleAssignment("Storage Blob Data Reader", Fixtures.ResourceGroup, RoleSystem.AzureRbac)]);

        Assert.True(outcome.Allowed);
    }

    [Fact]
    public void Evaluate_RefusesAnAssignmentMadeFurtherDownTheTree()
    {
        // Reader on one container does not grant reader on a sibling.
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Read, Fixtures.CheckpointsContainer),
            [new RoleAssignment("Storage Blob Data Reader", Fixtures.ReportsContainer, RoleSystem.AzureRbac)]);

        Assert.False(outcome.Allowed);
        Assert.Equal("Storage Blob Data Reader", outcome.MissingRole?.RoleName);
    }

    [Fact]
    public void Evaluate_DoesNotMistakeAPrefixForAScope()
    {
        // "/…/containers/reports" is a textual prefix of
        // "/…/containers/reports-archive" and is not an ancestor of it.
        var archive = ResourceScope.Parse(Fixtures.ReportsContainer.Path + "-archive");

        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Read, archive),
            [new RoleAssignment("Storage Blob Data Reader", Fixtures.ReportsContainer, RoleSystem.AzureRbac)]);

        Assert.False(outcome.Allowed);
    }

    [Fact]
    public void Evaluate_LetsAContributorSatisfyAReadRequirement()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Read, Fixtures.ReportsContainer),
            [new RoleAssignment("Storage Blob Data Contributor", Fixtures.StorageAccount, RoleSystem.AzureRbac)]);

        Assert.True(outcome.Allowed);
    }

    [Fact]
    public void Evaluate_DoesNotLetAReaderSatisfyAWriteRequirement()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Write, Fixtures.ReportsContainer),
            [new RoleAssignment("Storage Blob Data Reader", Fixtures.StorageAccount, RoleSystem.AzureRbac)]);

        Assert.False(outcome.Allowed);
        Assert.Equal("Storage Blob Data Contributor", outcome.MissingRole?.RoleName);
    }

    [Fact]
    public void Evaluate_LetsQueueDataContributorCoverSendingAndProcessing()
    {
        var assignments = new[]
        {
            new RoleAssignment("Storage Queue Data Contributor", Fixtures.StorageAccount, RoleSystem.AzureRbac),
        };

        Assert.True(RoleCatalog.Evaluate(
            new AccessRequest(AzureService.QueueStorage, AccessIntent.SendMessages, Fixtures.WorkQueue),
            assignments).Allowed);
        Assert.True(RoleCatalog.Evaluate(
            new AccessRequest(AzureService.QueueStorage, AccessIntent.ProcessMessages, Fixtures.WorkQueue),
            assignments).Allowed);
    }

    [Fact]
    public void Evaluate_DoesNotLetASenderProcessMessages()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.QueueStorage, AccessIntent.ProcessMessages, Fixtures.WorkQueue),
            [new RoleAssignment("Storage Queue Data Message Sender", Fixtures.WorkQueue, RoleSystem.AzureRbac)]);

        Assert.False(outcome.Allowed);
        Assert.Equal("Storage Queue Data Message Processor", outcome.MissingRole?.RoleName);
    }

    [Fact]
    public void Evaluate_RefusesOwnerBecauseOwnerIsNotADataRole()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Read, Fixtures.ReportsContainer),
            [new RoleAssignment("Owner", Fixtures.Subscription, RoleSystem.AzureRbac)]);

        Assert.False(outcome.Allowed);
        Assert.Equal(403, outcome.Denial?.HttpStatus);
        Assert.Equal("AuthorizationPermissionMismatch", outcome.Denial?.ErrorCode);
    }

    [Fact]
    public void Evaluate_SaysWhyTheControlPlaneRoleDidNotHelp()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Read, Fixtures.ReportsContainer),
            [new RoleAssignment("Contributor", Fixtures.ResourceGroup, RoleSystem.AzureRbac)]);

        Assert.Contains("control-plane", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_RefusesACosmosDataRoleRecordedAsAnAzureRbacAssignment()
    {
        // This is the mistake that produces a role assignment which exists,
        // reads correctly in the portal, and grants nothing.
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.CosmosNoSql, AccessIntent.Write, Fixtures.CosmosAccount),
            [new RoleAssignment("Cosmos DB Built-in Data Contributor", Fixtures.CosmosAccount, RoleSystem.AzureRbac)]);

        Assert.False(outcome.Allowed);
        Assert.Equal(RoleSystem.CosmosDataPlane, outcome.MissingRole?.System);
    }

    [Fact]
    public void Evaluate_AllowsTheSameCosmosRoleInTheCosmosSystem()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.CosmosNoSql, AccessIntent.Write, Fixtures.CosmosAccount),
            [
                new RoleAssignment(
                    "Cosmos DB Built-in Data Contributor",
                    Fixtures.CosmosAccount,
                    RoleSystem.CosmosDataPlane),
            ]);

        Assert.True(outcome.Allowed);
    }

    [Fact]
    public void Evaluate_RefusesWhenTheIdentityHoldsNothingAtAll()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.EventHubs, AccessIntent.SendMessages, Fixtures.TelemetryHub),
            []);

        Assert.False(outcome.Allowed);
        Assert.Equal("Azure Event Hubs Data Sender", outcome.MissingRole?.RoleName);
    }

    [Fact]
    public void Evaluate_ReportsTheEventHubsDenialWithoutAnHttpStatus()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.EventHubs, AccessIntent.ProcessMessages, Fixtures.TelemetryHub),
            []);

        Assert.NotNull(outcome.Denial);
        Assert.Null(outcome.Denial!.HttpStatus);
    }

    [Fact]
    public void Evaluate_IgnoresARoleAssignedInADifferentResourceGroup()
    {
        var outcome = RoleCatalog.Evaluate(
            new AccessRequest(AzureService.BlobStorage, AccessIntent.Read, Fixtures.ReportsContainer),
            [new RoleAssignment("Storage Blob Data Owner", Fixtures.OtherResourceGroup, RoleSystem.AzureRbac)]);

        Assert.False(outcome.Allowed);
    }

    [Fact]
    public void Satisfies_IsNotSymmetric()
    {
        Assert.True(RoleCatalog.Satisfies("Storage Blob Data Contributor", "Storage Blob Data Reader"));
        Assert.False(RoleCatalog.Satisfies("Storage Blob Data Reader", "Storage Blob Data Contributor"));
    }

    [Fact]
    public void ControlPlaneRoles_ContainNoDataRole()
    {
        Assert.DoesNotContain(RoleCatalog.ControlPlaneRoles, role => role.Contains("Data", StringComparison.Ordinal));
    }
}
