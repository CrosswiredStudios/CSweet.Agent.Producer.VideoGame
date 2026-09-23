using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Producer.VideoGame.Tests;

public sealed class CanonicalPlanningTests
{
    [Fact]
    public void SupersessionCancelsOnlyUntouchedUnscheduledStalePlanning()
    {
        var staleEpic = Item("old-m1", WorkItemKinds.Epic, WorkStatuses.Ready);
        var staleStory = Item("old-story", WorkItemKinds.Story, WorkStatuses.Backlog, staleEpic.Id);
        var staleTask = Item("old-task", WorkItemKinds.Task, WorkStatuses.Backlog, staleStory.Id);
        var protectedEpic = Item("shipping-m1", WorkItemKinds.Epic, WorkStatuses.Ready);
        var protectedStory = Item("shipping-story", WorkItemKinds.Story, WorkStatuses.Backlog, protectedEpic.Id);
        var sprintTask = Item("shipping-task", WorkItemKinds.Task, WorkStatuses.Ready,
            protectedStory.Id, Guid.NewGuid());
        var current = Item("current-m1", WorkItemKinds.Epic, WorkStatuses.Ready);
        var unrelated = new WorkItem(Guid.NewGuid(), Guid.NewGuid(), null, null, WorkItemKinds.Epic,
            "Human plan", "", WorkStatuses.Ready, WorkPriorities.High, null, 1, 1, null);

        var stale = SpecialistAgent.SupersededPlanningItems(
            [staleEpic, staleStory, staleTask, protectedEpic, protectedStory, sprintTask, current, unrelated],
            new HashSet<string>(["current-m1"], StringComparer.Ordinal));

        Assert.Equal([staleTask.Id, staleStory.Id, staleEpic.Id], stale.Select(x => x.Id));
    }

    private static WorkItem Item(string key, string kind, string status, Guid? parentId = null, Guid? sprintId = null)
    {
        var package = new ArtifactPackageDigest(Guid.NewGuid(), 1, "digest", DateTimeOffset.UtcNow, []);
        return new WorkItem(Guid.NewGuid(), Guid.NewGuid(), parentId, sprintId, kind,
            $"[{key}] Plan", "", status, WorkPriorities.High, null, 1, 1, null)
        {
            Planning = new WorkItemPlanningSpecification(["Plan"], ["Verified"]) { ArtifactPackageDigest = package },
            ProposalProvenance = new WorkItemProposalProvenance(Guid.NewGuid(), "artifact", key)
        };
    }
}
