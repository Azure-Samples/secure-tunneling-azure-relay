using AzureWebPubSubBridge;
using Microsoft.Extensions.Hosting;

namespace SecureTunneling.SimulatedDevice
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Relay.Bridge;
    using Microsoft.Azure.Relay.Bridge.Configuration;
    using Newtonsoft.Json;

    internal class Program
    {
        private static DeviceClient deviceClient;
        private static Host host;
        private static IHost webPubSubBridgeHost;
        private static bool connected;
        private static bool isWebPubSub;
        private static string deviceId;

        private static async Task Main()
        {
            var deviceConnectionString = ConfigurationManager.AppSettings["IOTHUB_DEVICE_CONNECTION_STRING"];
            var azureRelayConnectionString = ConfigurationManager.AppSettings["AZRELAY_CONN_STRING"];
            deviceId = IotHubConnectionStringBuilder.Create(deviceConnectionString).DeviceId;

            await InitializeDeviceClient(deviceConnectionString);
            InitializeAzureRelayHost(azureRelayConnectionString);

            Console.WriteLine("Press control-C to exit.");
            using CancellationTokenSource cts = new();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;

                if (connected)
                {
                    if (isWebPubSub)
                        webPubSubBridgeHost.StopAsync().GetAwaiter().GetResult();
                    else
                        host.Stop();
                    connected = false;
                }
                cts.Cancel();

                Console.WriteLine("Exiting...");
            };

            await Task.WhenAll(SendTelemetry(cts), StartWebServer());
        }

        private static async Task InitializeDeviceClient(string deviceConnectionString)
        {
            deviceClient = DeviceClient.CreateFromConnectionString(deviceConnectionString);
            deviceClient.SetRetryPolicy(new ExponentialBackoff(5, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

            await deviceClient.SetMethodHandlerAsync("CreateConnection", CreateConnection, null);
            await deviceClient.SetMethodHandlerAsync("CreateWebPubSubConnection", CreateWebPubSubConnection, null);
            await deviceClient.SetMethodHandlerAsync("DeleteConnection", DeleteConnection, null);
        }

        private static void InitializeAzureRelayHost(string azureRelayConnectionString)
        {
            Config config = new()
            {
                RemoteForward = new List<RemoteForward>
                {
                    new RemoteForward
                    {
                        RelayName = deviceId,
                        Host = "localhost",
                        HostPort = 8080,
                        PortName = "test",
                    }
                },
                AzureRelayConnectionString = azureRelayConnectionString
            };

            host = new(config);
        }

        private static void InitializeAzureWebPubSubBridgeHost()
        {
            var azureWebPubSubEndpoint = ConfigurationManager.AppSettings["AZWEBPUBSUB_ENDPOINT"];
            var azureWebPubSubKey = ConfigurationManager.AppSettings["AZWEBPUBSUB_KEY"];
            var azureWebPubSubHub = ConfigurationManager.AppSettings["AZWEBPUBSUB_HUB"];
            webPubSubBridgeHost = BridgeHost.CreateRemote(c =>
            {
                c.PubSubEndpoint = new Uri(azureWebPubSubEndpoint);
                c.PubSubKey = azureWebPubSubKey;
                c.Hub = azureWebPubSubHub;
                c.Port = 65532;
                c.Connect.ServerId = deviceId;
            });
        }

        private static Task<MethodResponse> CreateConnection(MethodRequest methodRequest, object userContext)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Received request to create connection. Device Id: {deviceId}");
            Console.ResetColor();
            string result;

            if (!connected)
            {
                try
                {
                    Console.WriteLine("Starting remote forwarder.");
                    host.Start();
                    connected = true;
                    isWebPubSub = false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    result = $"{{\"result\":\"Unable to start Remote Forwarder for Hybrid Connection: {deviceId}\"}}";
                    return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 500));
                }
            }

            result = $"{{\"result\":\"Executed direct method: {methodRequest.Name} for Hybrid Connection: {deviceId}\"}}";
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
        }

        private static async Task<MethodResponse> CreateWebPubSubConnection(MethodRequest methodRequest, object userContext)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Received request to create azure web pubsub connection. Device Id: {deviceId}");
            Console.ResetColor();
            string result;

            if (!connected)
            {
                try
                {
                    Console.WriteLine("Starting az web pubsub bridge remote forwarder.");
                    InitializeAzureWebPubSubBridgeHost();
                    await webPubSubBridgeHost.StartAsync();
                    connected = true;
                    isWebPubSub = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    result = $"{{\"result\":\"Unable to start Az Web PubSub Bridge Remote Forwarder for Hybrid Connection: {deviceId}\"}}";
                    return new MethodResponse(Encoding.UTF8.GetBytes(result), 500);
                }
            }

            result = $"{{\"result\":\"Executed direct method: {methodRequest.Name} for Hybrid Connection: {deviceId}\"}}";
            return new MethodResponse(Encoding.UTF8.GetBytes(result), 200);
        }

        private static async Task<MethodResponse> DeleteConnection(MethodRequest methodRequest, object userContext)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Received request to delete connection. Device Id: {deviceId}");
            Console.ResetColor();
            string result;

            if (connected)
            {
                try
                {
                    Console.WriteLine("Stopping remote forwarder.");
                    if (isWebPubSub)
                        await webPubSubBridgeHost.StopAsync();
                    else
                        host.Stop();
                    connected = false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    result = $"{{\"result\":\"Unable to stop Remote Forwarder for Hybrid Connection: {deviceId}\"}}";
                    return new MethodResponse(Encoding.UTF8.GetBytes(result), 500);
                }
            }

            result = $"{{\"result\":\"Executed direct method: {methodRequest.Name} for Hybrid Connection: {deviceId}\"}}";
            return new MethodResponse(Encoding.UTF8.GetBytes(result), 204);
        }

        private static async Task SendTelemetry(CancellationTokenSource cts)
        {
            await SendDeviceToCloudMessagesAsync(cts.Token);

            await deviceClient.CloseAsync();

            deviceClient.Dispose();
            Console.WriteLine("Telemetry simulator shutdown requested.");
        }

        private static async Task SendDeviceToCloudMessagesAsync(CancellationToken ct)
        {
            double minTemperature = 20;
            double minHumidity = 60;
            Random rand = new();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    double currentTemperature = minTemperature + (rand.NextDouble() * 15);
                    double currentHumidity = minHumidity + (rand.NextDouble() * 20);

                    string messageBody = JsonConvert.SerializeObject(
                        new
                        {
                            temperature = currentTemperature,
                            humidity = currentHumidity,
                        });
                    using var message = new Message(Encoding.ASCII.GetBytes(messageBody))
                    {
                        ContentType = "application/json",
                        ContentEncoding = "utf-8",
                    };

                    message.Properties.Add("temperatureAlert", (currentTemperature > 30) ? "true" : "false");

                    await deviceClient.SendEventAsync(message, ct);
                    Console.WriteLine($"{DateTime.Now} > Sending message: {messageBody}");

                    await Task.Delay(1000, ct);
                }
            }
            catch (TaskCanceledException)
            {
                // ct signaled
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex.GetType()}\nConfirm app setting 'IOTHUB_DEVICE_CONNECTION_STRING' is valid.");
                Environment.Exit(1);
            }
        }

        private static async Task StartWebServer()
        {
            var builder = WebApplication.CreateBuilder();

            var app = builder.Build();
            app.MapGet("/", () => "Hello from device!");
            await app.RunAsync();
        }
    }
}
