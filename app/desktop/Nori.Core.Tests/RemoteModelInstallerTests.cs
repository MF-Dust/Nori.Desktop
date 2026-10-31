using System.Net;
using System.Text;
using Nori.Core.Data;
using Nori.Core.Live2D;
using Nori.Core.Resources;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public sealed class RemoteModelInstallerTests : IDisposable
{
	private const string Manifest = """
		{
			"FileReferences": {
				"Moc": "ARGNori.moc3",
				"Textures": ["ARGNori.4096/texture_00.png"],
				"Physics": "ARGNori.physics3.json",
				"Expressions": [{"Name": "Smile", "File": "expressions/07 Smile.exp3.json"}],
				"Motions": {"Idle": [{"File": "motions/idle.motion3.json", "Sound": "sounds/idle.wav"}]}
			}
		}
		""";

	private readonly string _dataDir = Directory.CreateTempSubdirectory("nori-remote-model-").FullName;
	private readonly ResourceManager _resources;

	public RemoteModelInstallerTests() => _resources = new ResourceManager(_dataDir);

	public void Dispose()
	{
		if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true);
	}

	private static Dictionary<string, string> Files(string manifest = Manifest) => new(StringComparer.Ordinal)
	{
		["/ARGNori_web/ARGNori.model3.json"] = manifest,
		["/ARGNori_web/ARGNori.moc3"] = "MOC3body",
		["/ARGNori_web/ARGNori.4096/texture_00.png"] = "png",
		["/ARGNori_web/ARGNori.physics3.json"] = "{}",
		["/ARGNori_web/expressions/07%20Smile.exp3.json"] = "{}",
		["/ARGNori_web/motions/idle.motion3.json"] = "{}",
	};

	private static HttpClient Client(Dictionary<string, string> files, List<string> requests) => new(new HttpTestHandler(request =>
	{
		Assert.Equal("static.nori.mom", request.RequestUri!.Host);
		string path = request.RequestUri.AbsolutePath;
		lock (requests) requests.Add(path);
		return files.TryGetValue(request.RequestUri.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped), out string? body)
			? new HttpResponseMessage(HttpStatusCode.OK) {Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))}
			: new HttpResponseMessage(HttpStatusCode.NotFound);
	}));

	private string[] ScratchDirectories() => Directory.Exists(Path.Combine(_dataDir, AppPaths.ResourcesDirName))
		? Directory.GetDirectories(Path.Combine(_dataDir, AppPaths.ResourcesDirName), ".nori-*")
		: [];

	[Fact]
	public async Task 按清单下载并原子安装_不下载声音且清理暂存()
	{
		List<string> requests = [];
		using HttpClient http = Client(Files(), requests);
		List<RemoteModelProgress> reports = [];
		RemoteModelInstaller installer = new(http, _resources);

		string id = await installer.InstallAsync("ARG-Nori", new SyncProgress(reports.Add));

		Assert.Equal(SupportedModelIds.ArgNori, id);
		Assert.True(_resources.IsInstalled(ResourceType.Live2D, SupportedModelIds.ArgNori));
		Assert.True(File.Exists(Path.Combine(_resources.ResourceDir(ResourceType.Live2D, id), "expressions", "07 Smile.exp3.json")));
		Assert.Equal(6, requests.Count);
		Assert.DoesNotContain(requests, path => path.Contains("sounds", StringComparison.Ordinal));
		Assert.Contains(reports, report => report is {CompletedFiles: 5, TotalFiles: 5});
		Assert.Empty(ScratchDirectories());
	}

	[Fact]
	public async Task 路径穿越清单在发出文件请求前被拒绝()
	{
		List<string> requests = [];
		using HttpClient http = Client(Files(Manifest.Replace("ARGNori.physics3.json", "../../evil.json", StringComparison.Ordinal)), requests);

		await Assert.ThrowsAsync<ResourceException>(() => new RemoteModelInstaller(http, _resources).InstallAsync("arg-nori"));

		Assert.Equal(["/ARGNori_web/ARGNori.model3.json"], requests);
		Assert.False(_resources.IsInstalled(ResourceType.Live2D, SupportedModelIds.ArgNori));
		Assert.Empty(ScratchDirectories());
	}

	[Fact]
	public async Task 缺失文件时失败且不改动已安装模型()
	{
		List<string> requests = [];
		using (HttpClient first = Client(Files(), requests))
			await new RemoteModelInstaller(first, _resources).InstallAsync("arg-nori");
		string installedMoc = Path.Combine(_resources.ResourceDir(ResourceType.Live2D, "arg-nori"), "ARGNori.moc3");

		Dictionary<string, string> broken = Files();
		broken.Remove("/ARGNori_web/ARGNori.4096/texture_00.png");
		broken["/ARGNori_web/ARGNori.moc3"] = "MOC3changed";
		using HttpClient http = Client(broken, requests);
		ResourceException failure = await Assert.ThrowsAsync<ResourceException>(() => new RemoteModelInstaller(http, _resources).InstallAsync("arg-nori"));

		Assert.Contains("404", failure.Message, StringComparison.Ordinal);
		Assert.Equal("MOC3body", File.ReadAllText(installedMoc));
		Assert.Empty(ScratchDirectories());
	}

	[Fact]
	public async Task 禁止联网或未知模型时不发请求()
	{
		List<string> requests = [];
		using HttpClient http = Client(Files(), requests);

		await Assert.ThrowsAsync<ResourceException>(() => new RemoteModelInstaller(http, _resources, () => false).InstallAsync("arg-nori"));
		await Assert.ThrowsAsync<ResourceException>(() => new RemoteModelInstaller(http, _resources).InstallAsync("other"));

		Assert.Empty(requests);
	}

	[Theory]
	[InlineData("http://static.nori.mom")]
	[InlineData("https://static.nori.mom/live2d")]
	[InlineData("https://user@static.nori.mom")]
	[InlineData("https://static.nori.mom/?a=1")]
	public void 只接受纯_HTTPS_origin(string origin)
	{
		using HttpClient http = new();
		Assert.Throws<ArgumentException>(() => new RemoteModelInstaller(http, _resources, origin: origin));
	}

	private sealed class SyncProgress(Action<RemoteModelProgress> report) : IProgress<RemoteModelProgress>
	{
		public void Report(RemoteModelProgress value)
		{
			lock (this) report(value);
		}
	}
}
