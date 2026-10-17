namespace Nori.Startup;

/// <summary>本地启动管道只传固定阶段与外观，不传用户内容或凭据。</summary>
internal sealed record SplashMessage(
	int Progress = 0,
	string Stage = "",
	bool Done = false,
	bool? Enabled = null,
	string Theme = "character",
	string Background = "",
	string Version = "Dev");
