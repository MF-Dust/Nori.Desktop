using Nori.Core.Configuration;
using Nori.Core.Data;
using Nori.Core.Tests.TestSupport;

namespace Nori.Core.Tests;

public class UiLanguageTests : IDisposable
{
	private readonly TempDatabase _temp = new("nori-lang");
	private readonly NoriDatabase _database;
	private readonly ConfigStore _config;

	public UiLanguageTests()
	{
		_database = NoriDatabase.Open(_temp.Path);
		_config = new ConfigStore(_database, new FixedKeyStore());
	}

	public void Dispose()
	{
		_config.Dispose();
		_database.Dispose();
		_temp.Dispose();
	}

	[Theory]
	[InlineData(null, false)]
	[InlineData("", false)]
	[InlineData("zh-CN", false)]
	[InlineData("en", true)]
	[InlineData("en-US", true)]
	[InlineData("EN-us", true)]
	[InlineData("en-GB", true)]
	public void 英文以前缀判断且忽略大小写(string? language, bool english) =>
		Assert.Equal(english, UiLanguage.IsEnglish(language));

	[Fact]
	public void 配置缺省为中文写入en前缀后为英文()
	{
		Assert.False(UiLanguage.IsEnglish(_config));
		_config.Set(ConfigStore.KeyLanguage, new ConfigValue.Text("en-GB"));
		Assert.True(UiLanguage.IsEnglish(_config));
	}
}
