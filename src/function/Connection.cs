// <copyright file="Connection.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace SecureTunneling
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Azure.Core;
    using Azure.Identity;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.Devices;
    using Microsoft.Azure.Management.Fluent;
    using Microsoft.Azure.Management.ResourceManager.Fluent;
    using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
    using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.Rest;
    using Newtonsoft.Json;
    using SecureTunneling.Functions;
    using SecureTunneling.Models;

    /// <summary>
    /// Open connection with device via Azure Relay.
    /// </summary>
    public class Connection
    {
        private readonly SecureTunnelingConfiguration config;

        /// <summary>
        /// The Azure Function Connection class.
        /// </summary>
        public Connection(IOptions<SecureTunnelingConfiguration> config)
        {
            this.config = config.Value;
        }

        /// <summary>
        /// This function is triggered by an HTTP request to the /api/connection endpoint.
        /// </summary>
        /// <param name="request">The Http request.</param>
        /// <param name="logger">The logger.</param>
        /// <returns>A <see cref="Task{TResult}"/> representing the result of the asynchronous operation.</returns>
        [FunctionName("connection")]
        public async Task<IActionResult> CloudToDeviceConnection(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", "delete", Route = null)] HttpRequest request, ILogger logger)
        {
            string requestBody = await new StreamReader(request.Body).ReadToEndAsync();
            IoTDevice device = JsonConvert.DeserializeObject<IoTDevice>(requestBody);
            string deviceId = device.DeviceId;

            if (string.IsNullOrEmpty(deviceId))
            {
                return new BadRequestObjectResult("The deviceId is required.");
            }

            try
            {
                logger.LogInformation($"Creating a ServiceClient to communicate with the IoT hub device {deviceId}.");
                ServiceClient serviceClient = ServiceClient.CreateFromConnectionString(this.config.IOT_SERVICE_CONN_STRING);

                if (request.Method.Equals("DELETE", StringComparison.InvariantCultureIgnoreCase))
                {
                    await DeleteConnection(deviceId, serviceClient, logger);
                    return new NoContentResult();
                }

                (int statusCode, string message) = await CreateConnection(deviceId, serviceClient, logger);
                return new ObjectResult(message) { StatusCode = statusCode };
            }
            catch (Exception ex)
            {
                logger.LogError(ex.Message);
                return new ObjectResult(ex.Message) { StatusCode = 500 };
            }
        }

        private async Task DeleteConnection(
            string deviceId,
            ServiceClient serviceClient,
            ILogger logger)
        {
            logger.LogInformation($"Invoking direct method 'DeleteConnection' on device: {deviceId}");
            CloudToDeviceMethodResult result = await InvokeCloudToDeviceMethodAsync("DeleteConnection", deviceId, serviceClient);
            logger.LogInformation($"Response status: {result.Status}, payload:\n\t{result.GetPayloadAsJson()}");

            logger.LogInformation("Deleting ACI...");
            DeleteContainerGroup(logger);
        }
        
        private async Task<(int, string)> CreateConnection(
            string deviceId,
            ServiceClient serviceClient,
            ILogger logger)
        {
            logger.LogInformation($"Invoking direct method 'CreateConnection' on device: {deviceId}");
            CloudToDeviceMethodResult result = await InvokeCloudToDeviceMethodAsync("CreateConnection", deviceId, serviceClient);
            if (result.Status == 200)
            {
                string deviceProtocol = JsonConvert.DeserializeObject<CreateConnectionResponse>(result.GetPayloadAsJson()).ServiceProtocol;
                logger.LogInformation("Starting ACI...");
                string response = CreateContainerGroup(deviceId, deviceProtocol, logger);
                return (200, response);
            }
            else
            {
                logger.LogError($"Invoking direct method failed with status: {result.Status} and message: {result.GetPayloadAsJson()}");
                return (result.Status, result.GetPayloadAsJson());
            }
        }

        private static async Task<CloudToDeviceMethodResult> InvokeCloudToDeviceMethodAsync(
            string directMethod,
            string deviceId,
            ServiceClient serviceClient)
        {
            CloudToDeviceMethod methodInvocation = new (directMethod)
            {
                ResponseTimeout = TimeSpan.FromSeconds(30),
            };

            return await serviceClient.InvokeDeviceMethodAsync(deviceId, methodInvocation);
        }

        private string CreateContainerGroup(
            string deviceId, string serviceProtocol,
            ILogger logger)
        {
            DefaultAzureCredential defaultCreds = new (new DefaultAzureCredentialOptions());
            string defaultToken = defaultCreds.GetToken(new TokenRequestContext(new[] { "https://management.azure.com/.default" })).Token;
            TokenCredentials defaultTokenCredentials = new (defaultToken);

            AzureCredentials creds = new (defaultTokenCredentials, defaultTokenCredentials, null, AzureEnvironment.AzureGlobalCloud);
            IAzure azure = Azure.Authenticate(creds).WithSubscription(this.config.AZURE_SUBSCRIPTION);


            logger.LogInformation($"\nCreating container group '{this.config.CONTAINER_GROUP_NAME}'...");

            IResourceGroup resGroup = azure.ResourceGroups.GetByName(this.config.RESOURCE_GROUP_NAME);
            Region azureRegion = resGroup.Region;

            Dictionary<string, string> envVars = new ()
            {
                { "AZRELAY_CONN_STRING",  this.config.AZRELAY_CONN_STRING },
                { "AZRELAY_HYBRID_CONNECTION", deviceId },
                { "CONTAINER_PORT", $"{this.config.CONTAINER_PORT}" },
            };

            var containerGroup = azure.ContainerGroups.GetByResourceGroup(this.config.RESOURCE_GROUP_NAME, this.config.CONTAINER_GROUP_NAME);
            if (containerGroup == null)
            {
                containerGroup = azure.ContainerGroups.Define(this.config.CONTAINER_GROUP_NAME)
                    .WithRegion(azureRegion)
                    .WithExistingResourceGroup(this.config.RESOURCE_GROUP_NAME)
                    .WithLinux()
                    .WithPrivateImageRegistry(this.config.CONTAINER_REGISTRY, this.config.CONTAINER_REGISTRY_USERNAME, this.config.CONTAINER_REGISTRY_PASSWORD)
                    .WithoutVolume()
                    .DefineContainerInstance(this.config.CONTAINER_GROUP_NAME + "-1")
                        .WithImage(this.config.CONTAINER_IMAGE)
                        .WithExternalTcpPort(this.config.CONTAINER_PORT)
                        .WithCpuCoreCount(1.0)
                        .WithMemorySizeInGB(1)
                        .WithEnvironmentVariables(envVars)
                        .Attach()
                    .WithDnsPrefix(this.config.CONTAINER_GROUP_NAME)
                    .Create();
                logger.LogInformation($"Once DNS has propagated, container group '{containerGroup.Name}' will be reachable using {serviceProtocol} at '{containerGroup.Fqdn}:{this.config.CONTAINER_PORT}'");
            }
            else if (containerGroup.State == "Stopped")
            {
                logger.LogInformation($"Container group '{this.config.CONTAINER_GROUP_NAME}' already exists but is in Stopped state.");
                azure.ContainerGroups.Start(this.config.RESOURCE_GROUP_NAME, this.config.CONTAINER_GROUP_NAME);
                logger.LogInformation($"'{containerGroup.Name}' is starting and will be reachable using {serviceProtocol} at '{containerGroup.Fqdn}:{this.config.CONTAINER_PORT}'");
            }
            else
            {
                logger.LogInformation($"Container group '{containerGroup.Name}' already exists and is running.");
            }

            return $"Device access will be available using {serviceProtocol} at '{containerGroup.Fqdn}:{this.config.CONTAINER_PORT}'";
        }

        private void DeleteContainerGroup(ILogger logger)
        {
            DefaultAzureCredential defaultCreds = new (new DefaultAzureCredentialOptions());
            string defaultToken = defaultCreds.GetToken(new TokenRequestContext(new[] { "https://management.azure.com/.default" })).Token;
            TokenCredentials defaultTokenCredentials = new (defaultToken);

            AzureCredentials creds = new (defaultTokenCredentials, defaultTokenCredentials, null, AzureEnvironment.AzureGlobalCloud);
            IAzure azure = Azure.Authenticate(creds).WithSubscription(this.config.AZURE_SUBSCRIPTION);

            logger.LogInformation($"\nDeleting container group '{this.config.CONTAINER_GROUP_NAME}'...");
            azure.ContainerGroups.DeleteByResourceGroup(this.config.RESOURCE_GROUP_NAME, this.config.CONTAINER_GROUP_NAME);

            return;
        }
    }
}
