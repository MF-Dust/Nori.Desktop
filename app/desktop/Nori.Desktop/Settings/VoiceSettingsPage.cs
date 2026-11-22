using System.Text.Json;
using Avalonia.Threading;
using Nori.Core.Voice.Realtime;

namespace Nori.Desktop.Settings;

/// <summary>语音、TTS 与 STT 设置页。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "设置命令回调按 UI 约定显式丢弃受控后台 Task。")]
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
	private readonly SettingsSectionViewModel _gptSection;
	private readonly SettingsSectionViewModel _indexSection;
	private readonly SettingsSectionViewModel _generalSection;
	private readonly SettingsCommand _previewCommand;
	private readonly SettingsCommand _cloneCommand;
	private bool _previewing;
	private bool _cloning;
	private bool _speaking;
	private int _realtimeRequest;
	private readonly SettingsFieldViewModel _micLevel;
	private readonly SettingsCommand _micStart;
	private readonly SettingsCommand _micStop;
	private readonly DispatcherTimer _micTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
	private CancellationTokenSource? _micTest;
	private Task? _micCancellation;

	/// <summary>创建语音设置页。</summary>
	public VoiceSettingsPage(SettingsService service, CancellationToken lifetimeToken = default)
		: base(service, "voice", "self", new("语音", "Voice"), new("配置朗读、语音克隆和语音识别。", "Configure speech, voice cloning and speech recognition."), lifetimeToken)
	{
		SettingsSectionViewModel devices = AddSection(new("设备与测试", "Devices & tests"));
		SettingsSectionViewModel general = _generalSection = AddSection(new("朗读", "Text to speech"));
		AddField(general, "volume", new("音量", "Volume"), new("调整 Nori 的全局朗读音量。", "Adjust Nori's global speech volume."), SettingsEditorKind.Slider,
			snapshot => SettingsSnapshotReader.Number(snapshot, 1, "voice", "volume"), 1,
			(value, token) => ExecuteAsync("settings_update_voice", new { volume = Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture) }, token),
			minimum: 0, maximum: 1, increment: 0.01);
		_ttsProvider = AddField(general, "ttsProvider", new("TTS 服务商", "TTS provider"), new("选择朗读服务。", "Choose the speech provider."), SettingsEditorKind.Choice,
			snapshot => SettingsSnapshotReader.String(snapshot, "openai", "voice", "ttsProvider"), "openai",
			(value, token) => SaveProviderAsync(Convert.ToString(value) ?? "openai", token), options: TtsProviders);
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
		_previewCommand = new SettingsCommand(command => _ = TestVoiceAsync(), canExecute => !_previewing && !_speaking);

		SettingsSectionViewModel gpt = _gptSection = AddSection(new("GPT-SoVITS", "GPT-SoVITS"));
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

		SettingsSectionViewModel index = _indexSection = AddSection(new("IndexTTS-2", "IndexTTS-2"));
		_indexTemplate = AddField(index, "indexttsTemplateAudio", new("模板音频", "Template audio"), new("用于克隆声音的本地音频。", "Local audio used for voice cloning."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "indexttsTemplateAudio"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { indexttsTemplateAudio = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddField(index, "indexttsEmoAlpha", new("情绪强度", "Emotion strength"), new("IndexTTS-2 的情绪控制强度。", "Emotion control strength for IndexTTS-2."), SettingsEditorKind.Slider,
			snapshot => SettingsSnapshotReader.Number(snapshot, 0.3, "voice", "indexttsEmoAlpha"), 0.3,
			(value, token) => ExecuteAsync("settings_update_voice", new { indexttsEmoAlpha = Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture) }, token),
			minimum: 0, maximum: 1, increment: 0.01);
		AddAction(index, "pickIndexTemplate", new("选择模板音频", "Pick template audio"), new("选择用于 IndexTTS-2 克隆的本地音频文件。", "Choose local audio for IndexTTS-2 voice cloning."), new SettingsCommand(command => _ = PickIndexTemplateAsync()));
		_cloneCommand = new SettingsCommand(command => _ = CloneIndexVoiceAsync(), canExecute => !_cloning);
		AddAction(index, "cloneIndexVoice", new("克隆声音", "Clone voice"), new("使用模板音频创建声音配置。", "Create a voice configuration from the template audio."), _cloneCommand);

		SettingsSectionViewModel stt = AddSection(new("语音识别", "Speech to text"));
		AddField(stt, "realtimeSttMode", new("识别模式", "Recognition mode"), new(
			"云端流式在说话时上传音频，整段模式在说完后上传。流式连接失败时可能改用整段上传。本地离线模式不上传音频，目前尚无本地引擎。切换模式会停止当前监听。",
			"Cloud streaming uploads audio as you speak; batch uploads after you finish. Streaming may fall back to batch. Offline recognition never uploads audio and is not available yet. Changing modes stops live listening."), SettingsEditorKind.Choice,
			snapshot => SettingsSnapshotReader.String(snapshot, "batch", "voice", "realtimeSttMode"), "batch",
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeSttMode = Convert.ToString(value) }, token),
			options: [new("batch", new("云端 · 整段", "Cloud · whole utterance")), new("realtime", new("云端 · 实时流式", "Cloud · live streaming")), new("local", new("本地离线（预留）", "Local offline (not yet available)"))]);
		AddField(stt, "sttBaseUrl", new("识别地址", "STT base URL"), new("OpenAI 兼容识别接口地址。", "OpenAI-compatible transcription endpoint."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "sttBaseUrl"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { sttProvider = "whisper", sttBaseUrl = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddField(stt, "sttApiKey", new("识别密钥", "STT API key"), new("保存成功后输入框自动清空。", "The input is cleared after a successful save."), SettingsEditorKind.Password,
			snapshot => SettingsSnapshotReader.SecretConfigured(snapshot, "voice", "hasSttApiKey"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { sttApiKey = Convert.ToString(value)?.Trim() ?? string.Empty }, token), secret: true);

		SettingsSectionViewModel live = AddSection(new("实时语音", "Live voice"));
		AddField(live, "realtimeStatus", new("当前状态", "Status"), new("状态栏只显示阶段。识别文字会进入聊天，原始录音不落盘。", "The status shows phases only. Recognized text enters Chat, and raw audio is not stored."), SettingsEditorKind.Text,
			StatusText, "", (_, _) => Task.FromResult<JsonElement>(default), readOnly: true);
		AddField(live, "realtimeEnabled", new("允许实时语音", "Allow live voice"), new("保存这个开关不会打开麦克风，启动时也不会自动监听。", "Saving this switch does not open the microphone, and listening does not start at launch."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "voice", "realtimeEnabled"), false,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeEnabled = Convert.ToBoolean(value) }, token));
		AddField(live, "realtimeContinuous", new("连续对话", "Continuous"), new("一轮对话及可选自动朗读结束后继续监听。自动朗读使用现有 TTS 开关。", "Resume listening after the reply and optional auto read-aloud. The existing TTS auto-play setting applies."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, false, "voice", "realtimeContinuous"), false,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeContinuous = Convert.ToBoolean(value) }, token));
		SettingsSectionViewModel advanced = AddSection(new("高级语音设置", "Advanced voice settings"), collapsible: true);
		AddField(advanced, "realtimeSensitivity", new("检测灵敏度", "Detection sensitivity"), new("0 更钝，1 更灵。只用本地音量，不会把检测发给云端。", "0 is less sensitive and 1 is more sensitive. Detection stays on this computer."), SettingsEditorKind.Slider,
			snapshot => SettingsSnapshotReader.Number(snapshot, 0.5, "voice", "realtimeSensitivity"), 0.5,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeSensitivity = Convert.ToDouble(value).ToString(System.Globalization.CultureInfo.InvariantCulture) }, token),
			minimum: 0, maximum: 1, increment: 0.05);
		AddField(advanced, "realtimeSilenceMs", new("静音结束", "Silence before end"), new("安静持续这么久就结束这一句，范围 200 到 3000 毫秒。", "End the utterance after this much silence, from 200 to 3000 milliseconds."), SettingsEditorKind.Number,
			snapshot => SettingsSnapshotReader.Number(snapshot, 700, "voice", "realtimeSilenceMs"), 700,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeSilenceMs = Math.Round(Convert.ToDouble(value)).ToString(System.Globalization.CultureInfo.InvariantCulture) }, token),
			minimum: 200, maximum: 3000, increment: 50);
		AddField(live, "realtimeInterruptEnabled", new("允许打断", "Allow interrupt"), new("关掉后，打断按钮和快捷键都不会停止朗读。", "When off, the interrupt button and shortcut leave playback running."), SettingsEditorKind.Boolean,
			snapshot => SettingsSnapshotReader.Boolean(snapshot, true, "voice", "realtimeInterruptEnabled"), true,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeInterruptEnabled = Convert.ToBoolean(value) }, token));
		AddVoiceText(advanced, "realtimeSttModel", new("流式识别模型", "Streaming model"), new("留空使用默认流式模型。", "Leave empty to use the default streaming model."), RealtimeVoiceSettings.Defaults.StreamingSttModel,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeSttModel = value }, token));
		AddVoiceText(advanced, "realtimeBatchModel", new("整段识别模型", "Batch model"), new("例如 gpt-transcribe 或 whisper-1；留空使用默认模型。", "For example, gpt-transcribe or whisper-1. Leave empty for the default."), RealtimeVoiceSettings.Defaults.BatchSttModel,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeBatchModel = value }, token));
		AddVoiceText(advanced, "realtimeLanguage", new("识别语言", "Recognition language"), new("例如 zh 或 en；留空自动识别。", "For example, zh or en. Leave empty for automatic detection."), RealtimeVoiceSettings.Defaults.Language,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeLanguage = value }, token));
		AddField(devices, "realtimeInputDevice", new("麦克风", "Microphone"), new("留空使用系统默认输入。当前采集不能按设备名切换，填了别的标识会拒绝开始。", "Leave empty for the system default. Capture cannot switch by device name; another id is rejected."), SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "realtimeInputDevice"), string.Empty,
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeInputDevice = Convert.ToString(value)?.Trim() ?? string.Empty }, token));
		AddVoiceText(devices, "realtimeOutputDevice", new("输出设备", "Output device"), new("此标识供后续版本使用；当前始终在系统默认设备上播放。", "This identifier is reserved for a later release. Playback still uses the system default."), "",
			(value, token) => ExecuteAsync("settings_update_voice", new { realtimeOutputDevice = value }, token));
		_micStart = new SettingsCommand(command => _ = TestMicrophoneAsync(), _ => _micTest is null);
		_micStop = new SettingsCommand(_ => CancelMicrophoneTest(), _ => _micTest is not null);
		AddAction(devices, "testMicrophone", new("测试麦克风", "Test microphone"), new("测试最多持续 10 秒，只在本机测量输入电平，不保存或上传录音。", "Measures input level locally for up to 10 seconds. No recording is saved or uploaded."), _micStart);
		AddAction(devices, "stopMicrophoneTest", new("结束麦克风测试", "Stop microphone test"), new("释放麦克风。", "Release the microphone."), _micStop);
		_micLevel = AddField(devices, "micTestLevel", new("麦克风电平", "Microphone level"), new("对着麦克风说话可查看电平变化。", "Speak into the microphone to check the input level."), SettingsEditorKind.Progress,
			_ => _micTest is null ? 0 : Service.MicrophoneTestLevel * 100, 0, (_, _) => Task.FromResult<JsonElement>(default), readOnly: true);
		_micTimer.Tick += (_, _) => _micLevel.Number = Service.MicrophoneTestLevel * 100;
		AddAction(devices, "testVoice", new("试听当前声音", "Preview voice"), new("使用当前 TTS 配置播放一条测试语音。", "Play a sample with the current TTS configuration."), _previewCommand);
		AddAction(devices, "stopVoice", new("停止播放", "Stop playback"), new("停止当前语音播放。", "Stop the current speech."), new SettingsCommand(command => _ = StopVoiceAsync()));
		AddAction(live, "realtimeStart", new("开始监听", "Start listening"), new("识别结果自动进入 AI 对话，可能调用工具和云端 API。其他录音或测试不能同时占用麦克风。", "Recognized speech starts an AI chat, which may use tools or cloud APIs. Live listening requires exclusive microphone access."), new SettingsCommand(command => _ = StartRealtimeAsync()));
		AddAction(live, "realtimeStop", new("停止监听", "Stop listening"), new("关闭实时监听并放开麦克风。", "Stop live listening and release the microphone."), new SettingsCommand(command => _ = StopRealtimeAsync()));
		AddAction(live, "realtimeInterrupt", new("打断朗读", "Interrupt speech"), new("立刻停止播放。朗读时麦克风是暂停的，说话不会打断；请用这个按钮，或在 Nori 窗口按 Ctrl+Shift+.（macOS 为 Command+Shift+.）。这不是系统全局热键。", "Stop playback immediately. The microphone pauses during speech, so talking does not interrupt; use this button or Ctrl+Shift+. in a Nori window (Command+Shift+. on macOS). This is not a system-wide hotkey."), new SettingsCommand(command => _ = InterruptRealtimeAsync()));
		Sections.Move(Sections.IndexOf(live), 0);
		Sections.Move(Sections.IndexOf(stt), 1);
		Sections.Move(Sections.IndexOf(advanced), 3);
		_ttsProvider.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(SettingsFieldViewModel.Selected)) UpdateProviderVisibility();
		};
		UpdateProviderVisibility();
	}

	internal override void ApplySnapshot(JsonElement snapshot)
	{
		base.ApplySnapshot(snapshot);
		_speaking = SettingsSnapshotReader.Boolean(snapshot, false, "voice", "speaking");
		_previewCommand.RaiseCanExecuteChanged();
		UpdateProviderVisibility();
	}

	private void AddVoiceText(SettingsSectionViewModel section, string key, SettingsText label, SettingsText description, string fallback,
		Func<string, CancellationToken, Task<JsonElement>> save) =>
		AddField(section, key, label, description, SettingsEditorKind.Text,
			snapshot => SettingsSnapshotReader.String(snapshot, fallback, "voice", key), fallback,
			(value, token) => save(Convert.ToString(value) ?? "", token));

	internal void CancelMicrophoneTest()
	{
		if (_micTest is { } test) _micCancellation ??= test.CancelAsync();
	}

	/// <inheritdoc />
	public override async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
	{
		CancelMicrophoneTest();
		if (Dispatcher.UIThread.CheckAccess()) _micTimer.Stop();
		else await Dispatcher.UIThread.InvokeAsync(_micTimer.Stop);
		await base.PrepareShutdownAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	protected override void OnDisposed()
	{
		CancelMicrophoneTest();
		if (Dispatcher.UIThread.CheckAccess()) _micTimer.Stop();
		else Dispatcher.UIThread.Post(_micTimer.Stop);
		base.OnDisposed();
	}

	private async Task TestMicrophoneAsync()
	{
		if (_micTest is not null) return;
		using var test = new CancellationTokenSource();
		_micTest = test;
		_micStart.RaiseCanExecuteChanged(); _micStop.RaiseCanExecuteChanged();
		try
		{
			if (!await FlushPendingSavesAsync(test.Token) || test.IsCancellationRequested) return;
			_micTimer.Start();
			await ExecuteAsync("voice_mic_test", cancellationToken: test.Token);
			if (!test.IsCancellationRequested) SetStatus(Text("麦克风测试结束。", "Microphone test complete."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception); }
		finally
		{
			_micTimer.Stop(); _micLevel.Number = 0;
			if (_micCancellation is { } stopping) await stopping;
			_micCancellation = null; _micTest = null;
			_micStart.RaiseCanExecuteChanged(); _micStop.RaiseCanExecuteChanged();
		}
	}

	private void UpdateProviderVisibility()
	{
		string provider = _ttsProvider.Selected;
		_gptSection.IsVisible = provider == "gpt_sovits";
		_indexSection.IsVisible = provider == "indextts";
		foreach (SettingsFieldViewModel field in _generalSection.Fields)
		{
			if (field.Key is "ttsBaseUrl" or "ttsApiKey") field.IsVisible = provider != "gpt_sovits";
			if (field.Key == "ttsModel") field.IsVisible = provider is "openai" or "gemini" or "indextts";
			if (field.Key == "ttsVoice") field.IsVisible = provider != "indextts";
		}
	}

	private async Task<JsonElement> SaveProviderAsync(string provider, CancellationToken cancellationToken)
	{
		(string BaseUrl, string Model, string Voice) defaults = await Dispatcher.UIThread.InvokeAsync(
			() => ProviderDefaults(provider, _ttsBaseUrl.Text, _ttsModel.Text, _ttsVoice.Text));
		return await ExecuteAsync("settings_update_voice", new
		{
			ttsProvider = provider,
			ttsBaseUrl = defaults.BaseUrl,
			ttsModel = defaults.Model,
			ttsVoice = defaults.Voice,
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>切换服务商时迁移默认参数，自定义接口和模型保持用户输入。</summary>
	internal static (string BaseUrl, string Model, string Voice) ProviderDefaults(string provider, string baseUrl, string model, string voice)
	{
		Dictionary<string, string> defaults = new(StringComparer.Ordinal)
		{
			["openai"] = "https://api.openai.com/v1",
			["gemini"] = "https://generativelanguage.googleapis.com/v1beta",
			["minimax"] = "https://api.minimaxi.com/v1",
			["indextts"] = "https://api.modelverse.cn/v1",
		};
		baseUrl = baseUrl.Trim();
		model = model.Trim();
		voice = voice.Trim();
		if ((baseUrl.Length == 0 || defaults.Values.Contains(baseUrl, StringComparer.OrdinalIgnoreCase)) && defaults.TryGetValue(provider, out string? address))
			baseUrl = address;
		if (provider == "gemini")
		{
			if (model.Length == 0 || model == "tts-1") model = "gemini-3.1-flash-tts-preview";
			if (voice.Length == 0 || voice is "nova" or "male-qn-qingse") voice = "Kore";
		}
		else if (provider == "minimax")
		{
			if (voice.Length == 0 || voice == "nova") voice = "male-qn-qingse";
		}
		else if (provider == "indextts")
		{
			if (model.Length == 0 || model == "tts-1" || model.StartsWith("gemini-", StringComparison.Ordinal)) model = "IndexTeam/IndexTTS-2";
		}
		else if (provider == "openai")
		{
			if (model.StartsWith("gemini-", StringComparison.Ordinal) || model.StartsWith("IndexTeam/", StringComparison.Ordinal)) model = "tts-1";
			if (voice is "male-qn-qingse" or "Kore") voice = "nova";
		}
		return (baseUrl, model, voice);
	}

	private static object StatusText(JsonElement snapshot)
	{
		string phase = SettingsSnapshotReader.String(snapshot, "idle", "voice", "realtimePhase");
		string error = SettingsSnapshotReader.String(snapshot, string.Empty, "voice", "realtimeError");
		bool paused = SettingsSnapshotReader.Boolean(snapshot, false, "voice", "realtimeCapturePaused");
		bool english = SettingsLocalization.IsEnglish;
		string label = phase switch
		{
			"listening" when paused => english ? "Listening is paused while audio plays" : "播放时已暂停监听",
			"listening" => english ? "Listening" : "正在听",
			"transcribing" => english ? "Transcribing" : "正在识别",
			"thinking" => english ? "Thinking" : "正在思考",
			"speaking" => english ? "Speaking. The microphone is paused" : "正在朗读，麦克风已暂停",
			"interrupting" => english ? "Interrupting" : "正在打断",
			"error" => english ? "Stopped" : "已停止",
			_ => english ? "Not listening" : "未监听",
		};
		if (SettingsSnapshotReader.Boolean(snapshot, false, "voice", "realtimeUsedBatchFallback"))
			label += " · " + (english ? "Streaming unavailable; using whole-utterance recognition." : SettingsSnapshotReader.String(snapshot, "已改用整段识别", "voice", "realtimeFallbackReason"));
		return error.Length == 0 ? label : label + " · " + SettingsErrorText.Resolve(error, english);
	}

	private async Task StartRealtimeAsync()
	{
		try
		{
			int request = Interlocked.Increment(ref _realtimeRequest);
			if (!await FlushPendingSavesAsync(LifetimeToken).ConfigureAwait(true)) return;
			if (request != Volatile.Read(ref _realtimeRequest)) return;
			await ExecuteAsync("realtime_voice_start", cancellationToken: LifetimeToken).ConfigureAwait(true);
			SetStatus(Text("已开始监听。", "Listening started."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception); }
	}

	private async Task StopRealtimeAsync()
	{
		try
		{
			Interlocked.Increment(ref _realtimeRequest);
			await ExecuteAsync("realtime_voice_stop", cancellationToken: LifetimeToken).ConfigureAwait(true);
			SetStatus(Text("已停止监听。", "Listening stopped."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception); }
	}

	private async Task InterruptRealtimeAsync()
	{
		try
		{
			JsonElement result = await ExecuteAsync("realtime_voice_interrupt", cancellationToken: LifetimeToken).ConfigureAwait(true);
			bool interrupted = result.ValueKind == JsonValueKind.Object
				&& result.TryGetProperty("interrupted", out JsonElement value)
				&& value.ValueKind == JsonValueKind.True;
			SetStatus(interrupted
				? Text("已打断朗读。", "Speech interrupted.")
				: Text("现在没有可打断的朗读。对着正在播放的声音说话不会打断。", "Nothing is speaking. Talking over playback does not interrupt."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception); }
	}

	private static string Text(string chinese, string english) => SettingsLocalization.IsEnglish ? english : chinese;

	private async Task PickIndexTemplateAsync()
	{
		try
		{
			JsonElement result = await ExecuteAsync("indextts_pick_template", cancellationToken: LifetimeToken).ConfigureAwait(true);
			string path = result.ValueKind == JsonValueKind.String
				? result.GetString() ?? string.Empty
				: result.ValueKind == JsonValueKind.Object && result.TryGetProperty("filePath", out JsonElement filePath)
					? filePath.GetString() ?? string.Empty
					: string.Empty;
			if (path.Length == 0)
			{
				SetStatus(Text("未选择模板音频。", "No template audio selected."));
				return;
			}
			_indexTemplate.Text = path;
			SetStatus(Text("模板音频已选择。", "Template audio selected."));
		}
		catch (Exception exception) { SetStatus(exception); }
	}

	private async Task TestVoiceAsync()
	{
		if (_previewing || _speaking) return;
		_previewing = true;
		_previewCommand.RaiseCanExecuteChanged();
		try
		{
			if (!await FlushPendingSavesAsync(LifetimeToken).ConfigureAwait(true)) return;
			await ExecuteAsync("tts_test", cancellationToken: LifetimeToken).ConfigureAwait(true);
			SetStatus(Text("试听已开始。", "Voice preview started."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception); }
		finally { _previewing = false; _previewCommand.RaiseCanExecuteChanged(); }
	}

	private async Task StopVoiceAsync()
	{
		try { await ExecuteAsync("tts_stop", cancellationToken: LifetimeToken).ConfigureAwait(true); }
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception); }
	}

	private async Task CloneIndexVoiceAsync()
	{
		if (_cloning) return;
		_cloning = true;
		_cloneCommand.RaiseCanExecuteChanged();
		try
		{
			if (!await FlushPendingSavesAsync(LifetimeToken).ConfigureAwait(true)) return;
			if (string.IsNullOrWhiteSpace(_indexTemplate.Text))
			{
				SetStatus(Text("请先填写模板音频路径。", "Set a template audio path first."));
				return;
			}
			JsonElement result = await ExecuteAsync("indextts_clone_voice", new { filePath = _indexTemplate.Text.Trim() }, LifetimeToken).ConfigureAwait(true);
			string voiceId = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("voiceId", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty;
			SetStatus(voiceId.Length > 0 ? Text("声音克隆完成：", "Voice cloned: ") + voiceId : Text("声音克隆完成。", "Voice cloned."));
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { SetStatus(exception); }
		finally { _cloning = false; _cloneCommand.RaiseCanExecuteChanged(); }
	}
}
