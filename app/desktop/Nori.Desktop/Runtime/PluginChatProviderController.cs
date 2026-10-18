using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Nori.Core.Chat;
using Nori.PluginRuntime;

namespace Nori.Desktop.Runtime;

/// <summary>将插件提供商注册进当前聊天服务；撤销时取消请求、释放客户端并移除插件委托。</summary>
internal sealed class PluginChatProviderController : IDisposable
{
	private readonly object _gate = new();
	private readonly PluginRuntimeHost _runtime;
	private readonly CustomChatProviderRegistry _providers;
	private readonly Action<Exception> _report;
	private readonly Dictionary<string, ProviderLease> _leases = new(StringComparer.Ordinal);
	private bool _disposed;

	internal PluginChatProviderController(PluginRuntimeHost runtime, CustomChatProviderRegistry providers, Action<Exception> report)
	{
		_runtime = runtime;
		_providers = providers;
		_report = report;
		_runtime.ContributionsChanged += Reconcile;
		Reconcile();
	}

	private void Reconcile()
	{
		lock (_gate)
		{
			if (_disposed) return;
			Dictionary<string, IPluginChatProviderContribution> active = _runtime.GetContributionsWithSource<IPluginChatProviderContribution>()
				.ToDictionary(item => $"plugin:{item.Plugin.Id}:{item.Contribution.Id}", item => item.Contribution, StringComparer.Ordinal);
			foreach (string id in _leases.Keys.Where(id => !active.TryGetValue(id, out IPluginChatProviderContribution? current) || !_leases[id].Owns(current)).ToArray())
			{
				_providers.Unregister(id);
				_leases.Remove(id, out ProviderLease? lease);
				lease?.Dispose();
			}
			foreach ((string id, IPluginChatProviderContribution contribution) in active)
			{
				if (_leases.ContainsKey(id)) continue;
				ProviderLease lease = new(contribution, _report);
				try
				{
					_providers.Register(new CustomChatProviderDescriptor(id, contribution.Name, contribution.DefaultBaseUrl, contribution.RequiresApiKey),
						lease.CreateClient, lease.GetModelsAsync);
					_leases.Add(id, lease);
				}
				catch (Exception exception)
				{
					lease.Dispose();
					_report(exception);
				}
			}
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_runtime.ContributionsChanged -= Reconcile;
			foreach ((string id, ProviderLease lease) in _leases)
			{
				_providers.Unregister(id);
				lease.Dispose();
			}
			_leases.Clear();
		}
	}

	private sealed class ProviderLease(IPluginChatProviderContribution contribution, Action<Exception> report) : IDisposable
	{
		private readonly object _gate = new();
		private readonly CancellationTokenSource _stopping = new();
		private readonly HashSet<OwnedClient> _clients = [];
		private IPluginChatProviderContribution? _contribution = contribution;

		internal bool Owns(IPluginChatProviderContribution current)
		{
			lock (_gate) return ReferenceEquals(_contribution, current);
		}

		internal IChatClient CreateClient(string baseUrl, string apiKey, string model)
		{
			lock (_gate)
			{
				IPluginChatProviderContribution provider = _contribution ?? throw new ChatException("插件 AI 提供商已停用");
				IChatClient inner = provider.CreateClient(new PluginChatClientOptions(baseUrl, apiKey, model))
					?? throw new ChatException("插件 AI 提供商返回了空客户端");
				OwnedClient client = new(inner, _stopping.Token, Remove);
				_clients.Add(client);
				return client;
			}
		}

		internal async Task<IReadOnlyList<string>> GetModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
		{
			IPluginChatProviderContribution provider;
			CancellationToken stopping;
			lock (_gate)
			{
				provider = _contribution ?? throw new ChatException("插件 AI 提供商已停用");
				stopping = _stopping.Token;
			}
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
			linked.Token.ThrowIfCancellationRequested();
			IReadOnlyList<string> models = await provider.GetModelsAsync(new PluginChatClientOptions(baseUrl, apiKey, ""), linked.Token).ConfigureAwait(false);
			linked.Token.ThrowIfCancellationRequested();
			return models.Where(model => !string.IsNullOrWhiteSpace(model) && model.Length <= 256 && !model.Any(char.IsControl)).Distinct(StringComparer.Ordinal).Take(1000).ToArray();
		}

		private void Remove(OwnedClient client)
		{
			lock (_gate) _clients.Remove(client);
		}

		public void Dispose()
		{
			OwnedClient[] clients;
			lock (_gate)
			{
				if (_contribution is null) return;
				_contribution = null;
				clients = _clients.ToArray();
				_clients.Clear();
			}
			try { _stopping.Cancel(); }
			catch (Exception exception) { report(exception); }
			foreach (OwnedClient client in clients)
			{
				try { client.Dispose(); }
				catch (Exception exception) { report(exception); }
			}
			_stopping.Dispose();
		}
	}

	private sealed class OwnedClient(IChatClient inner, CancellationToken stopping, Action<OwnedClient> removed) : IChatClient
	{
		private IChatClient? _inner = inner;

		public async Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
		{
			IChatClient client = Volatile.Read(ref _inner) ?? throw new ChatException("插件 AI 客户端已释放");
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
			linked.Token.ThrowIfCancellationRequested();
			return await client.GetResponseAsync(messages, options, linked.Token).ConfigureAwait(false);
		}

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			IChatClient client = Volatile.Read(ref _inner) ?? throw new ChatException("插件 AI 客户端已释放");
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
			linked.Token.ThrowIfCancellationRequested();
			await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options, linked.Token).ConfigureAwait(false))
			{
				linked.Token.ThrowIfCancellationRequested();
				yield return update;
			}
		}

		public object? GetService(Type serviceType, object? serviceKey = null) => Volatile.Read(ref _inner)?.GetService(serviceType, serviceKey);

		public void Dispose()
		{
			IChatClient? client = Interlocked.Exchange(ref _inner, null);
			if (client is null) return;
			removed(this);
			client.Dispose();
		}
	}
}
