/**
 * 首次运行向导状态机
 *
 * 步进、守卫与提交状态全部收在这里, 视图只做渲染:
 * 原来的实现里「下一步」在末步静默 no-op、模型保存与 complete_first_run 失败只打
 * console.error, 界面毫无变化 —— 观感就是「卡在模型选择」。这里把每一步的失败
 * 都变成可见状态 (stepError / finishError) 并允许重试。
 */

/** 向导步骤标识 */
export type WizardStepKey = "welcome" | "language" | "model" | "ai" | "ready"

/**
 * 向导步骤顺序 (与 FirstRunView 的渲染分支一致)
 *
 * `ai` 是可跳过的一步: 不填也能继续, 只有填了内容才在离开时落盘。
 */
export const WIZARD_STEPS: WizardStepKey[] = ["welcome", "language", "model", "ai", "ready"]

/** 提交阶段状态 */
export type WizardFinishState = "idle" | "submitting" | "failed"

/** 向导快照 (纯数据, 便于测试) */
export interface WizardState {
	index: number
	step: WizardStepKey
	direction: 1 | -1
	isFirst: boolean
	isLast: boolean
	canNext: boolean
	canPrev: boolean
	finishState: WizardFinishState
	stepError: string
	finishError: string
}

/** 提交回调: 成功 resolve, 失败 reject (错误消息用于展示) */
export type WizardFinisher = () => Promise<void>

/**
 * 创建向导状态机
 *
 * @param finish 完成回调 (调用 complete_first_run)
 */
export function createWizard(finish: WizardFinisher) {
	let index = 0
	let direction: 1 | -1 = 1
	let finishState: WizardFinishState = "idle"
	let stepError = ""
	let finishError = ""
	// 步骤级阻断: 例如模型选择保存失败时不允许继续
	const BLOCKED = new Set<WizardStepKey>()

	const stepAt = (value: number): WizardStepKey => {
		// eslint-disable-next-line -- Codacy误报：value仅来自向导内部有界索引
		return WIZARD_STEPS[value] ?? WIZARD_STEPS[0]
	}

	const snapshot = (): WizardState => ({
		index,
		step: stepAt(index),
		direction,
		isFirst: index === 0,
		isLast: index === WIZARD_STEPS.length - 1,
		canNext: index < WIZARD_STEPS.length - 1 && !BLOCKED.has(stepAt(index)),
		canPrev: index > 0 && finishState !== "submitting",
		finishState,
		stepError,
		finishError,
	})

	return {
		snapshot,

		/** 前进一步; 末步或被阻断时返回 false */
		next(): boolean {
			if (!snapshot().canNext) return false
			direction = 1
			index += 1
			stepError = ""
			return true
		},

		/** 后退一步 */
		prev(): boolean {
			if (!snapshot().canPrev) return false
			direction = -1
			index -= 1
			stepError = ""
			finishError = ""
			if (finishState === "failed") finishState = "idle"
			return true
		},

		/** 标记当前步骤出错并阻断前进 */
		blockStep(message: string): void {
			stepError = message
			BLOCKED.add(stepAt(index))
		},

		/** 清除当前步骤的阻断 */
		clearStep(): void {
			stepError = ""
			BLOCKED.delete(stepAt(index))
		},

		/**
		 * 提交向导
		 *
		 * 成功后停留在末步 (窗口随后由宿主关闭); 失败则回到 failed 态并暴露错误可重试。
		 */
		async finish(): Promise<boolean> {
			if (finishState === "submitting") return false
			finishState = "submitting"
			finishError = ""
			try {
				await finish()
				finishState = "idle"
				return true
			} catch (error) {
				finishState = "failed"
				finishError = error instanceof Error ? error.message : String(error)
				return false
			}
		},
	}
}

export type Wizard = ReturnType<typeof createWizard>
