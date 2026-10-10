# Minimal traffic-control sidecar.
#
# Built locally and pinned rather than pulling a general-purpose netshoot image:
# it is a few MB instead of several hundred, and the experiment runs it with
# --pull=never so a missing image fails loudly instead of downloading mid-fault.
#
# It is never attached to the application image. The experiment runs it as a
# throwaway container sharing the target's network namespace, so the app
# container never holds NET_ADMIN.
FROM alpine:3.21

RUN apk add --no-cache iproute2

ENTRYPOINT ["tc"]
