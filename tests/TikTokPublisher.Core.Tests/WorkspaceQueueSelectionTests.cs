using TikTokPublisher.Core.Queue;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Core.Tests;

public sealed class WorkspaceQueueSelectionTests
{
    [Theory]
    [InlineData(1L, true)]
    [InlineData(2L, false)]
    public void Stale_workspace_load_restores_manual_choices_without_enabling_new_or_other_workspace_rows(
        long observedRevision, bool persistenceFlushed)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "account-a");
        var otherWorkspace = Path.Combine(Path.GetTempPath(), "account-b");
        var uncheckedProject = Path.Combine(workspace, "unchecked");
        var checkedProject = Path.Combine(workspace, "checked");
        var newProject = Path.Combine(workspace, "new");
        var snapshot = new MainViewModel.WorkspaceQueueSnapshot
        {
            SelectionRevision = 2,
            Items = [Row(uncheckedProject, false), Row(checkedProject, true)],
        };

        var unloaded = new[]
        {
            Row(uncheckedProject + Path.DirectorySeparatorChar, true),
            Row(checkedProject, false),
            Row(newProject, true),
            Row(Path.Combine(otherWorkspace, "unchecked"), true),
        };
        snapshot.RestoreSelection(unloaded, observedRevision, persistenceFlushed);

        Assert.Equal(new[] { false, true, true, true }, unloaded.Select(item => item.Enabled));
    }

    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(2L, 2L, true)]
    public void Fresh_workspace_load_uses_saved_values(long latestRevision, long observedRevision, bool persistenceFlushed)
    {
        var project = Path.Combine(Path.GetTempPath(), "account-a", "project");
        var snapshot = new MainViewModel.WorkspaceQueueSnapshot
        {
            SelectionRevision = latestRevision,
            Items = [Row(project, false)],
        };
        var readFromDisk = Row(project, true);
        snapshot.RestoreSelection([readFromDisk], observedRevision, persistenceFlushed);

        Assert.True(readFromDisk.Enabled);
    }

    private static QueueProjectItem Row(string path, bool enabled) => new() { ProjectDir = path, Enabled = enabled };
}
