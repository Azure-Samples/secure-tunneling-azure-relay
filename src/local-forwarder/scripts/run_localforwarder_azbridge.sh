#!/bin/bash
export IP_ADDRESS=$(ifconfig eth0 | grep -oP '(?<=inet\s)\d+(\.\d+){3}')
echo "IP Address: $IP_ADDRESS"
sleep 90
/usr/share/azbridge/azbridge -x $AZRELAY_CONN_STRING -L $IP_ADDRESS:$CONTAINER_PORT/test:$AZRELAY_HYBRID_CONNECTION