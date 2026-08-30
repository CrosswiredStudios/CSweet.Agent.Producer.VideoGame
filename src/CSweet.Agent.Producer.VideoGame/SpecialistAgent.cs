using CSweet.VideoGame.AgentKit;

namespace CSweet.Agent.Producer.VideoGame;

public sealed class SpecialistAgent : VideoGameSpecialistAgentBase
{
    public override string AgentId => "com.csweet.video-game-producer";
    public override string Version => "1.0.0";
    public override string PrimaryCapability => "video-game.producer.execute.v1";
    protected override string RoleKey => "game-producer";
    protected override string ArtifactTypeKey => "video-game.production-plan.v1";
    protected override string RolePrompt => "You are the operational lead for one video game team. Own board health, sprint planning, schedule, budget, dependencies, staffing, risks, and attributed portfolio reporting. Convert uncertainty into assigned work or durable decisions.";
    protected override IReadOnlyList<string> RequiredSections => ["Schedule", "Budget", "Dependencies", "Staffing", "Risks", "Status Reporting"];
}

