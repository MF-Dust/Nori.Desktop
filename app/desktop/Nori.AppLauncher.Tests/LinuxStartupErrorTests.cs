using Nori.Startup;

namespace Nori.AppLauncher.Tests;

public sealed class LinuxStartupErrorTests
{
	[Theory]
	[InlineData("zenity")]
	[InlineData("kdialog")]
	public void 错误文本只作为参数而不是Shell脚本(string tool)
	{
		const string title = "启动失败";
		const string message = "路径含空格、引号 \" 与 $(touch /tmp/not-a-command)";
		var command = LinuxStartupError.CreateCommand(tool, title, message);
		Assert.False(command.UseShellExecute);
		Assert.Equal(tool, command.FileName);
		Assert.Equal(message, command.ArgumentList[^1]);
		Assert.Contains(title, command.ArgumentList);
		Assert.Equal(tool == "zenity", command.ArgumentList.Contains("--no-markup"));
	}
}
