namespace Nori.Core.Sandbox.Linux;

internal sealed record SandboxHostRequest(
	int Abi, string FileName, string WorkspaceRoot, IReadOnlyList<string> ReadOnlyPaths,
	bool AllowNetwork, IReadOnlyDictionary<string, string> EnvironmentOverrides);
