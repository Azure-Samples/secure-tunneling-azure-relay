﻿namespace SecureTunneling.SimulatedDevice
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Relay.Bridge;
    using Microsoft.Azure.Relay.Bridge.Configuration;
    using Newtonsoft.Json;

    internal class Program
    {
        private static readonly string Url = "http://127.0.0.1:8080/";
        private static readonly string PageData = File.ReadAllText("SamplePage.html");

        private static DeviceClient deviceClient;
        private static Host host;
        private static HttpListener listener;
        private static bool connected;
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
                    host.Stop();
                    connected = false;
                }
                cts.Cancel();

                Console.WriteLine("Exiting...");
            };

            await Task.WhenAll(SendTelemetry(cts), StartWebServer(cts));
        }

        private static async Task InitializeDeviceClient(string deviceConnectionString)
        {
            deviceClient = DeviceClient.CreateFromConnectionString(deviceConnectionString);
            deviceClient.SetRetryPolicy(new ExponentialBackoff(5, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100)));

            await deviceClient.SetMethodHandlerAsync("CreateConnection", CreateConnection, null);
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
                        Host = "127.0.0.1",
                        HostPort = 8080,
                        PortName = "test",
                    }
                },
                AzureRelayConnectionString = azureRelayConnectionString
            };

            host = new(config);
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

        private static Task<MethodResponse> DeleteConnection(MethodRequest methodRequest, object userContext)
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
                    host.Stop();
                    connected = false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    result = $"{{\"result\":\"Unable to stop Remote Forwarder for Hybrid Connection: {deviceId}\"}}";
                    return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 500));
                }
            }

            result = $"{{\"result\":\"Executed direct method: {methodRequest.Name} for Hybrid Connection: {deviceId}\"}}";
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 204));
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

        private static async Task StartWebServer(CancellationTokenSource cts)
        {
            listener = new HttpListener();
            listener.Prefixes.Add(Url);
            listener.Start();
            Console.WriteLine("Listening for connections on {0}", Url);

            await HandleIncomingConnections(cts.Token);

            listener.Close();
        }

        private static async Task HandleIncomingConnections(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    HttpListenerContext ctx = await listener.GetContextAsync()
                        .AsCancellable(ct);

                    HttpListenerRequest req = ctx.Request;
                    HttpListenerResponse resp = ctx.Response;

                    Console.WriteLine(req.Url.ToString());
                    Console.WriteLine(req.HttpMethod);
                    Console.WriteLine(req.UserHostName);
                    Console.WriteLine(req.UserAgent);
                    Console.WriteLine();

                    byte[] data = Encoding.UTF8.GetBytes(string.Format(PageData));
                    resp.ContentType = "text/html";
                    resp.ContentEncoding = Encoding.UTF8;
                    resp.ContentLength64 = data.LongLength;

                    await resp.OutputStream.WriteAsync(data, ct);
                    resp.Close();
                }
            }
            catch (TaskCanceledException)
            {
            }
        }
    }
}