param(
    [switch]$SkipLogin
)

$ErrorActionPreference = "Stop"
Install-Module powershell-yaml

$Settings = Get-Content -Path settings.yaml | ConvertFrom-Yaml
$Azure = $Settings.Azure

if (-not $SkipLogin)
{
    Write-Host "Logging in to Azure..."
    az login --tenant $Azure.TenantId
    az account set --subscription $Azure.SubscriptionId
}

Write-Host "Removing local images if they exist..."
$AcrServer = $Azure.ContainerRegistry.Name+".azurecr.io"
$FunctionImage = $AcrServer + "/" + $Azure.Function.Name
$LocalForwarderImage = $AcrServer + "/localforwarder-azbridge"

docker rmi $FunctionImage 
docker rmi $LocalForwarderImage

Write-Host "Deleting Resource Group..."
az group delete --name $Azure.ResourceGroup.Name --yes