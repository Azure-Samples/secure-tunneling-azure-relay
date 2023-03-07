param(
    [switch]$SkipLogin,
    [switch]$SkipProvisionResources,
    [switch]$SkipBuildPushImages,
    [switch]$SkipFunctionDeployment,
    [switch]$AzWebPubSubBridge
)

$ErrorActionPreference = "Stop"
Install-Module powershell-yaml

$Settings = Get-Content -Path settings.yaml | ConvertFrom-Yaml
$Azure = $Settings.Azure
$AzureFunctionSettings = [System.Collections.ArrayList]@()
$FunctionAppSettingsFile = "azure.settings.json"
$DeviceAppConfigFile = "../src/simulated-device/app.config"

function AddAppSetting {
    param (
        [string]$SettingName,
        [string]$SettingValue
    )  
    if (Test-Path $FunctionAppSettingsFile) {
        Clear-Content $FunctionAppSettingsFile
    }
    $AppSettings = "{name: '$SettingName', value: '$SettingValue', slotSetting: false}"
    $AppSettingsObject = $AppSettings | ConvertFrom-Json
    $AzureFunctionSettings.Add($AppSettingsObject)
}

function CreateDeviceAppConfig {
    if (Test-Path $DeviceAppConfigFile) {
        Clear-Content $DeviceAppConfigFile
    }

    $AppConfigContent = @"
<?xml version="1.0" encoding="utf-8" ?>
<configuration>
    <appSettings>
        <add key="IOTHUB_DEVICE_CONNECTION_STRING" value="$DeviceConnectionString" />
        <add key="AZRELAY_CONN_STRING" value="$AzureRelayConnString" />
        <add key="AZWEBPUBSUB_KEY" value="$AzWebPubSubKey" />
        <add key="AZWEBPUBSUB_ENDPOINT" value="$AzWebPubSubEndpoint" />
        <add key="AZWEBPUBSUB_HUB" value="$AzureWebPubSubHubName" />
    </appSettings>
</configuration>
"@
    Add-Content $DeviceAppConfigFile $AppConfigContent
}

if (-not $SkipLogin) {
    Write-Host "Logging in to Azure..."
    az login --tenant $Azure.TenantId
    az account set --subscription $Azure.SubscriptionId
}

if (-not $SkipProvisionResources) {
    Write-Host "Registering ContainerInstance provider..."
    az provider register --namespace 'Microsoft.ContainerInstance'

    Write-Host "Creating Resource Group..."
    az group create --name $Azure.ResourceGroup.Name --location $Azure.ResourceGroup.Region

    Write-Host "Creating Container Registry..."
    az acr create --resource-group $Azure.ResourceGroup.Name --name $Azure.ContainerRegistry.Name --sku Basic --admin-enabled

    Write-Host "Creating IoT Hub..."
    az iot hub create --resource-group $Azure.ResourceGroup.Name --name $Azure.IoT.Name

    Write-Host "Creating IoT simulated device..."
    az iot hub device-identity create -d $Azure.IoT.DeviceId -n $Azure.IoT.Name
    
    Write-Host "Creating Azure Relay and a Hybrid Connection named as the IoT device..."
    az relay namespace create --resource-group $Azure.ResourceGroup.Name --name $Azure.Relay.Namespace
    az relay namespace authorization-rule create --resource-group $Azure.ResourceGroup.Name --namespace-name $Azure.Relay.Namespace --name SendListen --rights Send Listen
    az relay hyco create --resource-group $Azure.ResourceGroup.Name --namespace-name $Azure.Relay.Namespace --name $Azure.IoT.DeviceId

    Write-Host "Creating an app service plan for the function app..."
    az functionapp plan create --name $Azure.Function.Plan --resource-group $Azure.ResourceGroup.Name --is-linux true --sku S1

    Write-Host "Creating a storage account..."
    az storage account create --name $Azure.Function.Storage --resource-group $Azure.ResourceGroup.Name

    if ($AzWebPubSubBridge) {
        Write-Host "Creating Azure Web PubSub..."
        az webpubsub create --resource-group $Azure.ResourceGroup.Name --name $Azure.WebPubSub.Name --sku Standard_S1
        az webpubsub hub create --resource-group $Azure.ResourceGroup.Name --name $Azure.WebPubSub.Name --hub-name $Azure.WebPubSub.HubName
    }
}

Write-Host "Logging in to the Azure Container Registry..."
az acr login --name $Azure.ContainerRegistry.Name
$AcrServer = $(az acr show --name $Azure.ContainerRegistry.Name --resource-group $Azure.ResourceGroup.Name --query loginServer -o tsv)
$AcrUser = $(az acr credential show -n $Azure.ContainerRegistry.Name --query username -o tsv)
$AcrUserPassword = $(az acr credential show -n $Azure.ContainerRegistry.Name --query passwords[0].value -o tsv)
$FunctionImage = $AcrServer + "/" + $Azure.Function.Name + ":latest"
$LocalForwarderImage = $AcrServer + "/localforwarder-azbridge:latest"
$AzureRelayConnString = $(az relay namespace authorization-rule keys list --resource-group $Azure.ResourceGroup.Name --namespace-name $Azure.Relay.Namespace --name SendListen --query primaryConnectionString -o tsv)
$DeviceConnectionString = $(az iot hub device-identity connection-string show --device-id $Azure.IoT.DeviceId --hub-name $Azure.IoT.Name -o tsv)

