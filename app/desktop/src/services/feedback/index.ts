/**
 * 全局反馈层
 *
 * naive 的 useMessage/useDialog 只能在 provider 的后代组件里取到, 所以由
 * FeedbackHost.vue 在挂载时把实例登记进来, 之后任何模块 (含非组件代码) 都能调用。
 *
 * 纪律: 失败路径必须让用户看见 —— console.error 只做诊断, 不再是唯一出口。
 */
import type {DialogApiInjection} from "naive-ui/es/dialog/src/DialogProvider"
import type {MessageApiInjection} from "naive-ui/es/message/src/MessageProvider"
import {ReportLogError} from "../runtime/logging"

let messageApi: MessageApiInjection | null = null
let dialogApi: DialogApiInjection | null = null

/** 由 FeedbackHost 调用 */
export const registerFeedback = (message: MessageApiInjection, dialog: DialogApiInjection): void => {
	messageApi = message
	dialogApi = dialog
}

/** 提取可读错误文本 */
export const errorText = (error: unknown): string => {
	if (error instanceof Error) return error.message
	if (typeof error === "string") return error
	return String(error)
}

/** 反馈入口 */
export const feedback = {
	success(text: string): void {
		messageApi?.success(text)
	},
	info(text: string): void {
		messageApi?.info(text)
	},
	warning(text: string): void {
		messageApi?.warning(text)
	},
	/**
	 * 报错: 同时进控制台 (诊断) 与消息条 (用户可见)
	 *
	 * @param text 面向用户的中文说明
	 * @param error 原始错误, 只写日志不直接展示
	 */
	error(text: string, error?: unknown): void {
		void ReportLogError("feedback.error", error)
		const DETAIL = error === undefined ? "" : `: ${errorText(error)}`
		messageApi?.error(`${text}${DETAIL}`)
	},
	/** 危险操作二次确认 */
	confirm(options: {title: string; content: string; positiveText: string; negativeText: string}): Promise<boolean> {
		if (!dialogApi) return Promise.resolve(false)
		return new Promise<boolean>((resolve) => {
			dialogApi?.warning({
				title: options.title,
				content: options.content,
				positiveText: options.positiveText,
				negativeText: options.negativeText,
				onPositiveClick: () => { resolve(true) },
				onNegativeClick: () => { resolve(false) },
				onClose: () => { resolve(false) },
				onMaskClick: () => { resolve(false) },
			})
		})
	},
}
