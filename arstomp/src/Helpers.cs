using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;


namespace ArStomp
{
internal static class Helpers
	{
		/// <summary>
		/// Static instance of heartbeat frame
		/// </summary>
		private readonly static Frame HeartbeatFrame = new Frame() { Type = FrameType.Heartbeat };

		internal static readonly Dictionary<string, FrameType> CmdMap = new Dictionary<string, FrameType>()
		{
			{"CONNECTED", FrameType.Connected},
			{"ERROR", FrameType.Error},
			{"RECEIPT", FrameType.Receipt},
			{"MESSAGE", FrameType.Message},
			{"", FrameType.Heartbeat} // fake command
		};

		internal static string GetCmdString(FrameType type)
		{
			return type switch
			{
				FrameType.Unknown => "UNKNOWN",
				FrameType.Connected => "CONNECTED",
				FrameType.Message => "MESSAGE",
				FrameType.Receipt => "RECEIPT",
				FrameType.Error => "ERROR",
				FrameType.Stomp => "STOMP",
				FrameType.Send => "SEND",
				FrameType.Subscribe => "SUBSCRIBE",
				FrameType.Unsubscribe => "UNSUBSCRIBE",
				FrameType.Ack => "ACK",
				FrameType.Nack => "NACK",
				FrameType.Begin => "BEGIN",
				FrameType.Commit => "COMMIT",
				FrameType.Abort => "ABORT",
				FrameType.Disconnect => "DISCONNECT",
				FrameType.Heartbeat => "",
				_ => "UNKNOWN"
			};
		}
		private static async Task GetMessage(MemoryStream output, ClientWebSocket ws, CancellationToken cancellationToken)
		{
			var barray = new byte[128 * 1024];
			// read first frame
			ArraySegment<byte> buffer = new ArraySegment<byte>(barray);
			var result = await ws.ReceiveAsync(buffer, cancellationToken);

			if (result.CloseStatus != null)
			{
				throw new Exception($"Unexpected close: {result.CloseStatus}: {result.CloseStatusDescription}");
			}

			output.Write(barray, 0, result.Count);

			while (result.EndOfMessage != true)
			{
				buffer = new ArraySegment<byte>(barray);
				result = await ws.ReceiveAsync(buffer, cancellationToken);
				output.Write(barray, 0, result.Count);
			}
			output.Seek(0, SeekOrigin.Begin);
		}

		private static StreamReader findBody(Stream input)
		{
			var output = new MemoryStream();
			int count;
			// read headers
			do
			{
				// read one line
				count = 0;
				while (true)
				{
					var ch = input.ReadByte();
					if (ch == -1) throw new Exception("Unexpected end of data");
					byte b = (byte)(0xff & ch); // convert to byte
					if (b == 13) continue; //skip CR
					output.WriteByte(b);
					if (b != 10) // LF - end of line
					{
						count++; // chars in line
					}
					else
					{
						break;
					}
				}
			} while (count > 0); // finish when got empty line
			output.Seek(0, SeekOrigin.Begin); // start read from begining
			return new StreamReader(output, Encoding.UTF8); // return UTF8 reader
		}

		internal static async Task<Frame> GetFrame(ClientWebSocket ws, CancellationToken cancellationToken)
		{
			var utf8 = Encoding.UTF8;

			var inputstream = new MemoryStream();
			var bodyoutput = new MemoryStream();
			try
			{
				await GetMessage(inputstream, ws, cancellationToken);
			}
			catch (TaskCanceledException)
			{
				return HeartbeatFrame; // just return empty frame
			}

			if (inputstream.ReadByte() == 10)
			{
				return HeartbeatFrame;
			}
			else
			{
				inputstream.Seek(0, SeekOrigin.Begin);
			}

			StreamReader reader = findBody(inputstream);

			var cmd = reader.ReadLine();

			if (!CmdMap.ContainsKey(cmd))
			{
				throw new Exception($"Bad STOMP Frame, unknown command {cmd}");
			}

			Frame frame = new Frame
			{
				Type = CmdMap[cmd]
			};
			// parse headers
			var line = reader.ReadLine().TrimEnd();
			while (line != "")
			{
				var colon = line.IndexOf(":");
				if (colon < 1) // must exist and cannot by first character in the line
				{
					throw new Exception("Cannot parse header");
				}
				var key = line.Substring(0, colon);
				var value = line[(colon + 1)..];
				frame.Headers[key.ToLower()] = value;

				line = reader.ReadLine().TrimEnd(); // next header
			}
			int length = -1;
			if (frame.Headers.ContainsKey("content-length"))
			{
				if (!int.TryParse(frame.Headers["content-length"], out length))
				{
					throw new Exception("Error: not valid value of header content-length");
				}
				byte[] body = new byte[length];
				inputstream.Read(body, 0, body.Length);
				frame.Body = new ArraySegment<byte>(body);
			}
			else
			{
				var bodyStream = new MemoryStream();
				int b;
				while ((b = inputstream.ReadByte()) > 0) // not -1 and not 0
				{
					bodyStream.WriteByte((byte)b);
				}
				var bl = (int)bodyStream.Length;
				byte[] data = bodyStream.GetBuffer();
				frame.Body = new ArraySegment<byte>(data, 0, bl);
			}
			if (StompClient.Debug) Console.WriteLine("<<<\n{0}\n<<<\n", frame);
			return frame;
		}
	}
}
