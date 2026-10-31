using Nori.Desktop.Audio.Linux;

namespace Nori.Desktop.Tests;

public sealed class AlsaRecoveryTests
{
	[Theory]
	[InlineData(-4)]
	[InlineData(-19)]
	public void 中断直接重试且设备丢失不尝试恢复(int error)
	{
		int result = AlsaPcm.RecoverError(error,
			() => throw new InvalidOperationException("不应恢复挂起"),
			() => throw new InvalidOperationException("不应重新准备"));

		Assert.Equal(error == -4 ? 0 : error, result);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-19)]
	public void 缓冲欠载或溢出重新准备并保留失败(int prepareResult)
	{
		int prepares = 0;
		int result = AlsaPcm.RecoverError(-32,
			() => throw new InvalidOperationException("不应恢复挂起"),
			() => { prepares++; return prepareResult; });

		Assert.Equal(prepareResult, result);
		Assert.Equal(1, prepares);
	}

	[Theory]
	[InlineData(0, 0, 0, 0)]
	[InlineData(-11, 0, -11, 0)]
	[InlineData(-38, 0, 0, 1)]
	[InlineData(-38, -19, -19, 1)]
	public void 挂起只尝试一次恢复且暂不可恢复时不丢弃缓冲(int resumeResult, int prepareResult, int expected, int expectedPrepares)
	{
		int resumes = 0;
		int prepares = 0;
		int result = AlsaPcm.RecoverError(-86,
			() => { resumes++; return resumeResult; },
			() => { prepares++; return prepareResult; });

		Assert.Equal(expected, result);
		Assert.Equal(1, resumes);
		Assert.Equal(expectedPrepares, prepares);
	}
}
