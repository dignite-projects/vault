# SQL Server 2025 with Full-Text Search, for the Vault Index storage PoC only.
# The official image ships without mssql-server-fts; the package must match the engine build exactly
# (https://learn.microsoft.com/sql/linux/install-upgrade/setup-full-text-search), so the build pins it to the
# image's com.microsoft.version label.
FROM mcr.microsoft.com/mssql/server:2025-latest

USER root

ARG SQL_VERSION=17.0.5045.3

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-mssql.gpg \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-mssql.gpg] https://packages.microsoft.com/ubuntu/24.04/mssql-server-2025 noble main" \
      > /etc/apt/sources.list.d/mssql-server-2025.list \
 && apt-get update \
 && FTS_VERSION="$(apt-cache madison mssql-server-fts | awk -v v="${SQL_VERSION}" '$3 ~ "^"v {print $3; exit}')" \
 && test -n "${FTS_VERSION}" \
 && apt-get install -y --no-install-recommends "mssql-server-fts=${FTS_VERSION}" \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

USER mssql
