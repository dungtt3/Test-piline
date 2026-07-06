# BASE_IMAGE cho phep dung flavor nhe hon (vd oxsecurity/megalinter-javascript:v8) khi thieu dung luong dia.
ARG BASE_IMAGE=oxsecurity/megalinter:v8
FROM ${BASE_IMAGE}

COPY entrypoint.sh /eaap-entrypoint.sh
RUN chmod +x /eaap-entrypoint.sh

ENTRYPOINT ["/eaap-entrypoint.sh"]
