#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$root"
python3 scripts/test_lnd_connection.py
python3 scripts/prepare.py
python3 -m unittest discover -s gateway -p test_gateway.py
sdk=mcr.microsoft.com/dotnet/sdk:10.0.400-noble
docker run --rm -v "$root/.build/nbxplorer:/source" \
  -v "$root/.build/btcpay/XBTPay/nuget:/packages" -w /source "$sdk" sh -c \
  'dotnet test NBXplorer.Tests/NBXplorer.Tests.csproj --filter FullyQualifiedName~XbtTests && dotnet pack NBXplorer.Client/NBXplorer.Client.csproj -c Release -o /packages'
docker run --rm -v "$root/.build/btcpay:/source" -w /source "$sdk" \
  dotnet test BTCPayServer.Tests/BTCPayServer.Tests.csproj --filter FullyQualifiedName~XbtTests
if [ "${1:-}" = '--test-only' ]; then exit 0; fi
docker build -t xbtpay-nbxplorer:beta .build/nbxplorer
docker build -t xbtpay-server:beta .build/btcpay
docker build -t xbtpay-cln-gateway:beta gateway
