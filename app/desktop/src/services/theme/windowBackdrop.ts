import {RUNTIME} from "../runtime"

/** 只在宿主确认系统模糊生效时透出窗口背景，查询失败时保留实底。 */
export const BindWindowBackdrop = (style: CSSStyleDeclaration): (() => void) => {
	let disposed = false
	let receivedEvent = false
	let unlisten: (() => void) | undefined
	const apply = (active: boolean) => {
		if (!disposed) style.setProperty("--window-background", active ? "var(--bg-glass)" : "var(--bg-base)")
	}
	apply(false)

	void (async () => {
		try {
			const STOP = await RUNTIME.onWindowBackdrop((state) => {
				receivedEvent = true
				apply(state.active === true)
			})
			if (disposed) {
				STOP()
				return
			}
			unlisten = STOP
			const STATE = await RUNTIME.windowBackdropState()
			// 初始查询可能晚于变更事件返回，事件代表更新的材质状态。
			if (!receivedEvent) apply(STATE.active === true)
		} catch {
			// 材质是可选增强，失败不能遮挡界面或中断启动。
			if (!receivedEvent) apply(false)
		}
	})()

	return () => {
		disposed = true
		unlisten?.()
		style.removeProperty("--window-background")
	}
}
