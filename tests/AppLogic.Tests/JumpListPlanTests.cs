using Resesh.App.Interop;
using Resesh.Core.Models;

namespace Resesh.AppLogic.Tests;

public sealed class JumpListPlanTests
{
    private static readonly Session Web = new() { Name = "web-01", Host = "web-01.example", Username = "deploy" };
    private static readonly Session Db = new() { Name = "db", Host = "10.0.0.5" };
    private static readonly Session Shell = new() { Name = "PowerShell", Kind = SessionKind.Local };
    private static readonly Dictionary<Guid, Session> Saved = new[] { Web, Db, Shell }.ToDictionary(s => s.Id);

    private static Session? Find(Guid id) => Saved.GetValueOrDefault(id);

    [Fact]
    public void Items_launch_the_session_by_id_and_describe_where_it_connects()
    {
        var plan = JumpListPlan.For([], [Web.Id, Db.Id, Shell.Id], Find);

        Assert.Equal(
            [
                new JumpListItem("web-01", $"--session {Web.Id:D}", "deploy@web-01.example"),
                new JumpListItem("db", $"--session {Db.Id:D}", "10.0.0.5"),
                new JumpListItem("PowerShell", $"--session {Shell.Id:D}", "Local terminal"),
            ],
            plan.Recent);
    }

    [Fact]
    public void Pinned_sessions_are_not_repeated_under_recent_and_deleted_ones_drop_out()
    {
        var plan = JumpListPlan.For([Db.Id, Guid.NewGuid()], [Db.Id, Web.Id, Guid.NewGuid()], Find);

        Assert.Equal(["db"], plan.Pinned.Select(item => item.Title));
        Assert.Equal(["web-01"], plan.Recent.Select(item => item.Title));
    }

    [Fact]
    public void Recent_is_capped_after_pinned_sessions_are_left_out()
    {
        var many = Enumerable.Range(0, 20).Select(i => new Session { Name = $"s{i}", Host = "h" }).ToList();
        var lookup = many.ToDictionary(s => s.Id);

        var plan = JumpListPlan.For([many[0].Id], many.Select(s => s.Id).ToList(), lookup.GetValueOrDefault);

        Assert.Equal(JumpListPlan.RecentLimit, plan.Recent.Count);
        Assert.Equal("s1", plan.Recent[0].Title);
    }

    [Fact]
    public void Items_the_user_removed_stay_off_the_list()
    {
        var plan = JumpListPlan.For([Db.Id], [Web.Id], Find)
            .Without(new HashSet<string> { JumpListPlan.ArgumentsFor(Db.Id), JumpListPlan.ArgumentsFor(Web.Id) });

        Assert.Empty(plan.Pinned);
        Assert.Empty(plan.Recent);
    }

    [Fact]
    public void Same_items_compare_equal_so_the_list_is_not_rewritten()
    {
        Assert.True(JumpListPlan.For([], [Web.Id], Find).SameAs(JumpListPlan.For([], [Web.Id], Find)));
        Assert.False(JumpListPlan.For([], [Web.Id], Find).SameAs(JumpListPlan.For([], [Db.Id], Find)));
    }

    [Fact]
    public void Jump_list_arguments_round_trip_through_launch_parsing()
    {
        var request = LaunchRequest.Parse([@"C:\Program Files\Resesh\Resesh.App.exe", "--session", Web.Id.ToString("D")]);

        Assert.Equal([Web.Id], request.SessionIds);
        Assert.True(request.OpensSomething);
    }

    [Fact]
    public void Launch_parsing_reads_every_open_option_and_skips_the_rest()
    {
        var request = LaunchRequest.Parse(
            ["resesh", "--data-dir", @"D:\data", "--open", "db", "--session", "not-a-guid", "--open-recording", @"C:\a.cast", "--open"]);

        Assert.Empty(request.SessionIds);
        Assert.Equal(["db"], request.SessionNames);
        Assert.Equal([@"C:\a.cast"], request.RecordingPaths);
    }

    [Fact]
    public void A_plain_launch_opens_nothing()
    {
        Assert.False(LaunchRequest.Parse([]).OpensSomething);
        Assert.False(LaunchRequest.Parse(["resesh"]).OpensSomething);
    }
}
