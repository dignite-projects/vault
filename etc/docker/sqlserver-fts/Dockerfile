# SQL Server 2025 with Full-Text Search, which the official image does not ship. Vault Index needs both: its vector
# column (2025) and its keyword leg (full-text). Used by the Aspire AppHost (src/Dignite.Vault.AppHost) for local
# development; production runs on a SQL Server 2025 or Azure SQL that has full-text search installed.
#
# mssql-server-fts must match the engine build exactly
# (https://learn.microsoft.com/sql/linux/install-upgrade/setup-full-text-search), and the image does not register
# the engine with dpkg, so the build cannot ask which build it is. The tag and SQL_VERSION therefore move together:
# the engine build of a tag is its com.microsoft.version label.
FROM mcr.microsoft.com/mssql/server:2025-CU11-ubuntu-24.04

ARG SQL_VERSION=17.0.5065.9

USER root

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-mssql.gpg \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-mssql.gpg] https://packages.microsoft.com/ubuntu/24.04/mssql-server-2025 noble main" \
      > /etc/apt/sources.list.d/mssql-server-2025.list \
 && apt-get update \
 && FTS_VERSION="$(apt-cache madison mssql-server-fts | awk -v v="${SQL_VERSION}" '$3 ~ "^"v"-" {print $3; exit}')" \
 && test -n "${FTS_VERSION}" \
 && apt-get install -y --no-install-recommends "mssql-server-fts=${FTS_VERSION}" \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

USER mssql
