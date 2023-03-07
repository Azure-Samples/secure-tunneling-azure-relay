// <copyright file="IoTDevice.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace SecureTunneling.Models
{
    /// <summary>
    /// The IoT device model.
    /// </summary>
    public class IoTDevice
    {
        /// <summary>
        /// Gets or sets the Id of the device.
        /// </summary>
        public string DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the type of bridge for connecting the device (null, relay, webpubsub)
        /// </summary>
        public string BridgeType { get; set; }
    }
}