# Simple STOMP over WS client

Implemented in C# (.NET Core and Framework)

Works with RabbitMQ.

Uses (and probably requires) binary frames.

Supports 'custom' Root CA certficates.

Has simple RPC helper.


## Usage

```csharp
class Program
{
	static async Task Main(string[] args)
	{
		var utf8 = Encoding.UTF8;

		byte[] bytes = File.ReadAllBytes("path/to/ca.crt");
		X509Certificate2 myca = new X509Certificate2(bytes);
		X509Certificate2Collection mycerts = new X509Certificate2Collection { myca };

		StompClient client = new StompClient(mycerts);
		// can get messages in handler
		//client.OnMessage += OnMessage;
		// or use something to make request/response simpler
		using var rpc = new RPC(client);

		// connect
		var uri = new Uri("wss://stomp.server:15673/ws");
		await client.Connect(uri, "login", "pass");

		// subscriptions
		var sub1 = await client.Subscribe("/exchange/ex1/test.#");
		sub1.OnMessage += OnBroadcast;
		var sub2 = await client.Subscribe("/exchange/ex2/test.#");
		sub2.OnMessage += OnBroadcast;

		// simple publish (no response)
		await client.Send("/exchange/something/test", "1", utf8.GetBytes("Test 1"));

		// simple call (sending request and expecting response)
		var result = await rpc.Call("/exchange/rpc/test", utf8.GetBytes("Test 2"),
			TimeSpan.FromSeconds(3));
		Console.WriteLine("RCP: {0}", result);

		await client.Close();
	}
	static void OnBroadcast(object sender, SubscriptionEventArgs ea)
	{
		Console.WriteLine("Broadcast {0}", ea.Frame);
		var body = Encoding.UTF8.GetString(ea.Frame.Body);
		Console.WriteLine("Body: {0}", body);
	}
}
```
