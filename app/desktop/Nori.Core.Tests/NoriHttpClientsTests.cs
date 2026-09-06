using System.Net;
using Nori.Core.Network;

namespace Nori.Core.Tests;

[Collection("HttpClient.DefaultProxy")]
public sealed class NoriHttpClientsTests
{
	[Fact]
	public async Task 默认公网客户端不读取系统代理()
	{
		IWebProxy originalProxy = HttpClient.DefaultProxy;
		ThrowingProxy proxy = new();
		HttpClient.DefaultProxy = proxy;
		try
		{
			using NoriHttpClients clients = NoriHttpClients.Create(
				allowInsecureTls: false, timeout: TimeSpan.FromSeconds(2));
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
			Exception? exception = await Record.ExceptionAsync(async () =>
			{
				using HttpResponseMessage response = await clients.Public.GetAsync(
					"http://nori-network-test.invalid/", timeout.Token);
			});

			Assert.NotNull(exception);
			Assert.Equal(0, proxy.CallCount);
		}
		finally
		{
			HttpClient.DefaultProxy = originalProxy;
		}
	}

	[Fact]
	public async Task 显式允许公网系统代理时使用代理()
	{
		IWebProxy originalProxy = HttpClient.DefaultProxy;
		CountingProxy proxy = new();
		HttpClient.DefaultProxy = proxy;
		try
		{
			using NoriHttpClients clients = NoriHttpClients.Create(
				allowInsecureTls: false,
				timeout: TimeSpan.FromSeconds(2),
				publicUseSystemProxy: true);
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
			Exception? exception = await Record.ExceptionAsync(async () =>
			{
				using HttpResponseMessage response = await clients.Public.GetAsync(
					"http://nori-network-test.invalid/", timeout.Token);
			});

			Assert.NotNull(exception);
			Assert.True(proxy.CallCount > 0);
		}
		finally
		{
			HttpClient.DefaultProxy = originalProxy;
		}
	}

	private sealed class CountingProxy : IWebProxy
	{
		public int CallCount { get; private set; }

		public Uri GetProxy(Uri destination)
		{
			CallCount++;
			return new Uri("http://127.0.0.1:1");
		}

		public bool IsBypassed(Uri host)
		{
			CallCount++;
			return false;
		}

		public ICredentials? Credentials { get; set; }
	}

	private sealed class ThrowingProxy : IWebProxy
	{
		public int CallCount { get; private set; }

		public Uri GetProxy(Uri destination)
		{
			CallCount++;
			throw new InvalidOperationException("不应查询系统代理");
		}

		public bool IsBypassed(Uri host)
		{
			CallCount++;
			throw new InvalidOperationException("不应查询系统代理");
		}

		public ICredentials? Credentials { get; set; }
	}
}
