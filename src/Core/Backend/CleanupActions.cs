namespace Resesh.Core.Backend;

/// <summary>Attempts every cleanup step, even when an earlier step fails.</summary>
public static class CleanupActions
{
    public static void Run(Action<Exception> report, params Action[] actions)
    {
        var errors = new List<Exception>();
        foreach (var action in actions)
        {
            try { action(); }
            catch (Exception exception) { errors.Add(exception); }
        }
        foreach (var error in errors)
            report(error);
    }
}
