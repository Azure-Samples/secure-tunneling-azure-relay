---
page_type: sample
languages:
- csharp
products:
- azure
- azure-relay
- azure-iot-hub
name: Secure Tunneling using Azure Relay
description: Secure Tunneling using Azure Relay
urlFragment: secure-tunneling-azure-relay
---

# Secure Tunneling with Azure Relay

When devices are installed at remote locations and protected by firewalls, the users who need to access them for troubleshooting or other operational tasks often need to be present on-site or connected to the same local network as the device. Secure tunneling enables users to establish secure, bi-directional connections to edge devices, without making significant changes to the firewall or network configuration on the edge.

<!--

TODO: To learn more about this scenario, please read [Secure Tunneling with Azure Relay](TODO) in the Azure Architecture Center.

-->
This code sample implements a secure tunneling solution that uses the [Azure Relay service](https://learn.microsoft.com/azure/azure-relay/relay-what-is-it), demonstrating how to open a connection that uses a secure tunnel between a cloud endpoint and a device at a remote site. The device acts as a listener that creates a [hybrid connection](https://learn.microsoft.com/azure/azure-relay/relay-hybrid-connections-protocol) with Azure Relay and waits for connection requests. An application running in the cloud can connect to the device by targeting the same hybrid connection. The cloud application then exposes a public endpoint that is accessible to users and marshals all network communication between the user and the device through the hybrid connection. This enables communication between the user and device using any protocol that leverages TCP.

This sample defaults to using HTTP with a web server listening on port 8080. Simply change the protocol and/or port to use any other TCP based protocol and/or port.

The flow below demonstrates how a user can access a service that is running on a remote device in a private network.

![A diagram that shows a user communicating to an IOT Hub orchestrated device through Azure Relay.](docs/assets/secure-tunneling.png)

1. The user triggers an orchestrator function to initiate a connection with a device specified in the request payload.
2. The orchestrator invokes a direct method to the device via IoT Hub.
    - Direct methods are synchronous, follow a request-response pattern and are meant for communications that require immediate confirmation of their result (within a user-specified timeout). 
    - The [Azure IoT service SDK](https://www.nuget.org/packages/Microsoft.Azure.Devices) is used as it contains code to interact directly with IoT Hub to manage devices.
3. The target device sends telemetry to the IoT Hub, and the device runs a web server on port 8080. Upon initiation of a connection, it runs a remote forwarder for the port the service is running on via [Azure Relay Bridge](https://github.com/Azure/azure-relay-bridge#readme) and starts listening to an Azure Relay Hybrid Connection of the same name.
    - The [Azure IoT Hub device SDK](https://www.nuget.org/packages/Microsoft.Azure.Devices.Client) is used to receive and respond to the direct method without having to worry about the underlying protocol details. 
4. Upon successful response, the orchestrator provisions and/or starts an Azure Container Instance (ACI) that runs a local forwarder via [Azure Relay Bridge](https://github.com/Azure/azure-relay-bridge#readme) configured to connect to the same Azure Relay Hybrid Connection.
5. The ACI instance connects to the Azure Relay Hybrid Connection and exposes a public endpoint.
6. When the connection is established, the user can access the service that is running on the remote device by using to the ACI's fully qualified domain name (FQDN) and port with an appropriate client tool, e.g., a web browser for HTTP.

## Deployment guide

### Prerequisites

- [Azure Subscription](https://azure.microsoft.com/) with
    - permissions to create resources and perform role assignments
    - the following [Resource providers](https://learn.microsoft.com/azure/azure-resource-manager/management/resource-providers-and-types#register-resource-provider) registered
        - Microsoft.Resources
        - Microsoft.ContainerRegistry
        - Microsoft.Devices
        - Microsoft.Relay
        - Microsoft.Web
        - Microsoft.Storage
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) 2.4.2 or newer
- [Azure CLI IoT extension](https://github.com/Azure/azure-iot-cli-extension#installation)
- [Powershell](https://learn.microsoft.com/powershell/scripting/install/installing-powershell)
- [.NET 6.0](https://dotnet.microsoft.com/download/dotnet/6.0)

### Provision Azure components

1. Clone the repo.

    ```bash
    git clone https://github.com/Azure-Samples/secure-tunneling-azure-relay.git
    cd secure-tunneling-azure-relay
    ```

1. Create and update the **settings.yaml** file.

    ```bash
    cd deploy
    cp sample.settings.yaml settings.yaml
    ```

    Set the `tenantId` and `subscriptionId`, and adjust the values for the Azure resource names by replacing `[unique-id]` with a short, unique identifier so that your resource names are unique across Azure.

    To use a different protocol than HTTP, change the value of `serviceProtocol` to an appropriate value, e.g., `ssh` and change the `servicePort` to the port on which the service is running, e.g., `22`. Note: The value of the protocol is arbitrary, used only for messaging purposes and does not have to match the actual protocol running on the device.

1. Execute the **provision_resources.ps1** script.

    This script contains all of the deployment actions necessary to deploy the solution to Azure. Review the script before executing to familiarize yourself with the flow. It will use the **settings.yaml** file for input and will perform the following:

    1. Request you sign into Azure
    1. Provision the Azure resources:
 
        - Resource group
        - Azure Relay
        - IoT Hub
        - IoT Device
        - Azure Container Registry (ACR)
        - App Service Plan
        - Azure Storage Account

    1. Build images for the Azure Function and the local forwarder that will run on an ACI instance
    1. Push images to ACR
    1. Generate the **app.config** file in the simulated-device project
    1. Generate the Azure Function app settings file, **azure.settings.json**
    1. Deploy Azure Function
    1. Configure the Azure Function using the generated **azure.settings.json** file

    ```bash
    pwsh provision_resources.ps1
    ```

## Run the sample

### Start the simulated device

To start the simulated device follow these steps:

1. Review the **src/simulated-device/app.config** to confirm that the values for the following appSettings were set during the provisioning step.
    - `IOTHUB_DEVICE_CONNECTION_STRING`
    - `AZRELAY_CONN_STRING`
    - `SERVICE_PROTOCOL`
    - `SERVICE_PORT`

1. Start the simulated device to:

    - Send telemetry to the IoT Hub
    - Run a web server (for HTTP)
    - Handle the direct method call to run the Azure Relay remote forwarder
    - Handle the direct method call to stop the Azure Relay remote forwarder

    ```bash
    cd ../src/simulated-device
    dotnet run
    ```

    ![A screenshot of dotnet run console output showing simulated device telemetry being generated and sent.](docs/assets/simulated-device-telemetry.png)

1. Open the local web server at `http://localhost:8080` in your browser.

    ![A screenshot of a browser pointed at 127.0.0.1:8080 with text saying "Hello from device!"](docs/assets/web-server.png)

    If you are using a different protocol, use the appropriate client tool to confirm access to the service running on the device. For example, using SSH:

    ```bash
    ssh <username>@localhost
    ``` 

### Call the Azure Function to create a connection to the device

The deployed Azure Function has two HTTP methods.

`POST` will:

- Call a direct method to the simulated device to create a connection
- If successful, provision/start an ACI that runs the Azure Relay bridge for the local forwarder

`DELETE` will:

- Call a direct method to the simulated device to delete the connection
- De-provision the ACI that runs the Azure Relay bridge for the local forwarder


1. Call the deployed function to create a connection.

    ```bash
    FUNCTION_NAME=<your-function-name>
    DEVICE_ID=<your-device-id>
    
    curl -X POST \
       -H "Content-Type: application/json" \
       -d "{ \"deviceId\": \"${DEVICE_ID}\" }" \
       https://${FUNCTION_NAME}.azurewebsites.net/api/connection
    ```

    Which returns the endpoint information.

    ```output
    Device access will be available via: http://your-endpoint.eastus.azurecontainer.io:8090
    ```

    The call will also be captured in the simulated device's logs.

    ![A screenshot of console output from the simulated device showing the direct method call was made.](docs/assets/simulated-device-direct-method.png)
    
1. Access the device.

    Once DNS is propagated and ACI starts up (it might take a few minutes), you will be able to access the service that is running on the device using the ACI instance's FQDN and port number that are returned in the response.

    For example, using HTTP:

    ![A screenshot of a web browser at http://aci-st-sample667z9.eastus.azurecontainer.io:8090 showing the simulated device's built-in web server response saying "Hello from device!"](docs/assets/web-server-aci.png)

    For example, using SSH:

    ```bash
    ssh -p 8090 <username>@aci-st-sample667z9.eastus.azurecontainer.io
    ```

1. Delete the connection.

    Call the function to delete the connection.

    ```bash
    curl -X DELETE \
       -H "Content-Type: application/json" \
       -d "{ \"deviceId\": \"${DEVICE_ID}\" }" \
       https://${FUNCTION_NAME}.azurewebsites.net/api/connection
    ```

## Clean up resources

To clean up all the resources in Azure and delete the local images that were created during provisioning execute the **cleanup_resources.ps1** script.

```bash
cd ../../deploy
pwsh cleanup_resources.ps1
```

## References

- [Sample code for Simulated Device (with direct method handler)](https://github.com/Azure/azure-iot-sdk-csharp/tree/main/iothub/device/samples/getting%20started/SimulatedDeviceWithCommand)
- [Sample code to invoke device direct method](https://github.com/Azure/azure-iot-sdk-csharp/blob/main/iothub/service/samples/getting%20started/InvokeDeviceMethod/Program.cs)
- [Control a device connected to an IoT hub](https://learn.microsoft.com/azure/iot-hub/quickstart-control-device?pivots=programming-language-csharp)
- [Azure Container Instances libraries for .NET](https://learn.microsoft.com/dotnet/api/overview/azure/containerinstance?view=azure-dotnet)
