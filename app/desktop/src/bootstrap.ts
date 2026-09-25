import {host} from "./services/host"
import {ReportLogError} from "./services/runtime/logging"

// 用户窗口已是原生界面。这个页面只给兼容音频宿主用。
if (host()?.label === "audio-host") {
	const {installAudioHost, uninstallAudioHost} = await import("./services/audio")
	window.addEventListener("pagehide", uninstallAudioHost, {once: true})
	try {
		await installAudioHost()
	} catch (error) {
		void ReportLogError("audio.error", error)
	}
}
