namespace Cafe.Agent.Core;

public record AgentSettings(string ServerUrl, string SharedKey, string MachineId, string EmergencyUnlockCodeHash);
