using Nori.Core.Configuration;

namespace Nori.Core.Cloud;

/// <summary>一次同步操作的结果。</summary>
public sealed record CloudSyncResult
{
	public bool Ok { get; init; }

	/// <summary>可直接展示的说明。成功时是「做了什么」，失败时是「为什么」与「该改哪一项」。</summary>
	public string Message { get; init; } = "";

	/// <summary>
	/// 云端比本机手上那份新。
	///
	/// 这是并发的正常结果，不是错误。调用方要把选择权交给用户 —— 默默覆盖等于把
	/// 版本号那道闸拆掉，默默取回则会丢掉本机刚改的东西。
	/// </summary>
	public bool Conflict { get; init; }

	/// <summary>冲突时云端那份的生成时刻，用来让用户判断该留哪一份。</summary>
	public string RemoteSavedAt { get; init; } = "";

	/// <summary>打包或恢复过程中被跳过的内容及原因。静默少传是这类功能最难发现的故障。</summary>
	public IReadOnlyList<string> Skipped { get; init; } = [];
}

/// <summary>云端存档的状态，用于在用户动手之前先告诉他云端有什么。</summary>
public sealed record CloudSyncStatus
{
	public bool SignedIn { get; init; }
	public bool Present { get; init; }
	public int Revision { get; init; }
	public string SavedAt { get; init; } = "";
	public string AppVersion { get; init; } = "";
	public int Bytes { get; init; }

	/// <summary>本机手上记的云端版本号。与 <see cref="Revision"/> 不等即说明别处改过。</summary>
	public int LocalKnownRevision { get; init; }

	public string Error { get; init; } = "";
	public bool Ok => Error.Length == 0;
}

