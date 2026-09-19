/**
 * 前端音频宿主。
 *
 * 音频从后端下沉到 WebView: 三平台共用一套 WebAudio / MediaRecorder 实现，
 * 不依赖 NAudio。监听器安装后先发送 audio_host_ready，后端才会投递音频事件。
 */
import {invoke} from "../host/invoke"
import {listen, type UnlistenFn} from "../host/event"
import {ReportLogError} from "../runtime/logging"

/** 音量采样间隔 (ms)，与原生后端一致。 */
const LEVEL_INTERVAL_MS = 60
/** 单段音频和录音上传上限。 */
export const MAX_AUDIO_BYTES = 32 * 1024 * 1024
const RECORDING_MIME_CANDIDATES = [
	"audio/webm;codecs=opus",
	"audio/webm",
	"audio/ogg;codecs=opus",
	"audio/mp4",
] as const
const SUPPORTED_AUDIO_TYPES = new Set([
	"audio/aac",
	"audio/flac",
	"audio/mp4",
	"audio/mpeg",
	"audio/mp3",
	"audio/ogg",
	"audio/opus",
	"audio/wav",
	"audio/wave",
	"audio/webm",
	"audio/x-wav",
])

interface PlayPayload {
	token: string
	url: string
	mime: string
	volume: number
}

interface RecordStartPayload {
	token: string
	uploadUrl: string
}

interface RecordStopPayload {
	token?: string
}

let context: AudioContext | null = null
let gain: GainNode | null = null
let analyser: AnalyserNode | null = null
let source: AudioBufferSourceNode | null = null
let levelTimer: ReturnType<typeof setInterval> | null = null
let currentToken = ""
let playbackGeneration = 0
let rmsBuffer = new Float32Array(1024)

let recorder: MediaRecorder | null = null
let recorderChunks: Blob[] = []
let recorderStream: MediaStream | null = null
let recordUploadUrl = ""
let recordToken = ""
let recordingGeneration = 0
let recordingStopping = false
let audioHostInstalled = false

/** 校验并规范音频 MIME，保留 codecs 参数。 */
export const normalizeAudioMime = (mime: string | null | undefined): string => {
	const VALUE = mime?.trim() ?? ""
	if (!VALUE) throw new Error("音频 MIME 类型为空")
	const SEPARATOR = VALUE.indexOf(";")
	const MEDIA_TYPE = (SEPARATOR < 0 ? VALUE : VALUE.slice(0, SEPARATOR)).trim().toLowerCase()
	if (!SUPPORTED_AUDIO_TYPES.has(MEDIA_TYPE)) throw new Error(`不支持的音频 MIME 类型: ${mime}`)
	return `${MEDIA_TYPE}${SEPARATOR < 0 ? "" : VALUE.slice(SEPARATOR).trim()}`
}

/** 判断 MIME 是否是当前音频链路支持的类型。 */
export const isSupportedAudioMime = (mime: string | null | undefined): boolean => {
	try {
		normalizeAudioMime(mime)
		return true
	} catch {
		return false
	}
}

/** 选择浏览器实际支持的 MediaRecorder MIME。 */
export const chooseRecordingMime = (mediaRecorder: typeof MediaRecorder | undefined = globalThis.MediaRecorder): string => {
	// Codacy 误报：这是 WebView 能力探测，运行时确实可能没有 MediaRecorder。
	// eslint-disable-next-line @typescript-eslint/no-unnecessary-condition -- WebView运行时能力可缺失
	if (!mediaRecorder) throw new Error("当前 WebView 不支持 MediaRecorder")
	for (const MIME of RECORDING_MIME_CANDIDATES) {
		if (typeof mediaRecorder.isTypeSupported !== "function" || mediaRecorder.isTypeSupported(MIME)) return MIME
	}
	return ""
}

/** 根据实际录音 MIME 生成上传文件名。 */
export const recordingFileNameForMime = (mime: string): string => {
	const MEDIA_TYPE = normalizeAudioMime(mime).split(";", 1)[0]
	const EXTENSION = MEDIA_TYPE === "audio/mpeg" || MEDIA_TYPE === "audio/mp3"
		? "mp3"
		: MEDIA_TYPE === "audio/wav" || MEDIA_TYPE === "audio/wave" || MEDIA_TYPE === "audio/x-wav"
			? "wav"
			: MEDIA_TYPE === "audio/ogg" || MEDIA_TYPE === "audio/opus"
				? "ogg"
				: MEDIA_TYPE === "audio/mp4"
					? "m4a"
					: MEDIA_TYPE === "audio/aac"
						? "aac"
						: MEDIA_TYPE === "audio/flac" ? "flac" : "webm"
	return `speech.${EXTENSION}`
}

