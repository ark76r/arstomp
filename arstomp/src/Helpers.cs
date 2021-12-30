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
			switch (type)
			{
				case FrameType.Unknown: return "UNKNOWN";
				case FrameType.Connected: return "CONNECTED";
				case FrameType.Message: return "MESSAGE";
				case FrameType.Receipt: return "RECEIPT";
				case FrameType.Error: return "ERROR";
				case FrameType.Stomp: return "STOMP";
				case FrameType.Send: return "SEND";
				case FrameType.Subscribe: return "SUBSCRIBE";
				case FrameType.Unsubscribe: return "UNSUBSCRIBE";
				case FrameType.Ack: return "ACK";
				case FrameType.Nack: return "NACK";
				case FrameType.Begin: return "BEGIN";
				case FrameType.Commit: return "COMMIT";
				case FrameType.Abort: return "ABORT";
				case FrameType.Disconnect: return "DISCONNECT";
				case FrameType.Heartbeat: return "";
				default: return "UNKNOWN";
			};
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

		internal static Frame GetFrame(byte[] msgBuffer, CancellationToken cancellationToken)
		{
			var inputstream = new MemoryStream(msgBuffer);

			var firstByte = inputstream.ReadByte();
			if (firstByte == 10)
			{
				return HeartbeatFrame;
			}
			else if (firstByte == 13) {
				var secondByte = inputstream.ReadByte();
				if (secondByte == 10) {
					return HeartbeatFrame;
				} else {
					throw new Exception("Invalid frame");
				}
			}
			// start from beginning
			inputstream.Seek(0, SeekOrigin.Begin);

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
					throw new Exception($"Cannot parse header: colon not found. Cmd: {cmd}, line: `${line}`");
				}
				var key = line.Substring(0, colon);
				var value = line.Substring(colon + 1);
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
	internal static class Backports
	{
		public static void WriteWholeArray(this MemoryStream stream, byte[] data)
		{
			stream.Write(data, 0, data.Length);
		}
	}
}