/// <summary>
/// 云端存档的同步。
///
/// ── 分工 ──────────────────────────────────────────────────────────────────
/// <see cref="CloudSaveService"/> 决定「打包什么、怎么合并回来」，
/// <see cref="NoriCloudClient"/> 决定「怎么送」，
/// <see cref="AccountSession"/> 决定「以谁的身份」。这一层只把它们接起来，并负责
/// 记住**本机知道的云端版本号** —— 那个数是冲突检测唯一的依据。
///
/// ── 为什么要记版本号 ──────────────────────────────────────────────────────
/// 一个账户可以在多台机器上登录。不记的话，这台机器每次上传都是无条件覆盖，另一台
/// 刚存的东西就没了，而且两边都不会有任何迹象。记下来之后，服务端会在版本对不上时
/// 拒绝，用户因此有机会选。
///
/// 版本号是**这台机器的状态**，不进云存档（见 CloudSaveScope 的排除清单）——
/// 把它同步过去等于让另一台机器以为自己已经见过某个版本。
/// </summary>
public sealed class CloudSyncService(
	NoriCloudClient cloud,
	AccountSession session,
	CloudSaveService saves,
	ConfigStore config)
{
	/// <summary>本机最后一次见到的云端版本号。</summary>
	internal const string RevisionKey = "cloud_save_revision";

	/// <summary>云端现在有什么。动手之前先看这个。</summary>
	public async Task<CloudSyncStatus> StatusAsync(CancellationToken cancel = default)
	{
		if (session.Current is not {} account)
		{
			return new CloudSyncStatus {SignedIn = false, Error = "尚未登录 Nori 账户"};
		}

		CloudSaveSnapshot snapshot = await cloud.FetchSaveAsync(account.Token, metaOnly: true, cancel);
		if (!snapshot.Ok)
		{
			// 令牌作废之后本机那份登录记录就是错的，先清掉再答 —— 这一轮的答案里
			// SignedIn 也要跟着变成 false，否则界面刷完还是「已登录」。
			if (snapshot.Expired) return new CloudSyncStatus {SignedIn = !DropExpiredSession(), Error = snapshot.Error};
			return new CloudSyncStatus {SignedIn = true, Error = snapshot.Error};
		}
		return new CloudSyncStatus
		{
			SignedIn = true,
			Present = snapshot.Present,
			Revision = snapshot.Revision,
			SavedAt = snapshot.SavedAt,
			AppVersion = snapshot.AppVersion,
			Bytes = snapshot.Bytes,
			LocalKnownRevision = KnownRevision,
		};
	}

	/// <summary>
	/// 打包本机数据并上传。
	///
	/// <paramref name="overwrite"/> 为真时不带版本号，即无条件覆盖云端。**只应在用户
	/// 明确选择「以本机为准」之后传真** —— 默认走版本检查，让冲突显出来。
	/// </summary>
	public async Task<CloudSyncResult> BackupAsync(bool overwrite = false, CancellationToken cancel = default)
	{
		if (session.Current is not {} account)
		{
			return new CloudSyncResult {Message = "尚未登录 Nori 账户"};
		}

		CloudSaveBuild build = saves.Build(ProductVersion.Current);
		if (build.Bytes > CloudSaveService.MaxBytes)
		{
			// 本机先拦，给得出更具体的原因。服务端也会拦，但那时只知道「超了」。
			return new CloudSyncResult
			{
				Message = $"存档 {build.Bytes / 1024} KB，超出上限 {CloudSaveService.MaxBytes / 1024} KB。"
					+ "请减少记忆或提醒后重试",
				Skipped = build.Skipped,
			};
		}

		string json = saves.Serialize(build.Document);
		CloudSaveUploadResult uploaded = await cloud.UploadSaveAsync(
			account.Token, json, overwrite ? null : KnownRevision, cancel);

		if (uploaded.Conflict)
		{
			return new CloudSyncResult
			{
				Conflict = true,
				RemoteSavedAt = uploaded.SavedAt,
				Message = "云端已有更新的存档。请选择保留哪一份。",
				Skipped = build.Skipped,
			};
		}
		if (!uploaded.Ok)
		{
			if (uploaded.Expired) DropExpiredSession();
			return new CloudSyncResult {Message = uploaded.Error, Skipped = build.Skipped};
		}

		KnownRevision = uploaded.Revision;
		return new CloudSyncResult
		{
			Ok = true,
			Message = $"已上传：偏好 {build.ConfigCount} 项、记忆 {build.MemoryCount} 条、提醒 {build.ReminderCount} 条",
			Skipped = build.Skipped,
		};
	}

	/// <summary>
	/// 取回云端存档并恢复到本机。
	///
	/// 恢复的语义是**合并，不删除**（见 <see cref="CloudSaveService"/>）：本机有、存档
	/// 里没有的记忆与提醒会留下。配置是例外，范围内的键直接覆盖。
	/// </summary>
	public async Task<CloudSyncResult> RestoreAsync(CancellationToken cancel = default)
	{
		if (session.Current is not {} account)
		{
			return new CloudSyncResult {Message = "尚未登录 Nori 账户"};
		}

		CloudSaveSnapshot snapshot = await cloud.FetchSaveAsync(account.Token, metaOnly: false, cancel);
		if (!snapshot.Ok)
		{
			if (snapshot.Expired) DropExpiredSession();
			return new CloudSyncResult {Message = snapshot.Error};
		}
		if (!snapshot.Present) return new CloudSyncResult {Message = "云端还没有存档"};
		if (snapshot.Document is null)
		{
			// 服务端说有，但拿回来的解析不了。覆盖是唯一的出路，所以直说。
			return new CloudSyncResult {Message = "云端存档无法解析，可以用本机数据覆盖它"};
		}

		CloudRestoreResult restored = saves.Restore(snapshot.Document);
		if (!restored.Succeeded) return new CloudSyncResult {Message = restored.Error};

		// 恢复之后本机这份就等同于云端那一版，版本号要跟上 —— 不跟的话下一次上传
		// 会拿着旧号去撞，得到一个假的冲突。
		KnownRevision = snapshot.Revision;

		return new CloudSyncResult
		{
			Ok = true,
			Message = $"已恢复：偏好 {restored.ConfigApplied} 项、新增记忆 {restored.MemoriesAdded} 条、"
				+ $"提醒新增 {restored.RemindersAdded} 条并更新 {restored.RemindersUpdated} 条",
			Skipped = restored.Skipped,
		};
	}

	/// <summary>
	/// 删除云端存档。本机数据不动。
	///
	/// 版本号一并清零：不清的话下次上传会带着一个云端已经不存在的版本号，被判成冲突。
	/// </summary>
	public async Task<CloudSyncResult> ForgetAsync(CancellationToken cancel = default)
	{
		if (session.Current is not {} account)
		{
			return new CloudSyncResult {Message = "尚未登录 Nori 账户"};
		}

		CloudSaveUploadResult removed = await cloud.DeleteSaveAsync(account.Token, cancel);
		if (!removed.Ok)
		{
			if (removed.Expired) DropExpiredSession();
			return new CloudSyncResult {Message = removed.Error};
		}

		KnownRevision = 0;
		return new CloudSyncResult {Ok = true, Message = "云端存档已删除。本机数据未改动。"};
	}

	/// <summary>
	/// 服务端说令牌不作数了，清掉本机这份登录记录。
	///
	/// 只清会话，**不清版本号**：那个号记的是「本机与云端存档比对到哪一版」，与是谁登录
	/// 无关；同一个账户重新登录之后它仍然成立（换账户登录时由 <see cref="AccountSession.Save"/>
	/// 清零）。
	///
	/// 清完之后调用方要发一次快照失效，界面才会跟着变。桌面端这条路径上的调用方是
	/// BridgeCommands 的云同步命令，它本来就会发。
	/// </summary>
	private bool DropExpiredSession()
	{
		session.Clear();
		return true;
	}

	/// <summary>
	/// 本机知道的云端版本号。
	///
	/// 存成带前缀的字符串而不是整数，理由和 <see cref="AccountSession"/> 里那个前缀一样：
	/// <see cref="ConfigValue.FromStorage"/> 读取时重新推断类型，而 <c>"1"</c> 会被推断成
	/// **布尔真**，再读出来是 <c>"true"</c>，解析失败退回 0。
	///
	/// 而 1 正是第一次上传之后的版本号。不加前缀的话，症状是：第一次上传成功，之后每一次
	/// 上传都带着 0 号版本去撞，被服务端判成冲突 —— 用户看到的是「云端已有更新的存档」，
	/// 而那份更新的存档正是他自己刚存的。
	/// </summary>
	private int KnownRevision
	{
		get => KnownRevisionOf(config);
		set => config.Set(RevisionKey, new ConfigValue.Text("r" + value.ToString(
			System.Globalization.CultureInfo.InvariantCulture)));
	}

	/// <summary>
	/// 只读取版本号，不需要整个同步服务。
	///
	/// 给状态快照用：那条路径上没有 HTTP 客户端，也不该为了显示一个数字建一个。
	/// </summary>
	public static int KnownRevisionOf(ConfigStore config)
	{
		string raw = config.GetStringOr(RevisionKey, "");
		return raw.StartsWith('r') && int.TryParse(raw[1..], out int value) && value >= 0 ? value : 0;
	}
}
