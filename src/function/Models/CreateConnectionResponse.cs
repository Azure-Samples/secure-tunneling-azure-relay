// <copyright file="CreateConnectionResponse.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace SecureTunneling.Models
{
    /// <summary>
    /// The IoT device model.
    /// </summary>
    public class CreateConnectionResponse
    {
        /// <summary>
        /// Gets or sets the Id of the device.
        /// </summary>
        public string Result { get; set; }

        /// <summary>
        /// Gets or sets the device's service protocol.
        /// </summary>
        public string ServiceProtocol { get; set; }

        /// <summary>
        /// Gets or sets the device's service port.
        /// </summary>
        public string ServicePort { get; set; }
    }
}