namespace SecureTunneling.Functions
{
    /// <summary>
    /// The configuration used for the Function.
    /// </summary>
    public class SecureTunnelingConfiguration
    {
        /// <summary>
        /// The Azure Relay namespace connection string.
        /// </summary>
        public string AZRELAY_CONN_STRING { get; set; }
        
        /// <summary>
        /// The Azure subscription Id.
        /// </summary>
        public string AZURE_SUBSCRIPTION { get; set; }

        /// <summary>
        /// The name of the ACI group name to be created.
        /// </summary>
        public string CONTAINER_GROUP_NAME { get; set; }
        
        /// <summary>
        /// The local forwarder image name.
        /// </summary>        
        public string CONTAINER_IMAGE { get; set; }
        
        /// <summary>
        /// The name of the container registry.
        /// </summary>
        public string CONTAINER_REGISTRY { get; set; }
        
        /// <summary>
        /// The admin username for the container registry.
        /// </summary>
        public string CONTAINER_REGISTRY_USERNAME { get; set; }
        
        /// <summary>
        /// The admin password for the container registry.
        /// </summary>
        public string CONTAINER_REGISTRY_PASSWORD { get; set; }
        
        /// <summary>
        /// The port for the local forwarder container.
        /// </summary>
        public int CONTAINER_PORT { get; set; }
        
        /// <summary>
        /// The service police IoT connection string.
        /// </summary>
        public string IOT_SERVICE_CONN_STRING { get; set; }
        
        /// <summary>
        /// The resource group name.
        /// </summary>
        public string RESOURCE_GROUP_NAME { get; set; }

        /// <summary>
        /// The azure web pub sub endpoint.
        /// </summary>
        public string AZWEBPUBSUB_ENDPOINT { get; set; }

        /// <summary>
        /// The azure web pub sub key.
        /// </summary>
        public string AZWEBPUBSUB_KEY { get; set; }

        /// <summary>
        /// The azure web pub sub hub.
        /// </summary>
        public string AZWEBPUBSUB_HUB { get; set; }

        /// <summary>
        /// The azure web pub sub hub.
        /// </summary>
        public string AZWEBPUBSUB_BRIDGE_CONTAINER_IMAGE { get; set; }
    }
}