const ensureContext = (): AudioContext => {
	if (!context) {
		context = new AudioContext()
		gain = context.createGain()
		analyser = context.createAnalyser()
		analyser.fftSize = 1024
		rmsBuffer = new Float32Array(analyser.fftSize)
		gain.connect(analyser)
		analyser.connect(context.destination)
	}
	return context
}

/** 当前 RMS 音量 (0~1)。复用固定缓冲，避免每 60ms 分配数组。 */
const readLevel = (): number => {
	if (!analyser) return 0
	if (rmsBuffer.length !== analyser.fftSize) rmsBuffer = new Float32Array(analyser.fftSize)
	analyser.getFloatTimeDomainData(rmsBuffer)
	let sum = 0
	for (const sample of rmsBuffer) sum += sample * sample
	return Math.min(1, Math.sqrt(sum / rmsBuffer.length) * 3)
}

const stopLevelTimer = () => {
	if (levelTimer) clearInterval(levelTimer)
	levelTimer = null
}

const reportFinished = (token: string, error?: string) => {
	if (!token) return
	void invoke("audio_playback_finished", error ? {token, error} : {token}).catch(() => {
		/* 宿主已退出时忽略 */
	})
}

const reportFinalLevel = (token: string) => {
	if (!token) return
	void invoke("audio_level", {level: 0}).catch(() => {
		/* 宿主已退出时忽略 */
	})
}

const releaseSource = (stop: boolean) => {
	stopLevelTimer()
	if (!source) return
	source.onended = null
	if (stop) {
		try {
			source.stop()
		} catch {
			/* 已经停了 */
		}
	}
	source.disconnect()
	source = null
}

/** 取消当前播放，不发送 finished；后端会由 audio-stop 解除等待。 */
const cancelPlayback = () => {
	const TOKEN = currentToken
	currentToken = ""
	playbackGeneration += 1
	releaseSource(true)
	if (TOKEN) reportFinalLevel(TOKEN)
}

/** 彻底释放 WebAudio 图和原生 AudioContext；重新安装宿主时会按需重建。 */
const releaseAudioGraph = () => {
	stopLevelTimer()
	try { gain?.disconnect() } catch { /* 已断开 */ }
	try { analyser?.disconnect() } catch { /* 已断开 */ }
	gain = null
	analyser = null
	rmsBuffer = new Float32Array(1024)
	const CONTEXT = context
	context = null
	if (CONTEXT && CONTEXT.state !== "closed") {
		void CONTEXT.close().catch(() => {
			/* WebView 正在退出时忽略 */
		})
	}
}

const isCurrentPlayback = (token: string, generation: number): boolean =>
	currentToken === token && playbackGeneration === generation

const finishPlayback = (token: string, generation: number, error?: string) => {
	if (!isCurrentPlayback(token, generation)) return
	currentToken = ""
	releaseSource(false)
	reportFinalLevel(token)
	reportFinished(token, error)
}

const play = async (payload: PlayPayload): Promise<void> => {
	cancelPlayback()
	currentToken = payload.token
	const GENERATION = playbackGeneration
	try {
		const CONTEXT = ensureContext()
		if (CONTEXT.state === "suspended") await CONTEXT.resume()
		if (!isCurrentPlayback(payload.token, GENERATION)) return

		const RESPONSE = await fetch(payload.url, {cache: "no-store"})
		if (!RESPONSE.ok) throw new Error(`音频下载失败: HTTP ${RESPONSE.status}`)
		if (Number(RESPONSE.headers.get("content-length") ?? 0) > MAX_AUDIO_BYTES) {
			throw new Error("音频响应超过 32MiB 限制")
		}
		normalizeAudioMime(RESPONSE.headers.get("content-type") || payload.mime)
		const DATA = await RESPONSE.arrayBuffer()
		if (DATA.byteLength === 0) throw new Error("音频响应为空")
		if (DATA.byteLength > MAX_AUDIO_BYTES) throw new Error("音频响应超过 32MiB 限制")
		if (!isCurrentPlayback(payload.token, GENERATION)) return
		const BUFFER = await CONTEXT.decodeAudioData(DATA)
		if (!isCurrentPlayback(payload.token, GENERATION)) return

		if (gain) gain.gain.value = Math.min(1, Math.max(0, payload.volume))
		const NODE = CONTEXT.createBufferSource()
		NODE.buffer = BUFFER
		NODE.connect(gain as GainNode)
		source = NODE
		const TOKEN = payload.token
		NODE.onended = () => { finishPlayback(TOKEN, GENERATION) }
		levelTimer = setInterval(() => {
			if (isCurrentPlayback(TOKEN, GENERATION)) void invoke("audio_level", {level: readLevel()}).catch(() => {})
		}, LEVEL_INTERVAL_MS)
		NODE.start()
	} catch (error) {
		if (isCurrentPlayback(payload.token, GENERATION)) {
			finishPlayback(payload.token, GENERATION, error instanceof Error ? error.message : String(error))
		}
	}
}

