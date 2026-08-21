namespace Resesh.Core.Agents;

/// <summary>
/// What the agent in a tab wants from the user. Ordered from quiet to loud; the badge
/// colour and the alert list both derive from this.
/// </summary>
public enum AgentAttention
{
    /// <summary>No agent, or nothing to say.</summary>
    None,

    /// <summary>An agent is present but not doing anything.</summary>
    Idle,

    /// <summary>The agent is working.</summary>
    Working,

    /// <summary>The agent is blocked on a permission/approval decision.</summary>
    NeedsApproval,

    /// <summary>The agent asked a question and is waiting for an answer.</summary>
    NeedsAnswer,

    /// <summary>The agent finished its task.</summary>
    Complete,

    /// <summary>The agent stopped with an error.</summary>
    Failed,

    /// <summary>
    /// Low-confidence attention: a bell or a generic OSC 9 notification. Something asked
    /// for the user, but nothing told us what — the UI must not claim input is required.
    /// </summary>
    Signal,
}

public static class AgentAttentionExtensions
{
    /// <summary>The agent is genuinely blocked on the user. These survive until the user
    /// actually sends input (or a structured event says work resumed) — merely looking at
    /// the tab doesn't unblock the agent.</summary>
    public static bool RequiresUser(this AgentAttention attention) =>
        attention is AgentAttention.NeedsApproval or AgentAttention.NeedsAnswer;

    /// <summary>Worth showing in a background-notification / alert list.</summary>
    public static bool IsAlert(this AgentAttention attention) =>
        attention.RequiresUser() || attention is AgentAttention.Failed or AgentAttention.Signal;

    /// <summary>Cleared by selecting the tab: the user has now seen it.</summary>
    public static bool ClearsOnView(this AgentAttention attention) =>
        attention is AgentAttention.Complete or AgentAttention.Failed or AgentAttention.Signal;

    /// <summary>Short, neutral wording for tooltips. Deliberately hedged for
    /// <see cref="AgentAttention.Signal"/> — a bell is not a statement about input.</summary>
    public static string Describe(this AgentAttention attention) => attention switch
    {
        AgentAttention.Working => "working",
        AgentAttention.NeedsApproval => "needs approval",
        AgentAttention.NeedsAnswer => "needs an answer",
        AgentAttention.Complete => "finished",
        AgentAttention.Failed => "failed",
        AgentAttention.Signal => "signalled (bell)",
        AgentAttention.Idle => "idle",
        _ => "",
    };
}
