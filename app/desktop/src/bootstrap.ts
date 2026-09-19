import {host} from "./services/host"
import {ReportLogError} from "./services/runtime/logging"

// 兼容音频宿主只安装音频协议，不加载 Vue 主界面、导航或运行时快照。
if (host()?.label === "audio-host") {
	const {installAudioHost, uninstallAudioHost} = await import("./services/audio")
	window.addEventListener("pagehide", uninstallAudioHost, {once: true})
	try {
		await installAudioHost()
	} catch (error) {
		void ReportLogError("audio.error", error)
	}
} else {
	await import("./main")
}