const reportRecordingFailure = (token: string, error: unknown, upload = false) => {
	if (!token) return
	const MESSAGE = error instanceof Error ? error.message : String(error)
	const COMMAND = upload ? "audio_upload_failed" : "audio_record_failed"
	void invoke(COMMAND, {token, error: MESSAGE}).catch(() => {
		/* 宿主已退出时忽略 */
	})
}

const stopStream = (stream: MediaStream | null) => {
	stream?.getTracks().forEach(track => { track.stop() })
}

/** 宿主卸载时只取消录音，不上传可能不完整的半截数据。 */
const cancelRecording = () => {
	recordingGeneration += 1
	recordingStopping = false
	const ACTIVE = recorder
	const STREAM = recorderStream
	recorder = null
	recorderStream = null
	recordUploadUrl = ""
	recordToken = ""
	recorderChunks = []
	if (ACTIVE) {
		ACTIVE.ondataavailable = null
		ACTIVE.onstop = null
		ACTIVE.onerror = null
		if (ACTIVE.state !== "inactive") {
			try { ACTIVE.stop() } catch { /* 已停止 */ }
		}
	}
	stopStream(STREAM)
}

const startRecording = async (payload: RecordStartPayload): Promise<void> => {
	if (recordingStopping) {
		reportRecordingFailure(payload.token, new Error("上一段录音仍在结束"))
		return
	}
	// 新请求取代尚未完成的权限申请；已开始的录音按宿主协议不会并发启动。
	const GENERATION = ++recordingGeneration
	recordToken = payload.token
	recordUploadUrl = payload.uploadUrl
	recorderChunks = []
	let acquiredStream: MediaStream | null = null
	try {
		// Codacy 误报：不同 WebView 的媒体能力在运行时可缺失。
		// eslint-disable-next-line @typescript-eslint/no-unnecessary-condition -- WebView运行时能力可缺失
		if (!navigator.mediaDevices?.getUserMedia) throw new Error("当前 WebView 不支持麦克风")
		acquiredStream = await navigator.mediaDevices.getUserMedia({audio: true})
		if (!audioHostInstalled || GENERATION !== recordingGeneration || recordToken !== payload.token) {
			stopStream(acquiredStream)
			return
		}
		recorderStream = acquiredStream
		const REQUESTED_MIME = chooseRecordingMime(globalThis.MediaRecorder)
		recorder = REQUESTED_MIME
			? new MediaRecorder(acquiredStream, {mimeType: REQUESTED_MIME})
			: new MediaRecorder(acquiredStream)
		const ACTIVE = recorder
		ACTIVE.ondataavailable = (event) => {
			if (GENERATION === recordingGeneration && event.data.size > 0) recorderChunks.push(event.data)
		}
		ACTIVE.start()
		normalizeAudioMime(ACTIVE.mimeType || REQUESTED_MIME)
		await invoke("audio_record_ready", {token: payload.token})
		// 保留浏览器报告的真实 MIME，不能在停止时强制改成 audio/wav。
	} catch (error) {
		if (GENERATION !== recordingGeneration || recordToken !== payload.token) {
			stopStream(acquiredStream)
			return
		}
		stopStream(recorderStream ?? acquiredStream)
		recorder = null
		recorderStream = null
		recordUploadUrl = ""
		recordToken = ""
		recorderChunks = []
		recordingGeneration += 1
		void ReportLogError("audio.error", error)
		reportRecordingFailure(payload.token, error)
	}
}

