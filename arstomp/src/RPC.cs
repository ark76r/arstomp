using System;
using System.Threading.Tasks;
using System.Timers;
using System.Collections.Concurrent;

/// <summary>
/// Minimal implementation of stomp client
/// </summary>
namespace ArStomp
{
	/// <summary>
	/// Simple helper implementing Remote Procedure Call on stomp.
	/// </summary>
	public class RPC : IDisposable
	{
		private readonly ConcurrentDictionary<string, Request> requests = new ConcurrentDictionary<string, Request>(5, 10);
		private Timer timer;

		private readonly StompClient client;
		/// <summary>
		/// Creates new RPC layer fror given stomp client
		/// </summary>
		/// <param name="client">initialized and connected stomp client</param>
		public RPC(StompClient client)
		{
			this.client = client;
			client.OnMessage += this.OnMessage;
			timer = new Timer(3000);
			timer.Elapsed += OnTimer;
			timer.Start();
		}
		/// <summary>
		/// Calles remote method
		/// </summary>
		/// <param name="destination">destination, where request is sent</param>
		/// <param name="body">content of the message</param>
		/// <param name="timeout">how long we're going to wait for response (default is 15s)</param>
		/// <returns>response for our call (<see cref="Frame"/> of returned message)</returns>
		/// <exception cref="TimeoutException">if there is no reponse after timeout</exception>
		public async Task<Frame> Call(string destination, byte[] body, TimeSpan timeout = default)
		{
			string correlationId = Guid.NewGuid().ToString();
			TimeSpan to = (timeout != default) ? timeout : TimeSpan.FromSeconds(15);
			var req = new Request(correlationId, to);
			if (!requests.TryAdd(correlationId, req))
				throw new Exception("Request with this cid is already processed");
			await client.Send(destination, correlationId, body);
			return await req.Source.Task;
		}

		public void Dispose()
		{
			if (timer != null)
			{
				var t = timer;
				timer = null;
				client.OnMessage -= this.OnMessage;
				t.Stop();
				t.Dispose();
			}
		}

		private void OnTimer(object sender, ElapsedEventArgs ea)
		{
			var ts = DateTime.UtcNow;
			foreach (var i in requests)
			{
				if (i.Value.Timeout < ts)
				{
					if (requests.TryRemove(i.Key, out Request req))
					{
						req.Source.SetException(new TimeoutException());
					}
				}
			}
		}

		private void OnMessage(object sender, SubscriptionEventArgs ea)
		{
			var cid = ea.Frame.Headers["correlation-id"];
			if (requests.TryRemove(cid, out Request req))
			{
				req.Source.SetResult(ea.Frame);
			}
			// else skip unknown message
		}

	}

	internal sealed class Request
	{
		public readonly DateTime Timeout;
		public readonly string Cid;
		public TaskCompletionSource<Frame> Source { get; } = new TaskCompletionSource<Frame>();
		public Request(string cid, TimeSpan waitingTime)
		{
			this.Cid = cid;
			this.Timeout = DateTime.UtcNow + waitingTime;
		}
	}
}
