using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;


namespace ArStomp
{
	/// <summary>
	/// Possible STOMP frame types. Not everything is used in this library
	/// </summary>
	public enum FrameType
	{
		// no type
		Unknown = 0,
		// server frames
		Connected,
		Message,
		Receipt,
		Error,

		// client frames
		Stomp,
		Send,
		Subscribe,
		Unsubscribe,
		Ack,
		Nack,
		Begin,
		Commit,
		Abort,
		Disconnect,
		Heartbeat // fake, but gives common view of server messages
	}
	/// <summary>
	/// Represents the frame of STOMP protocol: message, command or error
	/// </summary>
	public class Frame
	{
		/// <summary>
		/// Type of frame
		/// </summary>
		public FrameType Type { get; internal set; }
		/// <summary>
		/// Headers from STOMP frame
		/// </summary>
		/// <typeparam name="string">header's name</typeparam>
		/// <typeparam name="string">header's value</typeparam>
		public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();
		/// <summary>
		/// Content (body) of the message.
		/// Not parsed and not processed in any way.
		/// </summary>s
		public ArraySegment<byte> Body { get; internal set; }

		public override string ToString()
		{
			StringBuilder sb = new StringBuilder();
			sb.Append(Helpers.GetCmdString(Type)).Append("\n");
			foreach (var i in Headers)
			{
				sb.AppendFormat("{0}:{1}\n", i.Key, i.Value);
			}
			sb.AppendFormat("Body size: {0}", Body.Count);
			return sb.ToString();
		}

		internal ValueTask Serialize(ClientWebSocket ws, CancellationToken cancellationToken)
		{
			var utf8 = Encoding.UTF8;
			var EOL = utf8.GetBytes("\n");
			var COLON = utf8.GetBytes(":");
			var NUL = new byte[] { 0 };
			var stream = new MemoryStream();

			// write command
			var cmd = Helpers.GetCmdString(Type);
			stream.Write(utf8.GetBytes(cmd));
			stream.Write(EOL);
			// write headers
			foreach (var i in Headers)
			{
				stream.Write(utf8.GetBytes(i.Key));
				stream.Write(COLON);
				stream.Write(utf8.GetBytes(i.Value));
				stream.Write(EOL);
			}
			// write empty line
			stream.Write(EOL);
			// write body
			if (Body != null && Body.Count > 0)
			{
				stream.Write(Body);
			}
			// write NUL character
			stream.Write(NUL);
			stream.Flush();
			var array = stream.GetBuffer();
			if (StompClient.Debug) Console.WriteLine(">>>\n{0}\n>>>\n", this);
			return ws.SendAsync(array.AsMemory(0, (int)stream.Position), WebSocketMessageType.Binary, true, cancellationToken);
		}
	}

	internal class StompFrm : Frame
	{
		public StompFrm(string login, string passwd)
		{
			Type = FrameType.Stomp;
			Headers["login"] = login;
			Headers["passcode"] = passwd;
			Headers["accept-version"] = "1.2";
		}
	}

	internal class SendFrm : Frame
	{
		public SendFrm(string destination, string correlationId, byte[] body)
		{
			Type = FrameType.Send;
			Headers["destination"] = destination;
			Headers["reply-to"] = "/temp-queue/rpc-replies";
			if (correlationId != null) Headers["correlation-id"] = correlationId;
			Headers["content-length"] = body.Length.ToString();
			Body = body;
		}
	}

	internal class SubscribeFrame : Frame
	{
		public SubscribeFrame(string id, string destination)
		{
			Type = FrameType.Subscribe;
			Headers["destination"] = destination;
			Headers["id"] = id;
		}
	}

	internal class UnsubscribeFrame : Frame
	{
		public UnsubscribeFrame(string id)
		{
			Type = FrameType.Unsubscribe;
			Headers["id"] = id;
		}
	}

}
