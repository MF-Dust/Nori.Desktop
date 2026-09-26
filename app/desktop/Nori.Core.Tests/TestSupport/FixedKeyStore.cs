using Nori.Core.Security;

namespace Nori.Core.Tests.TestSupport;

/// <summary>测试用固定主密钥，不触碰用户真实数据目录。</summary>
internal sealed class FixedKeyStore : ISecretKeyStore
{
	private readonly byte[] _key = Enumerable.Range(0, SecretKeyStore.KeySize).Select(index => (byte)index).ToArray();

	public byte[] LoadOrCreate() => _key;

	public bool IsFileFallback => true;
}
