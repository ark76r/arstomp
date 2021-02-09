using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArStomp
{
	/// <summary>
	/// Stomp client
	/// </summary>
	public class StompClient
	{
		private Task runner = null;
		public static bool Debug { get; set; } = false;
		private ClientWebSocket ws = new ClientWebSocket();
		public CancellationTokenSource Token { get; } = new CancellationTokenSource();
		private readonly X509Certificate2Collection certCollection;
		private readonly Dictionary<string, Subscription> subs = new Dictionary<string, Subscription>();
		/// <summary>
		/// Handler of incoming messages.
		/// Used only for RPC. <see cref="Subscription">Subsscriptions</see> have own handlers.
		/// </summary>
		public event EventHandler<SubscriptionEventArgs> OnMessage;
		/// <summary>
		/// Creates new object.
		/// Supports RabbitMQ.
		/// Works with binary frames.
		/// </summary>
		/// <param name="certCollection">collection of root ca certificates (if TLS is used)</param>
		public StompClient(X509Certificate2Collection certCollection = null)
		{

			ws = new ClientWebSocket();
			ws.Options.AddSubProtocol("v12.stomp");
			if (certCollection != null && certCollection.Count > 0)
			{
				this.certCollection = certCollection;
				ws.Options.RemoteCertificateValidationCallback = RemoteCertificateValidationCallback;
			}
		}

		internal void InvokeOnMessage(SubscriptionEventArgs args)
		{
			try
			{
				OnMessage?.Invoke(this, args);
			}
			catch
			{
				// skip all exceptions
			}
		}

		/// <summary>
		/// Verify cert chain in case of using own (custom) ca certificates for TLS
		/// </summary>
		/// <param name="sender">tls stream</param>
		/// <param name="certificate">server's certificate</param>
		/// <param name="chain">constructed chain of trust</param>
		/// <param name="sslPolicyErrors">detected errors</param>
		/// <returns>true if server certificate is valid, false otherwise</returns>
		private bool RemoteCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
		{
			// if there is no detected problems we can say OK
			if ((sslPolicyErrors & (SslPolicyErrors.None)) > 0) return true;
			// sins that cannot be forgiven
			if (
				(sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNameMismatch)) > 0 ||
				(sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable)) > 0
			) return false;
			// last certificate in chain should be one of our trust anchors
			X509Certificate2 projectedRootCert = chain.ChainElements[^1].Certificate;
			// check if server's root ca is one of our trusted
			bool anytrusted = false;
			foreach (var cert in certCollection)
			{
				anytrusted = anytrusted || (projectedRootCert.Thumbprint == cert.Thumbprint);
			}
			if (!anytrusted) return false;
			// any other problems than unknown CA?
			if (chain.ChainStatus.Any(statusFlags => statusFlags.Status != X509ChainStatusFlags.UntrustedRoot)) return false;
			// everything OK
			return true;
		}

		private void ExpectFrame(Frame frame, FrameType expected)
		{
			if (frame.Type != expected)
			{
				var reason = "Unknown reason";
				if (frame.Type == FrameType.Error)
				{
					if (frame.Body != null)
					{
						reason = Encoding.UTF8.GetString(frame.Body);
					}
				}
				throw new Exception($"Unexpected frame '{frame.Type}'. Message from server: {reason}");
			}
		}
		/// <summary>
		/// Connects to stomp server.
		/// Uses <see cref="ClientWebSocket"/>.
		/// </summary>
		/// <param name="uri">uri in format ws://host[:port][/path] or wss://host[:port][/path]</param>
		/// <param name="login">login name</param>
		/// <param name="password">password</param>
		public async Task Connect(Uri uri, string login, string password)
		{
			if (runner != null) throw new Exception("Cannot connect in this state. Should close before");
			var ct = Token.Token;
			await ws.ConnectAsync(uri, ct);

			StompFrm connect = new StompFrm(login, password);
			await connect.Serialize(ws, ct);

			Frame fr = await Helpers.GetFrame(ws, ct);

			ExpectFrame(fr, FrameType.Connected);
			runner = Run(); // Run is async
		}
		/// <summary>
		/// Reports state of conection
		/// </summary>
		/// <returns>true if it looks like we have proper connection to server</returns>
		public bool IsConnected()
		{
			return ws.CloseStatus == null;
		}
		/// <summary>
		/// Send message
		/// </summary>
		/// <param name="destination">queue o exchange (eg /exchange/name/routing-key in case of RabbitMQ)</param>
		/// <param name="correlationId">property correlationId for the message</param>
		/// <param name="body">content of the message</param>
		public ValueTask Send(string destination, string correlationId, byte[] body)
		{
			var ct = Token.Token;

			SendFrm send = new SendFrm(destination, correlationId, body);
			return send.Serialize(ws, ct);
		}

		private int SubId = 0;
		/// <summary>
		/// Create news subscription
		/// </summary>
		/// <param name="destination">eg /exchange/name/rouring-key</param>
		/// <returns><see cref="Subscription"/> object</returns>
		public async Task<Subscription> Subscribe(string destination)
		{
			var ct = Token.Token;
			var id = $"sub-{++SubId}";
			var sub = new Subscription()
			{
				Destination = destination,
				Id = id
			};
			var sframe = new SubscribeFrame(id, destination);
			await sframe.Serialize(ws, ct);
			subs.Add(id, sub);
			return sub;
		}
		/// <summary>
		/// Cancel subscrption
		/// </summary>
		/// <param name="sub">object of subscription</param>
		public async Task Unsubscribe(Subscription sub)
		{
			if (subs.ContainsKey(sub.Id))
			{
				subs.Remove(sub.Id);

				var ct = Token.Token;
				var sframe = new UnsubscribeFrame(sub.Id);
				await sframe.Serialize(ws, ct);
			}
		}

		private async Task Run()
		{
			var ct = Token.Token;
			try
			{
				while (!ct.IsCancellationRequested)
				{
					Frame fr = null;
					try
					{
						fr = await Helpers.GetFrame(ws, ct);
					}
					catch (ThreadInterruptedException)
					{
						break;
					}
					if (fr.Type == FrameType.Error) ExpectFrame(fr, FrameType.Message);
					if (fr.Type == FrameType.Message)
					{
						if (fr.Headers.ContainsKey("subscription"))
						{
							if (fr.Headers["subscription"] == "/temp-queue/rpc-replies")
							{
								InvokeOnMessage(new SubscriptionEventArgs(fr));
							}
							else
							{
								var sub = subs[fr.Headers["subscription"]];
								if (sub != null) sub.InvokeOnMessage(new SubscriptionEventArgs(fr));
								else Console.WriteLine("Nieoczekiwany komunikat {0}", fr);

							}
						}
					}
				}
			}
			finally
			{
				await Close();
			}
		}
		/// <summary>
		/// Cancel current operaton and close connection
		/// </summary>
		/// <returns></returns>
		public async Task Close()
		{
			try
			{
				Token.Cancel();
				var ct = new CancellationTokenSource().Token;
				await ws.CloseAsync(WebSocketCloseStatus.Empty, null, ct);
			}
			catch
			{
				// skip
			}
		}
	}
	/// <summary>
	/// Represents the subscpriotn
	/// </summary>
	public class Subscription
	{
		/// <summary>
		/// Id of this particular subscription
		/// </summary>
		public string Id { get; internal set; }
		/// <summary>
		/// Destination used with this syubscription
		/// </summary>
		/// <value></value>
		public string Destination { get; internal set; }
		/// <summary>
		/// Handler fro incoming messages
		/// </summary>
		public event EventHandler<SubscriptionEventArgs> OnMessage;

		internal void InvokeOnMessage(SubscriptionEventArgs args)
		{
			try
			{
				OnMessage?.Invoke(this, args);
			}
			catch
			{
				// skip all exceptions
			}
		}
	}
	/// <summary>
	/// Arguments for OnMessage handlers
	/// </summary>
	public class SubscriptionEventArgs : EventArgs
	{
		/// <summary>
		/// The frame (MESSAGE) got from server
		/// </summary>
		/// <value></value>
		public Frame Frame { get; }

		internal SubscriptionEventArgs(Frame f)
		{
			Frame = f;
		}
	}
}
