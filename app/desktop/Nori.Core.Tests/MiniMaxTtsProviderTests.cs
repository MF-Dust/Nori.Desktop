using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;
using Nori.Core.Voice;
using Nori.Core.Voice.Audio;

namespace Nori.Core.Tests;

/// <summary>MiniMax 同步 T2A 请求映射、hex 解码与错误诊断测试。</summary>
public class MiniMaxTtsProviderTests : IDisposable
{
	private readonly TempDatabase _tempDatabase = new("nori-minimax-tts");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public MiniMaxTtsProviderTests()
	{
		_database = NoriDatabase.Open(_tempDatabase.Path);
		_config = new ConfigStore(_database);
		_config.InitDefaults("0.1.0");
		_config.Set("tts_provider", new ConfigValue.Text("minimax"));
		_config.Set("tts_base_url", new ConfigValue.Text("https://api.minimaxi.com/v1"));
		_config.Set("tts_api_key", new ConfigValue.Text("test-secret"));
	}

	[Fact]
	public async Task 同步T2A正确映射请求并解码Hex音频()
	{
		byte[] wave = WaveDecoder.Encode(new PcmAudio {Samples = [0, 0.5f, -0.5f], SampleRate = 24000, Channels = 1});
		HttpTestHandler handler = new(_ => SuccessResponse(Convert.ToHexString(wave)));
		using HttpClient client = new(handler);
		MiniMaxTtsProvider provider = new(client, _config);

		EncodedAudio audio = await provider.SynthesizeAsync(
			"你好，Nori",
			new TtsSynthesizeOptions {Voice = "male-qn-qingse", Speed = 1.2},
			CancellationToken.None);

		Assert.Equal(new Uri("https://api.minimaxi.com/v1/t2a_v2"), handler.LastUri);
		Assert.Equal("Bearer", handler.AuthorizationScheme);
		Assert.Equal("test-secret", handler.AuthorizationParameter);
		Assert.Equal("audio/wav", audio.Mime);
		Assert.Equal(wave, audio.Bytes);
		PcmAudio decoded = WaveDecoder.Decode(audio.Bytes);
		Assert.Equal(24000, decoded.SampleRate);
		Assert.Equal(new float[] {0, 0.5f, -0.5f}, decoded.Samples);

		JsonNode body = JsonNode.Parse(handler.LastBody!)!;
		Assert.Equal("speech-2.8-turbo", body["model"]?.GetValue<string>());
		Assert.Equal("你好，Nori", body["text"]?.GetValue<string>());
		Assert.False(body["stream"]?.GetValue<bool>() ?? true);
		Assert.Equal("male-qn-qingse", body["voice_setting"]?["voice_id"]?.GetValue<string>());
		Assert.Equal(1.2, body["voice_setting"]?["speed"]?.GetValue<double>());
		Assert.Equal("wav", body["audio_setting"]?["format"]?.GetValue<string>());
		Assert.Equal("hex", body["output_format"]?.GetValue<string>());
	}

	[Theory]
	[InlineData(null, "audio/wav")]
	[InlineData("mp3", "audio/mpeg")]
	[InlineData("flac", "audio/flac")]
	public async Task 响应格式缺省沿用WAV且显式格式保留真实MIME(string? format, string mime)
	{
		using HttpClient client = new(new HttpTestHandler(_ => SuccessResponse("01", format)));
		MiniMaxTtsProvider provider = new(client, _config);

		EncodedAudio audio = await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions(), CancellationToken.None);

		Assert.Equal(mime, audio.Mime);
	}

	[Fact]
	public async Task 完整端点不会重复追加T2A路径()
	{
		_config.Set("tts_base_url", new ConfigValue.Text("https://api.minimaxi.com/v1/t2a_v2"));
		HttpTestHandler handler = new(_ => SuccessResponse("01"));
		using HttpClient client = new(handler);
		MiniMaxTtsProvider provider = new(client, _config);

		await provider.SynthesizeAsync("测试", new TtsSynthesizeOptions(), CancellationToken.None);

		Assert.Equal(new Uri("https://api.minimaxi.com/v1/t2a_v2"), handler.LastUri);
	}

	[Fact]
	public async Task 业务错误包含状态消息与TraceId()
	{
		HttpTestHandler handler = new(_ => JsonResponse("""
			{
				"data": null,
				"trace_id": "trace-123",
				"base_resp": {"status_code": 1008, "status_msg": "invalid api key"}
			}
			"""));
		using HttpClient client = new(handler);
		MiniMaxTtsProvider provider = new(client, _config);

		VoiceProviderException error = await Assert.ThrowsAsync<VoiceProviderException>(() =>
			provider.SynthesizeAsync("测试", new TtsSynthesizeOptions(), CancellationToken.None));

		Assert.Contains("status_code=1008", error.Message, StringComparison.Ordinal);
		Assert.Contains("invalid api key", error.Message, StringComparison.Ordinal);
		Assert.Contains("trace-123", error.Message, StringComparison.Ordinal);
		Assert.Equal(VoiceFailureKind.ProviderRejected, error.FailureKind);
		Assert.Equal(1008, error.ProviderStatusCode);
	}

	[Fact]
	public void VoiceService能够创建MiniMaxProvider()
	{
		using HttpClient client = new(new HttpTestHandler(_ => SuccessResponse("01")));
		using VoiceService service = new(client, _config, null, () => null);

		Assert.IsType<MiniMaxTtsProvider>(service.CreateProvider("minimax"));
	}

	public void Dispose()
	{
		_database.Dispose();
		_tempDatabase.Dispose();
	}

	private static HttpResponseMessage SuccessResponse(string audioHex, string? format = "wav") => JsonResponse($$"""
		{
			"data": {"audio": "{{audioHex}}", "status": 2},
			"extra_info": {"audio_format": {{JsonSerializer.Serialize(format)}}},
			"trace_id": "trace-ok",
			"base_resp": {"status_code": 0, "status_msg": "success"}
		}
		""");

	private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json, Encoding.UTF8, "application/json"),
	};
}
