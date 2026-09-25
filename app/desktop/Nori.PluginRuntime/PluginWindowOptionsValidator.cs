namespace Nori.PluginRuntime;

/// <summary>插件 WebView 窗口参数的唯一校验入口。</summary>
internal static class PluginWindowOptionsValidator
{
	public static void Validate(PluginWebViewOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		PluginWindowHost.ValidateId(options.Id, nameof(options.Id));
		if (string.IsNullOrWhiteSpace(options.Title) || options.Title.Any(char.IsControl))
			throw new ArgumentException("插件窗口标题无效", nameof(options));
		if (string.IsNullOrWhiteSpace(options.EntryPoint) || options.EntryPoint.Any(char.IsControl))
			throw new ArgumentException("插件窗口入口无效", nameof(options));
		if (options.EntryPoint.StartsWith("//", StringComparison.Ordinal) || options.EntryPoint.Contains('\\'))
			throw new ArgumentException("插件窗口入口必须是同源 HTTP(S) 地址或相对路径", nameof(options));
		if (Uri.TryCreate(options.EntryPoint, UriKind.Absolute, out Uri? absolute)
			&& (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps
				|| !absolute.IsLoopback
				|| absolute.UserInfo.Length > 0
				|| absolute.Fragment.Length > 0))
		{
			throw new ArgumentException("插件窗口入口不能离开宿主回环资源服务", nameof(options));
		}
		if (double.IsNaN(options.Width) || double.IsInfinity(options.Width) || options.Width <= 0 ||
			double.IsNaN(options.Height) || double.IsInfinity(options.Height) || options.Height <= 0)
			throw new ArgumentException("插件窗口尺寸无效", nameof(options));
	}
}
