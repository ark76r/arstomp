using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using WebSocketSharp;
using WebSocketSharp.Net;

namespace ArStomp
{
	/// <summary>
	/// Stomp client
	/// </summary>
	public class StompClient
	{
		private int heartBeatInMillis;
		public static bool Debug { get; set; } = false;
		private WebSocket ws;
		private volatile bool stompConnected = false;
		public CancellationTokenSource Token { get; } = new CancellationTokenSource();
		private readonly X509Certificate2Collection certCollection;
		private readonly Dictionary<string, Subscription> subs = new Dictionary<string, Subscription>();
		private DateTime lastSend = DateTime.UtcNow;
		private DateTime lastRcvd = DateTime.UtcNow;
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
		public StompClient(X509Certificate2Collection certCollection = null, int heartBeatSec = 20)
		{
			this.heartBeatInMillis = heartBeatSec * 1000;
			if (certCollection != null && certCollection.Count > 0)
			{
				this.certCollection = certCollection;
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
			if (Debug)
			{
				System.Console.WriteLine("Subject: {0}", certificate.Subject.ToString());
				System.Console.WriteLine("Cert: {0}", certificate.ToString());
			}
			// if there is no detected problems we can say OK
			if ((sslPolicyErrors & (SslPolicyErrors.None)) > 0)
			{
				if (Debug) System.Console.WriteLine("Cert OK: ((sslPolicyErrors & (SslPolicyErrors.None)) > 0)");
				return true;
			}
			// sins that cannot be forgiven
			if (
				(sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNameMismatch)) > 0 ||
				(sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable)) > 0
			)
			{
				if (Debug) System.Console.WriteLine("Cert Fail: (sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNameMismatch)) > 0 - {0}", (sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNameMismatch)) > 0);
				if (Debug) System.Console.WriteLine("Cert Fail: (sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable)) > 0 - {0}", (sslPolicyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable)) > 0);
				return false;
			}
			if (Debug)
			{
				System.Console.WriteLine("Chain:");
				foreach (var ce in chain.ChainElements)
				{
					System.Console.WriteLine("Element: {0}", ce.Certificate);
				}
			}
			// last certificate in chain should be one of our trust anchors
			X509Certificate2 projectedRootCert = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
			// check if server's root ca is one of our trusted
			bool anytrusted = false;
			foreach (var cert in certCollection)
			{
				if (Debug) System.Console.WriteLine("Anytrust: {0}, {1} =? {2}", projectedRootCert.Thumbprint.ToString(), cert.Thumbprint.ToString(), (projectedRootCert.Thumbprint == cert.Thumbprint));
				anytrusted = anytrusted || (projectedRootCert.Thumbprint == cert.Thumbprint);
			}
			if (!anytrusted)
			{
				if (Debug) System.Console.WriteLine("Cert Fail: (!anytrusted)");
				return false;
			}
			// any other problems than unknown CA?
			if (chain.ChainStatus.Any(statusFlags => statusFlags.Status != X509ChainStatusFlags.UntrustedRoot))
			{
				if (Debug) System.Console.WriteLine("Cert Fail: chain.ChainStatus.Any(statusFlags => statusFlags.Status != X509ChainStatusFlags.UntrustedRoot)");
				return false;
			}
			// everything OK
			if (Debug) Console.WriteLine("Certificate OK");
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
						reason = Encoding.UTF8.GetString(frame.Body.Array, frame.Body.Offset, frame.Body.Count);
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
			if (ws != null) throw new Exception("Cannot connect in this state. Should close before");
			ws = new WebSocket(uri.ToString(), "v12.stomp");
			if (uri.Scheme == "wss")
			{
				if (certCollection != null)
				{
					ws.SslConfiguration.ServerCertificateValidationCallback = RemoteCertificateValidationCallback;
				}
				ws.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
			}
			var ct = Token.Token;
			TaskCompletionSource<bool> openTask = new TaskCompletionSource<bool>();

			ws.OnClose += (sender, o) =>
			{
				if (Debug) System.Console.WriteLine("OnClose: code: {0}, wasClean: {1}, reason: {2}", o.Code, o.WasClean, o.Reason);
				Token.Cancel();
			};

			ws.OnError += (sender, o) =>
			{
				if (Debug) System.Console.WriteLine("OnError: {0}, {1}", o.Message, o.Exception);
				Token.Cancel();
			};

			ws.OnOpen += (sender, o) =>
			{
				if (Debug) System.Console.WriteLine("OnOpen");
				StompFrm connect = new StompFrm(login, password, heartBeatInMillis);
				lastSend = DateTime.UtcNow;
				Task.Run(() => { return connect.Serialize(ws, ct); });
			};

			ws.OnMessage += (sender, o) =>
			{
				lastRcvd = DateTime.UtcNow;
				try
				{
					if (Debug)
					{
						string type = o.IsBinary ? "Binary" : (o.IsText ? "Text" : (o.IsPing ? "Ping" : "Unknown"));
						string value = o.IsBinary ? $"byte[{o.RawData.Length}]" : (o.IsText ? o.Data : "");
						System.Console.WriteLine("OnMessage: type: {0} data: {1}", type, value);
					}
					if (!stompConnected)
					{
						Frame fr = Helpers.GetFrame(o.RawData, ct);
						stompConnected = true;
						try
						{
							ExpectFrame(fr, FrameType.Connected);
							openTask.TrySetResult(true);
							if (Debug) System.Console.WriteLine("Logon done");
						}
						catch (Exception e)
						{
							openTask.TrySetException(e);
						}
						return;
					}

					ProcessMessage(o);
				}
				catch (Exception e)
				{
					Console.WriteLine("onMessage: Wyjątek {0}", e);
				}


			};

			ws.Connect();
			CancellationTokenSource cts = new CancellationTokenSource(5000); // 5 seconds timeout
			cts.Token.Register(() => openTask.TrySetCanceled(), useSynchronizationContext: false);
			await openTask.Task;
			if (Debug) System.Console.WriteLine("STOMP Connected");
		}
		/// <summary>
		/// Reports state of conection
		/// </summary>
		/// <returns>true if it looks like we have proper connection to server</returns>
		public bool IsConnected()
		{
			return ws != null && ws.IsAlive && (DateTime.UtcNow - lastRcvd) < TimeSpan.FromMilliseconds(3 * heartBeatInMillis);
		}
		/// <summary>
		/// Send message
		/// </summary>
		/// <param name="destination">queue o exchange (eg /exchange/name/routing-key in case of RabbitMQ)</param>
		/// <param name="correlationId">property correlationId for the message</param>
		/// <param name="body">content of the message</param>
		public async Task Send(string destination, string correlationId, byte[] body)
		{
			var ct = Token.Token;

			SendFrm send = new SendFrm(destination, correlationId, body);
			await send.Serialize(ws, ct);
			lastSend = DateTime.UtcNow;
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

		private void ProcessMessage(WebSocketSharp.MessageEventArgs msg)
		{
			var ct = Token.Token;
			try
			{
				Frame fr = null;
				try
				{
					fr = Helpers.GetFrame(msg.RawData, ct);
				}
				catch (Exception e)
				{
					throw e;
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
							else Console.WriteLine("Unexpected message {0}", fr);

						}
					}
				}
				if (fr.Type == FrameType.Heartbeat || DateTime.UtcNow - lastSend > TimeSpan.FromSeconds(20))
				{
					lastSend = DateTime.UtcNow;
					ws.Send(new byte[] { 10 }); // send heartbeat
				}


			}
			catch (Exception e)
			{
				System.Console.WriteLine("Excpetion: {0}", e);
			}
		}
		/// <summary>
		/// Cancel current operaton and close connection
		/// </summary>
		/// <returns></returns>
		public Task Close()
		{
			try
			{
				Token.Cancel();
				var ct = new CancellationTokenSource().Token;
				ws.Close();
				stompConnected = false;
				ws = null;
			}
			catch
			{
				// skip
			}
			return Task.CompletedTask;
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