if ($AzWebPubSubBridge) {
    $AzWebPubSubBridgeImage = $AcrServer + "/az-web-pubsub-bridge:latest"
    $AzWebPubSubKey = $(az webpubsub key show --resource-group $Azure.ResourceGroup.Name --name $Azure.WebPubSub.Name --query primaryKey -o tsv)
    $AzWebPubSubEndpoint = "https://" + $Azure.WebPubSub.Name + ".webpubsub.azure.com"
    $AzWebPubSubBridgePath = $Azure.WebPubSub.BridgeFolderPath + "/."
    $AzWebPubSubBridgeDockerfile = $Azure.WebPubSub.BridgeFolderPath + "/Dockerfile"
    $AzureWebPubSubHubName = $Azure.WebPubSub.HubName
}

Write-Host "Creating simulated-device app.config..."
CreateDeviceAppConfig

if (-not $SkipBuildPushImages) {
    Write-Host "Building the docker image for the local forwarder..."
    docker build ../src/local-forwarder --platform=linux/amd64 -f ../src/local-forwarder/Dockerfile --tag $LocalForwarderImage

    Write-Host "Pushing the localforwarder image to the Azure Container Registry..."
    docker push $LocalForwarderImage

    Write-Host "Building the docker image for the azure function..."
    docker build ../src/function/. -f ../src/function/Dockerfile --tag $FunctionImage

    Write-Host "Pushing the azure function image to the Azure Container Registry..."
    docker push $FunctionImage

    if ($AzWebPubSubBridge) {
        Write-Host "Building the docker image for the azure web pubsub bridge..."
        docker buildx build --platform linux/amd64 $AzWebPubSubBridgePath -f $AzWebPubSubBridgeDockerfile --tag $AzWebPubSubBridgeImage

        Write-Host "Pushing the azure web pubsub bridge image to Azure Container Registry..."
        docker push $AzWebPubSubBridgeImage
    }
}

if (-not $SkipFunctionDeployment) {
    Write-Host "Creating and deploying the azure function..."
    az functionapp create --name $Azure.Function.Name --resource-group $Azure.ResourceGroup.Name --storage-account $Azure.Function.Storage `
        --plan $Azure.Function.Plan`
        --deployment-container-image-name $FunctionImage `
        --docker-registry-server-password $AcrUserPassword `
        --docker-registry-server-user $AcrUser `
        --functions-version 4 `
        --os-type Linux `
        --disable-app-insights true `
        --runtime dotnet `
        --runtime-version 6

    Write-Host "Assigning contributor role to function identity for the resource group scope..."
    $Scope = '/subscriptions/' + $Azure.SubscriptionId + '/resourceGroups/' + $Azure.ResourceGroup.Name
    az functionapp identity assign --resource-group $Azure.ResourceGroup.Name `
        --name $Azure.Function.Name `
        --role contributor `
        --scope $Scope

    Write-Host "Generating the azure function app settings file..."
    $IoTHubServiceConnectionString = $(az iot hub connection-string show --hub-name $Azure.IoT.Name --policy-name service -o tsv)
    AddAppSetting -SettingName "AZRELAY_CONN_STRING" -SettingValue $AzureRelayConnString
    AddAppSetting -SettingName "AZURE_SUBSCRIPTION" -SettingValue $Azure.SubscriptionId 
    AddAppSetting -SettingName "CONTAINER_GROUP_NAME" -SettingValue $Azure.Container.InstanceGroupName
    AddAppSetting -SettingName "CONTAINER_IMAGE" -SettingValue $LocalForwarderImage
    AddAppSetting -SettingName "CONTAINER_PORT" -SettingValue $Azure.Container.Port
    AddAppSetting -SettingName "CONTAINER_REGISTRY" -SettingValue $AcrServer
    AddAppSetting -SettingName "CONTAINER_REGISTRY_USERNAME" -SettingValue $AcrUser
    AddAppSetting -SettingName "CONTAINER_REGISTRY_PASSWORD" -SettingValue $AcrUserPassword
    AddAppSetting -SettingName "IOT_SERVICE_CONN_STRING" -SettingValue $IoTHubServiceConnectionString
    AddAppSetting -SettingName "RESOURCE_GROUP_NAME" -SettingValue $Azure.ResourceGroup.Name

    if ($AzWebPubSubBridge) {
        AddAppSetting -SettingName "AZWEBPUBSUB_KEY" -SettingValue $AzWebPubSubKey
        AddAppSetting -SettingName "AZWEBPUBSUB_ENDPOINT" -SettingValue $AzWebPubSubEndpoint
        AddAppSetting -SettingName "AZWEBPUBSUB_HUB" -SettingValue $Azure.WebPubSub.HubName
        AddAppSetting -SettingName "AZWEBPUBSUB_BRIDGE_CONTAINER_IMAGE" -SettingValue $AzWebPubSubBridgeImage
    }
    $AzureFunctionSettings | ConvertTo-Json -AsArray | Out-File $FunctionAppSettingsFile

    Write-Host "Configuring the azure function app settings..."
    az functionapp config appsettings set --name $Azure.Function.Name --resource-group $Azure.ResourceGroup.Name --settings '@azure.settings.json'
}