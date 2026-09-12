using System.Text.Json;

namespace Nori.Desktop.Settings;

/// <summary>语音、TTS 与 STT 设置页。</summary>
public sealed class VoiceSettingsPage : SettingsPageBase
{
	private static readonly IReadOnlyList<SettingsOption> TtsProviders =
	[
		new("openai", new("OpenAI", "OpenAI")),
		new("gemini", new("Google Gemini", "Google Gemini")),
		new("minimax", new("MiniMax", "MiniMax")),
		new("custom", new("自定义 HTTP", "Custom HTTP")),
		new("gpt_sovits", new("GPT-SoVITS", "GPT-SoVITS")),
		new("indextts", new("IndexTTS-2", "IndexTTS-2")),
	];

	private readonly SettingsFieldViewModel _ttsProvider;
	private readonly SettingsFieldViewModel _ttsBaseUrl;
	private readonly SettingsFieldViewModel _ttsModel;
	private readonly SettingsFieldViewModel _ttsVoice;
	private readonly SettingsFieldViewModel _indexTemplate;

	/// <summary>创建语音设置页。</summary>
	public VoiceSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "voice", "perception", new("语音", "Voice"), new("配置朗读、语音克隆和语音识别。", "Configure speech, voice cloning and speech recognition."), lifetimeToken)
	{
		SettingsSectionViewModel general = AddSection(new("朗读", "Text to speech"));
		AddField(general, "volume", new("音量", "Volume"), new("调整 Nori 的全局朗读音量。", "Adjust Nori's global speech volume."), SettingsEditorKind.Slider,
			snapshot => SettingsSnapshotReader.Number(snapshot, 1, "voice", "volume"), 1,
			(value, token) => ExecuteAsync("settings_update_voice", new { volume = Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture) }, token),
			minimum: 0, maximum: 1, increment: 0.01);
		_ttsProvider = AddField(general, "ttsProvider", new("TTS 服务商", "TTS provider"), new("选择朗读服务。", "Choose the speech provider."), SettingsEditorKind.Choice,
			snapshot => SettingsSnapshotReader.String(snapshot, "openai", "voice", "ttsProvider"), "openai",
			(value, token) => ExecuteAsync("settings_update_voice", new { ttsProvider = Convert.ToString(value) ?? "openai" }, token), options: TtsProviders);
		_ttsBaseUrl = AddField(general, "ttsBaseUrl", new("TTS 地址", "TTS base URL"), new("云服务或本地兼容服务地址。", "Cloud or local compatible service endpoint."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "ttsBaseUrl"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { ttsBaseUrl = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		_ttsModel = AddField(general, "ttsModel", new("TTS 模型", "TTS model"), new("填写服务商支持的模型 ID。", "Enter a model ID supported by the provider."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, "tts-1", "voice", "ttsModel"), "tts-1",
			(value, token) => ExecuteAsync("settings_update_voice", new { ttsModel = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddField(general, "ttsApiKey", new("TTS 密钥", "TTS API key"), new("保存成功后输入框自动清空。", "The input is cleared after a successful save."), SettingsEditorKind.Password,
			snapshot => SettingsSnapshotReader.SecretConfigured(snapshot, "voice", "hasTtsApiKey"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { ttsApiKey = Convert.ToString(value)?.Trim() ?? string.Empty }, token), secret: true);
		_ttsVoice = AddField(general, "ttsVoice", new("声音", "Voice"), new("填写服务商支持的声音名称。", "Enter a voice supported by the provider."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, "nova", "voice", "ttsVoice"), "nova",
			(value, token) => ExecuteAsync("settings_update_voice", new { ttsVoice = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddField(general, "ttsSpeed", new("语速", "Speech speed"), new("通常范围为 0.5 到 2。", "Usually between 0.5 and 2."), SettingsEditorKind.Number,
			snapshot => SettingsSnapshotReader.Number(snapshot, 1, "voice", "ttsSpeed"), 1,
			(value, token) => ExecuteAsync("settings_update_voice", new { ttsSpeed = Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture) }, token),
			minimum: 0.25, maximum: 4, increment: 0.05);
		AddField(general, "ttsAutoPlay", new("自动朗读", "Auto play"), new("让对话回复自动播放语音。", "Play speech automatically for chat replies."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "voice", "ttsAutoPlay"), false,
			(value, token) => ExecuteAsync("settings_update_voice", new { ttsAutoPlay = Convert.ToBoolean(value) }, token));
		AddAction(general, "testVoice", new("试听当前声音", "Preview voice"), new("使用当前 TTS 配置播放一条测试语音。", "Play a sample with the current TTS configuration."), new SettingsCommand(_ => _ = TestVoiceAsync()));
		AddAction(general, "ackVoiceNotice", new("关闭旧版提示", "Dismiss legacy notice"), new("确认已阅读旧版浏览器语音配置提示。", "Dismiss the legacy browser voice configuration notice."), new SettingsCommand(_ => _ = AcknowledgeVoiceNoticeAsync()));

		SettingsSectionViewModel gpt = AddSection(new("GPT-SoVITS", "GPT-SoVITS"));
		AddField(gpt, "gptsovitsBaseUrl", new("服务地址", "Service URL"), new("GPT-SoVITS API 地址。", "GPT-SoVITS API endpoint."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, "http://127.0.0.1:9880", "voice", "gptsovitsBaseUrl"), "http://127.0.0.1:9880",
			(value, token) => ExecuteAsync("settings_update_voice", new { gptsovitsBaseUrl = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddField(gpt, "gptsovitsRefAudio", new("参考音频", "Reference audio"), new("本地参考音频路径。", "Path to a local reference audio file."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "gptsovitsRefAudio"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { gptsovitsRefAudio = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddField(gpt, "gptsovitsPromptText", new("参考文本", "Reference text"), new("参考音频对应的文本。", "Text spoken in the reference audio."), SettingsEditorKind.Multiline,
			snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "gptsovitsPromptText"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { gptsovitsPromptText = Convert.ToString(value) ?? string.Empty }, token));
		AddField(gpt, "gptsovitsPromptLang", new("参考语言", "Reference language"), new("例如 zh、en。", "For example, zh or en."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, "zh", "voice", "gptsovitsPromptLang"), "zh",
			(value, token) => ExecuteAsync("settings_update_voice", new { gptsovitsPromptLang = Convert.ToString(value)?.Trim() ?? string.Empty }, token));

		SettingsSectionViewModel index = AddSection(new("IndexTTS-2", "IndexTTS-2"));
		_indexTemplate = AddField(index, "indexttsTemplateAudio", new("模板音频", "Template audio"), new("用于克隆声音的本地音频。", "Local audio used for voice cloning."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "indexttsTemplateAudio"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { indexttsTemplateAudio = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddField(index, "indexttsEmoAlpha", new("情绪强度", "Emotion strength"), new("IndexTTS-2 的情绪控制强度。", "Emotion control strength for IndexTTS-2."), SettingsEditorKind.Slider,
			snapshot => SettingsSnapshotReader.Number(snapshot, 0.3, "voice", "indexttsEmoAlpha"), 0.3,
			(value, token) => ExecuteAsync("settings_update_voice", new { indexttsEmoAlpha = Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture) }, token),
			minimum: 0, maximum: 1, increment: 0.01);
		AddAction(index, "pickIndexTemplate", new("选择模板音频", "Pick template audio"), new("选择用于 IndexTTS-2 克隆的本地音频文件。", "Choose local audio for IndexTTS-2 voice cloning."), new SettingsCommand(_ => _ = PickIndexTemplateAsync()));
		AddAction(index, "cloneIndexVoice", new("克隆声音", "Clone voice"), new("使用模板音频创建声音配置。", "Create a voice configuration from the template audio."), new SettingsCommand(_ => _ = CloneIndexVoiceAsync()));

		SettingsSectionViewModel stt = AddSection(new("语音识别", "Speech to text"));
		AddField(stt, "sttBaseUrl", new("识别地址", "STT base URL"), new("Whisper 兼容接口地址。", "Whisper-compatible endpoint."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "sttBaseUrl"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { sttProvider = "whisper", sttBaseUrl = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddField(stt, "sttApiKey", new("识别密钥", "STT API key"), new("保存成功后输入框自动清空。", "The input is cleared after a successful save."), SettingsEditorKind.Password,
			snapshot => SettingsSnapshotReader.SecretConfigured(snapshot, "voice", "hasSttApiKey"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { sttApiKey = Convert.ToString(value)?.Trim() ?? string.Empty }, token), secret: true);
	}

	private async Task PickIndexTemplateAsync()
	{
		try
		{
			JsonElement result = await ExecuteAsync("indextts_pick_template", cancellationToken: LifetimeToken).ConfigureAwait(false);
			string path = result.ValueKind == JsonValueKind.String
				? result.GetString() ?? string.Empty
				: result.ValueKind == JsonValueKind.Object && result.TryGetProperty("filePath", out JsonElement filePath)
					? filePath.GetString() ?? string.Empty
					: string.Empty;
			if (path.Length == 0)
			{
				SetStatus("未选择模板音频。 / No template audio selected.");
				return;
			}
			_indexTemplate.Text = path;
			SetStatus("模板音频已选择。 / Template audio selected.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private async Task TestVoiceAsync()
	{
		try
		{
			await ExecuteAsync("tts_test", cancellationToken: LifetimeToken).ConfigureAwait(false);
			SetStatus("试听已开始。 / Voice preview started.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private async Task AcknowledgeVoiceNoticeAsync()
	{
		try
		{
			await ExecuteAsync("settings_ack_voice_notice", cancellationToken: LifetimeToken).ConfigureAwait(false);
			SetStatus("旧版提示已关闭。 / Legacy notice dismissed.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}

	private async Task CloneIndexVoiceAsync()
	{
		try
		{
			if (string.IsNullOrWhiteSpace(_indexTemplate.Text))
			{
				SetStatus("请先填写模板音频路径。 / Set a template audio path first.");
				return;
			}
			JsonElement result = await ExecuteAsync("indextts_clone_voice", new { filePath = _indexTemplate.Text.Trim() }, LifetimeToken).ConfigureAwait(false);
			string voiceId = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("voiceId", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty;
			SetStatus(voiceId.Length > 0 ? "声音克隆完成：" + voiceId : "声音克隆完成。 / Voice cloned.");
		}
		catch (Exception exception) { SetStatus(exception.Message); }
	}
}
