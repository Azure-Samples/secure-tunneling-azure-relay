FROM ubuntu:20.04
ARG package_name='azbridge.0.8.1-rel.ubuntu.20.04-x64.deb'
COPY ./$package_name .
COPY ./scripts/run_localforwarder_azbridge.sh .
RUN apt-get update -y
RUN apt-get install -y ./$package_name net-tools
ENTRYPOINT ["/bin/bash", "./run_localforwarder_azbridge.sh"]