param(
    [switch]$SkipLogin,
    [switch]$SkipProvisionResources,
    [switch]$SkipBuildPushImages,
    [switch]$SkipFunctionDeployment
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
        <add key="SERVICE_PROTOCOL" value="$ServiceProtocol" />
        <add key="SERVICE_PORT" value="$ServicePort" />
    </appSettings>
</configuration>
"@
    Add-Content $DeviceAppConfigFile $AppConfigContent
}

if (-not $SkipLogin) {
    Write-Host "Logging into Azure..."
    az login --tenant $Azure.TenantId
    az account set --subscription $Azure.SubscriptionId
}

if (-not $SkipProvisionResources) {
    Write-Host "Creating Resource Group..."
    az group create --name $Azure.ResourceGroup.Name --location $Azure.ResourceGroup.Region

    Write-Host "Creating Container Registry..."
    az acr create --resource-group $Azure.ResourceGroup.Name --name $Azure.ContainerRegistry.Name --sku Standard --admin-enabled

    Write-Host "Creating IoT Hub..."
    az iot hub create --resource-group $Azure.ResourceGroup.Name --name $Azure.IoT.Name

    Write-Host "Creating IoT simulated device..."
    az iot hub device-identity create -d $Azure.IoT.DeviceId -n $Azure.IoT.Name
    
    Write-Host "Creating Azure Relay and a Hybrid Connection named as the IoT device..."
    az relay namespace create --resource-group $Azure.ResourceGroup.Name --name $Azure.Relay.Namespace
    az relay namespace authorization-rule create --resource-group $Azure.ResourceGroup.Name --namespace-name $Azure.Relay.Namespace --name SendListen --rights Send Listen
    az relay hyco create --resource-group $Azure.ResourceGroup.Name --namespace-name $Azure.Relay.Namespace --name $Azure.IoT.DeviceId

    Write-Host "Creating an Azure App Service plan for the Function App..."
    az functionapp plan create --name $Azure.Function.Plan --resource-group $Azure.ResourceGroup.Name --is-linux true --sku S1

    Write-Host "Creating a storage account..."
    az storage account create --name $Azure.Function.Storage --resource-group $Azure.ResourceGroup.Name --access-tier Hot --https-only true --kind StorageV2 --public-network-access Enabled
}

Write-Host "Logging in to the Azure Container Registry..."
az acr login --name $Azure.ContainerRegistry.Name
$AcrServer = $(az acr show --name $Azure.ContainerRegistry.Name --resource-group $Azure.ResourceGroup.Name --query loginServer -o tsv)
$AcrUser = $(az acr credential show -n $Azure.ContainerRegistry.Name --query username -o tsv)
$AcrUserPassword = $(az acr credential show -n $Azure.ContainerRegistry.Name --query passwords[0].value -o tsv)
$LocalForwarderImage = "localforwarder-azbridge:latest"
$AcrLocalForwarderImage = $AcrServer + "/" + $LocalForwarderImage
$FunctionImage = $Azure.Function.Name + ":latest"
$AcrFunctionImage = $AcrServer + "/" + $FunctionImage
$AzureRelayConnString = $(az relay namespace authorization-rule keys list --resource-group $Azure.ResourceGroup.Name --namespace-name $Azure.Relay.Namespace --name SendListen --query primaryConnectionString -o tsv)
$DeviceConnectionString = $(az iot hub device-identity connection-string show --device-id $Azure.IoT.DeviceId --hub-name $Azure.IoT.Name -o tsv)
$ServiceProtocol = $Azure.IoT.serviceProtocol
$ServicePort = $Azure.IoT.servicePort

Write-Host "Creating simulated-device app.config..."
CreateDeviceAppConfig

if (-not $SkipBuildPushImages) {
    Write-Host "Building and pushing the container image for the local forwarder..."
    az acr build --registry $Azure.ContainerRegistry.Name --image $LocalForwarderImage --platform linux/amd64 ../src/local-forwarder/

    Write-Host "Building and pushing the container image for the Azure Function..."
    az acr build --registry $Azure.ContainerRegistry.Name --image $FunctionImage --platform linux/amd64 ../src/function/
}

if (-not $SkipFunctionDeployment) {
    Write-Host "Creating and deploying the Azure Function..."
    az functionapp create --name $Azure.Function.Name --resource-group $Azure.ResourceGroup.Name --storage-account $Azure.Function.Storage `
        --plan $Azure.Function.Plan `
        --deployment-container-image-name $AcrFunctionImage `
        --docker-registry-server-password $AcrUserPassword `
        --docker-registry-server-user $AcrUser `
        --functions-version 4 `
        --os-type Linux `
        --disable-app-insights true `
        --runtime dotnet `
        --runtime-version 6

    Write-Host "Assigning contributor role to function's identity for the resource group scope..."
    $Scope = '/subscriptions/' + $Azure.SubscriptionId + '/resourceGroups/' + $Azure.ResourceGroup.Name
    az functionapp identity assign --resource-group $Azure.ResourceGroup.Name `
        --name $Azure.Function.Name `
        --role contributor `
        --scope $Scope

    Write-Host "Generating the Azure Function app settings file..."
    $IoTHubServiceConnectionString = $(az iot hub connection-string show --hub-name $Azure.IoT.Name --policy-name service -o tsv)
    AddAppSetting -SettingName "AZRELAY_CONN_STRING" -SettingValue $AzureRelayConnString
    AddAppSetting -SettingName "AZURE_SUBSCRIPTION" -SettingValue $Azure.SubscriptionId 
    AddAppSetting -SettingName "CONTAINER_GROUP_NAME" -SettingValue $Azure.Container.InstanceGroupName
    AddAppSetting -SettingName "CONTAINER_IMAGE" -SettingValue $AcrLocalForwarderImage
    AddAppSetting -SettingName "CONTAINER_PORT" -SettingValue $Azure.Container.Port
    AddAppSetting -SettingName "CONTAINER_REGISTRY" -SettingValue $AcrServer
    AddAppSetting -SettingName "CONTAINER_REGISTRY_USERNAME" -SettingValue $AcrUser
    AddAppSetting -SettingName "CONTAINER_REGISTRY_PASSWORD" -SettingValue $AcrUserPassword
    AddAppSetting -SettingName "IOT_SERVICE_CONN_STRING" -SettingValue $IoTHubServiceConnectionString
    AddAppSetting -SettingName "RESOURCE_GROUP_NAME" -SettingValue $Azure.ResourceGroup.Name
    $AzureFunctionSettings | ConvertTo-Json -AsArray | Out-File $FunctionAppSettingsFile

    Write-Host "Configuring the azure function app settings..."
    az functionapp config appsettings set --name $Azure.Function.Name --resource-group $Azure.ResourceGroup.Name --settings '@azure.settings.json'
}