const stopRecording = async (payload?: RecordStopPayload): Promise<void> => {
	const TOKEN = payload?.token || recordToken
	const ACTIVE = recorder
	const URL_TARGET = recordUploadUrl
	const STREAM = recorderStream
	const GENERATION = recordingGeneration
	if (TOKEN && recordToken && TOKEN !== recordToken) return
	recorder = null
	recordUploadUrl = ""
	recordToken = ""
	recorderStream = null
	if (!ACTIVE || !URL_TARGET || !TOKEN) {
		if (GENERATION === recordingGeneration) recordingGeneration += 1
		recorderChunks = []
		stopStream(STREAM)
		if (TOKEN) reportRecordingFailure(TOKEN, new Error("录音未启动"), true)
		return
	}

	recordingStopping = true
	try {
		await new Promise<void>((resolve, reject) => {
			ACTIVE.onstop = () => { resolve() }
			ACTIVE.onerror = () => { reject(new Error("MediaRecorder 录音失败")) }
			try {
				ACTIVE.stop()
			} catch (error) {
				reject(error instanceof Error ? error : new Error(String(error)))
			}
		})
		const MIME = normalizeAudioMime(ACTIVE.mimeType || recorderChunks[0]?.type)
		const BLOB = new Blob(recorderChunks, {type: MIME})
		recorderChunks = []
		if (BLOB.size === 0) throw new Error("录音内容为空")
		if (BLOB.size > MAX_AUDIO_BYTES) throw new Error("录音超过 32MiB 限制")
		const RESPONSE = await fetch(URL_TARGET, {
			method: "POST",
			headers: {
				"Content-Type": MIME,
				"X-Nori-Audio-Filename": recordingFileNameForMime(MIME),
			},
			body: BLOB,
		})
		if (!RESPONSE.ok) throw new Error(`录音上传失败: HTTP ${RESPONSE.status}`)
	} catch (error) {
		recorderChunks = []
		void ReportLogError("audio.error", error)
		reportRecordingFailure(TOKEN, error, true)
	} finally {
		stopStream(STREAM)
		ACTIVE.ondataavailable = null
		ACTIVE.onstop = null
		ACTIVE.onerror = null
		if (GENERATION === recordingGeneration) recordingGeneration += 1
		recordingStopping = false
	}
}

let unlisteners: UnlistenFn[] = []

/** 安装音频宿主 (仅 main 窗口调用)。 */
export const installAudioHost = async (): Promise<void> => {
	if (audioHostInstalled) return
	audioHostInstalled = true
	const NEXT_UNLISTENERS: UnlistenFn[] = []
	try {
		NEXT_UNLISTENERS.push(await listen<PlayPayload>("nori:audio-play", ({payload}) => void play(payload)))
		NEXT_UNLISTENERS.push(await listen("nori:audio-stop", () => { cancelPlayback() }))
		NEXT_UNLISTENERS.push(await listen<RecordStartPayload>("nori:audio-record-start", ({payload}) => void startRecording(payload)))
		NEXT_UNLISTENERS.push(await listen<RecordStopPayload>("nori:audio-record-stop", ({payload}) => void stopRecording(payload)))
		unlisteners = NEXT_UNLISTENERS
		await invoke("audio_host_ready")
	} catch (error) {
		for (const UNLISTEN of NEXT_UNLISTENERS) UNLISTEN()
		unlisteners = []
		audioHostInstalled = false
		cancelPlayback()
		cancelRecording()
		releaseAudioGraph()
		throw error
	}
}

/** 卸载音频宿主并释放浏览器侧原生音频/麦克风资源。 */
export const uninstallAudioHost = (): void => {
	audioHostInstalled = false
	for (const UNLISTEN of unlisteners) UNLISTEN()
	unlisteners = []
	cancelPlayback()
	cancelRecording()
	releaseAudioGraph()
}

/** 供测试使用的纯函数：由时域样本算 RMS 电平。 */
export const computeLevel = (samples: ArrayLike<number>): number => {
	let sum = 0
	// eslint-disable-next-line -- Codacy误报：数组索引由受控循环边界产生
	for (let index = 0; index < samples.length; index += 1) sum += samples[index] * samples[index]
	if (samples.length === 0) return 0
	return Math.min(1, Math.sqrt(sum / samples.length) * 3)
}
